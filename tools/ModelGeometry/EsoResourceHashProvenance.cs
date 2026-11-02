using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using EsoTest;

/// <summary>Investigate whether the original ESO archive's 32-bit
/// manifest-hash field derives from known resource paths or file bytes.
/// Tests a bounded sample; it never invents an external material link.</summary>
internal static class EsoResourceHashProvenance
{
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc=~0u;
        foreach(byte value in data)
        {
            crc^=value;
            for(int i=0;i<8;i++)
                crc=(crc>>1)^((crc&1)!=0?0xEDB88320u:0u);
        }
        return ~crc;
    }
    private static uint Fnv1A(ReadOnlySpan<byte> data)
    {
        uint h=2166136261u;
        foreach(byte b in data)h=(h^b)*16777619u;
        return h;
    }
    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a=1,b=0;
        foreach(byte c in data)
        {
            a=(a+c)%65521u;
            b=(b+a)%65521u;
        }
        return (b<<16)|a;
    }
    private static uint Djb2(ReadOnlySpan<byte> data)
    {
        uint h=5381;
        foreach(byte c in data)h=((h<<5)+h)+c;
        return h;
    }
    private static uint Jenkins(ReadOnlySpan<byte> data)
    {
        uint h=0;
        foreach(byte c in data)
        {
            h+=c;h+=h<<10;h^=h>>6;
        }
        h+=h<<3;h^=h>>11;h+=h<<15;
        return h;
    }
    private static uint Murmur32(ReadOnlySpan<byte> data)
    {
        const uint c1=0xcc9e2d51u,c2=0x1b873593u;
        uint hash=0;
        int i=0;
        for(;i+4<=data.Length;i+=4)
        {
            uint k=BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            k*=c1;k=BitOperations.RotateLeft(k,15);k*=c2;
            hash^=k;
            hash=BitOperations.RotateLeft(hash,13)*5u+0xe6546b64u;
        }
        uint tail=0;
        for(int j=data.Length-1;j>=i;j--)
            tail=(tail<<8)|data[j];
        if(data.Length>i)
        {
            tail*=c1;tail=BitOperations.RotateLeft(tail,15);tail*=c2;
            hash^=tail;
        }
        hash^=(uint)data.Length;
        hash^=hash>>16;hash*=0x85ebca6bu;hash^=hash>>13;
        hash*=0xc2b2ae35u;hash^=hash>>16;
        return hash;
    }
    private static uint XxHash32(ReadOnlySpan<byte> data)
    {
        const uint p1=2654435761u,p2=2246822519u,p3=3266489917u,
            p4=668265263u,p5=374761393u;
        int i=0;
        uint h;
        if(data.Length>=16)
        {
            uint v1=unchecked(p1+p2),v2=p2,v3=0,v4=unchecked(0u-p1);
            static uint Round(uint acc,uint val)=>
                BitOperations.RotateLeft(acc+val*2246822519u,13)*2654435761u;
            for(;i<=data.Length-16;i+=16)
            {
                v1=Round(v1,BinaryPrimitives.ReadUInt32LittleEndian(data[i..]));
                v2=Round(v2,BinaryPrimitives.ReadUInt32LittleEndian(data[(i+4)..]));
                v3=Round(v3,BinaryPrimitives.ReadUInt32LittleEndian(data[(i+8)..]));
                v4=Round(v4,BinaryPrimitives.ReadUInt32LittleEndian(data[(i+12)..]));
            }
            h=BitOperations.RotateLeft(v1,1)+BitOperations.RotateLeft(v2,7)+
                BitOperations.RotateLeft(v3,12)+BitOperations.RotateLeft(v4,18);
        }
        else h=p5;
        h+=(uint)data.Length;
        for(;i+4<=data.Length;i+=4)
            h=BitOperations.RotateLeft(h+
                BinaryPrimitives.ReadUInt32LittleEndian(data[i..])*p3,17)*p4;
        for(;i<data.Length;i++)
            h=BitOperations.RotateLeft(h+data[i]*p5,11)*p1;
        h^=h>>15;h*=p2;h^=h>>13;h*=p3;h^=h>>16;
        return h;
    }

    private static readonly (string Name,Func<byte[],uint> Hash)[] Hashes={
        ("CRC32",bytes=>Crc32(bytes)),
        ("FNV1a32",bytes=>Fnv1A(bytes)),
        ("Adler32",bytes=>Adler32(bytes)),
        ("DJB2",bytes=>Djb2(bytes)),
        ("Jenkins",bytes=>Jenkins(bytes)),
        ("Murmur3x86_32",bytes=>Murmur32(bytes)),
        ("xxHash32",bytes=>XxHash32(bytes))
    };

    private static void CheckKnownVectors()
    {
        byte[] digits=Encoding.ASCII.GetBytes("123456789");
        byte[] hello=Encoding.ASCII.GetBytes("hello");
        byte[] wiki=Encoding.ASCII.GetBytes("Wikipedia");
        byte[] foo=Encoding.ASCII.GetBytes("foo");
        if(Crc32(digits)!=0xCBF43926u||
           Fnv1A(hello)!=0x4F9F2CABu||
           Adler32(wiki)!=0x11E60398u||
           Murmur32(foo)!=0xF6A5C420u||
           XxHash32(Array.Empty<byte>())!=0x02CC5D05u)
            throw new InvalidOperationException(
                "Known standard hash vectors failed; no resource hash conclusions permitted");
    }

    internal static int Run(string[] args)
    {
        CheckKnownVectors();
        if(args.Length is <3 or >4)
        {
            Console.Error.WriteLine("--audit-original-resource-hashes <manifest> <oodle> <local-index-tsv> [sample-count]");
            return 2;
        }
        int limit=args.Length==4?int.Parse(args[3],CultureInfo.InvariantCulture):18;
        if(limit is <1 or >100)throw new ArgumentOutOfRangeException(nameof(limit));
        using var archive=new EsoArchive(args[0]);
        using var oodle=OodleDecoder.FromEnvironment(args[1])??
            throw new FileNotFoundException("Local original Oodle decoder missing");
        int samples=0,nameMatches=0,byteMatches=0;
        foreach(string row in File.ReadLines(args[2]))
        {
            string[] fields=row.Split('\t');
            if(fields.Length!=2||
               !uint.TryParse(fields[0],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint id)||
               !fields[1].EndsWith(".dds",StringComparison.OrdinalIgnoreCase))
                continue;
            var record=archive.FindId(id).FirstOrDefault(r=>
                archive.GetIdIndexFields(r.Ordinal).Flags==0);
            if(record.UncompressedSize is <128 or >2000000)continue;
            string path=fields[1];
            string[] variants={
                path,path.ToLowerInvariant(),path.ToUpperInvariant(),
                path.TrimStart('/'),path.TrimStart('/').Replace('/','\\'),
                path.ToUpperInvariant().Replace('/','\\'),
                Path.GetFileName(path),Path.GetFileName(path).ToLowerInvariant()
            };
            var names=new List<string>();
            foreach(var variant in variants.Distinct())
            {
                byte[] name=Encoding.UTF8.GetBytes(variant);
                foreach(var (algo,hash) in Hashes)
                    if(hash(name)==record.Hash)names.Add(algo+" "+variant);
            }
            var contents=new List<string>();
            try
            {
                byte[] decoded=archive.ReadDecoded(record,oodle);
                byte[] packed=archive.ReadRaw(record);
                foreach(var (algo,hash) in Hashes)
                {
                    if(hash(decoded)==record.Hash)contents.Add(algo+" decoded");
                    if(hash(packed)==record.Hash)contents.Add(algo+" packed");
                }
            }
            catch(Exception e)when(e is InvalidDataException or IOException or NotSupportedException){}
            Console.WriteLine($"RESOURCE {id:X8} manifest={record.Hash:X8} "+
                $"name-hash={string.Join(";",names.DefaultIfEmpty("<none>"))} "+
                $"content-hash={string.Join(";",contents.DefaultIfEmpty("<none>"))}");
            if(names.Count>0)nameMatches++;
            if(contents.Count>0)byteMatches++;
            samples++;
            if(samples>=limit)break;
        }
        Console.WriteLine($"HASH_PROVENANCE_AUDIT sample={samples} "+
            $"filename-matches={nameMatches} file-content-matches={byteMatches}; "+
            "no evidence of a runtime texture-ID association from these hashes.");
        return 0;
    }
}
