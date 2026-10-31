using System.Diagnostics;
using System.Text.Json;
using AClockworkAurbis.Simulation;

try
{
    string? catalogPath=null,trace=null;
    int days=3,seed=42;
    string? economicMode=null;
    bool traceFirst=false,details=false,allResidents=false;
    bool disableTrade=false,disableProduction=false,disableAid=false;
    for(int i=0;i<args.Length;i++)
    {
        string Next() => i+1<args.Length ? args[++i] :
            throw new ArgumentException("Missing value after "+args[i]);
        switch(args[i])
        {
            case "--catalog":catalogPath=Next();break;
            case "--days":days=int.Parse(Next());break;
            case "--seed":seed=int.Parse(Next());break;
            case "--trace":trace=Next();break;
            case "--trace-first":traceFirst=true;break;
            case "--details":details=true;break;
            case "--resident-details":allResidents=true;break;
            case "--economy":economicMode=Next();break;
            case "--no-foreign-trade":disableTrade=true;break;
            case "--no-local-production":disableProduction=true;break;
            case "--no-assistance":disableAid=true;break;
            case "--help":
                Console.WriteLine(
                    "dotnet run -c Release --project tools/SettlementRunner -- "+
                    "--catalog catalog.json [--days 3] [--seed 42] [--trace-first] "+
                    "[--economy prototype|production] [--no-foreign-trade] "+
                    "[--no-local-production] [--no-assistance] [--trace NPC_ID] [--details] [--resident-details]");
                return 0;
            default:throw new ArgumentException("Unknown parameter "+args[i]);
        }
    }
    if(catalogPath is null||days is <0 or >365)
        throw new ArgumentException("--catalog FILE required; --days must be 0..365");
    var loading=Stopwatch.StartNew();
    var catalog=SettlementCatalog.Load(catalogPath);
    if(economicMode is not null && economicMode is not ("production" or "prototype"))
        throw new ArgumentException("--economy must be production or prototype");
    EconomyMode mode=economicMode=="production" ||
        economicMode is null && catalog.Scope=="whole_zone"
            ? EconomyMode.RegionalProduction : EconomyMode.Prototype;
    var world=new SettlementWorld(catalog,new SettlementSettings
    {Seed=seed,Economy=mode,EnableExternalTrade=!disableTrade,
        EnableLocalProduction=!disableProduction,EnablePublicAssistance=!disableAid});
    loading.Stop();
    if(traceFirst && world.Residents.Count>0)trace=world.Residents[0].Source.Id;
    if(trace is not null)
    {
        if(!world.Residents.Any(r=>r.Source.Id==trace))
            throw new ArgumentException("Unknown NPC trace ID "+trace);
        string selected=trace;
        world.Observer=eventInfo=>
        {
            if(eventInfo.NpcId!=selected)return;
            Console.Error.WriteLine(
                $"D{eventInfo.Minute/1440+1} "+
                $"{eventInfo.Minute%1440/60:D2}:{eventInfo.Minute%60:D2} "+
                $"{eventInfo.Action,-9} {eventInfo.Detail}");
        };
    }
    var timer=Stopwatch.StartNew();
    world.AdvanceTo(days*1440L);
    timer.Stop();
    world.CheckInvariants();
    var summary=world.Report();
    var report=new
    {
        Mode="Source-backed headless settlement, no Godot or SQLite at runtime",
        Input=Path.GetFullPath(catalogPath),
        SourceData=catalog.Source,
        SimulatedDays=days,
        Economy=mode.ToString(),
        Scenario=disableProduction?"crop-failure":disableTrade?"no-foreign":disableAid?"no-assistance":"normal",
        Seed=seed,
        summary.Settlement,
        summary.SourcePlaces,
        summary.SimulatedPlaces,
        summary.SimulatedBusinesses,
        summary.Businesses,
        summary.Residents,
        summary.SourceHomeAssignments,
        summary.SourceStoreAssignments,
        summary.InferredWorkAssignments,
        summary.SyntheticHomeCount,
        summary.UnemployedResidents,
        summary.SimulatedContracts,
        summary.ResidentsFurloughed,
        summary.PersonHoursAtZeroSatiation,
        summary.PersonHoursBelow20Satiation,
        summary.WorkersFurloughedCumulative,
        summary.JobsReassigned,
        summary.HousingUnits,
        summary.HousingBeds,
        summary.HousingOccupied,
        summary.HomelessResidents,
        summary.RentCollected,
        summary.TotalEvictions,
        summary.TotalRelocations,
        summary.TotalRehousings,
        summary.ShipmentsSent,
        summary.ShipmentsDelivered,
        summary.FreightUnitsDelivered,
        summary.ShipmentsInTransit,
        summary.FoodInTransit,
        summary.MaterialsInTransit,
        summary.GoodsInTransit,
        summary.FreightFees,
        FreightRoutes=details?summary.FreightRoutes.Take(30).ToArray():null,
        summary.ResidentsWithLowSatiation,
        summary.ResidentsWithZeroSatiation,
        summary.ResidentsWithoutFood,
        summary.ResidentsWithoutGold,
        summary.Events,
        summary.Wages,
        summary.Dividends,
        summary.UnpaidHours,
        summary.RetailFoodSales,
        summary.RetailGoodsSales,
        summary.Meals,
        summary.Travel,
        summary.TravelMinutes,
        summary.LongJourneys,
        summary.ResidentsWithMapAnchor,
        summary.PlacesWithSourceMarker,
        summary.PlacesWithEstimatedMarker,
        summary.CraftedGoods,
        summary.GoodsUsed,
        summary.FoodProducedLocally,
        summary.FoodHarvestedForSubsistence,
        summary.RawMaterialsExtracted,
        summary.FoodExported,
        summary.MaterialsExported,
        summary.GoodsExported,
        summary.GoldPaidAbroad,
        summary.GoldReceivedFromAbroad,
        summary.AssistanceGoldPaid,
        summary.AssistedResidents,
        summary.RevenueTaxed,
        summary.CooperativeDistributions,
        summary.PublicGold,
        summary.ExternalGold,
        summary.InitialAllAccountsGold,
        summary.AllAccountsGold,
        summary.ExternalFood,
        summary.ExternalMaterials,
        summary.FoodProducers,
        summary.ResourceProducers,
        summary.FoodImported,
        summary.MaterialsImported,
        summary.InitialGold,
        summary.TotalGold,
        summary.FoodRemaining,
        summary.MaterialsRemaining,
        summary.GoodsRemaining,
        LoadingMs=Math.Round(loading.Elapsed.TotalMilliseconds,2),
        SimulationMs=Math.Round(timer.Elapsed.TotalMilliseconds,2),
        GoldConserved=mode==EconomyMode.Prototype?
            summary.TotalGold==summary.InitialGold :
            summary.AllAccountsGold==summary.InitialAllAccountsGold,
        FoodLedgerValid=summary.FoodRemaining+summary.Meals+
            (mode==EconomyMode.Prototype?0:summary.FoodExported)==
            summary.InitialFood+summary.FoodImported+
            (mode==EconomyMode.Prototype?0:summary.FoodProducedLocally),
        MaterialLedgerValid=summary.MaterialsRemaining+summary.GoodsRemaining+summary.GoodsUsed+
            (mode==EconomyMode.Prototype?0:summary.MaterialsExported+summary.GoodsExported)==
            summary.InitialMaterials+summary.InitialGoods+summary.MaterialsImported+
            (mode==EconomyMode.Prototype?0:summary.RawMaterialsExtracted),
        BusinessesDetail=details?summary.BusinessDetails:
            summary.BusinessDetails.Where(x=>x.RetailSales>0||x.WorkHours>0||
                x.ItemsCrafted>0).Take(12).ToArray(),
        ResidentDetails=allResidents?world.Residents.Select(r=>new {
            Id=r.Source.Id,Name=r.Source.Name,Race=r.Source.Race,
            SourceProfession=r.Source.Profession,
            SourceHomeId=r.Source.HomePlaceId,SourceWorkId=r.Source.WorkPlaceId,
            r.EmploymentEvidence,
            CurrentWorkId=r.WorkId,SimulatedWorkId=r.AssignedWorkPlaceId,
            CurrentWorkplace=world.Businesses.FirstOrDefault(b=>b.Id==r.WorkId)?.Name,
            LastLocation=r.CurrentPlaceId,
            r.HomeId,r.HousingId,r.RentSpent,r.Evictions,r.Relocations,r.Rehousings,
            r.HomelessDays,r.Gold,r.Food,r.Goods,r.Satiation,r.Energy,
            r.MealsEaten,r.Purchases,r.Journeys,r.WageIncome,
            r.DividendIncome,r.CooperativeIncome,r.AssistanceGold,
            r.SubsistenceFoodHarvested,r.UnpaidHours,r.WorkMinutes,
            r.SleepMinutes,r.JourneyMinutes,r.OnUnpaidLeave,r.InsolvencyJobChanges
        }).ToArray():null,
        ExampleResidents=world.Residents.Where(r=>r.WorkId!=null)
            .Take(5).Select(r=>new {
                r.Source.Name,
                SourceRole=r.Source.Profession,
                r.Source.WorkEvidence,
                r.EmploymentEvidence,
                Home=r.Source.HomePlaceId,
                Workplace=world.Businesses.FirstOrDefault(b=>b.Id==r.WorkId)?.Name,
                r.Gold,r.Food,r.Goods,r.Satiation,r.Energy,r.MealsEaten,r.Purchases,
                r.WageIncome,r.DividendIncome,r.CooperativeIncome,r.AssistanceGold,
                r.SubsistenceFoodHarvested,r.WorkMinutes
            }).ToArray(),
        Assumptions="Source identities and map markers are preserved. Contracts, co-ops, "+
            "production yields, prices, welfare, foreign trade, needs and travel "+
            "are simulation assumptions. Vendor association does not establish ownership"
    };
    Console.WriteLine(JsonSerializer.Serialize(report,
        new JsonSerializerOptions{WriteIndented=true}));
    return 0;
}
catch(Exception e)
{
    Console.Error.WriteLine("SETTLEMENT ERROR: "+e);
    return 1;
}
