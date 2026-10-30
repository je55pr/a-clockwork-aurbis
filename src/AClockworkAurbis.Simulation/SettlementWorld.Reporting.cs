namespace AClockworkAurbis.Simulation;

public sealed partial class SettlementWorld
{
    private void Observe(ResidentState actor,string action,string detail)
    {
        if(Observer!=null)
            Observer(new SimObservation(Minute,actor.Source.Id,action,detail));
    }

    private long TotalGold() =>
        checked(_businesses.Sum(b=>b.Gold)+_residents.Sum(r=>r.Gold));

    private long TotalFood() =>
        checked(_businesses.Sum(b=>(long)b.Food)+_residents.Sum(r=>(long)r.Food));

    private long TotalMaterials() => _businesses.Sum(b=>(long)b.Materials);

    private long TotalGoods() =>
        checked(_businesses.Sum(b=>(long)b.Goods)+_residents.Sum(r=>(long)r.Goods));

    public SettlementReport Report()
    {
        return new SettlementReport
        {
            Settlement=Catalog.Settlement,
            SourcePlaces=Catalog.Places.Length,
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
            InitialFood=_initialFood,InitialMaterials=_initialMaterials,
            InitialGoods=_initialGoods,InitialGold=_initialGold,TotalGold=TotalGold(),
            BusinessDetails=_businesses.OrderBy(b=>b.Id,StringComparer.Ordinal)
                .Select(b=>new BusinessSnapshot(b.Name,b.Kind,b.Id,b.OwnerId,
                    b.Operators.Count,b.SimulatedLeaseholderId,b.LeaseEvidence,
                    b.Gold,b.Food,b.Materials,b.Goods,
                    b.RetailSales,b.WorkHours,b.ItemsCrafted,b.WagesPaid,b.DividendsPaid)).ToArray()
        };
    }

    public void CheckInvariants()
    {
        if(TotalGold()!=_initialGold)
            throw new InvalidOperationException("Gold appeared or disappeared outside transfers");
        if(TotalFood()+_meals!=_initialFood+_foodImported)
            throw new InvalidOperationException("Food ledger did not reconcile");
        if(TotalMaterials()+TotalGoods()+_goodsUsed!=_initialMaterials+_initialGoods+_materialsImported)
            throw new InvalidOperationException("Material + finished goods ledger did not reconcile");
        if(_dividends!=_residents.Sum(r=>r.DividendIncome)||
           _dividends!=_businesses.Sum(b=>b.DividendsPaid))
            throw new InvalidOperationException("Leaseholder dividend ledger mismatch");
        if(_wages!=_residents.Sum(r=>r.WageIncome)||
           _wages!=_businesses.Sum(b=>b.WagesPaid))
            throw new InvalidOperationException("Worker wages do not reconcile to business wages");
        foreach(var r in _residents)
        {
            if(r.Gold<0||r.Food<0||r.Goods<0||r.Satiation is <0 or >100 ||
               r.Energy is <0 or >100||r.WorkRemainderMinutes is <0 or >=60)
                throw new InvalidOperationException("Invalid resident state: "+r.Source.Id);
        }
        foreach(var b in _businesses)
        {
            if(b.Gold<0||b.Food<0||b.Materials<0||b.Goods<0)
                throw new InvalidOperationException("Invalid business account/stock: "+b.Id);
        }
    }
}
