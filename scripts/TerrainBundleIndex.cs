using System;
using System.Collections.Generic;
using System.Linq;

namespace EsoTest;

/// <summary>
/// A 583-byte terrain descriptor owns its heightfields through its 16-bit
/// MES2 group key: (descriptorID * 32) | 1. A region may contain hundreds
/// or thousands of 100-unit tile records, not merely a 5x5 preview.
/// </summary>
public sealed class TerrainBundle
{
    public int DescriptorOrdinal { get; init; }
    public ushort ArchiveIndex { get; init; }
    public uint DescriptorId { get; init; }
    public int OriginX { get; init; }
    public int OriginY { get; init; }
    public int MaximumX { get; init; }
    public int MaximumY { get; init; }
    public List<EsoRecord> Tiles { get; init; } = new();

    public string Description =>
        $"DAT {ArchiveIndex:D4} / descriptor {DescriptorId} / " +
        $"tile coordinates ({OriginX},{OriginY}) to ({MaximumX},{MaximumY}) / {Tiles.Count} tiles";
}

public static class TerrainBundleIndex
{
    public static List<TerrainBundle> Discover(EsoArchive archive)
    {
        var result = new List<TerrainBundle>();
        for (int i = 0; i < archive.Count; i++)
        {
            var descriptor = archive.GetRecord(i);
            var tag = archive.GetIdIndexFields(i);
            if (descriptor.UncompressedSize != 583 || descriptor.Compression != 4 ||
                tag.Field2 != 0 || tag.Flags != 0x4400)
                continue;

            uint heightKey = descriptor.FileId * 32u + 1u;
            var tiles = new List<EsoRecord>();
            // Follow all records in the parent descriptor's archive, up to the
            // next descriptor. Previously we stopped at 25; that discarded
            // most of many zones and produced false overlapping terrains.
            for (int j = i + 1; j < archive.Count; j++)
            {
                var record = archive.GetRecord(j);
                var child = archive.GetIdIndexFields(j);
                if (record.ArchiveIndex != descriptor.ArchiveIndex ||
                    (record.UncompressedSize == 583 &&
                     child.Flags == 0x4400 && child.Field2 == 0))
                    break;

                if (record.Compression != 4 ||
                    record.UncompressedSize is < 410000 or > 430000 ||
                    child.Flags != 0x4000 || child.Field2 != heightKey)
                    continue;

                int x = (int)(record.FileId >> 16);
                int z = (int)(record.FileId & 0xFFFF);
                if (x >= 1024 || z >= 1024) continue;
                tiles.Add(record);
            }
            if (tiles.Count < 8) continue;

            result.Add(new TerrainBundle
            {
                DescriptorOrdinal = descriptor.Ordinal,
                ArchiveIndex = descriptor.ArchiveIndex,
                DescriptorId = descriptor.FileId,
                OriginX = tiles.Min(r => (int)(r.FileId >> 16)),
                OriginY = tiles.Min(r => (int)(r.FileId & 0xFFFF)),
                MaximumX = tiles.Max(r => (int)(r.FileId >> 16)),
                MaximumY = tiles.Max(r => (int)(r.FileId & 0xFFFF)),
                Tiles = tiles
            });
        }
        return result;
    }
}
