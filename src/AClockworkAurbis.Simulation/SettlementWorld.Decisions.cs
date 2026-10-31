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

        if(actor.Satiation<53)
        {
            if(actor.Food>0)
            {
                Enqueue(index,12,SettlementAction.Eat);
                Observe(actor,"eat",actor.Source.Name+" is hungry");
                return;
            }
            if(hour>=7&&hour<21&&actor.Gold>=_settings.MealPrice)
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

        if (Regional && actor.Food==0 && actor.Gold<_settings.MealPrice &&
            actor.Satiation<72 && ChooseSubsistence(index,actor))return;

        if(actor.WorkId is not null &&
           localMinute>=actor.WorkStart*60&&
           localMinute<(actor.WorkStart+8)*60 &&
           actor.Energy>=18)
        {
            if(actor.CurrentPlaceId!=actor.WorkId)
                Travel(index,actor,actor.WorkId);
            else
            {
                int untilShiftEnds=(actor.WorkStart+8)*60-localMinute;
                Enqueue(index,Math.Min(60,untilShiftEnds),SettlementAction.Work);
                Observe(actor,"work",DisplayPlace(actor.WorkId));
            }
            return;
        }

        // Household tool/general-goods purchases are deliberately modest.
        // We do not assert which specific TES items residents need yet.
        if(actor.Goods==0 && actor.Gold>=_settings.GoodsPrice+_settings.MealPrice &&
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
        long lowest=long.MaxValue;
        foreach(var shop in food?_foodShops:_goodsShops)
        {
            if(food)
            {
                if(shop.Kind!=BusinessKinds.FoodRetail||
                    (Detailed?!HasPreparedMeal(shop):shop.Food<1))continue;
            }
            else if(shop.Kind is not (BusinessKinds.GoodsRetail or BusinessKinds.Workshop)||
                    (Detailed?!HasHouseholdSupply(shop):shop.Goods<1))continue;
            // Source-backed map proximity outranks an arbitrary source-ID hash.
            // Stable small jitter breaks ties without drawing everyone into
            // one vendor on the same city-centre marker.
            ulong hash=StableRandom.Hash(actor.Source.Id,_settings.Seed)^
                StableRandom.Hash(shop.Id,unchecked(_settings.Seed+41));
            if(shop.Id==actor.CurrentPlaceId)return shop;
            long cost=JourneyDuration(actor,shop.Id)+(long)(hash%5uL);
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
