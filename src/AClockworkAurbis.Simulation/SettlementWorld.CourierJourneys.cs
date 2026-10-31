namespace AClockworkAurbis.Simulation;

public sealed partial class SettlementWorld
{
    private decimal _courierEscrowGold,_courierWages;
    private long _courierAssignments,_courierDeliveries;
    private readonly Queue<CourierJourneySnapshot> _recentCourierJourneys = new();
    private const int CourierHistoryLimit = 96;

    private bool UsesNpcCourierJourneys =>
        UsesPaidCarriers && _settings.EnableNpcCourierJourneys;

    private void CourierStaffShortage()
    {
        // No invented delivery worker or untracked goods movement.
        _unfulfilledFreightRequests++;
    }

    private static bool CourierEligible(ResidentState p)
    {
        // Avoid assigning implausible roles to daedra, skeletons or rulers
        // solely because UESP doesn't list their profession.
        if(p.Source.Race is not (
            "High Elf" or "Wood Elf" or "Dark Elf" or
            "Altmer" or "Bosmer" or "Dunmer" or
            "Khajiit" or "Argonian" or "Orc" or "Orsimer" or
            "Breton" or "Nord" or "Imperial" or "Redguard" or "Maormer"))return false;
        if(p.WorkId is null)
        {
            if(p.Source.Profession is not null)return false;
            string name=p.Source.Name;
            if(name.StartsWith("High Kinlord ",StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Captain ",StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Queen ",StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("King ",StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Prince ",StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Princess ",StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private ResidentState? ChooseCourierFor(BusinessState carrier,BusinessState seller)
    {
        if(!UsesNpcCourierJourneys)return null;
        return _residents.Where(p=>p.CourierShipmentId is null &&
                (p.WorkId is null || p.WorkId==carrier.Id) &&
                CourierEligible(p) &&
                p.Satiation>=58 && p.Energy>=38 && p.Food>0 &&
                !_personalTasks.ContainsKey(p.Source.Id))
            .Select(p=>(Person:p,Trip:JourneyDuration(p,seller.Id)))
            .Where(x=>x.Trip<=160)
            // Employees get first refusal if they are reasonably close to
            // the supplier. Otherwise a local resident can take a paid gig.
            .OrderBy(x=>x.Trip+
                (x.Person.WorkId==carrier.Id?-24:0)+
                (int)Math.Min(50,x.Person.CourierDeliveries*2))
            .ThenBy(x=>StableRandom.Hash(
                x.Person.Source.Id+"|courier|"+_nextFreightId,_settings.Seed))
            .Select(x=>x.Person)
            .FirstOrDefault();
    }

    private bool TryAdvanceCourier(int index,ResidentState actor)
    {
        if(actor.CourierShipmentId is not int id)return false;
        if(!_freight.TryGetValue(id,out var shipment) ||
           shipment.CourierId!=actor.Source.Id)
            throw new InvalidOperationException("Courier assignment lost its shipment");

        if(shipment.PickupMinute is null)
        {
            if(actor.Satiation<38 && actor.Food>0)
            {
                Enqueue(index,12,SettlementAction.Eat);
                return true;
            }
            if(actor.CurrentPlaceId!=shipment.From.Id)
            {
                Travel(index,actor,shipment.From.Id);
                return true;
            }
            long trip=JourneyDuration(actor,shipment.To.Id)+7L;
            shipment=shipment with
            {
                PickupMinute=Minute,
                ExpectedDeliveryMinute=checked(Minute+trip)
            };
            _freight[id]=shipment;
            Observe(actor,"courier_pickup",shipment.Quantity+" "+
                shipment.Commodity+" from "+shipment.From.Name);
        }
        if(actor.CurrentPlaceId!=shipment.To.Id)
        {
            Travel(index,actor,shipment.To.Id);
            Observe(actor,"courier_carry",shipment.Commodity+" x"+
                shipment.Quantity+" to "+shipment.To.Name);
            return true;
        }

        // The resident has really completed both journeys. Only now may
        // the goods leave transit and the reserved courier wage be paid.
        DeliverFreight(-2-id);
        return false;
    }

    private void CompleteCourierPayment(int shipmentId,FreightShipment shipment)
    {
        if(shipment.CourierId is null ||
           !_residentById.TryGetValue(shipment.CourierId,out var actor) ||
           actor.CourierShipmentId!=shipmentId ||
           shipment.PickupMinute is null ||
           actor.CurrentPlaceId!=shipment.To.Id ||
           shipment.Carrier is null ||
           _courierEscrowGold<shipment.CourierWage)
            throw new InvalidOperationException("Invalid courier-delivery completion");
        _courierEscrowGold-=shipment.CourierWage;
        actor.Gold+=shipment.CourierWage;
        actor.CourierWageIncome+=shipment.CourierWage;
        actor.WageIncome+=shipment.CourierWage;
        shipment.Carrier.WagesPaid+=shipment.CourierWage;
        _courierWages+=shipment.CourierWage;
        _wages+=shipment.CourierWage;
        actor.CourierShipmentId=null;
        actor.CourierDeliveries++;
        _courierDeliveries++;

        var completed=CourierSnapshot(shipmentId,shipment,"Delivered",Minute);
        _recentCourierJourneys.Enqueue(completed);
        while(_recentCourierJourneys.Count>CourierHistoryLimit)
            _recentCourierJourneys.Dequeue();
        Observe(actor,"courier_delivered",shipment.Quantity+" "+
            shipment.Commodity+" to "+shipment.To.Name+
            "; paid "+shipment.CourierWage+" gold");
    }

    private CourierJourneySnapshot CourierSnapshot(
        int id,FreightShipment shipment,string stage,long? deliveredMinute)
    {
        if(shipment.CourierId is null)
            throw new InvalidOperationException("A courier snapshot needs an NPC");
        var actor=_residentById[shipment.CourierId];
        return new CourierJourneySnapshot(
            id,actor.Source.Id,actor.Source.Name,shipment.Carrier!.Id,
            shipment.From.Id,shipment.From.Name,
            shipment.To.Id,shipment.To.Name,
            shipment.Commodity,shipment.Quantity,shipment.CourierWage,stage,
            shipment.DepartMinute,shipment.PickupMinute,
            shipment.ExpectedDeliveryMinute,deliveredMinute,
            shipment.From.Place.MapX,shipment.From.Place.MapY,
            shipment.To.Place.MapX,shipment.To.Place.MapY);
    }

    private CourierJourneySnapshot[] GetCourierJourneySnapshots()
    {
        // Recent completed journeys provide deterministic playback even
        // when the user requests a day-end snapshot with zero active trips.
        return _recentCourierJourneys.Concat(_freight
                .Where(kv=>kv.Value.CourierId is not null)
                .OrderBy(kv=>kv.Key)
                .Select(kv=>CourierSnapshot(kv.Key,kv.Value,
                    kv.Value.PickupMinute is null?"HeadingToPickup":"Carrying",
                    null)))
            .ToArray();
    }
}
