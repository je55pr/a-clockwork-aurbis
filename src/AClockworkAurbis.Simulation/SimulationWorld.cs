namespace AClockworkAurbis.Simulation;

/// <summary>
/// Headless discrete-event simulation. One scheduled event per NPC rather than
/// an NPC update per rendered frame or simulated minute.
/// No Godot reference, global random generator or wall-clock dependency.
/// </summary>
public sealed class SimulationWorld
{
    private const int MinutesPerDay = 1_440;
    private readonly SimulationSettings _settings;
    private readonly List<NpcState> _agents;
    private readonly PriorityQueue<int, (long Minute, long Order)> _events = new();
    private long _nextOrder;
    private long _initialGold;
    private long _initialFood;
    private long _eventsProcessed;
    private long _decisions;
    private long _wages;
    private long _foodSold;
    private long _foodEaten;
    private long _foodProduced;
    private long _foodRestocked;
    private long _journeys;
    private long _unpaid;
    private long _startedSleep;
    private long _startedWork;

    public SimulationWorld(IEnumerable<NpcSeed> seeds, SimulationSettings? settings = null)
    {
        _settings = settings ?? new SimulationSettings();
        if (_settings.MealPrice <= 0 || _settings.HourlyWage < 0 ||
            _settings.TravelMinutes < 1 || _settings.WorkingHours is < 1 or > 12 ||
            _settings.ShopOpeningHour < 0 || _settings.ShopClosingHour > 24 ||
            _settings.ShopOpeningHour >= _settings.ShopClosingHour ||
            _settings.InitialFoodPerNpcAtShop < 0 ||
            _settings.InitialFoodPerNpcAtFarm < 0 ||
            _settings.DailyFoodProductionPerNpc < 0 ||
            _settings.WholesalePrice < 0 || _settings.StartingShopGoldPerNpc < 0 ||
            _settings.StartingEmployerGoldPerNpc < 0)
            throw new ArgumentOutOfRangeException(nameof(settings), "Invalid simulation economics or hours.");

        _agents = new List<NpcState>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in seeds)
        {
            if (string.IsNullOrWhiteSpace(source.Id) || !seen.Add(source.Id))
                throw new ArgumentException("NPC identifiers must be unique and nonempty", nameof(seeds));
            int employmentRoll = StableRandom.Between(source.Id, _settings.Seed, 10, 100);
            bool employed = source.Profession is not null || employmentRoll < 65;
            int shift = 7 + StableRandom.Between(source.Id, _settings.Seed, 11, 3);
            var actor = new NpcState(source, employed,
                15 + StableRandom.Between(source.Id, _settings.Seed, 12, 55),
                StableRandom.Between(source.Id, _settings.Seed, 13, 3),
                55 + StableRandom.Between(source.Id, _settings.Seed, 14, 45),
                55 + StableRandom.Between(source.Id, _settings.Seed, 15, 45),
                shift);
            int index = _agents.Count;
            _agents.Add(actor);
            Schedule(index, StableRandom.Between(source.Id, _settings.Seed, 16, 51) + 1,
                Activity.Idle);
        }

        var count = _agents.Count;
        ShopStock = checked(count * _settings.InitialFoodPerNpcAtShop);
        FarmStock = checked(count * _settings.InitialFoodPerNpcAtFarm);
        ShopGold = checked(count * _settings.StartingShopGoldPerNpc);
        EmployerGold = checked(count * _settings.StartingEmployerGoldPerNpc);
        _initialGold = TotalGold();
        _initialFood = TotalFood();
        // World event -1 is daily food production and paid shop restocking.
        _events.Enqueue(-1, (360, _nextOrder++));
    }

    public long Minute { get; private set; }
    public IReadOnlyList<NpcState> Agents => _agents;
    public int ShopStock { get; private set; }
    public int FarmStock { get; private set; }
    public long ShopGold { get; private set; }
    public long EmployerGold { get; private set; }
    public long FarmGold { get; private set; }
    public Action<SimObservation>? Observer { get; set; }

    public void AdvanceTo(long targetMinute)
    {
        if (targetMinute < Minute || targetMinute < 0)
            throw new ArgumentOutOfRangeException(nameof(targetMinute), "Simulation time cannot move backwards.");
        while (_events.TryPeek(out _, out var priority) && priority.Minute <= targetMinute)
        {
            int index = _events.Dequeue();
            Minute = priority.Minute;
            _eventsProcessed++;
            if (index == -1)
            {
                ReplenishFood();
                _events.Enqueue(-1, (checked(Minute + MinutesPerDay), _nextOrder++));
                continue;
            }
            var actor = _agents[index];
            AdvanceNeeds(actor, Minute);
            CompleteAction(actor);
            Decide(index, actor);
        }

        // Lazy need updates also make partial-day snapshots correct, without
        // waking NPCs or running any decisions at unscheduled times.
        Minute = targetMinute;
        foreach (var actor in _agents) AdvanceNeeds(actor, Minute);
    }

    private void AdvanceNeeds(NpcState agent, long minute)
    {
        long delta = minute - agent.LastUpdatedMinute;
        if (delta == 0) return;
        if (delta < 0) throw new InvalidOperationException("NPC time moved backwards");
        agent.Satiation = Math.Clamp(agent.Satiation - delta * 0.044, 0, 100);
        double energyRate = agent.CurrentActivity switch
        {
            Activity.Sleep => 0.16,
            Activity.Work => -0.078,
            _ => -0.052
        };
        agent.Energy = Math.Clamp(agent.Energy + delta * energyRate, 0, 100);
        if (agent.CurrentActivity == Activity.Work)
            agent.TotalMinutesWorking += delta;
        if (agent.CurrentActivity == Activity.Sleep)
            agent.TotalMinutesSleeping += delta;
        agent.LastUpdatedMinute = minute;
    }

    private void CompleteAction(NpcState agent)
    {
        switch (agent.CurrentActivity)
        {
            case Activity.Travel:
                agent.Location = agent.Destination;
                _journeys++;
                if (Observer != null) Observe(agent, "arrived", agent.Location.ToString());
                break;
            case Activity.Work:
                // Pay for actual completed hours, carrying partial-shift minutes.
                agent.OutstandingWorkMinutes += checked((int)(Minute - agent.ActivityStartMinute));
                int completedHours = agent.OutstandingWorkMinutes / 60;
                agent.OutstandingWorkMinutes %= 60;
                if (completedHours > 0)
                {
                    long amount = checked((long)completedHours * _settings.HourlyWage);
                    if (EmployerGold >= amount)
                    {
                        EmployerGold -= amount;
                        agent.Gold = checked(agent.Gold + amount);
                        agent.WagesReceived += amount;
                        _wages += amount;
                        if (Observer != null)
                            Observe(agent, "wages", $"+{amount} gold; balance {agent.Gold}");
                    }
                    else
                    {
                        agent.UnpaidHours += completedHours;
                        _unpaid += completedHours;
                        if (Observer != null)
                            Observe(agent, "unpaid", "Employer treasury exhausted");
                    }
                }
                break;
            case Activity.BuyFood:
                if (ShopStock > 0 && agent.Gold >= _settings.MealPrice)
                {
                    ShopStock--;
                    agent.Food++;
                    agent.Gold -= _settings.MealPrice;
                    agent.MoneySpent += _settings.MealPrice;
                    ShopGold += _settings.MealPrice;
                    _foodSold++;
                    if (Observer != null) Observe(agent, "purchase", $"1 meal for {_settings.MealPrice} gold; balance {agent.Gold}");
                }
                break;
            case Activity.Eat:
                if (agent.Food > 0)
                {
                    agent.Food--;
                    agent.Satiation = Math.Min(100, agent.Satiation + 58);
                    agent.MealsEaten++;
                    _foodEaten++;
                    if (Observer != null) Observe(agent, "ate", $"Satiation {agent.Satiation:0.0}/100");
                }
                break;
        }
    }

    private void Decide(int index, NpcState agent)
    {
        _decisions++;
        int minuteOfDay = (int)(Minute % MinutesPerDay);
        int hour = minuteOfDay / 60;
        bool night = hour >= 22 || hour < 6;
        if (night)
        {
            if (agent.Location != Place.Home)
                Travel(index, agent, Place.Home);
            else
            {
                int untilSix = ((6 * 60 - minuteOfDay + MinutesPerDay) % MinutesPerDay);
                Schedule(index, Math.Max(1, untilSix), Activity.Sleep);
                _startedSleep++;
                if (Observer != null) Observe(agent, "sleep", "Rest until morning");
            }
            return;
        }

        if (agent.Satiation < 53)
        {
            if (agent.Food > 0)
            {
                Schedule(index, 12, Activity.Eat);
                if (Observer != null) Observe(agent, "eat", "Eating food from inventory");
                return;
            }

            if (hour >= _settings.ShopOpeningHour && hour < _settings.ShopClosingHour &&
                agent.Gold >= _settings.MealPrice && ShopStock > 0)
            {
                if (agent.Location != Place.Shop) Travel(index, agent, Place.Shop);
                else
                {
                    Schedule(index, 4, Activity.BuyFood);
                    if (Observer != null) Observe(agent, "shop", "Buying food from merchant");
                }
                return;
            }
        }

        if (agent.Employed &&
            minuteOfDay >= agent.WorkStartHour * 60 &&
            minuteOfDay < (agent.WorkStartHour + _settings.WorkingHours) * 60 &&
            agent.Energy > 15)
        {
            if (agent.Location != Place.Workplace)
                Travel(index, agent, Place.Workplace);
            else
            {
                int minutesLeft = (agent.WorkStartHour + _settings.WorkingHours) * 60 - minuteOfDay;
                Schedule(index, Math.Min(60, minutesLeft), Activity.Work);
                _startedWork++;
                if (Observer != null) Observe(agent, "work", agent.Source.Profession ?? "Assigned simulation employment");
            }
            return;
        }

        if (agent.Energy < 28 && agent.Location == Place.Home)
        {
            Schedule(index, 90, Activity.Sleep);
            _startedSleep++;
            if (Observer != null) Observe(agent, "rest", "Recovering fatigue at home");
            return;
        }

        if (agent.Location != Place.Home && (hour >= 18 || agent.Energy < 28))
            Travel(index, agent, Place.Home);
        else
            Schedule(index, 40, Activity.Idle);
    }

    private void Travel(int index, NpcState agent, Place destination)
    {
        agent.Destination = destination;
        Schedule(index, _settings.TravelMinutes, Activity.Travel);
        if (Observer != null) Observe(agent, "travel", $"{agent.Location} to {destination}");
    }

    private void Schedule(int index, long minutes, Activity activity)
    {
        if (minutes < 1) throw new InvalidOperationException("Zero-length activity");
        var agent = _agents[index];
        agent.CurrentActivity = activity;
        agent.ActivityStartMinute = Minute;
        agent.ActivityEndMinute = checked(Minute + minutes);
        _events.Enqueue(index, (agent.ActivityEndMinute, _nextOrder++));
    }

    private void ReplenishFood()
    {
        int production = checked(_agents.Count * _settings.DailyFoodProductionPerNpc);
        FarmStock = checked(FarmStock + production);
        _foodProduced += production;

        int targetStock = checked(_agents.Count * 2);
        int want = Math.Max(0, targetStock - ShopStock);
        int available = Math.Min(want, FarmStock);
        int bought = _settings.WholesalePrice == 0 ? available :
            (int)Math.Min(available, ShopGold / _settings.WholesalePrice);
        if (bought > 0)
        {
            long payment = checked((long)bought * _settings.WholesalePrice);
            ShopGold -= payment;
            FarmGold += payment;
            FarmStock -= bought;
            ShopStock += bought;
            _foodRestocked += bought;
        }
    }

    private void Observe(NpcState agent, string action, string detail) =>
        Observer?.Invoke(new SimObservation(Minute, agent.Source.Id, action, detail));

    private long TotalGold() =>
        checked(_agents.Sum(x => x.Gold) + ShopGold + EmployerGold + FarmGold);

    private long TotalFood() =>
        checked(_agents.Sum(x => (long)x.Food) + ShopStock + FarmStock);

    public SimulationReport Report()
    {
        long gold = TotalGold(), food = TotalFood();
        return new SimulationReport
        {
            Npcs = _agents.Count,
            Minute = Minute,
            EventsProcessed = _eventsProcessed,
            DecisionCount = _decisions,
            WagesPaid = _wages,
            GoodsSold = _foodSold,
            GoodsEaten = _foodEaten,
            GoodsProduced = _foodProduced,
            GoodsRestocked = _foodRestocked,
            JourneysCompleted = _journeys,
            UnpaidHours = _unpaid,
            ShopStock = ShopStock,
            FarmStock = FarmStock,
            ShopGold = ShopGold,
            EmployerGold = EmployerGold,
            FarmGold = FarmGold,
            TotalGold = gold,
            InitialTotalGold = _initialGold,
            TotalFoodRemaining = food,
            InitialTotalFood = _initialFood,
            ActionsSleeping = _startedSleep,
            ActionsWorking = _startedWork
        };
    }

    public void CheckInvariants()
    {
        var report = Report();
        if (report.TotalGold != report.InitialTotalGold)
            throw new InvalidOperationException("Gold was created or destroyed outside a transaction");
        if (report.TotalFoodRemaining + report.GoodsEaten !=
            report.InitialTotalFood + report.GoodsProduced)
            throw new InvalidOperationException("Food inventory accounting mismatch");
        if (ShopGold < 0 || EmployerGold < 0 || FarmGold < 0 ||
            ShopStock < 0 || FarmStock < 0)
            throw new InvalidOperationException("Negative business account or inventory");
        foreach (var actor in _agents)
        {
            if (actor.Gold < 0 || actor.Food < 0 ||
                actor.Satiation is < 0 or > 100 ||
                actor.Energy is < 0 or > 100 ||
                actor.ActivityEndMinute < actor.LastUpdatedMinute)
                throw new InvalidOperationException($"Invalid simulated NPC state: {actor.Source.Id}");
        }
    }
}
