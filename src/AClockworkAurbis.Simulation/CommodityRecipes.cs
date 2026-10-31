namespace AClockworkAurbis.Simulation;

// Quantities, outputs, prices and crop mixtures are provisional economic
// hypotheses, not ESO's player-crafting recipes or documented Auridon crops.
public sealed record CommodityRecipe(
    string Id, string Industry,
    IReadOnlyDictionary<string,int> Ingredients,
    IReadOnlyDictionary<string,int> Products);

public static class CommodityRecipes
{
    public static readonly string[] Meals =
        ["provisions","foraged_greens","bread","vegetable_stew",
         "garden_salad","fish_meal","roast_meat"];
    public static readonly string[] HouseholdGoods =
        ["firewood","iron_tools","leather_goods","furnishings"];
    public static readonly string[] Crops = ["wheat","potato","onion","lettuce"];
    public static readonly string[] FinishedGoods =
        ["firewood","pickaxe","felling_axe","horseshoes","iron_tools",
         "leather_goods","furnishings","harness"];
    public static readonly IReadOnlyDictionary<string,int> Prices =
        new Dictionary<string,int>(StringComparer.Ordinal)
        {
            ["provisions"]=6, ["foraged_greens"]=2, ["wheat"]=4,
            ["potato"]=4, ["onion"]=4, ["lettuce"]=4,
            ["fish"]=3, ["game_meat"]=3, ["raw_hide"]=3,
            ["iron_ore"]=3, ["logs"]=3, ["lumber"]=5,
            ["firewood"]=3, ["iron_ingot"]=8, ["leather"]=6,
            ["bread"]=3, ["vegetable_stew"]=3, ["garden_salad"]=3,
            ["fish_meal"]=4, ["roast_meat"]=4,
            ["pickaxe"]=16, ["felling_axe"]=16, ["horseshoes"]=9,
            ["iron_tools"]=7, ["leather_goods"]=7, ["furnishings"]=9,
            ["harness"]=15
        };

    private static Dictionary<string,int> Stock(params (string Id,int Count)[] items) =>
        items.ToDictionary(x=>x.Id,x=>x.Count,StringComparer.Ordinal);

    public static readonly IReadOnlyList<CommodityRecipe> All =
    [
        new("saw_logs",BusinessKinds.Sawmill,
            Stock(("logs",2)),Stock(("lumber",2),("firewood",3))),
        new("smelt_iron",BusinessKinds.Smeltery,
            Stock(("iron_ore",2),("firewood",1)),Stock(("iron_ingot",2))),
        new("tan_hide",BusinessKinds.Tannery,
            Stock(("raw_hide",2),("firewood",1)),Stock(("leather",2))),
        new("bake_bread",BusinessKinds.Bakery,
            Stock(("wheat",2),("firewood",1)),Stock(("bread",12))),
        new("cook_stew",BusinessKinds.Bakery,
            Stock(("potato",1),("onion",1),("firewood",1)),
            Stock(("vegetable_stew",12))),
        new("prepare_salad",BusinessKinds.Bakery,
            Stock(("lettuce",1),("onion",1)),Stock(("garden_salad",12))),
        new("cook_fish",BusinessKinds.Bakery,
            Stock(("fish",1),("potato",1),("firewood",1)),
            Stock(("fish_meal",12))),
        new("roast_game",BusinessKinds.Bakery,
            Stock(("game_meat",1),("onion",1),("firewood",1)),
            Stock(("roast_meat",12))),
        new("forge_pickaxes",BusinessKinds.Workshop,
            Stock(("iron_ingot",2),("lumber",1)),Stock(("pickaxe",2))),
        new("forge_felling_axes",BusinessKinds.Workshop,
            Stock(("iron_ingot",2),("lumber",1)),Stock(("felling_axe",2))),
        new("forge_horseshoes",BusinessKinds.Workshop,
            Stock(("iron_ingot",1)),Stock(("horseshoes",4))),
        new("forge_iron_tools",BusinessKinds.Workshop,
            Stock(("iron_ingot",1),("lumber",1)),Stock(("iron_tools",2))),
        new("carve_furnishings",BusinessKinds.Workshop,
            Stock(("lumber",2)),Stock(("furnishings",1))),
        new("craft_leather_goods",BusinessKinds.Leatherworker,
            Stock(("leather",2)),Stock(("leather_goods",2))),
        new("craft_harness",BusinessKinds.Leatherworker,
            Stock(("leather",2),("iron_ingot",1)),Stock(("harness",2)))
    ];

    public static IEnumerable<CommodityRecipe> For(BusinessState business) =>
        All.Where(r=>r.Industry==business.Kind &&
            (business.Kind!=BusinessKinds.Workshop ||
                IsCarpenter(business)==(r.Id=="carve_furnishings")));

    public static bool IsCarpenter(BusinessState business) =>
        (business.Place.SourceType?.Contains("carpent",StringComparison.OrdinalIgnoreCase)??false) ||
        business.Name.Contains("Timber",StringComparison.OrdinalIgnoreCase);

    public static bool Edible(string sku) => Meals.Contains(sku,StringComparer.Ordinal);
}
