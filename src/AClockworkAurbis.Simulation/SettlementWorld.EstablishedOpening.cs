namespace AClockworkAurbis.Simulation;

public sealed partial class SettlementWorld
{
    // Optional scenario endowment: an existing island has populated kitchens,
    // warehouses and households. This is opening ledger stock, never daily
    // production or undocumented evidence from ESO.
    private void SeedEstablishedOpeningInventory()
    {
        void AddPersonal(ResidentState person,string sku,int requested)
        {
            int room=_settings.MaxPersonalCommodityStock-Quantity(person.Stock,sku);
            int amount=Math.Max(0,Math.Min(room,requested));
            if(amount==0)return;
            StockAdd(person.Stock,sku,amount);
            if(CommodityRecipes.Edible(sku))person.Food+=amount;
            if(CommodityRecipes.HouseholdGoods.Contains(sku,StringComparer.Ordinal))
                person.Goods+=amount;
        }
        foreach(var person in _residents)
        {
            int meals=2+StableRandom.Between(person.Source.Id,_settings.Seed,57,3);
            AddPersonal(person,"bread",meals);
            if(StableRandom.Between(person.Source.Id,_settings.Seed,58,4)==0)
                AddPersonal(person,"vegetable_stew",1);
            if(StableRandom.Between(person.Source.Id,_settings.Seed,59,3)==0)
                AddPersonal(person,"firewood",2);
            if(StableRandom.Between(person.Source.Id,_settings.Seed,60,7)==0)
                AddPersonal(person,"leather_goods",1);
            if(StableRandom.Between(person.Source.Id,_settings.Seed,61,9)==0)
                AddPersonal(person,"furnishings",1);
        }
        void Add(BusinessState firm,string sku,int amount)
        {
            if(amount<=0)return;
            int allowed=_settings.MaxCommodityStockPerSite-Quantity(firm.Stock,sku);
            if(allowed>0)StockAdd(firm.Stock,sku,Math.Min(allowed,amount));
        }
        foreach(var company in _businesses)
        {
            switch(company.Kind)
            {
                case BusinessKinds.FoodRetail:
                    Add(company,"bread",18);
                    Add(company,"vegetable_stew",8);
                    Add(company,"garden_salad",8);
                    Add(company,"fish_meal",3);
                    Add(company,"roast_meat",3);
                    Add(company,"wheat",5);
                    Add(company,"potato",5);
                    Add(company,"onion",5);
                    Add(company,"lettuce",5);
                    if(HasNamedChef(company))Add(company,"chef_game_pie",2);
                    break;
                case BusinessKinds.FoodProducer:
                    foreach(var crop in CropMix(company))Add(company,crop,12);
                    break;
                case BusinessKinds.ResourceProducer:
                    Add(company,IsLoggingSite(company)?"logs":"iron_ore",15);
                    break;
                case BusinessKinds.GoodsRetail:
                    Add(company,"firewood",12);
                    Add(company,"iron_tools",5);
                    Add(company,"leather_goods",5);
                    Add(company,"furnishings",4);
                    Add(company,"pickaxe",2);
                    Add(company,"felling_axe",2);
                    break;
                case BusinessKinds.Bakery:
                    Add(company,"wheat",12);
                    Add(company,"potato",8);
                    Add(company,"onion",8);
                    Add(company,"lettuce",8);
                    Add(company,"firewood",10);
                    Add(company,"bread",20);
                    Add(company,"vegetable_stew",6);
                    Add(company,"garden_salad",6);
                    if(HasNamedChef(company))Add(company,"chef_game_pie",2);
                    break;
                case BusinessKinds.Sawmill:
                    Add(company,"logs",12);
                    Add(company,"lumber",10);
                    Add(company,"firewood",10);
                    break;
                case BusinessKinds.Smeltery:
                    Add(company,"iron_ore",12);
                    Add(company,"iron_ingot",8);
                    Add(company,"firewood",10);
                    break;
                case BusinessKinds.Tannery:
                    Add(company,"raw_hide",12);
                    Add(company,"leather",6);
                    Add(company,"firewood",8);
                    break;
                case BusinessKinds.Leatherworker:
                    Add(company,"leather",10);
                    Add(company,"leather_goods",5);
                    break;
                case BusinessKinds.Workshop:
                    Add(company,"lumber",8);
                    if(CommodityRecipes.IsCarpenter(company))
                        Add(company,"furnishings",6);
                    else
                    {
                        Add(company,"iron_ingot",10);
                        Add(company,"iron_tools",4);
                        Add(company,"pickaxe",3);
                        Add(company,"felling_axe",3);
                        Add(company,"iron_sword",2);
                    }
                    break;
                case BusinessKinds.Hunter:
                    Add(company,"game_meat",12);
                    Add(company,"raw_hide",10);
                    break;
                case BusinessKinds.Fishery:
                    Add(company,"fish",12);
                    break;
                case BusinessKinds.ImportHub:
                    Add(company,"horseshoes",8);
                    Add(company,"harness",4);
                    break;
                case BusinessKinds.GuardPost:
                    Add(company,"iron_sword",3);
                    Add(company,"bread",8);
                    break;
                case BusinessKinds.Institution:
                case BusinessKinds.DomesticService:
                    Add(company,"bread",10);
                    Add(company,"firewood",6);
                    Add(company,"furnishings",3);
                    break;
                case BusinessKinds.CleaningService:
                    Add(company,"iron_tools",3);
                    break;
            }
        }
    }
}
