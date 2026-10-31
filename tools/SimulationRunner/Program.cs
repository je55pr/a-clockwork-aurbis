using System.Diagnostics;
using System.Text.Json;
using AClockworkAurbis.Simulation;

static int Usage()
{
    Console.WriteLine("""
    A Clockwork Aurbis | independent, deterministic headless simulation

    dotnet run -c Release --project tools/SimulationRunner -- [options]

    --people N        Synthetic agents (default 10000); real profile cap only if specified
    --days N          Simulated days (default 3, max 365)
    --seed N          Stable world seed (default 42)
    --profiles PATH   Use ProfileBuilder initial-npcs.jsonl instead of synthetic agents
    --area PLACE      Limit real NPC profiles to a named UESP location
    --trace ID        Print activity events for one NPC by stable ID
    --trace-first     Print activity events for the first loaded NPC
    --json            Emit a structured JSON report
    --help            Show this help

    Source identity is preserved. Gold, food, needs, work assignment and travel
    times are simulation defaults, NOT factual ESO character attributes.
    No Godot, scene, asset archive, SQLite, graphics device or network required.
    """);
    return 0;
}

try
{
    int? requestedPeople = null;
    int days = 3, seed = 42;
    string? path = null, area = null, trace = null;
    bool traceFirst = false, json = false;

    for (int i = 0; i < args.Length; i++)
    {
        string value = args[i];
        string Next() => i + 1 < args.Length
            ? args[++i] : throw new ArgumentException("Missing value after " + value);
        switch (value)
        {
            case "--help":
            case "-h": return Usage();
            case "--people": requestedPeople = int.Parse(Next()); break;
            case "--days": days = int.Parse(Next()); break;
            case "--seed": seed = int.Parse(Next()); break;
            case "--profiles": path = Next(); break;
            case "--area": area = Next(); break;
            case "--trace": trace = Next(); break;
            case "--trace-first": traceFirst = true; break;
            case "--json": json = true; break;
            default: throw new ArgumentException("Unknown option " + value);
        }
    }
    if (requestedPeople is < 0 or > 1_000_000 || days is < 0 or > 365)
        throw new ArgumentOutOfRangeException(nameof(args), "People must be 0..1,000,000 and days 0..365.");
    if (area is not null && path is null)
        throw new ArgumentException("--area requires --profiles");

    var setupClock = Stopwatch.StartNew();
    var seeds = path is null ? NpcSeeds.Synthetic(requestedPeople ?? 10_000)
        : NpcSeeds.ReadProfiles(path, requestedPeople ?? int.MaxValue, area);
    if (traceFirst && seeds.Count > 0) trace = seeds[0].Id;
    var world = new SimulationWorld(seeds, new SimulationSettings { Seed = seed });
    setupClock.Stop();

    if (trace is not null)
    {
        string tracked = trace;
        world.Observer = message =>
        {
            if (!string.Equals(message.NpcId, tracked, StringComparison.Ordinal))
                return;
            Console.Error.WriteLine(
                $"D{1 + message.Minute / 1440} " +
                $"{message.Minute % 1440 / 60:D2}:{message.Minute % 60:D2} " +
                $"{message.Action,-9} {message.Detail}");
        };
    }

    var simClock = Stopwatch.StartNew();
    world.AdvanceTo(days * 1440L);
    simClock.Stop();
    world.CheckInvariants();
    var report = world.Report();
    double agentsPerSecond = simClock.Elapsed.TotalSeconds > 0
        ? (double)seeds.Count * days / simClock.Elapsed.TotalSeconds : 0;
    double eventsPerSecond = simClock.Elapsed.TotalSeconds > 0
        ? report.EventsProcessed / simClock.Elapsed.TotalSeconds : 0;
    var output = new
    {
        Mode = "Godot-free discrete-event simulation",
        Input = path is null ? "Synthetic baseline NPCs" : Path.GetFullPath(path),
        Area = area,
        SourceProfiles = path is not null,
        SimulatedDays = days,
        Seed = seed,
        report.Npcs,
        report.EventsProcessed,
        report.DecisionCount,
        report.WagesPaid,
        report.GoodsSold,
        report.GoodsEaten,
        report.GoodsProduced,
        report.GoodsRestocked,
        report.JourneysCompleted,
        report.UnpaidHours,
        report.ActionsSleeping,
        report.ActionsWorking,
        report.ShopStock,
        report.FarmStock,
        report.ShopGold,
        report.EmployerGold,
        report.FarmGold,
        report.InitialTotalGold,
        report.TotalGold,
        report.InitialTotalFood,
        report.TotalFoodRemaining,
        SetupMs = Math.Round(setupClock.Elapsed.TotalMilliseconds, 2),
        SimulationMs = Math.Round(simClock.Elapsed.TotalMilliseconds, 2),
        EventsPerSecond = Math.Round(eventsPerSecond, 0),
        NpcDaysPerSecond = Math.Round(agentsPerSecond, 0),
        GoldConserved = report.TotalGold == report.InitialTotalGold,
        FoodReconciles = report.TotalFoodRemaining + report.GoodsEaten ==
            report.InitialTotalFood + report.GoodsProduced,
        ResourceModel = "All currency transfers; finite stocks; explicit daily food production",
        Defaults = "Needs, money, salaries, homes, jobs and travel are simulation assumptions"
    };
    if (json) Console.WriteLine(JsonSerializer.Serialize(output));
    else Console.WriteLine(JsonSerializer.Serialize(output,
        new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("SIMULATION ERROR: " + exception.Message);
    return 1;
}
