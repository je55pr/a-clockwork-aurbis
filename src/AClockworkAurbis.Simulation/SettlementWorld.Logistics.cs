namespace AClockworkAurbis.Simulation;

// A shipment is physical stock in transit, not simultaneous inventory at
// its source and destination. Routes are estimated from source map markers.
// Paid carriers own handling capacity and earn delivery fees when enabled.
// Individual NPC travel with cargo and detailed roads are not modelled.
internal sealed record FreightShipment(
    BusinessState From, BusinessState To, string Commodity, int Quantity,
    long DepartMinute, long ArriveMinute,
    long LandedValueMilli = 0,BusinessState? Carrier = null,
    string? CourierId = null,decimal CourierWage = 0m,
    long? PickupMinute = null,long? ExpectedDeliveryMinute = null);

public sealed partial class SettlementWorld
{
    private readonly Dictionary<int, FreightShipment> _freight = new();
    private readonly Dictionary<(string From,string To,string Commodity),long>
        _deliveredByRoute = new();
    private int _nextFreightId;
    private long _shipmentsSent, _shipmentsDelivered;
    private decimal _freightFees;
    private long _freightUnitsDelivered;

    private int Inbound(BusinessState buyer, string commodity)
    {
        int total = 0;
        foreach (var shipment in _freight.Values)
            if (shipment.To.Id == buyer.Id && shipment.Commodity == commodity)
                total = checked(total + shipment.Quantity);
        return total;
    }

    private int TransitCount(string commodity) =>
        _freight.Values.Where(s => s.Commodity == commodity)
            .Sum(s => s.Quantity);

    // This is a temporary common-carrier abstraction. Paid fees enter the
    // island's public transport/service account, not a fictitious NPC wallet.
    // The route has finite loads and nonzero time, even inside a settlement.
    private void RegionalWholesale(
        BusinessState buyer, IReadOnlyList<BusinessState> providers,
        int target, string requestedKind)
    {
        string commodity = requestedKind switch
        {
            BusinessKinds.FoodRetail => "food",
            BusinessKinds.Workshop => "materials",
            _ => "goods"
        };
        int inStock = commodity switch
        {
            "food" => buyer.Food,
            "materials" => buyer.Materials,
            _ => buyer.Goods
        };
        int needed = Math.Max(0, target - inStock - Inbound(buyer, commodity));
        if (needed == 0) return;

        int unitPrice = commodity switch
        {
            "food" => _settings.FoodWholesalePrice,
            "materials" => _settings.MaterialWholesalePrice,
            _ => _settings.GoodsWholesalePrice
        };
        foreach (var supplier in providers)
        {
            if (supplier.Id == buyer.Id || needed == 0) continue;
            int available = commodity switch
            {
                "food" => supplier.Food,
                "materials" => supplier.Materials,
                _ => supplier.Goods
            };
            if (available == 0) continue;

            int duration = PlaceTravelMinutes(supplier.Place, buyer.Place);
            int fee = checked(_settings.BaseFreightFee +
                (int)Math.Ceiling((double)duration / 90) *
                _settings.FreightFeePer90Minutes);
            if (buyer.Gold < fee) continue;
            decimal budget = buyer.Gold - fee;
            int affordable = unitPrice == 0 ? needed :
                (int)Math.Min(int.MaxValue, budget / unitPrice);
            int amount = Math.Min(_settings.MaxFreightLoad,
                Math.Min(needed, Math.Min(available, affordable)));
            if (amount <= 0) continue;
            long payment = checked((long)amount * unitPrice);
            buyer.Gold -= payment + fee;
            supplier.Gold += payment;
            _publicGold += fee;
            _freightFees += fee;
            buyer.WholesaleSpending += payment;
            supplier.WholesaleRevenue += payment;
            if (commodity == "food") supplier.Food -= amount;
            else if (commodity == "materials") supplier.Materials -= amount;
            else supplier.Goods -= amount;

            long arrival = checked(Minute + duration);
            int id = _nextFreightId++;
            _freight.Add(id, new FreightShipment(
                supplier, buyer, commodity, amount, Minute, arrival));
            // -1 is the daily economy event; lower negative values are freight.
            _queue.Enqueue(-2-id,(arrival,_sequence++));
            _shipmentsSent++;
            needed -= amount;
        }
    }

    private void DeliverFreight(int eventIndex)
    {
        int id = -2 - eventIndex;
        var shipment = _freight[id];
        _freight.Remove(id);
        if(shipment.CourierId is not null)
            CompleteCourierPayment(id,shipment);
        if(Detailed)
        {
            StockAdd(shipment.To.Stock,shipment.Commodity,shipment.Quantity);
            CreditInventoryValue(shipment.To,shipment.Commodity,
                shipment.LandedValueMilli);
        }
        else switch (shipment.Commodity)
        {
            case "food":
                shipment.To.Food = checked(shipment.To.Food + shipment.Quantity);
                break;
            case "materials":
                shipment.To.Materials = checked(shipment.To.Materials + shipment.Quantity);
                break;
            case "goods":
                shipment.To.Goods = checked(shipment.To.Goods + shipment.Quantity);
                break;
            default:
                throw new InvalidOperationException("Unknown freight commodity");
        }
        _shipmentsDelivered++;
        _freightUnitsDelivered += shipment.Quantity;
        if(shipment.Carrier is not null)
        {
            shipment.Carrier.FreightUnitsTransported+=shipment.Quantity;
            shipment.Carrier.CargoHandled+=shipment.Quantity;
            _paidFreightUnitsDelivered+=shipment.Quantity;
        }
        var key = (shipment.From.Id, shipment.To.Id, shipment.Commodity);
        _deliveredByRoute[key] = _deliveredByRoute.GetValueOrDefault(key) + shipment.Quantity;
    }
}
