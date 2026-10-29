using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace EsoTest;

/// <summary>
/// One-to-one diagnostic of an ORIGINAL single-section terrain grid.
/// Colours indicate source heightfield status, not geographic land/water.
/// Reads original DAT resources directly and creates only an in-memory image.
/// </summary>
public static class TerrainGapDiagnostics
{
    public enum Status : byte
    {
        Missing = 0,
        Valid = 1,
        BorderZero = 2,
        InteriorZero = 3,
        AllZero = 4,
        Invalid = 5
    }

    public sealed record Result(
        uint SectionId, Image Image, int Width, int Height, int TotalTiles,
        int ValidTiles, int BorderZeroTiles, int InteriorZeroTiles,
        int AllZeroTiles, int MissingTiles, int InvalidTiles,
        long ZeroSamples, long DroppedPreviewQuads)
    {
        public int AffectedTiles => BorderZeroTiles+InteriorZeroTiles+AllZeroTiles+InvalidTiles;
    }

    public static Result Scan(EsoArchive archive, OodleDecoder decoder,
        TerrainBundle section)
    {
        int minX=section.OriginX,minZ=section.OriginY;
        int width=section.MaximumX-minX+1,height=section.MaximumY-minZ+1;
        if(width<1||height<1||(long)width*height>30000)
            throw new InvalidOperationException("Unreasonable section dimensions");
        var map=new Status[width*height];
        int valid=0,border=0,interior=0,all=0,invalid=0;
        long zeros=0,skipped=0;
        foreach(var record in section.Tiles)
        {
            int x=(int)(record.FileId>>16)-minX;
            int z=(int)(record.FileId&0xffff)-minZ;
            if(x<0||z<0||x>=width||z>=height)continue;
            try
            {
                byte[] data=archive.ReadDecoded(record,decoder);
                if(!LandTile.TryParse(data,out var tile)||tile==null||
                    tile.Width!=65||tile.Height!=65)
                {
                    map[z*width+x]=Status.Invalid;invalid++;continue;
                }
                int nonzero=0,zeroBorder=0,zeroInterior=0;
                for(int row=0;row<65;row++)
                for(int col=0;col<65;col++)
                {
                    float value=tile.Samples[row*65+col];
                    if(value!=0f){nonzero++;continue;}
                    zeros++;
                    if(row==0||row==64||col==0||col==64)zeroBorder++;
                    else zeroInterior++;
                }
                for(int row=0;row<64;row+=4)
                for(int col=0;col<64;col+=4)
                {
                    int pos=row*65+col;
                    if(tile.Samples[pos]==0||tile.Samples[pos+4]==0||
                       tile.Samples[pos+4*65]==0||
                       tile.Samples[pos+4*65+4]==0)
                        skipped++;
                }
                Status status=nonzero==0?Status.AllZero:
                    zeroInterior>0?Status.InteriorZero:
                    zeroBorder>0?Status.BorderZero:Status.Valid;
                map[z*width+x]=status;
                switch(status){
                    case Status.AllZero:all++;break;
                    case Status.InteriorZero:interior++;break;
                    case Status.BorderZero:border++;break;
                    default:valid++;break;
                }
            }
            catch(Exception)
            {
                map[z*width+x]=Status.Invalid;invalid++;
            }
        }
        int missing=map.Count(s=>s==Status.Missing);
        using var image=Image.CreateEmpty(width,height,false,Image.Format.Rgba8);
        var palette=new[]{
            new Color("#2D3544"),  // absent from source
            new Color("#3E9871"),  // original geometry has nonzero samples
            new Color("#E6C66B"),  // zero only at a border
            new Color("#E89249"),  // some zero in interior
            new Color("#CC5664"),  // entire tile zero
            new Color("#A468C9")   // decode error
        };
        for(int z=0;z<height;z++)for(int x=0;x<width;x++)
            image.SetPixel(x,z,palette[(int)map[z*width+x]]);
        return new Result(section.DescriptorId,(Image)image.Duplicate(),
            width,height,section.Tiles.Count,valid,border,interior,all,
            missing,invalid,zeros,skipped);
    }
}
