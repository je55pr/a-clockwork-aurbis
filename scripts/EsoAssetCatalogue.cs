using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace EsoTest;

/// <summary>
/// Source-backed catalogue indices. Descriptions deliberately distinguish
/// worldspace metadata, map artwork, item *icons*, and render-effect meshes
/// from server-controlled equipment instances and NPC entities.
/// </summary>
public static class EsoAssetCatalogue
{
    public sealed record Entry(
        string Title,
        string Kind,
        uint ResourceId,
        string Source,
        uint? SectionId = null,
        uint? ZoneId = null,
        uint Width = 0,
        uint Height = 0,
        int RelatedFiles = 1);

    public sealed record Category(string Title, string Description,
        IReadOnlyList<Entry> Entries);

    private static readonly Regex WeaponType = new(
        @"(?:_|^)(?:[12]hsword|[12]haxe|[12]hmace|[12]hhammer|sword|dagger|axe|hammer|mace|staff|bow|shield|sheild)(?:[_\.]|\d|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ArmorType = new(
        @"(?:_|^)(?:head|helmet|helm|chest|robe|feet|boots|hands|gloves|shoulders|pauldrons|waist|belt|legs|greaves|bracers|armcops)(?:[_\.]|\d|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MapTileSuffix = new(
        @"(?i)(?:[._](?:base|overlay|overland|floor|housetile|tile))?_\d{1,3}\.dds$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Independently identified using the original section→zoneId mapping
    // and published ESO zone ID references. Unknown zones retain source labels.
    private static readonly Dictionary<uint,string> VerifiedZones = new()
    {
        [204]="Bangkorai",
        [233]="Auridon",
        [868]="Clockwork City",
        [899]="Summerset",
        [1149]="Blackwood",
        [1391]="Solstice"
    };

    public static IReadOnlyList<Category> Build(EsoArchive archive,
        OodleDecoder decoder, IReadOnlyDictionary<uint,string> filenames,
        out string worldspaceStatus, string? modelNameIndexPath=null)
    {
        var entries=filenames.ToArray();
        var maps=new Dictionary<string,List<KeyValuePair<uint,string>>>(
            StringComparer.OrdinalIgnoreCase);
        var weapons=new List<Entry>();
        var armor=new List<Entry>();
        var fxMeshes=new List<Entry>();
        var creatureArt=new List<Entry>();
        var named=new List<Entry>(entries.Length);

        foreach(var (id,path) in entries)
        {
            string file=path.Split('/').Last();
            named.Add(new Entry(file,"Named archive file",id,path));
            if(path.StartsWith("/art/maps/",StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(".dds",StringComparison.OrdinalIgnoreCase))
            {
                string stem=MapTileSuffix.Replace(path,".dds");
                if(!maps.TryGetValue(stem,out var tiles))
                    maps[stem]=tiles=new List<KeyValuePair<uint,string>>();
                tiles.Add(new KeyValuePair<uint,string>(id,path));
            }
            else if(path.StartsWith("/esoui/art/icons/gear_",StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(".dds",StringComparison.OrdinalIgnoreCase))
            {
                if(WeaponType.IsMatch(file))
                    weapons.Add(new Entry(Readable(file),"Weapon equipment icon",id,path));
                else if(ArmorType.IsMatch(file))
                    armor.Add(new Entry(Readable(file),"Armor equipment icon",id,path));
            }
            else if(path.StartsWith("/art/fx/particle/",StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(".gr2",StringComparison.OrdinalIgnoreCase))
            {
                fxMeshes.Add(new Entry(Readable(file),"Named particle-effect GR2",id,path));
            }
            else if(path.StartsWith("/esoui/art/icons/monster_",StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(".dds",StringComparison.OrdinalIgnoreCase))
            {
                creatureArt.Add(new Entry(Readable(file),"Monster-related loot icon, NOT an enemy definition",id,path));
            }
        }

        var mapEntries=new List<Entry>(maps.Count);
        foreach(var (stem,files) in maps)
        {
            var first=files.FirstOrDefault(f=>f.Value.EndsWith("_0.dds",StringComparison.OrdinalIgnoreCase));
            if(first.Value==null)first=files[0];
            var part=stem["/art/maps/".Length..].Split('/');
            string region=part.Length>1?Readable(part[0]):"Unknown region";
            string label=Readable(part[^1]);
            mapEntries.Add(new Entry(region+" / "+label,
                "Named map-image set",first.Key,first.Value,
                RelatedFiles:files.Count));
        }

        var locations=ReadWorldspaces(archive,decoder,out worldspaceStatus);
        var models=ReadModelReferences(archive,decoder,filenames,modelNameIndexPath);
        int identifiedModels=models.Count(m=>m.Kind=="Recovered GR2 model");
        static IReadOnlyList<Entry> Sorted(IEnumerable<Entry> source) =>
            source.OrderBy(x=>x.Title,StringComparer.OrdinalIgnoreCase).ToArray();

        return new Category[]
        {
            new("Worldspaces",
                "Authoritative local section and zone identifiers from depot metadata category 60. Open a section in the 3D terrain viewer when geometry is available.",
                Sorted(locations)),
            new("Map locations",
                "Original map DDS files grouped by source map name. A map label is not automatically a 3D worldspace ID.",
                Sorted(mapEntries)),
            new("Weapons",
                "Original weapon EQUIPMENT ICONS identified by gear filenames. These are not yet the weapon's 3D mesh or gameplay item definition.",
                Sorted(weapons)),
            new("Armor",
                "Original armor equipment icons identified by gear filenames. Armor meshes, stats and variants have not been linked yet.",
                Sorted(armor)),
            new("Model records",
                $"Metadata-49-linked source IDs, including {identifiedModels:N0} named original 3D models from the optional local index. Model names are not gameplay NPC or item identities. Original meshes now use their recovered source diffuse DDS materials and UV0 where available; special shaders, normals and animations remain incomplete.",
                Sorted(models)),
            new("Effect meshes",
                "Named Granny2/GR2 PARTICLE-EFFECT meshes. This is not a catalogue of actor or equipment geometry.",
                Sorted(fxMeshes)),
            new("Creature art",
                "Monster-themed LOOT ICONS only. These do not establish hostile creature species, spawn points or NPC identities.",
                Sorted(creatureArt)),
            new("Enemies",
                "Enemy classification, character identities and placements have not been recovered from a verified source. No entries are fabricated.",
                Array.Empty<Entry>()),
            new("Friendly NPCs",
                "Friendly NPC identities and interactions are not reliably available from the currently decoded local records. No entries are fabricated.",
                Array.Empty<Entry>()),
            new("All named files",
                "Every original resource with a ZOSFT filename: maps, UI sprites, FX textures, and named particle meshes. Unknown assets remain unclassified.",
                Sorted(named))
        };
    }

    private static IReadOnlyList<Entry> ReadModelReferences(EsoArchive archive,
        OodleDecoder decoder,IReadOnlyDictionary<uint,string> filenames,
        string? modelNameIndexPath)
    {
        try
        {
            var resource=archive.FindId(49).Where(r =>
            {
                var t=archive.GetIdIndexFields(r.Ordinal);
                return t.Flags==0x6000&&t.Field2==0;
            }).OrderByDescending(r=>r.Ordinal).FirstOrDefault();
            if(resource.UncompressedSize==0)return Array.Empty<Entry>();

            var source=archive.ReadDecoded(resource,decoder);
            var table=DepotMetadataContainer.Parse(source,source.Length);
            var ids=new HashSet<uint>();
            for(int i=0;i<table.Entries.Count;i++)
            {
                byte[] row=table.ReadEntry(i);
                if(row.Length>=4)
                {
                    uint id=BinaryPrimitives.ReadUInt32BigEndian(row.AsSpan(0,4));
                    if(id!=0)ids.Add(id);
                }
            }

            // A catalogue entry must point at an actual data record; metadata
            // by itself can mention IDs absent from this installation.
            var found=new List<Entry>(ids.Count);
            var cached=EsoModelNameIndex.Load(modelNameIndexPath??string.Empty);
            var seen=new HashSet<uint>();
            for(int ordinal=0;ordinal<archive.Count;ordinal++)
            {
                var tag=archive.GetIdIndexFields(ordinal);
                if(tag.Flags!=0)continue;
                var record=archive.GetRecord(ordinal);
                if(!ids.Contains(record.FileId)||!seen.Add(record.FileId))continue;
                bool named=filenames.TryGetValue(record.FileId,out string? path);
                string title=named?Readable(path!.Split('/').Last()):
                    $"Model record 0x{record.FileId:X8}";
                bool recovered=cached.TryGetValue(record.FileId,out var cachedName) &&
                    cachedName!.Status=="OK" &&
                    EsoModelNameIndex.MatchesSource(cachedName,record.ArchiveIndex,
                        record.Offset,record.CompressedSize,record.UncompressedSize);
                string? originalName=recovered?EsoModelNameIndex.PreferredName(cachedName!):null;
                if(!string.IsNullOrWhiteSpace(originalName))title=originalName;
                string sourceDetails=$"FAFAEBEB category 49 → original DAT archive {record.ArchiveIndex}\n"+
                    $"Stored: {record.CompressedSize:N0} bytes; decoded: {record.UncompressedSize:N0} bytes"+
                    (named?$"\nNamed file: {path}":"\nNo manifest filename");
                if(recovered)
                    sourceDetails+=$"\n\nRecovered GR2 source filename: {cachedName!.OriginalFilename}"+
                        $"\nInternal model: {cachedName.ModelName}"+
                        $"\nInternal mesh: {cachedName.MeshName}"+
                        $"\nAnimations: {(cachedName.AnimationNames.Length>0?cachedName.AnimationNames:"(none)")}"+
                        $"\nGranny structure: {cachedName.ModelCount} models, {cachedName.MeshCount} meshes, {cachedName.AnimationCount} animations"+
                        "\nMetadata recovered from source GR2 (not gameplay identity)";
                found.Add(new Entry(title,recovered?"Recovered GR2 model":"Model-linked asset ID (category 49)",
                    record.FileId,sourceDetails));
            }
            return found;
        }
        catch(Exception)
        {
            // Keep other categories navigable when an update interrupts the
            // model-metadata stream or an unsupported decoder variant appears.
            return Array.Empty<Entry>();
        }
    }

    private static IReadOnlyList<Entry> ReadWorldspaces(EsoArchive archive,
        OodleDecoder decoder,out string status)
    {
        var results=new List<Entry>();
        try
        {
            var resource=archive.FindId(60).Where(r =>
            {
                var t=archive.GetIdIndexFields(r.Ordinal);
                return t.Flags==0x6000 && t.Field2==0;
            }).OrderByDescending(r=>r.Ordinal).FirstOrDefault();
            if(resource.UncompressedSize==0)
                throw new InvalidOperationException("Metadata category 60 is missing");
            byte[] data=archive.ReadDecoded(resource,decoder);
            var table=DepotMetadataContainer.Parse(data,data.Length);
            for(int i=0;i<table.Entries.Count;i++)
            {
                byte[] row=table.ReadEntry(i);
                if(row.Length<60)continue;
                uint section=BinaryPrimitives.ReadUInt32BigEndian(row.AsSpan(0,4));
                uint width=BinaryPrimitives.ReadUInt32BigEndian(row.AsSpan(25,4));
                uint height=BinaryPrimitives.ReadUInt32BigEndian(row.AsSpan(29,4));
                int nameLength=row[34];
                int zoneOffset=52+nameLength;
                if(nameLength>120 || zoneOffset+4>row.Length)continue;
                string raw=Encoding.UTF8.GetString(row,35,nameLength).TrimEnd('\0');
                uint zone=BinaryPrimitives.ReadUInt32BigEndian(row.AsSpan(zoneOffset,4));
                if(section==0||width==0||height==0)continue;
                string label=VerifiedZones.TryGetValue(section,out string? known)
                    ?known:Readable(raw);
                if(string.IsNullOrEmpty(label))label=$"Section {section}";
                results.Add(new Entry(label+" · zone "+zone,
                    "Original terrain section metadata",
                    ResourceId:section,Source:$"Depot FAFAEBEB metadata 60 / {raw}",
                    SectionId:section,ZoneId:zone,Width:width,Height:height));
            }
            status=$"{results.Count:N0} section records decoded from category 60";
        }
        catch(Exception e)
        {
            status="Worldspace metadata temporarily unavailable: "+e.Message;
        }
        return results;
    }

    private static string Readable(string s)
    {
        var word=s;
        int pos=word.LastIndexOf('.');
        if(pos>=0)word=word[..pos];
        word=word.Replace('_',' ').Replace('-',' ').Trim();
        return word.Length==0?s:word;
    }
}
