using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using Godot;

namespace EsoTest;

/// <summary>
/// Original ESO terrain-color RGBA maps. Read source BEEF000C per-tile
/// subrecord ID 1 (BEEF0001 128x128x4), not generated from the LOD diffuse.
/// Intended for experimental close-up tinting and visual source inspection.
/// </summary>
public static class TerrainNearFieldColors
{
    public sealed record Result(Texture2DArray Texture,int LoadedTiles,
        int SourceX,int SourceZ,int Side);

    public static Result LoadPatch(EsoArchive archive,OodleDecoder decoder,
        TerrainBundle section,int centerX,int centerZ,int radius=2)
        => LoadRegion(archive,decoder,section,centerX-radius,centerZ-radius,2*radius+1);

    public static Result LoadRegion(EsoArchive archive,OodleDecoder decoder,
        TerrainBundle section,int startX,int startZ,int side)
    {
        if(side is <1 or >8)throw new ArgumentOutOfRangeException(nameof(side));
        var recordMap=new Dictionary<(int,int),EsoRecord>();
        foreach(var r in section.Tiles)
            recordMap[((int)(r.FileId>>16),(int)(r.FileId&65535))]=r;
        var images=new Godot.Collections.Array<Image>();
        try {
            for(int z=0;z<side;z++)
            for(int x=0;x<side;x++)
            {
                if(!recordMap.TryGetValue((startX+x,startZ+z),out var record))
                    throw new InvalidOperationException(
                        $"Original terrain-colour record missing ({startX+x},{startZ+z})");
                byte[] bytes=archive.ReadDecoded(record,decoder);
                if(bytes.Length<130||BinaryPrimitives.ReadUInt32LittleEndian(bytes)!=0xBEEF000C)
                    throw new InvalidOperationException("Unexpected source land record");
                int header=10+9;
                int off=BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(header+1));
                int length=BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(header+5));
                if(bytes[header]!=1||off<20||length<65812||off+length>bytes.Length)
                    throw new InvalidOperationException("Original terrain-colour subrecord invalid");
                var span=bytes.AsSpan(off,length);
                if(BinaryPrimitives.ReadUInt32LittleEndian(span)!=0xBEEF0001||
                    BinaryPrimitives.ReadInt32LittleEndian(span.Slice(4))!=128||
                    BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8))!=128||
                    BinaryPrimitives.ReadInt32LittleEndian(span.Slice(12))!=512)
                    throw new InvalidOperationException("Unexpected original terrain colour encoding");
                var rgba=new byte[128*128*4];
                int read=16;
                for(int row=0;row<128;row++)
                {
                    if(BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(read))!=512)
                        throw new InvalidOperationException("Bad colour row stride");
                    read+=2;
                    span.Slice(read,512).CopyTo(rgba.AsSpan(row*512,512));
                    read+=512;
                }
                images.Add(Image.CreateFromData(128,128,false,Image.Format.Rgba8,rgba));
            }
            var textures=new Texture2DArray();
            var result=textures.CreateFromImages(images);
            if(result!=Error.Ok){textures.Dispose();throw new InvalidOperationException(
                $"Cannot load authored terrain colour array: {result}");}
            return new Result(textures,images.Count,startX,startZ,side);
        }
        finally {
            foreach(var img in images)img.Dispose();
        }
    }
}
