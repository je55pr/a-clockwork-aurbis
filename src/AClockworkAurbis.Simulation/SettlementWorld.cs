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
    private readonly Dictionary<string,string> _placeNames =
        new(StringComparer.Ordinal);
    private BusinessState[] _foodShops = [];
    private BusinessState[] _goodsShops = [];
    private readonly Dictionary<string,BusinessState> _byBusiness =
        new(StringComparer.Ordinal);
    private readonly PriorityQueue<int,(long Minute,long Order)> _queue = new();
    private long _sequence;
    private long _initialGold,_initialFood,_initialMaterials,_initialGoods;
    private long _events,_wages,_dividends,_unpaid,_foodSales,_goodsSales,_meals,_travel;
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
           _settings.FoodWholesalePrice<0 || _settings.MaterialWholesalePrice<0 ||
           _settings.GoodsWholesalePrice<0 ||
           _settings.FoodImportPerResident<0 || _settings.MaterialImportPerTenResidents<0 ||
           _settings.StartHour is <0 or >23)
            throw new ArgumentOutOfRangeException(nameof(settings));

        var places=catalog.Places.ToDictionary(p=>p.Id,StringComparer.Ordinal);
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
        if(!_businesses.Any(b=>b.Kind==BusinessKinds.ImportHub))
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
        }

        foreach(var person in catalog.Residents.OrderBy(x=>x.Id,StringComparer.Ordinal))
        {
            int start=7+StableRandom.Between(person.Id,_settings.Seed,22,3);
            int cash=22+StableRandom.Between(person.Id,_settings.Seed,23,50);
            int food=StableRandom.Between(person.Id,_settings.Seed,24,3);
            double satiation=55+StableRandom.Between(person.Id,_settings.Seed,25,40);
            double energy=55+StableRandom.Between(person.Id,_settings.Seed,26,40);
            string home=person.HomePlaceId ?? "sim:unmapped-home:"+person.Id;
            var state=new ResidentState(person,home,cash,food,satiation,energy,start);
            int idx=_residents.Count;
            _residents.Add(state);
            _residentById.Add(person.Id,state);
            Enqueue(idx,1+StableRandom.Between(person.Id,_settings.Seed,27,40),
                SettlementAction.Idle);
        }

        // The wiki identifies vendors, not legal owners. A vendor may be
        // assigned a *simulated* leaseholding role without ever changing
        // the source OwnerId field or claiming this is ESO canon.
        _foodShops=_businesses.Where(b=>b.Kind==BusinessKinds.FoodRetail).ToArray();
        _goodsShops=_businesses.Where(b=>b.Kind is BusinessKinds.GoodsRetail or BusinessKinds.Workshop).ToArray();
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
            var actor=_residents[index];
            AdvanceNeeds(actor,Minute);
            CompleteAction(actor);
            Decide(index,actor);
        }
        Minute=minute;
        foreach(var actor in _residents)AdvanceNeeds(actor,Minute);
    }

    private static void AdvanceNeeds(ResidentState actor,long minute)
    {
        long delta=minute-actor.LastUpdate;
        if(delta<0)throw new InvalidOperationException("NPC time cannot run backwards");
        if(delta==0)return;
        actor.Satiation=Math.Clamp(actor.Satiation-delta*0.044,0,100);
        double rate=actor.Activity switch
        {
            SettlementAction.Sleep=>0.16,
            SettlementAction.Work=>-0.076,
            _=>-0.053
        };
        actor.Energy=Math.Clamp(actor.Energy+delta*rate,0,100);
        if(actor.Activity==SettlementAction.Work)actor.WorkMinutes+=delta;
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
                Observe(actor,"arrive",DisplayPlace(actor.CurrentPlaceId));
                break;
            case SettlementAction.Eat:
                if(actor.Food>0)
                {
                    actor.Food--;
                    actor.Satiation=Math.Min(100,actor.Satiation+56);
                    actor.MealsEaten++;
                    _meals++;
                    Observe(actor,"eat","Consumed 1 food; satiation "+actor.Satiation.ToString("0"));
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
        }
    }
}
