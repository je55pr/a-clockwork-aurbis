// Runtime reader for ESO's MES2/PES2 data. Reads the original install, without extracting assets.
// Format interpreted from eso-tools (MIT, Aleksandr Zelenin), see THIRD_PARTY.md.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace EsoTest;

public readonly record struct EsoRecord(
    int Ordinal, uint FileId, uint UncompressedSize, uint CompressedSize,
    uint Hash, uint Offset, ushort ArchiveIndex, ushort Compression);

public sealed class EsoArchive : IDisposable
{
    private readonly string _basePath;
    private readonly ushort[] _archiveIds;
    private readonly byte[] _idRows;       // 8-byte index records
    private readonly byte[] _locationRows; // 20-byte data records
    public int Count => _locationRows.Length / 20;
    public int ArchiveCount => _archiveIds.Length;
    public string SourcePath { get; }

    public EsoArchive(string manifestPath)
    {
        SourcePath = Path.GetFullPath(manifestPath);
        _basePath = Path.Combine(Path.GetDirectoryName(SourcePath)!,
            Path.GetFileNameWithoutExtension(SourcePath));
        using var stream = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        if (System.Text.Encoding.ASCII.GetString(Read(reader, 4)) != "MES2")
            throw new InvalidDataException("Not an MES2 manifest");
        ushort version = reader.ReadUInt16();
        if (version != 3) throw new NotSupportedException($"MES2 version {version}");
        ushort archiveCount = reader.ReadUInt16();
        _archiveIds = new ushort[archiveCount];
        for (int i = 0; i < archiveCount; i++)
            _archiveIds[i] = reader.ReadUInt16();
        _ = reader.ReadUInt32(); // field5
        _ = reader.ReadUInt32(); // data size

        ushort indexId = ReadBigUInt16(reader);
        if (indexId == 0)
        {
            Read(reader, 2);
            uint size1 = ReadBigUInt32(reader);
            stream.Seek(size1, SeekOrigin.Current);
            uint size2 = ReadBigUInt32(reader);
            stream.Seek(size2, SeekOrigin.Current);
            indexId = ReadBigUInt16(reader);
        }
        if (indexId != 3)
            throw new NotSupportedException($"Unexpected index kind {indexId}");

        Read(reader, 4); // index flags
        uint count1 = ReadBigUInt32(reader);
        uint count2 = ReadBigUInt32(reader);
        uint count3 = ReadBigUInt32(reader);

        // Block 1 is a lookup table not needed for sequential record access.
        SkipCompressed(reader);
        _idRows = ReadCompressed(reader, maxBytes: 256_000_000);
        _locationRows = ReadCompressed(reader, maxBytes: 256_000_000);
        if (_idRows.Length % 8 != 0 || _locationRows.Length % 20 != 0
            || _idRows.Length / 8 != _locationRows.Length / 20
            || _locationRows.Length / 20 != count3)
            throw new InvalidDataException("MNF index row sizes do not agree");
        _ = count1;
        _ = count2;
    }

    public EsoRecord GetRecord(int ordinal)
    {
        if ((uint)ordinal >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(ordinal));
        var id = _idRows.AsSpan(ordinal * 8, 8);
        var loc = _locationRows.AsSpan(ordinal * 20, 20);
        return new EsoRecord(
            ordinal,
            BinaryPrimitives.ReadUInt32LittleEndian(id),
            BinaryPrimitives.ReadUInt32LittleEndian(loc),
            BinaryPrimitives.ReadUInt32LittleEndian(loc[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(loc[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(loc[12..]),
            BinaryPrimitives.ReadUInt16LittleEndian(loc[16..]),
            BinaryPrimitives.ReadUInt16LittleEndian(loc[18..])
        );
    }

    /// <summary>
    /// Preserve the two previously-unexposed 16-bit fields in the manifest's
    /// 8-byte ID-index record. Their semantics have not yet been verified.
    /// They may be useful when recovering parent-resource relationships.
    /// </summary>
    public (ushort Field2, ushort Flags) GetIdIndexFields(int ordinal)
    {
        if ((uint)ordinal >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        var row = _idRows.AsSpan(ordinal * 8, 8);
        return (
            BinaryPrimitives.ReadUInt16LittleEndian(row[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(row[6..]));
    }

    public IEnumerable<EsoRecord> FindId(uint fileId)
    {
        for (int i = 0; i < Count; i++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(_idRows.AsSpan(i * 8)) == fileId)
                yield return GetRecord(i);
    }

    public IEnumerable<EsoRecord> FindByCompression(ushort compression, int limit)
    {
        int found = 0;
        for (int i = 0; i < Count && found < limit; i++)
            if (BinaryPrimitives.ReadUInt16LittleEndian(_locationRows.AsSpan(i * 20 + 18)) == compression)
            {
                found++;
                yield return GetRecord(i);
            }
    }

    public byte[] ReadRaw(EsoRecord record)
    {
        if (record.ArchiveIndex >= ArchiveCount)
            throw new InvalidDataException("Archive index outside manifest");
        if (record.CompressedSize > 512_000_000)
            throw new InvalidDataException("Resource exceeds 512MB safety limit");
        string path = $"{_basePath}{_archiveIds[record.ArchiveIndex]:D4}.dat";
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if ((ulong)record.Offset + record.CompressedSize > (ulong)file.Length)
            throw new InvalidDataException("Resource extends beyond DAT file");
        file.Position = record.Offset;
        var bytes = new byte[checked((int)record.CompressedSize)];
        file.ReadExactly(bytes);
        return bytes;
    }

    public byte[] ReadDecoded(EsoRecord record, OodleDecoder? oodle)
    {
        byte[] source = ReadRaw(record);
        byte[] decompressed = record.Compression switch
        {
            0 => source,
            1 => Zlib(source, checked((int)record.UncompressedSize)),
            4 or 8 when oodle != null => oodle.Decompress(source, checked((int)record.UncompressedSize)),
            4 or 8 => throw new NotSupportedException("Oodle decoder unavailable; configure ESO_OODLE_DLL"),
            _ => throw new NotSupportedException($"Unknown compression {record.Compression}")
        };
        if (decompressed.Length != record.UncompressedSize)
            throw new InvalidDataException("Decoded resource size mismatch");
        // Some sub-files have an optional 3-section wrapper. Preserve bytes as-is;
        // the payload-specific decoder is responsible for unwrapping this.
        return decompressed;
    }

    public void Dispose() { }

    private static byte[] Read(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new EndOfStreamException();
        return bytes;
    }

    private static ushort ReadBigUInt16(BinaryReader reader) =>
        BinaryPrimitives.ReadUInt16BigEndian(Read(reader, 2));
    private static uint ReadBigUInt32(BinaryReader reader) =>
        BinaryPrimitives.ReadUInt32BigEndian(Read(reader, 4));

    private static void SkipCompressed(BinaryReader reader)
    {
        uint unpacked = ReadBigUInt32(reader);
        uint packed = ReadBigUInt32(reader);
        if (unpacked > 256_000_000 || packed > 256_000_000)
            throw new InvalidDataException("Suspicious MNF block size");
        reader.BaseStream.Seek(packed, SeekOrigin.Current);
    }

    private static byte[] ReadCompressed(BinaryReader reader, int maxBytes)
    {
        uint unpacked = ReadBigUInt32(reader);
        uint packed = ReadBigUInt32(reader);
        if (unpacked > maxBytes || packed > maxBytes)
            throw new InvalidDataException("Suspicious MNF block size");
        byte[] source = Read(reader, checked((int)packed));
        return Zlib(source, checked((int)unpacked));
    }

    private static byte[] Zlib(byte[] source, int outputSize)
    {
        using var compressed = new MemoryStream(source, false);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        var output = new byte[outputSize];
        zlib.ReadExactly(output);
        if (zlib.ReadByte() != -1) throw new InvalidDataException("Zlib block exceeds declared size");
        return output;
    }
}
