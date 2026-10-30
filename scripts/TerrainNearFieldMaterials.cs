using System;
using System.Linq;
using System.Collections.Generic;
using System.Buffers.Binary;
using Godot;

namespace EsoTest;

/// <summary>
/// Original near-field terrain materials. Every BEEF000C land tile has a 64-byte
/// palette of 16 local material IDs beginning at byte 102. These resolve through
/// FAFAEBEB global category 41 to the original diffuse and packed-normal DDS IDs.
/// No substitute textures or pre-extracted ESO assets are used.
/// </summary>
public static class TerrainNearFieldMaterials
{
    private sealed record Definition(uint Id,uint DiffuseId,uint NormalId,
        float TextureSize,float Glossiness);

    public sealed record Result(ShaderMaterial Material,int SourceTiles,
        int UniqueMaterials,int DiffuseDds,int NormalDds);

    private const int TilePixels=128;
    private const int PaletteSlots=16;

    private static uint U32(ReadOnlySpan<byte> b,int offset)=>
        BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(offset,4));
    private static uint Be32(ReadOnlySpan<byte> b,int offset)=>
        BinaryPrimitives.ReadUInt32BigEndian(b.Slice(offset,4));

    private static Dictionary<uint,Definition> ReadDefinitions(
        EsoArchive archive,OodleDecoder decoder)
    {
        var record=archive.FindId(41).Where(r=>{
            var tag=archive.GetIdIndexFields(r.Ordinal);
            return tag.Flags==0x6000&&tag.Field2==0;
        }).OrderByDescending(r=>r.Ordinal).FirstOrDefault();
        if(record.UncompressedSize==0)
            throw new InvalidOperationException("Original terrain material metadata category 41 is absent");

        byte[] data=archive.ReadDecoded(record,decoder);
        var catalog=DepotMetadataContainer.Parse(data,data.Length);
        var definitions=new Dictionary<uint,Definition>();
        for(int i=0;i<catalog.Entries.Count;i++)
        {
            byte[] entry=catalog.ReadEntry(i);
            if(entry.Length!=54)continue;
            uint id=Be32(entry,0),diffuse=Be32(entry,33),normal=Be32(entry,37);
            float textureSize=BitConverter.ToSingle(entry,25);
            float gloss=BitConverter.ToSingle(entry,41);
            if(id==0||diffuse==0||normal==0||
                !float.IsFinite(textureSize)||textureSize<=0)
                continue;
            definitions[id]=new Definition(id,diffuse,normal,textureSize,gloss);
        }
        if(definitions.Count==0)
            throw new InvalidOperationException("Original terrain material definitions could not be decoded");
        return definitions;
    }

    private static Image ReadDdsImage(EsoArchive archive,OodleDecoder decoder,uint fileId)
    {
        foreach(var record in archive.FindId(fileId))
        {
            var tag=archive.GetIdIndexFields(record.Ordinal);
            if(tag.Flags!=0||record.UncompressedSize is <160 or >5_000_000)continue;
            byte[] data=archive.ReadDecoded(record,decoder);
            if(U32(data,0)!=0x20534444)
                continue;
            // ESO's BC1/BC2/BC3 DDS headers sometimes provide a top-level
            // size while omitting DDSD_LINEARSIZE, or use an obsolete size
            // (2048 for 512x512 BC3). Normalize only the in-memory header.
            // Never modify any authored compressed pixels or mipmap payloads.
            uint width=U32(data,16),height=U32(data,12);
            uint fourCC=U32(data,84);
            uint blockBytes=fourCC==0x31545844?8u:
                fourCC is 0x33545844 or 0x35545844?16u:0u;
            if(blockBytes!=0 && width is >0 and <=4096 &&
                height is >0 and <=4096)
            {
                uint flags=U32(data,8);
                flags=(flags|0x80000u)&~0x8u; // DDSD_LINEARSIZE, not DDSD_PITCH
                uint expected=((width+3u)/4u)*((height+3u)/4u)*blockBytes;
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8,4),flags);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20,4),expected);
            }
            var img=new Image();
            Error status=img.LoadDdsFromBuffer(data);
            if(status!=Error.Ok||img.IsEmpty())
            {
                img.Dispose();
                throw new InvalidOperationException(
                    $"Cannot load original DDS 0x{fileId:X8}: {status}");
            }
            status=img.Decompress();
            if(status!=Error.Ok)
            {
                img.Dispose();
                throw new InvalidOperationException(
                    $"Cannot decompress original DDS 0x{fileId:X8}: {status}");
            }
            img.Convert(Image.Format.Rgba8);
            // Rare ESO material placeholders use original 4x4 or 256x256 DDS.
            // Resample only their original pixels to keep texture-array dimensions.
            if(img.GetWidth()!=512||img.GetHeight()!=512)
                img.Resize(512,512,Image.Interpolation.Nearest);
            img.GenerateMipmaps();
            return img;
        }
        throw new InvalidOperationException($"Original terrain DDS 0x{fileId:X8} missing");
    }

    private static Texture2DArray MakeArray(Godot.Collections.Array<Image> images,string label)
    {
        var array=new Texture2DArray();
        Error status=array.CreateFromImages(images);
        if(status!=Error.Ok)
        {
            array.Dispose();
            throw new InvalidOperationException(
                $"Cannot build original terrain {label} array: {status}");
        }
        return array;
    }

    public static Result LoadPatch(EsoArchive archive,OodleDecoder decoder,
        TerrainBundle section,int centerX,int centerZ,
        TerrainNearFieldColors.Result? originalColour=null,
        bool normalEnabled=true,bool applyTint=true)
        => LoadRegion(archive,decoder,section,centerX-2,centerZ-2,5,
            originalColour,normalEnabled,applyTint);

    public static Result LoadRegion(EsoArchive archive,OodleDecoder decoder,
        TerrainBundle section,int firstX,int firstZ,int tileSide,
        TerrainNearFieldColors.Result? originalColour=null,
        bool normalEnabled=true,bool applyTint=true)
    {
        if(tileSide is <1 or >8)throw new ArgumentOutOfRangeException(nameof(tileSide));
        var records=section.Tiles.ToDictionary(r=>
            ((int)(r.FileId>>16),(int)(r.FileId&0xffff)));
        var definitions=ReadDefinitions(archive,decoder);
        var tilePalettes=new uint[tileSide*tileSide,PaletteSlots];
        var weights=new Godot.Collections.Array<Image>();
        var diffuseImgs=new Godot.Collections.Array<Image>();
        var normalImgs=new Godot.Collections.Array<Image>();

        try
        {
            var used=new SortedSet<uint>();
            for(int tz=0;tz<tileSide;tz++)
            for(int tx=0;tx<tileSide;tx++)
            {
                int tileIndex=tz*tileSide+tx;
                if(!records.TryGetValue((firstX+tx,firstZ+tz),out var record))
                    throw new InvalidOperationException(
                        $"Missing original close-up tile {firstX+tx},{firstZ+tz}");
                byte[] bytes=archive.ReadDecoded(record,decoder);
                if(bytes.Length<182||U32(bytes,0)!=0xBEEF000C||
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(100))!=64)
                    throw new InvalidOperationException("Unexpected source land palette");
                for(int slot=0;slot<PaletteSlots;slot++)
                {
                    uint id=U32(bytes,102+4*slot);
                    tilePalettes[tileIndex,slot]=id;
                    if(id!=0)used.Add(id);
                }
                for(int group=0;group<4;group++)
                {
                    int sub=group+2;
                    int header=10+sub*9;
                    int off=(int)U32(bytes,header+1);
                    int length=(int)U32(bytes,header+5);
                    if(bytes[header]!=sub||off<=0||length<65812||
                        (long)off+length>bytes.Length)
                        throw new InvalidOperationException(
                            $"Missing original terrain paint group {group}");
                    var src=bytes.AsSpan(off,length);
                    if(U32(src,0)!=0xBEEF0001||U32(src,4)!=128||
                        U32(src,8)!=128||U32(src,12)!=512)
                        throw new InvalidOperationException("Invalid original blend format");
                    byte[] rgba=new byte[TilePixels*TilePixels*4];
                    int cursor=16;
                    for(int row=0;row<TilePixels;row++)
                    {
                        if(BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(cursor))!=512)
                            throw new InvalidOperationException("Invalid blend row size");
                        cursor+=2;
                        src.Slice(cursor,512).CopyTo(rgba.AsSpan(row*512,512));
                        cursor+=512;
                    }
                    // The raw channel order matches header palette index.
                    // It is not the previous inspector's shader-BGRA slot numbering.
                    weights.Add(Image.CreateFromData(128,128,false,Image.Format.Rgba8,rgba));
                }
            }
            if(used.Count>254)
                throw new InvalidOperationException("Too many terrain materials for palette lookup");

            var materialIndex=new Dictionary<uint,int>();
            foreach(uint id in used)
            {
                if(!definitions.TryGetValue(id,out var def))
                    throw new InvalidOperationException($"Missing original terrain material ID {id}");
                materialIndex[id]=materialIndex.Count;
                diffuseImgs.Add(ReadDdsImage(archive,decoder,def.DiffuseId));
                normalImgs.Add(ReadDdsImage(archive,decoder,def.NormalId));
            }
            var diffuseArray=MakeArray(diffuseImgs,"diffuse");
            var normalArray=MakeArray(normalImgs,"normal");
            var weightArray=MakeArray(weights,"blend");

            byte[] lookup=new byte[PaletteSlots*tileSide*tileSide*4];
            for(int tile=0;tile<tileSide*tileSide;tile++)
            for(int slot=0;slot<PaletteSlots;slot++)
            {
                uint id=tilePalettes[tile,slot];
                if(id==0)continue;
                int off=(tile*PaletteSlots+slot)*4;
                lookup[off]=(byte)(materialIndex[id]+1);
                // Category-41 authored texture-size scalar, in world units.
                lookup[off+1]=(byte)Math.Clamp(
                    (int)Math.Round(definitions[id].TextureSize),1,255);
                lookup[off+2]=(byte)Math.Clamp(
                    (int)Math.Round(definitions[id].Glossiness*255f),0,255);
                lookup[off+3]=255;
            }
            using var paletteImage=Image.CreateFromData(
                PaletteSlots,tileSide*tileSide,false,Image.Format.Rgba8,lookup);
            var paletteTexture=ImageTexture.CreateFromImage(paletteImage);
            var shader=new Shader{Code=ShaderCode};
            var result=new ShaderMaterial{Shader=shader};
            result.SetShaderParameter("material_diffuse",diffuseArray);
            result.SetShaderParameter("material_normals",normalArray);
            result.SetShaderParameter("material_weights",weightArray);
            result.SetShaderParameter("material_lookup",paletteTexture);
            result.SetShaderParameter("tile_origin",new Vector2I(firstX,firstZ));
            result.SetShaderParameter("tile_side",tileSide);
            result.SetShaderParameter("use_original_normals",normalEnabled);
            result.SetShaderParameter("apply_original_tint",originalColour!=null&&applyTint);
            if(originalColour!=null)
                result.SetShaderParameter("original_tile_colour",originalColour.Texture);

            return new Result(result,tileSide*tileSide,used.Count,used.Count,used.Count);
        }
        finally
        {
            foreach(var img in weights)img.Dispose();
            foreach(var img in diffuseImgs)img.Dispose();
            foreach(var img in normalImgs)img.Dispose();
        }
    }

    // Texture IDs and colour/specular data are authentic. The tile UV repeat
    // calculation below uses the category-41 authored size; exact runtime
    // rotation/bias and ESO light response have not yet been recovered.
    private const string ShaderCode=@"
shader_type spatial;
render_mode cull_disabled, diffuse_burley, specular_schlick_ggx;

uniform sampler2DArray material_diffuse : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2DArray material_normals : filter_linear_mipmap, repeat_enable;
uniform sampler2DArray material_weights : filter_linear, repeat_disable;
uniform sampler2D material_lookup : filter_nearest, repeat_disable;
uniform sampler2DArray original_tile_colour : source_color, filter_linear, repeat_disable;
uniform ivec2 tile_origin=ivec2(0,0);
uniform int tile_side=5;
uniform bool use_original_normals=true;
uniform bool apply_original_tint=true;

void fragment() {
    vec2 tile_coord=UV2;
    ivec2 patch_coord=ivec2(floor(tile_coord))-tile_origin;
    patch_coord=clamp(patch_coord,ivec2(0),ivec2(tile_side-1));
    int tile_index=patch_coord.y*tile_side+patch_coord.x;
    vec2 local_uv=fract(tile_coord);
    // ESO TerrainSingleLayerPS.hlsl: sample the 128x128 blend maps at
    // texel centres, keeping the outermost weights on the tile borders.
    // This is an exact recovered UV rule, separate from the still-unknown
    // per-material terrain texture rotation and bias.
    const float blend_pixel_width=1.0/128.0;
    vec2 blend_uv=local_uv*(1.0-blend_pixel_width)+0.5*blend_pixel_width;
    vec4 blend0=texture(material_weights,vec3(blend_uv,float(tile_index*4)));
    vec4 blend1=texture(material_weights,vec3(blend_uv,float(tile_index*4+1)));
    vec4 blend2=texture(material_weights,vec3(blend_uv,float(tile_index*4+2)));
    vec4 blend3=texture(material_weights,vec3(blend_uv,float(tile_index*4+3)));

    vec3 diffuse=vec3(0.0);
    vec3 normal_sum=vec3(0.0);
    float total=0.0;
    float roughness_sum=0.0;

    for(int slot=0;slot<16;slot++) {
        vec4 block=blend0;
        if(slot>=12)block=blend3;
        else if(slot>=8)block=blend2;
        else if(slot>=4)block=blend1;
        float weight=block[slot%4];
        if(weight<0.001)continue;

        vec2 key=vec2((float(slot)+0.5)/16.0,(float(tile_index)+0.5)/float(tile_side*tile_side));
        vec4 definition=texture(material_lookup,key);
        int layer=int(round(definition.r*255.0))-1;
        if(layer<0)continue;

        float authored_size=max(1.0,round(definition.g*255.0));
        // One original height tile spans 100 source world units.
        float repeats=100.0/authored_size;
        vec2 uv=local_uv*repeats;
        vec4 base=texture(material_diffuse,vec3(uv,float(layer)));
        diffuse+=base.rgb*weight;
        roughness_sum+=(1.0-definition.b)*weight;

        if(use_original_normals) {
            vec4 packed=texture(material_normals,vec3(uv,float(layer)));
            vec2 xy=packed.ag*2.0-1.0;
            float nz=sqrt(max(0.01,1.0-dot(xy,xy)));
            normal_sum+=vec3(xy,nz)*weight;
        }
        total+=weight;
    }
    if(total>0.001) {
        diffuse/=total;
        if(apply_original_tint) {
            vec3 tint=texture(original_tile_colour,vec3(local_uv,float(tile_index))).rgb;
            diffuse*=tint;
        }
        ALBEDO=diffuse;
        ROUGHNESS=clamp(roughness_sum/total,0.05,1.0);
        METALLIC=0.0;
        if(use_original_normals) {
            vec3 final_normal=normalize(normal_sum/max(total,0.001));
            NORMAL_MAP=final_normal*0.5+0.5;
            NORMAL_MAP_DEPTH=1.0;
        }
    } else {
        ALBEDO=vec3(0.0);
    }
}
";
}
