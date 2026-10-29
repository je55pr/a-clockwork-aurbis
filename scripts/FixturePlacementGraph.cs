using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace EsoTest;

/// <summary>
/// Infers relative section origins from identical *fixture instance IDs* and
/// their original XYZ coordinates. Does not use terrain-edge matching or the
/// archive descriptor ID grid. This is an evidence graph, not named zone data.
/// </summary>
public sealed class FixturePlacementGraph
{
    public const int FixtureStride = 96;
    public const int OriginUnit = 500;

    public readonly record struct Position(int X, int Z);
    public readonly record struct Link(uint First, uint Second, short DeltaX,
        short DeltaZ, int Witnesses);
    public sealed record Component(
        IReadOnlyDictionary<uint, Position> Origins,
        int ConsistencyErrors, int EdgeCount, long Witnesses)
    {
        public int Count => Origins.Count;
    }

    public int DescriptorCount { get; private init; }
    public int SourceCount { get; private init; }
    public int FixtureCount { get; private init; }
    public int UnreadableSources { get; private init; }
    public IReadOnlyList<Link> Links { get; private init; } = Array.Empty<Link>();
    public IReadOnlyList<Component> Components { get; private init; } = Array.Empty<Component>();

    private readonly record struct Fixture(
        uint Id, uint Section, float X, float Y, float Z);
    private readonly record struct EvidenceKey(
        uint A, uint B, short OffsetX, short OffsetZ);

    public static FixturePlacementGraph Discover(
        EsoArchive archive, OodleDecoder decoder, int minWitnesses = 20)
    {
        if (minWitnesses < 1)
            throw new ArgumentOutOfRangeException(nameof(minWitnesses));
        var points = new List<Fixture>(1_500_000);
        int descriptors = 0, sources = 0, failures = 0;
        for (int i = 0; i < archive.Count; i++)
        {
            EsoRecord descriptor = archive.GetRecord(i);
            var tag = archive.GetIdIndexFields(i);
            if (descriptor.UncompressedSize != 583 ||
                tag.Flags != 0x4400 || tag.Field2 != 0)
                continue;
            descriptors++;
            for (int j = i + 1; j < Math.Min(i + 130, archive.Count); j++)
            {
                EsoRecord rec = archive.GetRecord(j);
                var childTag = archive.GetIdIndexFields(j);
                if (rec.ArchiveIndex != descriptor.ArchiveIndex ||
                    (rec.UncompressedSize == 583 && childTag.Flags == 0x4400))
                    break;
                int channel = childTag.Field2 & 31;
                if (childTag.Flags != 0x4800 ||
                    channel is not (1 or 2) ||
                    (uint)(childTag.Field2 >> 5) != descriptor.FileId ||
                    rec.UncompressedSize is < 48 or > 8_000_000)
                    continue;

                try
                {
                    var data = archive.ReadDecoded(rec, decoder);
                    if (data.Length < 8 ||
                        BinaryPrimitives.ReadUInt32LittleEndian(data) != 23)
                        continue;
                    int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
                    if (count < 0 || (long)8 + count * FixtureStride > data.Length)
                        continue;
                    sources++;
                    for (int k = 0; k < count; k++)
                    {
                        int offset = 8 + k * FixtureStride;
                        uint id = BinaryPrimitives.ReadUInt32LittleEndian(
                            data.AsSpan(offset));
                        if (id < 1_000) continue;
                        float x = BitConverter.ToSingle(data, offset + 28);
                        float y = BitConverter.ToSingle(data, offset + 32);
                        float z = BitConverter.ToSingle(data, offset + 36);
                        if (!float.IsFinite(x) || !float.IsFinite(y) ||
                            !float.IsFinite(z) || Math.Abs(x) > 20_000 ||
                            Math.Abs(y) > 10_000 || Math.Abs(z) > 20_000)
                            continue;
                        points.Add(new Fixture(id, descriptor.FileId, x, y, z));
                    }
                }
                catch (Exception)
                {
                    failures++;
                }
            }
            if (points.Count > 3_000_000)
                throw new InvalidOperationException(
                    "Fixture index exceeded the safety limit.");
        }
        int fixtureCount = points.Count;
        points.Sort((a, b) => a.Id.CompareTo(b.Id));
        var votes = new Dictionary<EvidenceKey, int>();
        for (int i = 0; i < points.Count;)
        {
            int end = i + 1;
            while (end < points.Count && points[end].Id == points[i].Id)
                end++;
            if (end - i is > 1 and <= 32)
            {
                for (int a = i; a < end; a++)
                for (int b = a + 1; b < end; b++)
                {
                    var left = points[a];
                    var right = points[b];
                    if (left.Section == right.Section)
                        continue;
                    if (left.Section > right.Section)
                        (left, right) = (right, left);

                    double dx = left.X - right.X;
                    double dz = left.Z - right.Z;
                    double dy = left.Y - right.Y;
                    int ox = (int)Math.Round(dx / OriginUnit);
                    int oz = (int)Math.Round(dz / OriginUnit);
                    if (Math.Abs(dx - ox * OriginUnit) > 0.06 ||
                        Math.Abs(dz - oz * OriginUnit) > 0.06 ||
                        Math.Abs(dy) > 0.06 ||
                        Math.Abs(ox) > 20 || Math.Abs(oz) > 20)
                        continue;

                    var key = new EvidenceKey(left.Section, right.Section,
                        (short)ox, (short)oz);
                    votes[key] = votes.GetValueOrDefault(key) + 1;
                }
            }
            i = end;
        }
        points.Clear();

        var links = votes
            .Where(p => p.Value >= minWitnesses &&
                (p.Key.OffsetX != 0 || p.Key.OffsetZ != 0))
            .Select(p => new Link(p.Key.A, p.Key.B,
                p.Key.OffsetX, p.Key.OffsetZ, p.Value))
            .OrderByDescending(p => p.Witnesses)
            .ToArray();
        var graph = new Dictionary<uint,
            List<(uint To, short DX, short DZ, int Strength)>>();
        foreach (var edge in links)
        {
            if (!graph.TryGetValue(edge.First, out var a))
                graph[edge.First] = a = new();
            if (!graph.TryGetValue(edge.Second, out var b))
                graph[edge.Second] = b = new();
            a.Add((edge.Second, edge.DeltaX, edge.DeltaZ, edge.Witnesses));
            b.Add((edge.First, (short)-edge.DeltaX,
                (short)-edge.DeltaZ, edge.Witnesses));
        }
        var visited = new HashSet<uint>();
        var components = new List<Component>();
        foreach (uint first in graph.Keys)
        {
            if (!visited.Add(first)) continue;
            var positions = new Dictionary<uint, Position> { [first] = new(0, 0) };
            var queue = new Queue<uint>();
            queue.Enqueue(first);
            int contradictions = 0, edges = 0;
            long witnesses = 0;
            while (queue.Count != 0)
            {
                uint id = queue.Dequeue();
                foreach (var edge in graph[id])
                {
                    var expected = new Position(
                        positions[id].X + edge.DX,
                        positions[id].Z + edge.DZ);
                    if (positions.TryGetValue(edge.To, out var existing))
                    {
                        if (existing != expected) contradictions++;
                    }
                    else
                    {
                        positions[edge.To] = expected;
                        visited.Add(edge.To);
                        queue.Enqueue(edge.To);
                    }
                    edges++;
                    witnesses += edge.Strength;
                }
            }
            components.Add(new Component(
                positions, contradictions, edges / 2, witnesses / 2));
        }
        return new FixturePlacementGraph
        {
            DescriptorCount = descriptors,
            SourceCount = sources,
            FixtureCount = fixtureCount,
            UnreadableSources = failures,
            Links = links,
            Components = components.OrderByDescending(c => c.Count).ToArray()
        };
    }
}
