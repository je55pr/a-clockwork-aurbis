using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AClockworkAurbis.Simulation;

// Game-neutral character data. Everything that could be absent in source records is nullable.
public sealed class NpcProfile
{
    public int SchemaVersion { get; init; }
    public string? Kind { get; init; }
    public string? Id { get; init; }
    public NpcIdentity? Identity { get; init; }
    public NpcSociety? Society { get; init; }
    public NpcCombat? Combat { get; init; }
    public NpcLocations? Locations { get; init; }
    public NpcReferences? References { get; init; }
    public NpcSource? Source { get; init; }
    public List<NpcAppearance>? Appearances { get; init; }
    public Dictionary<string, List<string>>? Conflicts { get; init; }
    public NpcSimulation? Simulation { get; init; }
    public NpcSelection? Selection { get; init; }
}
public sealed class NpcIdentity
{
    public string? Name { get; init; }
    public string? Race { get; init; }
    public string? Gender { get; init; }
    public string? Title { get; init; }
    public string? GameNpcId { get; init; }
    public bool? Generic { get; init; }
    public bool? Creature { get; init; }
}
public sealed class NpcSociety
{
    public string? Faction { get; init; }
    public string? ServiceRole { get; init; }
    public string? Occupation { get; init; }
    public string? SocialStatus { get; init; }
}
public sealed class NpcCombat
{
    public string? GameClass { get; init; }
    public string? Reaction { get; init; }
    public long? Health { get; init; }
    public string? HealthRaw { get; init; }
    public long? Level { get; init; }
    public string? Difficulty { get; init; }
}
public sealed class NpcLocations
{
    public NpcPlaceReference? Primary { get; init; }
    public List<NpcPlaceReference>? Candidates { get; init; }
    public bool? Ambiguous { get; init; }
    public bool? MultiAppearance { get; init; }
}
public sealed class NpcPlaceReference
{
    public string? Name { get; init; }
    public long? PlaceId { get; init; }
    public string? PageTitle { get; init; }
    public string? Relation { get; init; }
    public int? SummaryNumber { get; init; }
    public string? Raw { get; init; }
    public string? Resolution { get; init; }
}
public sealed class NpcReferences
{
    public List<NpcPageReference>? Quests { get; init; }
    public NpcPageReference? Lore { get; init; }
    public NpcRaceReferences? Race { get; init; }
    public string? ImageFilename { get; init; }
}
public sealed class NpcRaceReferences
{
    public NpcPageReference? Eso { get; init; }
    public NpcPageReference? Lore { get; init; }
}
public sealed class NpcPageReference
{
    public string? Name { get; init; }
    public long? PageId { get; init; }
    public string? PageTitle { get; init; }
    public string? WikiUrl { get; init; }
    public string? RedirectTitle { get; init; }
    public string? Resolution { get; init; }
}
public sealed class NpcSource
{
    public string? Provider { get; init; }
    public string? Game { get; init; }
    public long? PageId { get; init; }
    public string? WikiTitle { get; init; }
    public long? RevisionId { get; init; }
    public string? RevisionTimestamp { get; init; }
    public string? WikiUrl { get; init; }
}
public sealed class NpcAppearance
{
    public int? SummaryNumber { get; init; }
    public Dictionary<string, JsonElement>? RawFields { get; init; }
}
public sealed class NpcSimulation
{
    public JsonElement? Age { get; set; }
    public JsonElement? Personality { get; set; }
    public JsonElement? Attributes { get; set; }
    public JsonElement? Inventory { get; set; }
    public JsonElement? Schedule { get; set; }
    public JsonElement? Home { get; set; }
    public JsonElement? Relationships { get; set; }
    public JsonElement? Wealth { get; set; }
    public JsonElement? CurrentHealth { get; set; }
}
public sealed class NpcSelection
{
    public bool? Eligible { get; init; }
    public List<string>? Reasons { get; init; }
    public string? Hash { get; init; }
}
