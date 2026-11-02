using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using EsoTest;

/// <summary>
/// Bounded source-only batch validation of ORIGINAL ESO DDS candidate resources.
/// Reads one manifest and locally installed archives. No extracted pixels or
/// proprietary file bytes are stored in the repository.
/// It cannot establish an authoritative runtime mesh-to-texture association.
/// </summary>
internal static class EsoCreatureTextureIndexAudit
{
    private sealed record Resource(uint Id,int Width,int Height,uint FourCC,
        bool PackedNormal);
    private readonly record struct Entry(uint Mesh,uint Diffuse,uint Normal,
        uint Specular,string Evidence);
    internal static int Run(string[] args)
    {
        if(args.Length!=3)
        {
            Console.Error.WriteLine(
                "--audit-original-creature-texture-index <manifest> <oodle> <candidate-index.tsv>");
            return 2;
        }
        using var archive=new EsoArchive(args[0]);
        using var oodle=OodleDecoder.FromEnvironment(args[1])??
            throw new FileNotFoundException("Installed ESO Oodle decoder unavailable");
        var ids=new HashSet<uint>();
        var entries=new List<Entry>();
        foreach(string raw in File.ReadLines(args[2]))
        {
            if(raw.StartsWith('#')||string.IsNullOrWhiteSpace(raw))continue;
            var f=raw.Split('\t');
            if(f.Length!=5||!uint.TryParse(f[0],NumberStyles.HexNumber,
                   CultureInfo.InvariantCulture,out uint mesh)||
               !uint.TryParse(f[1],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint d)||
               !uint.TryParse(f[2],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint n)||
               !uint.TryParse(f[3],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint s)||
               mesh==0||d==0||!ids.Add(mesh))
                throw new InvalidDataException("Invalid/duplicate original ESO candidate mesh row");
            string evidence=f[4];
            if(evidence is not ("archive-neighbor-provisional" or
                "archive-triplet-provisional" or
                "archive-triplet-authoring-provisional"))
                throw new InvalidDataException("Provisional evidence required, no auto-verification");
            if(Math.Abs((long)mesh-d)>64)
                throw new InvalidDataException("DDS candidate is too far from source mesh");
            if(evidence is "archive-triplet-provisional" or
                "archive-triplet-authoring-provisional")
            {
                if(n!=d+2||s!=d+4)
                    throw new InvalidDataException(
                        "Source DDS candidate triplet is no longer consecutive");
            }
            entries.Add(new Entry(mesh,d,n,s,evidence));
            if(entries.Count>5000)
                throw new InvalidDataException("Excessive source texture catalogue");
        }
        var records=new Dictionary<uint,Resource>();
        foreach(uint id in entries.SelectMany(e=>new[]{e.Diffuse,e.Normal,e.Specular})
                    .Where(i=>i!=0).Distinct())
        {
            var record=archive.FindId(id).FirstOrDefault(r=>
                archive.GetIdIndexFields(r.Ordinal).Flags==0);
            if(record.UncompressedSize is <128 or >20_000_000)
                throw new InvalidDataException($"Original source DDS missing 0x{id:X8}");
            byte[] bytes=archive.ReadDecoded(record,oodle);
            if(bytes.Length<128||BitConverter.ToUInt32(bytes,0)!=0x20534444u)
                throw new InvalidDataException($"Not an original DDS file 0x{id:X8}");
            int width=BitConverter.ToInt32(bytes,16),
                height=BitConverter.ToInt32(bytes,12);
            uint codec=BitConverter.ToUInt32(bytes,84);
            if(width is <16 or >4096||height is <16 or >4096||
               codec is not (0x31545844u or 0x33545844u or 0x35545844u))
                throw new InvalidDataException($"Unsupported source DDS geometry 0x{id:X8}");
            float meanGreen=0,meanAlpha=0;
            bool sampled=codec==0x35545844u &&
                EsoDxt5NormalEvidence.TrySample(bytes,out meanGreen,out meanAlpha);
            records.Add(id,new Resource(id,width,height,codec,
                sampled&&EsoDxt5NormalEvidence.LooksPackedNormal(meanGreen,meanAlpha)));
        }
        int normals=0,specular=0,authoring=0,weakNeighborMismatches=0;
        foreach(var e in entries)
        {
            var diffuse=records[e.Diffuse];
            if(e.Normal!=0)
            {
                var normal=records[e.Normal];
                if(normal.Width!=diffuse.Width||normal.Height!=diffuse.Height||
                   !normal.PackedNormal)
                {
                    if(e.Evidence=="archive-neighbor-provisional")
                    {
                        weakNeighborMismatches++;
                        Console.WriteLine($"WARNING weak original material neighbour "+
                            $"0x{e.Mesh:X8}: normal is not a matching packed map");
                    }
                    else throw new InvalidDataException(
                        $"Candidate native packed-normal DDS invalid for mesh 0x{e.Mesh:X8}");
                }
                else normals++;
            }
            if(e.Specular!=0)
            {
                var spec=records[e.Specular];
                if(spec.Width!=diffuse.Width||spec.Height!=diffuse.Height)
                {
                    if(e.Evidence=="archive-neighbor-provisional")
                    {
                        weakNeighborMismatches++;
                        Console.WriteLine($"WARNING weak original material neighbour "+
                            $"0x{e.Mesh:X8}: specular dimensions mismatch");
                    }
                    else throw new InvalidDataException(
                        $"Candidate native specular DDS dimensions invalid for mesh 0x{e.Mesh:X8}");
                }
                else specular++;
            }
            if(e.Evidence=="archive-triplet-authoring-provisional")
            {
                if(diffuse.FourCC!=0x31545844u||
                   records[e.Normal].FourCC!=0x35545844u||
                   records[e.Specular].FourCC!=0x35545844u)
                    throw new InvalidDataException(
                        "Strong original ESO candidate DDS triplet codec mismatch");
                authoring++;
            }
        }
        Console.WriteLine($"PASS original ESO candidate index: {entries.Count} source mesh links "+
            $"({authoring} authoring+structural provisional), "+
            $"{normals} source normal maps, {specular} specular candidates, "+
            $"{records.Count} distinct genuine archived DDS files, "+
            $"{weakNeighborMismatches} flagged weak-neighbour mismatches.");
        Console.WriteLine("No authoritative ESO runtime material relationship was inferred.");
        return 0;
    }
}
