namespace AClockworkAurbis.Simulation;

public sealed partial class SettlementWorld
{
    private long _tavernVisits,_clothingWornOut,_clothingPurchased;
    private decimal _tavernGoldSpent;

    // A modest optional evening activity, not an occupational requirement.
    // Risk tolerance increases willingness to try it, but no resident can
    // spend the food and housing reserve just to buy a drink.
    private bool TryVisitTavern(int index,ResidentState visitor)
    {
        long day=Minute/DayMinutes;
        if(visitor.LastTavernDay>=day || visitor.Satiation<55 ||
           visitor.Energy<32 || visitor.Food==0)return false;
        int odds=5+visitor.RiskTolerance/3;
        ulong roll=StableRandom.Hash(visitor.Source.Id+"|tavern|"+day,
            unchecked(_settings.Seed+811));
        if(roll%100UL>=(ulong)odds)return false;
        decimal reserve=Math.Max(10m,EverydayMealCost*3m+
            (visitor.HousingId is not null?_settings.DailySharedRent*2:0));
        var pub=_businesses.Where(b=>b.Kind==BusinessKinds.FoodRetail &&
                Quantity(b.Stock,"ale")>0)
            .Select(b=>(Shop:b,Distance:JourneyDuration(visitor,b.Id)))
            .Where(x=>x.Distance<=90 &&
                visitor.Gold>=MarketRetailQuote(x.Shop,"ale")+reserve)
            .OrderBy(x=>x.Distance)
            .ThenBy(x=>MarketRetailQuote(x.Shop,"ale"))
            .ThenBy(x=>x.Shop.Id,StringComparer.Ordinal)
            .FirstOrDefault();
        if(pub.Shop is null)return false;
        if(visitor.CurrentPlaceId!=pub.Shop.Id)
            Travel(index,visitor,pub.Shop.Id);
        else
            Enqueue(index,25,SettlementAction.VisitTavern);
        return true;
    }

    private void CompleteTavernVisit(ResidentState visitor)
    {
        visitor.LastTavernDay=Minute/DayMinutes;
        if(!_byBusiness.TryGetValue(visitor.CurrentPlaceId,out var pub) ||
           pub.Kind!=BusinessKinds.FoodRetail ||
           Quantity(pub.Stock,"ale")<1)return;
        decimal price=MarketRetailQuote(pub,"ale");
        decimal reserve=Math.Max(10m,EverydayMealCost*3m+
            (visitor.HousingId is not null?_settings.DailySharedRent*2:0));
        if(price<=0 || visitor.Gold<price+reserve)return;

        long cost=WithdrawInventoryValue(pub,"ale",1);
        UseFinished(pub.Stock,"ale",1);
        _goodsUsed++;
        MarketObserveSale(pub,"ale",1,price,cost,false);
        decimal tax=Math.Min(Money(price*_settings.DetailedGoodsTaxRate),
            Math.Max(0m,price-0.01m));
        visitor.Gold-=price;
        pub.Gold+=price-tax;
        _publicGold+=tax;
        _taxRevenue+=tax;
        _retailSalesTaxRevenue+=tax;
        pub.RetailRevenue+=price-tax;
        pub.RetailSales++;
        visitor.Purchases++;
        visitor.TavernVisits++;
        _purchases++;
        _tavernVisits++;
        _tavernGoldSpent+=price;
        Observe(visitor,"tavern_visit",pub.Name+": ale for "+price+" gold");
    }
}
