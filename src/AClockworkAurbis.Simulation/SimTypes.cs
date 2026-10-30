namespace AClockworkAurbis.Simulation;

// Source identity is immutable; these initial economy values are explicitly
// synthetic. They must never overwrite UESP's nullable character data.
public sealed record NpcSeed(
    string Id,
    string Name,
    string? Race,
    string? Profession,
    string? SourcePlace
);

public enum Activity : byte
{
    Idle,
    Travel,
    Work,
    BuyFood,
    Eat,
    Sleep
}

public enum Place : byte
{
    Home,
    Workplace,
    Shop
}

public sealed class NpcState
{
    internal NpcState(NpcSeed source, bool employed, long cash, int food,
        double satiation, double energy, int workStartHour)
    {
        Source = source;
        Employed = employed;
        Gold = cash;
        Food = food;
        Satiation = satiation;
        Energy = energy;
        WorkStartHour = workStartHour;
    }

    public NpcSeed Source { get; }
    public bool Employed { get; }
    public int WorkStartHour { get; }
    public long Gold { get; internal set; }
    public int Food { get; internal set; }
    public double Satiation { get; internal set; }
    public double Energy { get; internal set; }
    public Place Location { get; internal set; } = Place.Home;
    public Place Destination { get; internal set; } = Place.Home;
    public Activity CurrentActivity { get; internal set; } = Activity.Idle;
    public long LastUpdatedMinute { get; internal set; }
    public long ActivityEndMinute { get; internal set; }
    public long ActivityStartMinute { get; internal set; }
    public int OutstandingWorkMinutes { get; internal set; }
    public long TotalMinutesWorking { get; internal set; }
    public long TotalMinutesSleeping { get; internal set; }
    public long WagesReceived { get; internal set; }
    public long MoneySpent { get; internal set; }
    public int MealsEaten { get; internal set; }
    public int UnpaidHours { get; internal set; }
}

public sealed record SimulationSettings
{
    public int Seed { get; init; } = 42;
    public int MealPrice { get; init; } = 6;
    public int WholesalePrice { get; init; } = 2;
    public int HourlyWage { get; init; } = 3;
    public int TravelMinutes { get; init; } = 18;
    public int WorkingHours { get; init; } = 8;
    public int ShopOpeningHour { get; init; } = 7;
    public int ShopClosingHour { get; init; } = 21;
    public long StartingShopGoldPerNpc { get; init; } = 15;
    public long StartingEmployerGoldPerNpc { get; init; } = 100;
    public int InitialFoodPerNpcAtShop { get; init; } = 1;
    public int InitialFoodPerNpcAtFarm { get; init; } = 2;
    public int DailyFoodProductionPerNpc { get; init; } = 2;
}

public readonly record struct SimObservation(
    long Minute, string NpcId, string Action, string Detail);

public sealed record SimulationReport
{
    public required int Npcs { get; init; }
    public required long Minute { get; init; }
    public required long EventsProcessed { get; init; }
    public required long DecisionCount { get; init; }
    public required long WagesPaid { get; init; }
    public required long GoodsSold { get; init; }
    public required long GoodsEaten { get; init; }
    public required long GoodsProduced { get; init; }
    public required long GoodsRestocked { get; init; }
    public required long JourneysCompleted { get; init; }
    public required long UnpaidHours { get; init; }
    public required int ShopStock { get; init; }
    public required int FarmStock { get; init; }
    public required long ShopGold { get; init; }
    public required long EmployerGold { get; init; }
    public required long FarmGold { get; init; }
    public required long TotalGold { get; init; }
    public required long InitialTotalGold { get; init; }
    public required long TotalFoodRemaining { get; init; }
    public required long InitialTotalFood { get; init; }
    public required long ActionsSleeping { get; init; }
    public required long ActionsWorking { get; init; }
}
