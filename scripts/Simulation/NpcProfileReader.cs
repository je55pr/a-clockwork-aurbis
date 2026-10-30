using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AClockworkAurbis.Simulation;

public static class NpcProfileReader
{
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Streams one profile at a time. Does not load the entire 10,000-NPC roster into memory.
    public static IEnumerable<NpcProfile> ReadJsonLines(string filePath)
    {
        var lineNumber = 0;
        foreach (var line in File.ReadLines(filePath))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            NpcProfile profile;
            try
            {
                profile = JsonSerializer.Deserialize<NpcProfile>(line, Options)
                    ?? throw new JsonException("JSON line was null.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Invalid NPC profile in {filePath} at line {lineNumber}.", exception);
            }

            if (profile.SchemaVersion != SupportedSchemaVersion
                || profile.Kind != "npc"
                || string.IsNullOrWhiteSpace(profile.Id))
            {
                throw new InvalidDataException(
                    $"Unsupported or incomplete NPC profile in {filePath} at line {lineNumber}.");
            }

            yield return profile;
        }
    }
}
