using System.Text.Json;
using AClockworkAurbis.Simulation;

var tests=new (string Name,Action Run)[]
{
    ("Named places, source employment and unresolved homes", PlaceAndEmployment),
    ("Food, material and goods accounting over 3 days", Economy),
    ("Only real profits fund simulated leaseholder dividends", ProfitShare),
    ("Merchant wages and named destination events", NamedTransactions),
    ("Identical world seeds reproduce exact state", Determinism),
    ("Stepped time gives the same results", Incremental),
    ("Economic shortage cannot mint wages or stock", Shortage),
    ("Missing port is explicitly synthetic outside trade", MissingPort),
    ("Malformed source associations rejected", CatalogValidation),
    ("Whole-island map travel and neighbourhood shopping", IslandTravel),
    ("Different buildings at a shared town marker take local minutes", LocalBuildingTravel),
    ("Regional production needs actual labour and finite natural capacity", RegionalProduction),
    ("Closed regional economy works without automatic imports", ClosedRegion),
    ("Disabling work and imports cannot create food", NoSupply),
    ("External trade funds are finite and count in all ledgers", FiniteForeignMarket),
    ("Crop failure exposes genuine food shortage", CropFailureSensitivity),
    ("Public assistance cannot exceed collected and allocated funds", FundedAssistance),
    ("Finite shared beds and paid rent can produce eviction", FiniteHousing),
    ("Unemployed residents can move towns for solvent jobs", EconomicMigration),
    ("Dock cargo needs paid handling and respects zero capacity", DockLabour),
    ("Competing ports attract cargo from their own productive hinterland", TwoPortCompetition),
    ("Wholesale stock moves as finite, delayed and conserved freight", DelayedFreight),
    ("Specific commodities flow through extraction and crafting recipes", CommodityChains),
    ("Missing smeltery blocks new ingots despite abundant ore", MissingSmeltery),
    ("Commodity world is deterministic with physical freight and consumption", CommodityDeterminism),
    ("No port means no invented depot and less efficient freight", PortlessCommodityRegion),
    ("Idle processing and unsold retail shifts do not burn full payroll", DemandDrivenLabour),
    ("Scarcity pricing and essential household purchases conserve accounts", CommodityPriceAndDemand),
    ("Public foraging cannot bypass finite commercial agriculture", FinitePublicForaging),
    ("One septim bread is a full meal with sustainable producer yields", BreadSeptimBenchmark),
    ("Bread prices respond to local stock and logistics", BreadScarcityPricing),
    ("Fractional septims really settle and all accounts reconcile", FractionalMarketSettlement),
    ("Businesses plan production and labour using observed market data", AdaptiveBusinessPlanning),
    ("Capital upgrades consume funded materials and real construction hours", FundedCapitalConstruction),
    ("Households cook with real ingredients at lower yields than chefs", HouseholdCookingAndChefs),
    ("Deterministic regional simulations can run in time chunks", RegionalDeterminism),
    ("Large 10k actor headless performance and integrity", Stress)
};
int passed=0;
foreach(var (name,action) in tests)
{
    try{action();Console.WriteLine("PASS "+name);passed++;}
    catch(Exception e){Console.Error.WriteLine("FAIL "+name+": "+e);return 1;}
}
Console.WriteLine($"ALL {passed} SETTLEMENT TESTS PASSED");
return 0;

static void Expect(bool okay,string message)
{
    if(!okay)throw new Exception(message);
}
static SettlementCatalog Fixture(int count=30,bool includePort=true,bool production=false)
{
    var places=new List<PlaceDefinition>();
    var businesses=new List<BusinessDefinition>();
    void Add(string id,string label,string kind)
    {
        places.Add(new PlaceDefinition(id,places.Count+1,label,
            kind,"Exampleton","Test zone",kind,"https://example.com/"+id));
        if(kind!="dwelling")
            businesses.Add(new BusinessDefinition(id,kind,null,
                id=="b:tavern"?["npc:example:1"]:[],
                "Fictional settlement fixture"));
    }
    Add("home:1","Test house","dwelling");
    Add("home:2","Test cottage","dwelling");
    Add("b:market","Market Street",BusinessKinds.FoodRetail);
    Add("b:tavern","Ye Olde Test Tavern",BusinessKinds.FoodRetail);
    Add("b:gear","Supplies Store",BusinessKinds.GoodsRetail);
    Add("b:forge","Blacksmith",BusinessKinds.Workshop);
    Add("b:guild","Guildhall",BusinessKinds.Institution);
    if(includePort)Add("b:port","Exampleton Harbour",BusinessKinds.ImportHub);
    if(production){
        Add("sim:fields","Simulated community allotment",BusinessKinds.FoodProducer);
        Add("b:mine","Source-linked ore mine",BusinessKinds.ResourceProducer);
    }
    var residents=new ResidentDefinition[count];
    for(int i=0;i<count;i++)
    {
        string? job=(i%12) switch
        {
            0=>"b:market",1=>"b:tavern",2=>"b:forge",
            3=>"b:port",4=>"b:gear",5=>"b:guild",_=>null
        };
        if(job=="b:port"&&!includePort)job=null;
        residents[i]=new ResidentDefinition(
            "npc:example:"+i,i+100,"Test citizen "+i,
            "Altmer",job is null?null:"Worker",
            i%5==0?"home:1":null,i%5==0?"explicit_npc_house":"unknown",
            job,job is null?"unknown":"explicit_npc_store",
            job is null?[]:[job],null);
    }
    return new SettlementCatalog(1,"Exampleton","Synthetic test fixture",
        default,places.ToArray(),businesses.ToArray(),residents);
}
static SettlementReport Run(int count,int days=3,SettlementSettings? settings=null)
{
    var world=new SettlementWorld(Fixture(count),settings);
    world.AdvanceTo(days*1440L);
    world.CheckInvariants();
    return world.Report();
}
static void PlaceAndEmployment()
{
    var world=new SettlementWorld(Fixture());
    Expect(world.Residents.Count==30,"source resident total");
    Expect(world.Businesses.Count==6,"distinct source shops and institutions");
    Expect(world.Residents.First(r=>r.Source.Id=="npc:example:0").HomeId=="home:1","explicit home preserved");
    Expect(world.Residents.Any(r=>r.HomeId.StartsWith("sim:unmapped-home:")),
        "unknown homes marked as simulated instead of silently invented source addresses");
    Expect(world.Residents.First(r=>r.Source.Id=="npc:example:0").WorkId=="b:market","merchant assigned to correct shop");
    Expect(world.Residents.First(r=>r.Source.Id=="npc:example:2").WorkId=="b:forge","blacksmith stays at forge");
    Expect(world.Businesses.First(b=>b.Id=="b:tavern").SimulatedLeaseholderId=="npc:example:1",
        "known vendor may be explicitly marked as simulated leaseholder");
    Expect(world.Businesses.All(b=>b.OwnerId is null),
        "vendor presence never fabricates legal ownership");
}
static void Economy()
{
    var report=Run(120);
    Expect(report.Residents==120 && report.Businesses==6,"population and business counts");
    Expect(report.ResidentsWithZeroSatiation>=0 && report.ResidentsWithZeroSatiation<=120,
        "well-being snapshot bounded by population");
    Expect(report.Wages>0 && report.RetailFoodSales>0 && report.Meals>0,
        "real wages and food purchases occur");
    Expect(report.RetailGoodsSales>0 && report.CraftedGoods>0,
        "goods sold and workshop production occur");
    Expect(report.FoodImported>0 && report.MaterialsImported>0,
        "external supply explicitly counted");
    Expect(report.InitialGold==report.TotalGold,"all gold remains in an account");
    Expect(report.FoodRemaining+report.Meals==report.InitialFood+report.FoodImported,
        "all imported food accounted for");
    Expect(report.MaterialsRemaining+report.GoodsRemaining+report.GoodsUsed==
        report.InitialMaterials+report.InitialGoods+report.MaterialsImported,
        "one material makes one good; none appears from nowhere");
    Expect(report.BusinessDetails.Any(b=>b.Name=="Ye Olde Test Tavern"&&b.RetailSales>0),
        "a real named source place actually handles transactions");
    Expect(report.GoodsUsed==0,"goods upkeep begins only after several days");
    Expect(report.BusinessDetails.Any(b=>b.Name=="Blacksmith"&&b.ItemsCrafted>0),
        "crafts happen at the named workshop");
}
static void ProfitShare()
{
    var world=new SettlementWorld(Fixture(120),new SettlementSettings
    {
        MealPrice=15,StartingBusinessGold=100
    });
    world.AdvanceTo(1440*8L);
    world.CheckInvariants();
    var report=world.Report();
    var tavern=world.Businesses.First(b=>b.Id=="b:tavern");
    var resident=world.Residents.First(r=>r.Source.Id=="npc:example:1");
    Expect(report.Dividends==world.Businesses.Sum(b=>b.DividendsPaid),
        "dividend sum agrees with businesses");
    Expect(report.Dividends==world.Residents.Sum(r=>r.DividendIncome),
        "dividend sum agrees with residents");
    Expect(report.Dividends>0&&tavern.DividendsPaid>0,
        "profitable source-linked merchant earns a simulated leaseholder distribution");
    Expect(resident.DividendIncome==tavern.DividendsPaid,
        "dividend actually arrives in linked resident wallet");
    Expect(report.InitialGold==report.TotalGold,"dividends cannot create gold");
}

static void NamedTransactions()
{
    var world=new SettlementWorld(Fixture(24));
    var records=new List<SimObservation>();
    world.Observer=records.Add;
    world.AdvanceTo(1440*3L);
    world.CheckInvariants();
    Expect(records.Any(r=>r.Action=="wages"&&r.Detail.Contains("Market Street")),
        "wages paid by the actual associated merchant shop");
    Expect(records.Any(r=>r.Action=="purchase"&&r.Detail.Contains("Tavern")),
        "food purchased from named tavern");
    Expect(records.Any(r=>r.Action=="travel"&&r.Detail.Contains("->")),
        "travel events retain meaningful source place names");
    for(int i=1;i<records.Count;i++)
        Expect(records[i-1].Minute<=records[i].Minute,"trace chronological");
}
static void Determinism()
{
    var cat=Fixture(95);
    var a=new SettlementWorld(cat);
    var b=new SettlementWorld(cat);
    a.AdvanceTo(1440*5L);
    b.AdvanceTo(1440*5L);
    a.CheckInvariants();b.CheckInvariants();
    Expect(JsonSerializer.Serialize(a.Report())==JsonSerializer.Serialize(b.Report()),
        "reports identical with identical source and world seed");
    for(int i=0;i<a.Residents.Count;i++)
    {
        var x=a.Residents[i];var y=b.Residents[i];
        Expect(x.CurrentPlaceId==y.CurrentPlaceId && x.Gold==y.Gold &&
            x.Food==y.Food && x.Goods==y.Goods &&
            x.Satiation==y.Satiation && x.Energy==y.Energy &&
            x.WorkMinutes==y.WorkMinutes,"individual states deterministic");
    }
}
static void Incremental()
{
    var cat=Fixture(65);
    var whole=new SettlementWorld(cat);
    var stepped=new SettlementWorld(cat);
    whole.AdvanceTo(1440*4L);
    for(long t=0;t<=1440*4;t+=120)
    {
        stepped.AdvanceTo(t);
        stepped.CheckInvariants();
    }
    Expect(JsonSerializer.Serialize(whole.Report())==
        JsonSerializer.Serialize(stepped.Report()),"step/chunk scheduling agrees");
    for(int i=0;i<whole.Residents.Count;i++)
    {
        var a=whole.Residents[i];var b=stepped.Residents[i];
        Expect(Math.Abs(a.Energy-b.Energy)<0.00000001 &&
            a.Gold==b.Gold && a.Food==b.Food &&
            a.CurrentPlaceId==b.CurrentPlaceId,"incremental agents agree");
    }
}
static void Shortage()
{
    var world=new SettlementWorld(Fixture(60),new SettlementSettings
    {
        StartingBusinessGold=0,FoodImportPerResident=0,
        MaterialImportPerTenResidents=0
    });
    world.AdvanceTo(1440*10L);
    world.CheckInvariants();
    var r=world.Report();
    Expect(r.UnpaidHours>0,"unfunded employers record unpaid work");
    Expect(r.BusinessDetails.First(b=>b.Kind==BusinessKinds.Institution).WagesPaid==0,
        "institution with no revenue cannot pay wages");
    Expect(r.CraftedGoods==0,"crafting needs material");
    Expect(r.FoodImported==0 && r.MaterialsImported==0,
        "no food/material appears without imports");
    Expect(r.TotalGold==r.InitialGold,"closed money ledger");
}
static void MissingPort()
{
    var cat=Fixture(20,includePort:false);
    var world=new SettlementWorld(cat);
    world.AdvanceTo(1440*2);
    world.CheckInvariants();
    Expect(world.Businesses.Any(b=>b.Id=="sim:external-trade-depot"),
        "external supplier explicitly marked as synthetic");
}
static void CatalogValidation()
{
    var cat=Fixture(6);
    var invalid=cat with
    {
        Residents=[cat.Residents[0],cat.Residents[0]]
    };
    bool failed=false;
    try {invalid.Validate();}catch(InvalidDataException){failed=true;}
    Expect(failed,"duplicate identities fail validation");
    failed=false;
    try{(cat with{Businesses=[new BusinessDefinition(
        "none",BusinessKinds.FoodRetail,null,[],"")]}).Validate();}
    catch(InvalidDataException){failed=true;}
    Expect(failed,"businesses must reference genuine place IDs");
}
static void IslandTravel()
{
    var fixture=Fixture(24);
    var places=fixture.Places.Select(p=>p.Id switch
    {
        "home:1" => p with {MapX=10000,MapY=10000,MapEvidence="source_map_marker",District="Exampleton"},
        "b:market" => p with {MapX=12000,MapY=10000,MapEvidence="source_map_marker",District="Exampleton"},
        "b:tavern" => p with {MapX=800000,MapY=800000,MapEvidence="source_map_marker",District="Elsewhere"},
        "b:forge" => p with {MapX=30000,MapY=20000,MapEvidence="source_map_marker",District="Exampleton"},
        "b:gear" => p with {MapX=18000,MapY=11000,MapEvidence="source_map_marker",District="Exampleton"},
        _ => p
    }).ToArray();
    var cat=fixture with {Places=places};
    var sim=new SettlementWorld(cat);
    const string resident="npc:example:10";
    int nearby=sim.EstimateTravelMinutes(resident,"b:market");
    int distant=sim.EstimateTravelMinutes(resident,"b:tavern");
    int unknown=sim.EstimateTravelMinutes(resident,"b:guild");
    Expect(nearby<25,"nearby source market short journey");
    Expect(distant>400,"distant island tavern does not take 18 minutes");
    Expect(unknown>nearby+60,"unmapped business does not rank as magically adjacent");
    var trace=new List<SimObservation>();
    sim.Observer=x=>{if(x.NpcId==resident)trace.Add(x);};
    sim.AdvanceTo(1440*3);
    sim.CheckInvariants();
    Expect(sim.Report().TravelMinutes>0,"trip durations accumulated");
    Expect(sim.Report().LongJourneys>0,"extended trips measured");
    Expect(trace.Any(t=>t.Action=="purchase"&&t.Detail.Contains("Market Street")),
        "nearby market chosen in observed purchase events");
    Expect(!trace.Any(t=>t.Action=="purchase"&&t.Detail.Contains("Test Tavern")),
        "resident does not cross whole island to buy identical nearby food");
}

static void RegionalProduction()
{
    var world=new SettlementWorld(Fixture(120,production:true),
        new SettlementSettings{Economy=EconomyMode.RegionalProduction});
    world.AdvanceTo(1440L*30);
    world.CheckInvariants();
    var report=world.Report();
    Expect(report.FoodProducedLocally>0 && report.RawMaterialsExtracted>0,
        "fields and mines produce output only after worker activities");
    Expect(report.FoodProducers==1 && report.ResourceProducers==1,
        "producer sites retain different resource types");
    Expect(report.SimulatedContracts>0,"jobs assigned but source identities unchanged");
    Expect(world.Residents.Any(p=>p.EmploymentEvidence=="simulated_labor_contract"),
        "synthetic contracts never masquerade as original professions");
    Expect(world.Residents.First(p=>p.Source.Id=="npc:example:0").Source.WorkPlaceId=="b:market",
        "source-known workplace association preserved");
    Expect(report.AllAccountsGold==report.InitialAllAccountsGold,
        "domestic, public and external gold reconcile");
}

static void ClosedRegion()
{
    var world=new SettlementWorld(Fixture(95,production:true),
        new SettlementSettings{Economy=EconomyMode.RegionalProduction,
            EnableExternalTrade=false,InitialOutsideGold=0,
            InitialOutsideFood=0,InitialOutsideMaterials=0});
    world.AdvanceTo(1440L*30);
    world.CheckInvariants();
    var report=world.Report();
    Expect(report.FoodProducedLocally>0 && report.FoodImported==0,
        "local crops grow without imported inventory");
    Expect(report.GoldPaidAbroad==0 && report.GoldReceivedFromAbroad==0,
        "foreign trade cannot occur when explicitly disabled");
    Expect(report.TotalGold==report.InitialGold,
        "closed region gold moves only between residents, firms and treasury");
    Expect(report.Meals>0,"locally generated food is eaten");
    Expect(report.ShipmentsSent>0 && report.FreightUnitsDelivered>0,
        "domestic freight continues even when all foreign trade is disabled");
}

static void NoSupply()
{
    var world=new SettlementWorld(Fixture(50,production:true),
        new SettlementSettings{Economy=EconomyMode.RegionalProduction,
            EnableExternalTrade=false,EnableLocalProduction=false,
            InitialOutsideGold=0,InitialOutsideFood=0,InitialOutsideMaterials=0});
    world.AdvanceTo(1440L*30);
    world.CheckInvariants();
    var r=world.Report();
    Expect(r.FoodProducedLocally==0 && r.RawMaterialsExtracted==0,
        "no harvesting without the production sector");
    Expect(r.FoodImported==0 && r.MaterialsImported==0,
        "no hidden automatic restock");
    Expect(r.Meals<=r.InitialFood,"only actual opening inventory may be eaten");
}

static void FiniteForeignMarket()
{
    var world=new SettlementWorld(Fixture(75,production:true),
        new SettlementSettings{Economy=EconomyMode.RegionalProduction,
            InitialOutsideGold=7,InitialOutsideFood=20,
            InitialOutsideMaterials=15});
    world.AdvanceTo(1440L*15);
    world.CheckInvariants();
    var r=world.Report();
    Expect(r.ExternalGold>=0 && r.ExternalFood>=0 && r.ExternalMaterials>=0,
        "foreign accounts cannot overdraft");
    Expect(r.AllAccountsGold==r.InitialAllAccountsGold,
        "imports and exports cannot create money");
}

static void CropFailureSensitivity()
{
    var catalog=Fixture(100,production:true);
    var growing=new SettlementWorld(catalog,new SettlementSettings
    {Economy=EconomyMode.RegionalProduction,EnableExternalTrade=false});
    var failed=new SettlementWorld(catalog,new SettlementSettings
    {Economy=EconomyMode.RegionalProduction,EnableExternalTrade=false,
     EnableLocalProduction=false});
    growing.AdvanceTo(1440L*30);
    failed.AdvanceTo(1440L*30);
    growing.CheckInvariants();failed.CheckInvariants();
    Expect(failed.Report().FoodProducedLocally==0,
        "crop failure actually removes local production");
    Expect(growing.Report().FoodProducedLocally>0,
        "unaffected land requires paid productive work to harvest");
    Expect(failed.Report().PersonHoursAtZeroSatiation>
           growing.Report().PersonHoursAtZeroSatiation,
        "a production shock must measurably worsen hunger");
}

static void FundedAssistance()
{
    var world=new SettlementWorld(Fixture(80,production:true),
        new SettlementSettings{Economy=EconomyMode.RegionalProduction,
            InitialPublicGoldPerResident=0,EnableExternalTrade=false,
            EnableHousing=false});
    world.AdvanceTo(1440L*30);
    world.CheckInvariants();
    var r=world.Report();
    Expect(r.AssistanceGoldPaid <= r.RevenueTaxed + r.FreightFees,
        "with no opening treasury, assistance is limited by tax and carrier receipts");
    Expect(r.PublicGold>=0,"public budget cannot run negative");
}

static void RegionalDeterminism()
{
    var catalog=Fixture(100,production:true);
    var a=new SettlementWorld(catalog,new SettlementSettings{Economy=EconomyMode.RegionalProduction});
    var b=new SettlementWorld(catalog,new SettlementSettings{Economy=EconomyMode.RegionalProduction});
    a.AdvanceTo(1440L*30);
    for(long minute=0;minute<=1440L*30;minute+=120)b.AdvanceTo(minute);
    a.CheckInvariants();b.CheckInvariants();
    Expect(JsonSerializer.Serialize(a.Report())==JsonSerializer.Serialize(b.Report()),
        "headless regional economy is deterministic across chunking");
}

static void Stress()
{
    var r=Run(10_000);
    Expect(r.Residents==10_000 && r.Events>15_000,
        "10k NPCs actually execute independent activity events");
}

static void LocalBuildingTravel()
{
    var fixture=Fixture(8);
    var places=fixture.Places.Select(p=>p.Id switch
    {
        "home:1" or "b:market" or "b:tavern" =>
            p with {MapX=1000,MapY=1000,District="Exampleton"},
        "b:forge" => p with {MapX=801000,MapY=1000,District="Far Away"},
        _=>p
    }).ToArray();
    var world=new SettlementWorld(fixture with {Places=places});
    int guildToShop=world.EstimateTravelMinutes("npc:example:0","b:market");
    int guildToTavern=world.EstimateTravelMinutes("npc:example:0","b:tavern");
    int acrossIsland=world.EstimateTravelMinutes("npc:example:0","b:forge");
    Expect(guildToShop>=1 && guildToShop<=8 && guildToTavern<=8,
        "same inherited city marker means a few local minutes, not 18");
    Expect(acrossIsland>300,"inter-city travel remains consequential");
}

static void FiniteHousing()
{
    var cat=Fixture(80,production:true);
    var normal=new SettlementWorld(cat,new SettlementSettings
        {Economy=EconomyMode.RegionalProduction});
    var info=normal.Report();
    Expect(info.HousingUnits>0 && info.HousingUnits<80 &&
        info.HousingBeds>=info.HousingOccupied &&
        info.HousingOccupied==80,"multiple NPCs share finite assigned beds");
    Expect(normal.Residents.Any(r=>r.Source.HomePlaceId=="home:1"&&r.HomeId=="home:1"),
        "source-confirmed house association remains accessible");
    normal.AdvanceTo(1440L*10);
    normal.CheckInvariants();
    Expect(normal.Report().RentCollected==normal.Residents.Sum(r=>r.RentSpent),
        "paid housing moves gold between real ledgers");

    var expensive=new SettlementWorld(cat,new SettlementSettings
    {
        Economy=EconomyMode.RegionalProduction,
        DailySharedRent=100,EnablePublicAssistance=false,
        InitialPublicGoldPerResident=0
    });
    expensive.AdvanceTo(1440L*5);
    expensive.CheckInvariants();
    var r=expensive.Report();
    Expect(r.TotalEvictions>0 && r.HomelessResidents>0,
        "people who cannot afford housing can actually lose their beds");
    Expect(r.HousingOccupied+r.HomelessResidents==r.Residents,
        "unhoused characters are not silently assigned phantom beds");
    Expect(r.RentCollected==expensive.Residents.Sum(p=>p.RentSpent),
        "landlord/public rent receipts cannot duplicate gold");
}

static void EconomicMigration()
{
    PlaceDefinition Place(string id,int source,string label,string type,string district,double x) =>
        new(id,source,label,type,district,"Auridon",
            type=="Home"?"dwelling":"business","",x,1000,
            "source_map_marker",district);
    var places=new[]
    {
        Place("home:a",1,"A boarding house","Home","A",1000),
        Place("home:b",2,"B boarding house","Home","B",801000),
        Place("port:a",3,"A harbour","Transportation","A",1000),
        Place("shop:b",4,"B workshop","Smithy","B",801000)
    };
    var businesses=new[]
    {
        new BusinessDefinition("port:a",BusinessKinds.ImportHub,null,[],"Test port"),
        new BusinessDefinition("shop:b",BusinessKinds.Workshop,null,[],"Test workshop")
    };
    var residents=Enumerable.Range(0,5).Select(i=>
        new ResidentDefinition("migrant:"+i,i+100,"Citizen "+i,"Altmer",null,
            i==3?null:i==4?"home:b":"home:a",
            i==3?"unknown":"explicit_npc_house",
            i<3?"port:a":i==4?"shop:b":null,
            i==3?"unknown":"test_source_workplace",
            [],null,
            i==4?"home:b":"home:a",
            "source-neighbourhood")).ToArray();
    var catalog=new SettlementCatalog(1,"Two Regions","Synthetic migration fixture",
        default,places,businesses,residents);
    var world=new SettlementWorld(catalog,new SettlementSettings
        {Economy=EconomyMode.RegionalProduction});
    Expect(world.Residents.First(p=>p.Source.Id=="migrant:3").WorkId is null,
        "remote jobs cannot be taken on as ordinary commutes at initialization");
    world.AdvanceTo(1440L*2);
    world.CheckInvariants();
    var mover=world.Residents.First(p=>p.Source.Id=="migrant:3");
    Expect(mover.WorkId=="shop:b" && mover.Relocations>=1,
        "jobless NPC can take a solvent distant job by relocating");
    Expect(mover.HousingId!=null && mover.HomeId!=null &&
        mover.HomeId.StartsWith("sim:shared-housing:",StringComparison.Ordinal),
        "migration requires an actual available shared bed near the employer");
    Expect(world.Report().TotalRelocations>=1,"economic migration is measured");
}

static void DockLabour()
{
    var cat=Fixture(100,production:true);
    var normal=new SettlementWorld(cat,new SettlementSettings
        {Economy=EconomyMode.RegionalProduction});
    var disabled=new SettlementWorld(cat,new SettlementSettings
        {Economy=EconomyMode.RegionalProduction,PortCargoUnitsPerWorkHour=0});
    normal.AdvanceTo(1440L*30);
    disabled.AdvanceTo(1440L*30);
    normal.CheckInvariants();disabled.CheckInvariants();
    var exported=normal.Report();
    var blocked=disabled.Report();
    Expect(exported.FoodExported>0 &&
        exported.BusinessDetails.Any(p=>p.Kind==BusinessKinds.ImportHub&&
            p.CargoHandled>0),"paid dockworkers enable real exports");
    Expect(blocked.FoodExported==0 && blocked.GoodsExported==0 &&
        blocked.MaterialsExported==0 && blocked.FoodImported==0,
        "port without any cargo-handling capacity cannot ship anything");
    Expect(blocked.BusinessDetails.Where(p=>p.Kind==BusinessKinds.ImportHub)
        .All(p=>p.CargoHandled==0),
        "no transport ledger entries appear without port labour");
}

static void TwoPortCompetition()
{
    PlaceDefinition Place(string id, int sourceId, string label, string district, double x) =>
        new(id, sourceId, label, "Transportation", district, "Auridon", "business",
            "", x, 1000, "source_map_marker", district);
    var places=new[]
    {
        Place("port:a",1,"Western Docks","West",1000),
        Place("farm:a",2,"Western Fields","West",1000),
        Place("port:b",3,"Eastern Docks","East",901000),
        Place("farm:b",4,"Eastern Fields","East",901000)
    };
    var businesses=new[]
    {
        new BusinessDefinition("port:a",BusinessKinds.ImportHub,null,[],"Test"),
        new BusinessDefinition("farm:a",BusinessKinds.FoodProducer,null,[],"Test"),
        new BusinessDefinition("port:b",BusinessKinds.ImportHub,null,[],"Test"),
        new BusinessDefinition("farm:b",BusinessKinds.FoodProducer,null,[],"Test")
    };
    var ids=new[]{"port:a","port:a","port:a","port:b","port:b","port:b",
        "farm:a","farm:a","farm:b","farm:b"};
    var residents=ids.Select((work,i)=>
    {
        string place=i is <3 or 6 or 7?"port:a":"port:b";
        return new ResidentDefinition("shipping:"+i,i+10,"Worker "+i,"Altmer",
            null,null,"unknown",work,"test_source_workplace",[],null,
            place,"source-neighbourhood");
    }).ToArray();
    var catalog=new SettlementCatalog(1,"Two Coastal Regions",
        "Synthetic shipping fixture",default,places,businesses,residents);
    var world=new SettlementWorld(catalog,new SettlementSettings
    {
        Economy=EconomyMode.RegionalProduction,
        DailyImportCapPerTenResidents=0,
        InitialOutsideFood=0,InitialOutsideMaterials=0
    });
    world.AdvanceTo(1440L*12);
    world.CheckInvariants();
    var report=world.Report();
    var ports=report.BusinessDetails.Where(p=>p.Kind==BusinessKinds.ImportHub).ToArray();
    Expect(ports.Length==2 && ports.All(p=>p.CargoHandled>0),
        "both docks can earn real shipping work from nearby farm exports");
    Expect(report.FoodExported>0 && report.FoodImported==0,
        "transport comes from domestic farm labour, not free imports");
}

static void DelayedFreight()
{
    var original=Fixture(100,production:true);
    var places=original.Places.Select(p=>p.Id switch
    {
        "sim:fields"=>p with {MapX=1000,MapY=1000,District="Rural Fields"},
        "b:market"=>p with {MapX=800000,MapY=1000,District="Town Market"},
        "b:tavern"=>p with {MapX=801000,MapY=1000,District="Town Market"},
        "b:forge"=>p with {MapX=801000,MapY=1000,District="Town Market"},
        _=>p
    }).ToArray();
    var world=new SettlementWorld(original with {Places=places},
        new SettlementSettings{Economy=EconomyMode.RegionalProduction,
            EnableExternalTrade=false,MaxFreightLoad=9});
    bool observedPending=false;
    long deliveredBefore=0;
    long sentBefore=0;
    for(long minute=0;minute<1440L*15;minute+=30)
    {
        world.AdvanceTo(minute);
        var report=world.Report();
        if(report.ShipmentsInTransit>0 && report.FoodInTransit>0)
        {
            observedPending=true;
            deliveredBefore=report.ShipmentsDelivered;
            sentBefore=report.ShipmentsSent;
            Expect(report.FoodInTransit<=report.ShipmentsInTransit*9,
                "every shipment has a finite vehicle carrying capacity");
            world.CheckInvariants();
            world.AdvanceTo(minute+1440);
            break;
        }
    }
    var after=world.Report();
    world.CheckInvariants();
    Expect(observedPending,"remote wholesale dispatches actual in-transit food");
    Expect(after.ShipmentsDelivered>deliveredBefore,
        "a delivery cannot be credited until the simulated trip finishes");
    Expect(after.ShipmentsSent>=sentBefore&&
        after.ShipmentsSent==after.ShipmentsDelivered+after.ShipmentsInTransit,
        "all freight is either moving or arrived, never duplicated");
    Expect(after.FreightFees>0,
        "the distance-dependent common carrier charges for delivery");
}

static SettlementCatalog CommodityFixture()
{
    var original=Fixture(130,production:true);
    var places=original.Places.ToList();
    var businesses=original.Businesses.ToList();
    businesses=businesses.Select(b=>b.PlaceId=="sim:fields"?
        b with{ProductionProfile="wheat,potato"}:b).ToList();
    void Add(string id,string name,string kind,string sourceType,string? profile=null)
    {
        double x=10000+places.Count*26000;
        places.Add(new PlaceDefinition(id,0,name,sourceType,"Exampleton",
            "Test zone",kind,"",x,10000,
            "simulation_at_source_anchor","Exampleton"));
        businesses.Add(new BusinessDefinition(id,kind,null,[],
            "SIMULATION HYPOTHESIS for offline fixture",profile));
    }
    Add("sim:greens","Green fields",BusinessKinds.FoodProducer,
        "simulation_inferred_agriculture","lettuce,onion");
    Add("sim:woods","Working woodland",BusinessKinds.ResourceProducer,
        "Grove","logs");
    Add("sim:smelter","Smelter",BusinessKinds.Smeltery,"simulation_inferred_smeltery");
    Add("sim:sawmill","Sawmill",BusinessKinds.Sawmill,"simulation_inferred_sawmill");
    Add("sim:bakery","Bakery",BusinessKinds.Bakery,"simulation_inferred_bakery");
    Add("sim:tannery","Tannery",BusinessKinds.Tannery,"simulation_inferred_tannery");
    Add("sim:hunter","Hunting camp",BusinessKinds.Hunter,"simulation_inferred_hunter");
    Add("sim:fishery","Fishing port",BusinessKinds.Fishery,"simulation_inferred_fishery");
    Add("sim:leather","Leatherworks",BusinessKinds.Leatherworker,"simulation_inferred_leatherworker");

    var jobs=new[]{"sim:fields","sim:greens","sim:woods","b:mine",
        "sim:smelter","sim:sawmill","sim:bakery","sim:tannery",
        "sim:hunter","sim:fishery","sim:leather"};
    var residents=original.Residents.Select((p,i)=>
        i>=50&&i<50+jobs.Length*4 ?
            p with {WorkPlaceId=jobs[(i-50)/4],WorkEvidence="synthetic_fixture_job"}:p)
        .ToArray();
    return original with {Places=places.ToArray(),Businesses=businesses.ToArray(),
        Residents=residents};
}

static CommoditySnapshot Item(SettlementReport r,string id) =>
    r.CommodityDetails.Single(x=>x.Commodity==id);

static void CheckCommodityBalance(SettlementReport r)
{
    foreach(var item in r.CommodityDetails)
        Expect(item.Initial+item.Extracted+item.Crafted==
               item.StockOnHand+item.InTransit+item.UsedAsIngredient+item.Consumed,
            "all "+item.Commodity+" must be extracted, transformed, stocked, "+
            "in transit or consumed, without appearing for free");
}

static void CommodityChains()
{
    var world=new SettlementWorld(CommodityFixture(),new SettlementSettings
        {Economy=EconomyMode.CommodityProduction,
         EnableExternalTrade=false,MaxCommodityStockPerSite=80,
         FirewoodDaysPerHousehold=100});
    world.AdvanceTo(1440L*25);
    world.CheckInvariants();
    var report=world.Report();
    CheckCommodityBalance(report);
    Expect(Item(report,"iron_ore").Extracted>0 &&
        Item(report,"iron_ingot").Crafted>0 &&
        Item(report,"pickaxe").Crafted>0 &&
        Item(report,"horseshoes").Crafted>0,
        "mines, smeltery and separate blacksmith form an actual iron chain");
    Expect(Item(report,"logs").Extracted>0 &&
        Item(report,"lumber").Crafted>0 &&
        Item(report,"firewood").Crafted>0 &&
        Item(report,"firewood").UsedAsIngredient>0,
        "felled logs become timber and fuel consumed by processors");
    Expect(Item(report,"raw_hide").Extracted>0 &&
        Item(report,"leather").Crafted>0 &&
        Item(report,"leather_goods").Crafted>0,
        "hunters supply hides to tanners and leatherworkers: raw="+
        Item(report,"raw_hide").Extracted+" leather="+Item(report,"leather").Crafted+
        " goods="+Item(report,"leather_goods").Crafted+
        " tannery="+string.Join(",",world.Businesses.Where(x=>x.Kind==BusinessKinds.Tannery)
            .Select(x=>"gold="+x.Gold+" hrs="+x.WorkHours+" "+
                string.Join(";",x.CommodityStock.Select(k=>k.Key+":"+k.Value))))+
        " sawmills="+string.Join("|",world.Businesses.Where(x=>x.Kind==BusinessKinds.Sawmill)
            .Select(x=>x.Gold+"/"+x.WorkHours+"/"+
                string.Join(";",x.CommodityStock.Select(k=>k.Key+":"+k.Value))))+
        " fuelCrafted="+Item(report,"firewood").Crafted+
        " fuelUsed="+Item(report,"firewood").UsedAsIngredient);
    Expect(Item(report,"fish").Extracted>0 &&
        Item(report,"wheat").Extracted>0 &&
        Item(report,"onion").Extracted>0 &&
        Item(report,"potato").Extracted>0 &&
        Item(report,"lettuce").Extracted>0 &&
        Item(report,"bread").Crafted>0 &&
        Item(report,"garden_salad").Crafted>0,
        "distinct local crops and fishing supply multiple cooked foods");
    Expect(report.ToolsWornOut>0 && Item(report,"horseshoes").Consumed>0 &&
        report.RecipesCompleted>0 && report.ShipmentsSent>0 && report.FreightFees>0,
        "tools wear out, recipes require paid workers and cargo needs transport");
    var corn=world.Businesses.Single(b=>b.Id=="sim:fields");
    var greens=world.Businesses.Single(b=>b.Id=="sim:greens");
    Expect(corn.CommodityStock.Keys.Where(CommodityRecipes.Crops.Contains)
            .All(k=>k is "wheat" or "potato") &&
        greens.CommodityStock.Keys.Where(CommodityRecipes.Crops.Contains)
            .All(k=>k is "lettuce" or "onion"),
        "farm crop identity remains restricted even when investment goods are stocked");
    Expect(report.AllAccountsGold==report.InitialAllAccountsGold,
        "commodity trading and delivery fees conserve all gold");
    var invalid=CommodityFixture();
    var bad=invalid with {Businesses=invalid.Businesses.Select(b=>
        b.PlaceId=="sim:fields"?
            b with{ProductionProfile="iron_ingot"}:b).ToArray()};
    bool rejected=false;
    try{bad.Validate();}catch(InvalidDataException){rejected=true;}
    Expect(rejected,"crop profiles may only contain recognised growable crops");
}

static void MissingSmeltery()
{
    var cat=CommodityFixture();
    var reduced=cat with
    {
        Places=cat.Places.Where(p=>p.Id!="sim:smelter").ToArray(),
        Businesses=cat.Businesses.Where(b=>b.PlaceId!="sim:smelter").ToArray(),
        Residents=cat.Residents.Select(r=>r.WorkPlaceId=="sim:smelter"?
            r with{WorkPlaceId=null,WorkEvidence="not_available"}:r).ToArray()
    };
    var world=new SettlementWorld(reduced,new SettlementSettings
        {Economy=EconomyMode.CommodityProduction,EnableExternalTrade=false});
    world.AdvanceTo(1440L*18);
    world.CheckInvariants();
    var report=world.Report();
    CheckCommodityBalance(report);
    Expect(Item(report,"iron_ore").Extracted>0 &&
        Item(report,"iron_ingot").Crafted==0,
        "ore cannot become ingots without an actual staffed smeltery");
    Expect(Item(report,"pickaxe").Crafted<=2,
        "a blacksmith cannot conjure ingots to replace worn tools");
}

static void CommodityDeterminism()
{
    var cat=CommodityFixture();
    var settings=new SettlementSettings{Economy=EconomyMode.CommodityProduction,
        EnableExternalTrade=false,MaxCommodityStockPerSite=80};
    var a=new SettlementWorld(cat,settings);
    var b=new SettlementWorld(cat,settings);
    a.AdvanceTo(1440L*12);
    for(long t=0;t<=1440L*12;t+=120)b.AdvanceTo(t);
    a.CheckInvariants();b.CheckInvariants();
    Expect(JsonSerializer.Serialize(a.Report())==JsonSerializer.Serialize(b.Report()),
        "all SKU stockpiles, shipment locations and recipe ledgers repeat across time chunks");
}

static void PortlessCommodityRegion()
{
    var source=CommodityFixture();
    var noPort=source with
    {
        Places=source.Places.Where(p=>p.Id!="b:port").ToArray(),
        Businesses=source.Businesses.Where(b=>b.PlaceId!="b:port").ToArray(),
        Residents=source.Residents.Select(r=>r.WorkPlaceId=="b:port"?
            r with{WorkPlaceId=null,WorkEvidence="no_local_port"}:r).ToArray()
    };
    var world=new SettlementWorld(noPort,new SettlementSettings
        {Economy=EconomyMode.CommodityProduction,EnableExternalTrade=false});
    Expect(world.Businesses.All(b=>b.Kind!=BusinessKinds.ImportHub),
        "a region without a documented port does not gain a free phantom port");
    world.AdvanceTo(1440L*20);
    world.CheckInvariants();
    var r=world.Report();
    CheckCommodityBalance(r);
    Expect(r.ShipmentsSent>12 && r.FreightDelayedForGear>0,
        "domestic freight can still operate but inadequate carrier equipment delays long routes");
}

static void DemandDrivenLabour()
{
    var world=new SettlementWorld(CommodityFixture(),new SettlementSettings
    {
        Economy=EconomyMode.CommodityProduction,EnableExternalTrade=false,
        EnableLocalProduction=false,EnablePublicAssistance=false
    });
    world.AdvanceTo(1440L*15);
    world.CheckInvariants();
    var report=world.Report();
    CheckCommodityBalance(report);
    var producers=world.Businesses.Where(b=>b.Kind is
        BusinessKinds.FoodProducer or BusinessKinds.ResourceProducer or
        BusinessKinds.Hunter or BusinessKinds.Fishery or
        BusinessKinds.Smeltery or BusinessKinds.Sawmill or
        BusinessKinds.Tannery or BusinessKinds.Bakery or
        BusinessKinds.Leatherworker or BusinessKinds.Workshop).ToArray();
    Expect(producers.All(b=>b.WagesPaid==0 && b.WorkHours==0),
        "employees do not receive a full manufacturing wage for missing inputs");
    var retailers=world.Businesses.Where(b=>b.Kind is
        BusinessKinds.FoodRetail or BusinessKinds.GoodsRetail).ToArray();
    Expect(retailers.Any(b=>b.WorkHours<15*8),
        "retailers with sparse sales do not staff empty eight-hour days");
    Expect(report.IdleProductionHoursAvoided>0 && report.RecipesCompleted==0,
        "unscheduled idle shifts and blocked recipes are visible");
}

static void CommodityPriceAndDemand()
{
    var world=new SettlementWorld(CommodityFixture(),new SettlementSettings
    {
        Economy=EconomyMode.CommodityProduction,EnableExternalTrade=false,
        MaxCommodityStockPerSite=90
    });
    world.AdvanceTo(1440L*30);
    world.CheckInvariants();
    var r=world.Report();
    CheckCommodityBalance(r);
    Expect(r.ScarcityPricePremiumGold>0 && r.ShipmentsSent>0,
        "low supplier inventory changes real paid wholesale quotes");
    Expect(r.RetailGoodsSales>0 && r.FirewoodBurned>0,
        "households buy useful goods and fuel gets consumed");
    Expect(r.AllAccountsGold==r.InitialAllAccountsGold,
        "scarcity premiums and production levies move existing gold only");
    Expect(r.FoodRemaining+r.MaterialsRemaining+r.GoodsRemaining==
        r.CommodityDetails.Sum(c=>c.StockOnHand+c.InTransit),
        "aggregated reports must include every commodity on hand or in transit");
}

static void FinitePublicForaging()
{
    const int days=12;
    const int perFarm=2;
    var catalog=CommodityFixture();
    var limited=new SettlementWorld(catalog,new SettlementSettings
    {
        Economy=EconomyMode.CommodityProduction,
        EnableExternalTrade=false,EnablePublicAssistance=false,
        PublicForagePerFarmPerDay=perFarm
    });
    limited.AdvanceTo(days*1440L);
    limited.CheckInvariants();
    var r=limited.Report();
    CheckCommodityBalance(r);
    Expect(r.FoodHarvestedForSubsistence<=days*perFarm*r.FoodProducers,
        "public gleaning must not exceed the daily forage allowance per farm");

    var forbidden=new SettlementWorld(catalog,new SettlementSettings
    {
        Economy=EconomyMode.CommodityProduction,
        EnableExternalTrade=false,EnablePublicAssistance=false,
        PublicForagePerFarmPerDay=0
    });
    forbidden.AdvanceTo(days*1440L);
    forbidden.CheckInvariants();
    Expect(forbidden.Report().FoodHarvestedForSubsistence==0,
        "commercial farmland cannot provide unlimited free meals");
}

static void BreadSeptimBenchmark()
{
    Expect(CommodityRecipes.MealRetailPrices["bread"]==1 &&
        CommodityRecipes.SatiationPerMeal("bread")==56,
        "a single one-septim loaf is one complete basic meal");
    var baking=CommodityRecipes.All.Single(r=>r.Id=="bake_bread");
    var home=CommodityRecipes.HouseholdMeals.Single(r=>r.Id=="bake_bread");
    Expect(baking.Products["bread"]>=home.Products["bread"]*2,
        "a professional baker makes at least twice the household yield");
    var prices=CommodityRecipes.Prices;
    int bakeryCost=baking.Ingredients.Sum(x=>x.Value*prices[x.Key])+
        new SettlementSettings().HourlyWage;
    Expect(2L*bakeryCost<=baking.Products["bread"],
        "baseline ingredient costs and paid wages permit bakery wholesale at "+
        "two loaves per septim without a forced producer loss");
    var world=new SettlementWorld(CommodityFixture(),new SettlementSettings
    {
        Economy=EconomyMode.CommodityProduction,EnableExternalTrade=false
    });
    var opening=world.Report();
    Expect(opening.BreadMarkets.Any(m=>m.BusinessName=="Ye Olde Test Tavern" &&
        m.PricePerLoaf>=0.75m && m.PricePerLoaf<=1.25m &&
        m.PricePerLoaf==Math.Round(m.PricePerLoaf,2) &&
        m.BreadInStock>0),
        "a stocked ordinary tavern quotes bread near one septim to two decimals");
    Expect(opening.BreadMarkets.Any(m=>m.PricePerLoaf>0m &&
        m.PricePerLoaf!=decimal.Truncate(m.PricePerLoaf)),
        "a real initial market has a sub-septim loaf price");
    world.AdvanceTo(1440L*6);
    world.CheckInvariants();
    var daySix=world.Report();
    CheckCommodityBalance(daySix);
    Expect(daySix.BreadUnitsSold>0 && daySix.BreadSalesRevenue>0,
        "fractionally priced bread is really sold and its proceeds reach a seller");
    Expect(daySix.AllAccountsGold==daySix.InitialAllAccountsGold,
        "cheap bread does not mint money or require a public subsidy");
}

static void BreadScarcityPricing()
{
    var world=new SettlementWorld(CommodityFixture(),new SettlementSettings
    {
        Economy=EconomyMode.CommodityProduction,EnableExternalTrade=false
    });
    var opening=world.Report();
    Expect(opening.BreadMarkets.Any(m=>m.PricePerLoaf is >0m and <1m),
        "production-cost bread may retail below one septim");
    Expect(opening.BreadMarkets.All(m=>m.PricePerLoaf*100m==
        decimal.Truncate(m.PricePerLoaf*100m)),
        "all displayed bread quotes use exactly hundredths of a septim");
    world.AdvanceTo(1440L*10);
    world.CheckInvariants();
    var later=world.Report();
    CheckCommodityBalance(later);
    Expect(later.MarketTrades>0 && later.MarketQuotes.Any(),
        "wholesale and retail market activity creates real quotes");
    Expect(later.MarketQuotes.All(q=>q.RetailPrice>=0m &&
        q.RetailPrice*100m==decimal.Truncate(q.RetailPrice*100m)),
        "item-level retail prices remain exact to two decimal places");
    Expect(later.MarketQuotes.Any(q=>q.WholesaleAsk>0m &&
        q.WholesaleAsk*100m==decimal.Truncate(q.WholesaleAsk*100m)),
        "wholesale offers can settle at fractional septims");
}

static void HouseholdCookingAndChefs()
{
    var source=CommodityFixture();
    var workers=source.Residents.Select(r=>
        r.WorkPlaceId=="b:tavern"?
            r with{Profession="Chef"}:r).ToArray();
    var cat=source with{Residents=workers};
    var world=new SettlementWorld(cat,new SettlementSettings
    {
        Economy=EconomyMode.CommodityProduction,EnableExternalTrade=false
    });
    world.AdvanceTo(1440L*25);
    world.CheckInvariants();
    var report=world.Report();
    CheckCommodityBalance(report);
    Expect(report.HomeCookedMeals>0 && report.GroceryKitsSold>0,
        "households buy ingredients and cook them into conserved meals");
    Expect(report.ChefCookedMeals>0 && report.SourceChefsEmployed>0,
        "named chefs prepare food with their employer's actual ingredients");
    var rare=CommodityRecipes.All.Single(r=>r.Id=="chef_game_pie");
    Expect(rare.Products.Values.Sum()>0 &&
        !CommodityRecipes.HouseholdMeals.Any(r=>r.Id==rare.Id),
        "higher skill cuisine is not an ordinary household recipe");
}

static void FractionalMarketSettlement()
{
    var world=new SettlementWorld(CommodityFixture(),new SettlementSettings
    {
        Economy=EconomyMode.CommodityProduction,
        EnableExternalTrade=false
    });
    var opening=world.Report();
    world.AdvanceTo(1440L*10);
    world.CheckInvariants();
    var report=world.Report();
    CheckCommodityBalance(report);
    Expect(report.MarketRetailRevenue>0m && report.MarketWholesaleRevenue>0m,
        "wholesale and retail transactions pay into real accounts");
    Expect(report.MarketRetailRevenue!=decimal.Truncate(report.MarketRetailRevenue) ||
        report.MarketWholesaleRevenue!=decimal.Truncate(report.MarketWholesaleRevenue),
        "actual market transactions settle in fractional septims");
    Expect(world.Residents.Any(r=>r.Gold!=decimal.Truncate(r.Gold)) ||
        world.Businesses.Any(b=>b.Gold!=decimal.Truncate(b.Gold)),
        "fractional market receipts appear in real wallets, not just reporting");
    Expect(report.AllAccountsGold==report.InitialAllAccountsGold,
        "one-hundredth septim transfers cannot mint or destroy gold");
    Expect(opening.MarketQuotes.Any(q=>q.RetailPrice is >0m and <1m),
        "a cost-based shop can advertise bread below a whole septim");
    Expect(report.MarketQuotes.All(q=>
        q.WholesaleAsk*100m==decimal.Truncate(q.WholesaleAsk*100m) &&
        q.RetailPrice*100m==decimal.Truncate(q.RetailPrice*100m)),
        "every advertised price is exactly cent-precise");
}

static void AdaptiveBusinessPlanning()
{
    var catalog=CommodityFixture();
    var live=new SettlementWorld(catalog,new SettlementSettings
    {
        Economy=EconomyMode.CommodityProduction,EnableExternalTrade=false
    });
    live.AdvanceTo(1440L);
    var opening=live.Report();
    Expect(opening.BusinessPlans.Where(p=>p.BusinessName is
        "Green fields" or "Simulated community allotment")
        .All(p=>p.BudgetedHours>=8),
        "opening farms provision known residents before sales history exists");
    Expect(opening.BusinessPlans.Where(p=>p.BusinessName=="Bakery")
        .All(p=>p.Product!="chef_game_pie"),
        "speculative high-margin chef-only food cannot bypass skill limits");
    live.AdvanceTo(1440L*16);
    live.CheckInvariants();
    var report=live.Report();
    CheckCommodityBalance(report);
    Expect(report.BusinessPlansMade>report.BusinessPlans.Length &&
        report.BusinessPlansRevised>0,
        "producers revise daily plans as sales, cash and published orders change");
    Expect(report.BusinessPlans.Any(p=>p.Product is not null) &&
        report.BusinessPlans.Any(p=>p.Product is null),
        "some producers find feasible production while others cannot invent missing inputs");
    Expect(report.BusinessPlans.All(p=>p.BudgetedHours>=0 &&
        p.EstimatedBatchMargin==Math.Round(p.EstimatedBatchMargin,2)),
        "daily labour budgets and cash estimates are valid");
    var halted=new SettlementWorld(catalog,new SettlementSettings
    {
        Economy=EconomyMode.CommodityProduction,EnableExternalTrade=false,
        EnableLocalProduction=false
    });
    halted.AdvanceTo(1440L*4);
    halted.CheckInvariants();
    Expect(halted.Report().BusinessPlans.Where(p=>p.Product is not null &&
        p.Product is not ("bread" or "provisions")).All(p=>p.BudgetedHours==0),
        "production-disabled scenarios do not create paid production plans");
    Expect(halted.Report().RecipesCompleted==0,
        "production plans cannot create physical goods without production");
}

static void FundedCapitalConstruction()
{
    var world=new SettlementWorld(CommodityFixture(),new SettlementSettings
    {
        Economy=EconomyMode.CommodityProduction,EnableExternalTrade=false
    });
    var fishery=world.Businesses.Single(b=>b.Id=="sim:fishery");
    world.AdvanceTo(11L*1440);
    // Allocate existing, paid-for materials and money through a zero-fee
    // intra-fixture donation. This does not mint goods or gold and makes
    // the construction path deterministic without depending on market luck.
    void Gift(string sku,int count)
    {
        int remaining=count;
        foreach(var source in world.Businesses.Where(b=>b.Id!=fishery.Id))
        {
            int available=source.Stock.GetValueOrDefault(sku);
            int moved=Math.Min(available,remaining);
            if(moved==0)continue;
            long originalValue=source.InventoryValueMilli.GetValueOrDefault(sku);
            long basis=moved==available?originalValue:
                originalValue*moved/available;
            source.Stock[sku]-=moved;
            if(source.Stock[sku]==0)
            {
                source.Stock.Remove(sku);
                source.InventoryValueMilli.Remove(sku);
            }
            else source.InventoryValueMilli[sku]=originalValue-basis;
            fishery.Stock[sku]=fishery.Stock.GetValueOrDefault(sku)+moved;
            fishery.InventoryValueMilli[sku]=
                fishery.InventoryValueMilli.GetValueOrDefault(sku)+basis;
            remaining-=moved;
            if(remaining==0)break;
        }
        Expect(remaining==0,"physical capital input "+sku+" exists in the fixture");
    }
    Gift("lumber",1);
    Gift("leather",1);
    decimal needed=Math.Max(0m,1500m-fishery.Gold);
    foreach(var source in world.Businesses.Where(b=>b.Id!=fishery.Id)
        .OrderByDescending(b=>b.Gold))
    {
        decimal transferred=Math.Min(needed,Math.Max(0m,source.Gold-100m));
        source.Gold-=transferred;
        fishery.Gold+=transferred;
        needed-=transferred;
        if(needed==0)break;
    }
    Expect(needed==0,"investment cash is transferred from real accounts");
    fishery.RecentDemand["fish"]=30m;
    world.CheckInvariants();
    world.AdvanceTo(22L*1440);
    world.CheckInvariants();
    var report=world.Report();
    CheckCommodityBalance(report);
    Expect(report.CapitalProjectsStarted>=1,
        "a solvent firm with material inputs can commission construction");
    Expect(report.CapitalProjectsCompleted>=1 && fishery.CapitalLevel>1,
        "capacity only increases after actual workshop worker hours");
    Expect(report.CapitalMaterialsConsumed>=2 &&
        report.ConstructionLabourPayments>=12m,
        "source goods and financed workshop pay are both real costs");
    Expect(report.AllAccountsGold==report.InitialAllAccountsGold,
        "paid construction cannot create or destroy fractional septims");
    var closed=new SettlementWorld(CommodityFixture(),new SettlementSettings
    {
        Economy=EconomyMode.CommodityProduction,EnableLocalProduction=false,
        EnableExternalTrade=false
    });
    closed.AdvanceTo(30L*1440);
    closed.CheckInvariants();
    Expect(closed.Report().CapitalProjectsStarted==0,
        "shutting off local production cannot secretly construct equipment");
}
