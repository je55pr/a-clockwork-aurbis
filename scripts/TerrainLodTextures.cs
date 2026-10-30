using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Godot;

namespace EsoTest;

/// <summary>
/// Original ESO terrain LOD diffuse AND normal resources, read from DATs at
/// runtime. This provides source-authored baked colour and lighting detail,
/// not ESO's undiscovered close-range material palette/blend rules.
/// </summary>
public static class TerrainLodTextures
{
    public sealed record Result(
        Texture2DArray Texture, Texture2DArray? NormalTexture,
        int Width, int Height, int ImageSize, int OriginalDdsCount)
    {
        public bool HasOriginalNormals => NormalTexture != null;
    }

    public static Result Load(EsoArchive archive,OodleDecoder decoder,
        TerrainBundle section)
    {
        int width=(section.MaximumX+4)/4;
        int height=(section.MaximumY+4)/4;
        if(width<1||height<1||(long)width*height>2048)
            throw new InvalidOperationException("Unexpected terrain LOD coverage");

        Texture2DArray diffuse=LoadChannel(
            archive,decoder,section,12,width,height,512,0x31545844);
        Texture2DArray? normals=null;
        try {
            normals=LoadChannel(archive,decoder,section,13,width,height,256,0x35545844);
        }
        catch(Exception e) {
            GD.PushWarning($"Original terrain LOD normals unavailable for {section.DescriptorId}: {e.Message}");
        }
        return new Result(diffuse,normals,width,height,512,width*height);
    }

    private static Texture2DArray LoadChannel(EsoArchive archive,OodleDecoder decoder,
        TerrainBundle section,int channel,int width,int height,int size,uint fourCc)
    {
        uint key=section.DescriptorId*32u+(uint)channel;
        var source=new Dictionary<(int X,int Z),EsoRecord>();
        for(int i=section.DescriptorOrdinal+1;i<archive.Count;i++)
        {
            var rec=archive.GetRecord(i);
            var tag=archive.GetIdIndexFields(i);
            if(rec.ArchiveIndex!=section.ArchiveIndex ||
                (rec.UncompressedSize==583&&tag.Flags==0x4400&&tag.Field2==0))
                break;
            if(tag.Flags!=0x4000||tag.Field2!=key)continue;
            int x=(int)(rec.FileId>>16),z=(int)(rec.FileId&65535);
            if(x<0||z<0||x>=width||z>=height||
                !source.TryAdd((x,z),rec))
                throw new InvalidOperationException($"Invalid or duplicate LOD channel {channel} ({x},{z})");
        }
        if(source.Count!=width*height)
            throw new InvalidOperationException(
                $"LOD channel {channel}: {source.Count}/{width*height} original DDS chunks");

        var images=new Godot.Collections.Array<Image>();
        try {
            for(int z=0;z<height;z++)
            for(int x=0;x<width;x++)
            {
                byte[] bytes=archive.ReadDecoded(source[(x,z)],decoder);
                if(bytes.Length<140||
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes)!=0x12345678||
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12))!=0x20534444||
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(96))!=fourCc)
                    throw new InvalidOperationException($"Unexpected original DDS at {x},{z}, channel {channel}");
                var img=new Image();
                Error status=img.LoadDdsFromBuffer(bytes.AsSpan(12).ToArray());
                if(status!=Error.Ok||img.IsEmpty()||
                    img.GetWidth()!=size||img.GetHeight()!=size)
                {
                    img.Dispose();
                    throw new InvalidOperationException(
                        $"Cannot decode original LOD channel {channel} DDS {x},{z}: {status}");
                }
                images.Add(img);
            }
            var array=new Texture2DArray();
            Error result=array.CreateFromImages(images);
            if(result!=Error.Ok)
            {
                array.Dispose();
                throw new InvalidOperationException($"Godot DDS array channel {channel}: {result}");
            }
            return array;
        }
        finally {
            foreach(var image in images)image.Dispose();
        }
    }

    public static ShaderMaterial CreateMaterial(Result lod,bool normalEnabled=true,
        TerrainNearFieldColors.Result? nearColour=null,bool applyTint=false)
    {
        var shader=new Shader
        {
            Code=@"shader_type spatial;
render_mode cull_disabled, diffuse_burley, specular_schlick_ggx;

uniform sampler2DArray original_lod_diffuse : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2DArray original_lod_normal : filter_linear_mipmap, repeat_disable;
uniform int lod_grid_width = 1;
uniform int lod_grid_height = 1;
uniform sampler2D full_detail_mask : filter_nearest, repeat_disable;
uniform bool use_full_detail_mask=false;
uniform ivec2 full_detail_mask_origin=ivec2(0,0);
uniform ivec2 full_detail_mask_size=ivec2(1,1);
uniform bool use_original_normals = true;
uniform float normal_strength : hint_range(0.0,1.0) = 0.65;
uniform sampler2DArray original_tile_colour : source_color, filter_linear_mipmap, repeat_disable;
uniform bool apply_original_tint = false;
uniform ivec2 tint_origin = ivec2(0,0);
uniform int tint_grid_width = 5;
uniform int tint_grid_height = 5;
uniform float experimental_tint_strength : hint_range(0.0,1.0) = 0.65;

void fragment() {
    ivec2 coord=ivec2(floor(UV2))-full_detail_mask_origin;
    if(use_full_detail_mask && all(greaterThanEqual(coord,ivec2(0))) &&
       all(lessThan(coord,full_detail_mask_size)) &&
       texture(full_detail_mask,(vec2(coord)+vec2(0.5))/vec2(full_detail_mask_size)).r>0.5)
        discard;
    vec2 cell = floor(UV);
    vec2 tile_uv = fract(UV);
    int gx = clamp(int(cell.x),0,lod_grid_width-1);
    int gz = clamp(int(cell.y),0,lod_grid_height-1);
    float layer = float(gz*lod_grid_width+gx);
    vec3 authored_colour=texture(original_lod_diffuse,vec3(tile_uv,layer)).rgb;
    if(apply_original_tint) {
        vec2 tile=UV2;
        ivec2 tile_index=ivec2(floor(tile))-tint_origin;
        tile_index=clamp(tile_index,ivec2(0,0),
            ivec2(tint_grid_width-1,tint_grid_height-1));
        float tint_layer=float(tile_index.y*tint_grid_width+tile_index.x);
        vec3 source_tint=texture(original_tile_colour,
            vec3(fract(tile),tint_layer)).rgb;
        // Optional diagnostic: baked diffuse may already contain this tint.
        authored_colour *= mix(vec3(1.0),source_tint,experimental_tint_strength);
    }
    ALBEDO=authored_colour;
    METALLIC=0.0;
    ROUGHNESS=0.85;
    if(use_original_normals) {
        vec4 packed=texture(original_lod_normal,vec3(tile_uv,layer));
        // Original terrain-lod-normal is DXT5nm, horizontal axes in A and G.
        // Do not interpret unused red/blue as XYZ.
        vec2 xy=(vec2(packed.a,packed.g)*2.0-1.0);
        if (packed.a < 0.05 || packed.g < 0.05) {
            xy=vec2(0.0);
        }
        xy=clamp(xy*normal_strength,vec2(-0.95),vec2(0.95));
        float nZ=sqrt(max(0.01,1.0-dot(xy,xy)));
        NORMAL_MAP=vec3(xy,nZ)*0.5+0.5;
        NORMAL_MAP_DEPTH=1.0;
    }
}"
        };
        var material=new ShaderMaterial{Shader=shader};
        material.SetShaderParameter("original_lod_diffuse",lod.Texture);
        material.SetShaderParameter("lod_grid_width",lod.Width);
        material.SetShaderParameter("lod_grid_height",lod.Height);
        material.SetShaderParameter("use_original_normals",lod.HasOriginalNormals&&normalEnabled);
        if(lod.NormalTexture!=null)
            material.SetShaderParameter("original_lod_normal",lod.NormalTexture);
        if(nearColour!=null) {
            material.SetShaderParameter("original_tile_colour",nearColour.Texture);
            material.SetShaderParameter("tint_origin",new Vector2I(nearColour.SourceX,nearColour.SourceZ));
            material.SetShaderParameter("tint_grid_width",nearColour.Side);
            material.SetShaderParameter("tint_grid_height",nearColour.Side);
            material.SetShaderParameter("apply_original_tint",applyTint);
        }
        return material;
    }
}
