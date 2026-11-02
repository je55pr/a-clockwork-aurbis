using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using EsoTest;

/// <summary>
/// Small read-only probe of ESO local manifest record IDs around a model.
/// Content IDs are not asserted to encode a texture relationship. This is
/// exploratory metadata; NO guessed texture gets used by a renderer.
/// </summary>
internal static class EsoResourceNeighbors
{
    public static int Run(string[] args)
    {
        if(args.Length<3)
        {
            Console.Error.WriteLine(
                "Usage: --audit-resource-neighbors <manifest> <oodle> <hex-id> [radius]");
            return 2;
        }
        uint id=uint.Parse(args[2],NumberStyles.HexNumber,CultureInfo.InvariantCulture);
        int radius=args.Length>3?int.Parse(args[3],CultureInfo.InvariantCulture):48;
        if(radius is <1 or >500)throw new ArgumentOutOfRangeException(nameof(radius));
        using var archive=new EsoArchive(args[0]);
        using var oodle=OodleDecoder.FromEnvironment(args[1])
            ??throw new FileNotFoundException("ESO Oodle decoder unavailable");
        uint start=id>=radius?id-(uint)radius:0,end=id+(uint)radius;
        var records=Enumerable.Range(0,archive.Count)
            .Select(i=>archive.GetRecord(i))
            .Where(r=>r.FileId>=start&&r.FileId<=end&&
                archive.GetIdIndexFields(r.Ordinal).Flags==0)
            .OrderBy(r=>r.FileId).ToArray();
        Console.WriteLine($"Neighbor IDs around 0x{id:X8} radius={radius} "+
            $"original-index-records={records.Length}");
        foreach(var r in records)
        {
            string magic="?";
            if(r.UncompressedSize is >=4 and <=8_000_000)
            {
                try
                {
                    byte[] data=archive.ReadDecoded(r,oodle);
                    magic=Encoding.ASCII.GetString(data.AsSpan(0,Math.Min(4,data.Length)));
                    magic=string.Concat(magic.Select(c=>char.IsControl(c)?'.':c));
                    if(data.Length>=4&&data[0]==0xE5&&data[1]==0x9B)
                        magic="GR2";
                    if(data.Length>=4&&data[0]==0xB8&&data[1]==0x67)
                        magic="GR2";
                    if(data.Length>=4&&data[0]==0x44&&data[1]==0x44&&data[2]==0x53&&data[3]==0x20)
                        magic="DDS";
                }
                catch(Exception e)when(e is InvalidDataException or NotSupportedException or IOException)
                {
                    magic=e.GetType().Name;
                }
            }
            Console.WriteLine($"{r.FileId:X8} {(r.FileId==id?"TARGET":"      ")} "+
                $"archive={r.ArchiveIndex:D3} compression={r.Compression} "+
                $"size={r.UncompressedSize} signature={magic}");
        }
        return 0;
    }
}
