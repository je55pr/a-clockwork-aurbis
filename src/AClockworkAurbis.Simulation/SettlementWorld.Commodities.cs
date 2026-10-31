namespace AClockworkAurbis.Simulation;

/// <summary>
/// A deliberately small but physical multi-commodity economy. This mode is
/// independent of Godot and ESO assets; production sites, crop mixes, recipes,
/// yields and prices are explicitly simulated assumptions, not game facts.
/// </summary>
public sealed partial class SettlementWorld
{
    private sealed class CommodityAccount
    {
        public long Initial, Extracted, Crafted, Ingredients, Consumed;
    }

    private readonly Dictionary<string, CommodityAccount> _commodityAccounts =
        new(StringComparer.Ordinal);
    private long _recipeBatches, _toolsWorn, _fuelBurned;
    private long _carriageEquipmentDelays, _longFreightRuns;
    private decimal _scarcityPremiumGold,_surplusDiscountGold;
    private long _idleProductionHoursAvoided;
    private long _homeCookingBatches, _homeCookedMeals, _chefCookedMeals;
    private long _oneSeptimBreadSold, _groceryKitsSold;
    private long _breadSalesUnits,_discountedBreadUnits;
    private decimal _breadSalesRevenue;

    private CommodityAccount Account(string id) =>
        _commodityAccounts.TryGetValue(id,out var a) ? a :
            _commodityAccounts[id] = new CommodityAccount();

    private static int Quantity(IReadOnlyDictionary<string,int> stock,string sku) =>
        stock.TryGetValue(sku,out int amount) ? amount : 0;

    private static void StockAdd(Dictionary<string,int> stock,string sku,int units)
    {
        if(units<0)throw new ArgumentOutOfRangeException(nameof(units));
        stock[sku] = checked(Quantity(stock,sku)+units);
    }

    private static void StockRemove(Dictionary<string,int> stock,string sku,int units)
    {
        if(units<0 || Quantity(stock,sku)<units)
            throw new InvalidOperationException("Unfunded commodity withdrawal: "+sku);
        int left = stock[sku]-units;
        if(left==0)stock.Remove(sku);
        else stock[sku]=left;
    }

    private void Extract(Dictionary<string,int> stock,string sku,int units)
    {
        if(units<=0)return;
        StockAdd(stock,sku,units);
        Account(sku).Extracted += units;
    }

    private void Make(Dictionary<string,int> stock,string sku,int units)
    {
        StockAdd(stock,sku,units);
        Account(sku).Crafted += units;
    }

    private void UseIngredient(Dictionary<string,int> stock,string sku,int units)
    {
        StockRemove(stock,sku,units);
        Account(sku).Ingredients += units;
    }

    private void UseFinished(Dictionary<string,int> stock,string sku,int units)
    {
        StockRemove(stock,sku,units);
        Account(sku).Consumed += units;
        if(sku=="firewood")_fuelBurned += units;
        if(sku is "pickaxe" or "felling_axe")_toolsWorn += units;
    }

    private void InitializeCommodityEconomy()
    {
        // Opening inventories and personal supplies are declared initial
        // endowments, not resources secretly created during the simulation.
        foreach(var resident in _residents)
        {
            if(resident.Food>0)StockAdd(resident.Stock,"provisions",resident.Food);
        }
        foreach(var company in _businesses)
        {
            company.Food=company.Materials=company.Goods=0; // legacy category stocks unused
            switch(company.Kind)
            {
                case BusinessKinds.FoodRetail:
                    StockAdd(company.Stock,"bread",6);
                    break;
                case BusinessKinds.ResourceProducer:
                    if(IsLoggingSite(company))
                        StockAdd(company.Stock,"felling_axe",3);
                    else
                        StockAdd(company.Stock,"pickaxe",3);
                    break;
                case BusinessKinds.Sawmill:
                    StockAdd(company.Stock,"logs",4);
                    break;
                case BusinessKinds.Smeltery:
                    StockAdd(company.Stock,"firewood",5);
                    break;
                case BusinessKinds.Bakery:
                    StockAdd(company.Stock,"wheat",5);
                    StockAdd(company.Stock,"potato",3);
                    StockAdd(company.Stock,"onion",3);
                    StockAdd(company.Stock,"firewood",6);
                    break;
                case BusinessKinds.Workshop:
                    StockAdd(company.Stock,"lumber",3);
                    if(!CommodityRecipes.IsCarpenter(company))
                        StockAdd(company.Stock,"iron_ingot",4);
                    break;
                case BusinessKinds.ImportHub:
                    StockAdd(company.Stock,"horseshoes",2);
                    break;
            }
        }
        OpenMarketAccounts();
        foreach(var sku in CommodityRecipes.Prices.Keys)
            Account(sku).Initial = CommodityStockOnHand(sku);
    }

    private static bool IsLoggingSite(BusinessState company) =>
        company.Definition.ProductionProfile=="logs" ||
        string.Equals(company.Place.SourceType,"Grove",StringComparison.OrdinalIgnoreCase);

    private static string[] CropMix(BusinessState company)
    {
        string[] crops = (company.Definition.ProductionProfile??"wheat,potato")
            .Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        return crops.Length==0?["wheat","potato"]:crops;
    }

    private bool CanScheduleCommodityWork(ResidentState worker,BusinessState company)
    {
        if(PlansProduction(company) &&
           company.PlannedHoursWorked>=company.PlannedHours)return false;
        // Share the minimum contracted shifts among real employees before
        // permitting overtime. Early starters must not consume the entire
        // day's regular payroll ahead of colleagues starting later.
        if(OpenResourceJob(company) &&
           worker.RegularHoursToday>=_settings.RegularGuaranteedHoursPerDay &&
           _staff[company.Id].Any(other=>!ReferenceEquals(other,worker) &&
               other.WorkId==company.Id &&
               other.RegularHoursToday<_settings.RegularGuaranteedHoursPerDay &&
               Minute%DayMinutes<(other.WorkStart+8)*60))return false;
        if(!_settings.EnableLocalProduction &&
           company.Kind is not (BusinessKinds.FoodRetail or
               BusinessKinds.GoodsRetail or BusinessKinds.Institution or
               BusinessKinds.GuardPost or BusinessKinds.CleaningService or
               BusinessKinds.DomesticService))
            return false;
        if(OpenResourceJob(company) &&
           worker.RegularHoursToday<_settings.RegularGuaranteedHoursPerDay &&
           company.Gold>=_settings.HourlyWage)
            return true; // Contracted, paid site maintenance when no harvest exists.
        switch(company.Kind)
        {
            case BusinessKinds.FoodProducer:
                return company.LandYieldRemaining>0 &&
                    CropMix(company).Any(c=>Quantity(company.Stock,c)<
                        _settings.MaxCommodityStockPerSite);
            case BusinessKinds.ResourceProducer:
                return company.MineralReserve>0 &&
                    Quantity(company.Stock,IsLoggingSite(company)?"felling_axe":"pickaxe")>0 &&
                    Quantity(company.Stock,IsLoggingSite(company)?"logs":"iron_ore")<
                        _settings.MaxCommodityStockPerSite;
            case BusinessKinds.Hunter:
                return company.LandYieldRemaining>0 &&
                    Quantity(company.Stock,"raw_hide")<_settings.MaxCommodityStockPerSite &&
                    Quantity(company.Stock,"game_meat")<_settings.MaxCommodityStockPerSite;
            case BusinessKinds.Fishery:
                return company.LandYieldRemaining>0 &&
                    Quantity(company.Stock,"fish")<_settings.MaxCommodityStockPerSite;
            case BusinessKinds.Bakery:
                return ProfessionalCookCanWork(worker,company);
            case BusinessKinds.Smeltery or BusinessKinds.Sawmill or BusinessKinds.Tannery
                or BusinessKinds.Leatherworker or BusinessKinds.Workshop:
                return HasConstructionOrders(company) ||
                    CommodityRecipes.For(company).Any(r=>
                    r.Ingredients.All(x=>Quantity(company.Stock,x.Key)>=x.Value) &&
                    r.Products.All(x=>Quantity(company.Stock,x.Key)+x.Value<=
                        _settings.MaxCommodityStockPerSite));
            case BusinessKinds.ImportHub:
                return false; // Domestic common-carrier freight is not dock work.
            case BusinessKinds.FoodRetail or BusinessKinds.GoodsRetail:
                return company.ServiceHoursWorkedToday < company.ServiceHoursBudget ||
                    IsNamedChef(worker) &&
                    company.ServiceHoursWorkedToday<
                        Math.Max(1,company.ServiceHoursBudget) &&
                    ProfessionalCookCanWork(worker,company);
            default:
                return true; // Other public services remain funded separately.
        }
    }

    private bool CompleteCommodityWork(ResidentState actor,BusinessState company,int hours)
    {
        if(!_settings.EnableLocalProduction || hours<=0)return true;
        if(OpenResourceJob(company) && !CanCasualProduce(company))
        {
            // Full-time contracted workers still tend fields, maintain
            // sites and equipment for their guaranteed paid hours.
            // No output is fabricated when natural inputs are missing.
            Observe(actor,"site_upkeep",hours+" funded upkeep hours at "+company.Name);
            return true;
        }
        switch(company.Kind)
        {
            case BusinessKinds.FoodProducer:
            {
                var crops=CropMix(company);
                for(int n=0;n<hours && company.LandYieldRemaining>0;n++)
                {
                    string? crop=company.PlannedProduct is string preferred &&
                        crops.Contains(preferred,StringComparer.Ordinal) &&
                        Quantity(company.Stock,preferred)<
                            _settings.MaxCommodityStockPerSite
                        ?preferred:PreferredCrop(company);
                    if(crop is null)break;
                    int capacity=_settings.MaxCommodityStockPerSite-
                        Quantity(company.Stock,crop);
                    int picked=Math.Min(capacity,Math.Min(company.LandYieldRemaining,
                        _settings.RenewableFoodPerWorkHour+company.CapitalLevel-1));
                    if(picked==0)break;
                    company.LandYieldRemaining-=picked;
                    ExtractMarketStock(company,crop,picked,1);
                    company.FoodProduced+=picked;
                    _localFood+=picked;
                }
                break;
            }
            case BusinessKinds.ResourceProducer:
            {
                string tool=IsLoggingSite(company)?"felling_axe":"pickaxe";
                string output=IsLoggingSite(company)?"logs":"iron_ore";
                for(int n=0;n<hours && company.MineralReserve>0;n++)
                {
                    if(Quantity(company.Stock,tool)<=0)break;
                    int capacity=Math.Max(0,_settings.MaxCommodityStockPerSite-
                        Quantity(company.Stock,output));
                    int picked=Math.Min(capacity,Math.Min(company.MineralReserve,
                        _settings.RawMaterialPerWorkHour+company.CapitalLevel-1));
                    if(picked==0)break;
                    company.MineralReserve-=picked;
                    ExtractMarketStock(company,output,picked,1);
                    company.MaterialsExtracted+=picked;
                    _localMaterials+=picked;
                    company.ToolWearHours++;
                    if(company.ToolWearHours>=_settings.ToolWorkHoursPerWear)
                    {
                        company.ToolWearHours=0;
                        UseBusinessSupply(company,tool,1);
                    }
                }
                break;
            }
            case BusinessKinds.Hunter:
            case BusinessKinds.Fishery:
            {
                for(int n=0;n<hours && company.LandYieldRemaining>0;n++)
                {
                    int capacity=company.Kind==BusinessKinds.Fishery
                        ? _settings.MaxCommodityStockPerSite-Quantity(company.Stock,"fish")
                        : Math.Min(_settings.MaxCommodityStockPerSite-
                            Quantity(company.Stock,"game_meat"),
                            _settings.MaxCommodityStockPerSite-
                            Quantity(company.Stock,"raw_hide"));
                    int units=Math.Min(capacity,Math.Min(
                        2+company.CapitalLevel-1,company.LandYieldRemaining));
                    if(units<=0)break;
                    company.LandYieldRemaining-=units;
                    if(company.Kind==BusinessKinds.Fishery)
                        ExtractMarketStock(company,"fish",units,1);
                    else
                    {
                        ExtractMarketStock(company,"game_meat",units,1);
                        ExtractMarketStock(company,"raw_hide",units,0);
                    }
                    _localFood+=units;
                }
                break;
            }
            case BusinessKinds.Bakery:
                CompleteProfessionalCooking(actor,company,hours);
                break;
            case BusinessKinds.FoodRetail or BusinessKinds.GoodsRetail
                when IsNamedChef(actor):
                CompleteProfessionalCooking(actor,company,hours);
                break;
            case BusinessKinds.Smeltery:
            case BusinessKinds.Sawmill:
            case BusinessKinds.Tannery:
            case BusinessKinds.Leatherworker:
            case BusinessKinds.Workshop:
            {
                int productive=hours;
                if(company.Kind==BusinessKinds.Workshop)
                    for(int hour=0;hour<hours;hour++)
                        if(CompleteConstructionHour(company))productive--;
                bool CraftBatch(int paidHours)
                {
                    var recipe=CommodityRecipes.For(company)
                        .Where(r=>RecipeAvailable(r,company.Stock,
                            _settings.MaxCommodityStockPerSite))
                        .OrderByDescending(r=>r.Id==company.PlannedProduct)
                        .ThenByDescending(r=>ExpectedRecipeMargin(company,r))
                        .ThenBy(r=>r.Id,StringComparer.Ordinal)
                        .FirstOrDefault();
                    if(recipe is null)return false;
                    CraftMarketRecipe(company,recipe,1,1,paidHours);
                    _recipeBatches++;
                    company.ItemsCrafted+=recipe.Products.Values.Sum();
                    _crafted+=recipe.Products.Values.Sum();
                    return true;
                }
                int actuallyProduced=0;
                for(int n=0;n<productive;n++)
                    if(CraftBatch(1))actuallyProduced++;
                    else break;
                // Extra machine cycles are earned only by productive hours.
                int extra=CapitalProductionHours(company,actuallyProduced)-
                    actuallyProduced;
                for(int n=0;n<extra && CraftBatch(0);n++) { }
                break;
            }
            case BusinessKinds.ImportHub:
            {
                int available=Math.Max(0,_settings.MaxPortCargoCapacity-company.CargoCapacity);
                int handled=(int)Math.Min(available,
                    (long)hours*_settings.PortCargoUnitsPerWorkHour);
                company.CargoCapacity+=handled;
                break;
            }
        }
        return true;
    }

    private void RunCommodityDay()
    {
        int day=checked((int)(Minute/DayMinutes));
        RegrowWildFood();
        foreach(var person in _residents)
        {
            person.CasualHoursToday=0;
            person.RegularHoursToday=0;
        }
        // Staff retailers for demand observed the preceding day. With no
        // customers, retain one management hour instead of paying for
        // multiple empty eight-hour shifts.
        foreach(var store in _businesses.Where(b=>b.Kind is
            BusinessKinds.FoodRetail or BusinessKinds.GoodsRetail))
        {
            decimal revenue=Math.Max(0,store.RetailRevenue-store.LastRetailRevenueAtDay);
            long costBasis=Math.Max(0,store.RetailCostOfGoodsMilli-
                store.LastRetailCostOfGoodsMilliAtDay);
            store.LastRetailSalesAtDay=store.RetailSales;
            store.LastRetailRevenueAtDay=store.RetailRevenue;
            store.LastRetailCostOfGoodsMilliAtDay=store.RetailCostOfGoodsMilli;
            store.LastBreadSalesAtDay=store.BreadSalesUnits;
            // Yesterday's actually earned net revenue, less the real
            // weighted-average inventory cost of goods sold.
            decimal grossMargin=Math.Max(0,revenue-costBasis/1000m);
            int affordableHours=checked((int)Math.Min(int.MaxValue,
                grossMargin/Math.Max(1,_settings.HourlyWage)));
            // A shop with no proven margin can still trade, but cannot
            // promise salaried hours it cannot pay for. A source-backed
            // operator may work as its proprietor, not a fictional paid shift.
            store.ServiceHoursBudget=Math.Clamp(affordableHours,0,
                Math.Max(1,ContractCapacity(store)*8));
            store.ServiceHoursWorkedToday=0;
        }
        if(_settings.EnableLocalProduction)
        {
            int perFarm=_farms.Length==0?0:
                Math.Max(10,checked(_residents.Count*
                    _settings.DailyLandRegrowthPerResident/_farms.Length));
            foreach(var farm in _farms)
            {
                farm.LandYieldRemaining=Math.Min(checked(perFarm*2),
                    farm.LandYieldRemaining+perFarm);
                // Gleaning is a separate, small communal opportunity.
                // It never grants unlimited access to commercial crops.
                farm.ForageRemaining=Math.Min(farm.LandYieldRemaining,
                    _settings.PublicForagePerFarmPerDay);
            }
            foreach(var producer in _extractors.Where(IsLoggingSite))
                producer.MineralReserve=Math.Min(_settings.InitialMineralReservePerSite,
                    producer.MineralReserve+32);
            foreach(var gatherer in _businesses.Where(b=>b.Kind is
                BusinessKinds.Hunter or BusinessKinds.Fishery))
                gatherer.LandYieldRemaining=Math.Min(40,gatherer.LandYieldRemaining+24);
        }

        if(day>0 && day%_settings.FirewoodDaysPerHousehold==0)
            foreach(var resident in _residents)
            {
                if(Quantity(resident.Stock,"firewood")==0)continue;
                UseFinished(resident.Stock,"firewood",1);
                resident.Goods=Math.Max(0,resident.Goods-1);
                _goodsUsed++;
            }

        if(day>0 && day%20==0)
            foreach(var resident in _residents)
            {
                string? worn=new[]{"iron_tools","leather_goods"}
                    .FirstOrDefault(s=>Quantity(resident.Stock,s)>0);
                if(worn is null)continue;
                UseFinished(resident.Stock,worn,1);
                resident.Goods=Math.Max(0,resident.Goods-1);
                _goodsUsed++;
            }

        PlanMarketDay();
        PlanCapitalDay(day);
        CommodityWholesaleDay();
    }

    private IEnumerable<(string Sku,int Target)> CommodityOrders(BusinessState company)
    {
        static IEnumerable<(string,int)> Pair(params string[] sku) =>
            sku.Select(s=>(s,18));
        switch(company.Kind)
        {
            case BusinessKinds.ResourceProducer:
                return Pair(IsLoggingSite(company)?"felling_axe":"pickaxe")
                    .Select(x=>(x.Item1,3));
            case BusinessKinds.Smeltery:return Pair("iron_ore","firewood");
            case BusinessKinds.Sawmill:return Pair("logs");
            case BusinessKinds.Tannery:return Pair("raw_hide","firewood");
            case BusinessKinds.Bakery:
                return Pair("wheat","potato","onion","lettuce","fish",
                    "game_meat","firewood");
            case BusinessKinds.Workshop:
                return CommodityRecipes.IsCarpenter(company)?
                    Pair("lumber"):Pair("iron_ingot","lumber");
            case BusinessKinds.Leatherworker:
                return Pair("leather","iron_ingot");
            case BusinessKinds.FoodRetail:
                var meals=Pair("bread","vegetable_stew","garden_salad",
                    "fish_meal","roast_meat","chef_game_pie")
                    .Select(x=>(x.Item1,x.Item1=="bread"?24:12));
                return IsProvisionsMarket(company)||HasNamedChef(company)
                    ? meals.Concat(new (string,int)[]{("wheat",8),("potato",8),
                        ("onion",8),("lettuce",8),("firewood",6),
                        ("fish",4),("game_meat",4)})
                    : meals;
            case BusinessKinds.GoodsRetail:
                var goods=new (string,int)[]{("firewood",9),("iron_tools",5),
                    ("leather_goods",5),("furnishings",2)};
                return HasNamedChef(company)
                    ? goods.Concat(new (string,int)[]{("wheat",8),("potato",8),
                        ("onion",8),("lettuce",8),("firewood",8),
                        ("fish",4),("game_meat",4)})
                    : goods;
            case BusinessKinds.ImportHub:
                return Pair("horseshoes");
            default:return [];
        }
    }

    private static bool CanSellCommodity(BusinessState seller,string sku) =>
        seller.Kind switch
        {
            BusinessKinds.FoodProducer => CropMix(seller).Contains(sku,StringComparer.Ordinal),
            BusinessKinds.ResourceProducer =>
                sku==(IsLoggingSite(seller)?"logs":"iron_ore"),
            BusinessKinds.Hunter => sku is "game_meat" or "raw_hide",
            BusinessKinds.Fishery => sku=="fish",
            BusinessKinds.Sawmill => sku is "lumber" or "firewood",
            BusinessKinds.Smeltery => sku=="iron_ingot",
            BusinessKinds.Tannery => sku=="leather",
            BusinessKinds.Bakery => sku is "bread" or "vegetable_stew" or
                "garden_salad" or "fish_meal" or "roast_meat" or
                "chef_game_pie",
            BusinessKinds.Workshop or BusinessKinds.Leatherworker =>
                CommodityRecipes.For(seller).Any(r=>r.Products.ContainsKey(sku)),
            _ => false // Retail stock, input stores, tools and carriers aren't a magic wholesaler.
        };

    private long QuoteCommodityWholesalePriceMilli(BusinessState supplier,
        string sku) => MarketAskMilli(supplier,sku);

    private int CommodityOrderPriority(BusinessState buyer,string sku)
    {
        // A town's ordinary bread counter must not spend its entire budget
        // on optional groceries before buying its basic staple.
        if(buyer.Kind==BusinessKinds.FoodRetail && sku=="bread")return 1;
        if(CommodityRecipes.For(buyer).Any(recipe=>
            recipe.Ingredients.ContainsKey(sku) &&
            recipe.Ingredients.Where(x=>x.Key!=sku).All(x=>
                Quantity(buyer.Stock,x.Key)>=x.Value) &&
            recipe.Products.All(x=>Quantity(buyer.Stock,x.Key)+x.Value<=
                _settings.MaxCommodityStockPerSite)))
            return Quantity(buyer.Stock,sku)==0?0:1;
        return buyer.Kind switch
        {
            BusinessKinds.FoodRetail => 2,
            BusinessKinds.GoodsRetail => 5,
            BusinessKinds.ImportHub => 6,
            _ => 3
        };
    }

    private void CommodityWholesaleDay()
    {
        int day=checked((int)(Minute/DayMinutes));
        // Pay attention to production chains, not arbitrary business IDs.
        // A tannery holding hides but lacking fuel is a blocked production
        // line; it competes ahead of retailers filling optional inventories.
        // All ties rotate deterministically by day and seed.
        var orders=_businesses.SelectMany(b=>
            CommodityOrders(b).Select(o=>(Sku:o.Sku,
                    Target:MarketTarget(b,o.Sku,o.Target)))
                .Concat(CapitalOrders(b))
                .GroupBy(x=>x.Sku,StringComparer.Ordinal)
                .Select(g=>(Buyer:b,Sku:g.Key,Target:g.Max(x=>x.Target))))
            .OrderBy(o=>CommodityOrderPriority(o.Buyer,o.Sku))
            .ThenBy(o=>StableRandom.Hash(o.Buyer.Id+o.Sku,
                unchecked(_settings.Seed+day))).ToArray();
        foreach(var (buyer,sku,target) in orders)
        {
            int need=Math.Max(0,target-Quantity(buyer.Stock,sku)-Inbound(buyer,sku));
            if(need==0)continue;
            var sellers=_businesses.Where(b=>b.Id!=buyer.Id &&
                    CanSellCommodity(b,sku) &&
                    Quantity(b.Stock,sku)>0)
                // Compare total landed unit costs rather than choosing
                // the closest supplier even when its prices are ruinous.
                .OrderBy(b=>QuoteCommodityWholesalePriceMilli(b,sku)+
                    1000L*(_settings.BaseFreightFee+
                    (int)Math.Ceiling(PlaceTravelMinutes(b.Place,buyer.Place)/90d)*
                    _settings.FreightFeePer90Minutes)/
                    Math.Max(1,Math.Min(need,Math.Min(Quantity(b.Stock,sku),
                        _settings.MaxFreightLoad))))
                .ThenBy(b=>BusinessTravelCost(b,buyer))
                .ThenBy(b=>StableRandom.Hash(b.Id,
                    unchecked(_settings.Seed+day+17)));
            foreach(var seller in sellers)
            {
                if(need==0)break;
                int available=Quantity(seller.Stock,sku);
                decimal unitPrice=ToSeptims(
                    QuoteCommodityWholesalePriceMilli(seller,sku));
                int minutes=PlaceTravelMinutes(seller.Place,buyer.Place);
                int charge=checked(_settings.BaseFreightFee+
                    (int)Math.Ceiling(minutes/90d)*_settings.FreightFeePer90Minutes);
                if(buyer.Gold<charge)continue;
                decimal availableGold=buyer.Gold-charge;
                // Each unit price is quoted and settled in hundredths
                // of a septim. No rounding up whole-septim payments.
                int affordable=(int)Math.Min(int.MaxValue,
                    decimal.Floor(availableGold/unitPrice));
                int units=Math.Min(need,Math.Min(available,
                    Math.Min(_settings.MaxFreightLoad,affordable)));
                if(units==0)continue;

                decimal payment=checked(units*unitPrice);
                buyer.Gold-=payment+charge;
                seller.Gold+=payment;
                _publicGold+=charge;
                _freightFees+=charge;
                buyer.WholesaleSpending+=payment;
                seller.WholesaleRevenue+=payment;
                if(sku=="bread")
                {
                    buyer.BreadLandedGold=checked(buyer.BreadLandedGold+payment+charge);
                    buyer.BreadLandedUnits+=units;
                }
                decimal priceDelta=unitPrice-CommodityRecipes.Prices[sku];
                if(priceDelta>0)
                    _scarcityPremiumGold+=units*priceDelta;
                else if(priceDelta<0)
                    _surplusDiscountGold+=units*(-priceDelta);
                long sourceCost=WithdrawInventoryValue(seller,sku,units);
                StockRemove(seller.Stock,sku,units);
                MarketObserveSale(seller,sku,units,payment,sourceCost,true);
                // Freight still uses abstract common carriers, but long
                // cart journeys cause real horseshoe wear at the closest
                // operating depot. Without replacement shoes, some runs
                // proceed slowly using improvised transport.
                int duration=minutes;
                if(minutes>30 && ++_longFreightRuns%12==0)
                {
                    var depot=_ports.OrderBy(p=>BusinessTravelCost(p,seller))
                        .ThenBy(p=>p.Id,StringComparer.Ordinal).FirstOrDefault();
                    if(depot is not null &&
                        Quantity(depot.Stock,"horseshoes")>0)
                        UseBusinessSupply(depot,"horseshoes",1);
                    else
                    {
                        duration=checked(duration+90);
                        _carriageEquipmentDelays++;
                    }
                }
                int id=_nextFreightId++;
                long arrival=checked(Minute+duration);
                _freight.Add(id,new FreightShipment(
                    seller,buyer,sku,units,Minute,arrival,
                    ToMilli(payment+charge)));
                _queue.Enqueue(-2-id,(arrival,_sequence++));
                _shipmentsSent++;
                need-=units;
            }
            if(need>0)
            {
                _unfilledOrders+=need;
                _unfilledMarketOrders[sku]=checked(
                    _unfilledMarketOrders.GetValueOrDefault(sku)+need);
            }
        }
    }

    private bool HasPreparedMeal(BusinessState shop) =>
        CommodityRecipes.Meals.Any(s=>Quantity(shop.Stock,s)>0);

    // A home needing fuel or replacement tools can buy them even if it still
    // owns durable furniture. Purchases are limited by the usual shopping
    // cooldown and cash, rather than one undifferentiated Goods counter.
    private bool NeedsHeatingFuel(ResidentState resident)
    {
        long day=Minute/DayMinutes;
        long untilBurn=_settings.FirewoodDaysPerHousehold-
            day%_settings.FirewoodDaysPerHousehold;
        return resident.HousingId is not null &&
            Quantity(resident.Stock,"firewood")==0 && untilBurn<=2;
    }

    private bool WantsHouseholdSupply(ResidentState resident)
    {
        long day=Minute/DayMinutes;
        return NeedsHeatingFuel(resident) ||
            day>=10 && Quantity(resident.Stock,"iron_tools")==0 ||
            day>=20 && Quantity(resident.Stock,"leather_goods")==0 ||
            day>=30 && Quantity(resident.Stock,"furnishings")==0;
    }

    private string? NeededSupplyAtShop(ResidentState resident,BusinessState shop)
    {
        long day=Minute/DayMinutes;
        if(NeedsHeatingFuel(resident) &&
            Quantity(shop.Stock,"firewood")>0)return "firewood";
        if(day>=10 && Quantity(resident.Stock,"iron_tools")==0 &&
            Quantity(shop.Stock,"iron_tools")>0)return "iron_tools";
        if(day>=20 && Quantity(resident.Stock,"leather_goods")==0 &&
            Quantity(shop.Stock,"leather_goods")>0)return "leather_goods";
        if(day>=30 && Quantity(resident.Stock,"furnishings")==0 &&
            Quantity(shop.Stock,"furnishings")>0)return "furnishings";
        return null;
    }

    private void CompleteCommodityPurchase(ResidentState resident,bool food)
    {
        if(!_byBusiness.TryGetValue(resident.CurrentPlaceId,out var shop))return;
        if(food && !IsKitchenBusiness(shop) ||
            !food && shop.Kind is not (BusinessKinds.GoodsRetail or BusinessKinds.Workshop or
                BusinessKinds.Sawmill or BusinessKinds.Leatherworker))
            return;
        decimal cost=0;
        string[] candidates=food?CommodityRecipes.Meals:
            ["firewood","iron_tools","leather_goods","furnishings"];
        string? sku=food
            ? candidates.Where(s=>Quantity(shop.Stock,s)>0 &&
                    MealRetailQuote(shop,s)<=resident.Gold)
                .OrderBy(s=>MealRetailQuote(shop,s))
                .ThenBy(s=>Quantity(resident.Stock,s))
                .ThenBy(s=>StableRandom.Hash(s+resident.Source.Id,_settings.Seed))
                .FirstOrDefault()
            : NeededSupplyAtShop(resident,shop);
        if(sku is null)return;
        cost=food?MealRetailQuote(shop,sku):HouseholdGoodsQuote(shop,sku);
        if(resident.Gold<cost || cost<=0)return;
        int count=food && sku=="bread" && BreadBulkDiscount(shop) &&
            resident.Food<2 ? 2 : 1;
        long acquisition=WithdrawInventoryValue(shop,sku,count);
        StockRemove(shop.Stock,sku,count);
        StockAdd(resident.Stock,sku,count);
        MarketObserveSale(shop,sku,count,cost,acquisition,false);
        if(food)
        {
            resident.Food+=count;
            _foodSales+=count;
        }
        else
        {
            resident.Goods++;
            _goodsSales++;
            resident.LastGoodsShoppingDay=Minute/DayMinutes;
        }
        resident.Gold-=cost;
        decimal tax=food
            ?Math.Min(Money(cost*0.05m),Math.Max(0m,cost-0.01m))
            :Math.Min(Money(cost*0.10m),Math.Max(0m,cost-0.01m));
        if(food && sku=="bread")
        {
            _breadSalesUnits+=count;
            _breadSalesRevenue+=cost;
            shop.BreadSalesUnits+=count;
            shop.BreadRevenueGold+=cost;
            if(cost==1 && count==1)_oneSeptimBreadSold++;
            if(cost==1 && count>1)_discountedBreadUnits+=count;
        }
        shop.Gold+=cost-tax;
        _publicGold+=tax;
        _taxRevenue+=tax;
        shop.RetailRevenue+=cost-tax;
        shop.RetailSales++;
        resident.Purchases++;
        _purchases++;
        Observe(resident,"purchase",sku+" at "+shop.Name+" for "+cost+" gold");
    }

    private void EatCommodityMeal(ResidentState resident)
    {
        string? sku=CommodityRecipes.Meals.FirstOrDefault(
            s=>Quantity(resident.Stock,s)>0);
        if(sku is null)
            throw new InvalidOperationException("Resident food count has no physical meal");
        UseFinished(resident.Stock,sku,1);
        resident.Food--;
        resident.Satiation=Math.Min(100,
            resident.Satiation+CommodityRecipes.SatiationPerMeal(sku));
        resident.MealsEaten++;
        _meals++;
        Observe(resident,"eat","Consumed "+sku+"; satiation "+resident.Satiation.ToString("0"));
    }

    private void HarvestCommoditySubsistence(ResidentState resident,BusinessState plot)
    {
        int amount=Math.Min(2,
            Math.Min(plot.LandYieldRemaining,plot.ForageRemaining));
        if(amount<=0)return;
        plot.LandYieldRemaining-=amount;
        plot.ForageRemaining-=amount;
        Extract(resident.Stock,"foraged_greens",amount);
        resident.Food+=amount;
        resident.SubsistenceFoodHarvested+=amount;
        _subsistenceFood+=amount;
        _localFood+=amount;
        Observe(resident,"forage",amount+" edible greens collected at "+plot.Name);
    }

    private long CommodityStockOnHand(string sku) =>
        _businesses.Sum(b=>(long)Quantity(b.Stock,sku))+
        _residents.Sum(r=>(long)Quantity(r.Stock,sku));

    private CommoditySnapshot[] CommodityLedger() =>
        CommodityRecipes.Prices.Keys.OrderBy(k=>k,StringComparer.Ordinal)
            .Select(s=>
            {
                var a=Account(s);
                return new CommoditySnapshot(s,a.Initial,a.Extracted,a.Crafted,
                    a.Ingredients,a.Consumed,CommodityStockOnHand(s),InboundAll(s));
            }).ToArray();

    private long InboundAll(string sku) =>
        _freight.Values.Where(x=>x.Commodity==sku).Sum(x=>(long)x.Quantity);

    private void ValidateCommodityLedger()
    {
        var unknown=_businesses.SelectMany(b=>b.Stock.Keys)
            .Concat(_residents.SelectMany(r=>r.Stock.Keys))
            .Concat(_freight.Values.Select(v=>v.Commodity))
            .Except(CommodityRecipes.Prices.Keys,StringComparer.Ordinal).ToArray();
        if(unknown.Length>0)throw new InvalidOperationException(
            "Unregistered commodities: "+string.Join(",",unknown));
        foreach(var entry in CommodityLedger())
            if(entry.StockOnHand<0 || entry.InTransit<0 ||
               entry.Initial+entry.Extracted+entry.Crafted!=
               entry.StockOnHand+entry.InTransit+entry.UsedAsIngredient+entry.Consumed)
                throw new InvalidOperationException(
                    "Commodity stock does not reconcile: "+entry.Commodity);
        foreach(var resident in _residents)
        {
            if(resident.Food!=CommodityRecipes.Meals.Sum(
                sku=>Quantity(resident.Stock,sku)))
                throw new InvalidOperationException("Resident food does not match meals");
            if(resident.Goods!=CommodityRecipes.HouseholdGoods.Sum(
                sku=>Quantity(resident.Stock,sku)))
                throw new InvalidOperationException("Resident goods do not match supplies");
        }
    }
}
