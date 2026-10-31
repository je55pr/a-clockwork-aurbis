namespace AClockworkAurbis.Simulation;

public sealed partial class SettlementWorld
{
    private void Observe(ResidentState actor,string action,string detail)
    {
        if(Observer!=null)
            Observer(new SimObservation(Minute,actor.Source.Id,action,detail));
    }

    private decimal TotalGold() =>
        checked(_businesses.Sum(b=>b.Gold)+_residents.Sum(r=>r.Gold)+_publicGold);

    private long TotalFood() =>
        Detailed ? CommodityRecipes.Meals.Sum(s=>
            CommodityStockOnHand(s)+InboundAll(s)) :
        checked(_businesses.Sum(b=>(long)b.Food)+_residents.Sum(r=>(long)r.Food)
            +TransitCount("food"));

    private long TotalMaterials() =>
        Detailed ? CommodityRecipes.Prices.Keys
            .Where(s=>!CommodityRecipes.Edible(s) &&
                !CommodityRecipes.FinishedGoods.Contains(s,StringComparer.Ordinal))
            .Sum(s=>CommodityStockOnHand(s)+InboundAll(s)) :
        checked(_businesses.Sum(b=>(long)b.Materials)+TransitCount("materials"));

    private long TotalGoods() =>
        Detailed ? CommodityRecipes.FinishedGoods.Sum(s=>
            CommodityStockOnHand(s)+InboundAll(s)) :
        checked(_businesses.Sum(b=>(long)b.Goods)+_residents.Sum(r=>(long)r.Goods)
            +TransitCount("goods"));

    public SettlementReport Report()
    {
        return new SettlementReport
        {
            Settlement=Catalog.Settlement,
            SourcePlaces=Catalog.Places.Count(p=>p.SourcePageId>0),
            SimulatedPlaces=Catalog.Places.Count(p=>p.SourcePageId==0),
            SimulatedBusinesses=_businesses.Count(b=>b.Place.SourcePageId==0),
            Businesses=_businesses.Count,
            Residents=_residents.Count,
            SourceHomeAssignments=_residents.Count(p=>p.Source.HomePlaceId is not null),
            SourceStoreAssignments=_residents.Count(p=>p.Source.WorkEvidence=="explicit_npc_store"),
            InferredWorkAssignments=_residents.Count(p=>p.Source.WorkEvidence=="inferred_profession_and_location"),
            SyntheticHomeCount=_residents.Count(p=>p.Source.HomePlaceId is null),
            UnemployedResidents=_residents.Count(p=>p.WorkId is null),
            ResidentsWithLowSatiation=_residents.Count(p=>p.Satiation<20),
            ResidentsWithZeroSatiation=_residents.Count(p=>p.Satiation<=0),
            ResidentsWithoutFood=_residents.Count(p=>p.Food==0),
            ResidentsWithoutGold=_residents.Count(p=>p.Gold==0),
            Minute=Minute,Events=_events,Wages=_wages,Dividends=_dividends,UnpaidHours=_unpaid,
            RetailFoodSales=_foodSales,RetailGoodsSales=_goodsSales,
            Meals=_meals,Travel=_travel,TravelMinutes=_travelMinutes,LongJourneys=_longJourneys,
            ResidentsWithMapAnchor=_residents.Count(r=>r.Source.AnchorPlaceId is not null &&
                _sourcePlaces.TryGetValue(r.Source.AnchorPlaceId,out var p)&&p.MapX.HasValue),
            PlacesWithSourceMarker=Catalog.Places.Count(p=>p.MapEvidence=="source_map_marker"),
            PlacesWithEstimatedMarker=Catalog.Places.Count(p=>p.MapEvidence=="estimated_parent_settlement_marker"),
            CraftedGoods=_crafted,GoodsUsed=_goodsUsed,
            FoodImported=_foodImported,MaterialsImported=_materialsImported,
            FoodRemaining=TotalFood(),MaterialsRemaining=TotalMaterials(),
            GoodsRemaining=TotalGoods(),
            Economy=_settings.Economy.ToString(),
            SimulatedContracts=_simulatedContracts,
            FoodProducedLocally=_localFood,
            FoodHarvestedForSubsistence=_subsistenceFood,
            CasualWorkHours=_casualHours,CasualWages=_casualWages,
            CasualWorkers=_casualWorkers.Count,WildForageSites=_wildForageSites.Count,
            PersonalGatherHours=_personalGatherHours,
            PersonalCraftBatches=_personalCraftBatches,
            PersonalTrades=_personalTrades,PersonalSaleIncome=_personalSaleIncome,
            OpenResourceSites=_openResourceSites.Count,
            RawMaterialsExtracted=_localMaterials,
            FoodExported=_foodExports,GoodsExported=_goodsExports,
            MaterialsExported=_materialsExports,
            FoodBoughtExternally=_foodImportsPaidUnits,
            MaterialsBoughtExternally=_materialsImportsPaidUnits,
            GoldPaidAbroad=_goldImportsPaid,GoldReceivedFromAbroad=_goldExportsEarned,
            AssistanceGoldPaid=_aidPaid,AssistedResidents=_aidRecipients.Count,
            RevenueTaxed=_taxRevenue,
            RetailSalesTaxRevenue=_retailSalesTaxRevenue,
            CooperativeDistributions=_cooperativeDistributions,
            PublicGold=_publicGold,ExternalGold=_outsideGold,
            InitialAllAccountsGold=Regional?_initialAllGold:_initialGold,
            AllAccountsGold=TotalGold()+_outsideGold,
            ExternalFood=_outsideFood,ExternalMaterials=_outsideMaterials,
            TotalFoodIncludingExternal=TotalFood()+_outsideFood,
            TotalMaterialsIncludingExternal=TotalMaterials()+TotalGoods()+_outsideMaterials+_outsideGoods,
            FoodProducers=_farms.Length,ResourceProducers=_extractors.Length,
            ResidentsFurloughed=_residents.Count(p=>p.OnUnpaidLeave),
            PersonHoursAtZeroSatiation=Math.Round(_residents.Sum(p=>p.MinutesAtZeroSatiation)/60d,2),
            PersonHoursBelow20Satiation=Math.Round(_residents.Sum(p=>p.MinutesBelow20Satiation)/60d,2),
            WorkersFurloughedCumulative=_furloughs,JobsReassigned=_rehired,
            HousingUnits=_housing.Count, HousingBeds=_housing.Sum(h=>h.Beds),
            HousingOccupied=_housing.Sum(h=>h.Occupied),
            HomelessResidents=Regional && _settings.EnableHousing ?
                _residents.Count(r=>r.HousingId is null) : 0,
            RentCollected=_rentCollected,TotalEvictions=0,
            TotalRelocations=_relocations,TotalRehousings=_rehousings,
            CommodityDetails=Detailed?CommodityLedger():[],
            RecipesCompleted=_recipeBatches,ToolsWornOut=_toolsWorn,
            FirewoodBurned=_fuelBurned,
            FreightDelayedForGear=_carriageEquipmentDelays,
            IdleProductionHoursAvoided=_idleProductionHoursAvoided,
            MarketWholesaleRevenue=_marketWholesaleRevenue,
            MarketWholesaleCostMilli=_marketWholesaleCostsMilli,
            MarketRetailRevenue=_marketRetailRevenue,
            MarketRetailCostMilli=_marketRetailCostsMilli,
            UnfilledCommodityOrderUnits=_unfilledOrders,
            MarketTrades=_marketTradeCount,
            BusinessPlansMade=_plansMade,
            BusinessPlansRevised=_plansRevised,
            LossMakingPlans=_unprofitablePlans,
            BusinessPlans=Detailed?BusinessPlans().ToArray():[],
            CapitalProjectsStarted=_capitalProjectsStarted,
            CapitalProjectsCompleted=_capitalProjectsCompleted,
            CapitalUpgradesWorn=_capitalProjectsWorn,
            CapitalMaintenanceUnits=_capitalMaintenancePaid,
            CapitalMaterialsConsumed=_capitalMaterialsConsumed,
            ConstructionLabourPayments=_constructionLabourPayments,
            CapitalProjects=Detailed?CapitalProjects():[],
            CapitalDecisions=Detailed?CapitalDecisions():[],
            MarketQuotes=Detailed?_businesses
                .OrderBy(b=>b.Id,StringComparer.Ordinal)
                .SelectMany(b=>b.Stock.OrderBy(s=>s.Key,StringComparer.Ordinal)
                    .Where(s=>CanSellCommodity(b,s.Key) ||
                        IsKitchenBusiness(b) && CommodityRecipes.Edible(s.Key) ||
                        b.Kind==BusinessKinds.GoodsRetail &&
                            CommodityRecipes.HouseholdGoods.Contains(s.Key))
                    .Select(s=>new MarketQuoteSnapshot(b.Id,b.Name,b.Kind,
                        s.Key,s.Value,UnitCostMilli(b,s.Key),
                        CanSellCommodity(b,s.Key)?ToSeptims(MarketAskMilli(b,s.Key)):0m,
                        CommodityRecipes.Edible(s.Key) ||
                            CommodityRecipes.HouseholdGoods.Contains(s.Key) &&
                            b.Kind==BusinessKinds.GoodsRetail ?
                            MarketRetailQuote(b,s.Key):0))).ToArray():[],
            ScarcityPricePremiumGold=_scarcityPremiumGold,
            SurplusPriceDiscountGold=_surplusDiscountGold,
            HomeCookingBatches=_homeCookingBatches,
            HomeCookedMeals=_homeCookedMeals,
            ChefCookedMeals=_chefCookedMeals,
            OneSeptimBreadSold=_oneSeptimBreadSold,
            BreadUnitsSold=_breadSalesUnits,
            BreadSalesRevenue=_breadSalesRevenue,
            DiscountedBreadUnits=_discountedBreadUnits,
            OneSeptimBreadMarkets=Detailed?_foodShops.Count(
                b=>Quantity(b.Stock,"bread")>0 && BreadRetailQuote(b)==1):0,
            BreadMarkets=Detailed?_foodShops.Where(b=>Quantity(b.Stock,"bread")>0)
                .OrderBy(b=>b.Id,StringComparer.Ordinal)
                .Select(b=>new BreadMarketSnapshot(b.Id,b.Name,b.Place.District??"",
                    Quantity(b.Stock,"bread"),BreadRetailQuote(b),
                    BreadBulkDiscount(b),b.BreadLandedUnits,b.BreadLandedGold,
                    b.BreadSalesUnits,b.BreadRevenueGold)).ToArray():[],
            GroceryKitsSold=_groceryKitsSold,
            SourceChefsEmployed=_residents.Count(r=>r.WorkId is not null &&
                IsNamedChef(r)),
            SimulatedGuards=_residents.Count(r=>r.WorkId is not null &&
                r.SimulatedServiceRole=="guard"),
            SimulatedServants=_residents.Count(r=>r.WorkId is not null &&
                r.SimulatedServiceRole=="servant"),
            SimulatedCleaners=_residents.Count(r=>r.WorkId is not null &&
                r.SimulatedServiceRole=="cleaner"),
            ShipmentsSent=_shipmentsSent,ShipmentsDelivered=_shipmentsDelivered,
            ShipmentsInTransit=_freight.Count,FreightUnitsDelivered=_freightUnitsDelivered,
            FoodInTransit=TransitCount("food"),MaterialsInTransit=TransitCount("materials"),
            GoodsInTransit=TransitCount("goods"),FreightFees=_freightFees,
            FreightRoutes=_deliveredByRoute.OrderByDescending(x=>x.Value)
                .ThenBy(x=>x.Key.From,StringComparer.Ordinal)
                .ThenBy(x=>x.Key.To,StringComparer.Ordinal)
                .ThenBy(x=>x.Key.Commodity,StringComparer.Ordinal)
                .Select(x=>new FreightRouteSnapshot(x.Key.From,
                    _placeNames[x.Key.From], x.Key.To,_placeNames[x.Key.To],
                    x.Key.Commodity,x.Value)).ToArray(),
            InitialFood=_initialFood,InitialMaterials=_initialMaterials,
            InitialGoods=_initialGoods,InitialGold=_initialGold,TotalGold=TotalGold(),
            BusinessDetails=_businesses.OrderBy(b=>b.Id,StringComparer.Ordinal)
                .Select(b=>new BusinessSnapshot(b.Name,b.Kind,b.Id,b.OwnerId,
                    b.Operators.Count,b.SimulatedLeaseholderId,b.LeaseEvidence,
                    b.Gold,b.Food,b.Materials,b.Goods,
                    b.RetailSales,b.WorkHours,b.ItemsCrafted,b.WagesPaid,b.DividendsPaid,
                    b.CargoCapacity,b.CargoHandled,
                    Detailed?new SortedDictionary<string,int>(b.Stock,
                        StringComparer.Ordinal):null,
                    b.CapitalLevel,b.CapitalInvestedMilli)).ToArray()
        };
    }

    public void CheckInvariants()
    {
        if (Regional)
        {
            if(checked(TotalGold()+_outsideGold)!=_initialAllGold)
                throw new InvalidOperationException("All gold accounts including foreign trade do not reconcile");
            if(Detailed)
            {
                ValidateCommodityLedger();
                CheckMarketCostInvariants();
            }
            else
            {
            if(TotalFood()+_outsideFood+_meals!=
               _initialFood+_initialOutsideFood+_localFood)
                throw new InvalidOperationException("Regional food accounting mismatch");
            if(TotalMaterials()+TotalGoods()+_outsideMaterials+_outsideGoods+_goodsUsed!=
               _initialMaterials+_initialGoods+_initialOutsideMaterials+_initialOutsideGoods+_localMaterials)
                throw new InvalidOperationException("Regional materials and goods accounting mismatch");
            }
        }
        else if(TotalGold()!=_initialGold)
            throw new InvalidOperationException("Gold appeared or disappeared outside transfers");
        if(!Regional && TotalFood()+_meals!=_initialFood+_foodImported)
            throw new InvalidOperationException("Food ledger did not reconcile");
        if(!Regional && TotalMaterials()+TotalGoods()+_goodsUsed!=_initialMaterials+_initialGoods+_materialsImported)
            throw new InvalidOperationException("Material + finished goods ledger did not reconcile");
        if(_dividends!=_residents.Sum(r=>r.DividendIncome)||
           _dividends!=_businesses.Sum(b=>b.DividendsPaid))
            throw new InvalidOperationException("Leaseholder dividend ledger mismatch");
        if(_wages!=_residents.Sum(r=>r.WageIncome)||
           _wages!=_businesses.Sum(b=>b.WagesPaid))
            throw new InvalidOperationException("Worker wages do not reconcile to business wages");
        if(_casualWages!=_residents.Sum(r=>r.CasualWageIncome) ||
           _casualWages>_wages || _casualHours<0)
            throw new InvalidOperationException("Casual payroll does not reconcile");
        if(_personalSaleIncome!=_residents.Sum(r=>r.PersonalSaleIncome) ||
           _personalCraftBatches!=_residents.Sum(r=>r.PersonalCrafts))
            throw new InvalidOperationException("Personal economy ledger mismatch");
        if(_openResourceSites.Values.Any(s=>s.Ore<0 || s.Logs<0 ||
            s.Fish<0 || s.Game<0))
            throw new InvalidOperationException("Negative independent resources");
        if(_wildForageSites.Any(s=>s.Available<0 ||
            s.Available>s.DailyRegrowth))
            throw new InvalidOperationException("Wild commons exceeded finite regrowth");
        if (Regional && _settings.EnableHousing)
        {
            if (_housing.Any(h => h.Occupied < 0 || h.Occupied > h.Beds) ||
                _housing.Sum(h => h.Occupied) !=
                    _residents.Count(r => r.HousingId is not null))
                throw new InvalidOperationException("Beds cannot be overallocated or duplicated");
            foreach (var home in _housing)
                if (home.Occupied != _residents.Count(r => r.HousingId == home.Id))
                    throw new InvalidOperationException("Occupancy does not match residents");
            if (_rentCollected != _residents.Sum(r => r.RentSpent))
                throw new InvalidOperationException("Housing rent ledger mismatch");
        }
        foreach(var r in _residents)
        {
            if(r.Gold<0||r.Food<0||r.Goods<0||r.Satiation is <0 or >100 ||
               r.Energy is <0 or >100||r.WorkRemainderMinutes is <0 or >=60)
                throw new InvalidOperationException("Invalid resident state: "+r.Source.Id);
        }
        if (_freight.Any(s => s.Value.Quantity <= 0 || s.Value.ArriveMinute <= Minute) ||
            _shipmentsSent != _shipmentsDelivered + _freight.Count)
            throw new InvalidOperationException("Regional freight schedule invalid");
        foreach(var b in _businesses)
        {
            if(b.Gold<0||b.Food<0||b.Materials<0||b.Goods<0||b.MineralReserve<0||b.LandYieldRemaining<0)
                throw new InvalidOperationException("Invalid business account/stock: "+b.Id);
        }
    }
}
