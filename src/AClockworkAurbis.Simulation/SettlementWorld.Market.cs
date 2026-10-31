namespace AClockworkAurbis.Simulation;

// Monetary inventory valuation is non-cash accounting. Each physical SKU has
// a weighted average actual acquisition / production cost in milli-septims.
// Cash always settles to two decimal places, preserving gold conservation.
public sealed partial class SettlementWorld
{
    private const long Milli=1000;
    private long _marketTradeCount,_marketRetailCostsMilli;
    private long _marketWholesaleCostsMilli;
    private decimal _marketRetailRevenue,_marketWholesaleRevenue;
    private long _unfilledOrders;

    private static long UnitCostMilli(BusinessState firm,string sku)
    {
        int units=Quantity(firm.Stock,sku);
        return units==0 ? 0 : firm.InventoryValueMilli.GetValueOrDefault(sku)/units;
    }

    private static void CreditInventoryValue(BusinessState firm,string sku,long amount)
    {
        if(amount<0)throw new InvalidOperationException("Negative cost basis");
        firm.InventoryValueMilli[sku]=checked(
            firm.InventoryValueMilli.GetValueOrDefault(sku)+amount);
    }

    private static long WithdrawInventoryValue(BusinessState firm,string sku,int units)
    {
        int available=Quantity(firm.Stock,sku);
        if(units<0 || units>available)throw new InvalidOperationException(
            "Unfunded inventory cost withdrawal");
        long basis=firm.InventoryValueMilli.GetValueOrDefault(sku);
        long withdrawn=units==available?basis:
            checked(basis*units/available);
        long remains=basis-withdrawn;
        if(units==available)firm.InventoryValueMilli.Remove(sku);
        else firm.InventoryValueMilli[sku]=remains;
        return withdrawn;
    }

    private void OpenMarketAccounts()
    {
        foreach(var business in _businesses)
            foreach(var (sku,count) in business.Stock)
            {
                // A finite starting endowment is marked at its historical
                // assumed cost, but all subsequent production is valued from
                // actual paid labour and acquired ingredients.
                long value=sku=="bread"?500:
                    (long)CommodityRecipes.Prices[sku]*Milli;
                CreditInventoryValue(business,sku,checked(value*count));
            }
    }

    private void ExtractMarketStock(BusinessState firm,string sku,int units,
        int hours,int? paidWagePerHour=null)
    {
        Extract(firm.Stock,sku,units);
        // Extraction carries the labour hours that were actually paid.
        CreditInventoryValue(firm,sku,
            checked((long)hours*(paidWagePerHour??_settings.HourlyWage)*Milli));
    }

    private void UseBusinessSupply(BusinessState firm,string sku,int units)
    {
        WithdrawInventoryValue(firm,sku,units);
        UseFinished(firm.Stock,sku,units);
    }

    private void CraftMarketRecipe(BusinessState firm,CommodityRecipe recipe,
        int multiplierNumerator,int multiplierDenominator,int paidHours,
        int? paidWagePerHour=null)
    {
        long ingredients=0;
        foreach(var (sku,amount) in recipe.Ingredients)
        {
            ingredients=checked(ingredients+WithdrawInventoryValue(firm,sku,amount));
            UseIngredient(firm.Stock,sku,amount);
        }
        long totalCost=checked(ingredients+
            (long)paidHours*(paidWagePerHour??_settings.HourlyWage)*Milli);
        int outputUnits=recipe.Products.Sum(x=>checked(
            x.Value*multiplierNumerator/multiplierDenominator));
        long remaining=totalCost;
        int unitsRemaining=outputUnits;
        foreach(var (sku,batchUnits) in recipe.Products)
        {
            int units=checked(batchUnits*multiplierNumerator/multiplierDenominator);
            long share=units==unitsRemaining?remaining:
                checked(totalCost*units/outputUnits);
            Make(firm.Stock,sku,units);
            CreditInventoryValue(firm,sku,share);
            remaining-=share;
            unitsRemaining-=units;
        }
        if(remaining!=0)throw new InvalidOperationException(
            "Recipe production cost was not fully allocated");
    }

    private static decimal Money(decimal value) =>
        Math.Round(value,2,MidpointRounding.AwayFromZero);

    private static decimal ToSeptims(long milli) => Money(milli/1000m);

    private static long ToMilli(decimal septims) =>
        checked((long)(septims*Milli));

    // The current ask is a consequence of the producer's *own* costs,
    // availability, and sales pressure. The reference price is used only
    // to value opening inventory, never imposed on ongoing quotations.
    private long MarketAskMilli(BusinessState producer,string sku)
    {
        if(Quantity(producer.Stock,sku)==0)return 0;
        long basis=UnitCostMilli(producer,sku);
        if(basis==0)basis=100; // Fully depreciated by-products remain tradable.
        int units=Quantity(producer.Stock,sku);
        long markup=125;
        if(units<12)markup+=25;
        if(units>90)markup-=20;
        // Use smoothed recent demand, not unbounded lifetime sales, so a
        // long-lived seller does not acquire a permanent scarcity surcharge.
        decimal recent=producer.RecentDemand.GetValueOrDefault(sku);
        if(recent>0m && units<recent/2m)markup+=15;
        return Math.Max(100,checked(basis*markup/100));
    }

    private decimal MarketRetailQuote(BusinessState shop,string sku)
    {
        int stock=Quantity(shop.Stock,sku);
        if(stock==0)return 0m;
        decimal unit=UnitCostMilli(shop,sku)/1000m;
        // Minimum value for a depreciated by-product, not a compulsory
        // one-septim floor. All final quotes use exactly two decimals.
        if(unit==0)unit=0.05m;
        decimal calculated=unit*(sku=="bread"?1.5m:1.25m)+
            (sku=="bread"?0.20m:0.15m);
        if(stock<4)calculated*=1.45m;
        if(stock>90)calculated*=0.90m;
        if(CommodityRecipes.Edible(sku) && sku!="bread")
        {
            int nutrition=CommodityRecipes.SatiationPerMeal(sku);
            calculated+=(nutrition-56)*0.055m;
            if(sku=="chef_game_pie")calculated+=1.20m;
        }
        return Math.Clamp(Money(calculated),0.01m,10000m);
    }

    private decimal GroceryRetailQuote(BusinessState shop,string sku) =>
        MarketRetailQuote(shop,sku);

    private decimal HouseholdGoodsQuote(BusinessState shop,string sku) =>
        MarketRetailQuote(shop,sku);

     private void MarketObserveSale(BusinessState business,string sku,int units,
        decimal saleGold,long originalCostMilli,bool wholesale)
    {
        business.SoldBySku[sku]=checked(
            business.SoldBySku.GetValueOrDefault(sku)+units);
        if(wholesale)
        {
            _marketWholesaleRevenue+=saleGold;
            _marketWholesaleCostsMilli+=originalCostMilli;
            business.WholesaleCostOfGoodsMilli+=originalCostMilli;
        }
        else
        {
            _marketRetailRevenue+=saleGold;
            _marketRetailCostsMilli+=originalCostMilli;
            business.RetailCostOfGoodsMilli+=originalCostMilli;
        }
        _marketTradeCount++;
    }

    private void CheckMarketCostInvariants()
    {
        if(!Detailed)return;
        if(Money(_publicGold)!=_publicGold || Money(_outsideGold)!=_outsideGold)
            throw new InvalidOperationException("Public money contains fractions of a cent");
        foreach(var person in _residents)
            if(person.Gold<0 || Money(person.Gold)!=person.Gold)
                throw new InvalidOperationException(
                    "Resident cash is negative or not cent-precise: "+person.Source.Id);
        foreach(var firm in _businesses)
        {
            if(firm.Gold<0 || Money(firm.Gold)!=firm.Gold)
                throw new InvalidOperationException(
                    "Business cash is negative or not cent-precise: "+firm.Name);
            foreach(var (sku,value) in firm.InventoryValueMilli)
                if(value<0 || Quantity(firm.Stock,sku)==0)
                    throw new InvalidOperationException(
                        "Cost basis without inventory: "+firm.Name+" / "+sku);
            foreach(var (sku,units) in firm.Stock)
                if(units>0 && !firm.InventoryValueMilli.ContainsKey(sku))
                    throw new InvalidOperationException(
                        "Inventory without cost basis: "+firm.Name+" / "+sku);
        }
    }
}
