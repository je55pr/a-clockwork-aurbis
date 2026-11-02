using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using EsoTest;

/// <summary>
/// Discover strictly structural DDS diffuse/normal/specular triplets next to
/// ORIGINAL ESO character/creature meshes. An index proximity pattern does
/// NOT prove the runtime material assignment. Never auto-populates materials.
/// All output remains a local, ignored source-ID-only research catalogue.
/// </summary>
internal static class EsoCreatureTextureTripletSurvey
{
    private readonly record struct DdsInfo(int Width,int Height,uint FourCc,ushort Archive,
        bool PackedNormal,float MeanGreen,float MeanAlpha);
    private sealed record SourceMesh(uint Id,string Name);
    private sealed record Candidate(SourceMesh Mesh,uint Diffuse,uint Normal,uint Specular,
        int DdsArchive,int Distance);

    private static readonly HashSet<uint> Bc1Sizes=new()
    {
        43704,87528,174904,349672,699192,1398248,2796344
    };
    private static bool IsPairedSize(uint diffuse,uint normal)=>
        diffuse>128 && normal==2*(diffuse-128)+128;
    private static string DecodeName(string base64)
    {
        byte[] raw=Convert.FromBase64String(base64);
        if(raw.Length is <2 or >512)throw new InvalidDataException("Original source name out of bounds");
        string name=Encoding.UTF8.GetString(raw);
        if(name.Any(char.IsControl))throw new InvalidDataException("Original source name contains controls");
        return name.Replace('\t',' ');
    }
    private static bool SourceDdsInfo(EsoArchive archive,OodleDecoder oodle,EsoRecord record,
        Dictionary<uint,DdsInfo?> cache,out DdsInfo result)
    {
        if(cache.TryGetValue(record.FileId,out var cached))
        {
            result=cached??default;
            return cached!=null;
        }
        result=default;
        try
        {
            byte[] payload=archive.ReadDecoded(record,oodle);
            if(payload.Length<128||BitConverter.ToUInt32(payload,0)!=0x20534444u)
            {cache[record.FileId]=null;return false;}
            int width=BitConverter.ToInt32(payload,16);
            int height=BitConverter.ToInt32(payload,12);
            uint fourcc=BitConverter.ToUInt32(payload,84);
            if(width is <16 or >4096 ||height is <16 or >4096 ||
               fourcc is not (0x31545844u or 0x35545844u))
            {cache[record.FileId]=null;return false;}
            float meanGreen=0,meanAlpha=0;
            bool sampled=fourcc==0x35545844u &&
                EsoDxt5NormalEvidence.TrySample(payload,out meanGreen,out meanAlpha);
            result=new DdsInfo(width,height,fourcc,record.ArchiveIndex,
                sampled&&EsoDxt5NormalEvidence.LooksPackedNormal(meanGreen,meanAlpha),
                sampled?meanGreen:0,sampled?meanAlpha:0);
            cache[record.FileId]=result;
            return true;
        }
        catch(Exception e)when(e is IOException or InvalidDataException or NotSupportedException or OverflowException)
        {cache[record.FileId]=null;return false;}
    }

    internal static int Run(string[] args)
    {
        if(args.Length is <4 or >6)
        {
            Console.Error.WriteLine(
                "Usage: --survey-original-creature-texture-triplets "+
                "<manifest> <oodle> <unlinked-model-names.tsv> <ignored-output.tsv> "+
                "[id-radius=24] [max-meshes=20000]");
            return 2;
        }
        int radius=args.Length>=5?int.Parse(args[4],CultureInfo.InvariantCulture):24;
        int maxMeshes=args.Length>=6?int.Parse(args[5],CultureInfo.InvariantCulture):20000;
        if(radius is <6 or >128||maxMeshes is <1 or >50000)
            throw new ArgumentOutOfRangeException("Archive-neighbor search limits");
        string output=Path.GetFullPath(args[3]);
        string root=Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,".research"));
        if(!output.StartsWith(Path.TrimEndingDirectorySeparator(root)+Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)||
           !output.EndsWith(".tsv",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Original source research output must stay in ignored .research/*.tsv");
        var meshes=new List<SourceMesh>();
        var meshIds=new HashSet<uint>();
        foreach(string row in File.ReadLines(args[2]))
        {
            if(row.StartsWith('#'))continue;
            string[] f=row.Split('\t');
            if(f.Length<9||f[5]!="OK"||
               !uint.TryParse(f[0],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint meshId))
                continue;
            try
            {
                if(DecodeName(f[7])!="Bip01")continue;
                string name=DecodeName(f[8]);
                if(meshIds.Add(meshId))meshes.Add(new SourceMesh(meshId,name));
            }
            catch(Exception ex) when(ex is FormatException or InvalidDataException){}
            if(meshes.Count>=maxMeshes)break;
        }
        using var archive=new EsoArchive(args[0]);
        using var oodle=OodleDecoder.FromEnvironment(args[1])??
            throw new FileNotFoundException("Original ESO Oodle decoder unavailable");
        var byId=new Dictionary<uint,EsoRecord>();
        for(int i=0;i<archive.Count;i++)
        {
            if(archive.GetIdIndexFields(i).Flags!=0)continue;
            var r=archive.GetRecord(i);
            if(r.Compression!=8 || r.UncompressedSize>8_000_000)continue;
            if(Bc1Sizes.Contains(r.UncompressedSize)||
               Bc1Sizes.Any(x=>IsPairedSize(x,r.UncompressedSize)))
                byId.TryAdd(r.FileId,r);
        }
        var decoded=new Dictionary<uint,DdsInfo?>();
        var results=new List<Candidate>();
        int withCandidate=0,ambiguous=0,rejectedNonNormals=0;
        foreach(var source in meshes)
        {
            var matches=new List<Candidate>();
            uint first=source.Id>(uint)radius?source.Id-(uint)radius:0;
            uint last=source.Id+(uint)radius;
            for(uint diffuse=first;diffuse+4<=last;diffuse++)
            {
                if(diffuse%2!=1 || !byId.TryGetValue(diffuse,out var d) ||
                   !Bc1Sizes.Contains(d.UncompressedSize) ||
                   !byId.TryGetValue(diffuse+2,out var n) ||
                   !byId.TryGetValue(diffuse+4,out var s) ||
                   !IsPairedSize(d.UncompressedSize,n.UncompressedSize) ||
                   n.UncompressedSize!=s.UncompressedSize ||
                   d.ArchiveIndex!=n.ArchiveIndex ||
                   d.ArchiveIndex!=s.ArchiveIndex)
                    continue;
                if(!SourceDdsInfo(archive,oodle,d,decoded,out var di) ||
                   !SourceDdsInfo(archive,oodle,n,decoded,out var ni) ||
                   !SourceDdsInfo(archive,oodle,s,decoded,out var si) ||
                   di.FourCc!=0x31545844u ||
                   ni.FourCc!=0x35545844u ||
                   si.FourCc!=0x35545844u ||
                   di.Width!=ni.Width ||di.Height!=ni.Height ||
                   di.Width!=si.Width ||di.Height!=si.Height)
                    continue;
                if(!ni.PackedNormal)
                {
                    rejectedNonNormals++;
                    continue;
                }
                int distance=Math.Abs((int)((long)diffuse-(long)source.Id));
                matches.Add(new Candidate(source,diffuse,diffuse+2,diffuse+4,
                    d.ArchiveIndex,distance));
            }
            if(matches.Count==0)continue;
            withCandidate++;
            if(matches.Count>1)ambiguous++;
            results.AddRange(matches.OrderBy(x=>x.Distance));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using(var writer=new StreamWriter(output,false,new UTF8Encoding(false)))
        {
            writer.WriteLine("# Original ESO source DDS archive-index triplets. NOT proven runtime mesh/material bindings.");
            writer.WriteLine("mesh-id\tmesh-name\tdiffuse-id\tnormal-id\tspecular-id\tarchive\tid-distance\tcandidate-count\tpacked-normal-means\tevidence");
            foreach(var group in results.GroupBy(x=>x.Mesh.Id).OrderBy(x=>x.Key))
                foreach(var x in group.OrderBy(x=>x.Distance))
                    writer.WriteLine($"{x.Mesh.Id:X8}\t{x.Mesh.Name}\t"+
                        $"{x.Diffuse:X8}\t{x.Normal:X8}\t{x.Specular:X8}\t"+
                        $"{x.DdsArchive}\t{x.Distance}\t{group.Count()}\t"+
                        $"{decoded[x.Normal]!.Value.MeanGreen:F3},{decoded[x.Normal]!.Value.MeanAlpha:F3}\t"+
                        "archive-triplet-structural-provisional");
        }
        Console.WriteLine($"ORIGINAL_CREATURE_DDS_SURVEY candidates={results.Count} "+
            $"source-meshes={meshes.Count} models-with-candidates={withCandidate} "+
            $"ambiguous-models={ambiguous} rejected-non-normal-triplets={rejectedNonNormals} "+
            $"unique-source-DDS-inspected={decoded.Count}");
        foreach(var x in results.Take(16))
            Console.WriteLine($"  {x.Mesh.Id:X8} {x.Mesh.Name} -> "+
                $"D={x.Diffuse:X8} N={x.Normal:X8} S={x.Specular:X8} "+
                $"distance={x.Distance} (STRUCTURAL ONLY)");
        Console.WriteLine($"Ignored source-only candidate report: {output}");
        return 0;
    }
}
