namespace AClockworkAurbis.Simulation;

public sealed partial class SettlementWorld
{
    private static bool PublicEmployer(BusinessState firm) =>
        firm.Kind is BusinessKinds.Institution or BusinessKinds.GuardPost
            or BusinessKinds.CleaningService or BusinessKinds.DomesticService;

    // Proportional funding into real departmental accounts, once per budget
    // period. A share of treasury cash stays available for household aid.
    // Zero-staff institutions may reserve the first position and reopen.
    private void FundBudgetedPublicServices()
    {
        var services=_businesses.Where(PublicEmployer)
            .OrderBy(b=>b.Id,StringComparer.Ordinal).ToArray();
        int days=_settings.PublicPayrollHorizonDays;
        long day=Minute/DayMinutes;
        if(day%days==0 && _settings.HourlyWage>0)
        {
            var needs=services.Select(firm=>
            {
                int slots=Math.Max(1,firm.WorkerCount);
                decimal target=checked((decimal)slots*
                    _settings.PublicTargetPaidHoursPerWorker*
                    _settings.HourlyWage*days);
                return (Firm:firm,Need:Math.Max(0m,target-firm.Gold));
            }).ToArray();
            decimal total=needs.Sum(x=>x.Need);
            decimal reserve=Math.Min(_publicGold/4m,_residents.Count);
            decimal pool=Math.Max(0m,Math.Min(total,_publicGold-reserve));
            if(total>0m && pool>0m)
            {
                // Round down rather than overdraw; largest fractional
                // remainder receives pennies in rotating, stable order.
                var grants=needs.Select(n=>Math.Floor(pool*n.Need/total*100m)/100m)
                    .ToArray();
                decimal leftover=pool-grants.Sum();
                int offset=(int)((day/days)%services.Length);
                for(int i=0;leftover>=0.01m && i<services.Length;i++)
                {
                    int index=(i+offset)%services.Length;
                    if(grants[index]+0.01m>needs[index].Need)continue;
                    grants[index]+=0.01m;
                    leftover-=0.01m;
                }
                for(int i=0;i<services.Length;i++)
                {
                    decimal grant=grants[i];
                    if(grant==0)continue;
                    _publicGold-=grant;
                    services[i].Gold+=grant;
                    services[i].PublicPayrollGrantsReceived+=grant;
                    _publicPayrollGrants+=grant;
                }
            }
        }
        foreach(var firm in services)
        {
            firm.PublicHoursWorkedToday=0;
            firm.PublicHoursReservedToday=0;
            int count=firm.WorkerCount;
            if(count==0)
            {
                firm.PublicHoursBudgetToday=0;
                firm.ConsecutiveUnfundedPublicDays=0;
                continue;
            }
            int minimum=checked(count*_settings.PublicMinimumPaidHoursPerWorker);
            int desired=checked(count*_settings.PublicTargetPaidHoursPerWorker);
            int affordable=_settings.HourlyWage==0?desired:
                (int)Math.Min(int.MaxValue,
                    decimal.Floor(firm.Gold/_settings.HourlyWage));
            // Extra hours are discretionary; preserve a fortnight of
            // minimum payroll before scheduling them.
            decimal cushion=checked((decimal)minimum*_settings.HourlyWage*
                _settings.PublicPayrollHorizonDays);
            int planned=firm.Gold>=cushion+
                (decimal)desired*_settings.HourlyWage?desired:minimum;
            firm.PublicHoursBudgetToday=Math.Max(0,
                Math.Min(planned,affordable));
            // A permanent post must have a minimum paid shift. A single
            // funded hour for ten employees is not ten genuine jobs.
            firm.ConsecutiveUnfundedPublicDays=
                firm.PublicHoursBudgetToday>=minimum?0:
                firm.ConsecutiveUnfundedPublicDays+1;

            if(firm.ConsecutiveUnfundedPublicDays<
               _settings.PublicUnfundedLeaveDays)continue;
            int retainable=_settings.PublicMinimumPaidHoursPerWorker==0?
                count:firm.PublicHoursBudgetToday/
                    _settings.PublicMinimumPaidHoursPerWorker;
            retainable=Math.Clamp(retainable,0,count);
            // Preserve source-associated senior positions first. Unfunded
            // posts move to unpaid leave; no gold or source job is invented.
            foreach(var worker in _staff[firm.Id].Skip(retainable).ToArray())
                FurloughForInsolvency(worker,firm);
            firm.ConsecutiveUnfundedPublicDays=0;
            firm.PublicHoursBudgetToday=Math.Min(firm.PublicHoursBudgetToday,
                retainable*_settings.PublicTargetPaidHoursPerWorker);
        }
    }

    private void ObservePrivateMarginDay()
    {
        int span=_settings.PrivateHireRunwayDays;
        foreach(var firm in _businesses.Where(b=>!PublicEmployer(b)))
        {
            // Margin after the recorded inventory acquisition/production
            // cost. Gross turnover does not establish ability to add staff.
            decimal total=firm.RetailRevenue+firm.WholesaleRevenue+
                firm.FreightRevenue-
                (firm.RetailCostOfGoodsMilli+firm.WholesaleCostOfGoodsMilli)/1000m;
            decimal increment=total-firm.LastObservedSalesMargin;
            firm.LastObservedSalesMargin=total;
            firm.PrivateRecentMargin.Enqueue(increment);
            firm.PrivateRecentMarginTotal+=increment;
            while(firm.PrivateRecentMargin.Count>span)
                firm.PrivateRecentMarginTotal-=firm.PrivateRecentMargin.Dequeue();
        }
    }

    // A diagnostic query for the simulation UI and hiring tests. The caller
    // still has to offer the contract; this never creates a job itself.
    public bool CanOfferPermanentContract(string businessId)
    {
        if(!Regional || !_byBusiness.TryGetValue(businessId,out var firm) ||
           _staff[firm.Id].Count>=ContractCapacity(firm))return false;
        return Detailed && _settings.EnableSustainableHiring
            ?CanSustainHire(firm)
            :firm.Gold>=Math.Max(12,_settings.HourlyWage*8);
    }

    private bool CanSustainHire(BusinessState firm)
    {
        int count=_staff[firm.Id].Count;
        int wage=_settings.HourlyWage;
        if(PublicEmployer(firm))
        {
            int slots=count+1;
            decimal pledged=checked((decimal)slots*
                _settings.PublicTargetPaidHoursPerWorker*wage*
                _settings.PublicPayrollHorizonDays);
            return firm.Gold>=pledged;
        }
        if(Minute/DayMinutes-firm.LastFurloughDay<
           _settings.PrivateRehireCooldownDays)return false;
        int guaranteed=Math.Max(1,Math.Min(8,
            _settings.RegularGuaranteedHoursPerDay));
        decimal runway=checked((decimal)(count+1)*guaranteed*wage*
            _settings.PrivateHireRunwayDays);
        decimal operatingBuffer=Math.Min(100m,
            _settings.StartingBusinessGold/12m);
        if(firm.Gold<runway+operatingBuffer)return false;
        int observedDays=firm.PrivateRecentMargin.Count;
        if(observedDays<Math.Min(7,_settings.PrivateHireRunwayDays))
            return false;
        // Require demonstrated profit margin supporting a new minimum
        // shift; retail costs exclude staff, while manufactured COGS already
        // includes wages paid during production.
        int workforce=firm.Kind is BusinessKinds.FoodRetail or
            BusinessKinds.GoodsRetail or BusinessKinds.ImportHub or
            BusinessKinds.Haulier?count+1:1;
        decimal requiredMargin=checked((decimal)observedDays*
            workforce*guaranteed*wage);
        return firm.PrivateRecentMarginTotal>=requiredMargin;
    }

    private bool CanFundPublicShift(ResidentState worker,BusinessState firm)
    {
        if(worker.RegularHoursToday>=_settings.PublicTargetPaidHoursPerWorker ||
            firm.PublicHoursWorkedToday+firm.PublicHoursReservedToday>=
                firm.PublicHoursBudgetToday)return false;
        int minimum=_settings.PublicMinimumPaidHoursPerWorker;
        if(worker.RegularHoursToday>=minimum &&
           _staff[firm.Id].Any(other=>!ReferenceEquals(other,worker) &&
               other.WorkId==firm.Id && other.RegularHoursToday<minimum &&
               Minute%DayMinutes<(other.WorkStart+8)*60))return false;
        return true;
    }
}
