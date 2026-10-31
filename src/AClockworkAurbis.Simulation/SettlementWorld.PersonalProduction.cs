namespace AClockworkAurbis.Simulation;

// These independent commons are simulation assumptions, not verified ESO nodes.
// Commercial mines, farms and fisheries retain their own protected inventories.
public sealed record OpenResourceSnapshot(
    string PlaceId,string Name,int Ore,int Logs,int Fish,int Game);

internal sealed class OpenResourceSite(
    PlaceDefinition place,int residents,SettlementSettings settings)
{
    public PlaceDefinition Place { get; } = place;
    public int Ore { get; private set; } = checked(residents*settings.OpenOrePerResident);
    public int Logs { get; private set; } = checked(residents*settings.OpenLogsPerResidentPerDay);
    public int Fish { get; private set; } = checked(residents*settings.OpenFishPerResidentPerDay);
    public int Game { get; private set; } = checked(residents*settings.OpenGamePerResidentPerDay);
    private readonly int _logCap=checked(residents*settings.OpenLogsPerResidentPerDay);
    private readonly int _fishCap=checked(residents*settings.OpenFishPerResidentPerDay);
    private readonly int _gameCap=checked(residents*settings.OpenGamePerResidentPerDay);

    public int Remaining(string sku) => sku switch
    {
        "iron_ore"=>Ore, "logs"=>Logs, "fish"=>Fish, "game_meat"=>Game, _=>0
    };

    public void Regrow()
    {
        Logs=Math.Min(_logCap,checked(Logs+_logCap));
        Fish=Math.Min(_fishCap,checked(Fish+_fishCap));
        Game=Math.Min(_gameCap,checked(Game+_gameCap));
        // Geological ore is a finite deposit, not a renewable crop.
    }

    public void Take(string sku,int quantity)
    {
        if(quantity<=0 || quantity>Remaining(sku))
            throw new InvalidOperationException("Independent resource depleted");
        switch(sku)
        {
            case "iron_ore":Ore-=quantity;break;
            case "logs":Logs-=quantity;break;
            case "fish":Fish-=quantity;break;
            case "game_meat":Game-=quantity;break;
            default:throw new ArgumentException("Unknown resource",nameof(sku));
        }
    }
}

public sealed partial class SettlementWorld
{
    private enum PersonalTaskKind { Gather,Craft,Buy,Sell }
    private sealed record PersonalTask(
        PersonalTaskKind Kind,string Destination,string Sku,int Quantity);
    private readonly Dictionary<string,PersonalTask> _personalTasks=new(StringComparer.Ordinal);
    private readonly Dictionary<string,OpenResourceSite> _openResourceSites=new(StringComparer.Ordinal);
    private long _personalGatherHours,_personalCraftBatches,_personalTrades;
    private decimal _personalSaleIncome;

    public IReadOnlyList<OpenResourceSnapshot> OpenResources =>
        _openResourceSites.Values.OrderBy(s=>s.Place.Id,StringComparer.Ordinal)
            .Select(s=>new OpenResourceSnapshot(s.Place.Id,s.Place.Name,
                s.Ore,s.Logs,s.Fish,s.Game)).ToArray();

    private void InitializeOpenResources()
    {
        if(!Detailed || !_settings.EnableLocalProduction)return;
        int i=0;
        foreach(var group in _residents.GroupBy(p=>Neighbourhood(ResidentAnchor(p)))
            .OrderBy(g=>g.Key,StringComparer.Ordinal))
        {
            var anchor=group.Select(ResidentAnchor).FirstOrDefault(p=>p?.MapX is not null)
                ?? group.Select(ResidentAnchor).FirstOrDefault();
            string id="sim:open-resource:"+i++;
            var place=new PlaceDefinition(id,0,
                "Open countryside near "+(anchor?.District??anchor?.Name??"unmapped country"),
                "simulation_independent_resource",anchor?.Settlement,Catalog.Settlement,
                "independent_gathering","",anchor?.MapX,anchor?.MapY,
                "simulation_at_source_neighbourhood",anchor?.District);
            _openResourceSites.Add(id,new OpenResourceSite(place,group.Count(),_settings));
            _sourcePlaces.Add(id,place);
            _placeNames.Add(id,place.Name);
        }
    }

    private void RegrowOpenResources()
    {
        foreach(var site in _openResourceSites.Values)site.Regrow();
    }

    private ResidentState RequireResident(string residentId) =>
        _residentById.TryGetValue(residentId,out var resident) ? resident :
            throw new KeyNotFoundException("Unknown resident "+residentId);

    private static string? GatheringTool(string sku) => sku switch
    {
        "iron_ore"=>"pickaxe",
        "logs"=>"felling_axe",
        _=>null
    };

    private bool EnqueuePersonal(ResidentState resident,PersonalTask task)
    {
        // A crop failure blocks new harvesting and crafting, not exchange
        // of stock already held by people and shops.
        if(!Detailed ||
           !_settings.EnableLocalProduction &&
               (task.Kind is PersonalTaskKind.Gather or PersonalTaskKind.Craft) ||
           _personalTasks.ContainsKey(resident.Source.Id) ||
           resident.Activity is SettlementAction.PersonalGather or
               SettlementAction.PersonalCraft or SettlementAction.PersonalBuy or
               SettlementAction.PersonalSell)return false;
        _personalTasks.Add(resident.Source.Id,task);
        resident.PersonalTask=task.Kind+":"+task.Sku;
        return true;
    }

    // The same commands can be issued by a future player controller or NPC planner.
    // They queue real timed actions rather than granting items immediately.
    public bool RequestPersonalGather(string residentId,string sku)
    {
        var resident=RequireResident(residentId);
        if(sku is not ("iron_ore" or "logs" or "fish" or "game_meat") ||
           GatheringTool(sku) is string tool && Quantity(resident.Stock,tool)==0 ||
           Quantity(resident.Stock,sku)>=_settings.MaxPersonalCommodityStock ||
           sku=="game_meat" && Quantity(resident.Stock,"raw_hide")>=
               _settings.MaxPersonalCommodityStock)return false;
        var site=_openResourceSites.Values.Where(s=>s.Remaining(sku)>0)
            .OrderBy(s=>JourneyDuration(resident,s.Place.Id))
            .ThenBy(s=>s.Place.Id,StringComparer.Ordinal).FirstOrDefault();
        return site is not null && EnqueuePersonal(resident,
            new PersonalTask(PersonalTaskKind.Gather,site.Place.Id,sku,1));
    }

    public bool RequestPersonalCraft(string residentId,string recipeId)
    {
        var resident=RequireResident(residentId);
        var recipe=CommodityRecipes.PersonalRecipes.FirstOrDefault(r=>r.Id==recipeId);
        if(recipe is null ||
           !RecipeAvailable(recipe,resident.Stock,_settings.MaxPersonalCommodityStock))
            return false;
        var station=_businesses.Where(b=>b.Kind==recipe.Industry)
            .OrderBy(b=>JourneyDuration(resident,b.Id))
            .ThenBy(b=>b.Id,StringComparer.Ordinal).FirstOrDefault();
        return station is not null && EnqueuePersonal(resident,
            new PersonalTask(PersonalTaskKind.Craft,station.Id,recipe.Id,1));
    }

    private static bool SellsToIndividuals(BusinessState firm,string sku) =>
        CanSellCommodity(firm,sku) ||
        firm.Kind==BusinessKinds.GoodsRetail &&
            CommodityRecipes.FinishedGoods.Contains(sku,StringComparer.Ordinal) ||
        firm.Kind==BusinessKinds.FoodRetail && CommodityRecipes.Edible(sku);

    public bool RequestPersonalPurchase(string residentId,string sellerId,string sku,
        int quantity=1)
    {
        var resident=RequireResident(residentId);
        if(quantity is <1 or >12 || !_byBusiness.TryGetValue(sellerId,out var seller) ||
           !CommodityRecipes.Prices.ContainsKey(sku) || !SellsToIndividuals(seller,sku) ||
           Quantity(seller.Stock,sku)<quantity ||
           Quantity(resident.Stock,sku)>_settings.MaxPersonalCommodityStock-quantity ||
           resident.Gold<checked(MarketRetailQuote(seller,sku)*quantity))return false;
        return EnqueuePersonal(resident,
            new PersonalTask(PersonalTaskKind.Buy,seller.Id,sku,quantity));
    }

    private static bool BuysFromIndividuals(BusinessState firm,string sku) =>
        firm.Kind switch
        {
            BusinessKinds.Smeltery=>sku=="iron_ore",
            BusinessKinds.Sawmill=>sku=="logs",
            BusinessKinds.Tannery=>sku=="raw_hide",
            BusinessKinds.Workshop=>sku is "iron_ingot" or "lumber",
            BusinessKinds.Leatherworker=>sku=="leather",
            BusinessKinds.Bakery=>sku is "wheat" or "potato" or "onion" or
                "lettuce" or "fish" or "game_meat" or "firewood",
            BusinessKinds.FoodRetail=>CommodityRecipes.Edible(sku),
            BusinessKinds.GoodsRetail=>CommodityRecipes.FinishedGoods
                .Contains(sku,StringComparer.Ordinal),
            _=>false
        };

    private static decimal IndividualBuyback(string sku) =>
        Money(CommodityRecipes.Prices[sku]*0.6m);

    public bool RequestPersonalSale(string residentId,string buyerId,string sku,
        int quantity=1)
    {
        var resident=RequireResident(residentId);
        if(quantity is <1 or >12 || !_byBusiness.TryGetValue(buyerId,out var buyer) ||
           !CommodityRecipes.Prices.ContainsKey(sku) ||
           !BuysFromIndividuals(buyer,sku) ||
           Quantity(resident.Stock,sku)<quantity ||
           resident.EquippedWeaponSku==sku &&
               Quantity(resident.Stock,sku)<=quantity ||
           Quantity(buyer.Stock,sku)>_settings.MaxCommodityStockPerSite-quantity ||
           buyer.Gold<checked(IndividualBuyback(sku)*quantity))return false;
        return EnqueuePersonal(resident,
            new PersonalTask(PersonalTaskKind.Sell,buyer.Id,sku,quantity));
    }

    public bool EquipPersonalWeapon(string residentId,string sku)
    {
        var resident=RequireResident(residentId);
        if(sku!="iron_sword" || Quantity(resident.Stock,sku)==0)return false;
        resident.EquippedWeaponSku=sku;
        return true;
    }

    public bool UnequipPersonalWeapon(string residentId)
    {
        var resident=RequireResident(residentId);
        if(resident.EquippedWeaponSku is null)return false;
        resident.EquippedWeaponSku=null;
        return true;
    }

    public bool CancelPersonalTask(string residentId)
    {
        var resident=RequireResident(residentId);
        resident.PersonalTask=null;
        return _personalTasks.Remove(residentId);
    }

    private bool SchedulePersonalTask(int index,ResidentState resident)
    {
        if(!_personalTasks.TryGetValue(resident.Source.Id,out var task))return false;
        // Exhausted characters recover before undertaking extraction or craft.
        if(task.Kind is PersonalTaskKind.Gather or PersonalTaskKind.Craft &&
           resident.Energy<20)return false;
        if(resident.CurrentPlaceId!=task.Destination)
            Travel(index,resident,task.Destination);
        else Enqueue(index,task.Kind switch
        {
            PersonalTaskKind.Gather=>60,PersonalTaskKind.Craft=>90,_=>5
        },task.Kind switch
        {
            PersonalTaskKind.Gather=>SettlementAction.PersonalGather,
            PersonalTaskKind.Craft=>SettlementAction.PersonalCraft,
            PersonalTaskKind.Buy=>SettlementAction.PersonalBuy,
            _=>SettlementAction.PersonalSell
        });
        return true;
    }

    private void CompletePersonalTask(ResidentState resident,PersonalTaskKind kind)
    {
        if(!_personalTasks.TryGetValue(resident.Source.Id,out var task) ||
           task.Kind!=kind)return;
        _personalTasks.Remove(resident.Source.Id);
        resident.PersonalTask=null;
        if(resident.CurrentPlaceId!=task.Destination)
        {
            Observe(resident,"personal_failed",task.Kind+":"+task.Sku+
                " destination changed before completion");
            return;
        }
        bool succeeded=kind switch
        {
            PersonalTaskKind.Gather=>CompletePersonalGather(resident,task),
            PersonalTaskKind.Craft=>CompletePersonalCraft(resident,task),
            PersonalTaskKind.Buy=>CompletePersonalBuy(resident,task),
            PersonalTaskKind.Sell=>CompletePersonalSell(resident,task),
            _=>false
        };
        if(!succeeded)
            Observe(resident,"personal_failed",task.Kind+":"+task.Sku+
                " no longer has the required funds, resources or capacity");
    }

    private bool CompletePersonalGather(ResidentState resident,PersonalTask task)
    {
        if(!_openResourceSites.TryGetValue(task.Destination,out var site) ||
           site.Remaining(task.Sku)<=0 ||
           GatheringTool(task.Sku) is string tool &&
               Quantity(resident.Stock,tool)==0)return false;
        int amount=Math.Min(site.Remaining(task.Sku),
            task.Sku is "fish" or "game_meat" ? 1 : _settings.RawMaterialPerWorkHour);
        amount=Math.Min(amount,_settings.MaxPersonalCommodityStock-
            Quantity(resident.Stock,task.Sku));
        if(task.Sku=="game_meat")
            amount=Math.Min(amount,_settings.MaxPersonalCommodityStock-
                Quantity(resident.Stock,"raw_hide"));
        if(amount<=0)return false;
        site.Take(task.Sku,amount);
        Extract(resident.Stock,task.Sku,amount);
        if(task.Sku=="game_meat")Extract(resident.Stock,"raw_hide",amount);
        // Raw fish and game meat are cooking ingredients, not prepared meals.
        // Keep them in inventory until cooked; never increment the meal count.
        if(task.Sku is "fish" or "game_meat")_localFood+=amount;
        else _localMaterials+=amount;
        resident.PersonalGathered+=amount;
        _personalGatherHours++;
        if(GatheringTool(task.Sku) is string implement)
        {
            int wear=resident.PersonalToolWorkHours.GetValueOrDefault(implement)+1;
            if(wear>=_settings.ToolWorkHoursPerWear)
            {
                resident.PersonalToolWorkHours.Remove(implement);
                UseFinished(resident.Stock,implement,1);
            }
            else resident.PersonalToolWorkHours[implement]=wear;
        }
        Observe(resident,"personal_gather",task.Sku+": "+amount+
            " kept in personal inventory");
        return true;
    }

    private bool CompletePersonalCraft(ResidentState resident,PersonalTask task)
    {
        var recipe=CommodityRecipes.PersonalRecipes.FirstOrDefault(r=>r.Id==task.Sku);
        if(recipe is null || !_byBusiness.TryGetValue(task.Destination,out var station) ||
           station.Kind!=recipe.Industry ||
           !RecipeAvailable(recipe,resident.Stock,_settings.MaxPersonalCommodityStock))
            return false;
        foreach(var (sku,count) in recipe.Ingredients)
        {
            UseIngredient(resident.Stock,sku,count);
            if(CommodityRecipes.HouseholdGoods.Contains(sku,StringComparer.Ordinal))
                resident.Goods=checked(resident.Goods-count);
        }
        foreach(var (sku,count) in recipe.Products)
        {
            Make(resident.Stock,sku,count);
            if(CommodityRecipes.HouseholdGoods.Contains(sku,StringComparer.Ordinal))
                resident.Goods=checked(resident.Goods+count);
        }
        resident.PersonalCrafts++;
        _personalCraftBatches++;
        _recipeBatches++;
        Observe(resident,"personal_craft",recipe.Id+" at "+station.Name);
        return true;
    }

    private bool CompletePersonalBuy(ResidentState resident,PersonalTask task)
    {
        if(!_byBusiness.TryGetValue(task.Destination,out var seller) ||
           !SellsToIndividuals(seller,task.Sku) ||
           Quantity(seller.Stock,task.Sku)<task.Quantity ||
           Quantity(resident.Stock,task.Sku)>
               _settings.MaxPersonalCommodityStock-task.Quantity)return false;
        decimal cost=checked(MarketRetailQuote(seller,task.Sku)*task.Quantity);
        if(resident.Gold<cost)return false;
        long basis=WithdrawInventoryValue(seller,task.Sku,task.Quantity);
        StockRemove(seller.Stock,task.Sku,task.Quantity);
        StockAdd(resident.Stock,task.Sku,task.Quantity);
        if(CommodityRecipes.Edible(task.Sku))
            resident.Food=checked(resident.Food+task.Quantity);
        if(CommodityRecipes.HouseholdGoods.Contains(task.Sku,StringComparer.Ordinal))
            resident.Goods=checked(resident.Goods+task.Quantity);
        decimal tax=Money(cost*(CommodityRecipes.HouseholdGoods.Contains(
            task.Sku,StringComparer.Ordinal) ||
            !CommodityRecipes.Edible(task.Sku)?_settings.DetailedGoodsTaxRate:
            _settings.DetailedFoodTaxRate));
        tax=Math.Min(tax,Math.Max(0,cost-0.01m));
        resident.Gold-=cost;
        seller.Gold+=cost-tax;
        _publicGold+=tax;
        _taxRevenue+=tax;
        _retailSalesTaxRevenue+=tax;
        seller.RetailRevenue+=cost-tax;
        seller.RetailSales++;
        MarketObserveSale(seller,task.Sku,task.Quantity,cost,basis,false);
        resident.Purchases++;
        _purchases++;
        _personalTrades++;
        Observe(resident,"personal_buy",task.Sku+": "+task.Quantity+
            " for "+cost+" gold from "+seller.Name);
        return true;
    }

    private bool CompletePersonalSell(ResidentState resident,PersonalTask task)
    {
        if(!_byBusiness.TryGetValue(task.Destination,out var buyer) ||
           !BuysFromIndividuals(buyer,task.Sku) ||
           Quantity(resident.Stock,task.Sku)<task.Quantity ||
           resident.EquippedWeaponSku==task.Sku &&
               Quantity(resident.Stock,task.Sku)<=task.Quantity ||
           Quantity(buyer.Stock,task.Sku)>
               _settings.MaxCommodityStockPerSite-task.Quantity)return false;
        decimal payment=checked(IndividualBuyback(task.Sku)*task.Quantity);
        if(buyer.Gold<payment)return false;
        StockRemove(resident.Stock,task.Sku,task.Quantity);
        StockAdd(buyer.Stock,task.Sku,task.Quantity);
        CreditInventoryValue(buyer,task.Sku,ToMilli(payment));
        if(CommodityRecipes.Edible(task.Sku))
            resident.Food=checked(resident.Food-task.Quantity);
        if(CommodityRecipes.HouseholdGoods.Contains(task.Sku,StringComparer.Ordinal))
            resident.Goods=checked(resident.Goods-task.Quantity);
        buyer.Gold-=payment;
        buyer.WholesaleSpending+=payment;
        resident.Gold+=payment;
        resident.PersonalSaleIncome+=payment;
        _personalSaleIncome+=payment;
        _personalTrades++;
        Observe(resident,"personal_sell",task.Sku+": "+task.Quantity+
            " for "+payment+" gold to "+buyer.Name);
        return true;
    }
}
