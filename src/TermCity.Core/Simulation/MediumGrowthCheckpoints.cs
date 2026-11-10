using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using TermCity.Core.Buildings;
using TermCity.Core.Persistence;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

public sealed record GrowthCheckpoint(string Name, int Population, double Days, double Happiness,
    int Buildings, int Roads, int PathCells, string Sha256);
public sealed record MediumGrowthResult(int Seed, bool Complete, string Limit, int PeakPopulation, double Days,
    IReadOnlyList<GrowthCheckpoint> Checkpoints);

/// <summary>Bounded, ordinary-rule settlement growth for reproducible rendering checkpoints.</summary>
public static class MediumGrowthCheckpoints
{
    public static MediumGrowthResult Generate(GameConfig config, string directory, int maxDays = 7280,
        TimeSpan? timeBudget = null, Action<string>? report = null, string? resumePath = null)
    {
        if (config.Scenario != CityScenario.Random || config.MapWidth != 480 || config.MapHeight != 480 ||
            !config.FullRules || maxDays <= 0)
            throw new ArgumentException("Growth checkpoints require a full-rules 480x480 random map and a positive day limit.");
        var limit = timeBudget ?? TimeSpan.FromMinutes(10);
        if (limit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeBudget));
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("Checkpoint output must be a new or empty directory; existing saves will not be overwritten.");
        var game = resumePath is null ? CityGame.New(config with { StartingYear = 1226 }) : SaveGameStore.Load(resumePath);
        if (game.Config.Scenario != CityScenario.Random || game.Map.Width != 480 || game.Map.Height != 480 ||
            !game.Config.FullRules || resumePath is not null && game.Settlement is null)
            throw new ArgumentException("A resumed growth checkpoint must be a full-rules medium settlement.");
        Directory.CreateDirectory(directory);
        game.Paused = true;
        foreach (var zone in Zones.Placeable) game.SetTax(zone, .07);
        var bounds = game.Settlement?.Bounds ?? FindFoundingSite(game);
        if (game.Settlement is null) Require(game.FoundSettlement(bounds));
        int extent = (game.Settlement ?? throw new InvalidOperationException("Founding did not create a settlement.")).WallBounds.Width;
        var checkpoints = new List<GrowthCheckpoint>();
        int[] targets = [10_000, 25_000, 50_000];
        int next = 0, peak = game.Stats.Population;
        int[] milestoneTargets = [100, 250, 500, 1_000, 2_500, 5_000];
        var milestones = new HashSet<int>(milestoneTargets.Where(milestone => milestone <= game.Stats.Population));
        string lastDecision = "Initial settlement";
        var watch = Stopwatch.StartNew();
        while (next < targets.Length && game.Stats.Population >= targets[next]) next++;
        Save(resumePath is null ? "beginning" : "resumed");
        for (int day = 0; day < maxDays && next < targets.Length; day++)
        {
            if (watch.Elapsed >= limit) { lastDecision = "Wall-clock generation limit reached. " + lastDecision; break; }
            if (game.Day == 0) Maintain();
            game.Paused = false;
            game.AdvanceDay();
            game.Paused = true;
            int population = game.Stats.Population;
            peak = Math.Max(peak, population);
            foreach (int milestone in milestoneTargets)
                if (population >= milestone && milestones.Add(milestone)) Save($"milestone-{milestone}");
            while (next < targets.Length && population >= targets[next])
            {
                Save($"{targets[next] / 1000}k");
                next++;
            }
            if (game.Day == 0)
            {
                report?.Invoke($"MEDIUM_GROWTH day={game.ElapsedDays:0} population={population} peak={peak} " +
                    $"happiness={game.Indicators.Happiness:0.0} money={game.Money} walls={game.Settlement!.Walls.Count} " +
                    $"enemies={game.Settlement.Enemies.Count} defenders={game.Settlement.Defenders.Count} action={lastDecision}");
                if ((int)game.ElapsedDays % 28 == 0) Save("progress");
            }
        }
        if (next < targets.Length) Save("achieved");
        var diagnostic = GrowthDiagnostics.ForZone(game, ZoneType.Residential);
        var result = new MediumGrowthResult(game.Config.Seed, next == targets.Length,
            next == targets.Length ? "All targets reached." :
                $"Missing {string.Join(", ", targets.Skip(next))}; {diagnostic.Status}: {diagnostic.Message}; {lastDecision}",
            peak, game.ElapsedDays, checkpoints);
        File.WriteAllText(Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        report?.Invoke($"MEDIUM_GROWTH_RESULT complete={result.Complete} days={result.Days:0} " +
            $"peak={result.PeakPopulation} limit={result.Limit}");
        return result;

        void Save(string name)
        {
            game.Touch();
            string path = Path.Combine(directory, name + ".json");
            SaveGameStore.Save(game, path, new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2, 0)
                { CameraEdgePadding = true });
            var loaded = SaveGameStore.Load(path);
            if (loaded.Stats.Population != game.Stats.Population ||
                loaded.Settlement?.Walls.Count != game.Settlement?.Walls.Count ||
                loaded.Settlement?.Defenders.Count != game.Settlement?.Defenders.Count ||
                Math.Abs(loaded.Indicators.Happiness - game.Indicators.Happiness) > .000001)
                throw new InvalidOperationException($"Checkpoint {name} round-trip differs: " +
                    $"population={game.Stats.Population}/{loaded.Stats.Population}, " +
                    $"walls={game.Settlement?.Walls.Count}/{loaded.Settlement?.Walls.Count}, " +
                    $"defenders={game.Settlement?.Defenders.Count}/{loaded.Settlement?.Defenders.Count}, " +
                    $"happiness={game.Indicators.Happiness}/{loaded.Indicators.Happiness}.");
            checkpoints.RemoveAll(checkpoint => checkpoint.Name == name);
            checkpoints.Add(new(name, game.Stats.Population, game.ElapsedDays, game.Indicators.Happiness,
                game.Map.BuildingFootprints.Count, game.Map.RoadCount, game.Map.PathLayer.Count(wear => wear > 0),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        }

        bool Build(string name)
        {
            var type = game.Map.Content.Buildings.Get(name);
            if (!CityProgression.IsUnlocked(game, type)) return false;
            int reserve = Math.Max(2_000, game.Finance.Expenses * 4);
            var sites = Enumerable.Range(3, 44).SelectMany(y => Enumerable.Range(3, 44)
                .Select(x => new Pos(x * 10, y * 10))).OrderBy(site =>
                    Math.Abs(site.X - bounds.X - bounds.Width / 2) + Math.Abs(site.Y - bounds.Y - bounds.Height / 2));
            foreach (var site in sites)
            {
                if (game.Settlement?.ContainsInterior(site) != true ||
                    game.BuildingPlacementError(type, site.X, site.Y) is not null ||
                    !game.CanPlaceBuilding(type, site.X, site.Y)) continue;
                var area = type.FootprintAt(site.X, site.Y);
                if (!area.Cells().All(game.Settlement.ContainsInterior)) continue;
                var quote = game.QuoteBuilding(type, area);
                if (quote.Cells != area.Area || quote.Cost + reserve > game.Money) continue;
                if (game.Stats.Population > 0 && type.WeeklyUpkeep > 200 && !type.Service.IsUtility() &&
                    game.Finance.Income - game.Finance.Expenses < type.WeeklyUpkeep) continue;
                Require(game.PlaceBuilding(type, area));
                lastDecision = $"Built {name}";
                return true;
            }
            lastDecision = $"No affordable clear site for {name}";
            return false;
        }

        void Maintain()
        {
            lastDecision = "Waiting for ordinary arrivals/income";
            if (game.WallRepairCost > 0 && game.WallRepairCost < game.Money / 2)
            {
                Require(game.RepairSettlementWalls());
                lastDecision = "Paid wall repairs";
            }
            if (game.WallUpgradeError() is null && game.WallUpgradeCost < game.Money / 2)
                Require(game.UpgradeSettlementWalls());
            if (game.Services.Power.Ratio < .98 && Build("Woodlot")) return;
            if (game.Services.Water.Ratio < .98 && Build("Town Well")) return;
            int population = game.Stats.Population;
            int archers = game.Map.BuildingFootprints.Values.Count(area => game.Map.BuildingAt(area.X, area.Y)!.Name == "Archer Barracks");
            if (population >= 100 && archers < Math.Min(8, 1 + population / 250) && Build("Archer Barracks")) return;
            int guards = game.Map.BuildingFootprints.Values.Count(area => game.Map.BuildingAt(area.X, area.Y)!.Name == "Guard Post");
            if (population >= 100 && guards < Math.Min(8, 1 + population / 500) && Build("Guard Post")) return;
            foreach (string name in new[] { "Chapel", "Fire Watch", "Watch House", "Apothecary", "Chantry School",
                "Market Cross", "Granary", "Village Green", "Stone Keep", "Tavern", "Guildhall", "Castle", "Cathedral" })
            {
                if (population < 25) break;
                if (game.Map.BuildingFootprints.Values.Any(area => game.Map.BuildingAt(area.X, area.Y)!.Name == name)) continue;
                if (Build(name)) return;
            }
            if (game.SettlementGrowthSites(World.ZoneType.Residential).Count > 8 || extent >= 432) return;
            int expanded = Math.Min(432, extent + 48);
            var center = new Vector2(bounds.X + bounds.Width / 2f, bounds.Y + bounds.Height / 2f);
            var corners = new[] { center + new Vector2(-expanded, -expanded) / 2,
                center + new Vector2(expanded, -expanded) / 2, center + new Vector2(expanded, expanded) / 2,
                center + new Vector2(-expanded, expanded) / 2 };
            var runs = ExpansionRuns(game, corners.Append(corners[0]).ToArray()).ToList();
            if (runs.Count == 0) { lastDecision = "Expansion would run along a road; waiting for a different affordable enclosure"; return; }
            var root = game.Settlement!.Walls.Where(wall => wall.IsTower && wall.BelongsToFoundingWall)
                .MinBy(wall => Vector2.DistanceSquared(wall.Center, corners[1]))!;
            var connector = ExpansionRuns(game, [root.Center, corners[1]]);
            if (connector.Count == 0) { lastDecision = "Connecting expansion to the founding walls would run along a road"; return; }
            runs.AddRange(connector);
            runs = runs.OrderByDescending(run => run.Gate).ToList();
            var plans = runs.Select(run => game.PlanWallRun(run.A, run.B, run.Gate)).ToArray();
            if (plans.Any(plan => !plan.Valid) || plans.Sum(plan => plan.Cost) + 4_000 > game.Money)
            {
                lastDecision = $"Expansion cost={plans.Sum(plan => plan.Cost)} money={game.Money}; " +
                    string.Join("; ", plans.Where(plan => !plan.Valid).Select(plan => plan.Error));
                return;
            }
            foreach (var run in runs) Require(game.BuildWallRun(run.A, run.B, run.Gate));
            extent = expanded;
            lastDecision = $"Built ordinary outer enclosure {extent}x{extent}";
        }
    }

    private static IReadOnlyList<(Vector2 A, Vector2 B, bool Gate)> ExpansionRuns(CityGame game, Vector2[] corners)
    {
        var runs = new List<(Vector2 A, Vector2 B, bool Gate)>();
        for (int side = 0; side < corners.Length - 1; side++)
        {
            var a = corners[side]; var b = corners[side + 1];
            int length = (int)Vector2.Distance(a, b);
            var gates = new bool[length];
            for (int step = 0; step < length; step++)
            {
                var point = Vector2.Lerp(a, b, (step + .5f) / length);
                bool road = Enumerable.Range(-2, 5).Any(dy => Enumerable.Range(-2, 5).Any(dx =>
                    game.Map.InBounds((int)point.X + dx, (int)point.Y + dy) &&
                    game.Network.RoadOverlapsCell(game.Map.Index((int)point.X + dx, (int)point.Y + dy))));
                if (!road) continue;
                for (int at = Math.Max(0, step - 4); at <= Math.Min(length - 1, step + 4); at++) gates[at] = true;
            }
            for (int start = 0; start < length;)
            {
                int end = start + 1;
                while (end < length && gates[end] == gates[start]) end++;
                if (gates[start] && end - start > 24) return [];
                runs.Add((Vector2.Lerp(a, b, (float)start / length), Vector2.Lerp(a, b, (float)end / length), gates[start]));
                start = end;
            }
        }
        return runs.OrderByDescending(run => run.Gate).ToArray();
    }

    private static CellRect FindFoundingSite(CityGame game)
    {
        foreach (int width in new[] { 96, 80, 64 })
            foreach (var site in Enumerable.Range(5, 16).SelectMany(y => Enumerable.Range(5, 16)
                .Select(x => new CellRect(x * 16, y * 16, width, width))).OrderBy(area =>
                    Math.Abs(area.X + width / 2 - 240) + Math.Abs(area.Y + width / 2 - 240)))
                if (game.SettlementPlacementError(site) is null && game.QuoteSettlement(site).Cost <= game.Money - 4_000)
                    return site;
        throw new InvalidOperationException("Seed has no affordable valid founding site; terrain was not flattened to manufacture one.");
    }

    private static void Require(ActionResult result)
    {
        if (!result.Success) throw new InvalidOperationException(result.Message);
    }
}
