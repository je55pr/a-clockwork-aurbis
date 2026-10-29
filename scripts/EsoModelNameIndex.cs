using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace EsoTest;

/// <summary>
/// Validated, derived metadata only: original internal GR2 names/meshes/animations.
/// This index contains no game geometry or proprietary binaries. Rows are keyed
/// to exact source record location/size so a depot patch cannot silently reuse
/// stale names with the same resource ID.
/// </summary>
public static class EsoModelNameIndex
{
    public const string Header = "# eso-model-names-v1";
    public sealed record Record(
        uint Id, int Archive, long Offset, uint CompressedSize, uint UncompressedSize,
        string Status, string OriginalFilename, string ModelName,
        string MeshName, string AnimationNames, int ModelCount,
        int MeshCount, int AnimationCount);

    public static string DefaultPath => Path.Combine(
        Directory.GetCurrentDirectory(), ".research", "model-names.tsv");

    private static string Encode(string s) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
    private static string Decode(string s) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(s));

    public static string Serialize(Record item) =>
        string.Join("\t", item.Id.ToString("X8"), item.Archive,
            item.Offset, item.CompressedSize, item.UncompressedSize,
            item.Status, Encode(item.OriginalFilename),
            Encode(item.ModelName), Encode(item.MeshName),
            Encode(item.AnimationNames),
            item.ModelCount,item.MeshCount,item.AnimationCount);

    public static bool TryParse(string line,out Record? record)
    {
        record=null;
        try
        {
            var p=line.Split('\t');
            if(p.Length!=13||!uint.TryParse(p[0],NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,out uint id))
                return false;
            record=new Record(id,
                int.Parse(p[1],CultureInfo.InvariantCulture),
                long.Parse(p[2],CultureInfo.InvariantCulture),
                uint.Parse(p[3],CultureInfo.InvariantCulture),
                uint.Parse(p[4],CultureInfo.InvariantCulture),
                p[5],Decode(p[6]),Decode(p[7]),Decode(p[8]),
                Decode(p[9]),int.Parse(p[10],CultureInfo.InvariantCulture),
                int.Parse(p[11],CultureInfo.InvariantCulture),
                int.Parse(p[12],CultureInfo.InvariantCulture));
            return true;
        }
        catch(Exception) {return false;}
    }

    public static Dictionary<uint,Record> Load(string path)
    {
        var items=new Dictionary<uint,Record>();
        if(!File.Exists(path))return items;
        try
        {
            using var stream=new StreamReader(path);
            if(stream.ReadLine()!=Header)return items;
            while(stream.ReadLine() is { } line)
            {
                if(TryParse(line,out var record)&&record!=null)
                    items[record.Id]=record;
            }
        }
        catch(IOException){/* Partial or inaccessible index remains optional. */}
        catch(UnauthorizedAccessException){}
        return items;
    }

    public static bool MatchesSource(Record cached,
        int archive,long offset,uint compressed,uint uncompressed) =>
        cached.Archive==archive && cached.Offset==offset &&
        cached.CompressedSize==compressed && cached.UncompressedSize==uncompressed;

    public static string PreferredName(Record m)
    {
        string original=m.OriginalFilename.Trim();
        if(original.Length>1&&!IsNumeric(original))
            return original.Replace('\\','/').Split('/')[^1];
        string mesh=m.MeshName.TrimStart('|').Trim();
        string model=m.ModelName.TrimStart('|').Trim();
        return mesh.Length>0?mesh:model;
    }

    private static bool IsNumeric(string text)
    {
        foreach(char c in text)if(!char.IsAsciiDigit(c))return false;
        return text.Length>0;
    }
}
