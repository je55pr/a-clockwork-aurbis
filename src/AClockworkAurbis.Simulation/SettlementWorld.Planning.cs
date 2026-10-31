namespace AClockworkAurbis.Simulation;

// Demand signals are prior completed sales and published, unfilled wholesale
// orders, not privileged knowledge of residents' future purchases.
// A daily plan may be wrong: inputs, capital and actual demand are binding.
public sealed partial class SettlementWorld
{
    private readonly Dictionary<string,int> _unfilledMarketOrders = new(StringComparer.Ordinal);
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
        int unmet=_unfilledMarketOrders.GetValueOrDefault(sku)/sellers;
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
            // A producer cannot sell an entire batch at the quoted price
            // merely because the quoted price is high.
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
        return options.Where(r=>RecipeAvailable(r,firm.Stock,
                    _settings.MaxCommodityStockPerSite,numerator,denominator))
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
        int shortage=Math.Min(40,_unfilledMarketOrders.GetValueOrDefault(sku)/
            Math.Max(1,_businesses.Count(b=>CanSellCommodity(b,sku))));
        int perDay=Math.Max(recent,shortage);
        // Demand-sensitive safety stock: unfamiliar luxury dishes receive
        // small trial orders rather than a fictional twelve-unit demand.
        bool luxury=sku=="chef_game_pie";
        int target=luxury && recent==0 ? Math.Min(1,perDay) :
            Math.Max(baseline/4,Math.Min(72,checked(perDay*3+baseline/4)));
        // Never discard a potentially vital staple safety buffer.
        if(firm.Kind==BusinessKinds.FoodRetail && sku=="bread")
            target=Math.Max(16,target);
        return Math.Clamp(target,0,Math.Min(72,_settings.MaxCommodityStockPerSite));
    }

    private void PlanMarketDay()
    {
        int day=checked((int)(Minute/DayMinutes));
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
                business.RecentDemand[sku]=Math.Min(100m,
                    Math.Round((previous*2+Math.Min(100,delta))/3m,2));
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
                budget=Math.Min(affordable,Math.Max(1,DemandFor(business,
                    business.Kind==BusinessKinds.Fishery?"fish":
                    business.Kind==BusinessKinds.Hunter?"game_meat":
                    IsLoggingSite(business)?"logs":"iron_ore")/2+2));
            else
                budget=choice==null?0:Math.Min(affordable,
                    estimatedMargin<=0 ? 1 :
                    Math.Max(2,Math.Min(8,checked((int)Math.Ceiling(
                        business.RecentDemand.Values.DefaultIfEmpty(0).Max()/4m)+3))));
            if(business.Kind==BusinessKinds.FoodProducer)
                budget=Math.Min(affordable,Math.Max(2,
                    choice is null?0:DemandFor(business,choice)/2+2));
            if(!_settings.EnableLocalProduction && PlansProduction(business))
                budget=0;
            if(estimatedMargin<0 && choice!=null)_unprofitablePlans++;
            if(business.PlannedProduct!=choice)_plansRevised++;
            business.PlannedProduct=choice;
            business.PlannedMargin=estimatedMargin;
            business.PlannedHours=Math.Clamp(budget,0,maxHours);
            business.PlannedHoursWorked=0;
            business.LastPlannedDay=day;
            _plansMade++;
            string reason=choice==null?"no feasible production recipe or output space":
                "observed demand, quoted output, paid inputs and cash";
            _latestPlans.Add(new BusinessPlanSnapshot(business.Id,business.Name,
                day,choice,business.PlannedHours,estimatedMargin,reason));
        }
        // The following wholesale clearing round publishes new unfilled
        // orders for tomorrow, never future information for today's plans.
        _unfilledMarketOrders.Clear();
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
