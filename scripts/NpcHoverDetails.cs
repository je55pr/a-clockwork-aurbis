using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace EsoTest;

/// <summary>Formats source-backed NPC details without inferring missing values.</summary>
public static class NpcHoverDetails
{
    public readonly record struct Card(string Title, string Subtitle, string Body, string? SourceUrl, string? ArticleWikitext);

    private static bool TryGet(JsonElement value, string key, out JsonElement result)
    {
        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty(key, out result) &&
            result.ValueKind != JsonValueKind.Null &&
            result.ValueKind != JsonValueKind.Undefined)
            return true;
        result = default;
        return false;
    }
    private static string? Field(JsonElement value, string name)
    {
        if (!TryGet(value, name, out var raw)) return null;
        string? text = raw.ValueKind == JsonValueKind.String
            ? raw.GetString()
            : raw.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                ? raw.ToString() : raw.GetRawText();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
    private static string Label(string field)
    {
        var words = field.Replace('_', ' ').Replace('-', ' ');
        return string.Join(" ", words.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => char.ToUpperInvariant(x[0]) + x[1..]));
    }
    private static void Add(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            sb.Append(label).Append(": ").AppendLine(value);
    }
    private static void Section(StringBuilder sb, string title)
    {
        sb.AppendLine().Append("── ").Append(title).AppendLine(" ──");
    }
    private static void AppendFields(StringBuilder sb, JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return;
        foreach (var field in obj.EnumerateObject())
            Add(sb, Label(field.Name), field.Value.ValueKind == JsonValueKind.String
                ? field.Value.GetString() : field.Value.ToString());
    }

    public static Card Build(JsonElement person, PopulationSpawnPlanner.Placement placement)
    {
        string name = Field(person, "name") ?? placement.Name;
        string race = Field(person, "race") ?? "Race unknown";
        string gender = Field(person, "gender") ?? "Gender unknown";
        string occupation = Field(person, "service") ?? "";
        string subtitle = string.Join(" · ",
            new[] { race, gender, occupation }.Where(x => !string.IsNullOrWhiteSpace(x)));

        if (!TryGet(person, "details", out var details))
        {
            return new Card(name, subtitle,
                "Generated ambient NPC for population-density testing.\n" +
                "No UESP character record is associated with this mannequin.",
                null, null);
        }

        var sb = new StringBuilder(3500);
        Section(sb, "Identity and known properties");
        Add(sb, "Name", name);
        Add(sb, "Race", Field(person, "race"));
        Add(sb, "Gender", Field(person, "gender"));
        Add(sb, "Service / role", Field(person, "service"));
        Add(sb, "Faction", Field(person, "faction"));
        Add(sb, "Reaction", Field(person, "reaction"));

        if (TryGet(details, "standardized", out var standardized))
        {
            if (TryGet(standardized, "identity", out var identity))
            {
                Add(sb, "Title", Field(identity, "title"));
                Add(sb, "Client NPC ID", Field(identity, "gameNpcId"));
                Add(sb, "Generic template", Field(identity, "generic"));
                Add(sb, "Creature", Field(identity, "creature"));
            }
            if (TryGet(standardized, "society", out var society))
            {
                Add(sb, "Faction (normalized)", Field(society, "faction"));
                Add(sb, "Occupation", Field(society, "occupation"));
                Add(sb, "Social status", Field(society, "socialStatus"));
            }
            if (TryGet(standardized, "combat", out var combat))
            {
                Section(sb, "Combat and disposition");
                Add(sb, "Class", Field(combat, "gameClass"));
                Add(sb, "Reaction", Field(combat, "reaction"));
                Add(sb, "Health (numeric)", Field(combat, "health"));
                Add(sb, "Health (source notation)", Field(combat, "healthRaw"));
                Add(sb, "Level", Field(combat, "level"));
                Add(sb, "Difficulty", Field(combat, "difficulty"));
            }
            if (TryGet(standardized, "conflicts", out var conflicts) &&
                conflicts.ValueKind == JsonValueKind.Object &&
                conflicts.EnumerateObject().Any())
            {
                Section(sb, "Conflicting source appearances");
                AppendFields(sb, conflicts);
            }
        }

        Section(sb, "Where the source says they appear");
        if (TryGet(person, "places", out var places) &&
            places.ValueKind == JsonValueKind.Array)
        {
            foreach (var place in places.EnumerateArray())
            {
                string label = Field(place, "name") ?? Field(place, "title") ?? "Unnamed place";
                string context = string.Join(", ", new[]{
                    Field(place,"city"),Field(place,"settlement"),Field(place,"zone")
                }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
                sb.Append("• ").Append(label);
                if (context.Length > 0) sb.Append(" (").Append(context).Append(')');
                sb.AppendLine();
            }
        }

        Section(sb, "Preview placement (estimated, not ESO coordinates)");
        if (TryGet(person, "town", out var town))
        {
            Add(sb, "Spawn area", Field(town, "place"));
            Add(sb, "Why this area", Field(town, "kind"));
            Add(sb, "Linked source location", Field(town, "sourcePlace"));
        }
        Add(sb, "Position in local preview",
            $"{placement.Position.X:0.0}, {placement.Position.Y:0.0}, {placement.Position.Z:0.0}");
        sb.AppendLine("This is an approximate first-frame spawn, not a documented NPC position.");

        Section(sb, "Related quests");
        if (TryGet(details, "quests", out var quests) && quests.ValueKind == JsonValueKind.Array &&
            quests.GetArrayLength() > 0)
        {
            foreach (var quest in quests.EnumerateArray())
            {
                var label = Field(quest, "quest_label") ?? quest.ToString();
                if (string.Equals(label, "Quest Name", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine("• Quest Name (unresolved source placeholder)");
                else
                    sb.Append("• ").AppendLine(label);
            }
        }
        else sb.AppendLine("No quest references collected.");

        if (TryGet(standardized, "references", out var references))
        {
            Section(sb, "ESO and cross-game lore references");
            if (TryGet(references, "lore", out var lore))
                Add(sb, "Character lore page", Field(lore, "wikiUrl"));
            if (TryGet(references, "race", out var racePages))
            {
                if (TryGet(racePages, "eso", out var eso))
                    Add(sb, "ESO race page", Field(eso, "wikiUrl"));
                if (TryGet(racePages, "lore", out var crossGame))
                    Add(sb, "Elder Scrolls lore race page", Field(crossGame, "wikiUrl"));
            }
            Add(sb, "Image filename", Field(references, "imageFilename"));
        }

        Section(sb, "All collected original NPC fields");
        if (TryGet(details, "sourceFields", out var sourceFields))
            AppendFields(sb, sourceFields);

        if (TryGet(details, "summaryVariants", out var summaries) &&
            summaries.ValueKind == JsonValueKind.Array)
        {
            Section(sb, "Raw NPC infobox appearances");
            foreach (var summary in summaries.EnumerateArray())
            {
                sb.Append("Appearance ").Append(Field(summary, "number") ?? "?").AppendLine(":");
                if (TryGet(summary, "fields", out var fields))
                    AppendFields(sb, fields);
                sb.AppendLine();
            }
        }

        Section(sb, "Source and revision");
        Add(sb, "UESP page", Field(details, "wikiTitle"));
        Add(sb, "Wiki revision", Field(details, "revisionId"));
        Add(sb, "Revision timestamp", Field(details, "revisionTimestamp"));
        Add(sb, "Source URL", Field(details, "sourcePage"));
        if (TryGet(details, "sourceRecord", out var record))
            AppendFields(sb, record);

        Section(sb, "Complete wiki article");
        sb.AppendLine("Pin this tooltip to expand the entire original wikitext, including all collected dialogue, biographies and quest events.");
        sb.AppendLine("Source templates remain raw and are not inferred simulation attributes.");

        return new Card(name, subtitle, sb.ToString(), Field(details, "sourcePage"),
            Field(details, "articleWikitext"));
    }
}
