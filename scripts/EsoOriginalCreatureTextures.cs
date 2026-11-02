using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;

namespace EsoTest;

/// <summary>
/// Source-only DDS loader for experimental ESO creature materials.
/// The optional mesh-to-DDS assignments come ONLY from a local research
/// manifest, never from unverified numeric proximity or inferred colors.
/// The genuine bytes are read on demand from the installed game archives.
/// Neither the manifest nor source DDS images are committed to Git.
/// </summary>
internal sealed class EsoOriginalCreatureTextures : IDisposable
{
    internal sealed record Mapping(uint MeshId,uint Diffuse,uint Normal,uint Specular,string Evidence);
    private readonly EsoArchive _archive;
    private readonly OodleDecoder _oodle;
    private readonly Dictionary<uint,Texture2D?> _diffuse=new();
    private readonly Dictionary<uint,Texture2D?> _normal=new();
    private readonly Dictionary<uint,Mapping> _mappings=new();
    internal int MappingCount=>_mappings.Count;
    internal int LoadedDiffuseCount=>_diffuse.Count;
    internal int LoadedNormalCount=>_normal.Count;

    internal EsoOriginalCreatureTextures(string manifest,string oodleDll,string mappings)
    {
        _archive=new EsoArchive(manifest);
        _oodle=OodleDecoder.FromEnvironment(oodleDll)
            ??throw new FileNotFoundException("Local original ESO Oodle decoder unavailable");
        if(!File.Exists(mappings))return;
        foreach(string line in File.ReadLines(mappings))
        {
            string text=line.Trim();
            if(text.Length==0||text.StartsWith('#'))continue;
            var fields=text.Split('\t');
            if(fields.Length is not (4 or 5) ||
               !uint.TryParse(fields[0],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint mesh)||
               !uint.TryParse(fields[1],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint diffuse)||
               !uint.TryParse(fields[2],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint normal)||
               (diffuse==0&&normal==0))
                throw new InvalidDataException("Bad original ESO texture-candidate fields: "+text);
            uint specular=0;
            if(fields.Length==5 &&
               !uint.TryParse(fields[3],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out specular))
                throw new InvalidDataException("Bad original ESO specular texture candidate: "+text);
            string evidence=fields[^1];
            if(evidence is not ("uv-provisional" or "source-verified" or
                 "archive-neighbor-provisional" or "archive-triplet-provisional"))
                throw new InvalidDataException("Unknown texture candidate evidence classification");
            if(_mappings.Count>=5000||!_mappings.TryAdd(mesh,
                new Mapping(mesh,diffuse,normal,specular,evidence)))
                throw new InvalidDataException("Duplicate or excessive original material mapping");
        }
    }

    internal Mapping? SourceMapping(uint meshId)=>
        _mappings.TryGetValue(meshId,out var source)?source:null;

    internal Material? LoadMaterial(uint meshId)
    {
        var mapping=SourceMapping(meshId);
        if(mapping==null)return null;
        Texture2D? diffuse=mapping.Diffuse!=0?GetTexture(mapping.Diffuse,_diffuse,false):null;
        Texture2D? normal=mapping.Normal!=0?GetTexture(mapping.Normal,_normal,true):null;
        if(diffuse==null)return null;
        var material=new StandardMaterial3D
        {
            AlbedoColor=Colors.White,AlbedoTexture=diffuse,
            Roughness=0.78f,CullMode=BaseMaterial3D.CullModeEnum.Disabled,
        };
        if(normal!=null)
        {
            material.NormalEnabled=true;
            material.NormalTexture=normal;
            material.NormalScale=1f;
        }
        return material;
    }

    private Texture2D? GetTexture(uint id,Dictionary<uint,Texture2D?> cache,bool normal)
    {
        if(cache.TryGetValue(id,out var value))return value;
        if(cache.Count>=256)return null;
        try
        {
            using var img=ReadOriginalDds(id);
            if(img==null){cache.Add(id,null);return null;}
            if(!normal)
            {
                var tex=ImageTexture.CreateFromImage(img);
                cache.Add(id,tex);
                return tex;
            }
            int width=img.GetWidth(),height=img.GetHeight();
            byte[] source=img.GetData();
            int size=checked(width*height*4);
            if(source.Length<size)throw new InvalidDataException("Original DDS normal stream truncated");
            // ESO creature normal textures use X in alpha and Y in green.
            // Reject dark specular maps that happen to sit next to a model.
            long green=0,alpha=0;
            int count=0;
            int stride=Math.Max(1,(width*height)/4096);
            for(int pixel=0;pixel<width*height;pixel+=stride)
            {
                int offset=pixel*4;
                green+=source[offset+1];
                alpha+=source[offset+3];
                count++;
            }
            float meanGreen=green/(255f*count);
            float meanAlpha=alpha/(255f*count);
            if(meanGreen is <0.28f or >0.73f ||
               meanAlpha is <0.28f or >0.73f)
                throw new InvalidDataException(
                    $"Expected ESO packed-normal G/A channels; measured G={meanGreen:F2}, A={meanAlpha:F2}");
            byte[] converted=new byte[size];
            // Original ESO ZoCommon.fxh: normal X in alpha, Y in green.
            // Reconstruct positive tangent-space Z for Godot's normal map.
            for(int p=0;p<size;p+=4)
            {
                float x=source[p+3]/127.5f-1f;
                float y=source[p+1]/127.5f-1f;
                float z=MathF.Sqrt(MathF.Max(0,1-x*x-y*y));
                converted[p]=source[p+3];
                converted[p+1]=source[p+1];
                converted[p+2]=(byte)Math.Clamp((int)MathF.Round((z*0.5f+0.5f)*255f),0,255);
                converted[p+3]=255;
            }
            using var normalImage=Image.CreateFromData(width,height,false,Image.Format.Rgba8,converted);
            normalImage.GenerateMipmaps();
            var texture=ImageTexture.CreateFromImage(normalImage);
            cache.Add(id,texture);
            return texture;
        }
        catch(Exception e) when(e is IOException or InvalidDataException or OverflowException or ArgumentException)
        {
            GD.PushWarning($"Original DDS {id:X8} unavailable: {e.Message}");
            cache[id]=null;
            return null;
        }
    }

    private Image? ReadOriginalDds(uint id)
    {
        var record=_archive.FindId(id).FirstOrDefault(x=>
            _archive.GetIdIndexFields(x.Ordinal).Flags==0 &&
            x.UncompressedSize is >=128 and <=20_000_000);
        if(record.UncompressedSize==0)return null;
        byte[] bytes=_archive.ReadDecoded(record,_oodle);
        if(bytes.Length<128 || BinaryPrimitives.ReadUInt32LittleEndian(bytes)!=0x20534444u)
            return null;
        uint width=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16,4));
        uint height=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12,4));
        uint codec=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(84,4));
        int block=codec switch
        {
            0x31545844u=>8,  // BC1 / DXT1
            0x33545844u=>16, // BC2 / DXT3
            0x35545844u=>16, // BC3 / DXT5
            _=>0
        };
        if(block==0||width is <4 or >4096||height is <4 or >4096)
            return null;
        uint linearSize=checked(((width+3u)/4u)*((height+3u)/4u)*(uint)block);
        if(bytes.Length<128+linearSize)return null;
        uint flags=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8,4));
        // ESO sometimes stores nonstandard pitch/linear-size fields. Patch
        // only a private in-memory copy, preserving the installed source.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8,4),
            (flags|0x80000u)&~0x8u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20,4),linearSize);
        var image=new Image();
        if(image.LoadDdsFromBuffer(bytes)!=Error.Ok||image.IsEmpty())
        {image.Dispose();return null;}
        if(image.IsCompressed()&&image.Decompress()!=Error.Ok)
        {image.Dispose();return null;}
        image.Convert(Image.Format.Rgba8);
        return image;
    }

    public void Dispose()
    {
        _oodle.Dispose();
        _archive.Dispose();
    }
}
