namespace AClockworkAurbis.Simulation;

public sealed partial class SettlementWorld
{
    private void RegionalWholesaleAndTrade()
    {
        if (_ports.Length == 0) return;
        if (_settings.EnableExternalTrade)
            PurchaseForeignShortfalls();

        var sources = _farms.Concat(_ports).ToArray();
        int foodTarget = _foodShops.Length == 0 ? 0 :
            Math.Max(8, checked(_residents.Count * 2 / _foodShops.Length));
        foreach (var shop in _foodShops)
            RegionalWholesale(shop, ClosestSuppliers(shop, sources), foodTarget,
                BusinessKinds.FoodRetail);

        int materialTarget = _workshops.Length == 0 ? 0 :
            Math.Max(5, checked(_residents.Count / 2 / _workshops.Length));
        var materialSources = _extractors.Concat(_ports).ToArray();
        foreach (var workshop in _workshops)
            RegionalWholesale(workshop, ClosestSuppliers(workshop, materialSources),
                materialTarget, BusinessKinds.Workshop);

        var goodsStores = _businesses.Where(b => b.Kind == BusinessKinds.GoodsRetail).ToArray();
        int goodsTarget = goodsStores.Length == 0 ? 0 :
            Math.Max(4, checked(_residents.Count / 4 / goodsStores.Length));
        foreach (var shop in goodsStores)
            RegionalWholesale(shop, ClosestSuppliers(shop, _workshops),
                goodsTarget, BusinessKinds.GoodsRetail);

        if (_settings.EnableExternalTrade)
            SellRegionalSurplusToForeignMarket();
    }

    private BusinessState[] ClosestSuppliers(
        BusinessState customer, IEnumerable<BusinessState> providers) =>
        providers.OrderBy(p => BusinessTravelCost(customer, p))
            .ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();

    private static int BusinessTravelCost(BusinessState a, BusinessState b) =>
        PlaceTradeDistance(a.Place, b.Place);

    private static int PlaceTradeDistance(PlaceDefinition a, PlaceDefinition b)
    {
        if (a.MapX is double ax && a.MapY is double ay &&
            b.MapX is double bx && b.MapY is double by)
            return (int)Math.Min(1_000_000,
                Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by)));
        if (a.District is string district &&
            string.Equals(district, b.District, StringComparison.OrdinalIgnoreCase))
            return 0;
        return 1_000_000;
    }

    private void PurchaseForeignShortfalls()
    {
        // Already dispatched cargo still belongs to the island. Do not
        // reorder from abroad just because local shipments have not arrived.
        int totalFood = checked(_businesses.Sum(b => b.Food) + TransitCount("food"));
        int totalMaterials = checked(_businesses.Sum(b => b.Materials) +
            TransitCount("materials"));
        int foodWant = Math.Max(0, _residents.Count - totalFood);
        int materialsWant = Math.Max(0, _workshops.Length * 20 - totalMaterials);
        int dailyLimit = checked((_residents.Count + 9) / 10 *
            _settings.DailyImportCapPerTenResidents);
        foodWant = Math.Min(foodWant, dailyLimit);
        materialsWant = Math.Min(materialsWant,
            dailyLimit == 0 ? 0 : Math.Max(1, dailyLimit / 2));

        // Import capacity is provided by paid port labour; process the best
        // staffed hub first, not always the first source page ID.
        foreach (var hub in _ports.OrderByDescending(p => p.CargoCapacity)
            .ThenBy(p => StableRandom.Hash(p.Id,
                unchecked(_settings.Seed + (int)(Minute / DayMinutes)))))
        {
            if (foodWant > 0)
            {
                int affordable = _settings.ExternalFoodImportPrice == 0 ? foodWant :
                    (int)Math.Min(foodWant, hub.Gold / _settings.ExternalFoodImportPrice);
                int food = Math.Min(hub.CargoCapacity,
                    Math.Min(foodWant, Math.Min(_outsideFood, affordable)));
                if (food > 0)
                {
                    long paid = checked((long)food * _settings.ExternalFoodImportPrice);
                    hub.Gold -= paid;
                    _outsideGold += paid;
                    hub.Food += food;
                    _outsideFood -= food;
                    hub.CargoCapacity -= food;
                    hub.CargoHandled += food;
                    _foodImported += food;
                    _foodImportsPaidUnits += food;
                    _goldImportsPaid += paid;
                    foodWant -= food;
                }
            }
            if (materialsWant > 0)
            {
                int affordable = _settings.ExternalMaterialImportPrice == 0 ?
                    materialsWant : (int)Math.Min(materialsWant,
                    hub.Gold / _settings.ExternalMaterialImportPrice);
                int materials = Math.Min(hub.CargoCapacity, Math.Min(materialsWant,
                    Math.Min(_outsideMaterials, affordable)));
                if (materials > 0)
                {
                    long paid = checked((long)materials *
                        _settings.ExternalMaterialImportPrice);
                    hub.Gold -= paid;
                    _outsideGold += paid;
                    hub.Materials += materials;
                    _outsideMaterials -= materials;
                    hub.CargoCapacity -= materials;
                    hub.CargoHandled += materials;
                    _materialsImported += materials;
                    _materialsImportsPaidUnits += materials;
                    _goldImportsPaid += paid;
                    materialsWant -= materials;
                }
            }
            if (foodWant == 0 && materialsWant == 0) break;
        }
    }

    private void SellRegionalSurplusToForeignMarket()
    {
        int foodDemand = checked(_residents.Count *
            _settings.DailyForeignFoodDemandPerResident);
        int materialDemand = Math.Max(0, _residents.Count / 4);
        int goodsDemand = checked(_residents.Count *
            _settings.DailyForeignGoodsDemandPerResident);

        // Match the producer to a port based on geography and actual loading
        // capacity. Supplier rotation avoids an arbitrary alphabetic monopoly.
        int food = ExportCommodity(_farms, foodDemand, _settings.FoodWholesalePrice,
            _settings.FoodExportPrice, "food", reserve: 8);
        _foodExports += food;
        _outsideFood = checked(_outsideFood + food);

        int materials = ExportCommodity(_extractors, materialDemand,
            _settings.MaterialWholesalePrice,
            _settings.MaterialWholesalePrice + 1, "materials", reserve: 10);
        _materialsExports += materials;
        _outsideMaterials = checked(_outsideMaterials + materials);

        int goods = ExportCommodity(_workshops, goodsDemand,
            _settings.GoodsWholesalePrice,
            _settings.GoodsExportPrice, "goods", reserve: 4);
        _goodsExports += goods;
        _outsideGoods = checked(_outsideGoods + goods);
    }

    // Every shipment has a paying island buyer and a paying foreign buyer.
    // Cargo-handling credits arise only from paid dockworker labour.
    private int ExportCommodity(
        BusinessState[] sellers, int demand, int domesticPrice, int foreignPrice,
        string commodity, int reserve)
    {
        if (demand <= 0 || foreignPrice <= 0 || sellers.Length == 0) return 0;
        var sorted = sellers.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
        int start = (int)((Minute / DayMinutes) % sorted.Length);
        int exported = 0;
        for (int i = 0; i < sorted.Length && demand > 0; i++)
        {
            var supplier = sorted[(start + i) % sorted.Length];
            int stock = commodity switch
            {
                "food" => supplier.Food,
                "materials" => supplier.Materials,
                _ => supplier.Goods
            };
            int available = Math.Max(0, stock - reserve);
            if (available == 0) continue;

            // A producer's natural nearest staffed port wins. If that port
            // is full or insolvent, another can pick up the contract.
            var eligible = _ports.Where(p => p.CargoCapacity > 0 &&
                (domesticPrice == 0 || p.Gold >= domesticPrice))
                .OrderBy(p => BusinessTravelCost(p, supplier))
                .ThenByDescending(p => p.CargoCapacity)
                .ThenBy(p => StableRandom.Hash(p.Id + supplier.Id,
                    unchecked(_settings.Seed + (int)(Minute / DayMinutes))));
            foreach (var port in eligible)
            {
                int affordableByPort = domesticPrice == 0 ? available :
                    (int)Math.Min(int.MaxValue, port.Gold / domesticPrice);
                int affordableByOutside = (int)Math.Min(int.MaxValue,
                    _outsideGold / foreignPrice);
                int quantity = Math.Min(demand, Math.Min(available,
                    Math.Min(port.CargoCapacity,
                        Math.Min(affordableByPort, affordableByOutside))));
                if (quantity <= 0) continue;

                long domesticPayment = checked((long)quantity * domesticPrice);
                long foreignPayment = checked((long)quantity * foreignPrice);
                port.Gold -= domesticPayment;
                supplier.Gold += domesticPayment;
                port.Gold += foreignPayment;
                _outsideGold -= foreignPayment;
                supplier.WholesaleRevenue += domesticPayment;
                port.WholesaleSpending += domesticPayment;
                port.WholesaleRevenue += foreignPayment;
                port.CargoCapacity -= quantity;
                port.CargoHandled += quantity;
                _goldExportsEarned += foreignPayment;
                if (commodity == "food") supplier.Food -= quantity;
                else if (commodity == "materials") supplier.Materials -= quantity;
                else supplier.Goods -= quantity;
                exported += quantity;
                available -= quantity;
                demand -= quantity;
                if (available == 0 || demand == 0 || _outsideGold < foreignPrice) break;
            }
        }
        return exported;
    }
}
