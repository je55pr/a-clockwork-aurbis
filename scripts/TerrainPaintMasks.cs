using System;
using System.Collections.Generic;
using System.Linq;
using System.Buffers.Binary;
using Godot;

namespace EsoTest;

/// <summary>
/// Directly inspect ONE authored terrain-paint channel from a 5x5 group of
/// original land records. Shader slot order is BGRA, as specified by
/// ESO's TerrainSingleLayerPS. Diagnostic only: slots have no verified
/// diffuse/normal texture IDs yet, so we render weights in grayscale.
/// </summary>
public static class TerrainPaintMasks
{
    public sealed record Result(Image Image,int Channel,
        int TilesRead,int TilesMissing,int ZeroPixels,int NonzeroPixels,
        float MeanWeight,int ImageWidth,int ImageHeight);

    public sealed record PatchSummary(
        int Tiles, long PixelCount, long ExactlyNormalizedPixels,
        long ApproximatelyNormalizedPixels, long[] ActiveSamples,
        long[] TotalWeights)
    {
        public string ActiveChannels => string.Join(", ",
            System.Linq.Enumerable.Range(0,16)
                .Where(i=>ActiveSamples[i]>0));
    }

    public static PatchSummary SummarizePatch(EsoArchive archive,
        OodleDecoder decoder,TerrainBundle section,int centerX,int centerZ)
    {
        var records=new Dictionary<(int,int),EsoRecord>();
        foreach(var r in section.Tiles)
            records[((int)(r.FileId>>16),(int)(r.FileId&65535))]=r;
        int tiles=0;
        long samples=0,exact=0,near=0;
        var active=new long[16];
        var weights=new long[16];
        for(int z=centerZ-2;z<=centerZ+2;z++)
        for(int x=centerX-2;x<=centerX+2;x++)
        {
            if(!records.TryGetValue((x,z),out var record))
                continue;
            var land=archive.ReadDecoded(record,decoder);
            var blocks=new byte[4][];
            for(int group=0;group<4;group++)
            {
                int sub=10+(group+2)*9;
                int off=BinaryPrimitives.ReadInt32LittleEndian(land.AsSpan(sub+1));
                int len=BinaryPrimitives.ReadInt32LittleEndian(land.AsSpan(sub+5));
                if(off<=0||len<65808||off+len>land.Length)
                    throw new InvalidOperationException("Missing paint-block source data");
                var src=land.AsSpan(off,len);
                var unpacked=new byte[128*128*4];
                int p=16;
                for(int row=0;row<128;row++)
                {
                    if(BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(p))!=512)
                        throw new InvalidOperationException("Invalid source paint row");
                    p+=2;
                    src.Slice(p,512).CopyTo(unpacked.AsSpan(row*512,512));
                    p+=512;
                }
                blocks[group]=unpacked;
            }
            for(int pixel=0;pixel<128*128;pixel++)
            {
                int sum=0;
                for(int channel=0;channel<16;channel++)
                {
                    // ESO's TerrainSingleLayerPS reads each BlendMap as
                    // .bgra: shader layers 0,1,2,3 correspond to B,G,R,A.
                    int rawChannel=(channel&3) switch { 0=>2,1=>1,2=>0,_=>3 };
                    int v=blocks[channel/4][pixel*4+rawChannel];
                    sum+=v;
                    weights[channel]+=v;
                    if(v!=0)active[channel]++;
                }
                samples++;
                if(sum==255)exact++;
                if(sum is >=252 and <=256)near++;
            }
            tiles++;
        }
        return new PatchSummary(tiles,samples,exact,near,active,weights);
    }

    public static Result ReadPatch(EsoArchive archive,OodleDecoder decoder,
        TerrainBundle section,int centerX,int centerZ,int layer)
    {
        if(layer is <0 or >15)throw new ArgumentOutOfRangeException(nameof(layer));
        const int tilePixels=128;
        const int side=5;
        int startX=centerX-2,startZ=centerZ-2;
        var records=new Dictionary<(int,int),EsoRecord>();
        foreach(var r in section.Tiles)
            records[((int)(r.FileId>>16),(int)(r.FileId&65535))]=r;
        var img=Image.CreateEmpty(side*tilePixels,side*tilePixels,false,Image.Format.Rgba8);
        int read=0,missing=0,zeros=0,nonzero=0;
        long total=0;
        var absent=new Color("#192735");
        for(int tileZ=0;tileZ<side;tileZ++)
        for(int tileX=0;tileX<side;tileX++)
        {
            var key=(startX+tileX,startZ+tileZ);
            if(!records.TryGetValue(key,out var r)){
                missing++;
                for(int y=0;y<tilePixels;y++)
                for(int x=0;x<tilePixels;x++)
                    img.SetPixel(tileX*tilePixels+x,tileZ*tilePixels+y,absent);
                continue;
            }
            var data=archive.ReadDecoded(r,decoder);
            if(data.Length<110||BinaryPrimitives.ReadUInt32LittleEndian(data)!=0xBEEF000C)
                throw new InvalidOperationException($"Unexpected land data {key}");
            int subIndex=2+layer/4;
            int subCursor=10+subIndex*9;
            int offset=(int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(subCursor+1));
            int len=(int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(subCursor+5));
            if(offset<=0||len<65808||offset+len>data.Length)
                throw new InvalidOperationException($"Land tile {key} lacks source blend group {subIndex}");
            var payload=data.AsSpan(offset,len);
            if(BinaryPrimitives.ReadUInt32LittleEndian(payload)!=0xBEEF0001 ||
               BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4))!=128 ||
               BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(8))!=128 ||
               BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(12))!=512)
                throw new InvalidOperationException($"Unrecognised blend encoding {key}");
            int p=16;
            for(int y=0;y<tilePixels;y++)
            {
                int rowBytes=BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(p));
                if(rowBytes!=512)throw new InvalidOperationException($"Invalid blend row in {key}");
                p+=2;
                for(int x=0;x<tilePixels;x++)
                {
                    int rawChannel=(layer&3) switch { 0=>2,1=>1,2=>0,_=>3 };
                    byte weight=payload[p+x*4+rawChannel];
                    total+=weight;
                    if(weight==0)zeros++;else nonzero++;
                    float value=weight/255f;
                    img.SetPixel(tileX*tilePixels+x,tileZ*tilePixels+y,
                        new Color(value,value,value,1f));
                }
                p+=512;
            }
            read++;
        }
        return new Result(img,layer,read,missing,zeros,nonzero,
            read==0?0f:(float)total/(read*tilePixels*tilePixels*255f),
            side*tilePixels,side*tilePixels);
    }
}
