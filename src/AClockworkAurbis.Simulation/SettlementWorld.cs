namespace AClockworkAurbis.Simulation;

/// <summary>
/// Fast, standalone source-backed settlement simulation. Source places and NPC
/// associations are immutable; account balances, inventory, needs and journeys
/// are explicit simulated state. Rendering and pathfinding are intentionally absent.
/// </summary>
public sealed partial class SettlementWorld
{
    private const int DayMinutes = 1440;
    private readonly SettlementSettings _settings;
    private readonly List<ResidentState> _residents = new();
    private readonly List<BusinessState> _businesses = new();
    private readonly Dictionary<string,ResidentState> _residentById =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string,PlaceDefinition> _sourcePlaces;
    private readonly Dictionary<string,string> _placeNames =
        new(StringComparer.Ordinal);
    private BusinessState[] _foodShops = [];
    private BusinessState[] _goodsShops = [];
    private readonly Dictionary<string,BusinessState> _byBusiness =
        new(StringComparer.Ordinal);
    private readonly PriorityQueue<int,(long Minute,long Order)> _queue = new();
    private long _sequence;
    private decimal _initialGold,_wages,_dividends;
    private long _initialFood,_initialMaterials,_initialGoods;
    private long _events,_unpaid,_foodSales,_goodsSales,_meals,_travel;
    private long _travelMinutes,_longJourneys;
    private long _crafted,_goodsUsed,_foodImported,_materialsImported;
    private long _purchases;
    public long Minute { get; private set; }
    public SettlementCatalog Catalog { get; }
    public IReadOnlyList<ResidentState> Residents => _residents;
    public IReadOnlyList<BusinessState> Businesses => _businesses;
    public Action<SimObservation>? Observer { get; set; }

    public SettlementWorld(SettlementCatalog catalog, SettlementSettings? settings=null)
    {
        catalog.Validate();
        Catalog=catalog;
        _settings=settings ?? new SettlementSettings();
        if(_settings.Seed==int.MinValue || _settings.MealPrice<=0 ||
           _settings.GoodsPrice<=0 || _settings.HourlyWage<0 ||
           _settings.StartingBusinessGold<0 || _settings.BaseTravelMinutes<1 ||
           _settings.LocalTravelMinutes<1 ||
           _settings.FoodWholesalePrice<0 || _settings.MaterialWholesalePrice<0 ||
           _settings.GoodsWholesalePrice<0 ||
           _settings.FoodImportPerResident<0 || _settings.MaterialImportPerTenResidents<0 ||
           _settings.StartHour is <0 or >23 ||
           _settings.MapUnitsPerTravelMinute<1 ||
           _settings.UnknownMapInterdistrictMinutes<0 ||
           _settings.FoodSalesTax<0 || _settings.GoodsSalesTax<0 ||
           _settings.FoodSalesTax>_settings.MealPrice ||
           _settings.GoodsSalesTax>_settings.GoodsPrice ||
           _settings.DetailedFoodTaxRate is <0m or >1m ||
           _settings.DetailedGoodsTaxRate is <0m or >1m ||
           _settings.InitialOutsideGold<0 || _settings.InitialOutsideFood<0 ||
           _settings.InitialOutsideMaterials<0 || _settings.InitialPublicGoldPerResident<0 ||
           _settings.DailyLandRegrowthPerResident<0 || _settings.InitialMineralReservePerSite<0 ||
           _settings.RenewableFoodPerWorkHour<1 || _settings.RawMaterialPerWorkHour<1 ||
           _settings.DailyForeignFoodDemandPerResident<0 ||
           _settings.DailyForeignGoodsDemandPerResident<0 ||
           _settings.DailyImportCapPerTenResidents<0 ||
           _settings.DailySharedRent<0 || _settings.RentGoldReserve<0 ||
           _settings.PortCargoUnitsPerWorkHour<0 ||
           _settings.MaxPortCargoCapacity<0 ||
           _settings.MaxFreightLoad<1 || _settings.BaseFreightFee<0 ||
           _settings.ToolWorkHoursPerWear<1 || _settings.FirewoodDaysPerHousehold<1 ||
           _settings.MaxCommodityStockPerSite<1 ||
           _settings.PublicForagePerFarmPerDay<0 ||
           _settings.FreightFeePer90Minutes<0 ||
           _settings.ExternalFoodImportPrice<0 ||
           _settings.ExternalMaterialImportPrice<0 || _settings.DailyAssistanceLimit<0 ||
           _settings.RegularGuaranteedHoursPerDay is <0 or >8 ||
           _settings.CasualHourlyWage<0 || _settings.MaxCasualHoursPerDay is <0 or >8 ||
           _settings.WildForagePerResidentPerDay<0 ||
           _settings.OpenOrePerResident is <0 or >1000 ||
           _settings.OpenLogsPerResidentPerDay is <0 or >1000 ||
           _settings.OpenFishPerResidentPerDay is <0 or >1000 ||
           _settings.OpenGamePerResidentPerDay is <0 or >1000 ||
           _settings.MaxPersonalCommodityStock is <1 or >10000 ||
           _settings.AutonomousParticipationPercent is <0 or >100 ||
           _settings.AutonomousDailyActionLimit is <0 or >8 ||
           _settings.AutonomousMaxTravelMinutes is <1 or >1440 ||
           _settings.AutonomousToolPurchaseGoldReserve is <0m or >1000000m ||
           _settings.PublicPayrollHorizonDays is <1 or >90 ||
           _settings.PublicMinimumPaidHoursPerWorker is <0 or >8 ||
           _settings.PublicTargetPaidHoursPerWorker is <0 or >8 ||
           _settings.PublicMinimumPaidHoursPerWorker>_settings.PublicTargetPaidHoursPerWorker ||
           _settings.PublicUnfundedLeaveDays is <1 or >365 ||
           _settings.PrivateHireRunwayDays is <1 or >90 ||
           _settings.PrivateRehireCooldownDays is <0 or >365)
            throw new ArgumentOutOfRangeException(nameof(settings));

        _sourcePlaces=catalog.Places.ToDictionary(p=>p.Id,StringComparer.Ordinal);
        var places=_sourcePlaces;
        foreach(var place in catalog.Places)_placeNames.Add(place.Id,place.Name);
        foreach(var def in catalog.Businesses.OrderBy(b=>b.PlaceId,StringComparer.Ordinal))
        {
            var business=new BusinessState(def,places[def.PlaceId],
                _settings.StartingBusinessGold);
            _businesses.Add(business);
            _byBusiness.Add(business.Id,business);
        }

        // If a settlement lacks a documented dock/port, imports remain
        // possible via an explicitly synthetic outside-trade depot.
        if(!Detailed && !_businesses.Any(b=>b.Kind==BusinessKinds.ImportHub))
        {
            const string id="sim:external-trade-depot";
            var generated=new BusinessState(
                new BusinessDefinition(id,BusinessKinds.ImportHub,null,[],
                    "SIMULATION DEFAULT, no source-backed port found"),
                new PlaceDefinition(id,0,"Outside trade (simulation default)",
                    null,catalog.Settlement,null,"simulation_default",""),
                _settings.StartingBusinessGold);
            _businesses.Add(generated);
            _byBusiness.Add(generated.Id,generated);
            _placeNames.Add(generated.Id,generated.Name);
            _sourcePlaces.Add(generated.Id,generated.Place);
        }

        foreach(var person in catalog.Residents.OrderBy(x=>x.Id,StringComparer.Ordinal))
        {
            int start=7+StableRandom.Between(person.Id,_settings.Seed,22,3);
            int cash=22+StableRandom.Between(person.Id,_settings.Seed,23,50);
            int food=StableRandom.Between(person.Id,_settings.Seed,24,3);
            double satiation=55+StableRandom.Between(person.Id,_settings.Seed,25,40);
            double energy=55+StableRandom.Between(person.Id,_settings.Seed,26,40);
            string home=person.HomePlaceId ?? "sim:unmapped-home:"+person.Id;
            var state=new ResidentState(person,home,cash,food,satiation,energy,start)
            {
                RiskTolerance=StableRandom.Between(person.Id,_settings.Seed,31,101)
            };
            int idx=_residents.Count;
            _residents.Add(state);
            _residentById.Add(person.Id,state);
            Enqueue(idx,1+StableRandom.Between(person.Id,_settings.Seed,27,40),
                SettlementAction.Idle);
        }

        // The wiki identifies vendors, not legal owners. A vendor may be
        // assigned a *simulated* leaseholding role without ever changing
        // the source OwnerId field or claiming this is ESO canon.
        _foodShops=_businesses.Where(b=>b.Kind==BusinessKinds.FoodRetail ||
            Detailed && b.Kind==BusinessKinds.Bakery).ToArray();
        _goodsShops=_businesses.Where(b=>b.Kind is BusinessKinds.GoodsRetail or
            BusinessKinds.Workshop || Detailed && b.Kind is
            BusinessKinds.Sawmill or BusinessKinds.Leatherworker or
            BusinessKinds.Tailor).ToArray();
        foreach(var business in _businesses)
        {
            if(business.OwnerId is not null)
            {
                business.SimulatedLeaseholderId=business.OwnerId;
                business.LeaseEvidence="explicit_source_owner";
            }
            else if(business.Kind is BusinessKinds.FoodRetail or
                        BusinessKinds.GoodsRetail or BusinessKinds.Workshop)
            {
                string? selected=business.Operators.FirstOrDefault(_residentById.ContainsKey);
                if(selected is not null)
                {
                    business.SimulatedLeaseholderId=selected;
                    business.LeaseEvidence="simulation_default_from_known_vendor";
                }
            }
        }
        // Goods in the economy initially exist only as explicitly marked
        // simulation assumptions. All further stock changes are accounted for.
        foreach(var business in _businesses)
        {
            if(business.Kind==BusinessKinds.FoodRetail)business.Food=5;
            if(business.Kind==BusinessKinds.GoodsRetail)business.Goods=2;
        }
        if (Regional)
        {
            InitializeRegional();
            if (Detailed)
            {
                InitializeCommodityEconomy();
                // A documented chef working at a shop or manor can prepare
                // food there; the place's original ESO type is unchanged.
                _foodShops=_businesses.Where(b=>
                    b.Kind is BusinessKinds.FoodRetail or BusinessKinds.Bakery ||
                    HasNamedChef(b)).ToArray();
            }
            if (_settings.EnableHousing) InitializeHousing();
            InitializeWildForage();
            InitializeOpenResources();
        }
        _initialGold=TotalGold();
        _initialFood=TotalFood();
        _initialMaterials=TotalMaterials();
        _initialGoods=TotalGoods();
        _queue.Enqueue(-1,(_settings.StartHour*60L,_sequence++));
    }

    private void Enqueue(int personIndex,long minutes,SettlementAction action)
    {
        if(minutes<=0)throw new InvalidOperationException("Activity length must be positive");
        var actor=_residents[personIndex];
        actor.Activity=action;
        actor.ActionStart=Minute;
        if (action==SettlementAction.Sleep)
            actor.RoughSleeping=Regional && _settings.EnableHousing &&
                actor.HousingId is null;
        _queue.Enqueue(personIndex,(checked(Minute+minutes),_sequence++));
    }

    public void AdvanceTo(long minute)
    {
        if(minute<Minute||minute<0)throw new ArgumentOutOfRangeException(nameof(minute));
        while(_queue.TryPeek(out _,out var eventTime)&&eventTime.Minute<=minute)
        {
            int index=_queue.Dequeue();
            Minute=eventTime.Minute;
            _events++;
            if(index==-1)
            {
                DeliverImportsAndRestock();
                _queue.Enqueue(-1,(checked(Minute+DayMinutes),_sequence++));
                continue;
            }
            if(index <= -2)
            {
                DeliverFreight(index);
                continue;
            }
            var actor=_residents[index];
            AdvanceNeeds(actor,Minute);
            CompleteAction(actor);
            Decide(index,actor);
        }
        Minute=minute;
        foreach(var actor in _residents)AdvanceNeeds(actor,Minute);
    }

    private void AdvanceNeeds(ResidentState actor,long minute)
    {
        long delta=minute-actor.LastUpdate;
        if(delta<0)throw new InvalidOperationException("NPC time cannot run backwards");
        if(delta==0)return;
        // Integrate deprivation analytically over scheduled events. Snapshot
        // hunger at day 90 alone can conceal repeated short starvation.
        const double hungerRate=0.044;
        double initialSatiation=actor.Satiation;
        actor.MinutesBelow20Satiation+=Math.Clamp((double)delta -
            Math.Max(0d,(initialSatiation-20d)/hungerRate),0d,(double)delta);
        actor.MinutesAtZeroSatiation+=Math.Clamp((double)delta -
            initialSatiation/hungerRate,0d,(double)delta);
        actor.Satiation=Math.Round(Math.Clamp(initialSatiation-delta*hungerRate,0,100),3);
        double rate=actor.Activity switch
        {
            SettlementAction.Sleep=>RegionalSleepRate(actor),
            SettlementAction.Work or SettlementAction.CasualWork or
                SettlementAction.PersonalGather or SettlementAction.PersonalCraft=>-0.076,
            _=>-0.053
        };
        actor.Energy=Math.Round(Math.Clamp(actor.Energy+delta*rate,0,100),3);
        if(actor.Activity is SettlementAction.Work or SettlementAction.CasualWork or
            SettlementAction.PersonalGather or SettlementAction.PersonalCraft)
            actor.WorkMinutes+=delta;
        if(actor.Activity==SettlementAction.Sleep)actor.SleepMinutes+=delta;
        actor.LastUpdate=minute;
    }

    private void CompleteAction(ResidentState actor)
    {
        switch(actor.Activity)
        {
            case SettlementAction.Travel:
                actor.CurrentPlaceId=actor.DestinationPlaceId;
                actor.Journeys++;
                _travel++;
                long trip=Minute-actor.ActionStart;
                actor.JourneyMinutes+=trip;
                _travelMinutes+=trip;
                if(trip>=60)_longJourneys++;
                Observe(actor,"arrive",DisplayPlace(actor.CurrentPlaceId));
                break;
            case SettlementAction.Eat:
                if(actor.Food>0)
                {
                    if(Detailed)EatCommodityMeal(actor);
                    else
                    {
                        actor.Food--;
                        actor.Satiation=Math.Min(100,actor.Satiation+56);
                        actor.MealsEaten++;
                        _meals++;
                        Observe(actor,"eat","Consumed 1 food; satiation "+actor.Satiation.ToString("0"));
                    }
                }
                break;
            case SettlementAction.Work:
                CompleteWork(actor);
                break;
            case SettlementAction.BuyFood:
                CompletePurchase(actor,food:true);
                break;
            case SettlementAction.BuyGoods:
                CompletePurchase(actor,food:false);
                break;
            case SettlementAction.BuyGroceries:
                CompleteGroceryPurchase(actor);
                break;
            case SettlementAction.CookAtHome:
                CompleteHomeCooking(actor);
                break;
            case SettlementAction.HarvestFood:
                CompleteSubsistenceHarvest(actor);
                break;
            case SettlementAction.CasualWork:
                CompleteCasualShift(actor);
                break;
            case SettlementAction.PersonalGather:
                CompletePersonalTask(actor,PersonalTaskKind.Gather);
                break;
            case SettlementAction.PersonalCraft:
                CompletePersonalTask(actor,PersonalTaskKind.Craft);
                break;
            case SettlementAction.PersonalBuy:
                CompletePersonalTask(actor,PersonalTaskKind.Buy);
                break;
            case SettlementAction.PersonalSell:
                CompletePersonalTask(actor,PersonalTaskKind.Sell);
                break;
            case SettlementAction.VisitTavern:
                CompleteTavernVisit(actor);
                break;
        }
    }
}
