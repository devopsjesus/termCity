using System.Diagnostics;
using TermCity.Core.Buildings;
using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using Xunit.Abstractions;

namespace TermCity.Tests;

/// <summary>Accelerated play, using only paid player actions and the default full simulation.</summary>
public class GuidedPlaythroughTests(ITestOutputHelper output)
{
    [Theory]
    [Trait("Category", "Playthrough")]
    [InlineData("small", 42)]
    [InlineData("medium", 42)]
    [InlineData("large", 42)]
    [InlineData("small", 137)]
    [InlineData("medium", 137)]
    [InlineData("large", 137)]
    public void GuideCanReachGreatCityWithoutLoansOrFreeBuildings(string sizeName, int seed)
    {
        var size = MapSize.Presets[sizeName];
        var clock = Stopwatch.StartNew();
        var game = CityGame.New(new GameConfig { MapWidth = size.Width, MapHeight = size.Height, Seed = seed });
        var center = new Pos(size.Width / 2, size.Height / 2);
        var sites = game.Map.RoadCells.Select(game.Map.PosOf)
            .Where(p => game.Network.IsConnected(game.Map, p.X, p.Y))
            .OrderBy(p => Distance(p, center)).ToArray();
        Assert.NotEmpty(sites);
        center = sites[0];
        int minimumGold = game.Money;
        int lastMilestone = 0;
        int purchases = 0;
        int roads = 0;

        bool Build(string name, Pos near, int reserve = 8_000)
        {
            var type = game.Map.Content.Buildings.Get(name);
            if (!CityProgression.IsUnlocked(game, type)) return false;
            var options = new List<Pos>();
            for (int y = Math.Max(0, near.Y - 30); y < Math.Min(size.Height, near.Y + 31); y++)
                for (int x = Math.Max(0, near.X - 30); x < Math.Min(size.Width, near.X + 31); x++)
                    if (game.Network.IsServed(game.Map.Index(x, y)) &&
                        new CellRect(x, y, type.Width, type.Height).Cells().All(p =>
                            game.Map.InBounds(p) && game.Map.TerrainAt(p.X, p.Y).Buildable &&
                            !game.Map.HasRoad(p.X, p.Y) && game.Map.BuildingAt(p.X, p.Y) is null &&
                            !game.Network.RoadOverlapsCell(game.Map.Index(p.X, p.Y))))
                        options.Add(new Pos(x, y));
            if (options.Count == 0 && type.Service.IsUtility())
                for (int y = 0; y < size.Height; y++)
                    for (int x = 0; x < size.Width; x++)
                        if (game.Network.IsServed(game.Map.Index(x, y)) && game.CanPlaceBuilding(type, x, y))
                            options.Add(new Pos(x, y));
            foreach (var p in options.OrderBy(p => Distance(p, near)))
            {
                var area = CellRect.Single(p);
                int cost = game.BuildingCostAt(type, p.X, p.Y);
                if (game.Money < cost + reserve) continue;
                var footprint = game.BuildingPlacementArea(type, area);
                if (footprint.Cells().Any(c => game.Map.ZoneAt(c.X, c.Y) != ZoneType.None))
                    Assert.True(game.Dezone(footprint).Success);
                var result = game.PlaceBuilding(type, area);
                Assert.True(result.Success, result.Message);
                purchases++;
                return true;
            }
            return false;
        }

        void Zone(ZoneType zone, int wanted)
        {
            if (wanted <= 0) return;
            var near = zone == ZoneType.Industrial
                ? new Pos(center.X < size.Width / 2 ? Math.Min(size.Width - 1, center.X + 45) : Math.Max(0, center.X - 45), center.Y)
                : center;
            var options = new List<Pos>();
            for (int y = 0; y < size.Height; y++)
                for (int x = 0; x < size.Width; x++)
                    if (x % 8 >= 3 && game.CanBuildOn(x, y) && game.Network.IsServed(game.Map.Index(x, y)))
                        options.Add(new Pos(x, y));
            foreach (var p in options.OrderBy(p => Distance(p, near)).Take(wanted))
                Assert.True(game.Designate(CellRect.Single(p), zone).Success);
        }

        bool ExpandRoads()
        {
            if (game.Money < 16_000 || game.Finance.Net < 200) return false;
            foreach (var start in game.Map.RoadCells.Select(game.Map.PosOf).OrderBy(p => Distance(p, center)))
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var line = Enumerable.Range(0, 13).Select(n => new Pos(start.X + n * dx, start.Y + n * dy)).ToArray();
                    if (!line.All(p => game.Map.InBounds(p) && game.Map.TerrainAt(p.X, p.Y).Buildable &&
                        game.Map.ZoneAt(p.X, p.Y) == ZoneType.None && game.Map.BuildingAt(p.X, p.Y) is null) ||
                        line.Count(p => !game.Network.IsServed(game.Map.Index(p.X, p.Y))) < 6) continue;
                    var quote = game.QuoteRoad(line);
                    if (quote.Cells < 10 || game.Money < quote.Cost + 8_000) continue;
                    Assert.True(game.BuildRoad(line).Success);
                    roads += quote.Cells;
                    return true;
                }
            return false;
        }

        Assert.True(Build("Woodlot", center, reserve: 0));
        Assert.True(Build("Town Well", center, reserve: 0));
        foreach (var kind in ServiceKinds.Area) game.SetFunding(kind, 0.75);
        Zone(ZoneType.Residential, 30);

        for (int week = 0; week < 4_000 && game.HighestMilestone < 10_000; week++)
        {
            int pop = game.Stats.Population;
            int reserve = Math.Max(8_000, pop * 5);
            if (game.Services.Power.Supply < game.Services.Power.Demand + 60)
                Build("Woodlot", center, game.Services.Power.Supply == 0 ? 0 : reserve);
            if (game.Services.Water.Supply < game.Services.Water.Demand + 60)
                Build("Town Well", center, game.Services.Water.Supply == 0 ? 0 : reserve);
            if (game.Stats.Residential.Occupied >= game.Config.MinResidentialCells)
            {
                Zone(ZoneType.Commercial, game.SupportedCells(ZoneType.Commercial) + 3 - game.Stats.Commercial.Zoned);
                Zone(ZoneType.Industrial, game.SupportedCells(ZoneType.Industrial) + 5 - game.Stats.Industrial.Zoned);
            }
            if (pop >= 300 && week % 4 == 0 && game.Stats.Residential.Zoned - game.Stats.Residential.Filled < 60)
            {
                if (ExpandRoads()) Zone(ZoneType.Residential, 30);
            }
            if (pop >= 150 && game.Services.SeatRank == 0 && game.Finance.Net > 180)
                Build("Motte and Bailey", center, reserve);
            if (pop >= 500 && game.Services.SeatRank < 2 && game.Finance.Net > 600)
                Build("Stone Keep", center, reserve);
            if (pop >= 3_000 && game.Services.SeatRank < 3 && game.Finance.Net > 3_000)
                Build("Castle", center, reserve);

            (string Name, ServiceKind Kind, string Reason)[] care =
            [
                ("Fire Watch", ServiceKind.Fire, "Fire watch"),
                ("Apothecary", ServiceKind.Health, "Physic"),
                ("Chantry School", ServiceKind.Education, "Learning"),
                ("Village Green", ServiceKind.Recreation, "Commons"),
                ("Watch House", ServiceKind.Police, "Lawlessness"),
                ("Chapel", ServiceKind.Faith, "Solace"),
                ("Motte and Bailey", ServiceKind.Defence, "Unguarded"),
            ];
            foreach (var (name, kind, reason) in care.OrderByDescending(c =>
                game.Indicators.Complaints.FirstOrDefault(p => p.Reason == c.Reason)?.Points ?? 0))
            {
                var type = game.Map.Content.Buildings.Get(name);
                if (pop < 100 || game.Finance.Net < type.WeeklyUpkeep * 0.9 ||
                    game.Indicators.CoverageOf(kind) >= 80) continue;
                var homes = game.Map.ZoneCells(ZoneType.Residential)
                    .Where(i => !game.Map.HouseholdLayer[i].IsEmpty)
                    .OrderBy(i => game.Services.Coverage(kind, i)).ToArray();
                bool built = false;
                foreach (int home in homes.Take(5))
                    if (Build(name, game.Map.PosOf(home), reserve)) { built = true; break; }
                if (built || game.Money < type.Cost * 1.5 + reserve) break;
            }
            if (pop >= 200 && game.Indicators.CoverageOf(ServiceKind.Trade) < 40 && game.Finance.Net > 200)
                Build("Market Cross", center, reserve);

            int vacant = game.Stats.Residential.Zoned - game.Stats.Residential.Filled;
            if (week % 26 == 0 && pop >= 300 && game.Money > reserve + 80_000)
            {
                int row = (week / 26 * 5 + center.Y) % size.Height;
                var line = new CellRect(0, row, size.Width, 1);
                var quote = game.QuoteRoad(line);
                if (quote.Cost > 0 && game.Money >= quote.Cost + reserve)
                {
                    Assert.True(game.BuildRoad(line).Success);
                    roads += quote.Cells;
                    Zone(ZoneType.Residential, 60);
                }
            }
            if (vacant < 30 && game.Services.Power.Ratio >= 1 && game.Services.Water.Ratio >= 1)
            {
                Zone(ZoneType.Residential, 60 - vacant);
                if (game.Stats.Residential.Zoned - game.Stats.Residential.Filled < 30 && game.Money > reserve + 20_000)
                {
                    int row = (week / 10 * 5 + center.Y) % size.Height;
                    var line = new CellRect(0, row, Math.Min(size.Width, 160), 1);
                    var quote = game.QuoteRoad(line);
                    if (quote.Cost > 0 && game.Money >= quote.Cost + reserve)
                    {
                        Assert.True(game.BuildRoad(line).Success);
                        roads += quote.Cells;
                    }
                }
            }
            game.AdvanceWeek();
            minimumGold = Math.Min(minimumGold, game.Money);
            if (game.HighestMilestone > lastMilestone)
            {
                lastMilestone = game.HighestMilestone;
                output.WriteLine($"{sizeName}/{seed}: week {game.Week}, milestone {lastMilestone}, pop {game.Stats.Population}, gold {game.Money}, net {game.Finance.Net}, happiness {game.Indicators.Happiness:F1}");
            }
            if (game.Week % 200 == 0 || game.Week <= 100 && game.Week % 10 == 0 || game.Money <= 0)
            {
                output.WriteLine($"CHECK week {game.Week}: pop {game.Stats.Population}, gold {game.Money}, net {game.Finance.Net}, happiness {game.Indicators.Happiness:F1}, unemployment {game.Indicators.Unemployment:P0}, fuel {game.Services.Power.Ratio:F2}, water {game.Services.Water.Ratio:F2}, vacant {game.Stats.Residential.Zoned - game.Stats.Residential.Filled}");
                output.WriteLine("Complaints: " + string.Join(", ", game.Indicators.Complaints.Select(c => $"{c.Reason} {c.Points:F1}")));
                output.WriteLine("Services: " + string.Join(", ", game.Map.ServiceCells.Select(i => game.Map.Content.Buildings[game.Map.BuildingLayer[i]].Name).GroupBy(n => n).Select(g => $"{g.Key} x{g.Count()}")));
                if (game.Money <= 0)
                    output.WriteLine("Events: " + string.Join("; ", game.Events.TakeLast(5).Select(e => e.Message)));
            }
            Assert.True(game.Money > 0, $"Treasury failed in {sizeName}/{seed}, week {game.Week}.");
            if (game.Week == 100)
                game = SaveGameStore.Deserialize(SaveGameStore.Serialize(game));
        }

        clock.Stop();
        output.WriteLine($"RESULT {sizeName}/{seed}: {clock.Elapsed.TotalSeconds:F2}s execution; {game.Week} weeks; {game.Week * game.Config.FastSecondsPerWeek / 60:F2} fast-speed minutes; pop {game.Stats.Population}; gold {game.Money}; minimum gold {minimumGold}; net {game.Finance.Net}; happiness {game.Indicators.Happiness:F1}; {purchases} paid buildings; {roads} paid road cells.");
        Assert.Equal(10_000, game.HighestMilestone);
        Assert.True(game.Finance.Net > 0);
        Assert.Equal(0, game.Budget.Loan);
        Assert.All(game.Map.Content.Buildings.Where(b => b.PlayerPlaceable), b => Assert.True(CityProgression.IsUnlocked(game, b)));
        string? artifacts = Environment.GetEnvironmentVariable("TERMCITY_PLAYTEST_OUTPUT");
        if (!string.IsNullOrEmpty(artifacts))
            SaveGameStore.Save(game, Path.Combine(artifacts, $"{sizeName}-{seed}.json"));
    }

    private static int Distance(Pos a, Pos b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
}
