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
    Expect(r.AssistanceGoldPaid <= r.RevenueTaxed,
        "with no opening treasury, assistance is limited by tax receipts");
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
