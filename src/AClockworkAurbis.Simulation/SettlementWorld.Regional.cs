namespace AClockworkAurbis.Simulation;

/// <summary>
/// Explicit island economy: renewable land has a daily harvest capacity,
/// mines have finite reserves, labour converts capacity to inventory, businesses
/// pay wages from real accounts, outside trade crosses a two-sided ledger,
/// and public aid must be funded. No game engine or source-asset dependencies.
/// </summary>
public sealed partial class SettlementWorld
{
    private long _publicGold, _outsideGold, _initialAllGold;
    private int _outsideFood, _outsideMaterials, _outsideGoods;
    private int _initialOutsideFood, _initialOutsideMaterials, _initialOutsideGoods;
    private long _localFood, _subsistenceFood, _localMaterials;
    private long _foodExports, _goodsExports, _materialsExports, _goldImportsPaid, _goldExportsEarned;
    private long _aidPaid, _taxRevenue, _cooperativeDistributions;
    private long _foodImportsPaidUnits, _materialsImportsPaidUnits;
    private readonly HashSet<string> _aidRecipients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ResidentState>> _staff = new(StringComparer.Ordinal);
    private BusinessState[] _farms = [];
    private BusinessState[] _extractors = [];
    private BusinessState[] _ports = [];
    private BusinessState[] _workshops = [];
    private int _simulatedContracts;
    private long _furloughs, _rehired;

    private bool Regional => _settings.Economy == EconomyMode.RegionalProduction;

    private void InitializeRegional()
    {
        _outsideGold = _settings.InitialOutsideGold;
        _outsideFood = _initialOutsideFood = _settings.InitialOutsideFood;
        _outsideMaterials = _initialOutsideMaterials = _settings.InitialOutsideMaterials;
        _initialOutsideGoods = _outsideGoods = 0;
        _publicGold = checked((long)_residents.Count * _settings.InitialPublicGoldPerResident);
        _farms = _businesses.Where(b => b.Kind == BusinessKinds.FoodProducer).ToArray();
        _extractors = _businesses.Where(b => b.Kind == BusinessKinds.ResourceProducer).ToArray();
        _ports = _businesses.Where(b => b.Kind == BusinessKinds.ImportHub).ToArray();
        _workshops = _businesses.Where(b => b.Kind == BusinessKinds.Workshop).ToArray();

        foreach (var business in _businesses)
        {
            business.MineralReserve = business.Kind == BusinessKinds.ResourceProducer
                ? _settings.InitialMineralReservePerSite : 0;
            _staff.Add(business.Id, new List<ResidentState>());
        }

        // Source links take precedence over all workforce planning.
        foreach (var person in _residents)
        {
            if (person.Source.WorkPlaceId is not string workplace ||
                !_staff.TryGetValue(workplace, out var personnel)) continue;
            person.EmploymentEvidence = person.Source.WorkEvidence;
            personnel.Add(person);
        }

        // These are hypothetical labour contracts, not UESP occupations.
        // The NPC's immutable profession, location and source association
        // remain untouched. Contract capacity is finite and depends on sector.
        foreach (var person in _residents.Where(p => p.WorkId is null))
        {
            BusinessState? choice = null;
            long best = long.MaxValue;
            foreach (var employer in _businesses)
            {
                var current = _staff[employer.Id].Count;
                if (current >= ContractCapacity(employer)) continue;
                long route = JourneyDuration(person, employer.Id);
                ulong tie = StableRandom.Hash(person.Source.Id + employer.Id, _settings.Seed);
                long score = route * 10L + (long)(tie % 30UL);
                // A job involving someone's known profession is more
                // plausible than an otherwise equally distant contract.
                string profession = person.Source.Profession?.ToLowerInvariant() ?? "";
                if (employer.Kind == BusinessKinds.FoodProducer &&
                    (profession.Contains("farm") || profession.Contains("grower")))
                    score = Math.Max(0, score - 300);
                if (employer.Kind == BusinessKinds.ResourceProducer &&
                    (profession.Contains("miner") || profession.Contains("wood")))
                    score = Math.Max(0, score - 300);
                if (score < best) { best = score; choice = employer; }
            }
            if (choice is null) continue;
            // Refuse a commute of several hours as an ordinary daily job.
            if (JourneyDuration(person, choice.Id) > 130) continue;
            person.AssignedWorkPlaceId = choice.Id;
            person.EmploymentEvidence = "simulated_labor_contract";
            _staff[choice.Id].Add(person);
            _simulatedContracts++;
        }
        foreach (var employer in _businesses)
            employer.WorkerCount = _staff[employer.Id].Count;
        _initialAllGold = checked(TotalGold() + _outsideGold);
    }

    private static int ContractCapacity(BusinessState employer) =>
        employer.Kind switch
        {
            BusinessKinds.FoodProducer => 7,
            BusinessKinds.ResourceProducer => 4,
            BusinessKinds.Workshop => 5,
            BusinessKinds.FoodRetail => 3,
            BusinessKinds.GoodsRetail => 3,
            BusinessKinds.ImportHub => 3,
            BusinessKinds.Institution => 2,
            _ => 0
        };

    private bool CompleteRegionalWork(ResidentState actor, BusinessState employer, int hours)
    {
        if (!Regional) return false;
        if (!_settings.EnableLocalProduction || hours <= 0) return true;
        switch (employer.Kind)
        {
            case BusinessKinds.FoodProducer:
            {
                int picked = Math.Min(employer.LandYieldRemaining,
                    checked(hours * _settings.RenewableFoodPerWorkHour));
                if (picked <= 0) break;
                employer.LandYieldRemaining -= picked;
                employer.Food = checked(employer.Food + picked);
                employer.FoodProduced += picked;
                _localFood += picked;
                Observe(actor, "harvest", $"{picked} food at {employer.Name}");
                break;
            }
            case BusinessKinds.ResourceProducer:
            {
                int collected = Math.Min(employer.MineralReserve,
                    checked(hours * _settings.RawMaterialPerWorkHour));
                if (collected <= 0) break;
                employer.MineralReserve -= collected;
                employer.Materials = checked(employer.Materials + collected);
                employer.MaterialsExtracted += collected;
                _localMaterials += collected;
                Observe(actor, "extract", $"{collected} materials at {employer.Name}");
                break;
            }
            case BusinessKinds.Workshop:
            {
                int made = Math.Min(hours, employer.Materials);
                if (made <= 0) break;
                employer.Materials -= made;
                employer.Goods += made;
                employer.ItemsCrafted += made;
                _crafted += made;
                Observe(actor, "crafted", $"{made} goods at {employer.Name}");
                break;
            }
        }
        return true;
    }

    private BusinessState? SubsistencePlot(ResidentState actor)
    {
        BusinessState? nearest = null;
        int distance = int.MaxValue;
        foreach (var farm in _farms)
        {
            if (farm.LandYieldRemaining < 2) continue;
            int trip = JourneyDuration(actor, farm.Id);
            if (trip < distance)
            {
                nearest = farm;
                distance = trip;
            }
        }
        return nearest;
    }

    private bool ChooseSubsistence(int index, ResidentState actor)
    {
        if (!Regional || !_settings.EnableLocalProduction || _farms.Length == 0)
            return false;
        var plot = SubsistencePlot(actor);
        if (plot is null) return false;
        // At this scale foraging represents one hour of productive labour in
        // an explicitly simulated cooperative allotment, not free food.
        if (actor.CurrentPlaceId == plot.Id)
            Enqueue(index, 60, SettlementAction.HarvestFood);
        else Travel(index, actor, plot.Id);
        return true;
    }

    private void CompleteSubsistenceHarvest(ResidentState actor)
    {
        if (!Regional || !_byBusiness.TryGetValue(actor.CurrentPlaceId, out var plot) ||
            plot.Kind != BusinessKinds.FoodProducer || !_settings.EnableLocalProduction)
            return;
        int harvested = Math.Min(2, plot.LandYieldRemaining);
        plot.LandYieldRemaining -= harvested;
        actor.Food += harvested;
        actor.SubsistenceFoodHarvested += harvested;
        _subsistenceFood += harvested;
        _localFood += harvested;
        if (harvested > 0)
            Observe(actor, "subsistence", $"{harvested} food grown at {plot.Name} (60 minutes labour)");
    }

    private void RunRegionalDay()
    {
        if (_residents.Count == 0) return;
        // Environmental productivity: land regenerates a bounded harvest
        // potential once a day, not an inventory of magically delivered food.
        if (_settings.EnableLocalProduction)
        {
            int perFarm = _farms.Length == 0 ? 0 :
                Math.Max(10, checked(_residents.Count *
                    _settings.DailyLandRegrowthPerResident / _farms.Length));
            foreach (var farm in _farms)
                farm.LandYieldRemaining = Math.Min(checked(perFarm * 2),
                    farm.LandYieldRemaining + perFarm);
            foreach (var producer in _extractors)
                if (producer.Place.SourceType == "Grove")
                    producer.MineralReserve = Math.Min(
                        _settings.InitialMineralReservePerSite,
                        checked(producer.MineralReserve + 16)); // renewable forestry
        }

        if (Minute > DayMinutes && Minute / DayMinutes % 5 == 0)
            foreach (var person in _residents)
                if (person.Goods > 0) { person.Goods--; _goodsUsed++; }

        DistributeRegionalSurplus();
        FundPublicServices();
        ReassignSolventEmployment();
        ProvideFundedAssistance();
        RegionalWholesaleAndTrade();
    }

    private void DistributeRegionalSurplus()
    {
        foreach (var business in _businesses)
        {
            long surplus = business.Gold - _settings.StartingBusinessGold;
            if (surplus <= 50) continue;
            if (business.Kind == BusinessKinds.FoodProducer &&
                _staff.TryGetValue(business.Id, out var workers) && workers.Count > 0)
            {
                // Co-ops share operating surplus with paid workers and the
                // community. Not a fictitious private shop ownership claim.
                long budget = Math.Min(surplus / 3, 500);
                long perWorker = Math.Min(25, budget / workers.Count);
                foreach (var worker in workers)
                {
                    if (perWorker <= 0) break;
                    business.Gold -= perWorker;
                    worker.Gold += perWorker;
                    worker.CooperativeIncome += perWorker;
                    _cooperativeDistributions += perWorker;
                }
            }
            else if (business.Kind is BusinessKinds.ImportHub or
                BusinessKinds.GoodsRetail or BusinessKinds.FoodRetail)
            {
                // Progressive port revenue recirculation avoids permanent
                // currency traps in a monopolistic export gateway.
                long levy = business.Kind == BusinessKinds.ImportHub ?
                    surplus / 3 : Math.Min(surplus / 10, 100);
                if (levy <= 0) continue;
                business.Gold -= levy;
                _publicGold += levy;
                _taxRevenue += levy;
            }
        }
    }

    private void ReassignSolventEmployment()
    {
        foreach(var person in _residents)
        {
            if(person.WorkId is not null)continue;
            BusinessState? best=null;
            long score=long.MaxValue;
            foreach(var employer in _businesses)
            {
                if(employer.Gold < Math.Max(12, _settings.HourlyWage * 8) ||
                   _staff[employer.Id].Count >= ContractCapacity(employer))continue;
                int route=JourneyDuration(person,employer.Id);
                if(route>130)continue;
                long candidate=(long)route*10+
                    (long)(StableRandom.Hash(person.Source.Id+employer.Id,_settings.Seed)%31uL);
                if(person.Source.WorkPlaceId==employer.Id)
                    candidate=Math.Max(0,candidate-500);
                if(candidate<score){score=candidate;best=employer;}
            }
            if(best is null)continue;
            person.AssignedWorkPlaceId=best.Id;
            person.OnUnpaidLeave=false;
            person.EmploymentEvidence=person.Source.WorkPlaceId==best.Id?
                person.Source.WorkEvidence:"simulated_solvent_labor_contract";
            _staff[best.Id].Add(person);
            best.WorkerCount++;
            _rehired++;
        }
    }

    private void FurloughForInsolvency(ResidentState person, BusinessState employer)
    {
        if(!Regional)return;
        person.OnUnpaidLeave=true;
        person.AssignedWorkPlaceId=null;
        person.EmploymentEvidence="simulated_unpaid_leave";
        person.InsolvencyJobChanges++;
        _staff[employer.Id].Remove(person);
        employer.WorkerCount=_staff[employer.Id].Count;
        _furloughs++;
    }

    private void FundPublicServices()
    {
        foreach (var service in _businesses.Where(b => b.Kind == BusinessKinds.Institution))
        {
            if (service.WorkerCount == 0) continue;
            long payroll = (long)service.WorkerCount * 8 * _settings.HourlyWage;
            long missing = Math.Max(0, payroll - service.Gold);
            long granted = Math.Min(missing, _publicGold / 4);
            if (granted == 0) continue;
            _publicGold -= granted;
            service.Gold += granted;
        }
    }

    private void ProvideFundedAssistance()
    {
        if (!_settings.EnablePublicAssistance || _publicGold <= 0) return;
        // Stable rotation prevents first source IDs from always receiving aid
        // when the treasury cannot fund everyone.
        int count = _residents.Count;
        int start = (int)(Minute / DayMinutes % count);
        for (int i = 0; i < count && _publicGold > 0; i++)
        {
            var person = _residents[(start + i) % count];
            if (person.Gold >= _settings.MealPrice * 2) continue;
            long need = _settings.MealPrice * 2 - person.Gold;
            long grant = Math.Min(_publicGold, Math.Min(need,
                _settings.DailyAssistanceLimit));
            if (grant == 0) continue;
            _publicGold -= grant;
            person.Gold += grant;
            person.AssistanceGold += grant;
            _aidPaid += grant;
            _aidRecipients.Add(person.Source.Id);
        }
    }
}
