using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using EsoTest;

/// <summary>
/// Read-only inspection of the original ESO filename table (ZOSFT).
/// The table maps content IDs to source paths, including original .dds files.
/// This does not extract any textures or embed proprietary data in the repo.
/// The ID/offset mapping is cross-checked against the current MNF at runtime.
/// </summary>
internal static class ZosftFileTable
{
    private const int MaxEntries=2_000_000;
    private const int MaxBlockBytes=160_000_000;
    private sealed record Index(uint Count1,uint Count2,uint Count3,
        byte[] Block2,byte[] Block3);

    private static Index ReadIndex(BinaryReader reader,bool keepNames)
    {
        ushort index=reader.ReadUInt16();
        uint field=reader.ReadUInt32();
        uint c1=reader.ReadUInt32(),c2=reader.ReadUInt32(),
            c3=reader.ReadUInt32();
        if(c1>MaxEntries||c2>MaxEntries||c3>MaxEntries)
            throw new InvalidDataException("Unreasonable ZOSFT index counts");
        byte[] block2=Array.Empty<byte>(),block3=Array.Empty<byte>();
        var counts=new[]{c1,c2,c3};
        for(int i=0;i<3;i++)
        {
            if(counts[i]==0)continue;
            int decoded=checked((int)reader.ReadUInt32());
            int packed=checked((int)reader.ReadUInt32());
            if(decoded<1||decoded>MaxBlockBytes||packed<1||packed>MaxBlockBytes)
                throw new InvalidDataException("Bad ZOSFT block lengths");
            if(!keepNames||i==0)
            {
                reader.BaseStream.Seek(packed,SeekOrigin.Current);
                continue;
            }
            var encoded=reader.ReadBytes(packed);
            if(encoded.Length!=packed)
                throw new EndOfStreamException("Truncated ZOSFT zlib block");
            using var input=new MemoryStream(encoded);
            using var zip=new ZLibStream(input,CompressionMode.Decompress);
            byte[] output=new byte[decoded];
            zip.ReadExactly(output);
            if(i==1)block2=output;else block3=output;
        }
        _=index;_ =field;
        return new Index(c1,c2,c3,block2,block3);
    }

    public static int Run(string[] args)
    {
        if(args.Length<3)
        {
            Console.Error.WriteLine("Usage: --index-zosft <manifest> <oodle-dll> <output-tsv>");
            return 2;
        }
        using var archive=new EsoArchive(args[0]);
        using var decoder=OodleDecoder.FromEnvironment(args[1])
            ??throw new FileNotFoundException("Oodle decoder unavailable");
        var record=archive.FindId(0x00FFFFFFu)
            .FirstOrDefault(x=>archive.GetIdIndexFields(x.Ordinal).Flags==0);
        if(record.UncompressedSize is <256 or >60_000_000)
            throw new InvalidDataException("No valid ZOSFT source filetable record");
        byte[] data=archive.ReadDecoded(record,decoder);
        using var memory=new MemoryStream(data);
        using var reader=new BinaryReader(memory,Encoding.UTF8);
        if(Encoding.ASCII.GetString(reader.ReadBytes(5))!="ZOSFT")
            throw new InvalidDataException("Not a ZOSFT archive filename table");
        reader.ReadBytes(10);
        uint total=reader.ReadUInt32();
        _=ReadIndex(reader,false);
        var named=ReadIndex(reader,true);
        _=ReadIndex(reader,false);
        int size=checked((int)reader.ReadUInt32());
        if(size<1||size>MaxBlockBytes||
            memory.Length-memory.Position<size+5)
            throw new InvalidDataException("ZOSFT string table invalid length");
        byte[] strings=reader.ReadBytes(size);
        if(Encoding.ASCII.GetString(reader.ReadBytes(5))!="ZOSFT")
            throw new InvalidDataException("No ZOSFT closing signature");
        if(named.Count2!=total||named.Count3!=total||
            named.Block2.Length!=total*4||named.Block3.Length!=total*16)
            throw new InvalidDataException("ZOSFT ID/offset table shape mismatch");
        var offsetNames=new Dictionary<uint,string>();
        for(int i=0,start=0;i<strings.Length;i++)
        {
            if(strings[i]!=0)continue;
            if(i>start)
            {
                string path=Encoding.UTF8.GetString(strings,start,i-start);
                offsetNames[(uint)start]=path;
            }
            start=i+1;
        }
        string output=Path.GetFullPath(args[2]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        int namedCount=0,dds=0,characters=0;
        var seen=new HashSet<uint>();
        using(var writer=new StreamWriter(output,false,new UTF8Encoding(false)))
        {
            writer.WriteLine("# ESO local source resource paths. Source-only index; no extracted textures.");
            writer.WriteLine("id\tpath");
            for(int i=0;i<(int)total;i++)
            {
                uint id=BinaryPrimitives.ReadUInt32LittleEndian(
                    named.Block2.AsSpan(i*4,4));
                uint offset=BinaryPrimitives.ReadUInt32LittleEndian(
                    named.Block3.AsSpan(i*16+4,4));
                if(!offsetNames.TryGetValue(offset,out var path) ||
                    path.Length is <2 or >700 || !seen.Add(id))
                    continue;
                namedCount++;
                if(path.EndsWith(".dds",StringComparison.OrdinalIgnoreCase))dds++;
                if(path.Contains("/character",StringComparison.OrdinalIgnoreCase)||
                   path.Contains("/armor",StringComparison.OrdinalIgnoreCase)||
                   path.Contains("/skin",StringComparison.OrdinalIgnoreCase))
                    characters++;
                writer.Write(id.ToString("X8"));
                writer.Write('\t');writer.WriteLine(path.Replace('\t',' '));
            }
        }
        Console.WriteLine($"ZOSFT: {total:N0} source filename records; " +
            $"{namedCount:N0} named IDs, {dds:N0} DDS paths, " +
            $"{characters:N0} character/armour/skin path candidates.");
        Console.WriteLine($"Source-only filename index: {output}");
        return 0;
    }
}
