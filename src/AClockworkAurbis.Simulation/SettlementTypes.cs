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
            if(resident.SimulatedStartingItems is not null &&
                resident.SimulatedStartingItems.Any(x=>
                    !CommodityRecipes.Prices.ContainsKey(x.Key) || x.Value<0))
                throw new InvalidDataException("Invalid simulated starting commodity stock");
        }
        foreach (var business in Businesses)
        {
            if (business.OperatorNpcIds is null)
                throw new InvalidDataException("Business operator list cannot be null");
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
    public const string Haulier = "haulier";
    public const string Institution = "institution";
    public const string Smeltery = "smeltery";
    public const string Sawmill = "sawmill";
    public const string Tannery = "tannery";
    public const string Bakery = "bakery";
    public const string Brewery = "brewery";
    public const string Tailor = "tailor";
    public const string Hunter = "hunter";
    public const string Fishery = "fishery";
    public const string Leatherworker = "leatherworker";
    public const string GuardPost = "guard_post";
    public const string CleaningService = "cleaning_service";
    public const string DomesticService = "domestic_service";
    public static readonly IReadOnlySet<string> Valid =
        new HashSet<string>(StringComparer.Ordinal)
        { FoodRetail, FoodProducer, ResourceProducer, GoodsRetail, Workshop,
          ImportHub, Haulier, Institution, Smeltery, Sawmill, Tannery,
          Bakery, Brewery, Tailor, Hunter, Fishery, Leatherworker,
          GuardPost, CleaningService, DomesticService };
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
    string? AnchorPlaceId = null, string? AnchorEvidence = null,
    // An explicit scenario/player endowment, never inferred from ESO source data.
    IReadOnlyDictionary<string,int>? SimulatedStartingItems = null);

public enum SettlementAction : byte
{
    Idle, Travel, Work, Eat, Sleep, BuyFood, BuyGoods, HarvestFood,
    BuyGroceries, CookAtHome, CasualWork, PersonalGather, PersonalCraft,
    PersonalBuy, PersonalSell, VisitTavern
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
    public decimal RentSpent { get; internal set; }
    // Source-backed job links are immutable; contracts are simulation state.
    public string? WorkId => OnUnpaidLeave ? null : AssignedWorkPlaceId ?? Source.WorkPlaceId;
    public bool OnUnpaidLeave { get; internal set; }
    public int InsolvencyJobChanges { get; internal set; }
    public string? AssignedWorkPlaceId { get; internal set; }
    public string EmploymentEvidence { get; internal set; } = "no_known_employment";
    public int SubsistenceFoodHarvested { get; internal set; }
    public double MinutesAtZeroSatiation { get; internal set; }
    public double MinutesBelow20Satiation { get; internal set; }
    public decimal AssistanceGold { get; internal set; }
    public decimal CooperativeIncome { get; internal set; }
    public string CurrentPlaceId { get; internal set; }
    public string DestinationPlaceId { get; internal set; }
    public SettlementAction Activity { get; internal set; }
    public long ActionStart { get; internal set; }
    public long LastUpdate { get; internal set; }
    public int WorkStart { get; }
    // Stable per-person inclination; 0 chooses the best scored option.
    public int RiskTolerance { get; internal set; }
    public long FoodChoiceCount { get; internal set; }
    public int? CourierShipmentId { get; internal set; }
    public long CourierDeliveries { get; internal set; }
    public decimal CourierWageIncome { get; internal set; }
    internal string? PlannedMealShopId { get; set; }
    internal string? PlannedMealSku { get; set; }
    public decimal Gold { get; internal set; }
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
    public string? SimulatedServiceRole { get; internal set; }
    public string? ServiceRoleEvidence { get; internal set; }
    public int HomeMealsCooked { get; internal set; }
    public int HomeCookingBatches { get; internal set; }
    public int GroceryKitsPurchased { get; internal set; }
    internal long LastFailedGrocerySearchDay { get; set; } = -1;
    public decimal WageIncome { get; internal set; }
    public decimal CasualWageIncome { get; internal set; }
    public int CasualHoursToday { get; internal set; }
    public int RegularHoursToday { get; internal set; }
    public string? PersonalTask { get; internal set; }
    public string? AutonomousGoal { get; internal set; }
    public string? AutonomousLastReason { get; internal set; }
    public long AutonomousChoices { get; internal set; }
    public long AutonomousCompleted { get; internal set; }
    public long AutonomousFailed { get; internal set; }
    internal int AutonomousDay { get; set; } = -1;
    internal int AutonomousActionsToday { get; set; }
    internal long LastAutonomousConsideredMinute { get; set; } = -10000;
    public string? EquippedWeaponSku { get; internal set; }
    public long PersonalGathered { get; internal set; }
    public long PersonalCrafts { get; internal set; }
    public decimal PersonalSaleIncome { get; internal set; }
    internal Dictionary<string,int> PersonalToolWorkHours { get; } =
        new(StringComparer.Ordinal);
    public decimal DividendIncome { get; internal set; }
    public long UnpaidHours { get; internal set; }
    public long WorkMinutes { get; internal set; }
    public long SleepMinutes { get; internal set; }
    public long JourneyMinutes { get; internal set; }
    internal long LastGoodsShoppingDay { get; set; } = -10;
    internal long LastTavernDay { get; set; } = -10;
    public int TavernVisits { get; internal set; }
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
    public decimal DividendsPaid { get; internal set; }
    public IReadOnlyList<string> Operators => Definition.OperatorNpcIds;
    public decimal Gold { get; internal set; }
    public int Food { get; internal set; }
    public int Materials { get; internal set; }
    public int Goods { get; internal set; }
    internal Dictionary<string,int> Stock { get; } = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string,int> CommodityStock { get; }
    internal Dictionary<string,long> InventoryValueMilli { get; } =
        new(StringComparer.Ordinal);
    internal Dictionary<string,long> SoldBySku { get; } =
        new(StringComparer.Ordinal);
    internal Dictionary<string,long> LastPlannedSales { get; } =
        new(StringComparer.Ordinal);
    internal Dictionary<string,decimal> RecentDemand { get; } =
        new(StringComparer.Ordinal);
    public string? PlannedProduct { get; internal set; }
    public decimal PlannedMargin { get; internal set; }
    public int PlannedHours { get; internal set; } = 8;
    internal int PlannedHoursWorked { get; set; }
    internal int LastPlannedDay { get; set; } = -1;
    internal long LastFurloughDay { get; set; } = -100000;
    internal decimal LastObservedSalesMargin { get; set; }
    internal Queue<decimal> PrivateRecentMargin { get; } = new();
    internal decimal PrivateRecentMarginTotal { get; set; }
    internal int PublicHoursBudgetToday { get; set; }
    internal int PublicHoursWorkedToday { get; set; }
    internal int PublicHoursReservedToday { get; set; }
    internal int ConsecutiveUnfundedPublicDays { get; set; }
    public decimal PublicPayrollGrantsReceived { get; internal set; }
    public int CapitalLevel { get; internal set; } = 1;
    public long CapitalInvestedMilli { get; internal set; }
    internal int CapitalWorkCredit { get; set; }
    internal bool RequestedCapital { get; set; }
    internal int LastCapitalInvestmentDay { get; set; } = -50;
    internal int LastCapitalMaintenanceDay { get; set; } = -1;
    public int ToolWearHours { get; internal set; }
    public decimal WagesPaid { get; internal set; }
    public decimal RetailRevenue { get; internal set; }
    public long RetailCostOfGoodsMilli { get; internal set; }
    internal long LastRetailCostOfGoodsMilliAtDay { get; set; }
    public decimal WholesaleRevenue { get; internal set; }
    public long WholesaleCostOfGoodsMilli { get; internal set; }
    public decimal WholesaleSpending { get; internal set; }
    // Includes paid delivery charges. Used to quote bread relative to its
    // actual acquisition cost rather than an arbitrary universal price.
    public decimal BreadLandedGold { get; internal set; }
    public long BreadLandedUnits { get; internal set; }
    public decimal BreadRevenueGold { get; internal set; }
    public long BreadSalesUnits { get; internal set; }
    public int RetailSales { get; internal set; }
    internal int LastRetailSalesAtDay { get; set; }
    internal decimal LastRetailRevenueAtDay { get; set; }
    internal long LastBreadSalesAtDay { get; set; }
    internal int ServiceHoursBudget { get; set; } = 2;
    internal int ServiceHoursWorkedToday { get; set; }
    internal int ServiceHoursReservedToday { get; set; }
    public int WorkHours { get; internal set; }
    public int CasualHoursWorked { get; internal set; }
    public int ItemsCrafted { get; internal set; }
    public int FoodProduced { get; internal set; }
    public int MaterialsExtracted { get; internal set; }
    public int LandYieldRemaining { get; internal set; }
    public int ForageRemaining { get; internal set; }
    public int MineralReserve { get; internal set; }
    public int WorkerCount { get; internal set; }
    public int CargoCapacity { get; internal set; }
    public long CargoHandled { get; internal set; }
    internal int CargoHoursReservedToday { get; set; }
    internal long LastCargoHandledAtDay { get; set; }
    internal Queue<int> RecentCargoHandled { get; } = new();
    internal int RecentCargoHandledTotal { get; set; }
    public decimal FreightRevenue { get; internal set; }
    public long FreightShipments { get; internal set; }
    public long FreightUnitsTransported { get; internal set; }
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
    // Detailed commodity prices are fractional, so detailed markets use
    // independently tunable ad-valorem rates instead of per-unit tax integers.
    public decimal DetailedFoodTaxRate { get; init; } = 0.05m;
    public decimal DetailedGoodsTaxRate { get; init; } = 0.10m;
    public int DailyAssistanceLimit { get; init; } = 8;
    public bool EnableBudgetedPublicEmployment { get; init; } = true;
    public int PublicPayrollHorizonDays { get; init; } = 14;
    public int PublicMinimumPaidHoursPerWorker { get; init; } = 1;
    public int PublicTargetPaidHoursPerWorker { get; init; } = 2;
    public int PublicUnfundedLeaveDays { get; init; } = 14;
    public bool EnableSustainableHiring { get; init; } = true;
    public int PrivateHireRunwayDays { get; init; } = 14;
    public int PrivateRehireCooldownDays { get; init; } = 30;
    // Paid casual work never replaces priority permanent farm/mine shifts.
    public int RegularGuaranteedHoursPerDay { get; init; } = 2;
    public int CasualHourlyWage { get; init; } = 2;
    public int MaxCasualHoursPerDay { get; init; } = 4;
    // Finite renewable wild commons per person in each local neighbourhood.
    // They are not the crops growing on commercial or source-named farms.
    public int WildForagePerResidentPerDay { get; init; } = 2;
    // Independent wilderness supplies do not consume employer-owned reserves.
    public int OpenOrePerResident { get; init; } = 8;
    public int OpenLogsPerResidentPerDay { get; init; } = 2;
    public int OpenFishPerResidentPerDay { get; init; } = 1;
    public int OpenGamePerResidentPerDay { get; init; } = 1;
    public int MaxPersonalCommodityStock { get; init; } = 100;
    // Personal economic initiative is opt-in per scenario and deterministic
    // per resident. It never changes the resource/transaction mechanics.
    public bool EnableAutonomousPersonalEconomy { get; init; } = true;
    // The former bare-world scenario remains available for regression tests.
    public bool EnableEstablishedOpeningEconomy { get; init; } = false;
    public bool EnableRiskAwareChoices { get; init; } = true;
    public int AutonomousParticipationPercent { get; init; } = 22;
    public int AutonomousDailyActionLimit { get; init; } = 2;
    public int AutonomousMaxTravelMinutes { get; init; } = 130;
    public decimal AutonomousToolPurchaseGoldReserve { get; init; } = 20m;
    public bool EnableLocalProduction { get; init; } = true;
    public bool EnableExternalTrade { get; init; } = true;
    public bool EnablePublicAssistance { get; init; } = true;
    public bool EnableHousing { get; init; } = true;
    public int DailySharedRent { get; init; } = 2;
    // Pay rent only when enough cash remains after payment. Nobody is
    // evicted for lack of gold; beds and household stock remain physical.
    public int RentGoldReserve { get; init; } = 20;
    public int PortCargoUnitsPerWorkHour { get; init; } = 50;
    public bool EnablePaidCarriers { get; init; } = true;
    // Courier travel shares the ordinary NPC clock, and freight cannot arrive
    // before its assigned person completes pickup and destination journeys.
    public bool EnableNpcCourierJourneys { get; init; } = true;
    public int HaulierCargoUnitsPerWorkHour { get; init; } = 50;
    public int MaxPortCargoCapacity { get; init; } = 2000;
    public int MaxHaulierCargoCapacity { get; init; } = 500;
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
    decimal Gold, int Food, int Materials, int Goods,
    int RetailSales, int WorkHours, int ItemsCrafted, decimal WagesPaid,
    decimal DividendsPaid,
    int CargoCapacity = 0, long CargoHandled = 0,
    IReadOnlyDictionary<string,int>? CommodityInventory = null,
    int CapitalLevel=1,long CapitalInvestedMilli=0,
    decimal PublicPayrollGrants=0m,int PublicHoursBudgetToday=0,
    int CasualHoursWorked=0,decimal FreightRevenue=0m,
    long FreightShipments=0,long FreightUnitsTransported=0);

public sealed record CapitalDecisionSnapshot(
    int Day,string BusinessId,string BusinessName,string Decision,
    decimal ExpectedPaybackDays,string Reason);

public sealed record CapitalProjectSnapshot(
    string InvestorId,string InvestorName,string BuilderName,
    int CompletedHours,int RequiredHours,long MaterialCostMilli,
    decimal LabourPayment);

public sealed record AutonomousActivitySnapshot(
    string Activity,long Chosen,long Completed,long Failed);

public sealed record BusinessPlanSnapshot(
    string BusinessId,string BusinessName,int Day,string? Product,
    int BudgetedHours,decimal EstimatedBatchMargin,string Reason);

public sealed record MarketQuoteSnapshot(
    string BusinessId,string BusinessName,string BusinessKind,
    string Commodity,int Stock,long UnitCostMilli,
    decimal WholesaleAsk,decimal RetailPrice);

public sealed record BreadMarketSnapshot(
    string BusinessId,string BusinessName,string District,
    int BreadInStock,decimal PricePerLoaf,bool TwoForOneAvailable,
    long PurchasedLoaves,decimal LandedCostGold,
    long LoavesSold,decimal BreadRevenueGold);

public sealed record CourierJourneySnapshot(
    int ShipmentId,string CourierId,string CourierName,string CarrierId,
    string FromId,string FromName,string ToId,string ToName,
    string Commodity,int Quantity,decimal CourierWageGold,string Stage,
    long AssignedMinute,long? PickupMinute,long? ExpectedDeliveryMinute,
    long? DeliveredMinute,
    double? FromMapX,double? FromMapY,
    double? ToMapX,double? ToMapY);

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
    public required decimal Wages { get; init; }
    public required decimal Dividends { get; init; }
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
    public required decimal InitialGold { get; init; }
    public required decimal TotalGold { get; init; }
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
    public long CasualWorkHours { get; init; }
    public decimal CasualWages { get; init; }
    public int CasualWorkers { get; init; }
    public int WildForageSites { get; init; }
    public long PersonalGatherHours { get; init; }
    public long PersonalCraftBatches { get; init; }
    public long PersonalTrades { get; init; }
    public decimal PersonalSaleIncome { get; init; }
    public int OpenResourceSites { get; init; }
    public int AutonomousParticipants { get; init; }
    public long AutonomousChoices { get; init; }
    public long AutonomousCompleted { get; init; }
    public long AutonomousFailed { get; init; }
    public AutonomousActivitySnapshot[] AutonomousActivities { get; init; } = [];
    public long RawMaterialsExtracted { get; init; }
    public long FoodExported { get; init; }
    public long GoodsExported { get; init; }
    public long MaterialsExported { get; init; }
    public long FoodBoughtExternally { get; init; }
    public long MaterialsBoughtExternally { get; init; }
    public decimal GoldPaidAbroad { get; init; }
    public decimal GoldReceivedFromAbroad { get; init; }
    public decimal AssistanceGoldPaid { get; init; }
    public int AssistedResidents { get; init; }
    public decimal RevenueTaxed { get; init; }
    public long TavernVisits { get; init; }
    public decimal TavernGoldSpent { get; init; }
    public long ClothingWornOut { get; init; }
    public long ClothingPurchased { get; init; }
    public decimal RetailSalesTaxRevenue { get; init; }
    public decimal CooperativeDistributions { get; init; }
    public decimal PublicGold { get; init; }
    public decimal ExternalGold { get; init; }
    public decimal InitialAllAccountsGold { get; init; }
    public decimal AllAccountsGold { get; init; }
    public int ExternalFood { get; init; }
    public int ExternalMaterials { get; init; }
    public long TotalFoodIncludingExternal { get; init; }
    public long TotalMaterialsIncludingExternal { get; init; }
    public int FoodProducers { get; init; }
    public int ResourceProducers { get; init; }
    public int ResidentsFurloughed { get; init; }
    public double PersonHoursAtZeroSatiation { get; init; }
    public double PersonHoursBelow20Satiation { get; init; }
    public decimal PublicPayrollGrants { get; init; }
    public long FundedPublicWorkHours { get; init; }
    public int ActivePublicEmployees { get; init; }
    public int UnfundedPublicInstitutions { get; init; }
    public long WorkersFurloughedCumulative { get; init; }
    public long JobsReassigned { get; init; }
    public int HousingUnits { get; init; }
    public int HousingBeds { get; init; }
    public int HousingOccupied { get; init; }
    public int HomelessResidents { get; init; }
    public decimal RentCollected { get; init; }
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
    public decimal FreightFees { get; init; }
    public decimal CarrierRevenue { get; init; }
    public long PaidFreightShipments { get; init; }
    public long CarrierCapacityHours { get; init; }
    public long UnfulfilledFreightRequests { get; init; }
    public long CourierAssignments { get; init; }
    public long CourierDeliveries { get; init; }
    public decimal CourierWages { get; init; }
    public decimal CourierWagesInEscrow { get; init; }
    public int ActiveCourierJourneys { get; init; }
    public CourierJourneySnapshot[] CourierJourneys { get; init; } = [];
    public FreightRouteSnapshot[] FreightRoutes { get; init; } = [];
    public CommoditySnapshot[] CommodityDetails { get; init; } = [];
    public long RecipesCompleted { get; init; }
    public long ToolsWornOut { get; init; }
    public long FirewoodBurned { get; init; }
    public long FreightDelayedForGear { get; init; }
    public long IdleProductionHoursAvoided { get; init; }
    public decimal MarketWholesaleRevenue { get; init; }
    public long MarketWholesaleCostMilli { get; init; }
    public decimal MarketRetailRevenue { get; init; }
    public long MarketRetailCostMilli { get; init; }
    public long UnfilledCommodityOrderUnits { get; init; }
    public long MarketTrades { get; init; }
    public long BusinessPlansMade { get; init; }
    public long BusinessPlansRevised { get; init; }
    public long LossMakingPlans { get; init; }
    public BusinessPlanSnapshot[] BusinessPlans { get; init; } = [];
    public long CapitalProjectsStarted { get; init; }
    public long CapitalProjectsCompleted { get; init; }
    public long CapitalUpgradesWorn { get; init; }
    public long CapitalMaintenanceUnits { get; init; }
    public long CapitalMaterialsConsumed { get; init; }
    public decimal ConstructionLabourPayments { get; init; }
    public CapitalProjectSnapshot[] CapitalProjects { get; init; } = [];
    public CapitalDecisionSnapshot[] CapitalDecisions { get; init; } = [];
    public MarketQuoteSnapshot[] MarketQuotes { get; init; } = [];
    public decimal ScarcityPricePremiumGold { get; init; }
    public decimal SurplusPriceDiscountGold { get; init; }
    public long HomeCookingBatches { get; init; }
    public long HomeCookedMeals { get; init; }
    public long ChefCookedMeals { get; init; }
    public long OneSeptimBreadSold { get; init; }
    public long BreadUnitsSold { get; init; }
    public decimal BreadSalesRevenue { get; init; }
    public long DiscountedBreadUnits { get; init; }
    public int OneSeptimBreadMarkets { get; init; }
    public BreadMarketSnapshot[] BreadMarkets { get; init; } = [];
    public long GroceryKitsSold { get; init; }
    public int SourceChefsEmployed { get; init; }
    public int SimulatedGuards { get; init; }
    public int SimulatedServants { get; init; }
    public int SimulatedCleaners { get; init; }
}
