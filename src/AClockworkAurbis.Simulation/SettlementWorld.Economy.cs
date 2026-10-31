namespace AClockworkAurbis.Simulation;

public sealed partial class SettlementWorld
{
    private void CompleteWork(ResidentState actor)
    {
        if(actor.WorkId is null||!_byBusiness.TryGetValue(actor.WorkId,out var business))
            return;
        int time=checked((int)(Minute-actor.ActionStart));
        actor.WorkRemainderMinutes+=time;
        int hours=actor.WorkRemainderMinutes/60;
        actor.WorkRemainderMinutes%=60;
        if(hours<=0)return;
        if(Detailed && business.Kind is BusinessKinds.FoodRetail or
            BusinessKinds.GoodsRetail)
        {
            // Several workers can start a shift together. Respect the
            // shared shop payroll budget when each finishes, otherwise
            // simultaneous shifts overshoot the amount sales can fund.
            int available=Math.Max(0,
                business.ServiceHoursBudget-business.ServiceHoursWorkedToday);
            int wasted=Math.Max(0,hours-available);
            hours=Math.Min(hours,available);
            _idleProductionHoursAvoided+=wasted;
            if(hours==0)return;
            business.ServiceHoursWorkedToday+=hours;
        }
        business.WorkHours+=hours;
        long payment=checked((long)hours*_settings.HourlyWage);
        bool paid = business.Gold>=payment;
        if(paid)
        {
            business.Gold-=payment;
            business.WagesPaid+=payment;
            actor.Gold=checked(actor.Gold+payment);
            actor.WageIncome+=payment;
            _wages+=payment;
            Observe(actor,"wages",$"{business.Name}: {payment} gold paid");
        }
        else
        {
            actor.UnpaidHours+=hours;
            _unpaid+=hours;
            Observe(actor,"unpaid",$"{business.Name} could not fund {hours} hour(s)");
            if(Regional)FurloughForInsolvency(actor,business);
        }
        if(CompleteRegionalWork(actor,business,paid?hours:0))return;
        // Craft workshops actually convert one raw-material unit into one
        // finished goods unit per completed productive hour.
        if(business.Kind==BusinessKinds.Workshop)
        {
            int made=Math.Min(hours,business.Materials);
            business.Materials-=made;
            business.Goods+=made;
            business.ItemsCrafted+=made;
            _crafted+=made;
            if(made>0)Observe(actor,"crafted",$"{made} goods at {business.Name} from {made} materials");
        }
    }

    private void CompletePurchase(ResidentState actor,bool food)
    {
        if(!_byBusiness.TryGetValue(actor.CurrentPlaceId,out var business))return;
        int cost=food?_settings.MealPrice:_settings.GoodsPrice;
        int hour=(int)(Minute%DayMinutes)/60;
        if(hour is <7 or >=22)return;
        if(Detailed){CompleteCommodityPurchase(actor,food);return;}
        if(actor.Gold<cost)return;
        if(food)
        {
            if(business.Kind!=BusinessKinds.FoodRetail||business.Food<=0)return;
            business.Food--;
            actor.Food++;
            _foodSales++;
        }
        else
        {
            if(business.Kind is not (BusinessKinds.GoodsRetail or BusinessKinds.Workshop)||
                business.Goods<=0)return;
            business.Goods--;
            actor.Goods++;
            actor.LastGoodsShoppingDay=Minute/DayMinutes;
            _goodsSales++;
        }
        actor.Gold-=cost;
        int tax = Regional ? (food?_settings.FoodSalesTax:_settings.GoodsSalesTax) : 0;
        business.Gold+=cost-tax;
        _publicGold+=tax;
        _taxRevenue+=tax;
        business.RetailRevenue+=cost-tax;
        business.RetailSales++;
        actor.Purchases++;
        _purchases++;
        Observe(actor,"purchase",$"{(food?"food":"goods")} at {business.Name} for {cost} gold");
    }

    private void DeliverImportsAndRestock()
    {
        if(Regional){RunRegionalDay();return;}
        if(_residents.Count==0)return;
        // Share a small portion of yesterday’s operating surplus with a
        // simulated leaseholder. The legal owner remains unknown in the
        // source record unless explicitly documented.
        if(Minute>=DayMinutes)
        {
            foreach(var business in _businesses)
            {
                if(business.SimulatedLeaseholderId is null||
                    business.Gold<=_settings.StartingBusinessGold)continue;
                if(!_residentById.TryGetValue(business.SimulatedLeaseholderId,
                    out var leaseholder))continue;
                decimal excess=business.Gold-_settings.StartingBusinessGold;
                decimal dividend=Math.Min(100,Math.Round(excess/10,2));
                if(dividend<=0)continue;
                business.Gold-=dividend;
                business.DividendsPaid+=dividend;
                leaseholder.Gold=checked(leaseholder.Gold+dividend);
                leaseholder.DividendIncome+=dividend;
                _dividends+=dividend;
                Observe(leaseholder,"dividend",$"{business.Name}: {dividend} gold as simulated leaseholder");
            }
        }
        // One generic household supply unit is used for maintenance every
        // five days. Consumption is a named sink in the item ledger.
        if(Minute>DayMinutes && Minute/DayMinutes%5==0)
        {
            foreach(var resident in _residents)
            {
                if(resident.Goods<1)continue;
                resident.Goods--;
                _goodsUsed++;
                Observe(resident,"upkeep","Used 1 household goods unit");
            }
        }
        // Deliberately exogenous imports. Source docks establish a place, not
        // an ESO economy supply quantity. Units are NOT disguised as farm output.
        var importers=_businesses.Where(b=>b.Kind==BusinessKinds.ImportHub)
            .OrderBy(b=>b.Id,StringComparer.Ordinal).ToArray();
        int newFood=checked(_residents.Count*_settings.FoodImportPerResident);
        int newMaterials=checked((_residents.Count+9)/10*
            _settings.MaterialImportPerTenResidents);
        for(int i=0;i<importers.Length;i++)
        {
            importers[i].Food+=newFood/importers.Length+(i<newFood%importers.Length?1:0);
            importers[i].Materials+=newMaterials/importers.Length+
                (i<newMaterials%importers.Length?1:0);
        }
        _foodImported+=newFood;
        _materialsImported+=newMaterials;

        var foodShops=_businesses.Where(b=>b.Kind==BusinessKinds.FoodRetail).ToArray();
        int foodTarget=foodShops.Length==0?0:
            Math.Max(4,(_residents.Count*2+foodShops.Length-1)/foodShops.Length);
        foreach(var shop in foodShops)
            Wholesale(shop,importers,foodTarget,BusinessKinds.FoodRetail);

        var workshops=_businesses.Where(b=>b.Kind==BusinessKinds.Workshop).ToArray();
        int materialTarget=workshops.Length==0?0:
            Math.Max(4,(_residents.Count/2+workshops.Length-1)/workshops.Length);
        foreach(var workshop in workshops)
            Wholesale(workshop,importers,materialTarget,BusinessKinds.Workshop);

        var retailers=_businesses.Where(b=>b.Kind==BusinessKinds.GoodsRetail).ToArray();
        int goodsTarget=retailers.Length==0?0:
            Math.Max(3,(_residents.Count/4+retailers.Length-1)/retailers.Length);
        foreach(var shop in retailers)
            Wholesale(shop,workshops,goodsTarget,BusinessKinds.GoodsRetail);
    }

    private void Wholesale(BusinessState buyer,IReadOnlyList<BusinessState> providers,
        int target,string requestedKind)
    {
        int have=requestedKind switch
        {
            BusinessKinds.FoodRetail=>buyer.Food,
            BusinessKinds.Workshop=>buyer.Materials,
            _=>buyer.Goods
        };
        int price=requestedKind switch
        {
            BusinessKinds.FoodRetail=>_settings.FoodWholesalePrice,
            BusinessKinds.Workshop=>_settings.MaterialWholesalePrice,
            _=>_settings.GoodsWholesalePrice
        };
        int needed=Math.Max(0,target-have);
        if(needed==0)return;
        foreach(var seller in providers)
        {
            if(seller.Id==buyer.Id)continue;
            int available=requestedKind switch
            {
                BusinessKinds.FoodRetail=>seller.Food,
                BusinessKinds.Workshop=>seller.Materials,
                _=>seller.Goods
            };
            int affordable=price==0?needed:
                (int)Math.Min(int.MaxValue,buyer.Gold/price);
            int traded=Math.Min(needed,Math.Min(available,affordable));
            if(traded<=0)continue;
            long amount=checked((long)traded*price);
            buyer.Gold-=amount;
            seller.Gold+=amount;
            buyer.WholesaleSpending+=amount;
            seller.WholesaleRevenue+=amount;
            if(requestedKind==BusinessKinds.FoodRetail)
            {
                buyer.Food+=traded;
                seller.Food-=traded;
            }
            else if(requestedKind==BusinessKinds.Workshop)
            {
                buyer.Materials+=traded;
                seller.Materials-=traded;
            }
            else
            {
                buyer.Goods+=traded;
                seller.Goods-=traded;
            }
            needed-=traded;
            if(needed<=0)break;
        }
    }
}
