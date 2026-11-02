using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using EsoTest;

/// <summary>
/// Read-only similarity probe for ORIGINAL archive DDS mipmaps, based on visual
/// atlas structure, not file-ID adjacency. All matches are research leads only.
/// It never declares a runtime mesh-material binding or writes image bytes.
/// </summary>
internal static class EsoDdsAtlasSearch
{
    // Uncompressed sizes of common BC1/BC2/BC3 DDS mip chains.
    // A matching size is only a scan filter, not evidence of a real texture.
    private static readonly HashSet<uint> LikelyTextures=new()
    {
        65664,174904,174928,262272,349672,349680,349712,
        699192,699216,1398248,1398256,1398288,2796344,
        2796368,5592560
    };
    private static readonly HashSet<uint> LikelyColourAtlases=new()
    {
        174904,349672,699192,1398248,2796344
    };
    private sealed record Result(uint Id,int Size,float Score,float Luma,float Edge);
    private sealed class Image32
    {
        internal readonly float[] Luma=new float[1024];
        internal readonly float[] Edge=new float[1024];
        internal float MeanL,MeanE,StdL,StdE;
        internal void Prepare()
        {
            MeanL=Luma.Average();
            for(int y=1;y<31;y++)
                for(int x=1;x<31;x++)
                {
                    int p=y*32+x;
                    float a=MathF.Abs(Luma[p-1]-Luma[p+1]);
                    float b=MathF.Abs(Luma[p-32]-Luma[p+32]);
                    Edge[p]=MathF.Sqrt(a*a+b*b);
                }
            MeanE=Edge.Average();
            float lv=0,ev=0;
            for(int i=0;i<1024;i++)
            {
                lv+=(Luma[i]-MeanL)*(Luma[i]-MeanL);
                ev+=(Edge[i]-MeanE)*(Edge[i]-MeanE);
            }
            StdL=MathF.Sqrt(lv/1024);
            StdE=MathF.Sqrt(ev/1024);
        }
    }
    private static float Corr(float[] a,float meanA,float devA,float[] b,float meanB,float devB)
    {
        if(devA<0.009f||devB<0.009f)return 0;
        double dot=0;
        for(int p=0;p<1024;p++)dot+=(a[p]-meanA)*(b[p]-meanB);
        return Math.Clamp((float)(dot/(1024*devA*devB)),-1,1);
    }
    private static Image32? ReadMip(byte[] bytes)
    {
        if(bytes.Length<128 || BitConverter.ToUInt32(bytes,0)!=0x20534444u)return null;
        int width=BitConverter.ToInt32(bytes,16),height=BitConverter.ToInt32(bytes,12);
        int mips=BitConverter.ToInt32(bytes,28);
        uint format=BitConverter.ToUInt32(bytes,84);
        if(width is <32 or >4096||height is <32 or >4096||mips is <1 or >16)return null;
        int bpb=format switch {0x31545844u=>8,0x33545844u or 0x35545844u=>16,_=>0};
        if(bpb==0)return null;
        int ptr=128,w=width,h=height;
        for(int m=0;m<mips;m++)
        {
            int blockCols=(w+3)/4,blockRows=(h+3)/4;
            long n=(long)blockCols*blockRows*bpb;
            if(n>bytes.Length-ptr)return null;
            if((w==32||h==32) && w is >=16 and <=64 && h is >=16 and <=64)
            {
                var img=new Image32();
                var unpacked=new float[w*h];
                for(int by=0;by<blockRows;by++)
                    for(int bx=0;bx<blockCols;bx++)
                    {
                        int offset=ptr+(by*blockCols+bx)*bpb+(bpb==16?8:0);
                        ushort c0=BitConverter.ToUInt16(bytes,offset);
                        ushort c1=BitConverter.ToUInt16(bytes,offset+2);
                        float Decode(ushort c)=>
                            (0.2126f*((c>>11)&31)/31f+
                             0.7152f*((c>>5)&63)/63f+
                             0.0722f*(c&31)/31f);
                        float a=Decode(c0),b=Decode(c1);
                        var palette=new[]{a,b,(2*a+b)/3,(a+2*b)/3};
                        if(bpb==8 && c0<=c1)palette=new[]{a,b,(a+b)/2,0};
                        uint code=BitConverter.ToUInt32(bytes,offset+4);
                        for(int y=0;y<4;y++)
                            for(int x=0;x<4;x++)
                            {
                                int xPos=bx*4+x,yPos=by*4+y;
                                if(xPos>=w||yPos>=h)continue;
                                int index=(int)((code>>(2*(y*4+x)))&3u);
                                unpacked[yPos*w+xPos]=palette[index];
                            }
                    }
                for(int y=0;y<32;y++)
                    for(int x=0;x<32;x++)
                        img.Luma[y*32+x]=unpacked[
                            Math.Min(h-1,y*h/32)*w+Math.Min(w-1,x*w/32)];
                img.Prepare();
                return img;
            }
            ptr+=checked((int)n);
            w=Math.Max(1,w/2);h=Math.Max(1,h/2);
        }
        return null;
    }
    internal static int Run(string[] args,bool countOnly)
    {
        if((countOnly && args.Length!=1)||(!countOnly && args.Length is <5 or >7))
        {
            Console.Error.WriteLine(countOnly
                ?"Usage: --count-original-dds-atlases <manifest>"
                :"Usage: --search-original-dds-atlases <manifest> <oodle> <reference-dds-id> <output.tsv> <max-records> [top-results] [colour-only]");
            return 2;
        }
        bool colourOnly=!countOnly && args.Length==7 && args[6]=="colour-only";
        if(!countOnly && args.Length==7 && !colourOnly)
            throw new ArgumentException("Unknown atlas search filter");
        using var archive=new EsoArchive(args[0]);
        var eligible=new List<EsoRecord>();
        var counts=new Dictionary<uint,int>();
        for(int i=0;i<archive.Count;i++)
        {
            var r=archive.GetRecord(i);
            if(archive.GetIdIndexFields(r.Ordinal).Flags!=0||r.Compression!=8 ||
               !LikelyTextures.Contains(r.UncompressedSize)||
               (colourOnly&&!LikelyColourAtlases.Contains(r.UncompressedSize)))continue;
            eligible.Add(r);
            counts[r.UncompressedSize]=counts.GetValueOrDefault(r.UncompressedSize)+1;
        }
        Console.WriteLine($"MNF total records={archive.Count:N0}, standard atlas candidates={eligible.Count:N0}");
        foreach(var c in counts.OrderByDescending(x=>x.Value))
            Console.WriteLine($"  size={c.Key} records={c.Value}");
        if(countOnly)return 0;
        using var oodle=OodleDecoder.FromEnvironment(args[1])??
            throw new FileNotFoundException("Source ESO decoder unavailable");
        uint referenceId=uint.Parse(args[2],NumberStyles.HexNumber,CultureInfo.InvariantCulture);
        var source=archive.FindId(referenceId).FirstOrDefault(r=>
            archive.GetIdIndexFields(r.Ordinal).Flags==0);
        var reference=ReadMip(archive.ReadDecoded(source,oodle))??
            throw new InvalidDataException("Reference DDS lacks a supported 32x32 mip image");
        int maxRecords=int.Parse(args[4],CultureInfo.InvariantCulture);
        if(maxRecords is <1 or >100000)throw new ArgumentOutOfRangeException(nameof(maxRecords));
        int top=args.Length>5?int.Parse(args[5],CultureInfo.InvariantCulture):60;
        if(top is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(top));
        var results=new List<Result>();
        int inspected=0,valid=0,failed=0;
        foreach(var rec in eligible)
        {
            if(inspected>=maxRecords)break;
            inspected++;
            try
            {
                var img=ReadMip(archive.ReadDecoded(rec,oodle));
                if(img==null)continue;
                valid++;
                float l=Corr(reference.Luma,reference.MeanL,reference.StdL,img.Luma,img.MeanL,img.StdL);
                float e=Corr(reference.Edge,reference.MeanE,reference.StdE,img.Edge,img.MeanE,img.StdE);
                float score=0.52f*e+0.48f*l;
                results.Add(new Result(rec.FileId,checked((int)rec.UncompressedSize),score,l,e));
            }
            catch(Exception ex) when(ex is InvalidDataException or IOException or NotSupportedException or OverflowException)
            {failed++;}
            if(inspected%5000==0)
                Console.Error.WriteLine($"source DDS inspected={inspected}/{maxRecords}, decodable-atlas={valid}, failure={failed}");
        }
        var ranked=results.OrderByDescending(x=>x.Score).Take(top).ToArray();
        string output=Path.GetFullPath(args[3]);
        string research=Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,".research"));
        if(!output.StartsWith(Path.TrimEndingDirectorySeparator(research)+Path.DirectorySeparatorChar,
           StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Similarity results must stay in ignored local .research");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using(var writer=new StreamWriter(output,false,new UTF8Encoding(false)))
        {
            writer.WriteLine("asset-id\tsource-DDS-bytes\tatlas-similarity\tluminance-correlation\tedge-correlation");
            foreach(var x in ranked)
                writer.WriteLine($"{x.Id:X8}\t{x.Size}\t{x.Score:F5}\t{x.Luma:F5}\t{x.Edge:F5}");
        }
        Console.WriteLine($"REFERENCE={referenceId:X8} scanned={inspected} valid={valid} failures={failed} top={ranked.Length}");
        foreach(var x in ranked.Take(24))
            Console.WriteLine($"  {x.Id:X8} score={x.Score:F4} luma={x.Luma:F4} edge={x.Edge:F4} size={x.Size}");
        Console.WriteLine($"Research-only ranked atlas leads: {output}");
        return 0;
    }
}
