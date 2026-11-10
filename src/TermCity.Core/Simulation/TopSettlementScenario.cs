using System.Numerics;
using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

/// <summary>A reproducible mature player-scale settlement for minimum-spec performance checks.</summary>
public static class TopSettlementScenario
{
    public const int Population = 100_000;
    public const int MapSpan = 640;
    public static CellRect TownBounds => new(18, 18, 605, 605);

    internal static CityGame Create(GameConfig config, GameContent? content, DateTime? startingDate)
    {
        config = config with { MapWidth = MapSpan, MapHeight = MapSpan };
        content = GameContent.ForScenario(CityScenario.Top, content);
        var map = MapGenerator.Generate(MapSpan, MapSpan, config.Seed, content);
        var game = new CityGame(config, map, GameRandom.ForStage(config.Seed, "simulation"))
        {
            CityName = "Top Settlement", Money = 10_000_000, GrainWeeks = 30,
            Week = startingDate is { } date ? Math.Min(config.WeeksPerYear - 1, (date.DayOfYear - 1) / 7) : 0,
            HighestPopulation = Population, HighestMilestone = 10_000, Paused = true,
        };
        foreach (var cell in TownBounds.Cells())
        {
            map.ClearCell(cell.X, cell.Y);
            map.SetTerrain(cell.X, cell.Y, content.Terrains.Base);
        }
        foreach (var cell in new CellRect(12, 12, 616, 616).Cells())
            if (!TownBounds.Contains(cell))
            {
                map.ClearCell(cell.X, cell.Y);
                map.SetTerrain(cell.X, cell.Y, content.Terrains.Base);
            }
        var track = content.Roads.Get("King's Road");
        for (int street = 0; street <= 11; street++)
        {
            int at = 18 + street * 55 - 3;
            if (at < 18 || at >= TownBounds.Right) continue;
            for (int along = 18; along <= TownBounds.Right; along++)
            {
                map.SetRoad(at, along, track);
                map.SetRoad(along, at, track);
            }
        }
        var plots = new List<Pos>();
        for (int row = 0; row < 55; row++)
            for (int column = 0; column < 55; column++)
                plots.Add(new(18 + column / 5 * 55 + column % 5 * 10,
                    18 + row / 5 * 55 + row % 5 * 10));
        bool Free(BuildingType type, Pos at) => type.FootprintAt(at.X, at.Y).Cells().All(cell =>
            TownBounds.Contains(cell) && map.BuildingAt(cell.X, cell.Y) is null && !map.HasRoad(cell.X, cell.Y));
        void Place(BuildingType type, Pos at)
        {
            var area = type.FootprintAt(at.X, at.Y);
            foreach (var cell in area.Cells()) map.SetZone(cell.X, cell.Y, type.Zone);
            map.SetBuildingFootprint(type, area);
            map.BuildingEstablishedDays.Remove(map.Index(at.X, at.Y));
        }
        // Established dispatch and utility provision use normal definitions, not benchmark-only service multipliers.
        string[] care = ["Fire Watch", "Gaol", "Infirmary", "Monastery", "Tavern", "Castle", "Cathedral", "Guildhall"];
        for (int gy = 80; gy < 620; gy += 160)
            for (int gx = 80; gx < 620; gx += 160)
            {
                foreach (string name in care)
                {
                    var type = content.Buildings.Get(name);
                    var site = plots.OrderBy(at => Math.Abs(at.X - gx) + Math.Abs(at.Y - gy)).FirstOrDefault(at => Free(type, at), new Pos(-1, -1));
                    if (site.X < 0) throw new InvalidOperationException($"TOP cannot place {name}.");
                    Place(type, site);
                }
            }
        foreach (var (name, count) in new[] { ("Charcoal Burners", 45), ("Town Well", 150), ("Granary", 12),
            ("Archer Barracks", 8), ("Guard Post", 8), ("Tavern", 120), ("Fire Watch", 32), ("Infirmary", 8) })
        {
            var type = content.Buildings.Get(name);
            int placed = 0;
            foreach (var at in plots.OrderBy(at => CellHash.Pick(at.X, at.Y, 1_000_003)))
            {
                if (placed == count) break;
                foreach (var offset in type.Width == 5 && type.Height == 5
                    ? new[] { new Pos(0, 0), new Pos(5, 0), new Pos(0, 5), new Pos(5, 5) } : new[] { new Pos(0, 0) })
                {
                    var site = at.Offset(offset.X, offset.Y);
                    if (placed == count) break;
                    if (!Free(type, site)) continue;
                    Place(type, site);
                    placed++;
                }
            }
            if (placed != count) throw new InvalidOperationException($"TOP requires {count} {name}, placed {placed}.");
        }
        int remaining = Population;
        var home = content.Buildings.Get("Tenement");
        foreach (var at in plots.OrderBy(at => Math.Abs(at.X - 320) + Math.Abs(at.Y - 320)))
        {
            if (remaining == 0) break;
            if (!Free(home, at)) continue;
            Place(home, at);
            int people = Math.Min(remaining, home.Capacity);
            int adults = people * 5 / 12, children = people * 5 / 12;
            map.SetHousehold(at.X, at.Y, new((byte)adults, (byte)children, (byte)(people - adults - children)));
            remaining -= people;
        }
        if (remaining != 0) throw new InvalidOperationException($"TOP is missing capacity for {remaining} residents.");
        foreach (var at in plots)
        {
            var type = content.Buildings.Get(CellHash.Pick(at.X, at.Y, 8) == 0 ? "Great Forge" : "Market Hall");
            if (Free(type, at)) Place(type, at);
        }
        var state = new SettlementState(new(288, 288, 64, 64), BaileyGateSide.Bottom)
        {
            Material = WallMaterial.ReinforcedStone, WaveNumber = 1, NextWaveSeconds = 120,
            OrcsIntroduced = true, TrollsIntroduced = true,
        };
        state.ClearWallNetwork();
        game.Settlement = state;
        state.BindGarrisonGame(game);
        state.EstablishedLand.UnionWith(TownBounds.Cells());
        state.ChangeWalls(() =>
        {
            const int edge = 16, far = 624, step = 19;
            var corners = new[] { new Vector2(edge, edge), new Vector2(far, edge), new Vector2(far, far), new Vector2(edge, far) };
            for (int side = 0; side < 4; side++)
                for (int piece = 0; piece < 32; piece++)
                {
                    var a = Vector2.Lerp(corners[side], corners[(side + 1) % 4], piece / 32f);
                    var b = Vector2.Lerp(corners[side], corners[(side + 1) % 4], (piece + 1) / 32f);
                    bool gate = piece == 16;
                    state.AddWall(new WallComponent
                    {
                        Id = state.NextWallId++, Cell = new((int)((a.X + b.X) / 2), (int)((a.Y + b.Y) / 2)),
                        Start = a, End = b, BuiltMaterial = WallMaterial.ReinforcedStone,
                        BelongsToFoundingWall = true, Health = step * 800, MaxHealth = step * 800,
                        IsGate = gate, Defense = gate ? WallDefenseKind.None : piece % 4 == 0 ? WallDefenseKind.Ballista : WallDefenseKind.None,
                    });
                    if (piece % 4 == 0)
                        state.AddWall(new WallComponent
                        {
                            Id = state.NextWallId++, Cell = new((int)a.X, (int)a.Y),
                            Start = a, End = a + Vector2.Normalize(b - a),
                            IsTower = true, JunctionTower = true, BelongsToFoundingWall = true,
                            BuiltMaterial = WallMaterial.ReinforcedStone, Health = 800, MaxHealth = 800,
                        });
                }
        });
        foreach (var area in map.BuildingFootprints.Values.Where(area => SettlementState.RosterCapacity(map.BuildingAt(area.X, area.Y)!) > 0))
            for (int member = 0; member < 4; member++)
                state.DefenderList.Add(new SettlementDefender
                {
                    Id = state.NextDefenderId++, Home = new(area.X, area.Y),
                    Kind = map.BuildingAt(area.X, area.Y)!.Name == "Archer Barracks" ? DefenderKind.Archer : DefenderKind.Soldier,
                    X = area.Right + 1.5, Y = area.Y + member + .5, PreviousX = area.Right + 1.5, PreviousY = area.Y + member + .5,
                });
        game.Taxes.Residential = .03;
        game.Taxes.Commercial = .05;
        game.Taxes.Industrial = .05;
        game.Touch();
        if (game.Stats.Population != Population) throw new InvalidOperationException($"TOP population is {game.Stats.Population}, not 100,000.");
        return game;
    }
}
