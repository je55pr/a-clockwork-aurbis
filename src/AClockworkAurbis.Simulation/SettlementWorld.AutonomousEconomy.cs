namespace AClockworkAurbis.Simulation;

// Deliberately simple, inspectable NPC planning. This policy issues exactly the
// same queued actions as a future player controller; it never mints money or stock.
// Resource access and occupational inclinations are simulation hypotheses.
public sealed partial class SettlementWorld
{
    private long _autonomousChoices, _autonomousCompleted, _autonomousFailed;
    private readonly long[] _autonomousChosenKinds=new long[4];
    private readonly long[] _autonomousCompletedKinds=new long[4];
    private readonly long[] _autonomousFailedKinds=new long[4];
    private readonly HashSet<string> _autonomousParticipants =
        new(StringComparer.Ordinal);

    private string PersonalEconomicGoal(ResidentState resident)
    {
        if(resident.AutonomousGoal is string goal)return goal;
        string profession=resident.Source.Profession?.ToLowerInvariant()??"";
        string name=resident.Source.Name.ToLowerInvariant();
        ulong roll=StableRandom.Hash(resident.Source.Id,
            unchecked(_settings.Seed+907));
        goal=profession.Contains("smith") || profession.Contains("forge") ||
            profession.Contains("armorer") ? "iron_sword" :
            profession.Contains("miner") ? "iron_ore" :
            profession.Contains("wood") || profession.Contains("lumber") ||
            profession.Contains("forester") ? "logs" :
            profession.Contains("fish") ? "fish" :
            profession.Contains("hunt") || profession.Contains("trapper") ? "game_meat" :
            name.Contains("smith") ? "iron_sword" :
            (roll%20UL) switch
            {
                0UL or 1UL => "iron_sword",
                2UL or 3UL or 4UL => "iron_ore",
                5UL or 6UL or 7UL => "logs",
                8UL or 9UL or 10UL or 11UL or 12UL => "game_meat",
                _ => "fish"
            };
        resident.AutonomousGoal=goal;
        return goal;
    }

    private bool CanAttemptAutonomy(ResidentState resident)
    {
        if(!Detailed || !_settings.EnableAutonomousPersonalEconomy ||
           _personalTasks.ContainsKey(resident.Source.Id))return false;
        int hour=(int)(Minute%DayMinutes)/60;
        if(hour is <9 or >=18 || resident.Energy<45 || resident.Satiation<65 ||
           resident.Food==0 && resident.Satiation<79)return false;
        int day=checked((int)(Minute/DayMinutes));
        if(resident.AutonomousDay!=day)
        {
            resident.AutonomousDay=day;
            resident.AutonomousActionsToday=0;
        }
        int budget=resident.WorkId is null ? _settings.AutonomousDailyActionLimit :
            Math.Min(1,_settings.AutonomousDailyActionLimit);
        if(resident.AutonomousActionsToday>=budget ||
           Minute-resident.LastAutonomousConsideredMinute<60)return false;
        ulong roll=StableRandom.Hash(resident.Source.Id,
            unchecked(_settings.Seed+901));
        if(roll%100UL>=(ulong)_settings.AutonomousParticipationPercent)
            return false;
        resident.LastAutonomousConsideredMinute=Minute;
        return true;
    }

    private bool NearbyCommons(ResidentState resident,string sku) =>
        _openResourceSites.Values.Any(s=>
            s.Remaining(sku)>ReservedPersonalUnits(s.Place.Id,sku,
                PersonalTaskKind.Gather) &&
            JourneyDuration(resident,s.Place.Id)<=_settings.AutonomousMaxTravelMinutes);

    private bool NearbyStation(ResidentState resident,string kind) =>
        _businesses.Any(b=>b.Kind==kind && JourneyDuration(resident,b.Id)<=
            _settings.AutonomousMaxTravelMinutes);

    private BusinessState? NearbyBuyer(ResidentState resident,string sku,int count)
    {
        if(count<1 || !CommodityRecipes.Prices.ContainsKey(sku))return null;
        decimal payment=IndividualBuyback(sku)*count;
        return _businesses.Where(b=>BuysFromIndividuals(b,sku) &&
                b.Gold-ReservedPersonalBuyerGold(b.Id)>=
                    payment+_settings.HourlyWage*2 &&
                Quantity(b.Stock,sku)+ReservedPersonalUnits(b.Id,sku,
                    PersonalTaskKind.Sell)<=_settings.MaxCommodityStockPerSite-count)
            .Select(b=>(Firm:b,Distance:JourneyDuration(resident,b.Id)))
            .Where(x=>x.Distance<=_settings.AutonomousMaxTravelMinutes)
            .OrderBy(x=>x.Distance).ThenBy(x=>x.Firm.Id,StringComparer.Ordinal)
            .Select(x=>x.Firm).FirstOrDefault();
    }

    private BusinessState? NearbyToolVendor(ResidentState resident,string tool)
    {
        if(resident.Food<1 || !_settings.EnableLocalProduction)return null;
        return _businesses.Where(b=>SellsToIndividuals(b,tool) &&
                Quantity(b.Stock,tool)>0 &&
                resident.Gold>=MarketRetailQuote(b,tool)+
                    _settings.AutonomousToolPurchaseGoldReserve)
            .Select(b=>(Firm:b,Distance:JourneyDuration(resident,b.Id),
                Price:MarketRetailQuote(b,tool)))
            .Where(x=>x.Distance<=_settings.AutonomousMaxTravelMinutes)
            .OrderBy(x=>x.Price).ThenBy(x=>x.Distance)
            .ThenBy(x=>x.Firm.Id,StringComparer.Ordinal)
            .Select(x=>x.Firm).FirstOrDefault();
    }

    private bool TryAutonomousAction(int index,ResidentState actor)
    {
        if(!CanAttemptAutonomy(actor))return false;
        string id=actor.Source.Id;
        string goal=PersonalEconomicGoal(actor);
        bool Choose(bool issued,string reason)
        {
            if(!issued)return false;
            var task=_personalTasks[id];
            _personalTasks[id]=task with { Autonomous=true };
            actor.AutonomousLastReason=reason;
            actor.AutonomousActionsToday++;
            actor.AutonomousChoices++;
            _autonomousChoices++;
            _autonomousChosenKinds[(int)task.Kind]++;
            _autonomousParticipants.Add(id);
            Observe(actor,"autonomous_decision",
                goal+": "+reason+"; "+task.Kind+" "+task.Sku+
                " at "+DisplayPlace(task.Destination));
            return SchedulePersonalTask(index,actor);
        }

        // Sell owned surpluses first: buyers must possess actual cash and
        // storage. A smith keeps essential inputs until the sword is finished.
        string[] priority=["raw_hide","fish","game_meat","iron_ore","logs",
            "firewood","lumber","iron_ingot","iron_sword"];
        foreach(string sku in priority)
        {
            int owned=Quantity(actor.Stock,sku);
            bool preservingSword=goal=="iron_sword" &&
                Quantity(actor.Stock,"iron_sword")==0 &&
                (sku is "iron_ore" or "logs" or "firewood" or
                    "lumber" or "iron_ingot");
            int reserve=preservingSword ? owned : sku=="iron_sword" ? 1:0;
            int quantity=Math.Min(2,owned-reserve);
            if(quantity<=0)continue;
            var buyer=NearbyBuyer(actor,sku,quantity);
            if(buyer is not null && Choose(RequestPersonalSale(id,buyer.Id,sku,quantity),
                "sell personal surplus for "+IndividualBuyback(sku)*quantity+
                " gold; buyer has funded demand"))
                return true;
        }

        if(_settings.EnableLocalProduction && goal=="iron_sword" &&
           Quantity(actor.Stock,"iron_sword")>0 &&
           actor.EquippedWeaponSku is null && EquipPersonalWeapon(id,"iron_sword"))
        {
            actor.AutonomousLastReason="equipped personally forged iron sword";
            Observe(actor,"autonomous_goal",actor.AutonomousLastReason);
        }

        if(_settings.EnableLocalProduction && goal=="iron_sword" &&
           Quantity(actor.Stock,"iron_sword")==0 &&
           TryAutonomousSwordStep(actor,Choose))return true;

        // Ordinary livelihood: choose a finite local resource with a buyer.
        // If the preferred resource is gone or its buyer is broke, switch to
        // another opportunity rather than blindly filling an unsellable bag.
        string[] resources=goal switch
        {
            "iron_ore"=>["iron_ore","fish","game_meat","logs"],
            "logs"=>["logs","fish","game_meat","iron_ore"],
            "game_meat"=>["game_meat","fish","logs","iron_ore"],
            _=>["fish","game_meat","logs","iron_ore"]
        };
        if(!_settings.EnableLocalProduction)return false;
        foreach(string sku in resources)
        {
            if(Quantity(actor.Stock,sku)>=(sku is "iron_ore" or "logs"?4:2) ||
               !NearbyCommons(actor,sku) || NearbyBuyer(actor,sku,1) is null)
                continue;
            string? tool=GatheringTool(sku);
            if(tool is not null && Quantity(actor.Stock,tool)==0)
            {
                var vendor=NearbyToolVendor(actor,tool);
                if(vendor is not null && Choose(
                    RequestPersonalPurchase(id,vendor.Id,tool),
                    "buy "+tool+" to gather and sell "+sku+" independently"))
                    return true;
                continue;
            }
            if(Choose(RequestPersonalGather(id,sku),
                sku==goal ? "gather a locally saleable resource without employment" :
                goal+" unavailable or unprofitable locally, switch to "+sku))
                return true;
        }
        return false;
    }

    private bool TryAutonomousSwordStep(ResidentState actor,
        Func<bool,string,bool> choose)
    {
        string id=actor.Source.Id;
        bool Has(string sku,int count)=>Quantity(actor.Stock,sku)>=count;
        bool Make(string recipe,string station,string reason) =>
            NearbyStation(actor,station) &&
            choose(RequestPersonalCraft(id,recipe),reason);
        bool Acquire(string sku,int target,string reason)
        {
            if(Has(sku,target))return false;
            if(!NearbyCommons(actor,sku))return false;
            string? tool=GatheringTool(sku);
            if(tool is not null && !Has(tool,1))
            {
                var vendor=NearbyToolVendor(actor,tool);
                return vendor is not null && choose(
                    RequestPersonalPurchase(id,vendor.Id,tool),
                    "acquire "+tool+" to "+reason);
            }
            return choose(RequestPersonalGather(id,sku),reason);
        }

        if(Has("iron_ingot",2) && Has("lumber",1))
            return Make("forge_personal_iron_sword",BusinessKinds.Workshop,
                "forge a weapon from personally owned ingots and lumber");
        if(Has("iron_ore",2) && Has("firewood",1))
            return Make("smelt_iron",BusinessKinds.Smeltery,
                "smelt personally gathered ore with retained fuel");
        if((!Has("lumber",1) || !Has("firewood",1)) && Has("logs",2))
            return Make("saw_logs",BusinessKinds.Sawmill,
                "turn personally gathered logs into lumber and fuel");
        if(!Has("lumber",1) || !Has("firewood",1))
            return Acquire("logs",2,"make lumber and firewood for a sword");
        if(!Has("iron_ingot",2))
            return Acquire("iron_ore",2,"smelt iron for a sword");
        return false;
    }
}
