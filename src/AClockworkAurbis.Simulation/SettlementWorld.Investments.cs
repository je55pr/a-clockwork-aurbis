namespace AClockworkAurbis.Simulation;

// Capital projects are deliberately explicit, noncanonical simulation
// institutions. An upgraded productive site needs paid, sourced goods and
// workshop workers' actual paid hours. No work -> no new capacity.
public sealed partial class SettlementWorld
{
    private sealed class ConstructionContract(
        BusinessState investor,BusinessState builder,int requiredHours,
        long materialCostMilli,decimal labourPayment)
    {
        public BusinessState Investor { get; } = investor;
        public BusinessState Builder { get; } = builder;
        public int RequiredHours { get; } = requiredHours;
        public int CompletedHours { get; set; }
        public long MaterialCostMilli { get; } = materialCostMilli;
        public decimal LabourPayment { get; } = labourPayment;
    }

    private readonly SortedDictionary<string,ConstructionContract> _construction =
        new(StringComparer.Ordinal);
    private long _capitalProjectsStarted,_capitalProjectsCompleted,_capitalProjectsWorn;
    private long _capitalMaintenancePaid,_capitalMaterialsConsumed;
    private decimal _constructionLabourPayments;
    private readonly Queue<CapitalDecisionSnapshot> _capitalDecisions=new();

    private void RecordCapitalDecision(BusinessState firm,string action,
        decimal expectedPaybackDays,string reason)
    {
        if(_capitalDecisions.Count>=256)_capitalDecisions.Dequeue();
        _capitalDecisions.Enqueue(new CapitalDecisionSnapshot(
            (int)(Minute/DayMinutes),firm.Id,firm.Name,action,
            Money(expectedPaybackDays),reason));
    }

    private decimal ExpectedCapitalPaybackDays(BusinessState firm)
    {
        var recipe=CapitalRecipe(firm);
        if(recipe is null || firm.PlannedHours==0 ||
            firm.PlannedMargin<=0)return 100000m;
        decimal cost=recipe.Value.WorkHours*_settings.HourlyWage;
        foreach(var (sku,amount) in recipe.Value.Materials)
        {
            decimal unit=Quantity(firm.Stock,sku)>0
                ?UnitCostMilli(firm,sku)/1000m
                :CommodityRecipes.Prices[sku];
            cost+=amount*unit;
        }
        // Hypothetical 25% more throughput, only if extra production
        // could be sold. This is a forecast, never guaranteed income.
        decimal dailyBenefit=firm.PlannedMargin*
            Math.Max(1,firm.PlannedHours)/4m;
        return dailyBenefit<=0?100000m:
            Math.Min(100000m,Money(cost/dailyBenefit));
    }

    // Recipe quantities and hours are provisional economic hypotheses.
    // Construction goods and workers belong to the existing island ledger.
    private static (Dictionary<string,int> Materials,int WorkHours)? CapitalRecipe(
        BusinessState firm)
    {
        string[] values=firm.Kind switch
        {
            BusinessKinds.FoodProducer=>["lumber:2","iron_tools:1"],
            BusinessKinds.ResourceProducer=>["lumber:2","iron_ingot:2","iron_tools:1"],
            BusinessKinds.Bakery=>["lumber:2","iron_ingot:2","firewood:1"],
            BusinessKinds.Sawmill=>["lumber:2","iron_ingot:1"],
            BusinessKinds.Smeltery=>["lumber:2","iron_ingot:2"],
            BusinessKinds.Tannery=>["lumber:2","iron_ingot:1"],
            BusinessKinds.Workshop=>["lumber:3","iron_ingot:1"],
            BusinessKinds.Leatherworker=>["lumber:2","leather:1"],
            BusinessKinds.Hunter or BusinessKinds.Fishery=>["lumber:1","leather:1"],
            _=>[]
        };
        if(values.Length==0)return null;
        var inputs=values.Select(s=>s.Split(':')).ToDictionary(
            s=>s[0],s=>int.Parse(s[1]),StringComparer.Ordinal);
        return (inputs,firm.Kind is BusinessKinds.ResourceProducer or
            BusinessKinds.FoodProducer ? 5 : 4);
    }

    // Upkeep uses materials naturally available to each industry. Supply
    // failures can still wear equipment out, but never invent repairs.
    private static string MaintenanceCommodity(BusinessState firm) =>
        firm.Kind switch
        {
            BusinessKinds.Bakery => "firewood",
            BusinessKinds.ResourceProducer =>
                IsLoggingSite(firm)?"felling_axe":"pickaxe",
            BusinessKinds.Sawmill => "lumber",
            BusinessKinds.Smeltery => "iron_ingot",
            BusinessKinds.Tannery or BusinessKinds.Leatherworker => "leather",
            BusinessKinds.Workshop => "lumber",
            _ => "lumber"
        };

    private bool HasConstructionOrders(BusinessState builder) =>
        _construction.Values.Any(p=>p.Builder.Id==builder.Id &&
            p.CompletedHours<p.RequiredHours);

    private IEnumerable<(string Sku,int Target)> CapitalOrders(BusinessState firm)
    {
        if(firm.CapitalLevel>1 &&
            Minute/DayMinutes>=firm.LastCapitalMaintenanceDay+23)
        {
            string upkeep=MaintenanceCommodity(firm);
            if(Quantity(firm.Stock,upkeep)==0)
                yield return (upkeep,1);
        }
        if(!firm.RequestedCapital || _construction.ContainsKey(firm.Id))
            yield break;
        var recipe=CapitalRecipe(firm);
        if(recipe is null)yield break;
        foreach(var (sku,amount) in recipe.Value.Materials)
            yield return (sku,amount);
    }

    private void PlanCapitalDay(int day)
    {
        if(!Detailed || !_settings.EnableLocalProduction)return;
        if(day>0)
            foreach(var firm in _businesses.Where(f=>f.CapitalLevel>1 &&
                day-f.LastCapitalMaintenanceDay>=30))
            {
                string upkeep=MaintenanceCommodity(firm);
                if(Quantity(firm.Stock,upkeep)>0)
                {
                    WithdrawInventoryValue(firm,upkeep,1);
                    UseIngredient(firm.Stock,upkeep,1);
                    firm.LastCapitalMaintenanceDay=day;
                    _capitalMaintenancePaid++;
                    RecordCapitalDecision(firm,"maintained",0,
                        "physical maintenance material consumed");
                }
                else if(day-firm.LastCapitalMaintenanceDay>=45)
                {
                    firm.CapitalLevel--;
                    firm.CapitalWorkCredit=0;
                    firm.LastCapitalMaintenanceDay=day;
                    _capitalProjectsWorn++;
                    RecordCapitalDecision(firm,"degraded",0,
                        "maintenance material unavailable after grace period");
                }
            }

        foreach(var firm in _businesses)
        {
            if(!PlansProduction(firm) || CapitalRecipe(firm) is null ||
                firm.CapitalLevel>=3 || _construction.ContainsKey(firm.Id))
                continue;
            decimal payback=ExpectedCapitalPaybackDays(firm);
            if(day>=12 && day-firm.LastCapitalInvestmentDay>=20 &&
                firm.Gold > _settings.StartingBusinessGold+25m &&
                firm.RecentDemand.Values.Sum()>=2m && payback<=90m &&
                !firm.RequestedCapital)
            {
                firm.RequestedCapital=true;
                RecordCapitalDecision(firm,"requested",payback,
                    "observed sales and projected positive capital payback");
            }
            if(!firm.RequestedCapital)continue;
            if(!CanFundCapital(firm))continue;

            var builder=_businesses.Where(b=>b.Kind==BusinessKinds.Workshop &&
                b.Id!=firm.Id && _staff.TryGetValue(b.Id,out var workers) &&
                workers.Any(w=>w.WorkId==b.Id))
                .OrderBy(b=>BusinessTravelCost(b,firm))
                .ThenBy(b=>b.Id,StringComparer.Ordinal).FirstOrDefault();
            if(builder is null)continue;
            var recipe=CapitalRecipe(firm)!.Value;
            decimal labour=recipe.WorkHours*_settings.HourlyWage;
            if(firm.Gold < labour+20)continue;
            long inputs=0;
            foreach(var (sku,amount) in recipe.Materials)
            {
                inputs+=WithdrawInventoryValue(firm,sku,amount);
                UseIngredient(firm.Stock,sku,amount);
                _capitalMaterialsConsumed+=amount;
            }
            firm.Gold-=labour;
            builder.Gold+=labour; // Funded contract, workshop pays workers.
            firm.CapitalInvestedMilli=checked(firm.CapitalInvestedMilli+inputs+
                ToMilli(labour));
            _constructionLabourPayments+=labour;
            _construction.Add(firm.Id,new ConstructionContract(firm,builder,
                recipe.WorkHours,inputs,labour));
            firm.RequestedCapital=false;
            firm.LastCapitalInvestmentDay=day;
            _capitalProjectsStarted++;
            RecordCapitalDecision(firm,"funded",payback,
                "materials conserved and workshop labour prepaid");
        }
    }

    private bool CanFundCapital(BusinessState firm)
    {
        var recipe=CapitalRecipe(firm);
        if(recipe is null)return false;
        return recipe.Value.Materials.All(i=>Quantity(firm.Stock,i.Key)>=i.Value);
    }

    // Called only when an actual paid workshop hour completes. The hour
    // cannot also craft an ordinary commercial product.
    private bool CompleteConstructionHour(BusinessState builder)
    {
        var project=_construction.Values.FirstOrDefault(p=>
            p.Builder.Id==builder.Id && p.CompletedHours<p.RequiredHours);
        if(project is null)return false;
        project.CompletedHours++;
        if(project.CompletedHours==project.RequiredHours)
        {
            project.Investor.CapitalLevel++;
            project.Investor.LastCapitalMaintenanceDay=(int)(Minute/DayMinutes);
            project.Investor.CapitalWorkCredit=0;
            _capitalProjectsCompleted++;
            RecordCapitalDecision(project.Investor,"completed",0,
                "all contracted paid workshop hours delivered");
            _construction.Remove(project.Investor.Id);
        }
        return true;
    }

    // Increases physical throughput per paid work hour. Extra batches still
    // withdraw their actual recipe ingredients, and renewable/finite resource
    // ceilings are unchanged.
    private int CapitalProductionHours(BusinessState firm,int hours)
    {
        if(hours<=0)return 0;
        int credit=checked(firm.CapitalWorkCredit+
            hours*(firm.CapitalLevel-1));
        int bonus=credit/4;
        firm.CapitalWorkCredit=credit%4;
        return checked(hours+bonus);
    }

    private CapitalDecisionSnapshot[] CapitalDecisions() =>
        _capitalDecisions.ToArray();

    private CapitalProjectSnapshot[] CapitalProjects() =>
        _construction.Values.Select(p=>new CapitalProjectSnapshot(
            p.Investor.Id,p.Investor.Name,p.Builder.Name,
            p.CompletedHours,p.RequiredHours,p.MaterialCostMilli,
            p.LabourPayment)).ToArray();
}
