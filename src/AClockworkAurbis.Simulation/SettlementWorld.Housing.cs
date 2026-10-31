namespace AClockworkAurbis.Simulation;

// Hypothetical shared accommodation is separate from source-backed houses.
// A UESP location association never establishes ownership, tenancy, or a bed.
internal sealed class HousingUnit(PlaceDefinition place, int beds, int rent)
{
    public PlaceDefinition Place { get; } = place;
    public string Id => Place.Id;
    public int Beds { get; } = beds;
    public int Rent { get; } = rent;
    public int Occupied { get; set; }
    public bool Vacant => Occupied < Beds;
}

public sealed partial class SettlementWorld
{
    private readonly List<HousingUnit> _housing = [];
    private readonly Dictionary<string, HousingUnit> _housingById = new(StringComparer.Ordinal);
    private decimal _rentCollected;
    private int _relocations, _rehousings;

    private double RegionalSleepRate(ResidentState resident) =>
        resident.RoughSleeping ? 0.07 : 0.16;

    private static string Neighbourhood(PlaceDefinition? place)
    {
        if (place?.District is string district)
            return "district:" + district.ToLowerInvariant();
        if (place?.MapX is double x && place.MapY is double y)
            return "map:" + x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                + ":" + y.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return "unmapped";
    }

    private PlaceDefinition? ResidentAnchor(ResidentState resident)
    {
        string? source = resident.Source.HomePlaceId ?? resident.Source.AnchorPlaceId;
        return source is null ? null : _sourcePlaces.GetValueOrDefault(source);
    }

    private void AddHousing(PlaceDefinition place, int beds, int rent)
    {
        var unit = new HousingUnit(place, beds, rent);
        _housing.Add(unit);
        _housingById.Add(place.Id, unit);
        if (!_sourcePlaces.ContainsKey(place.Id))
        {
            _sourcePlaces.Add(place.Id, place);
            _placeNames.Add(place.Id, place.Name);
        }
    }

    // Source place names can suggest room quality, but the rent amounts
    // are game-simulation assumptions, not claimed ESO tenancy data.
    // Unclassified locations use the ordinary room price.
    internal static int SuggestedHousingRent(PlaceDefinition place,int ordinaryRent)
    {
        if(ordinaryRent==0)return 0;
        string name=place.Name;
        if(name.Contains("bunkhouse",StringComparison.OrdinalIgnoreCase) ||
            name.Contains("inn room",StringComparison.OrdinalIgnoreCase))
            return (ordinaryRent+1)/2;
        if(name.Contains("manor",StringComparison.OrdinalIgnoreCase) ||
            name.Contains("manse",StringComparison.OrdinalIgnoreCase) ||
            name.Contains("villa",StringComparison.OrdinalIgnoreCase))
            return (int)Math.Min(int.MaxValue,((long)ordinaryRent*5+1)/2);
        return ordinaryRent;
    }

    private void InitializeHousing()
    {
        foreach (var house in Catalog.Places.Where(p => p.Role == "dwelling"))
            AddHousing(house, beds: 4,
                rent: SuggestedHousingRent(house,_settings.DailySharedRent));

        // The named dwellings carry inferred price tiers, not claims of
        // source-backed property ownership or historical rent.
        // Create finite, shared beds from observed NPC neighbourhoods, NOT
        // one invisible private home per character. A modest vacancy reserve
        // permits migration without assuming unlimited free property.
        var groups = _residents.GroupBy(p => Neighbourhood(ResidentAnchor(p)))
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
        int number = 0;
        foreach (var group in groups)
        {
            var people = group.ToArray();
            var reference = people.Select(ResidentAnchor)
                .FirstOrDefault(p => p?.MapX is not null)
                ?? people.Select(ResidentAnchor).FirstOrDefault();
            int units = (people.Length + 3) / 4 + Math.Max(1, people.Length / 40);
            string district = reference?.District ?? reference?.Name ?? "unmapped Auridon";
            for (int i = 0; i < units; i++)
            {
                string id = "sim:shared-housing:" + number++;
                AddHousing(new PlaceDefinition(
                    id, 0, "Shared lodging near " + district + " #" + (i + 1),
                    "simulation_default_shared_housing", district, Catalog.Settlement,
                    "dwelling", "", reference?.MapX, reference?.MapY,
                    "simulation_at_source_neighbourhood", reference?.District ?? district),
                    beds: 4, rent: _settings.DailySharedRent);
            }
        }

        // A location associated with an NPC is not necessarily their home.
        // Prefer a documented house only when the source says npc_house.
        foreach (var person in _residents)
        {
            if (person.Source.HomePlaceId is string sourceHome &&
                _housingById.TryGetValue(sourceHome, out var home) && home.Vacant)
                Occupy(person, home, initial: true);
        }

        foreach (var person in _residents.Where(p => p.HousingId is null))
        {
            string neighbourhood = Neighbourhood(ResidentAnchor(person));
            var home = _housing.FirstOrDefault(h => h.Vacant &&
                h.Place.SourcePageId == 0 &&
                Neighbourhood(h.Place) == neighbourhood);
            // Some source coordinates do not have a named district. Fallback
            // to geometric proximity without silently expanding bed capacity.
            home ??= _housing.Where(h => h.Vacant)
                .OrderBy(h => PlaceTravelMinutes(ResidentAnchor(person), h.Place))
                .ThenBy(h => h.Id, StringComparer.Ordinal).FirstOrDefault();
            if (home is not null) Occupy(person, home, initial: true);
            else MakeHomeless(person);
        }
    }

    private void Occupy(ResidentState resident, HousingUnit unit, bool initial, bool jobMove = false)
    {
        if (!unit.Vacant) throw new InvalidOperationException("Housing capacity exceeded");
        if (resident.HousingId is string oldId &&
            _housingById.TryGetValue(oldId, out var old))
            old.Occupied--;
        unit.Occupied++;
        resident.HomeId = unit.Id;
        resident.HousingId = unit.Id;
        if (initial)
        {
            resident.CurrentPlaceId = unit.Id;
            resident.DestinationPlaceId = unit.Id;
        }
        else if (jobMove)
        {
            resident.Relocations++;
            _relocations++;
            Observe(resident, "relocate", "Moved for work to " + unit.Place.Name);
        }
        else
        {
            resident.Rehousings++;
            _rehousings++;
            Observe(resident, "rehoused", "Found an available bed at " + unit.Place.Name);
        }
    }

    private void MakeHomeless(ResidentState resident)
    {
        if (resident.HousingId is string id &&
            _housingById.TryGetValue(id, out var old))
            old.Occupied--;
        resident.HousingId = null;
        string rough = "sim:rough-sleep:" + resident.Source.Id;
        if (!_sourcePlaces.ContainsKey(rough))
        {
            var anchor = ResidentAnchor(resident);
            var place = new PlaceDefinition(rough, 0,
                "Unhoused sleeping place near " + (anchor?.District ?? anchor?.Name ?? "unknown"),
                "simulation_default_rough_sleep", anchor?.District,
                Catalog.Settlement, "dwelling", "", anchor?.MapX, anchor?.MapY,
                "simulation_at_source_neighbourhood", anchor?.District);
            _sourcePlaces.Add(rough, place);
            _placeNames.Add(rough, place.Name);
        }
        resident.HomeId = rough;
        Observe(resident, "homeless", "Lost access to a bed");
    }

    private int PlaceTravelMinutes(PlaceDefinition? from, PlaceDefinition to)
    {
        if (from?.MapX is double ax && from.MapY is double ay &&
            to.MapX is double bx && to.MapY is double by)
        {
            bool local = from.District is string a && to.District is string b &&
                string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            return _settings.BaseTravelMinutes +
                (int)Math.Ceiling(Math.Sqrt((ax-bx)*(ax-bx)+(ay-by)*(ay-by)) /
                    _settings.MapUnitsPerTravelMinute) -
                (local ? _settings.BaseTravelMinutes - _settings.LocalTravelMinutes : 0);
        }
        return from?.District is string left && to.District is string right &&
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            ? _settings.LocalTravelMinutes
            : _settings.BaseTravelMinutes + _settings.UnknownMapInterdistrictMinutes;
    }

    private int HomeCommuteMinutes(ResidentState resident, BusinessState workplace) =>
        PlaceTravelMinutes(Anchor(resident, resident.HomeId), workplace.Place);

    private HousingUnit? AvailableHousingNear(ResidentState resident, BusinessState workplace)
    {
        return _housing.Where(h => h.Vacant && h.Id != resident.HousingId &&
                (h.Place.SourcePageId == 0 || h.Id == resident.Source.HomePlaceId) &&
                PlaceTravelMinutes(h.Place, workplace.Place) <= 130)
            .OrderBy(h => PlaceTravelMinutes(h.Place, workplace.Place))
            .ThenBy(h => h.Rent).ThenBy(h => h.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private bool RelocateForJob(ResidentState resident, BusinessState employer)
    {
        if (!_settings.EnableHousing) return false;
        var room = AvailableHousingNear(resident, employer);
        if (room is null) return false;
        Occupy(resident, room, initial: false, jobMove: true);
        return true;
    }

    private void RunHousingDay()
    {
        if (!_settings.EnableHousing) return;
        // Any resident can use an actual vacant bed. Income does not
        // determine whether they have somewhere to sleep.
        foreach (var person in _residents.Where(p => p.HousingId is null))
        {
            var room = _housing.Where(h => h.Vacant &&
                (h.Place.SourcePageId == 0 || h.Id == person.Source.HomePlaceId) &&
                PlaceTravelMinutes(ResidentAnchor(person), h.Place) <= 130)
                .OrderBy(h => PlaceTravelMinutes(ResidentAnchor(person), h.Place))
                .ThenBy(h => h.Rent).ThenBy(h => h.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (room is not null) Occupy(person, room, initial: false);
        }

        if (Minute >= DayMinutes)
        {
            foreach (var person in _residents)
            {
                if (person.HousingId is not string id) continue;
                var room = _housingById[id];
                // A simple voluntary means-tested contribution: preserve
                // a cash reserve for essentials. Never bill or evict
                // anyone who cannot pay. Only real gold enters the treasury.
                if (room.Rent > 0 &&
                    person.Gold >= (decimal)room.Rent + _settings.RentGoldReserve)
                {
                    person.Gold -= room.Rent;
                    person.RentSpent += room.Rent;
                    _publicGold += room.Rent;
                    _rentCollected += room.Rent;
                }
            }
        }

        foreach (var person in _residents.Where(p => p.HousingId is null))
            person.HomelessDays++;
    }
}
