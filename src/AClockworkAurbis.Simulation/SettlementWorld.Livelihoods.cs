namespace AClockworkAurbis.Simulation;

// Ordinary employment and on-demand shifts share the same finite resources.
// Wild food is a separate, explicitly simulated commons, not somebody's
// commercial farm or a handout from the treasury.
internal sealed class WildForageSite(PlaceDefinition place,int dailyRegrowth)
{
    public PlaceDefinition Place { get; } = place;
    public string Id => Place.Id;
    public int DailyRegrowth { get; } = dailyRegrowth;
    public int Available { get; set; } = dailyRegrowth;
}

public sealed partial class SettlementWorld
{
    private readonly List<WildForageSite> _wildForageSites = [];
    private readonly Dictionary<string,WildForageSite> _wildForageById =
        new(StringComparer.Ordinal);
    private long _casualHours;
    private decimal _casualWages;
    private readonly HashSet<string> _casualWorkers = new(StringComparer.Ordinal);

    private static bool OpenResourceJob(BusinessState firm) =>
        firm.Kind is BusinessKinds.FoodProducer or BusinessKinds.ResourceProducer
            or BusinessKinds.Hunter or BusinessKinds.Fishery;

    private int CasualRate => Math.Min(_settings.CasualHourlyWage,
        Math.Max(0,_settings.HourlyWage-1));

    private void InitializeWildForage()
    {
        if(!Regional || !_settings.EnableLocalProduction ||
           _settings.WildForagePerResidentPerDay==0)return;
        int index=0;
        foreach(var group in _residents.GroupBy(p=>Neighbourhood(ResidentAnchor(p)))
            .OrderBy(g=>g.Key,StringComparer.Ordinal))
        {
            var sample=group.Select(ResidentAnchor).FirstOrDefault(p=>p?.MapX is not null)
                ?? group.Select(ResidentAnchor).FirstOrDefault();
            string id="sim:wild-forage:"+index++;
            int daily=checked(group.Count()*_settings.WildForagePerResidentPerDay);
            var place=new PlaceDefinition(id,0,
                "Wild forage near "+(sample?.District??sample?.Name??"unmapped country"),
                "simulation_renewable_wild_food",sample?.Settlement,
                Catalog.Settlement,"wild_foraging","",sample?.MapX,sample?.MapY,
                "simulation_at_source_neighbourhood",sample?.District);
            var site=new WildForageSite(place,daily);
            _wildForageSites.Add(site);
            _wildForageById.Add(id,site);
            _sourcePlaces.Add(id,place);
            _placeNames.Add(id,place.Name);
        }
    }

    private void RegrowWildFood()
    {
        if(!_settings.EnableLocalProduction)return;
        // A daily public harvest quota, restored at the start of each day.
        // This is not persistent ecological depletion; commercial farm stocks
        // and private resource deposits are separate pools.
        foreach(var site in _wildForageSites)
            site.Available=site.DailyRegrowth;
    }

    private WildForageSite? NearestWildFood(ResidentState person) =>
        _wildForageSites.Where(s=>s.Available>0)
            .OrderBy(s=>JourneyDuration(person,s.Id))
            .ThenBy(s=>s.Id,StringComparer.Ordinal).FirstOrDefault();

    private bool TryWildForage(int index,ResidentState person)
    {
        var site=NearestWildFood(person);
        if(site is null)return false;
        if(person.CurrentPlaceId!=site.Id)Travel(index,person,site.Id);
        else Enqueue(index,60,SettlementAction.HarvestFood);
        return true;
    }

    private void HarvestWildFood(ResidentState person,WildForageSite site)
    {
        int collected=Math.Min(2,site.Available);
        if(collected<=0)return;
        site.Available-=collected;
        if(Detailed)Extract(person.Stock,"foraged_greens",collected);
        person.Food=checked(person.Food+collected);
        person.SubsistenceFoodHarvested+=collected;
        _subsistenceFood+=collected;
        _localFood+=collected;
        Observe(person,"wild_forage",collected+
            " wild greens collected at "+site.Place.Name);
    }

    private bool CanCasualProduce(BusinessState company)
    {
        if(!_settings.EnableLocalProduction)return false;
        int cap=_settings.MaxCommodityStockPerSite;
        return company.Kind switch
        {
            BusinessKinds.FoodProducer =>
                company.LandYieldRemaining>0 &&
                CropMix(company).Any(c=>Quantity(company.Stock,c)<cap),
            BusinessKinds.ResourceProducer =>
                company.MineralReserve>0 &&
                Quantity(company.Stock,IsLoggingSite(company)?"felling_axe":"pickaxe")>0 &&
                Quantity(company.Stock,IsLoggingSite(company)?"logs":"iron_ore")<cap,
            BusinessKinds.Hunter =>
                company.LandYieldRemaining>0 &&
                Quantity(company.Stock,"raw_hide")<cap &&
                Quantity(company.Stock,"game_meat")<cap,
            BusinessKinds.Fishery =>
                company.LandYieldRemaining>0 && Quantity(company.Stock,"fish")<cap,
            BusinessKinds.Haulier => CanScheduleCargoWork(company),
            BusinessKinds.Brewery or BusinessKinds.Tailor =>
                CommodityRecipes.For(company).Any(r=>
                    RecipeAvailable(r,company.Stock,cap)),
            BusinessKinds.Bakery => CommodityRecipes.All.Any(r=>
                r.Industry==BusinessKinds.Bakery && r.Id!="chef_game_pie" &&
                RecipeAvailable(r,company.Stock,cap)),
            BusinessKinds.Smeltery or BusinessKinds.Sawmill or
                BusinessKinds.Tannery or BusinessKinds.Leatherworker or
                BusinessKinds.Workshop => CommodityRecipes.For(company).Any(r=>
                    RecipeAvailable(r,company.Stock,cap)),
            _=>false
        };
    }

    internal bool CanTakeCasualShift(BusinessState firm,ResidentState worker)
    {
        if(!Detailed || !(OpenResourceJob(firm) || IsDetailedProcessor(firm) ||
            firm.Kind==BusinessKinds.Haulier) ||
            !CanCasualProduce(firm) || CasualRate<=0 ||
            firm.Kind==BusinessKinds.Bakery &&
                !ProfessionalCookCanWork(worker,firm))return false;
        // Permanent staff have first claim on the day's planned work and
        // on the money needed to pay their minimum shifts.
        if(firm.PlannedHoursWorked<firm.PlannedHours &&
            Minute%DayMinutes<16*60)return false;
        int regularRemaining=Math.Max(0,
            _staff[firm.Id].Count(w=>w.WorkId==firm.Id)*
                _settings.RegularGuaranteedHoursPerDay-firm.PlannedHoursWorked);
        decimal reserved=regularRemaining*_settings.HourlyWage;
        return firm.Gold>=reserved+CasualRate;
    }

    private BusinessState? CasualShiftFor(ResidentState person)
    {
        if(!Detailed || !_settings.EnableLocalProduction ||
            person.CasualHoursToday>=_settings.MaxCasualHoursPerDay)return null;
        return _businesses.Where(b=>CanTakeCasualShift(b,person))
            .Select(b=>(Business:b,Minutes:JourneyDuration(person,b.Id)))
            .Where(x=>x.Minutes<=130)
            .OrderBy(x=>x.Business.Kind==BusinessKinds.Haulier &&
                RecentCarrierDemand()?0:1)
            .ThenBy(x=>x.Minutes)
            .ThenBy(x=>StableRandom.Hash(person.Source.Id+x.Business.Id,
                _settings.Seed))
            .Select(x=>x.Business).FirstOrDefault();
    }

    private bool TryCasualShift(int index,ResidentState person)
    {
        var employer=CasualShiftFor(person);
        if(employer is null)return false;
        if(person.CurrentPlaceId!=employer.Id)Travel(index,person,employer.Id);
        else Enqueue(index,60,SettlementAction.CasualWork);
        return true;
    }

    private void CompleteCasualShift(ResidentState person)
    {
        if(!_byBusiness.TryGetValue(person.CurrentPlaceId,out var employer) ||
            person.CasualHoursToday>=_settings.MaxCasualHoursPerDay ||
            !CanTakeCasualShift(employer,person))return;
        int rate=CasualRate;
        // Preflight availability and money immediately before executing.
        // A resource already depleted by another worker pays no shift.
        employer.Gold-=rate;
        employer.WagesPaid+=rate;
        employer.WorkHours++;
        employer.CasualHoursWorked++;
        person.Gold+=rate;
        person.WageIncome+=rate;
        person.CasualWageIncome+=rate;
        person.CasualHoursToday++;
        _wages+=rate;
        _casualHours++;
        _casualWages+=rate;
        _casualWorkers.Add(person.Source.Id);
        CompleteCommodityWork(person,employer,1,rate);
        Observe(person,"casual_work",employer.Name+": "+rate+
            " gold for one resource-gathering hour");
    }
}