using System.Text.Json;

namespace AClockworkAurbis.Simulation;

// The catalog is source-backed and immutable; runtime accounts, stock, jobs
// and unknown housing are explicitly separate simulation state.
public sealed record SettlementCatalog(
    int SchemaVersion,
    string Settlement,
    string Source,
    JsonElement Counts,
    PlaceDefinition[] Places,
    BusinessDefinition[] Businesses,
    ResidentDefinition[] Residents)
{
    public static SettlementCatalog Load(string file)
    {
        using var stream = File.OpenRead(file);
        var catalog = JsonSerializer.Deserialize<SettlementCatalog>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Empty settlement catalog");
        catalog.Validate();
        return catalog;
    }

    public void Validate()
    {
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(Settlement) ||
            Places is null || Businesses is null || Residents is null)
            throw new InvalidDataException("Unknown or malformed settlement catalog");
        var placeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var place in Places)
        {
            if (string.IsNullOrWhiteSpace(place.Id) || !placeIds.Add(place.Id) ||
                string.IsNullOrWhiteSpace(place.Name) ||
                place.MapX.HasValue != place.MapY.HasValue ||
                place.MapX is double x && (x < 0 || x > 1_000_000 || double.IsNaN(x)) ||
                place.MapY is double y && (y < 0 || y > 1_000_000 || double.IsNaN(y)))
                throw new InvalidDataException("Duplicate/unnamed source place");
        }

        var businessIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var business in Businesses)
        {
            if (!placeIds.Contains(business.PlaceId) || !businessIds.Add(business.PlaceId) ||
                !BusinessKinds.Valid.Contains(business.Kind))
                throw new InvalidDataException("Invalid business location or kind");
        }

        var actorIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resident in Residents)
        {
            if (string.IsNullOrWhiteSpace(resident.Id) || !actorIds.Add(resident.Id) ||
                string.IsNullOrWhiteSpace(resident.Name) ||
                resident.HomePlaceId is not null && !placeIds.Contains(resident.HomePlaceId) ||
                resident.WorkPlaceId is not null && !businessIds.Contains(resident.WorkPlaceId))
                throw new InvalidDataException("Unknown resident identity, home or workplace");
        }
        foreach (var business in Businesses)
        {
            if (business.OwnerNpcId is not null && !actorIds.Contains(business.OwnerNpcId))
                throw new InvalidDataException("Owner has no source-backed resident profile");
        }
    }
}

public static class BusinessKinds
{
    public const string FoodRetail = "food_retail";
    public const string GoodsRetail = "goods_retail";
    public const string Workshop = "workshop";
    public const string ImportHub = "import_hub";
    public const string Institution = "institution";
    public static readonly IReadOnlySet<string> Valid =
        new HashSet<string>(StringComparer.Ordinal)
        { FoodRetail, GoodsRetail, Workshop, ImportHub, Institution };
}

public sealed record PlaceDefinition(
    string Id, int SourcePageId, string Name, string? SourceType,
    string? Settlement, string? Zone, string Role, string SourceUrl,
    double? MapX = null, double? MapY = null,
    string? MapEvidence = null, string? District = null);

public sealed record BusinessDefinition(
    string PlaceId, string Kind, string? OwnerNpcId,
    string[] OperatorNpcIds, string Evidence);

public sealed record ResidentDefinition(
    string Id, int SourcePageId, string Name,
    string? Race, string? Profession,
    string? HomePlaceId, string HomeEvidence,
    string? WorkPlaceId, string WorkEvidence,
    string[] LocationPlaceIds, string? SourceUrl,
    string? AnchorPlaceId = null, string? AnchorEvidence = null);

public enum SettlementAction : byte
{
    Idle, Travel, Work, Eat, Sleep, BuyFood, BuyGoods
}

public sealed class ResidentState
{
    internal ResidentState(ResidentDefinition source, string homeId,
        int gold, int food, double satiation, double energy, int workStart)
    {
        Source = source;
        HomeId = homeId;
        CurrentPlaceId = homeId;
        DestinationPlaceId = homeId;
        Gold = gold;
        Food = food;
        Satiation = satiation;
        Energy = energy;
        WorkStart = workStart;
    }
    public ResidentDefinition Source { get; }
    public string HomeId { get; }
    public string? WorkId => Source.WorkPlaceId;
    public string CurrentPlaceId { get; internal set; }
    public string DestinationPlaceId { get; internal set; }
    public SettlementAction Activity { get; internal set; }
    public long ActionStart { get; internal set; }
    public long LastUpdate { get; internal set; }
    public int WorkStart { get; }
    public long Gold { get; internal set; }
    public int Food { get; internal set; }
    public int Goods { get; internal set; }
    public double Satiation { get; internal set; }
    public double Energy { get; internal set; }
    public int WorkRemainderMinutes { get; internal set; }
    public int MealsEaten { get; internal set; }
    public int Purchases { get; internal set; }
    public int Journeys { get; internal set; }
    public long WageIncome { get; internal set; }
    public long DividendIncome { get; internal set; }
    public long UnpaidHours { get; internal set; }
    public long WorkMinutes { get; internal set; }
    public long SleepMinutes { get; internal set; }
    public long JourneyMinutes { get; internal set; }
    internal long LastGoodsShoppingDay { get; set; } = -10;
}

public sealed class BusinessState
{
    internal BusinessState(BusinessDefinition definition, PlaceDefinition place, long gold)
    {
        Definition = definition;
        Place = place;
        Gold = gold;
    }

    public BusinessDefinition Definition { get; }
    public PlaceDefinition Place { get; }
    public string Id => Definition.PlaceId;
    public string Kind => Definition.Kind;
    public string Name => Place.Name;
    public string? OwnerId => Definition.OwnerNpcId; // Actual source owner, if verifiable.
    public string? SimulatedLeaseholderId { get; internal set; }
    public string LeaseEvidence { get; internal set; } = "unresolved";
    public long DividendsPaid { get; internal set; }
    public IReadOnlyList<string> Operators => Definition.OperatorNpcIds;
    public long Gold { get; internal set; }
    public int Food { get; internal set; }
    public int Materials { get; internal set; }
    public int Goods { get; internal set; }
    public long WagesPaid { get; internal set; }
    public long RetailRevenue { get; internal set; }
    public long WholesaleRevenue { get; internal set; }
    public long WholesaleSpending { get; internal set; }
    public int RetailSales { get; internal set; }
    public int WorkHours { get; internal set; }
    public int ItemsCrafted { get; internal set; }
}

public sealed record SettlementSettings
{
    public int Seed { get; init; } = 42;
    public int MealPrice { get; init; } = 6;
    public int GoodsPrice { get; init; } = 12;
    public int FoodWholesalePrice { get; init; } = 2;
    public int MaterialWholesalePrice { get; init; } = 3;
    public int GoodsWholesalePrice { get; init; } = 6;
    public int HourlyWage { get; init; } = 3;
    public int FoodImportPerResident { get; init; } = 2;
    public int MaterialImportPerTenResidents { get; init; } = 3;
    public int BaseTravelMinutes { get; init; } = 18;
    // UESP map units are not metres. This is a simulated travel-time
    // calibration parameter, not a real-world speed measurement.
    public int MapUnitsPerTravelMinute { get; init; } = 2500;
    public int UnknownMapInterdistrictMinutes { get; init; } = 90;
    public int StartingBusinessGold { get; init; } = 1_200;
    public int StartHour { get; init; } = 6;
}

public sealed record BusinessSnapshot(
    string Name, string Kind, string PlaceId, string? OwnerId,
    int NamedOperators, string? SimulatedLeaseholderId, string LeaseEvidence,
    long Gold, int Food, int Materials, int Goods,
    int RetailSales, int WorkHours, int ItemsCrafted, long WagesPaid,
    long DividendsPaid);

public sealed record SettlementReport
{
    public required string Settlement { get; init; }
    public required int SourcePlaces { get; init; }
    public required int Businesses { get; init; }
    public required int Residents { get; init; }
    public required int SourceHomeAssignments { get; init; }
    public required int SourceStoreAssignments { get; init; }
    public required int InferredWorkAssignments { get; init; }
    public required long Minute { get; init; }
    public required long Events { get; init; }
    public required long Wages { get; init; }
    public required long Dividends { get; init; }
    public required long UnpaidHours { get; init; }
    public required long RetailFoodSales { get; init; }
    public required long RetailGoodsSales { get; init; }
    public required long Meals { get; init; }
    public required long Travel { get; init; }
    public required long TravelMinutes { get; init; }
    public required long LongJourneys { get; init; }
    public required int ResidentsWithMapAnchor { get; init; }
    public required int PlacesWithSourceMarker { get; init; }
    public required int PlacesWithEstimatedMarker { get; init; }
    public required long CraftedGoods { get; init; }
    public required long GoodsUsed { get; init; }
    public required long FoodImported { get; init; }
    public required long MaterialsImported { get; init; }
    public required long FoodRemaining { get; init; }
    public required long MaterialsRemaining { get; init; }
    public required long GoodsRemaining { get; init; }
    public required long InitialFood { get; init; }
    public required long InitialMaterials { get; init; }
    public required long InitialGoods { get; init; }
    public required long InitialGold { get; init; }
    public required long TotalGold { get; init; }
    public required int SyntheticHomeCount { get; init; }
    public required int UnemployedResidents { get; init; }
    public required int ResidentsWithLowSatiation { get; init; }
    public required int ResidentsWithZeroSatiation { get; init; }
    public required int ResidentsWithoutFood { get; init; }
    public required int ResidentsWithoutGold { get; init; }
    public required BusinessSnapshot[] BusinessDetails { get; init; }
}
