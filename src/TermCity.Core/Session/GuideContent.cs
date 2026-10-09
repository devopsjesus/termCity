using TermCity.Core.Buildings;
using TermCity.Core.Effects;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Core.Session;

/// <summary>The pages of the GUIDE dialog: how to play, and how each thing you place drives the town's population.</summary>
public static class GuideContent
{
    /// <summary>Longest line, in characters, of any page: the dialog is sized to fit it.</summary>
    public const int MaxLineLength = 100;

    public static IReadOnlyList<PromptTab> Tabs(CityGame game, bool showNextStep = true) =>
    [
        new("Start", Start(game, showNextStep)),
        new("Zones", Zones(game)),
        new("Roads", Roads(game)),
        new("Services", Services(game)),
        new("Population", Population(game)),
        new("Happiness", Happiness()),
        new("Economy", Economy()),
        new("Glossary", Glossary(game)),
    ];

    private static string Join(params string[] blocks) => string.Join("\n\n", blocks.Where(block => block.Length > 0));

    private static string Table(TableColumn[] columns, IEnumerable<string[]> rows) =>
        TextTable.Text(columns, rows.Select(r => (IReadOnlyList<string>)r).ToArray());

    private static string Start(CityGame game, bool showNextStep)
    {
        int homes = game.Config.MinResidentialCells;
        return Join(
            showNextStep ? "YOUR NEXT STEP\n" + CityProgression.NextStep(game) : "",
            "A town grows when people have somewhere to live, work to do, a road to reach both and\n" +
            "the services they need. You steer it by marking land, laying roads and building.",
            Table(["STEP", "KEYS", "WHAT TO DO"],
            [
                ["1 Road", "T", "Draw a track from the King's Road at the map edge into open land"],
                ["2 Homes", "R", "Mark homesteads along the track: families settle only by a road"],
                ["3 Supplies", "Enter", game.Config.FullRules ? "Services: Woodlot (8,000g) and Town Well (4,000g), beside a road" : "Classic rules do not require fuel or water"],
                ["4 Go", "P", $"Resume. At {homes} occupied homes zone jobs with C and I"],
                ["5 Care", "Enter", "Spend the weekly surplus on local care; save for a Motte and Bailey"],
                ["6 Watch", "F7  F8", "City Grew! unlocks and growth blockers; expand supplies before homes"],
            ]),
            "CITY GREW!\n" +
            "  Population milestones permanently unlock larger service buildings, even if people leave.\n" +
            "  Dismiss the Start-tab tip to turn off coaching, not milestones. F6 can enable it again.\n" +
            "  F7 lists every milestone and its unlocks. New-city-size announcements pause the clock.",
            "A SUSTAINABLE START\n" +
            "  Keep at least 8,000g to replace a lost Woodlot, plus money for winter grain and tribute.\n" +
            "  Fuel and water work anywhere by a connected road; local care must be close to homes.\n" +
            "  Zone smoky workshops in a separate district, not between homes. Shops can stay nearby.\n" +
            "  At 100 souls, save for the largest complaint (F6, Happiness); do not buy every service.\n" +
            "  Try 75% local-service funding in Budget: less upkeep, still about 84% of the benefit.\n" +
            "  When space runs short, extend short tracks into open land. Leave plots for care, or\n" +
            "  dezone empty lots (U) to fit it. Do not wait for every poorly served vacant home to fill.",
            "SELECTING\n" +
            "  Click a cell, or drag a box. Shift+click grows the selection to the cell you click.\n" +
            "  Ctrl/Alt+drag starts a new box. Shift+arrows (or S, arrows, S) select by keyboard.\n" +
            "  Every action (zone, road, build, demolish) applies to the selection or the cursor cell.",
            "DIALOGS\n" +
            "  Underlined letters pick a row, Up/Down move, Enter selects, Esc closes. Shift+click or\n" +
            "  Shift+Enter opens option help on the right without activating it. Shift+letter still selects.\n" +
            "  Left/Right (or a click) changes guide tabs. F1 is the short list of controls.");
    }

    public static string Glossary(CityGame game)
    {
        var lines = new List<string>
        {
            "ICON / GLYPH GLOSSARY",
            "Glyphs can have several meanings: colour, layer and context distinguish them.",
            "Roads are smooth curves on the map; connection glyphs appear in overlays and the minimap.",
            "",
        };
        void Add(string glyphs, string meaning)
        {
            string remaining = $"{glyphs} — {meaning}";
            while (remaining.Length > MaxLineLength)
            {
                int split = remaining.LastIndexOf(' ', MaxLineLength);
                if (split < 1) split = MaxLineLength;
                lines.Add(remaining[..split]);
                remaining = remaining[split..].TrimStart();
            }
            lines.Add(remaining);
        }
        foreach (var terrain in game.Map.Content.Terrains)
            Add(ChoiceHelpContent.Icons(terrain.Glyphs), $"{terrain.Name}: {terrain.Description}");
        foreach (var feature in game.Map.Content.Features)
            Add(ChoiceHelpContent.Icons(feature.Glyphs), $"{feature.Name}: {feature.Description}");
        foreach (var zone in World.Zones.Placeable.Select(World.Zones.Get))
            Add($"{zone.Letter} {zone.EmptyGlyph}", $"{zone.Name}: demand letter / vacant zoned land");
        foreach (var road in game.Map.Content.Roads)
            Add(ChoiceHelpContent.Icons(road.Glyphs), $"{road.Name}: {road.Description}");
        foreach (var building in game.Map.Content.Buildings)
        {
            Add(ChoiceHelpContent.Icons(building.Glyphs), $"{building.Name}: {building.Description}");
            if (building.FootprintArt.Count > 0)
            {
                Add(ChoiceHelpContent.Icons(building.FootprintArt.SelectMany(row => row.Select(c => c.ToString()))
                    .Where(c => !string.IsNullOrWhiteSpace(c))), $"{building.Name}: multi-cell footprint art");
            }
            foreach (var area in AreaOfEffect.ForBuilding(building, new(0, 0)))
                Add(ChoiceHelpContent.Icons(area.Glyphs), $"{building.Name}: area-of-effect ring, radius {area.Radius} cells" +
                    (building.Pollution != 0 ? "; ♧ cleans air, Ψ smoke pollution" : ""));
        }
        lines.Add("");
        lines.Add("ANIMATED EFFECTS (not additional buildings or resources)");
        Add(ChoiceHelpContent.Icons(EffectGlyphs.BuildBars.Select(c => c.ToString())), "Construction progress bars");
        foreach (var (glyphs, meaning) in new (IEnumerable<string>, string)[]
        {
            (EffectGlyphs.Dust, "Building / demolition dust"),
            (EffectGlyphs.Sparkle, "Sparkles"),
            (EffectGlyphs.Flame, "Fire"),
            (EffectGlyphs.Smoke, "Smoke"),
            (EffectGlyphs.Ripple, "Water ripples"),
            (EffectGlyphs.Confetti, "Celebration confetti (when enabled)"),
            (EffectGlyphs.Coin, "Reserved coin-effect glyphs; money animations are not active in gameplay"),
            (EffectGlyphs.Bird, "Birds"),
            (EffectGlyphs.Spout, "Whale spout"),
            (EffectGlyphs.Bubble, "Water bubbles"),
            ([EffectGlyphs.FishRight, EffectGlyphs.FishLeft], "Swimming fish"),
            ([EffectGlyphs.WhaleBack], "Whale back"),
            ([EffectGlyphs.Person], "Walking person"),
            ([EffectGlyphs.Car], "Road traffic"),
            ([EffectGlyphs.Crack], "Quake cracks"),
        }) Add(ChoiceHelpContent.Icons(glyphs), meaning);
        return string.Join("\n", lines);
    }

    private static string Zones(CityGame game) => Join(
        "Zoning is free. Mark land and buildings appear by themselves where a road reaches\n" +
        $"(within {game.Config.RoadServiceReach} cells). Zoning does not build anything on its own: it invites.",
        Table(["ZONE (KEY)", "GROWS", "WHAT IT IS FOR"],
        [
            ["Homesteads (R)", "Cottage > Burgage House > Tenement", "Homes: only homes hold people"],
            ["Marketplace (C)", "Market Stall > Merchant House > Market Hall", "Jobs, shops and market tolls"],
            ["Craftworks (I)", "Workshop > Mill > Great Forge", "Jobs and dues; smoke annoys"],
        ]),
        "ROOM FOR PEOPLE\n" +
        "  A cottage houses 6 souls, a burgage house 18, a tenement 48. A taller building needs a big\n" +
        "  enough town (500 souls for the second level, 5,000 for the third), rising land value,\n" +
        "  fuel and water, a lord's seat (motte, then keep) and a cobbled road for the third level.",
        "UNLOCKS\n" +
        $"  Markets and workshops appear only once {game.Config.MinResidentialCells} homes are occupied: people first, trade follows.\n" +
        "  Keep the three in step: homes without jobs go idle, and jobs without homes sit empty.",
        "CHANGING YOUR MIND\n" +
        "  U dezones. Buildings on dezoned land leave over 2 to 3 weeks; zone it again to keep them.\n" +
        "  D (or Delete) demolishes for free, with no refund.");

    private static string Roads(CityGame game)
    {
        var rows = game.Map.Content.Roads.Where(r => r.PlayerPlaceable).OrderBy(r => r.Rank).Select(road => new[]
        {
            road.Name,
            Fmt.Money((int)Math.Round(game.Config.RoadCostPerCell * road.CostMultiplier)),
            Fmt.Money(road.WeeklyUpkeep),
            road.TrafficCapacity.ToString(),
        });
        return Join(
            "Roads are how the town works. A home or shop that no road reaches stays empty, whatever\n" +
            $"you zone. Reach means a road within {game.Config.RoadServiceReach} cells.",
            TextTable.Text(
                [new("ROAD"), TableColumn.Right("COST/CELL"), TableColumn.Right("UPKEEP/WK"), TableColumn.Right("TRAFFIC")],
                rows.Select(r => (IReadOnlyList<string>)r).ToArray()),
            "LAYING ROADS\n" +
            "  T draws a line at any angle: place the first end, move or drag the second, Enter confirms.\n" +
            "  B previews the selected cells with the default track. The area menu has every road type.\n" +
            "  Every road type is drawn as smooth curves that join one another, whatever the angle.\n" +
            "  Roads cross and branch but never run side by side: a new road may meet another only end-on\n" +
            "  or at a crossing, and crossings keep apart, so cells that would hug a road are skipped.",
            "THE KING'S ROAD\n" +
            "  The double-lined highways run to the map edges and carry trade in from outside. Join your\n" +
            "  tracks to one: a town cut off from the edge cannot grow. F8 shows which plots lack access.",
            "TRAFFIC\n" +
            "  Each road type carries a load. When a town outgrows its roads, traffic costs happiness:\n" +
            "  upgrade busy tracks to cobbled roads or build a second route.");
    }

    private static string Services(CityGame game)
    {
        TableColumn[] Columns(string reach) =>
        [
            "BUILDING", "SERVES", TableColumn.Right("COST"), TableColumn.Right("UPKEEP"), TableColumn.Right(reach), "NEEDS",
        ];
        string[] Row(BuildingType b) =>
        [
            b.Name, CityReport.ServiceName(b.Service), Fmt.Money(b.Cost), Fmt.Money(b.WeeklyUpkeep),
            b.Radius > 0 ? b.Radius.ToString() : b.Capacity > 0 ? $"{b.Capacity:N0} units" : string.Empty,
            b.MinPopulation > 0 ? $"{CityProgression.RequiredPopulation(b):N0} souls" : string.Empty,
        ];
        var placeable = game.Map.Content.Buildings.Where(b => b.PlayerPlaceable && b.Service != ServiceKind.None).ToList();
        var supplies = placeable.Where(b => b.Service.IsUtility()).Select(Row);
        var civic = placeable.Where(b => !b.Service.IsUtility()).OrderBy(b => b.Service).ThenBy(b => b.Cost).Select(Row);
        return Join(
            "Place these from the area menu (right-click or Enter, then Service buildings). Choose one,\n" +
            "move it with the mouse or arrows, then click or press Enter.\n" +
            "Upkeep is paid weekly; reach is measured in map cells.",
            "SUPPLIES (city-wide)\n" + Table(Columns("OUTPUT"), supplies),
            "  Fuel and water are shared by the whole town. If supply falls short of what buildings need,\n" +
            "  a matching share of blocks goes cold or dry. An aqueduct must stand on a shore.\n" +
            "  Start with a Woodlot and Town Well (12,000g together, 60g/week). Larger plants can wait.",
            "SERVICES (local)\n" + Table(Columns("REACH"), civic),
            "  Each covers the cells around it, strongest close by. Services fund at 100% by default;\n" +
            "  the Budget menu can trade money for strength.");
    }

    private static string Population(CityGame game)
    {
        var cfg = game.Config;
        return Join(
            "HOW THE TOWN FILLS UP\n" +
            $"  Each week {cfg.MaxNewResidentialPerWeek} families arrive, plus about {cfg.MigrationRatePerWeek:P1} of the larger of your occupied homes\n" +
            "  or a fifth of your people, so a bigger town draws more. That flow is multiplied by the\n" +
            "  town's ATTRACTION: comfort x jobs x appeal (0, nobody comes, to 2, a boom), lifted by a\n" +
            "  lord's seat and eased by winter.",
            "WHAT YOU PLACE, AND WHAT IT DOES",
            Table(["PLACE", "EFFECT ON POPULATION"],
            [
                ["Roads", $"Gate everything: homes must lie within {cfg.RoadServiceReach} cells of a road joined to the edge"],
                ["Homesteads", "Are the people: more occupied homes, more arrivals, more births and room to grow"],
                ["Marketplace", "Gives jobs and trade. Plentiful work lifts attraction; unemployment lowers it"],
                ["Craftworks", "Gives jobs and income, but smoke lowers happiness and land value nearby"],
                ["Fuel, water", "Without either, homes lose 30 or 25 happiness: the biggest single hit"],
                ["Fire, sheriff", "Make homes safer; needed once the town reaches 300 and 600 souls"],
                ["Physic", "Cuts deaths and plague; needed from 700 souls"],
                ["Learning", "Schools lift wages and tithes; needed from 450 souls"],
                ["Greens, tavern", "Cheer and cool tempers; needed from 900 souls"],
                ["Chapel, church", "Solace for the faithful, and a gentler temper; wanted from 500 souls"],
                ["Keep, castle", "Guard against raiders and draw settlers; wanted from 400 souls"],
                ["Granary", "Keeps hunger off in a bad year; hunger drives people away"],
                ["Market Cross", "Cheaper grain and busier trade, so richer, steadier growth"],
            ]),
            "WHEN IT STALLS\n" +
            "  Under happiness 32 nobody new arrives. Under 42 families leave (so do many if over 12%\n" +
            "  are jobless); under 40 an empty home is abandoned.\n" +
            "  The City health report (Esc, Health) names the top complaint; fix that one first.",
            "RANK\n" +
            "  Hamlet, Village (120), Market Town (800), Borough (5,000), City (25,000). Each rank lifts\n" +
            "  shop and workshop tithes by 3%. A market (Cross or Guildhall) must cover the town for\n" +
            "  Market Town; a Borough needs a lord's seat; a City a Stone Keep and a church.");
    }

    private static string Happiness() => Join(
        "Each home starts at 70 happiness and loses points for every unmet need. Comfort runs from\n" +
        "32 (nobody arrives) to 66 (a normal flow), and higher still beyond.",
        Table(["CHARGE", "POINTS", "REMEDY"],
        [
            ["No fuel", "30", "Charcoal burners or woodlots"],
            ["No water", "25", "Town wells; an aqueduct on a shore"],
            ["Unemployment", "45 x (rate - 6%)", "Zone more markets and craftworks"],
            ["Traffic", "up to 18", "Better roads, a second route"],
            ["Fire, health, learning", "up to 12, 14, 9", "Fire watch, apothecary, chantry school"],
            ["Greens", "up to 8", "Village green or tavern"],
            ["Crime", "0.28 x felt crime", "Watch house, sheriff; greens and faith help"],
            ["Smog", "scaled by smoke", "Fewer smoky works, more greens, woodlots not charcoal"],
            ["Hunger", "45 x hunger", "Stores and gold both run out: granary, market cross"],
            ["Unguarded", "7", "A motte, keep or castle covering the homes"],
            ["No solace", "4", "A chapel or church in a town big enough to want one"],
            ["Taxes", "-4 to 20", "Lower the tax towards the fair rate (Budget)"],
            ["No road access", "10", "Join the plot to a road"],
        ]),
        "Service charges grow with how much of the town's need is unmet, so a hamlet is not punished\n" +
        "for lacking a sheriff it does not yet need. Press O to see fuel, water, services, smoke, land\n" +
        "value and contentment painted over the map.");

    private static string Economy() => Join(
        "MONEY\n" +
        "  Income is tithes and rents: hearth tithe on homes, market tolls on shops and guild dues\n" +
        "  on workshops, each a share of the plot's value, cut by unemployment and empty jobs.\n" +
        "  Outgoings are upkeep of services and roads, loan interest and the reeve's administration.",
        "  Roads already on the map have no weekly upkeep; you maintain every road you build.\n" +
        "  Grain purchases, tribute and raid losses also take gold; keep a reserve beyond weekly upkeep.",
        "BUDGET (Esc, Budget)\n" +
        "  Select a service to step its funding down by quarters: half funding gives about two thirds of\n" +
        "  the benefit. Taxes above the fair rate cost happiness. Loans: up to 40 weeks of income at\n" +
        "  0.2% a week. Fuel and water are always paid in full.",
        "THE YEAR",
        Table(["SEASON", "WEEKS", "WHAT TO EXPECT"],
        [
            ["Winter", "49 to 9", "Travel slows; stores are eaten"],
            ["Spring", "10 to 22", "Fields are sown"],
            ["Summer", "23 to 35", "The season of plague and fire; keep watches paid"],
            ["Autumn", "36 to 48", "Harvest fills the grain store; Michaelmas (week 39) brings the tribute"],
        ]),
        "GRAIN, TRIBUTE AND TROUBLE\n" +
        "  Grain is stored at harvest. When it runs out the town buys from merchants, and with neither\n" +
        "  nor gold, hunger kills and drives people out. At Michaelmas the crown's reeve takes about 2g\n" +
        "  a soul, less with a lord's seat and a charter (towns under 150 are below notice); a\n" +
        "  shortfall becomes arrears and emboldens raiders.\n" +
        "  Fires, plague (from 250 souls), raiders (from 150), floods and quakes strike now and then.");
}
