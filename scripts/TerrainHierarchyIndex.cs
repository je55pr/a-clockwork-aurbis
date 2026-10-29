using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace EsoTest;

/// <summary>
/// Manifest-observed asset hierarchy, NOT a decoded ESO world/zone hierarchy.
/// Stores only resource identity metadata (hash, dimensions, manifest ordinals).
/// No copied or converted game content is stored.
/// </summary>
public sealed class TerrainHierarchyIndex
{
    public sealed record ArchiveNode(ushort ArchiveIndex, IReadOnlyList<TerrainBundle> Sectors);
    public IReadOnlyList<ArchiveNode> Archives { get; }
    public int TotalSectors { get; }
    public int FullSectors { get; }
    public int DistinctCompleteHeightLayouts { get; }
    public int ReusedSectionCount { get; }
    private readonly IReadOnlyDictionary<uint, IReadOnlyList<uint>> _sameHeights;

    private TerrainHierarchyIndex(
        IReadOnlyList<ArchiveNode> archives, int fullSectors, int unique, int reused,
        IReadOnlyDictionary<uint,IReadOnlyList<uint>> equalGroups)
    {
        Archives=archives;
        TotalSectors=archives.Sum(a=>a.Sectors.Count);
        FullSectors=fullSectors;
        DistinctCompleteHeightLayouts=unique;
        ReusedSectionCount=reused;
        _sameHeights=equalGroups;
    }

    public static TerrainHierarchyIndex Analyse(IReadOnlyList<TerrainBundle> sectors)
    {
        var archives=sectors.GroupBy(s=>s.ArchiveIndex)
            .OrderBy(g=>g.Key)
            .Select(g=>new ArchiveNode(g.Key,g.OrderBy(s=>s.DescriptorId).ToArray()))
            .ToArray();
        // Complete *observed rectangular coverage*, not necessarily the entire
        // worldspace: source records may omit unused border tiles.
        var fullyPopulated=sectors.Where(s=>s.Tiles.Count ==
            (long)(s.MaximumX - s.OriginX + 1) *
            (s.MaximumY - s.OriginY + 1)).ToArray();
        // A fingerprint uses resource hashes and local sub-tile IDs, not DAT offsets.
        // This groups byte-identical height-record layouts across different archives.
        var bySignature=fullyPopulated.GroupBy(s=>Fingerprint(s.Tiles)).ToArray();
        var matches=new Dictionary<uint,IReadOnlyList<uint>>();
        foreach(var set in bySignature.Where(g=>g.Count()>1))
        {
            var ids=set.Select(s=>s.DescriptorId).Order().ToArray();
            foreach(uint id in ids)matches[id]=ids;
        }
        return new TerrainHierarchyIndex(archives, fullyPopulated.Length,
            bySignature.Length, matches.Count, matches);
    }

    public IReadOnlyList<uint> GetEquivalentHeightLayouts(uint descriptorId)
        => _sameHeights.TryGetValue(descriptorId,out var list)
            ? list : Array.Empty<uint>();

    private static string Fingerprint(IEnumerable<EsoRecord> tiles)
    {
        var sb=new StringBuilder(650);
        foreach(var t in tiles.OrderBy(t=>t.FileId))
            sb.Append(t.FileId).Append(':').Append(t.UncompressedSize)
                .Append(':').Append(t.Hash).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
