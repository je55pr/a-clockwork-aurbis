using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using EsoTest;

/// <summary>
/// ESO's animsfiletable.dat contains animation resource references, not
/// directly playable motion tracks. Read only, source-only index output;
/// GR2 identity/clip metadata must still be inspected per referenced ID.
/// </summary>
internal static class AnftFileTable
{
    public static int Run(string[] args)
    {
        if(args.Length<3)
        {
            Console.Error.WriteLine("Usage: --index-anft <manifest> <oodle-dll> <output-tsv>");
            return 2;
        }
        using var archive=new EsoArchive(args[0]);
        using var decoder=OodleDecoder.FromEnvironment(args[1])
            ??throw new FileNotFoundException("Oodle decoder unavailable");
        var record=archive.FindId(0x01000000u)
            .FirstOrDefault(r=>archive.GetIdIndexFields(r.Ordinal).Flags==0);
        if(record.UncompressedSize is <16 or >100_000_000)
            throw new InvalidDataException("Installed depot has no ANFT filetable resource");
        byte[] bytes=archive.ReadDecoded(record,decoder);
        if(bytes.Length!=record.UncompressedSize)
            throw new InvalidDataException("ANFT decoded size differs from source record");
        if(Encoding.ASCII.GetString(bytes,0,4)!="ANFT" ||
            Encoding.ASCII.GetString(bytes,bytes.Length-4,4)!="ANFT" ||
            bytes[4]!=1)
            throw new InvalidDataException("Unexpected ANFT signature or version");
        uint count=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(5,4));
        if(count>2_000_000 || (long)count*16+13!=bytes.Length)
            throw new InvalidDataException($"Unexpected ANFT row count {count} / source length {bytes.Length}");
        string output=Path.GetFullPath(args[2]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var unique=new System.Collections.Generic.HashSet<uint>();
        using(var writer=new StreamWriter(output,false,new UTF8Encoding(false)))
        {
            writer.WriteLine("# Source ANFT animation reference records; no extracted animation data.");
            writer.WriteLine("field1\tfield2\tid\tfield4");
            for(int i=0;i<count;i++)
            {
                int at=9+i*16;
                uint a=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at,4));
                uint b=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at+4,4));
                uint id=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at+8,4));
                uint d=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at+12,4));
                unique.Add(id);
                writer.WriteLine($"{a:X8}\t{b:X8}\t{id:X8}\t{d:X8}");
            }
        }
        Console.WriteLine($"ANFT source rows={count:N0}, unique referenced IDs={unique.Count:N0}, " +
            $"archive={record.ArchiveIndex}, output={output}");
        Console.WriteLine($"First 8 source IDs: {string.Join(",",unique.Take(8).Select(i=>i.ToString("X8")))}");
        return 0;
    }
}
