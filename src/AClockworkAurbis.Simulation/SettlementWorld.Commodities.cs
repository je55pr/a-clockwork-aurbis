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

    private bool CompleteCommodityWork(ResidentState actor,BusinessState company,int hours)
    {
        if(!_settings.EnableLocalProduction || hours<=0)return true;
        switch(company.Kind)
        {
            case BusinessKinds.FoodProducer:
            {
                var crops=CropMix(company);
                for(int n=0;n<hours && company.LandYieldRemaining>0;n++)
                {
                    string crop=crops[(company.WorkHours-hours+n)%crops.Length];
                    int capacity=Math.Max(0,_settings.MaxCommodityStockPerSite-
                        Quantity(company.Stock,crop));
                    int picked=Math.Min(capacity,Math.Min(company.LandYieldRemaining,
                        _settings.RenewableFoodPerWorkHour));
                    if(picked==0)continue;
                    company.LandYieldRemaining-=picked;
                    Extract(company.Stock,crop,picked);
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
                        _settings.RawMaterialPerWorkHour));
                    if(picked==0)break;
                    company.MineralReserve-=picked;
                    Extract(company.Stock,output,picked);
                    company.MaterialsExtracted+=picked;
                    _localMaterials+=picked;
                    company.ToolWearHours++;
                    if(company.ToolWearHours>=_settings.ToolWorkHoursPerWear)
                    {
                        company.ToolWearHours=0;
                        UseFinished(company.Stock,tool,1);
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
                    int units=Math.Min(capacity,Math.Min(2,company.LandYieldRemaining));
                    if(units<=0)break;
                    company.LandYieldRemaining-=units;
                    if(company.Kind==BusinessKinds.Fishery)
                        Extract(company.Stock,"fish",units);
                    else
                    {
                        Extract(company.Stock,"game_meat",units);
                        Extract(company.Stock,"raw_hide",units);
                    }
                    _localFood+=units;
                }
                break;
            }
            case BusinessKinds.Smeltery:
            case BusinessKinds.Sawmill:
            case BusinessKinds.Tannery:
            case BusinessKinds.Bakery:
            case BusinessKinds.Leatherworker:
            case BusinessKinds.Workshop:
            {
                for(int n=0;n<hours;n++)
                {
                    var available=CommodityRecipes.For(company)
                        .Where(r=>r.Ingredients.All(input=>
                            Quantity(company.Stock,input.Key)>=input.Value) &&
                            r.Products.All(output=>
                                Quantity(company.Stock,output.Key)+output.Value<=
                                _settings.MaxCommodityStockPerSite))
                        .OrderBy(r=>r.Products.Sum(p=>
                            Quantity(company.Stock,p.Key)))
                        .ThenBy(r=>StableRandom.Hash(r.Id,
                            unchecked(_settings.Seed+(int)(Minute/DayMinutes))))
                        .ToArray();
                    if(available.Length==0)break;
                    var recipe=available[0];
                    foreach(var input in recipe.Ingredients)
                        UseIngredient(company.Stock,input.Key,input.Value);
                    foreach(var output in recipe.Products)
                        Make(company.Stock,output.Key,output.Value);
                    _recipeBatches++;
                    company.ItemsCrafted+=recipe.Products.Values.Sum();
                    _crafted+=recipe.Products.Values.Sum();
                }
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
        if(_settings.EnableLocalProduction)
        {
            int perFarm=_farms.Length==0?0:
                Math.Max(10,checked(_residents.Count*
                    _settings.DailyLandRegrowthPerResident/_farms.Length));
            foreach(var farm in _farms)
                farm.LandYieldRemaining=Math.Min(checked(perFarm*2),
                    farm.LandYieldRemaining+perFarm);
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
                return Pair("bread","vegetable_stew","garden_salad","fish_meal","roast_meat")
                    .Select(x=>(x.Item1,24));
            case BusinessKinds.GoodsRetail:
                return Pair("firewood","iron_tools","leather_goods","furnishings");
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
                "garden_salad" or "fish_meal" or "roast_meat",
            BusinessKinds.Workshop or BusinessKinds.Leatherworker =>
                CommodityRecipes.For(seller).Any(r=>r.Products.ContainsKey(sku)),
            _ => false // Retail stock, input stores, tools and carriers aren't a magic wholesaler.
        };

    private void CommodityWholesaleDay()
    {
        int day=checked((int)(Minute/DayMinutes));
        // Within each day every business requests inputs independently.
        // The nearest actual stocked supplier wins; nothing teleports.
        foreach(var buyer in _businesses
            .OrderBy(b=>StableRandom.Hash(b.Id,unchecked(_settings.Seed+day))))
        foreach(var (sku,target) in CommodityOrders(buyer))
        {
            int need=Math.Max(0,target-Quantity(buyer.Stock,sku)-Inbound(buyer,sku));
            if(need==0)continue;
            int unitPrice=CommodityRecipes.Prices[sku];
            var sellers=_businesses.Where(b=>b.Id!=buyer.Id &&
                    CanSellCommodity(b,sku) &&
                    Quantity(b.Stock,sku)>0)
                .OrderBy(b=>BusinessTravelCost(b,buyer))
                .ThenBy(b=>StableRandom.Hash(b.Id,
                    unchecked(_settings.Seed+day+17)));
            foreach(var seller in sellers)
            {
                if(need==0)break;
                int available=Quantity(seller.Stock,sku);
                int minutes=PlaceTravelMinutes(seller.Place,buyer.Place);
                int charge=checked(_settings.BaseFreightFee+
                    (int)Math.Ceiling(minutes/90d)*_settings.FreightFeePer90Minutes);
                if(buyer.Gold<charge)continue;
                int affordable=unitPrice==0?need:
                    (int)Math.Min(int.MaxValue,(buyer.Gold-charge)/unitPrice);
                int units=Math.Min(need,Math.Min(available,
                    Math.Min(_settings.MaxFreightLoad,affordable)));
                if(units==0)continue;

                long payment=checked((long)units*unitPrice);
                buyer.Gold-=payment+charge;
                seller.Gold+=payment;
                _publicGold+=charge;
                _freightFees+=charge;
                buyer.WholesaleSpending+=payment;
                seller.WholesaleRevenue+=payment;
                StockRemove(seller.Stock,sku,units);
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
                        UseFinished(depot.Stock,"horseshoes",1);
                    else
                    {
                        duration=checked(duration+90);
                        _carriageEquipmentDelays++;
                    }
                }
                int id=_nextFreightId++;
                long arrival=checked(Minute+duration);
                _freight.Add(id,new FreightShipment(
                    seller,buyer,sku,units,Minute,arrival));
                _queue.Enqueue(-2-id,(arrival,_sequence++));
                _shipmentsSent++;
                need-=units;
            }
        }
    }

    private bool HasPreparedMeal(BusinessState shop) =>
        CommodityRecipes.Meals.Any(s=>Quantity(shop.Stock,s)>0);

    private bool HasHouseholdSupply(BusinessState shop) =>
        CommodityRecipes.HouseholdGoods.Any(s=>Quantity(shop.Stock,s)>0);

    private void CompleteCommodityPurchase(ResidentState resident,bool food)
    {
        if(!_byBusiness.TryGetValue(resident.CurrentPlaceId,out var shop))return;
        if(food && shop.Kind!=BusinessKinds.FoodRetail ||
            !food && shop.Kind is not (BusinessKinds.GoodsRetail or BusinessKinds.Workshop))
            return;
        int cost=food?_settings.MealPrice:_settings.GoodsPrice;
        if(resident.Gold<cost)return;
        string[] candidates=food?CommodityRecipes.Meals:
            ["firewood","iron_tools","leather_goods","furnishings"];
        string? sku=candidates.Where(s=>Quantity(shop.Stock,s)>0)
            .OrderBy(s=>Quantity(resident.Stock,s))
            .ThenBy(s=>StableRandom.Hash(s+resident.Source.Id,_settings.Seed))
            .FirstOrDefault();
        if(sku is null)return;
        StockRemove(shop.Stock,sku,1);
        StockAdd(resident.Stock,sku,1);
        if(food)
        {
            resident.Food++;
            _foodSales++;
        }
        else
        {
            resident.Goods++;
            _goodsSales++;
            resident.LastGoodsShoppingDay=Minute/DayMinutes;
        }
        resident.Gold-=cost;
        int tax=food?_settings.FoodSalesTax:_settings.GoodsSalesTax;
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
        resident.Satiation=Math.Min(100,resident.Satiation+56);
        resident.MealsEaten++;
        _meals++;
        Observe(resident,"eat","Consumed "+sku+"; satiation "+resident.Satiation.ToString("0"));
    }

    private void HarvestCommoditySubsistence(ResidentState resident,BusinessState plot)
    {
        int amount=Math.Min(2,plot.LandYieldRemaining);
        if(amount<=0)return;
        plot.LandYieldRemaining-=amount;
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
