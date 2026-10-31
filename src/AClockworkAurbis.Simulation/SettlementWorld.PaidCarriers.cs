namespace AClockworkAurbis.Simulation;

public sealed partial class SettlementWorld
{
    private decimal _carrierRevenue;
    private long _paidFreightShipments,_paidFreightUnitsDelivered;
    private long _carrierCapacityHours,_unfulfilledFreightRequests;
    private long _lastCarrierDemandDay=-1000;

    // Legacy test settlements without a haulage industry retain their former
    // public common carrier. A mapped haulier economy instead requires real
    // cargo capacity created by paid hours at transport firms or ports.
    private bool UsesPaidCarriers => Detailed && _settings.EnablePaidCarriers &&
        _businesses.Any(b=>b.Kind==BusinessKinds.Haulier);

    private void UpdateCarrierCargoDemandDay()
    {
        if(!UsesPaidCarriers)return;
        foreach(var carrier in _businesses.Where(b=>
            b.Kind is BusinessKinds.Haulier or BusinessKinds.ImportHub))
        {
            carrier.CargoHoursReservedToday=0;
            long delivered=carrier.CargoHandled-carrier.LastCargoHandledAtDay;
            if(delivered<0)throw new InvalidOperationException(
                "Cargo handling history went backwards");
            carrier.LastCargoHandledAtDay=carrier.CargoHandled;
            int handled=checked((int)delivered);
            carrier.RecentCargoHandled.Enqueue(handled);
            carrier.RecentCargoHandledTotal=checked(
                carrier.RecentCargoHandledTotal+handled);
            while(carrier.RecentCargoHandled.Count>7)
                carrier.RecentCargoHandledTotal-=
                    carrier.RecentCargoHandled.Dequeue();
        }
    }

    // Keep a modest multi-day reserve of paid cargo preparation for
    // fluctuating orders, without preparing the entire 500-unit yard cap
    // every day regardless of demand.
    private int PlannedCargoCapacity(BusinessState carrier)
    {
        int max=carrier.Kind==BusinessKinds.Haulier?
            _settings.MaxHaulierCargoCapacity:_settings.MaxPortCargoCapacity;
        if(!UsesPaidCarriers)return max;
        int hourly=carrier.Kind==BusinessKinds.Haulier?
            _settings.HaulierCargoUnitsPerWorkHour:
            _settings.PortCargoUnitsPerWorkHour;
        int recent=carrier.RecentCargoHandled.Count;
        long perDay=recent==0?0:
            (carrier.RecentCargoHandledTotal+recent-1L)/recent;
        return (int)Math.Min(max,Math.Max((long)hourly*2,perDay*3));
    }

    private bool CanScheduleCargoWork(BusinessState carrier)
    {
        if(carrier.Kind==BusinessKinds.Haulier && !RecentCarrierDemand())
            return false;
        int hourly=carrier.Kind==BusinessKinds.Haulier?
            _settings.HaulierCargoUnitsPerWorkHour:
            _settings.PortCargoUnitsPerWorkHour;
        return hourly>0 &&
            (long)carrier.CargoCapacity+
            (long)carrier.CargoHoursReservedToday*hourly<
            PlannedCargoCapacity(carrier);
    }

    private bool RecentCarrierDemand() => UsesPaidCarriers &&
        Minute/DayMinutes<=_lastCarrierDemandDay+5;

    private void CarrierShortage(int requestedUnits)
    {
        if(requestedUnits<=0)return;
        _lastCarrierDemandDay=Minute/DayMinutes;
        _unfulfilledFreightRequests++;
    }

    private BusinessState? ChoosePaidCarrier(BusinessState seller,BusinessState buyer)
    {
        int routeMinutes=PlaceTravelMinutes(seller.Place,buyer.Place);
        return _businesses
            .Where(b=>(b.Kind is BusinessKinds.Haulier or BusinessKinds.ImportHub) &&
                b.CargoCapacity>0)
            // A port terminal has fixed handling overhead, but land
            // carriers incur a larger dispatch burden on long routes.
            // This is a geographic service-selection assumption, not a
            // claim that a particular ESO path uses ships or roads.
            .OrderBy(b=>(long)PlaceTravelMinutes(b.Place,seller.Place)+
                PlaceTravelMinutes(b.Place,buyer.Place)+
                (b.Kind==BusinessKinds.ImportHub?50:
                    Math.Max(0,routeMinutes-30)))
            .ThenBy(b=>b.Kind==BusinessKinds.Haulier?0:1)
            .ThenBy(b=>b.Id,StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private void SettleFreightFee(decimal fee,int units,BusinessState? carrier)
    {
        if(carrier is null)
            _publicGold+=fee;
        else
        {
            if(carrier.CargoCapacity<units)
                throw new InvalidOperationException("Carrier lacks paid handling capacity");
            carrier.CargoCapacity-=units;
            carrier.Gold+=fee;
            carrier.FreightRevenue+=fee;
            carrier.FreightShipments++;
            // Observed shipments, not just failed orders, signal work to
            // independent shift workers and permanent haulage staff.
            _lastCarrierDemandDay=Minute/DayMinutes;
            _carrierRevenue+=fee;
            _paidFreightShipments++;
        }
        _freightFees+=fee;
    }
}
