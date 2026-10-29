using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace EsoTest;

/// <summary>
/// Small, in-memory elevation overview for visually comparing an original
/// ESO terrain package against the known Auridon 2D source map.
/// The palette is RELATIVE ELEVATION and does not infer sea level or map ID.
/// </summary>
public static class TerrainElevationOverview
{
    public sealed record Result(Image Image, int Width, int Height,
        int Tiles, int Missing, int Failed, float LowReference,
        float HighReference);

    private const int SamplesPerTile=8;

    public static Result Generate(EsoArchive archive,OodleDecoder decoder,
        TerrainBundle section)
    {
        int width=section.MaximumX-section.OriginX+1;
        int height=section.MaximumY-section.OriginY+1;
        if(width<1||height<1||(long)width*height>30000)
            throw new InvalidOperationException("Invalid source section extent");

        int widthPx=width*SamplesPerTile,heightPx=height*SamplesPerTile;
        var values=new float[widthPx*heightPx];
        var available=new bool[values.Length];
        var finiteValues=new List<float>(section.Tiles.Count*64);
        int succeeded=0,failed=0;
        foreach(var rec in section.Tiles)
        {
            int tileX=(int)(rec.FileId>>16)-section.OriginX;
            int tileZ=(int)(rec.FileId&0xffff)-section.OriginY;
            if(tileX<0||tileZ<0||tileX>=width||tileZ>=height)continue;
            try
            {
                var decoded=archive.ReadDecoded(rec,decoder);
                if(!LandTile.TryParse(decoded,out var tile)||tile==null ||
                   tile.Width!=65 ||tile.Height!=65)
                {failed++;continue;}
                for(int j=0;j<SamplesPerTile;j++)
                for(int i=0;i<SamplesPerTile;i++)
                {
                    // Mid-cell samples avoid artificial zero padding at the
                    // original tile's external row/column.
                    int sx=i*8+4,sz=j*8+4;
                    float h=tile.Samples[sz*65+sx];
                    if(h==0)continue;
                    int at=(tileZ*SamplesPerTile+j)*widthPx +
                        tileX*SamplesPerTile+i;
                    values[at]=h;
                    available[at]=true;
                    finiteValues.Add(h);
                }
                succeeded++;
            }
            catch(Exception){failed++;}
        }
        if(finiteValues.Count==0)
            throw new InvalidOperationException("No usable height samples.");
        finiteValues.Sort();
        float low=finiteValues[(int)(finiteValues.Count*0.05)];
        float high=finiteValues[(int)(finiteValues.Count*0.95)];
        if(high<=low)high=low+1;

        var image=Image.CreateEmpty(widthPx,heightPx,false,Image.Format.Rgba8);
        var ground=new Color("#31566C");
        var hills=new Color("#699976");
        var peaks=new Color("#EEE0C4");
        var missing=new Color("#1B2533");
        for(int j=0;j<heightPx;j++)
        for(int i=0;i<widthPx;i++)
        {
            int at=j*widthPx+i;
            if(!available[at]){image.SetPixel(i,j,missing);continue;}
            float t=Math.Clamp((values[at]-low)/(high-low),0f,1f);
            Color color=t<0.5f?
                ground.Lerp(hills,t*2f):
                hills.Lerp(peaks,(t-0.5f)*2f);
            image.SetPixel(i,j,color);
        }
        return new Result(image,width,height,succeeded,
            width*height-section.Tiles.Count,failed,low,high);
    }
}
