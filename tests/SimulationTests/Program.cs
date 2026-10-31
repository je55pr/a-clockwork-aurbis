using System.Text.Json;
using AClockworkAurbis.Simulation;

var tests = new (string Name, Action Run)[]
{
    ("Godot-free baseline economics and needs", Economics),
    ("Exact deterministic simulation results", Determinism),
    ("Incremental time advancement matches continuous run", Incremental),
    ("No gold or food fabricated by transactions", NoMoneyOrFood),
    ("Zero NPC simulation", EmptyWorld),
    ("Source-backed JSONL identity and place filtering", SourceProfiles),
    ("Duplicate NPC IDs rejected", DuplicateIds),
    ("Ten thousand NPCs for three simulated days", TenThousand),
    ("Wages cannot exceed completed work hours", AccurateWages),
    ("Event traces are ordered and source-identified", TraceEvents),
    ("Cannot move simulation clock backwards", NoTimeTravel),
    ("Reject negative initial economic accounts", BadEconomics)
};
int completed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("PASS " + name); completed++; }
    catch (Exception ex)
    {
        Console.Error.WriteLine("FAIL " + name + ": " + ex);
        return 1;
    }
}
Console.WriteLine($"ALL {completed} HEADLESS SIMULATION TESTS PASSED");
return 0;

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void Economics()
{
    var world = new SimulationWorld(NpcSeeds.Synthetic(120));
    var initialWallets = world.Agents.Select(x => x.Gold).ToArray();
    world.AdvanceTo(3 * 1440);
    world.CheckInvariants();
    var report = world.Report();
    Assert(report.Npcs == 120, "population");
    Assert(report.WagesPaid > 0, "working population must receive wages");
    Assert(report.GoodsSold > 0 && report.GoodsEaten > 0, "actual purchases and meals");
    Assert(report.GoodsRestocked > 0 && report.GoodsProduced > 0, "food supply chain");
    Assert(report.JourneysCompleted > 0, "physical travel actions");
    Assert(report.ActionsSleeping > 0 && report.ActionsWorking > 0, "sleep and work actions");
    Assert(world.Agents.Any(p => p.MealsEaten > 0), "food affects individual NPC");
    Assert(world.Agents.Any(p => p.TotalMinutesSleeping > 0), "sleep affects individual NPC");
    Assert(world.Agents.Any(p => p.WagesReceived > 0), "wages affect individual NPC");
    Assert(world.Agents.Where((p,i) => p.Gold != initialWallets[i]).Any(),
        "individual balances change from their actual initial values");
    Assert(report.TotalGold == report.InitialTotalGold, "currency conservation");
    Assert(report.TotalFoodRemaining + report.GoodsEaten ==
        report.InitialTotalFood + report.GoodsProduced, "food conservation");
}

static void Determinism()
{
    var seeds = NpcSeeds.Synthetic(125);
    var a = new SimulationWorld(seeds, new SimulationSettings { Seed = 71 });
    var b = new SimulationWorld(seeds, new SimulationSettings { Seed = 71 });
    a.AdvanceTo(5 * 1440L);
    b.AdvanceTo(5 * 1440L);
    a.CheckInvariants();
    b.CheckInvariants();
    Assert(JsonSerializer.Serialize(a.Report()) == JsonSerializer.Serialize(b.Report()),
        "identical seeds must produce identical world report");
    for (int i = 0; i < seeds.Count; i++)
    {
        var x = a.Agents[i];
        var y = b.Agents[i];
        Assert(x.Gold == y.Gold && x.Food == y.Food &&
            x.Satiation == y.Satiation && x.Energy == y.Energy &&
            x.Location == y.Location && x.CurrentActivity == y.CurrentActivity,
            "individual simulation state must be deterministic");
    }
}

static void Incremental()
{
    var seeds = NpcSeeds.Synthetic(90);
    var continuous = new SimulationWorld(seeds);
    var stepwise = new SimulationWorld(seeds);
    continuous.AdvanceTo(4320);
    for (long minute = 0; minute <= 4320; minute += 120)
    {
        stepwise.AdvanceTo(minute);
        stepwise.CheckInvariants();
    }
    Assert(JsonSerializer.Serialize(continuous.Report()) ==
        JsonSerializer.Serialize(stepwise.Report()), "chunking must not alter event execution");
    for (int i = 0; i < seeds.Count; i++)
    {
        var x = continuous.Agents[i];
        var y = stepwise.Agents[i];
        Assert(Math.Abs(x.Satiation - y.Satiation) < 0.00001 &&
            Math.Abs(x.Energy - y.Energy) < 0.00001 &&
            x.Gold == y.Gold && x.Food == y.Food,
            "chunking must not alter agent outcomes");
    }
}

static void NoMoneyOrFood()
{
    var world = new SimulationWorld(NpcSeeds.Synthetic(40),
        new SimulationSettings
        {
            StartingEmployerGoldPerNpc = 0,
            StartingShopGoldPerNpc = 0,
            InitialFoodPerNpcAtShop = 0,
            InitialFoodPerNpcAtFarm = 0,
            DailyFoodProductionPerNpc = 0
        });
    world.AdvanceTo(30 * 1440);
    world.CheckInvariants();
    var result = world.Report();
    Assert(result.WagesPaid == 0, "unfunded employer must not print money");
    Assert(result.UnpaidHours > 0, "unpaid work recorded");
    Assert(result.GoodsSold == 0, "unstocked shop cannot conjure food");
    Assert(result.GoodsProduced == 0, "production cannot be hidden");
    Assert(world.Agents.Any(x => x.Satiation <= 0), "no food causes unmet needs");
}

static void EmptyWorld()
{
    var empty = new SimulationWorld(Array.Empty<NpcSeed>());
    empty.AdvanceTo(1440 * 20L);
    empty.CheckInvariants();
    Assert(empty.Report().Npcs == 0, "zero NPC world");
    Assert(empty.Report().GoodsSold == 0 && empty.Report().TotalGold == 0,
        "zero world has zero transactions and currency");
}

static void SourceProfiles()
{
    var path = Path.Combine(Path.GetTempPath(),
        "aurbis-sim-profiles-" + Guid.NewGuid().ToString("N") + ".jsonl");
    try
    {
        File.WriteAllLines(path,
        [
            """{"id":"npc:1","identity":{"name":"Named Worker","race":"High Elf"},"society":{"occupation":"Dockworker"},"locations":{"primary":{"name":"Vulkhel Guard"},"candidates":[]}}""",
            """{"id":"npc:2","identity":{"name":"Other Worker"},"society":{"serviceRole":"Merchant"},"locations":{"primary":{"name":"Elsewhere"},"candidates":[{"name":"Vulkhel Guard"}]}}""",
            """{"id":"npc:3","identity":{"name":"Unknown Place"},"society":{},"locations":{"primary":null,"candidates":[]}}"""
        ]);
        var whole = NpcSeeds.ReadProfiles(path);
        Assert(whole.Count == 3, "all source profiles load");
        Assert(whole[0].Name == "Named Worker" && whole[0].Profession == "Dockworker",
            "source NPC identity, profession stay intact");
        Assert(whole[2].SourcePlace == null, "unknown location stays unknown");
        var nearby = NpcSeeds.ReadProfiles(path, area: "Vulkhel Guard");
        Assert(nearby.Count == 2, "area matches primary and candidate links");
        Assert(NpcSeeds.ReadProfiles(path, 1).Count == 1, "limit works");
        File.WriteAllLines(path,
        [
            "    ",
            """{"id":"npc:good","identity":{"name":"Good"}}""",
            "{ malformed"
        ]);
        bool jsonLine=false;
        try{NpcSeeds.ReadProfiles(path);}
        catch(InvalidDataException e){jsonLine=e.Message.Contains("line 3");}
        Assert(jsonLine,"whitespace-only lines count toward physical parse-error line numbers");
        Assert(NpcSeeds.ReadProfiles(path,1).Count==1,
            "explicit profile limit stops before a later malformed line");
        File.WriteAllLines(path,
        [
            "",
            """{"identity":{"name":"Missing id"}}"""
        ]);
        bool missingIdLine=false;
        try{NpcSeeds.ReadProfiles(path);}
        catch(InvalidDataException e){missingIdLine=e.Message.Contains("line 2");}
        Assert(missingIdLine,"missing source ID reports its physical JSONL line");
    }
    finally { File.Delete(path); }
}

static void DuplicateIds()
{
    bool threw = false;
    try
    {
        _ = new SimulationWorld([
            new NpcSeed("same", "One", null, null, null),
            new NpcSeed("same", "Two", null, null, null)
        ]);
    }
    catch (ArgumentException) { threw = true; }
    Assert(threw, "source identities must be unique");
}

static void AccurateWages()
{
    var world = new SimulationWorld(NpcSeeds.Synthetic(240));
    world.AdvanceTo(3 * 1440);
    long actualTotal = world.Agents.Sum(x => x.WagesReceived);
    Assert(actualTotal == world.Report().WagesPaid, "paid wages reconcile to all agent accounts");
    foreach (var npc in world.Agents)
    {
        Assert(npc.WagesReceived <= (npc.TotalMinutesWorking / 60) * 3,
            "full-hour wages cannot be paid for partial work");
        Assert(npc.OutstandingWorkMinutes is >= 0 and < 60,
            "unpaid partial work tracked in minutes");
    }
}

static void TraceEvents()
{
    var world = new SimulationWorld(NpcSeeds.Synthetic(1));
    var observations = new List<SimObservation>();
    world.Observer = observations.Add;
    world.AdvanceTo(3 * 1440);
    world.CheckInvariants();
    Assert(observations.Any(e => e.Action == "wages"), "trace includes salary transfers");
    Assert(observations.Any(e => e.Action == "ate"), "trace includes food consumption");
    Assert(observations.Any(e => e.Action == "sleep"), "trace includes sleeping");
    for (int i = 1; i < observations.Count; i++)
        Assert(observations[i - 1].Minute <= observations[i].Minute,
            "event observations sorted by simulated time");
    Assert(observations.All(x => x.NpcId == "synthetic:0"), "stable identity in trace");
}

static void NoTimeTravel()
{
    var world = new SimulationWorld(NpcSeeds.Synthetic(2));
    world.AdvanceTo(60);
    bool rejected = false;
    try { world.AdvanceTo(59); } catch (ArgumentOutOfRangeException) { rejected = true; }
    Assert(rejected, "backwards simulation time rejected");
}

static void BadEconomics()
{
    bool rejected = false;
    try { _ = new SimulationWorld(NpcSeeds.Synthetic(1),
        new SimulationSettings { StartingShopGoldPerNpc = -1 }); }
    catch (ArgumentOutOfRangeException) { rejected = true; }
    Assert(rejected, "negative business balance rejected");
}

static void TenThousand()
{
    var world = new SimulationWorld(NpcSeeds.Synthetic(10_000));
    world.AdvanceTo(4320);
    world.CheckInvariants();
    var report = world.Report();
    Assert(report.Npcs == 10_000 && report.EventsProcessed > 50_000,
        "headless engine must actually simulate thousands of active NPCs");
}
