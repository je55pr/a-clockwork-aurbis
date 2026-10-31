namespace AClockworkAurbis.Simulation;

// Household food preparation and groceries are physical, conservative stock
// transfers. All recipes and prices are provisional simulation assumptions.
public sealed partial class SettlementWorld
{
    private int EverydayMealCost => Detailed ?
        CommodityRecipes.MealRetailPrices["bread"] : _settings.MealPrice;

    private static bool IsNamedChef(ResidentState person) =>
        string.Equals(person.Source.Profession,"Chef",StringComparison.OrdinalIgnoreCase);

    private bool HasNamedChef(BusinessState company) =>
        _staff.TryGetValue(company.Id,out var workers) &&
        workers.Any(IsNamedChef);

    private bool IsKitchenBusiness(BusinessState company) =>
        company.Kind is BusinessKinds.Bakery or BusinessKinds.FoodRetail ||
        company.Kind==BusinessKinds.GoodsRetail && HasNamedChef(company);

    private static bool RecipeAvailable(CommodityRecipe recipe,
        IReadOnlyDictionary<string,int> stock,int storageLimit,int outputMultiplier=1,
        int outputDivisor=1) =>
        recipe.Ingredients.All(x=>Quantity(stock,x.Key)>=x.Value) &&
        recipe.Products.All(x=>Quantity(stock,x.Key) +
            checked(x.Value*outputMultiplier/outputDivisor)<=storageLimit);

    // Bread's everyday market benchmark is one septim per full meal.
    // Nearby shops with healthy stock and affordable landed costs can
    // profitably meet it; sparse stock or expensive delivery raises the
    // local quote. We still use integral septims for individual loaves.
    private decimal BreadRetailQuote(BusinessState seller) =>
        MarketRetailQuote(seller,"bread");

    private decimal MealRetailQuote(BusinessState seller,string sku) =>
        MarketRetailQuote(seller,sku);

    private bool BreadBulkDiscount(BusinessState seller) =>
        (seller.Kind==BusinessKinds.Bakery || HasNamedChef(seller)) &&
        CommodityRecipes.CanDiscountSurplusBread(
            Quantity(seller.Stock,"bread"),BreadRetailQuote(seller)) &&
        BreadRetailQuote(seller)>=Money(2m*UnitCostMilli(seller,"bread")/1000m);

    private static bool IsProvisionsMarket(BusinessState shop) =>
        shop.Kind==BusinessKinds.FoodRetail &&
        (shop.Name.Contains("Market",StringComparison.OrdinalIgnoreCase) ||
         shop.Name.Contains("Mercantile",StringComparison.OrdinalIgnoreCase) ||
         shop.Name.Contains("Provisions",StringComparison.OrdinalIgnoreCase) ||
         shop.Name.Contains("Grocer",StringComparison.OrdinalIgnoreCase));

     private CommodityRecipe? ReadyHomeRecipe(ResidentState person)
    {
        if(person.HousingId is null)return null;
        return CommodityRecipes.HouseholdMeals
            .Where(r=>r.Ingredients.All(x=>Quantity(person.Stock,x.Key)>=x.Value))
            .OrderBy(r=>r.Products.Sum(p=>Quantity(person.Stock,p.Key)))
            .ThenBy(r=>StableRandom.Hash(r.Id+person.Source.Id,_settings.Seed))
            .FirstOrDefault();
    }

    private bool CanCookAtHome(ResidentState person) =>
        person.HousingId is not null && ReadyHomeRecipe(person) is not null;

    private bool TryGroceryKit(ResidentState person,BusinessState shop,
        out CommodityRecipe? recipe,out decimal cost)
    {
        recipe=null;
        cost=0;
        if(person.HousingId is null||!IsProvisionsMarket(shop))return false;
        var options=CommodityRecipes.HouseholdMeals.Select(r=>
        {
            decimal price=0;
            foreach(var item in r.Ingredients)
            {
                int needed=Math.Max(0,item.Value-Quantity(person.Stock,item.Key));
                if(needed>Quantity(shop.Stock,item.Key))return (Recipe:r,Price:decimal.MaxValue);
                price=checked(price+needed*GroceryRetailQuote(shop,item.Key));
            }
            return (Recipe:r,Price:price);
        }).Where(x=>x.Price>0&&x.Price<=person.Gold &&
            x.Price<=x.Recipe.Products.Values.Sum()*2)
          .OrderBy(x=>(long)x.Price*1000/x.Recipe.Products.Values.Sum())
          .ThenBy(x=>x.Price)
          .ThenBy(x=>StableRandom.Hash(x.Recipe.Id+person.Source.Id,_settings.Seed))
          .ToArray();
        if(options.Length==0)return false;
        recipe=options[0].Recipe;
        cost=options[0].Price;
        return true;
    }

    private BusinessState? ChooseGroceryShop(ResidentState person)
    {
        if(person.HousingId is null||CanCookAtHome(person) ||
           person.Gold<3 || person.LastFailedGrocerySearchDay==Minute/DayMinutes)
            return null;
        BusinessState? best=null;
        decimal score=decimal.MaxValue;
        foreach(var shop in _foodShops)
        {
            if(!TryGroceryKit(person,shop,out _,out decimal cost))continue;
            int minutes=JourneyDuration(person,shop.Id);
            // Never choose an impractically distant bulk-food trip instead
            // of a one-septim emergency bread purchase.
            if(minutes>90)continue;
            decimal candidate=minutes*100m+cost;
            if(candidate<score){score=candidate;best=shop;}
        }
        // If there are no affordable ingredients anywhere nearby today,
        // do not repeat an island-wide market scan on every idle decision.
        if(best is null)person.LastFailedGrocerySearchDay=Minute/DayMinutes;
        return best;
    }

    private void CompleteGroceryPurchase(ResidentState person)
    {
        if(!Detailed||person.HousingId is null||
           !_byBusiness.TryGetValue(person.CurrentPlaceId,out var seller) ||
           !TryGroceryKit(person,seller,out var recipe,out decimal cost) ||
           recipe is null)return;
        foreach(var item in recipe.Ingredients)
        {
            int amount=Math.Max(0,item.Value-Quantity(person.Stock,item.Key));
            if(amount==0)continue;
            decimal unitPrice=GroceryRetailQuote(seller,item.Key);
            long acquisition=WithdrawInventoryValue(seller,item.Key,amount);
            StockRemove(seller.Stock,item.Key,amount);
            StockAdd(person.Stock,item.Key,amount);
            MarketObserveSale(seller,item.Key,amount,
                checked(unitPrice*amount),acquisition,false);
            if(item.Key=="firewood")person.Goods=checked(person.Goods+amount);
        }
        person.Gold-=cost;
        decimal tax=Money(cost*0.05m);
        seller.Gold+=cost-tax;
        _publicGold+=tax;
        _taxRevenue+=tax;
        seller.RetailRevenue+=cost-tax;
        seller.RetailSales++;
        person.GroceryKitsPurchased++;
        _groceryKitsSold++;
        person.Purchases++;
        _purchases++;
        Observe(person,"groceries",recipe.Id+" ingredients at "+seller.Name+
            " for "+cost+" septims");
    }

    private void CompleteHomeCooking(ResidentState person)
    {
        if(!Detailed||person.HousingId is null||
           person.CurrentPlaceId!=person.HomeId)return;
        var recipe=ReadyHomeRecipe(person);
        if(recipe is null)return;
        foreach(var item in recipe.Ingredients)
        {
            UseIngredient(person.Stock,item.Key,item.Value);
            if(item.Key=="firewood")
                person.Goods=checked(person.Goods-item.Value);
        }
        int output=0;
        foreach(var product in recipe.Products)
        {
            Make(person.Stock,product.Key,product.Value);
            output=checked(output+product.Value);
        }
        person.Food=checked(person.Food+output);
        person.HomeCookingBatches++;
        person.HomeMealsCooked=checked(person.HomeMealsCooked+output);
        _homeCookingBatches++;
        _homeCookedMeals+=output;
        _recipeBatches++;
        Observe(person,"home_cook",recipe.Id+" produced "+output+
            " meals at "+DisplayPlace(person.HomeId));
    }

    private IReadOnlyList<CommodityRecipe> CookableProfessionalRecipes(ResidentState worker)
    {
        return IsNamedChef(worker)
            ? CommodityRecipes.All.Where(r=>r.Industry==BusinessKinds.Bakery).ToArray()
            : CommodityRecipes.All.Where(r=>r.Industry==BusinessKinds.Bakery &&
                r.Id!="chef_game_pie").ToArray();
    }

    private bool ProfessionalCookCanWork(ResidentState worker,BusinessState workplace)
    {
        int multiplier=IsNamedChef(worker)?3:2;
        int divisor=2;
        return CookableProfessionalRecipes(worker).Any(r=>
            RecipeAvailable(r,workplace.Stock,_settings.MaxCommodityStockPerSite,
                multiplier,divisor));
    }

    private void CompleteProfessionalCooking(ResidentState worker,
        BusinessState workplace,int hours)
    {
        int numerator=IsNamedChef(worker)?3:2;
        for(int n=0;n<hours;n++)
        {
            var available=CookableProfessionalRecipes(worker)
                .Where(r=>RecipeAvailable(r,workplace.Stock,
                    _settings.MaxCommodityStockPerSite,numerator,2))
                .OrderByDescending(r=>r.Id==workplace.PlannedProduct)
                .ThenByDescending(r=>ExpectedRecipeMargin(workplace,r,numerator,2))
                .ThenBy(r=>r.Id,StringComparer.Ordinal)
                .ToArray();
            if(available.Length==0)break;
            var recipe=available[0];
            CraftMarketRecipe(workplace,recipe,numerator,2,1);
            int output=recipe.Products.Sum(p=>checked(
                p.Value*numerator/2));
            _recipeBatches++;
            workplace.ItemsCrafted=checked(workplace.ItemsCrafted+output);
            _crafted+=output;
            if(IsNamedChef(worker))_chefCookedMeals+=output;
        }
    }
}
