namespace AClockworkAurbis.Simulation;

public sealed partial class SettlementWorld
{
    // Persistent choice intent is important: otherwise travelling to a shop
    // causes the NPC to reroll and bounce endlessly between merchants.
    private BusinessState? ChooseRiskAwareMealShop(ResidentState resident)
    {
        if(resident.PlannedMealShopId is string id &&
           resident.PlannedMealSku is string sku &&
           _byBusiness.TryGetValue(id,out var planned) &&
           Quantity(planned.Stock,sku)>0 &&
           MealRetailQuote(planned,sku)<=resident.Gold)
            return planned;
        resident.PlannedMealShopId=null;
        resident.PlannedMealSku=null;

        var options=new List<(BusinessState Shop,string Sku,decimal Price,double Cost)>();
        foreach(var shop in _businesses.Where(IsKitchenBusiness)
                     .OrderBy(b=>b.Id,StringComparer.Ordinal))
        {
            int trip=JourneyDuration(resident,shop.Id);
            if(trip>130)continue;
            foreach(string food in CommodityRecipes.Meals)
            {
                if(Quantity(shop.Stock,food)==0)continue;
                decimal price=MealRetailQuote(shop,food);
                if(price<=0 || price>resident.Gold)continue;
                // Individual tastes are stable; they aren't a global random
                // draw that changes with event queue processing order.
                int preference=StableRandom.Between(
                    resident.Source.Id+"|food|"+food,_settings.Seed,32,17)-8;
                double score=trip+6d*(double)price+preference;
                options.Add((shop,food,price,score));
            }
        }
        if(options.Count==0)return null;
        decimal cheapest=options.Min(x=>x.Price);
        // Exploration never spends the emergency food reserve on a luxury.
        // The cheapest available meal is always allowed.
        var safe=options.Where(x=>x.Price<=cheapest ||
            resident.Gold-x.Price>=EverydayMealCost*5m).ToArray();
        var weighted=safe.Select(x=>new NpcChoiceOption<
            (BusinessState Shop,string Sku,decimal Price,double Cost)>(x,x.Cost))
            .ToArray();
        long sequence=resident.FoodChoiceCount++;
        var selected=NpcChoicePolicy.Choose(weighted,resident.RiskTolerance,
            resident.Source.Id,_settings.Seed,sequence);
        resident.PlannedMealShopId=selected.Shop.Id;
        resident.PlannedMealSku=selected.Sku;
        Observe(resident,"meal_choice",
            selected.Sku+" at "+selected.Shop.Name+" for "+selected.Price+
            " gold (risk "+resident.RiskTolerance+", "+safe.Length+" feasible options)");
        return selected.Shop;
    }
}
