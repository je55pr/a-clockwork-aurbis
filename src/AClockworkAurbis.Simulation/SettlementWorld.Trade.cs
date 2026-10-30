namespace AClockworkAurbis.Simulation;

public sealed partial class SettlementWorld
{
    private void RegionalWholesaleAndTrade()
    {
        if (_ports.Length == 0) return;

        // Explicit imports: goods AND money change owners, with finite
        // external stock, finite island funds and a daily shipping quota.
        if (_settings.EnableExternalTrade)
            PurchaseForeignShortfalls();

        var sources = _farms.Concat(_ports).ToArray();
        int foodTarget = _foodShops.Length == 0 ? 0 :
            Math.Max(8, checked(_residents.Count * 2 / _foodShops.Length));
        foreach (var shop in _foodShops)
            Wholesale(shop, ClosestSuppliers(shop, sources), foodTarget,
                BusinessKinds.FoodRetail);

        int materialTarget = _workshops.Length == 0 ? 0 :
            Math.Max(5, checked(_residents.Count / 2 / _workshops.Length));
        var materialSources = _extractors.Concat(_ports).ToArray();
        foreach (var workshop in _workshops)
            Wholesale(workshop, ClosestSuppliers(workshop, materialSources),
                materialTarget, BusinessKinds.Workshop);

        var goodsStores = _businesses.Where(b => b.Kind == BusinessKinds.GoodsRetail).ToArray();
        int goodsTarget = goodsStores.Length == 0 ? 0 :
            Math.Max(4, checked(_residents.Count / 4 / goodsStores.Length));
        foreach (var shop in goodsStores)
            Wholesale(shop, ClosestSuppliers(shop, _workshops),
                goodsTarget, BusinessKinds.GoodsRetail);

        if (_settings.EnableExternalTrade)
            SellRegionalSurplusToForeignMarket();
    }

    private BusinessState[] ClosestSuppliers(
        BusinessState customer, IEnumerable<BusinessState> providers) =>
        providers.OrderBy(p => BusinessTravelCost(customer, p))
            .ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();

    private static int BusinessTravelCost(BusinessState a, BusinessState b)
    {
        if (a.Place.MapX is double ax && a.Place.MapY is double ay &&
            b.Place.MapX is double bx && b.Place.MapY is double by)
            return (int)Math.Min(1_000_000,
                Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by)));
        if (a.Place.District is string district &&
            string.Equals(district, b.Place.District, StringComparison.OrdinalIgnoreCase))
            return 0;
        return 1_000_000;
    }

    private void PurchaseForeignShortfalls()
    {
        int totalFood = _businesses.Sum(b => b.Food);
        int totalMaterials = _businesses.Sum(b => b.Materials);
        int foodWant = Math.Max(0, _residents.Count - totalFood);
        int materialsWant = Math.Max(0, _workshops.Length * 20 - totalMaterials);
        int dailyLimit = checked((_residents.Count + 9) / 10 *
            _settings.DailyImportCapPerTenResidents);
        foodWant = Math.Min(foodWant, dailyLimit);
        materialsWant = Math.Min(materialsWant, Math.Max(1, dailyLimit / 2));
        foreach (var hub in _ports)
        {
            if (foodWant > 0)
            {
                int affordable = _settings.ExternalFoodImportPrice == 0 ? foodWant :
                    (int)Math.Min(foodWant, hub.Gold / _settings.ExternalFoodImportPrice);
                int food = Math.Min(foodWant, Math.Min(_outsideFood, affordable));
                if (food > 0)
                {
                    long paid = checked((long)food * _settings.ExternalFoodImportPrice);
                    hub.Gold -= paid;
                    _outsideGold += paid;
                    hub.Food += food;
                    _outsideFood -= food;
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
                int materials = Math.Min(materialsWant,
                    Math.Min(_outsideMaterials, affordable));
                if (materials > 0)
                {
                    long paid = checked((long)materials *
                        _settings.ExternalMaterialImportPrice);
                    hub.Gold -= paid;
                    _outsideGold += paid;
                    hub.Materials += materials;
                    _outsideMaterials -= materials;
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
        var farms = _farms.OrderBy(b => b.Id, StringComparer.Ordinal).ToArray();
        var mines = _extractors.OrderBy(b => b.Id, StringComparer.Ordinal).ToArray();
        var workshops = _workshops.OrderBy(b => b.Id, StringComparer.Ordinal).ToArray();
        foreach (var hub in _ports)
        {
            if (foodDemand > 0)
            {
                int sold = ExportCommodity(hub, farms, foodDemand,
                    _settings.FoodWholesalePrice,
                    _settings.FoodExportPrice, "food", reserve: 8);
                foodDemand -= sold;
                _foodExports += sold;
                _outsideFood = checked(_outsideFood + sold);
            }

            if (materialDemand > 0)
            {
                // Raw material exports can fund miners when local fabrication
                // demand is low. External buyer pays, mine transfers inventory.
                int sold = ExportCommodity(hub, mines, materialDemand,
                    _settings.MaterialWholesalePrice,
                    _settings.MaterialWholesalePrice + 1, "materials", reserve: 10);
                materialDemand -= sold;
                _materialsExports += sold;
                _outsideMaterials = checked(_outsideMaterials + sold);
            }

            if (goodsDemand > 0)
            {
                int sold = ExportCommodity(hub, workshops, goodsDemand,
                    _settings.GoodsWholesalePrice,
                    _settings.GoodsExportPrice, "goods", reserve: 4);
                goodsDemand -= sold;
                _goodsExports += sold;
                _outsideGoods = checked(_outsideGoods + sold);
            }
            if (foodDemand == 0 && materialDemand == 0 && goodsDemand == 0) break;
        }
    }

    // Buyers are ports, sellers are real local producers. Every export
    // involves TWO explicit transactions: port buys domestically and then
    // sells to the external market. There is no free export income.
    private int ExportCommodity(
        BusinessState port, BusinessState[] sellers, int demand,
        int domesticPrice, int foreignPrice, string commodity, int reserve)
    {
        if (demand <= 0 || foreignPrice <= 0) return 0;
        int exported = 0;
        foreach (var supplier in ClosestSuppliers(port, sellers))
        {
            int stock = commodity switch
            {
                "food" => supplier.Food,
                "materials" => supplier.Materials,
                _ => supplier.Goods
            };
            int available = Math.Max(0, stock - reserve);
            if (available == 0) continue;
            int affordableByPort = domesticPrice == 0 ? available :
                (int)Math.Min(int.MaxValue, port.Gold / domesticPrice);
            int affordableByOutside = (int)Math.Min(int.MaxValue,
                _outsideGold / foreignPrice);
            int quantity = Math.Min(demand,
                Math.Min(available, Math.Min(affordableByPort, affordableByOutside)));
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
            _goldExportsEarned += foreignPayment;
            if (commodity == "food") supplier.Food -= quantity;
            else if (commodity == "materials") supplier.Materials -= quantity;
            else supplier.Goods -= quantity;

            exported += quantity;
            demand -= quantity;
            if (demand == 0) break;
        }
        return exported;
    }
}
