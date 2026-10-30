using System.Diagnostics;
using System.Text.Json;
using AClockworkAurbis.Simulation;

try
{
    string? catalogPath=null,trace=null;
    int days=3,seed=42;
    bool traceFirst=false,details=false;
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
            case "--help":
                Console.WriteLine(
                    "dotnet run -c Release --project tools/SettlementRunner -- "+
                    "--catalog catalog.json [--days 3] [--seed 42] [--trace-first] "+
                    "[--trace npc:uesp-online:167019] [--details]");
                return 0;
            default:throw new ArgumentException("Unknown parameter "+args[i]);
        }
    }
    if(catalogPath is null||days is <0 or >365)
        throw new ArgumentException("--catalog FILE required; --days must be 0..365");
    var loading=Stopwatch.StartNew();
    var catalog=SettlementCatalog.Load(catalogPath);
    var world=new SettlementWorld(catalog,new SettlementSettings{Seed=seed});
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
        Seed=seed,
        summary.Settlement,
        summary.SourcePlaces,
        summary.Businesses,
        summary.Residents,
        summary.SourceHomeAssignments,
        summary.SourceStoreAssignments,
        summary.InferredWorkAssignments,
        summary.SyntheticHomeCount,
        summary.UnemployedResidents,
        summary.Events,
        summary.Wages,
        summary.Dividends,
        summary.UnpaidHours,
        summary.RetailFoodSales,
        summary.RetailGoodsSales,
        summary.Meals,
        summary.Travel,
        summary.CraftedGoods,
        summary.GoodsUsed,
        summary.FoodImported,
        summary.MaterialsImported,
        summary.InitialGold,
        summary.TotalGold,
        summary.FoodRemaining,
        summary.MaterialsRemaining,
        summary.GoodsRemaining,
        LoadingMs=Math.Round(loading.Elapsed.TotalMilliseconds,2),
        SimulationMs=Math.Round(timer.Elapsed.TotalMilliseconds,2),
        GoldConserved=summary.TotalGold==summary.InitialGold,
        FoodLedgerValid=summary.FoodRemaining+summary.Meals==
            summary.InitialFood+summary.FoodImported,
        MaterialLedgerValid=summary.MaterialsRemaining+summary.GoodsRemaining+summary.GoodsUsed==
            summary.InitialMaterials+summary.InitialGoods+summary.MaterialsImported,
        BusinessesDetail=details?summary.BusinessDetails:
            summary.BusinessDetails.Where(x=>x.RetailSales>0||x.WorkHours>0||
                x.ItemsCrafted>0).Take(12).ToArray(),
        ExampleResidents=world.Residents.Where(r=>r.WorkId!=null)
            .Take(5).Select(r=>new {
                r.Source.Name,
                SourceRole=r.Source.Profession,
                r.Source.WorkEvidence,
                Home=r.Source.HomePlaceId,
                Workplace=world.Businesses.FirstOrDefault(b=>b.Id==r.WorkId)?.Name,
                r.Gold,r.Food,r.Goods,r.Satiation,r.Energy,r.MealsEaten,r.Purchases,
                r.WageIncome,r.DividendIncome,r.WorkMinutes
            }).ToArray(),
        Assumptions="Gold, shop stock, imports, prices, travel duration, needs and "+
            "unknown homes are simulation defaults; operator links are not ownership"
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
