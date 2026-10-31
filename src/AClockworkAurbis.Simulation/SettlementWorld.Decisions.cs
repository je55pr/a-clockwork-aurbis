namespace AClockworkAurbis.Simulation;

public sealed partial class SettlementWorld
{
    private void Decide(int index,ResidentState actor)
    {
        int localMinute=(int)(Minute%DayMinutes);
        int hour=localMinute/60;
        bool night=hour>=22||hour<6;
        if(night)
        {
            if(actor.CurrentPlaceId!=actor.HomeId)
                Travel(index,actor,actor.HomeId);
            else
            {
                int untilMorning=(6*60-localMinute+DayMinutes)%DayMinutes;
                Enqueue(index,Math.Max(1,untilMorning),SettlementAction.Sleep);
                Observe(actor,actor.HousingId is null && Regional && _settings.EnableHousing ?
                    "rough_sleep" : "sleep",DisplayPlace(actor.HomeId));
            }
            return;
        }

        if(Detailed && actor.Food==0 && actor.Satiation<75 &&
            CanCookAtHome(actor))
        {
            if(actor.CurrentPlaceId!=actor.HomeId)
                Travel(index,actor,actor.HomeId);
            else Enqueue(index,45,SettlementAction.CookAtHome);
            return;
        }

        if(actor.Satiation<53)
        {
            if(actor.Food>0)
            {
                Enqueue(index,12,SettlementAction.Eat);
                Observe(actor,"eat",actor.Source.Name+" is hungry");
                return;
            }
            if(Detailed && actor.HousingId is not null &&
                hour>=7 && hour<20)
            {
                var grocery=ChooseGroceryShop(actor);
                if(grocery is not null)
                {
                    if(actor.CurrentPlaceId!=grocery.Id)
                        Travel(index,actor,grocery.Id);
                    else Enqueue(index,8,SettlementAction.BuyGroceries);
                    return;
                }
            }
            if(hour>=7&&hour<21&&actor.Gold>=EverydayMealCost)
            {
                var food=ChooseShop(actor,food:true);
                if(food is not null)
                {
                    if(actor.CurrentPlaceId!=food.Id)Travel(index,actor,food.Id);
                    else Enqueue(index,4,SettlementAction.BuyFood);
                    return;
                }
            }
        }

        // Natural subsistence is the fallback when a hungry resident has no
        // food they can buy. Don't preempt ordinary shopping at mild hunger.
        if (Regional && actor.Food==0 &&
            actor.Satiation<53 && ChooseSubsistence(index,actor))return;

        // Explicitly requested work for personal ownership takes precedence
        // over employment; the request is still subject to travel and time.
        if(Detailed && SchedulePersonalTask(index,actor))return;

        if(actor.WorkId is not null &&
           localMinute>=actor.WorkStart*60&&
           localMinute<(actor.WorkStart+8)*60 &&
           actor.Energy>=18)
        {
            if(Detailed && _byBusiness.TryGetValue(actor.WorkId,out var employer))
            {
                if(employer.Gold<_settings.HourlyWage)
                {
                    FurloughForInsolvency(actor,employer);
                    Enqueue(index,60,SettlementAction.Idle);
                    return;
                }
                if(!CanScheduleCommodityWork(actor,employer))
                {
                    _idleProductionHoursAvoided++;
                    Enqueue(index,60,SettlementAction.Idle);
                    return;
                }
            }
            if(actor.CurrentPlaceId!=actor.WorkId)
                Travel(index,actor,actor.WorkId);
            else
            {
                int untilShiftEnds=(actor.WorkStart+8)*60-localMinute;
                if(Detailed && _byBusiness.TryGetValue(actor.WorkId,out var shop) &&
                   shop.Kind is BusinessKinds.FoodRetail or BusinessKinds.GoodsRetail)
                    shop.ServiceHoursReservedToday++;
                Enqueue(index,Math.Min(60,untilShiftEnds),SettlementAction.Work);
                Observe(actor,"work",DisplayPlace(actor.WorkId));
            }
            return;
        }

        // Casual paid extraction is available without a permanent contract.
        // Full-time shifts above always take priority. NPCs seek extra work
        // when low on cash; the site still needs resources and payroll.
        if(Detailed && hour>=10 && hour<18 && actor.Gold<12 &&
           actor.Energy>=20 && TryCasualShift(index,actor))return;

        // Household tool/general-goods purchases are deliberately modest.
        // We do not assert which specific TES items residents need yet.
        if((Detailed?WantsHouseholdSupply(actor):actor.Goods==0) &&
           actor.Gold>=(Detailed ? EverydayMealCost+
               (actor.HousingId is not null ? _settings.DailySharedRent*3 : 0) :
               _settings.GoodsPrice) &&
           Minute/DayMinutes>actor.LastGoodsShoppingDay+2 &&
           hour>=9&&hour<20 && actor.Satiation>=37)
        {
            var goods=ChooseShop(actor,food:false);
            if(goods is not null)
            {
                if(actor.CurrentPlaceId!=goods.Id)Travel(index,actor,goods.Id);
                else Enqueue(index,4,SettlementAction.BuyGoods);
                return;
            }
        }

        if(actor.CurrentPlaceId!=actor.HomeId &&
           (hour>=18||actor.Energy<28||actor.WorkId is null))
        {
            Travel(index,actor,actor.HomeId);
            return;
        }
        if(actor.Energy<35 && actor.CurrentPlaceId==actor.HomeId)
        {
            Enqueue(index,90,SettlementAction.Sleep);
            Observe(actor,"rest",DisplayPlace(actor.HomeId));
        }
        else Enqueue(index,45,SettlementAction.Idle);
    }

    private BusinessState? ChooseShop(ResidentState actor,bool food)
    {
        BusinessState? best=null;
        decimal lowest=decimal.MaxValue;
        // Chef staffing changes over time; don't cache which shops can
        // sell already-cooked meals after a cook leaves or joins.
        IEnumerable<BusinessState> outlets=food && Detailed
            ? _businesses.Where(IsKitchenBusiness)
            : food?_foodShops:_goodsShops;
        foreach(var shop in outlets)
        {
            if(food)
            {
                if(shop.Kind is not (BusinessKinds.FoodRetail or BusinessKinds.Bakery) &&
                    !(Detailed && IsKitchenBusiness(shop)) ||
                    (Detailed?!HasPreparedMeal(shop):shop.Food<1) ||
                    Detailed && !CommodityRecipes.MealRetailPrices.Any(
                        m=>Quantity(shop.Stock,m.Key)>0 &&
                            MealRetailQuote(shop,m.Key)<=actor.Gold))
                    continue;
            }
            else if(shop.Kind is not (BusinessKinds.GoodsRetail or BusinessKinds.Workshop
                    or BusinessKinds.Sawmill or BusinessKinds.Leatherworker) ||
                    (Detailed?NeededSupplyAtShop(actor,shop) is not string sku ||
                        HouseholdGoodsQuote(shop,sku)>actor.Gold:
                        shop.Goods<1))continue;
            // Source-backed map proximity outranks an arbitrary source-ID hash.
            // Stable small jitter breaks ties without drawing everyone into
            // one vendor on the same city-centre marker.
            ulong hash=StableRandom.Hash(actor.Source.Id,_settings.Seed)^
                StableRandom.Hash(shop.Id,unchecked(_settings.Seed+41));
            if(shop.Id==actor.CurrentPlaceId)return shop;
            long trip=JourneyDuration(actor,shop.Id);
            decimal price=Detailed
                ? food?CommodityRecipes.Meals.Where(s=>Quantity(shop.Stock,s)>0 &&
                        MealRetailQuote(shop,s)<=actor.Gold)
                        .Select(s=>MealRetailQuote(shop,s)).DefaultIfEmpty(9999).Min()
                    : NeededSupplyAtShop(actor,shop) is string goodsSku ?
                        HouseholdGoodsQuote(shop,goodsSku):9999
                : 0;
            // A cost of one septim is worth about six minutes of walking
            // to most customers; urgent food still favours local supply.
            decimal cost=trip+price*6m+(long)(hash%5uL);
            if(cost<lowest){lowest=cost;best=shop;}
        }
        return best;
    }

    private void Travel(int index,ResidentState actor,string destination)
    {
        actor.DestinationPlaceId=destination;
        ulong hash=StableRandom.Hash(actor.Source.Id,_settings.Seed)^
            StableRandom.Hash(destination,unchecked(_settings.Seed+71));
        int variation=(int)(hash%7uL);
        Enqueue(index,JourneyDuration(actor,destination)+variation,SettlementAction.Travel);
        Observe(actor,"travel",$"{DisplayPlace(actor.CurrentPlaceId)} -> {DisplayPlace(destination)}");
    }

    // Used by headless diagnostics, not a pathfinder or literal travel ETA.
    public int EstimateTravelMinutes(string residentId,string destinationId)
    {
        if(!_residentById.TryGetValue(residentId,out var resident))
            throw new KeyNotFoundException("Unknown resident ID "+residentId);
        if(!_sourcePlaces.ContainsKey(destinationId))
            throw new KeyNotFoundException("Unknown destination ID "+destinationId);
        return JourneyDuration(resident,destinationId);
    }

    private PlaceDefinition? Anchor(ResidentState actor,string placeId)
    {
        // A source appearance is an approximate neighbourhood anchor for a
        // simulated home, never evidence of an actual dwelling.
        if(placeId==actor.HomeId && actor.Source.HomePlaceId is null)
            placeId=actor.Source.AnchorPlaceId??placeId;
        return _sourcePlaces.GetValueOrDefault(placeId);
    }

    private int JourneyDuration(ResidentState actor,string destination)
    {
        var from=Anchor(actor,actor.CurrentPlaceId);
        var to=Anchor(actor,destination);
        if(from?.MapX is double ax && from.MapY is double ay &&
           to?.MapX is double bx && to.MapY is double by)
        {
            double distance=Math.Sqrt((ax-bx)*(ax-bx)+(ay-by)*(ay-by));
            // Sub-buildings often inherit the same town-centre marker. Treat
            // distinct places in one district as a short local journey, not
            // an 18-minute cross-settlement trip.
            bool local = from.District is string fromDistrict && to.District is string toDistrict &&
                string.Equals(fromDistrict, toDistrict, StringComparison.OrdinalIgnoreCase);
            int baseMinutes = local ? _settings.LocalTravelMinutes : _settings.BaseTravelMinutes;
            return checked(baseMinutes +
                (int)Math.Ceiling(distance/_settings.MapUnitsPerTravelMinute));
        }
        if(from?.District is string a && to?.District is string b)
        {
            if(string.Equals(a,b,StringComparison.OrdinalIgnoreCase))
                return _settings.LocalTravelMinutes;
            return _settings.BaseTravelMinutes+_settings.UnknownMapInterdistrictMinutes;
        }
        // Without a coordinate or shared known district, assume nonlocal
        // travel rather than treating the unmapped shop as the closest.
        if(from?.MapX is not null || to?.MapX is not null)
            return _settings.BaseTravelMinutes+_settings.UnknownMapInterdistrictMinutes;
        return _settings.BaseTravelMinutes;
    }

    private string DisplayPlace(string placeId)
    {
        if(_byBusiness.TryGetValue(placeId,out var business))return business.Name;
        if(_placeNames.TryGetValue(placeId,out var placeName))return placeName;
        return placeId.StartsWith("sim:unmapped-home:",StringComparison.Ordinal)
            ? "Unmapped home (simulation default)"
            : placeId;
    }
}
