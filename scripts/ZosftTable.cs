// Direct parser for ESO's ZOSFT filename table (in-memory only).
// Layout informed by eso-tools (MIT, Aleksandr Zelenin).
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace EsoTest;

public static class ZosftTable
{
    public static Dictionary<uint, string> Parse(byte[] raw)
    {
        raw = SkipResourceWrapper(raw);
        using var stream = new MemoryStream(raw, false);
        using var reader = new BinaryReader(stream);
        if (Encoding.ASCII.GetString(reader.ReadBytes(5)) != "ZOSFT")
            throw new InvalidDataException("Filetable has no ZOSFT header");
        reader.ReadBytes(10);
        int count = checked((int)reader.ReadUInt32());

        SkipIndex(reader); // index 1
        var (ids, paths) = ParseNameIndex(reader); // index 2
        SkipIndex(reader); // index 3

        uint stringBytes = reader.ReadUInt32();
        if (stringBytes > raw.Length || stringBytes > stream.Length - stream.Position)
            throw new InvalidDataException("Bad ZOSFT name-string block");
        byte[] nameBytes = reader.ReadBytes(checked((int)stringBytes));
        var byOffset = new Dictionary<uint, string>();
        int start = 0;
        for (int i = 0; i < nameBytes.Length; i++)
        {
            if (nameBytes[i] != 0) continue;
            byOffset[(uint)start] = Encoding.UTF8.GetString(nameBytes, start, i - start);
            start = i + 1;
        }
        var result = new Dictionary<uint, string>();
        for (int i = 0; i < Math.Min(count, Math.Min(ids.Length, paths.Length)); i++)
        {
            if (byOffset.TryGetValue(paths[i], out string? name))
                result[ids[i]] = name;
        }
        return result;
    }

    private static (uint[] ids, uint[] nameOffsets) ParseNameIndex(BinaryReader br)
    {
        _ = br.ReadUInt16();  // index id
        _ = br.ReadUInt32();  // flags
        uint count1 = br.ReadUInt32();
        uint count2 = br.ReadUInt32();
        uint count3 = br.ReadUInt32();
        if (count1 > 0) _ = ReadBlock(br);
        byte[] block2 = count2 > 0 ? ReadBlock(br) : Array.Empty<byte>();
        byte[] block3 = count3 > 0 ? ReadBlock(br) : Array.Empty<byte>();
        if (count2 > 5_000_000 || count3 > 5_000_000 || block2.Length < count2 * 4 || block3.Length < count3 * 8)
            throw new InvalidDataException("ZOSFT index sizes invalid");
        int n = checked((int)Math.Min(count2, count3));
        int size2 = count2 > 0 ? block2.Length / (int)count2 : 0;
        int size3 = count3 > 0 ? block3.Length / (int)count3 : 0;
        uint[] ids = new uint[n], offsets = new uint[n];
        for (int i = 0; i < n; i++)
        {
            ids[i] = BinaryPrimitives.ReadUInt32LittleEndian(block2.AsSpan(i * size2));
            offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(block3.AsSpan(i * size3 + 4));
        }
        return (ids, offsets);
    }

    private static void SkipIndex(BinaryReader br)
    {
        _ = br.ReadUInt16();
        _ = br.ReadUInt32();
        uint c1 = br.ReadUInt32(), c2 = br.ReadUInt32(), c3 = br.ReadUInt32();
        if (c1 > 0) SkipBlock(br);
        if (c2 > 0) SkipBlock(br);
        if (c3 > 0) SkipBlock(br);
    }

    private static void SkipBlock(BinaryReader br)
    {
        uint unpacked = br.ReadUInt32();
        uint packed = br.ReadUInt32();
        if (unpacked > 300_000_000 || packed > 300_000_000)
            throw new InvalidDataException("Bad ZOSFT block size");
        br.BaseStream.Seek(packed, SeekOrigin.Current);
    }

    private static byte[] ReadBlock(BinaryReader br)
    {
        int unpacked = checked((int)br.ReadUInt32());
        int packed = checked((int)br.ReadUInt32());
        if (unpacked > 300_000_000 || packed > 300_000_000)
            throw new InvalidDataException("Bad ZOSFT block size");
        byte[] input = br.ReadBytes(packed);
        if (input.Length != packed) throw new EndOfStreamException();
        byte[] output = new byte[unpacked];
        using var z = new ZLibStream(new MemoryStream(input, false), CompressionMode.Decompress);
        z.ReadExactly(output);
        return output;
    }

    private static byte[] SkipResourceWrapper(byte[] raw)
    {
        if (raw.Length < 16 || raw[0] != 0) return raw;
        if (BinaryPrimitives.ReadUInt32BigEndian(raw) != 0) return raw;
        uint offset = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(4));
        long p = 8L + offset;
        if (p < 0 || p + 4 > raw.Length) return raw;
        uint second = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan((int)p));
        p += 4L + second;
        if (p < 0 || p + 5 > raw.Length) return raw;
        return raw.AsSpan((int)p).ToArray();
    }
}
