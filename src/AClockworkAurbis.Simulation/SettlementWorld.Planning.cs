namespace AClockworkAurbis.Simulation;

// Demand signals are prior completed sales and published, unfilled wholesale
// orders, not privileged knowledge of residents' future purchases.
// A daily plan may be wrong: inputs, capital and actual demand are binding.
public sealed partial class SettlementWorld
{
    private readonly Dictionary<string,int> _unfilledMarketOrders = new(StringComparer.Ordinal);
    // Last day's published shortages remain visible during both production
    // planning and this day's wholesale purchasing. The working dictionary
    // receives only today's actual unfilled orders.
    private readonly Dictionary<string,int> _publishedUnfilledOrders =
        new(StringComparer.Ordinal);
    internal int PublishedUnfilledDemand(string sku) =>
        _publishedUnfilledOrders.GetValueOrDefault(sku);
    private long _plansMade,_plansRevised,_unprofitablePlans;
    private readonly List<BusinessPlanSnapshot> _latestPlans = new();

    private static bool PlansProduction(BusinessState business) =>
        business.Kind is BusinessKinds.FoodProducer or
            BusinessKinds.ResourceProducer or BusinessKinds.Hunter or
            BusinessKinds.Fishery or BusinessKinds.Bakery or
            BusinessKinds.Smeltery or BusinessKinds.Sawmill or
            BusinessKinds.Tannery or BusinessKinds.Workshop or
            BusinessKinds.Leatherworker;

    private int DemandFor(BusinessState business,string sku)
    {
        decimal sales=business.RecentDemand.GetValueOrDefault(sku);
        int sellers=Math.Max(1,_businesses.Count(b=>CanSellCommodity(b,sku)));
        int unmet=PublishedUnfilledDemand(sku)/sellers;
        return Math.Max(1,checked((int)Math.Ceiling(sales*2)+Math.Min(40,unmet)));
    }

    private decimal ExpectedWholesaleAsk(BusinessState firm,string sku)
    {
        if(Quantity(firm.Stock,sku)>0)return ToSeptims(MarketAskMilli(firm,sku));
        var peers=_businesses.Where(b=>b.Id!=firm.Id && CanSellCommodity(b,sku) &&
                Quantity(b.Stock,sku)>0)
            .Select(b=>ToSeptims(MarketAskMilli(b,sku))).ToArray();
        if(peers.Length>0)return peers.OrderBy(p=>p).ElementAt(peers.Length/2);
        // Only when neither the business nor a peer has observed stock:
        // opening reference prices seed estimates; they are not market asks.
        return CommodityRecipes.Prices[sku];
    }

    private decimal ExpectedRecipeMargin(BusinessState firm,CommodityRecipe recipe,
        int numerator=1,int denominator=1)
    {
        decimal sales=recipe.Products.Sum(product=>
        {
            int made=checked(product.Value*numerator/denominator);
            int stocked=Quantity(firm.Stock,product.Key);
            int interested=Math.Max(2,DemandFor(firm,product.Key)+
                Math.Max(0,24-stocked)/4);
            if(CommodityRecipes.Edible(product.Key) &&
               product.Key is not ("bread" or "foraged_greens" or "provisions"))
            {
                // Wholesale standing orders are NOT evidence that actual
                // customers will pay for elaborate food. Observe real
                // retail sales before committing an expensive whole batch.
                int retailDemand=_businesses.Where(b=>b.Kind==BusinessKinds.FoodRetail)
                    .Sum(b=>(int)Math.Ceiling(
                        b.RecentDemand.GetValueOrDefault(product.Key)));
                int cooks=Math.Max(1,_businesses.Count(b=>b.Kind==BusinessKinds.Bakery));
                interested=Math.Min(interested,Math.Max(1,retailDemand/cooks+1));
            }
            if(product.Key=="bread" && Minute/DayMinutes<=3)
            {
                int bakeries=Math.Max(1,_businesses.Count(b=>
                    b.Kind==BusinessKinds.Bakery));
                // Town population is visible before the first sales.
                // Essential food must not require previous starvation to
                // generate a plausible opening demand estimate.
                interested=Math.Max(interested,
                    (_residents.Count+bakeries-1)/bakeries);
            }
            // No sale is booked until a real customer actually pays.
            return Math.Min(made,interested)*ExpectedWholesaleAsk(firm,product.Key);
        });
        decimal inputs=recipe.Ingredients.Sum(input=>
        {
            decimal unit=Quantity(firm.Stock,input.Key)>0
                ?UnitCostMilli(firm,input.Key)/1000m
                :CommodityRecipes.Prices[input.Key];
            return input.Value*unit;
        });
        return Money(sales-inputs-_settings.HourlyWage);
    }

    private CommodityRecipe? PreferredRecipe(BusinessState firm,
        IEnumerable<CommodityRecipe> options,int numerator=1,int denominator=1)
    {
        // Plans can request missing ingredients for delivery later today.
        // Actual shifts still use RecipeAvailable, never planned inventory.
        return options.Where(r=>r.Products.All(x=>Quantity(firm.Stock,x.Key)+
                    checked(x.Value*numerator/denominator)<=
                    _settings.MaxCommodityStockPerSite))
            .OrderByDescending(r=>ExpectedRecipeMargin(firm,r,numerator,denominator))
            .ThenBy(r=>r.Id,StringComparer.Ordinal).FirstOrDefault();
    }

    private string? PreferredCrop(BusinessState farm)
    {
        return CropMix(farm)
            .Where(c=>Quantity(farm.Stock,c)<_settings.MaxCommodityStockPerSite)
            .OrderByDescending(c=>
                ExpectedWholesaleAsk(farm,c)*
                Math.Min(_settings.RenewableFoodPerWorkHour,
                    DemandFor(farm,c)+Math.Max(0,20-Quantity(farm.Stock,c))/3))
            .ThenBy(c=>Quantity(farm.Stock,c))
            .ThenBy(c=>c,StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private int PlannedTarget(BusinessState firm,string sku,int baseline)
    {
        int recent=checked((int)Math.Ceiling(firm.RecentDemand.GetValueOrDefault(sku)));
        int shortage=Math.Min(40,PublishedUnfilledDemand(sku)/
            Math.Max(1,_businesses.Count(b=>CanSellCommodity(b,sku))));
        int perDay=Math.Max(recent,shortage);
        // Demand-sensitive safety stock: unfamiliar luxury dishes receive
        // small trial orders rather than a fictional twelve-unit demand.
        bool luxury=sku=="chef_game_pie";
        int target=luxury && recent==0 ? 1 :
            Math.Max(1,Math.Max(baseline/4,
                Math.Min(72,checked(perDay*3+baseline/4))));
        // Never discard a potentially vital staple safety buffer.
        if(firm.Kind==BusinessKinds.FoodRetail && sku=="bread")
            target=Math.Max(16,target);
        return Math.Clamp(target,0,Math.Min(72,_settings.MaxCommodityStockPerSite));
    }

    private void PlanMarketDay()
    {
        int day=checked((int)(Minute/DayMinutes));
        _publishedUnfilledOrders.Clear();
        foreach(var (sku,quantity) in _unfilledMarketOrders)
            _publishedUnfilledOrders[sku]=quantity;
        _unfilledMarketOrders.Clear();
        _latestPlans.Clear();
        foreach(var business in _businesses)
        {
            // Completed sales only, measured since the previous planning day.
            foreach(var (sku,total) in business.SoldBySku)
            {
                long prior=business.LastPlannedSales.GetValueOrDefault(sku);
                long delta=Math.Max(0,total-prior);
                business.LastPlannedSales[sku]=total;
                decimal previous=business.RecentDemand.GetValueOrDefault(sku);
                decimal smoothed=(previous*2+Math.Min(100,delta))/3m;
                business.RecentDemand[sku]=smoothed<0.01m?0m:
                    Math.Min(100m,Math.Round(smoothed,3));
            }
            if(!PlansProduction(business) &&
               business.Kind is not (BusinessKinds.FoodRetail or BusinessKinds.GoodsRetail))
                continue;

            string? choice=null;
            decimal estimatedMargin=0;
            if(business.Kind==BusinessKinds.FoodProducer)
            {
                choice=PreferredCrop(business);
                if(choice!=null)
                    estimatedMargin=Money(ExpectedWholesaleAsk(business,choice)*
                        Math.Min(_settings.RenewableFoodPerWorkHour,
                            DemandFor(business,choice)+4)-_settings.HourlyWage);
            }
            else if(business.Kind is BusinessKinds.ResourceProducer or
                BusinessKinds.Hunter or BusinessKinds.Fishery)
            {
                choice=business.Kind==BusinessKinds.Fishery?"fish":
                    business.Kind==BusinessKinds.Hunter?"game_meat":
                    IsLoggingSite(business)?"logs":"iron_ore";
                estimatedMargin=Money(ExpectedWholesaleAsk(business,choice)*
                    (business.Kind==BusinessKinds.ResourceProducer?
                        _settings.RawMaterialPerWorkHour:2)-_settings.HourlyWage);
            }
            else if(PlansProduction(business))
            {
                // The rare chef recipe is only part of a feasible plan
                // when an actual source-labelled chef is employed here.
                var recipes=business.Kind==BusinessKinds.Bakery
                    ?CommodityRecipes.All.Where(r=>r.Industry==BusinessKinds.Bakery &&
                        (r.Id!="chef_game_pie" || HasNamedChef(business)))
                    :CommodityRecipes.For(business);
                var preferred=PreferredRecipe(business,recipes);
                choice=preferred?.Id;
                if(preferred!=null)
                    estimatedMargin=ExpectedRecipeMargin(business,preferred);
            }

            int maxHours=Math.Max(1,ContractCapacity(business)*8);
            // Business survival matters: reserve part of its cash for
            // tomorrow's ingredients and outstanding freight.
            int affordable=(int)Math.Min(maxHours,
                decimal.Floor(Math.Max(0,business.Gold-10)/
                    Math.Max(1,_settings.HourlyWage)));
            int budget;
            if(business.Kind is BusinessKinds.FoodRetail or BusinessKinds.GoodsRetail)
                budget=Math.Min(maxHours,business.ServiceHoursBudget);
            else if(business.Kind is BusinessKinds.ResourceProducer or
                BusinessKinds.Hunter or BusinessKinds.Fishery)
            {
                int normal=Math.Max(1,DemandFor(business,
                    business.Kind==BusinessKinds.Fishery?"fish":
                    business.Kind==BusinessKinds.Hunter?"game_meat":
                    IsLoggingSite(business)?"logs":"iron_ore")/2+2);
                if(Minute/DayMinutes<=3 &&
                    business.Kind==BusinessKinds.ResourceProducer &&
                    IsLoggingSite(business))
                {
                    int loggerCount=Math.Max(1,_businesses.Count(b=>
                        b.Kind==BusinessKinds.ResourceProducer && IsLoggingSite(b)));
                    int sawmillCount=_businesses.Count(b=>b.Kind==BusinessKinds.Sawmill);
                    int logsRequired=sawmillCount*8*2/loggerCount;
                    normal=Math.Max(normal,(logsRequired+
                        _settings.RawMaterialPerWorkHour-1)/
                        _settings.RawMaterialPerWorkHour);
                }
                budget=Math.Min(affordable,normal);
            }
            else
                budget=choice==null?0:Math.Min(affordable,
                    estimatedMargin<=0 ? 1 :
                    Math.Max(2,Math.Min(8,checked((int)Math.Ceiling(
                        business.RecentDemand.Values.DefaultIfEmpty(0).Max()/4m)+3))));
            if(Minute/DayMinutes<=3 &&
               business.Kind==BusinessKinds.Sawmill && choice is not null &&
               estimatedMargin>0)
            {
                int sawmills=Math.Max(1,_businesses.Count(b=>b.Kind==BusinessKinds.Sawmill));
                int ovens=_businesses.Count(b=>b.Kind==BusinessKinds.Bakery);
                budget=Math.Min(affordable,Math.Max(budget,
                    (ovens*8+3*sawmills-1)/(3*sawmills)));
            }
            if(Minute/DayMinutes<=3 &&
               business.Kind==BusinessKinds.Bakery && choice is not null &&
               estimatedMargin>0)
            {
                int bakers=Math.Max(1,_businesses.Count(b=>b.Kind==BusinessKinds.Bakery));
                int expectedMeals=(_residents.Count*2+bakers-1)/bakers;
                int outputPerHour=CommodityRecipes.All
                    .Where(r=>r.Id==choice).Select(r=>r.Products.Values.Sum())
                    .FirstOrDefault();
                if(outputPerHour>0)
                    budget=Math.Min(affordable,Math.Max(budget,
                        (expectedMeals+outputPerHour-1)/outputPerHour));
            }
            if(business.Kind==BusinessKinds.FoodProducer &&
               Minute/DayMinutes<=3)
            {
                // A brand-new market has little recorded sales, not little
                // need for food. Farms can observe their settlement's
                // population and plan a starter supply before sales history
                // exists. Future shortages and sales still raise the plan.
                int peoplePerFarm=(_residents.Count+Math.Max(1,_farms.Length)-1)/
                    Math.Max(1,_farms.Length);
                int hoursForFood=(peoplePerFarm+
                    _settings.RenewableFoodPerWorkHour-1)/
                    _settings.RenewableFoodPerWorkHour;
                budget=choice==null?0:Math.Min(affordable,
                    Math.Max(hoursForFood,DemandFor(business,choice)/2+2));
            }
            if(business.Kind==BusinessKinds.Workshop &&
               HasConstructionOrders(business))
                budget=Math.Max(budget,Math.Min(maxHours,
                    _construction.Values.Where(p=>p.Builder.Id==business.Id)
                        .Sum(p=>p.RequiredHours-p.CompletedHours)));
            if(!_settings.EnableLocalProduction && PlansProduction(business))
                budget=0;
            // Contracted farm, mine, forest, hunt and fishery workers have
            // priority scheduled hours whenever physical resources and paid
            // payroll exist. Casual labour only uses remaining capacity.
            if(OpenResourceJob(business) && _settings.EnableLocalProduction)
            {
                // A permanent contract funds essential site tending even
                // when the harvest is temporarily full or ore is exhausted.
                int regulars=_staff[business.Id].Count(p=>p.WorkId==business.Id);
                budget=Math.Max(budget,Math.Min(affordable,
                    regulars*_settings.RegularGuaranteedHoursPerDay));
            }
            if(estimatedMargin<0 && choice!=null)_unprofitablePlans++;
            if(business.PlannedProduct!=choice)_plansRevised++;
            business.PlannedProduct=choice;
            business.PlannedMargin=estimatedMargin;
            business.PlannedHours=Math.Clamp(budget,0,maxHours);
            business.PlannedHoursWorked=0;
            business.LastPlannedDay=day;
            _plansMade++;
            string reason=business.Kind is BusinessKinds.FoodRetail or
                BusinessKinds.GoodsRetail ? "staffing from realised retail margin" :
                choice==null?"no feasible production recipe or output space":
                "observed demand, quoted output, paid inputs and cash";
            _latestPlans.Add(new BusinessPlanSnapshot(business.Id,business.Name,
                day,choice,business.PlannedHours,estimatedMargin,reason));
        }
        // This day's wholesale clearing fills _unfilledMarketOrders,
        // which is published at the start of tomorrow's planning.
    }

    private int MarketTarget(BusinessState firm,string sku,int baseline)
    {
        if(firm.Kind is BusinessKinds.FoodRetail or BusinessKinds.GoodsRetail)
            return PlannedTarget(firm,sku,baseline);

        var candidates=firm.Kind==BusinessKinds.Bakery
            ?CommodityRecipes.All.Where(r=>r.Industry==BusinessKinds.Bakery)
            :CommodityRecipes.For(firm);
        var active=candidates.Where(r=>r.Id==firm.PlannedProduct).ToArray();
        if(active.Length==0)
            return baseline;
        int perBatch=active.Sum(r=>r.Ingredients.GetValueOrDefault(sku));
        return perBatch==0?0:Math.Clamp(perBatch*
            Math.Max(2,Math.Min(8,firm.PlannedHours)),perBatch,
            Math.Min(48,_settings.MaxCommodityStockPerSite));
    }

    private IReadOnlyList<BusinessPlanSnapshot> BusinessPlans() =>
        _latestPlans.OrderBy(b=>b.BusinessId,StringComparer.Ordinal).ToArray();
}
