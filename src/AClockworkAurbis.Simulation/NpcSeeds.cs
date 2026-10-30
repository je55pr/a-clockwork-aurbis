using System.Text.Json;

namespace AClockworkAurbis.Simulation;

public static class NpcSeeds
{
    public static List<NpcSeed> Synthetic(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        var result = new List<NpcSeed>(count);
        for (int i = 0; i < count; i++)
            result.Add(new NpcSeed(
                $"synthetic:{i}",
                $"Resident {i}",
                i % 7 == 0 ? "Argonian" : "High Elf",
                i % 5 == 0 ? null : i % 9 == 0 ? "Merchant" : "Worker",
                $"Synthetic district {i % 12}"));
        return result;
    }

    // Streams the existing ProfileBuilder's JSONL format. No SQLite, renderer,
    // engine, client binaries, or third-party dependency is required.
    public static List<NpcSeed> ReadProfiles(
        string jsonlPath, int limit = int.MaxValue, string? area = null)
    {
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        var result = new List<NpcSeed>(Math.Min(limit, 10_000));
        if (limit == 0) return result;
        using var file = File.OpenRead(jsonlPath);
        using var reader = new StreamReader(file);
        while (reader.ReadLine() is { } line && result.Count < limit)
        {
            if (line.Length == 0) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var locations = Element(root, "locations");
            if (area is not null && !MatchesArea(locations, area))
                continue;
            var identity = Element(root, "identity");
            var society = Element(root, "society");
            var primary = Element(locations, "primary");
            string? id = String(root, "id");
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidDataException("Source profile has no stable ID");
            result.Add(new NpcSeed(
                id, String(identity, "name") ?? id,
                String(identity, "race"),
                String(society, "occupation") ?? String(society, "serviceRole"),
                String(primary, "name")));
        }
        return result;
    }

    private static bool MatchesArea(JsonElement locations, string area)
    {
        if (MatchesPlace(Element(locations, "primary"), area))
            return true;
        var candidates = Element(locations, "candidates");
        if (candidates.ValueKind == JsonValueKind.Array)
            foreach (var place in candidates.EnumerateArray())
                if (MatchesPlace(place, area)) return true;
        return false;
    }

    private static bool MatchesPlace(JsonElement place, string area) =>
        string.Equals(String(place, "name"), area, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(String(place, "pageTitle"), "Online:" + area,
            StringComparison.OrdinalIgnoreCase);

    private static JsonElement Element(JsonElement source, string property) =>
        source.ValueKind == JsonValueKind.Object &&
        source.TryGetProperty(property, out var result) ? result : default;

    private static string? String(JsonElement source, string property)
    {
        var value = Element(source, property);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}

internal static class StableRandom
{
    public static ulong Hash(string value, int seed)
    {
        ulong hash = 14695981039346656037UL;
        unchecked
        {
            hash ^= (uint)seed;
            hash *= 1099511628211UL;
            foreach (char c in value)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
        }
        return hash;
    }

    public static int Between(string id, int seed, int salt, int exclusiveUpper) =>
        (int)(Hash(id, unchecked(seed + salt * 19937)) % (uint)exclusiveUpper);
}
