namespace AClockworkAurbis.Simulation;

/// <summary>
/// Explicit island economy: renewable land has a daily harvest capacity,
/// mines have finite reserves, labour converts capacity to inventory, businesses
/// pay wages from real accounts, outside trade crosses a two-sided ledger,
/// and public aid must be funded. No game engine or source-asset dependencies.
/// </summary>
public sealed partial class SettlementWorld
{
    private decimal _publicGold, _outsideGold, _initialAllGold;
    private int _outsideFood, _outsideMaterials, _outsideGoods;
    private int _initialOutsideFood, _initialOutsideMaterials, _initialOutsideGoods;
    private long _localFood, _subsistenceFood, _localMaterials;
    private long _foodExports, _goodsExports, _materialsExports;
    private decimal _goldImportsPaid, _goldExportsEarned;
    private decimal _aidPaid, _taxRevenue, _cooperativeDistributions;
    private decimal _retailSalesTaxRevenue;
    private long _foodImportsPaidUnits, _materialsImportsPaidUnits;
    private readonly HashSet<string> _aidRecipients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ResidentState>> _staff = new(StringComparer.Ordinal);
    private BusinessState[] _farms = [];
    private BusinessState[] _extractors = [];
    private BusinessState[] _ports = [];
    private BusinessState[] _workshops = [];
    private int _simulatedContracts;
    private long _furloughs, _rehired;

    private bool Regional => _settings.Economy is EconomyMode.RegionalProduction
        or EconomyMode.CommodityProduction;
    private bool Detailed => _settings.Economy == EconomyMode.CommodityProduction;

    private void InitializeRegional()
    {
        // Commodity mode is a closed island until foreign cargo is converted
        // to the same physical SKU ledger. Do not expose a phantom reserve.
        _outsideGold = Detailed ? 0 : _settings.InitialOutsideGold;
        _outsideFood = _initialOutsideFood =
            Detailed ? 0 : _settings.InitialOutsideFood;
        _outsideMaterials = _initialOutsideMaterials =
            Detailed ? 0 : _settings.InitialOutsideMaterials;
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
                if(route>130)continue;
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
                if (employer.Kind == BusinessKinds.GuardPost &&
                    (person.Source.Name.StartsWith("Watchman",StringComparison.OrdinalIgnoreCase) ||
                     person.Source.Name.StartsWith("Watchwoman",StringComparison.OrdinalIgnoreCase)))
                    score = Math.Max(0,score-500);
                if (employer.Kind == BusinessKinds.DomesticService &&
                    profession.Contains("steward"))
                    score = Math.Max(0,score-350);
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
        {
            employer.WorkerCount = _staff[employer.Id].Count;
            foreach(var worker in _staff[employer.Id])
                AssignServiceRole(worker,employer);
        }
        _initialAllGold = checked(TotalGold() + _outsideGold);
    }

    private static void AssignServiceRole(ResidentState worker,BusinessState employer)
    {
        string? role=employer.Kind switch
        {
            BusinessKinds.GuardPost => "guard",
            BusinessKinds.DomesticService => "servant",
            BusinessKinds.CleaningService => "cleaner",
            BusinessKinds.Institution when worker.Source.Name.StartsWith(
                "Watchman",StringComparison.OrdinalIgnoreCase) ||
                worker.Source.Name.StartsWith("Watchwoman",StringComparison.OrdinalIgnoreCase)
                => "guard",
            BusinessKinds.Institution when string.Equals(worker.Source.Profession,
                "Hall Steward",StringComparison.OrdinalIgnoreCase) => "servant",
            _ => null
        };
        worker.SimulatedServiceRole=role;
        worker.ServiceRoleEvidence=role is null?null:
            employer.Place.SourcePageId==0?"simulated_paid_service_contract":
            "source_name_or_profession_inferred_service";
    }

    private static int ContractCapacity(BusinessState employer) =>
        employer.Kind switch
        {
            BusinessKinds.FoodProducer => 7,
            BusinessKinds.Smeltery or BusinessKinds.Sawmill or BusinessKinds.Tannery
                or BusinessKinds.Bakery or BusinessKinds.Hunter or BusinessKinds.Fishery
                or BusinessKinds.Leatherworker => 5,
            BusinessKinds.ResourceProducer => 4,
            BusinessKinds.Workshop => 5,
            BusinessKinds.FoodRetail => 3,
            BusinessKinds.GoodsRetail => 3,
            BusinessKinds.ImportHub => 3,
            BusinessKinds.Institution => 2,
            BusinessKinds.GuardPost or BusinessKinds.CleaningService or
                BusinessKinds.DomesticService => 3,
            _ => 0
        };

    private bool CompleteRegionalWork(ResidentState actor, BusinessState employer, int hours)
    {
        if (!Regional) return false;
        if (Detailed) return CompleteCommodityWork(actor,employer,hours);
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
            case BusinessKinds.ImportHub:
            {
                // Paid stevedore labour creates actual throughput for the
                // next cargo transfers. An unstaffed port cannot ship freely.
                int available = Math.Max(0, _settings.MaxPortCargoCapacity - employer.CargoCapacity);
                int handled = (int)Math.Min(available,
                    (long)hours * _settings.PortCargoUnitsPerWorkHour);
                employer.CargoCapacity += handled;
                if (handled > 0)
                    Observe(actor, "dockwork", $"{handled} cargo capacity at {employer.Name}");
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
            if (farm.LandYieldRemaining < 2 ||
                Detailed && farm.ForageRemaining < 2) continue;
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
        if (!Regional || !_settings.EnableLocalProduction)return false;
        // The commons belong to no employer or farm. A person without gold
        // can forage directly rather than needing a paid harvesting job.
        if(TryWildForage(index,actor))return true;
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
        if(!Regional || !_settings.EnableLocalProduction)return;
        if(_wildForageById.TryGetValue(actor.CurrentPlaceId,out var wild))
        {
            HarvestWildFood(actor,wild);
            return;
        }
        if(!_byBusiness.TryGetValue(actor.CurrentPlaceId,out var plot) ||
            plot.Kind != BusinessKinds.FoodProducer)return;
        if(Detailed){HarvestCommoditySubsistence(actor,plot);return;}
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
        if(!Detailed)RegrowWildFood();
        // Environmental productivity: land regenerates a bounded harvest
        // potential once a day, not an inventory of magically delivered food.
        if (_settings.EnableLocalProduction && !Detailed)
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

        if (!Detailed && Minute > DayMinutes && Minute / DayMinutes % 5 == 0)
            foreach (var person in _residents)
                if (person.Goods > 0) { person.Goods--; _goodsUsed++; }

        DistributeRegionalSurplus();
        FundPublicServices();
        // Aid must reach the resident before rent becomes due, otherwise
        // a solvent public treasury can evict an eligible tenant needlessly.
        ProvideFundedAssistance();
        ReassignSolventEmployment();
        RunHousingDay();
        if(Detailed)RunCommodityDay();
        else RegionalWholesaleAndTrade();
    }

    private bool IsDetailedProcessor(BusinessState business) =>
        Detailed && business.Kind is BusinessKinds.Bakery or BusinessKinds.Sawmill
            or BusinessKinds.Smeltery or BusinessKinds.Tannery
            or BusinessKinds.Workshop or BusinessKinds.Leatherworker;

    private void DistributeRegionalSurplus()
    {
        foreach (var business in _businesses)
        {
            decimal surplus = business.Gold - _settings.StartingBusinessGold;
            if (surplus <= 50) continue;
            if (business.Kind == BusinessKinds.FoodProducer &&
                _staff.TryGetValue(business.Id, out var workers) && workers.Count > 0)
            {
                // Co-ops share operating surplus with paid workers and the
                // community. Not a fictitious private shop ownership claim.
                decimal budget = Math.Min(Math.Round(surplus / 3,2), 500);
                decimal perWorker = Math.Min(25,Math.Round(budget / workers.Count,2));
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
                BusinessKinds.GoodsRetail or BusinessKinds.FoodRetail ||
                IsDetailedProcessor(business))
            {
                // Recycle some surplus without removing operating reserves.
                // Processors also participate, rather than capturing the
                // entire island's cash in a few profitable bakeries.
                int cap = IsDetailedProcessor(business) ? 600 : 100;
                decimal levy = business.Kind == BusinessKinds.ImportHub ?
                    Math.Round(surplus/3,2) :
                    Math.Min(Math.Round(surplus/10,2),cap);
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
                // Employment viability is based on home-to-work commuting,
                // not where somebody happens to be standing at 6 AM.
                int route=HomeCommuteMinutes(person,employer);
                if(route>130 && (!_settings.EnableHousing ||
                    AvailableHousingNear(person, employer) is null))continue;
                // Prefer nearby opportunities, but allow a fresh start in
                // another town when there is no local solvent work.
                long candidate=route>130 ? 2000L +
                    (long)(StableRandom.Hash(person.Source.Id+employer.Id,_settings.Seed)%31uL)
                    : (long)route*10+
                    (long)(StableRandom.Hash(person.Source.Id+employer.Id,_settings.Seed)%31uL);
                if(person.Source.WorkPlaceId==employer.Id)
                    candidate=Math.Max(0,candidate-500);
                if(employer.Kind==BusinessKinds.GuardPost &&
                    (person.Source.Name.StartsWith("Watchman",StringComparison.OrdinalIgnoreCase) ||
                     person.Source.Name.StartsWith("Watchwoman",StringComparison.OrdinalIgnoreCase)))
                    candidate=Math.Max(0,candidate-300);
                if(candidate<score){score=candidate;best=employer;}
            }
            if(best is null)continue;
            if (HomeCommuteMinutes(person, best) > 130 &&
                !RelocateForJob(person, best)) continue;
            person.AssignedWorkPlaceId=best.Id;
            person.OnUnpaidLeave=false;
            person.EmploymentEvidence=person.Source.WorkPlaceId==best.Id?
                person.Source.WorkEvidence:"simulated_solvent_labor_contract";
            _staff[best.Id].Add(person);
            best.WorkerCount++;
            AssignServiceRole(person,best);
            _rehired++;
            Observe(person, "hired", best.Name + ": solvent employment contract");
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
        Observe(person, "laid_off", employer.Name + ": payroll could not be funded");
    }

    private void FundPublicServices()
    {
        foreach (var service in _businesses.Where(b => b.Kind is
            BusinessKinds.Institution or BusinessKinds.GuardPost or
            BusinessKinds.CleaningService or BusinessKinds.DomesticService))
        {
            if (service.WorkerCount == 0) continue;
            long payroll = (long)service.WorkerCount * 8 * _settings.HourlyWage;
            decimal missing = Math.Max(0, payroll - service.Gold);
            decimal granted = Math.Min(missing, Math.Round(_publicGold/4,2));
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
            decimal target = EverydayMealCost * 2 +
                (Detailed && person.HousingId is not null ?
                    _settings.DailySharedRent * 2 : 0);
            if (person.Gold >= target) continue;
            decimal need = target - person.Gold;
            decimal grant = Math.Min(_publicGold, Math.Min(need,
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
