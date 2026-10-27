using TermCity.Core.Buildings;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

public sealed partial class CityGame
{
    private SettlementState? _settlement;
    public SettlementState? Settlement
    {
        get => _settlement;
        internal set
        {
            _settlement = value;
            Map.SetSettlementBounds(value?.Bounds);
        }
    }
    internal bool LoadedFromSave { get; set; }
    internal bool SimulationStarted { get; set; }
    public event Action? SiegeChanged;
    public event Action? SiegeWaveStarted;
    internal bool IsSettlementWall(int x, int y) => Settlement?.WallAt(new(x, y)) is not null;

    internal void BeginSiegeWave()
    {
        Speed = GameSpeed.Slow;
        SiegeWaveStarted?.Invoke();
    }

    private string? DebugSiegeError() => !DebugToolsAvailable ? "Siege debug actions require a Debug build." :
        Settlement is null ? "Found a bailey before using siege debug actions." : null;

    public ActionResult DebugStartSiegeWave(int swarmSize)
    {
        if (DebugSiegeError() is { } error) return ActionResult.Fail(error);
        if (swarmSize is < 1 or > SettlementState.MaximumGoblinSwarm)
            return ActionResult.Fail($"Swarm size must be between 1 and {SettlementState.MaximumGoblinSwarm}.");
        if (HighestPopulation < 100) return ActionResult.Fail("Siege waves require the permanent 100-population milestone.");
        if (!Settlement!.SpawnWave(this, swarmSize))
            return ActionResult.Fail("Not enough reachable exterior dry land to place that swarm without overlapping.");
        NotifyStateChanged();
        SiegeChanged?.Invoke();
        return ActionResult.Ok($"Debug: started wave {Settlement.WaveNumber} with {swarmSize} goblins, replacing the previous swarm.");
    }

    public ActionResult DebugRemoveGoblins()
    {
        if (DebugSiegeError() is { } error) return ActionResult.Fail(error);
        var siege = Settlement!;
        int removed = siege.GoblinList.Count;
        siege.GoblinList.Clear();
        siege.ProjectileList.Clear();
        siege.Accumulator = 0;
        siege.NextWaveSeconds = siege.NextWaveIntervalSeconds;
        siege.Revision++;
        NotifyStateChanged();
        SiegeChanged?.Invoke();
        return ActionResult.Ok($"Debug: removed {removed} goblins and reset the next-wave countdown.");
    }

    public ActionResult DebugRepairAllBuildings()
    {
        if (DebugSiegeError() is { } error) return ActionResult.Fail(error);
        foreach (var wall in Settlement!.Walls) wall.Health = wall.MaxHealth;
        Settlement.ClearRepairCrew();
        Settlement.Revision++;
        NotifyStateChanged();
        SiegeChanged?.Invoke();
        return ActionResult.Ok("Debug: repaired all walls, gates, towers and mounted defenses for free.");
    }

    private void AdvanceSiege(double seconds)
    {
        if (Settlement is not { } settlement) return;
        int before = settlement.Revision;
        settlement.Advance(this, seconds);
        if (before != settlement.Revision) SiegeChanged?.Invoke();
    }

    private bool ValidSettlementBounds(CellRect bounds) =>
        bounds.Width is >= SettlementState.MinimumEnclosureSize and <= SettlementState.MaximumEnclosureSize &&
        bounds.Height is >= SettlementState.MinimumEnclosureSize and <= SettlementState.MaximumEnclosureSize &&
        bounds.X >= 0 && bounds.Y >= 0 &&
        (long)bounds.X + bounds.Width <= Map.Width && (long)bounds.Y + bounds.Height <= Map.Height;

    private List<(BuildingType Type, CellRect Area)>? SettlementBuildings(CellRect bounds)
    {
        var result = new List<(BuildingType Type, CellRect Area)>();
        foreach (string name in new[] { "Motte and Bailey", "Town Well", "Woodlot" })
        {
            var type = Map.Content.Buildings.Find(name);
            if (type is null) return null;
            var positions = new CellRect(bounds.X + 2, bounds.Y + 2, bounds.Width - 4, bounds.Height - 4).Cells()
                .OrderBy(p => Math.Abs(p.X + type.Width / 2 - (bounds.X + bounds.Width / 2)) +
                    Math.Abs(p.Y + type.Height / 2 - (bounds.Y + bounds.Height / 2)))
                .ThenBy(p => p.Y).ThenBy(p => p.X);
            CellRect? chosen = null;
            foreach (var p in positions)
            {
                var area = type.FootprintAt(p.X, p.Y);
                if (area.Right >= bounds.Right - 1 || area.Bottom >= bounds.Bottom - 1 ||
                    !CanPlaceBuilding(type, p.X, p.Y) ||
                    result.Any(existing => area.X <= existing.Area.Right && area.Right >= existing.Area.X &&
                        area.Y <= existing.Area.Bottom && area.Bottom >= existing.Area.Y)) continue;
                chosen = area;
                break;
            }
            if (chosen is null) return null;
            result.Add((type, chosen.Value));
        }
        return result;
    }

    public string? SettlementPlacementError(CellRect bounds)
    {
        if (Settlement is not null) return "A settlement has already been founded.";
        if (!ValidSettlementBounds(bounds)) return "Choose a 12x12 to 160x160 enclosure entirely inside the map.";
        foreach (var p in bounds.Cells())
        {
            if (!Map.TerrainAt(p.X, p.Y).Buildable) return "The entire enclosure must be dry, buildable land.";
            if (Map.HasRoad(p.X, p.Y) || Network.RoadOverlapsCell(Map.Index(p.X, p.Y)))
                return "Roads cross the enclosure; choose clear land.";
            if (Map.BuildingAt(p.X, p.Y) is not null || Map.ZoneAt(p.X, p.Y) != ZoneType.None)
                return "The enclosure must be free of buildings and zones.";
        }
        if (SettlementBuildings(bounds) is null) return "The enclosure has no room for its central seat, well and woodlot.";
        return null;
    }

    public Quote QuoteSettlement(CellRect bounds)
    {
        if (SettlementPlacementError(bounds) is not null) return new(0, 0, Math.Max(0, bounds.Area));
        var buildings = SettlementBuildings(bounds)!;
        int wallCells = 2 * bounds.Width + 2 * bounds.Height - 4;
        int cost = wallCells * 20 + buildings.Sum(b => BuildingCostAt(b.Type, b.Area.X, b.Area.Y));
        return new(wallCells, cost, 0);
    }

    public ActionResult FoundSettlement(CellRect bounds, BaileyGateSide gateSide = BaileyGateSide.Right)
    {
        if (!Enum.IsDefined(gateSide)) return ActionResult.Fail("Unknown gate facing.");
        string? error = SettlementPlacementError(bounds);
        if (error is not null) return ActionResult.Fail(error);
        var buildings = SettlementBuildings(bounds)!;
        var quote = QuoteSettlement(bounds);
        if (Money < quote.Cost) return ActionResult.Fail($"The enclosure, seat, well and woodlot cost {quote.Cost:N0} gold.");
        // Validate the entire transaction before changing any layer, treasury or permanent milestone.
        foreach (var b in buildings)
        {
            var result = PlaceBuilding(b.Type, b.Area);
            if (!result.Success) throw new InvalidOperationException("Validated settlement placement failed: " + result.Message);
        }
        Money -= quote.Cells * 20;
        foreach (var p in bounds.Cells()) Map.SetFeature(p.X, p.Y, null);
        Settlement = new SettlementState(bounds, gateSide);
        Notify(roadsChanged: false, servicesChanged: true);
        return ActionResult.Ok($"Founded a walled settlement with a seat, well and woodlot for {quote.Cost:N0} gold.",
            quote.Cost, quote.Cells);
    }

    internal void ClearSettlementReservations()
    {
        if (Settlement is not { } settlement) return;
        foreach (var p in settlement.Bounds.Cells())
            if (Map.BuildingAt(p.X, p.Y) is null && Map.ZoneAt(p.X, p.Y) != ZoneType.None)
                Map.SetZone(p.X, p.Y, ZoneType.None);
    }

    internal int SettlementResidentialCapacity(CellRect bounds)
    {
        var reserved = SettlementBuildings(bounds)!;
        return SettlementSites(bounds, Map.Content.Buildings.ForZone(ZoneType.Residential)!, reserved.Select(b => b.Area).ToArray()).Count *
            (Map.Content.Buildings.ForZone(ZoneType.Residential)?.Capacity ?? 0);
    }

    public string? WallDefensePlacementError(Pos cell, WallDefenseKind kind)
    {
        if (!Enum.IsDefined(kind)) return "Unknown wall defense.";
        if (Settlement?.WallAt(cell) is not { } wall) return "Choose a settlement wall or tower.";
        if (wall.Health <= 0) return "Repair this wall before mounting a defense.";
        if (wall.Defense == kind) return "This wall already has that defense.";
        var definition = WallDefenses.Get(kind);
        if (HighestPopulation < definition.RequiredPopulation)
            return $"{definition.Name} unlocks permanently at {definition.RequiredPopulation:N0} souls.";
        if (Money < definition.Cost) return $"{definition.Name} costs {definition.Cost:N0} gold.";
        return null;
    }

    public ActionResult PlaceWallDefense(Pos cell, WallDefenseKind kind)
    {
        var error = WallDefensePlacementError(cell, kind);
        if (error is not null) return ActionResult.Fail(error);
        var wall = Settlement!.WallAt(cell)!;
        var definition = WallDefenses.Get(kind);
        Money -= definition.Cost;
        wall.Defense = kind;
        // Remounting must not reset a spent weapon's reload.
        wall.DefenseCooldown = Math.Max(wall.DefenseCooldown, definition.ReloadSeconds);
        Settlement.Revision++;
        NotifyStateChanged();
        SiegeChanged?.Invoke();
        return ActionResult.Ok(kind == WallDefenseKind.None ? "Removed the mounted defense." :
            $"Mounted {definition.Name} for {definition.Cost:N0} gold.", definition.Cost);
    }

    public int WallUpgradeCost => Settlement is not { } settlement || settlement.Material == WallMaterial.ReinforcedStone
        ? 0 : settlement.Walls.Count * (settlement.Material == WallMaterial.Wood ? 60 : 120);

    public int WallRepairCost => Settlement is not { } settlement ? 0 :
        (int)Math.Ceiling(settlement.WallSections.Sum(w => w.MaxHealth - w.Health) * 0.4);

    public string? WallUpgradeError()
    {
        if (Settlement is not { } settlement) return "Found a settlement first.";
        if (settlement.UpgradeTargetMaterial is not null) return "The purchased wall upgrade is already under construction.";
        if (settlement.Material == WallMaterial.ReinforcedStone) return "The walls are fully upgraded.";
        int required = settlement.Material == WallMaterial.Wood ? 250 : 1_000;
        if (HighestPopulation < required) return $"The next wall material unlocks at {required:N0} souls.";
        return Money < WallUpgradeCost ? $"The wall upgrade costs {WallUpgradeCost:N0} gold." : null;
    }

    public string? WallRepairError()
    {
        if (Settlement is null) return "Found a settlement first.";
        if (WallRepairCost == 0) return "The walls need no repairs.";
        return Money < WallRepairCost ? $"Wall repairs cost {WallRepairCost:N0} gold." : null;
    }

    public ActionResult UpgradeSettlementWalls()
    {
        if (Settlement?.UpgradeTargetMaterial is not null)
            return ActionResult.Fail("The purchased wall upgrade is already under construction.");
        if (WallUpgradeError() is { } error) return ActionResult.Fail(error);
        var settlement = Settlement!;
        int cost = WallUpgradeCost;
        Money -= cost;
        settlement.BeginUpgrade();
        NotifyStateChanged();
        SiegeChanged?.Invoke();
        return ActionResult.Ok($"Purchased {settlement.UpgradeTargetMaterial} walls for {cost:N0} gold; crews will upgrade one section at a time.", cost);
    }

    public ActionResult RepairSettlementWalls()
    {
        if (WallRepairError() is { } error) return ActionResult.Fail(error);
        var settlement = Settlement!;
        int cost = WallRepairCost;
        Money -= cost;
        foreach (var wall in settlement.Walls) wall.Health = wall.MaxHealth;
        settlement.ClearRepairCrew();
        settlement.Revision++;
        NotifyStateChanged();
        SiegeChanged?.Invoke();
        return ActionResult.Ok($"Repaired the settlement walls for {cost:N0} gold.", cost);
    }
}

public static class SettlementDebugSetup
{
    public static string? EnclosureSizeError(CityGame game, int? wallWidth, int? wallHeight)
    {
        if (wallWidth is null && wallHeight is null) return null;
        if (wallWidth is not { } width || wallHeight is not { } height)
            return "Enter both starting wall width and height.";
        if (width is < SettlementState.MinimumEnclosureSize or > SettlementState.MaximumEnclosureSize ||
            height is < SettlementState.MinimumEnclosureSize or > SettlementState.MaximumEnclosureSize)
            return $"Wall width and height must each be {SettlementState.MinimumEnclosureSize}-{SettlementState.MaximumEnclosureSize} cells.";
        if (width > game.Map.Width - 2 || height > game.Map.Height - 2)
            return $"This map supports starting walls up to {Math.Min(SettlementState.MaximumEnclosureSize, game.Map.Width - 2)}x" +
                $"{Math.Min(SettlementState.MaximumEnclosureSize, game.Map.Height - 2)} cells, leaving space outside the walls.";
        return null;
    }

    public static Pos Apply(CityGame game, bool siege = false, BaileyGateSide gateSide = BaileyGateSide.Right,
        int? wallWidth = null, int? wallHeight = null)
    {
        if (!CityGame.DebugToolsAvailable) throw new InvalidOperationException("Settlement presets require a Debug build.");
        if (game.LoadedFromSave || game.SimulationStarted || game.Settlement is not null ||
            game.Day != 0 || game.DayProgressSeconds != 0 || game.GrowthState.Planned ||
            game.Map.BuildingFootprints.Count != 0 || game.Map.BuildingLayer.Any(id => id != 0) ||
            Zones.Placeable.Any(zone => game.Map.ZoneCells(zone).Any()))
            throw new InvalidOperationException("Settlement presets require a new empty city.");
        if (EnclosureSizeError(game, wallWidth, wallHeight) is { } error)
            throw new InvalidOperationException(error);
        CellRect? chosen = null;
        var sizes = wallWidth is { } requestedWidth && wallHeight is { } requestedHeight
            ? new[] { (Width: requestedWidth, Height: requestedHeight) }
            : siege ? new[] { (24, 24) } : new[] { (32, 32), (24, 24), (20, 20), (16, 16), (12, 12) };
        foreach (var (width, height) in sizes)
        {
            for (int y = 1; y + height < game.Map.Height && chosen is null; y++)
                for (int x = 1; x + width < game.Map.Width; x++)
                {
                    var bounds = new CellRect(x, y, width, height);
                    if (game.SettlementPlacementError(bounds) is not null) continue;
                    if (game.QuoteSettlement(bounds).Cost > game.Money) continue;
                    if (siege && game.SettlementResidentialCapacity(bounds) < 100) continue;
                    chosen = bounds;
                    break;
                }
            if (chosen is not null) break;
        }
        if (chosen is not { } area)
            throw new InvalidOperationException(wallWidth is { } w && wallHeight is { } h
                ? $"No affordable dry {w}x{h} enclosure fits this map. Choose smaller walls or a different map."
                : "No affordable dry enclosure fits this map.");
        var result = game.FoundSettlement(area, gateSide);
        if (!result.Success) throw new InvalidOperationException(result.Message);
        if (siege)
        {
            int remaining = 100;
            var type = game.Map.Content.Buildings.ForZone(ZoneType.Residential)!;
            int homeCount = (100 + type.Capacity - 1) / type.Capacity;
            foreach (var plot in SpreadPlots(game, ZoneType.Residential, homeCount))
            {
                if (remaining <= 0) break;
                var home = game.Map.Content.Buildings.Find(CellHash.Pick(plot.X, plot.Y, 2) == 0 ? "Timber Home" : "Stone Home") ?? type;
                game.Map.SetBuildingFootprint(home, plot);
                int people = Math.Min(remaining, type.Capacity);
                int adults = Math.Min(people, 4);
                game.Map.SetHousehold(plot.X, plot.Y, new Household((byte)adults, (byte)(people - adults), 0));
                remaining -= people;
            }
            if (remaining != 0) throw new InvalidOperationException("The debug enclosure cannot house 100 residents.");
            game.Touch();
            foreach (var zone in new[] { ZoneType.Commercial, ZoneType.Industrial })
            {
                var workplace = game.Map.Content.Buildings.ForZone(zone);
                if (workplace is null) continue;
                int supported = game.SupportedCells(zone);
                foreach (var plot in SpreadPlots(game, zone, supported))
                {
                    string trade = zone == ZoneType.Commercial ? "Supply Shop" :
                        CellHash.Pick(plot.X, plot.Y, 3) switch { 0 => "Blacksmith", 1 => "Woodsmith", _ => "Armory" };
                    game.Map.SetBuildingFootprint(game.Map.Content.Buildings.Find(trade) ?? workplace, plot);
                }
            }
            // This fixture represents an established hamlet. Missing establishment dates already mean
            // established service in both the simulation and legacy saves; no calendar or disasters need advancing.
            foreach (int service in game.Map.ServiceCells) game.Map.BuildingEstablishedDays.Remove(service);
            game.Touch();
        }
        game.Paused = false;
        return new(area.X + area.Width / 2, area.Y + area.Height / 2);
    }

    private static IEnumerable<CellRect> SpreadPlots(CityGame game, ZoneType zone, int count)
    {
        var bounds = game.Settlement!.Bounds;
        double centerX = bounds.X + bounds.Width / 2.0, centerY = bounds.Y + bounds.Height / 2.0;
        var candidates = game.VacantPlots(zone).ToList();
        var selected = new List<CellRect>();
        double Distance(CellRect a, CellRect b) => Math.Pow(a.X + a.Width / 2.0 - b.X - b.Width / 2.0, 2) +
            Math.Pow(a.Y + a.Height / 2.0 - b.Y - b.Height / 2.0, 2);
        for (int n = 0; n < count && candidates.Count > 0; n++)
        {
            var plot = candidates.OrderByDescending(p => selected.Count > 0 ? selected.Min(other => Distance(p, other)) : 0)
                .ThenBy(p => Math.Pow(p.X + p.Width / 2.0 - centerX, 2) + Math.Pow(p.Y + p.Height / 2.0 - centerY, 2))
                .ThenBy(p => p.Y).ThenBy(p => p.X).First();
            candidates.Remove(plot);
            selected.Add(plot);
            yield return plot;
        }
    }
}
