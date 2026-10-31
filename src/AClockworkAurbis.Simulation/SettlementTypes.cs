using System.Text.Json;
using System.Collections.ObjectModel;

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
    ResidentDefinition[] Residents,
    string? Scope = null)
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
            if(business.ProductionProfile is string profile)
            {
                bool validCrops=business.Kind==BusinessKinds.FoodProducer &&
                    profile.Split(',',StringSplitOptions.TrimEntries|
                        StringSplitOptions.RemoveEmptyEntries) is var crops &&
                    crops.Length is >=1 and <=4 &&
                    crops.Distinct(StringComparer.Ordinal).Count()==crops.Length &&
                    crops.All(c=>CommodityRecipes.Crops.Contains(c,StringComparer.Ordinal));
                bool validForest=business.Kind==BusinessKinds.ResourceProducer &&
                    profile=="logs";
                if(!validCrops && !validForest)
                    throw new InvalidDataException(
                        "Unknown or incompatible production profile: "+profile);
            }
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
    public const string FoodProducer = "food_producer";
    public const string ResourceProducer = "resource_producer";
    public const string GoodsRetail = "goods_retail";
    public const string Workshop = "workshop";
    public const string ImportHub = "import_hub";
    public const string Institution = "institution";
    public const string Smeltery = "smeltery";
    public const string Sawmill = "sawmill";
    public const string Tannery = "tannery";
    public const string Bakery = "bakery";
    public const string Hunter = "hunter";
    public const string Fishery = "fishery";
    public const string Leatherworker = "leatherworker";
    public static readonly IReadOnlySet<string> Valid =
        new HashSet<string>(StringComparer.Ordinal)
        { FoodRetail, FoodProducer, ResourceProducer, GoodsRetail, Workshop,
          ImportHub, Institution, Smeltery, Sawmill, Tannery,
          Bakery, Hunter, Fishery, Leatherworker };
}

public sealed record PlaceDefinition(
    string Id, int SourcePageId, string Name, string? SourceType,
    string? Settlement, string? Zone, string Role, string SourceUrl,
    double? MapX = null, double? MapY = null,
    string? MapEvidence = null, string? District = null);

public sealed record BusinessDefinition(
    string PlaceId, string Kind, string? OwnerNpcId,
    string[] OperatorNpcIds, string Evidence,
    string? ProductionProfile = null);

public sealed record ResidentDefinition(
    string Id, int SourcePageId, string Name,
    string? Race, string? Profession,
    string? HomePlaceId, string HomeEvidence,
    string? WorkPlaceId, string WorkEvidence,
    string[] LocationPlaceIds, string? SourceUrl,
    string? AnchorPlaceId = null, string? AnchorEvidence = null);

public enum SettlementAction : byte
{
    Idle, Travel, Work, Eat, Sleep, BuyFood, BuyGoods, HarvestFood
}

public enum EconomyMode { Prototype, RegionalProduction, CommodityProduction }

public sealed class ResidentState
{
    internal ResidentState(ResidentDefinition source, string homeId,
        int gold, int food, double satiation, double energy, int workStart)
    {
        Source = source;
        CommodityStock = new ReadOnlyDictionary<string,int>(Stock);
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
    public string HomeId { get; internal set; }
    public string? HousingId { get; internal set; }
    internal bool RoughSleeping { get; set; }
    public int Relocations { get; internal set; }
    public int Rehousings { get; internal set; }
    public int Evictions { get; internal set; }
    public int HomelessDays { get; internal set; }
    public long RentSpent { get; internal set; }
    // Source-backed job links are immutable; contracts are simulation state.
    public string? WorkId => OnUnpaidLeave ? null : AssignedWorkPlaceId ?? Source.WorkPlaceId;
    public bool OnUnpaidLeave { get; internal set; }
    public int InsolvencyJobChanges { get; internal set; }
    public string? AssignedWorkPlaceId { get; internal set; }
    public string EmploymentEvidence { get; internal set; } = "no_known_employment";
    public int SubsistenceFoodHarvested { get; internal set; }
    public double MinutesAtZeroSatiation { get; internal set; }
    public double MinutesBelow20Satiation { get; internal set; }
    public long AssistanceGold { get; internal set; }
    public long CooperativeIncome { get; internal set; }
    public string CurrentPlaceId { get; internal set; }
    public string DestinationPlaceId { get; internal set; }
    public SettlementAction Activity { get; internal set; }
    public long ActionStart { get; internal set; }
    public long LastUpdate { get; internal set; }
    public int WorkStart { get; }
    public long Gold { get; internal set; }
    public int Food { get; internal set; }
    public int Goods { get; internal set; }
    internal Dictionary<string,int> Stock { get; } = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string,int> CommodityStock { get; }
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
        CommodityStock = new ReadOnlyDictionary<string,int>(Stock);
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
    internal Dictionary<string,int> Stock { get; } = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string,int> CommodityStock { get; }
    public int ToolWearHours { get; internal set; }
    public long WagesPaid { get; internal set; }
    public long RetailRevenue { get; internal set; }
    public long WholesaleRevenue { get; internal set; }
    public long WholesaleSpending { get; internal set; }
    public int RetailSales { get; internal set; }
    internal int LastRetailSalesAtDay { get; set; }
    internal int ServiceHoursBudget { get; set; } = 2;
    internal int ServiceHoursWorkedToday { get; set; }
    public int WorkHours { get; internal set; }
    public int ItemsCrafted { get; internal set; }
    public int FoodProduced { get; internal set; }
    public int MaterialsExtracted { get; internal set; }
    public int LandYieldRemaining { get; internal set; }
    public int ForageRemaining { get; internal set; }
    public int MineralReserve { get; internal set; }
    public int WorkerCount { get; internal set; }
    public int CargoCapacity { get; internal set; }
    public long CargoHandled { get; internal set; }
}

public sealed record SettlementSettings
{
    public int Seed { get; init; } = 42;
    public EconomyMode Economy { get; init; } = EconomyMode.Prototype;
    // Economic constants are simulation hypotheses, not ESO measurements.
    public int RenewableFoodPerWorkHour { get; init; } = 3;
    public int RawMaterialPerWorkHour { get; init; } = 2;
    public int DailyLandRegrowthPerResident { get; init; } = 4;
    public int InitialMineralReservePerSite { get; init; } = 3000;
    public int InitialOutsideGold { get; init; } = 1_200_000;
    public int InitialOutsideFood { get; init; } = 100_000;
    public int InitialOutsideMaterials { get; init; } = 50_000;
    public int InitialPublicGoldPerResident { get; init; } = 10;
    public int DailyForeignFoodDemandPerResident { get; init; } = 2;
    public int DailyForeignGoodsDemandPerResident { get; init; } = 1;
    public int DailyImportCapPerTenResidents { get; init; } = 2;
    public int FoodExportPrice { get; init; } = 3;
    public int GoodsExportPrice { get; init; } = 8;
    public int ExternalFoodImportPrice { get; init; } = 1;
    public int ExternalMaterialImportPrice { get; init; } = 2;
    public int FoodSalesTax { get; init; } = 1;
    public int GoodsSalesTax { get; init; } = 2;
    public int DailyAssistanceLimit { get; init; } = 8;
    public bool EnableLocalProduction { get; init; } = true;
    public bool EnableExternalTrade { get; init; } = true;
    public bool EnablePublicAssistance { get; init; } = true;
    public bool EnableHousing { get; init; } = true;
    public int DailySharedRent { get; init; } = 2;
    public int PortCargoUnitsPerWorkHour { get; init; } = 50;
    public int MaxPortCargoCapacity { get; init; } = 2000;
    public int MaxFreightLoad { get; init; } = 40;
    public int ToolWorkHoursPerWear { get; init; } = 24;
    public int FirewoodDaysPerHousehold { get; init; } = 5;
    public int MaxCommodityStockPerSite { get; init; } = 160;
    public int PublicForagePerFarmPerDay { get; init; } = 8;
    public int BaseFreightFee { get; init; } = 1;
    public int FreightFeePer90Minutes { get; init; } = 1;
    public int MealPrice { get; init; } = 6;
    public int GoodsPrice { get; init; } = 12;
    public int FoodWholesalePrice { get; init; } = 2;
    public int MaterialWholesalePrice { get; init; } = 3;
    public int GoodsWholesalePrice { get; init; } = 6;
    public int HourlyWage { get; init; } = 3;
    public int FoodImportPerResident { get; init; } = 2;
    public int MaterialImportPerTenResidents { get; init; } = 3;
    public int BaseTravelMinutes { get; init; } = 18;
    public int LocalTravelMinutes { get; init; } = 4;
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
    long DividendsPaid,
    int CargoCapacity = 0, long CargoHandled = 0,
    IReadOnlyDictionary<string,int>? CommodityInventory = null);

public sealed record FreightRouteSnapshot(
    string FromId, string From, string ToId, string To, string Commodity, long Units);

public sealed record CommoditySnapshot(
    string Commodity, long Initial, long Extracted, long Crafted,
    long UsedAsIngredient, long Consumed, long StockOnHand, long InTransit);

public sealed record SettlementReport
{
    public required string Settlement { get; init; }
    public required int SourcePlaces { get; init; }
    public int SimulatedPlaces { get; init; }
    public int SimulatedBusinesses { get; init; }
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
    public string Economy { get; init; } = "prototype";
    public int SimulatedContracts { get; init; }
    public long FoodProducedLocally { get; init; }
    public long FoodHarvestedForSubsistence { get; init; }
    public long RawMaterialsExtracted { get; init; }
    public long FoodExported { get; init; }
    public long GoodsExported { get; init; }
    public long MaterialsExported { get; init; }
    public long FoodBoughtExternally { get; init; }
    public long MaterialsBoughtExternally { get; init; }
    public long GoldPaidAbroad { get; init; }
    public long GoldReceivedFromAbroad { get; init; }
    public long AssistanceGoldPaid { get; init; }
    public int AssistedResidents { get; init; }
    public long RevenueTaxed { get; init; }
    public long CooperativeDistributions { get; init; }
    public long PublicGold { get; init; }
    public long ExternalGold { get; init; }
    public long InitialAllAccountsGold { get; init; }
    public long AllAccountsGold { get; init; }
    public int ExternalFood { get; init; }
    public int ExternalMaterials { get; init; }
    public long TotalFoodIncludingExternal { get; init; }
    public long TotalMaterialsIncludingExternal { get; init; }
    public int FoodProducers { get; init; }
    public int ResourceProducers { get; init; }
    public int ResidentsFurloughed { get; init; }
    public double PersonHoursAtZeroSatiation { get; init; }
    public double PersonHoursBelow20Satiation { get; init; }
    public long WorkersFurloughedCumulative { get; init; }
    public long JobsReassigned { get; init; }
    public int HousingUnits { get; init; }
    public int HousingBeds { get; init; }
    public int HousingOccupied { get; init; }
    public int HomelessResidents { get; init; }
    public long RentCollected { get; init; }
    public int TotalEvictions { get; init; }
    public int TotalRelocations { get; init; }
    public int TotalRehousings { get; init; }
    public long ShipmentsSent { get; init; }
    public long ShipmentsDelivered { get; init; }
    public long FreightUnitsDelivered { get; init; }
    public int ShipmentsInTransit { get; init; }
    public int FoodInTransit { get; init; }
    public int MaterialsInTransit { get; init; }
    public int GoodsInTransit { get; init; }
    public long FreightFees { get; init; }
    public FreightRouteSnapshot[] FreightRoutes { get; init; } = [];
    public CommoditySnapshot[] CommodityDetails { get; init; } = [];
    public long RecipesCompleted { get; init; }
    public long ToolsWornOut { get; init; }
    public long FirewoodBurned { get; init; }
    public long FreightDelayedForGear { get; init; }
    public long IdleProductionHoursAvoided { get; init; }
    public long ScarcityPricePremiumGold { get; init; }
    public long SurplusPriceDiscountGold { get; init; }
}
