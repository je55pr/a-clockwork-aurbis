using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace EsoTest;

/// <summary>
/// Read-only parser for the nested FAFAEBEB metadata container found inside an
/// ESO depot resource. This is *not* a known zone/world placement table.
/// The caller supplies already-decoded Oodle bytes; entries are zlib-inflated
/// in memory only, without writing an intermediate game-data file.
/// </summary>
public sealed class DepotMetadataContainer
{
    public const uint Signature = 0xFAFAEBEB;
    public readonly record struct Entry(
        int Ordinal, uint DeclaredSize, uint StoredSize,
        int CompressedOffset, uint CompressedSize);

    private readonly byte[] _data;
    private readonly Entry[] _entries;
    public uint FormatVersion { get; }
    public uint DeclaredCount { get; }
    public uint HeaderField { get; }
    public IReadOnlyList<Entry> Entries => _entries;
    public bool IsTruncated => _entries.Length < DeclaredCount;
    public int DecodedBytesConsumed { get; }

    private DepotMetadataContainer(byte[] data, uint version, uint count,
        uint headerField, Entry[] entries, int consumed)
    {
        _data = data;
        FormatVersion = version;
        DeclaredCount = count;
        HeaderField = headerField;
        _entries = entries;
        DecodedBytesConsumed = consumed;
    }

    public static DepotMetadataContainer Parse(byte[] decodedPrefix,
        int availableBytes, bool permitTruncated = false)
    {
        if (availableBytes < 16 || availableBytes > decodedPrefix.Length)
            throw new InvalidDataException("Invalid decoded metadata prefix length");

        ReadOnlySpan<byte> data = decodedPrefix.AsSpan(0, availableBytes);
        uint signature = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (signature != Signature)
            throw new InvalidDataException($"Unexpected metadata signature 0x{signature:X8}");

        uint version = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4));
        uint count = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8));
        uint reserved = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(12));
        if (version != 23)
            throw new NotSupportedException($"Unrecognised metadata version {version}");
        if (count > 2_000_000)
            throw new InvalidDataException($"Unreasonable metadata entry count {count}");

        var entries = new List<Entry>(checked((int)count));
        int offset = 16;
        while (entries.Count < count)
        {
            if (offset + 12 > availableBytes)
                break;

            uint unpacked = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset));
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 4));
            uint compressed = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 8));
            long end = (long)offset + 12 + compressed;
            if (unpacked == 0 || unpacked > 10_000_000 || compressed < 2 ||
                compressed > 10_000_000)
                throw new InvalidDataException(
                    $"Implausible metadata sizes at entry {entries.Count}");
            if (end > availableBytes)
                break;

            int payloadOffset = offset + 12;
            if (data[payloadOffset] != 0x78)
                throw new InvalidDataException(
                    $"Expected zlib stream at metadata entry {entries.Count}");
            entries.Add(new Entry(entries.Count, unpacked, stored,
                payloadOffset, compressed));
            offset = checked((int)end);
        }

        if (!permitTruncated && entries.Count != count)
            throw new InvalidDataException(
                $"Metadata container is incomplete: {entries.Count}/{count} records");
        return new DepotMetadataContainer(decodedPrefix, version, count,
            reserved, entries.ToArray(), offset);
    }

    public byte[] ReadEntry(int ordinal)
    {
        if ((uint)ordinal >= (uint)_entries.Length)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        Entry e = _entries[ordinal];
        using var input = new MemoryStream(_data, e.CompressedOffset,
            checked((int)e.CompressedSize), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        byte[] output = new byte[checked((int)e.DeclaredSize)];
        zlib.ReadExactly(output);
        if (zlib.ReadByte() != -1)
            throw new InvalidDataException($"Entry {ordinal} inflated beyond declared size");
        return output;
    }
}
