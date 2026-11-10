using TermCity.Core.Buildings;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

public sealed partial class CityGame
{
    private SettlementState? _settlement;
    private int _settlementSitesVersion = -1;
    private readonly Dictionary<(CellRect Bounds, BaileyGateSide Side), List<(BuildingType Type, CellRect Area)>?> _settlementStarterSites = [];
    public SettlementState? Settlement
    {
        get => _settlement;
        internal set
        {
            _settlement = value;
            if (value is not null) value.AttachedMap = Map;
            Map.Fortifications = value;
            Map.SetSettlementBounds(value?.Bounds);
        }
    }
    internal bool LoadedFromSave { get; set; }
    internal bool SimulationStarted { get; set; }
    public event Action? SiegeChanged;
    public event Action? SiegeWaveStarted;
    internal bool IsSettlementWall(int x, int y) => Settlement is { } settlement &&
        settlement.FortificationReserved(new(x, y));

    private bool RoadWallBlocked(int x, int y)
    {
        if (Settlement is not { } state || !state.FortificationReserved(new(x, y))) return false;
        var point = new System.Numerics.Vector2(x + .5f, y + .5f);
        return state.GroundBlocked(point) || !state.Walls.Where(w => w.IsGate && w.Health > 0).Any(gate =>
        {
            var delta = point - gate.Center;
            return Math.Abs(System.Numerics.Vector2.Dot(delta, gate.Tangent)) < (Rendering.WallDamageLayout.Width(gate) - .75f) / 2 &&
                Math.Abs(-delta.X * gate.Tangent.Y + delta.Y * gate.Tangent.X) <= 3;
        });
    }

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
        if (swarmSize is < 1 or > SettlementState.MaximumEnemySwarm)
            return ActionResult.Fail($"Swarm size must be between 1 and {SettlementState.MaximumEnemySwarm}.");
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
        int removed = siege.EnemyList.Count;
        siege.EnemyList.Clear();
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
        bounds.X >= 2 && bounds.Y >= 2 &&
        (long)bounds.X + bounds.Width <= Map.Width - 2 && (long)bounds.Y + bounds.Height <= Map.Height - 2;

    private List<(BuildingType Type, CellRect Area)>? SettlementBuildings(CellRect bounds,
        BaileyGateSide gateSide = BaileyGateSide.Right)
    {
        if (_settlementSitesVersion != _version)
        {
            _settlementStarterSites.Clear();
            _settlementSitesVersion = _version;
        }
        if (_settlementStarterSites.TryGetValue((bounds, gateSide), out var cached)) return cached;
        if (_settlementStarterSites.Count >= 256) _settlementStarterSites.Clear();
        return _settlementStarterSites[(bounds, gateSide)] = FindSettlementBuildings(bounds, gateSide);
    }

    private List<(BuildingType Type, CellRect Area)>? FindSettlementBuildings(CellRect bounds, BaileyGateSide gateSide)
    {
        var result = new List<(BuildingType Type, CellRect Area)>();
        HashSet<Pos>? reachable = null;
        SettlementState? wetLayout = null;
        Pos gateStart = default;
        HashSet<Pos> Flood(CellRect? candidate)
        {
            bool Open(Pos p) => bounds.Contains(p) && Map.TerrainAt(p.X, p.Y).Buildable &&
                candidate?.Contains(p) != true && !result.Any(existing => existing.Area.Contains(p)) &&
                !wetLayout!.GroundBlocked(new(p.X + .5f, p.Y + .5f));
            var cells = new HashSet<Pos>();
            var queue = new Queue<Pos>();
            if (Open(gateStart)) { cells.Add(gateStart); queue.Enqueue(gateStart); }
            while (queue.TryDequeue(out var p))
                foreach (var direction in RoadNetwork.Neighbors)
                {
                    var next = p.Offset(direction.Dx, direction.Dy);
                    if (!cells.Contains(next) && Open(next) && wetLayout!.CanWalk(
                        new(p.X + .5f, p.Y + .5f), new(next.X + .5f, next.Y + .5f)))
                    { cells.Add(next); queue.Enqueue(next); }
                }
            return cells;
        }
        if (bounds.Cells().Any(p => !Map.TerrainAt(p.X, p.Y).Buildable))
        {
            var gate = gateSide switch
            {
                BaileyGateSide.Right => new Pos(bounds.Right, bounds.Y + bounds.Height / 2),
                BaileyGateSide.Bottom => new Pos(bounds.X + bounds.Width / 2, bounds.Bottom),
                BaileyGateSide.Left => new Pos(bounds.X, bounds.Y + bounds.Height / 2),
                _ => new Pos(bounds.X + bounds.Width / 2, bounds.Y),
            };
            if (!Map.TerrainAt(gate.X, gate.Y).Buildable) return null;
            gateStart = gate;
            wetLayout = new(bounds, gateSide, terrain: Map);
            reachable = Flood(null);
        }
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
                    reachable is not null && !area.Cells().All(reachable.Contains) ||
                    result.Any(existing => area.X <= existing.Area.Right && area.Right >= existing.Area.X &&
                        area.Y <= existing.Area.Bottom && area.Bottom >= existing.Area.Y)) continue;
                if (wetLayout is not null)
                {
                    var after = Flood(area);
                    bool Access(CellRect plot) => plot.Cells().Any(cell => RoadNetwork.Neighbors.Any(direction =>
                        after.Contains(cell.Offset(direction.Dx, direction.Dy))));
                    if (!Access(area) || result.Any(existing => !Access(existing.Area))) continue;
                }
                chosen = area;
                break;
            }
            if (chosen is null) return null;
            result.Add((type, chosen.Value));
            if (wetLayout is not null) reachable = Flood(null);
        }
        return result;
    }

    public string? SettlementPlacementError(CellRect bounds, BaileyGateSide gateSide = BaileyGateSide.Right)
    {
        if (Settlement is not null) return "A settlement has already been founded.";
        if (!ValidSettlementBounds(bounds)) return "Choose a 12x12 to 160x160 enclosure with two clear cells beyond every edge.";
        foreach (var p in bounds.Cells())
        {
            if (!Map.TerrainAt(p.X, p.Y).Buildable &&
                Map.TerrainAt(p.X, p.Y).Name != Terrain.DefaultTerrains.WaterName)
                return "The enclosure needs buildable land or water for grated wall crossings.";
            if (Map.HasRoad(p.X, p.Y) || Network.RoadOverlapsCell(Map.Index(p.X, p.Y)))
                return "Roads cross the enclosure; choose clear land.";
            if (Map.BuildingAt(p.X, p.Y) is not null || Map.ZoneAt(p.X, p.Y) != ZoneType.None)
                return "The enclosure must be free of buildings and zones.";
        }
        foreach (var p in new CellRect(bounds.X - 2, bounds.Y - 2, bounds.Width + 4, bounds.Height + 4).Cells())
            if (WorldScale.FortificationReserved(bounds, p) &&
                ((!Map.TerrainAt(p.X, p.Y).Buildable && Map.TerrainAt(p.X, p.Y).Name != Terrain.DefaultTerrains.WaterName) ||
                 Map.BuildingAt(p.X, p.Y) is not null ||
                 Map.ZoneAt(p.X, p.Y) != ZoneType.None || Map.HasRoad(p.X, p.Y) ||
                 Network.RoadOverlapsCell(Map.Index(p.X, p.Y))))
                return "Towers and future wall upgrades need a clear two-cell corridor on both sides.";
        if (SettlementBuildings(bounds, gateSide) is null)
            return "The enclosure needs a dry gate and connected dry sites for its central seat, well and woodlot.";
        return null;
    }

    public Quote QuoteSettlement(CellRect bounds, BaileyGateSide gateSide = BaileyGateSide.Right)
    {
        if (SettlementPlacementError(bounds, gateSide) is not null) return new(0, 0, Math.Max(0, bounds.Area));
        var buildings = SettlementBuildings(bounds, gateSide)!;
        int wallCells = 2 * bounds.Width + 2 * bounds.Height - 4;
        int cost = wallCells * 20 + buildings.Sum(b => BuildingCostAt(b.Type, b.Area.X, b.Area.Y));
        return new(wallCells, cost, 0);
    }

    public ActionResult FoundSettlement(CellRect bounds, BaileyGateSide gateSide = BaileyGateSide.Right)
    {
        if (!Enum.IsDefined(gateSide)) return ActionResult.Fail("Unknown gate facing.");
        string? error = SettlementPlacementError(bounds, gateSide);
        if (error is not null) return ActionResult.Fail(error);
        var buildings = SettlementBuildings(bounds, gateSide)!;
        var quote = QuoteSettlement(bounds, gateSide);
        if (Money < quote.Cost) return ActionResult.Fail($"The enclosure, seat, well and woodlot cost {quote.Cost:N0} gold.");
        // Validate the entire transaction before changing any layer, treasury or permanent milestone.
        foreach (var b in buildings)
        {
            var result = PlaceBuilding(b.Type, b.Area);
            if (!result.Success) throw new InvalidOperationException("Validated settlement placement failed: " + result.Message);
        }
        Money -= quote.Cells * 20;
        foreach (var p in bounds.Cells()) Map.SetFeature(p.X, p.Y, null);
        foreach (var p in new CellRect(bounds.X - 2, bounds.Y - 2, bounds.Width + 4, bounds.Height + 4).Cells())
            if (WorldScale.FortificationReserved(bounds, p)) Map.SetFeature(p.X, p.Y, null);
        Settlement = new SettlementState(bounds, gateSide, terrain: Map);
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

    internal int SettlementResidentialCapacity(CellRect bounds, BaileyGateSide gateSide)
    {
        var reserved = SettlementBuildings(bounds, gateSide)!;
        return SettlementSites(bounds, Map.Content.Buildings.ForZone(ZoneType.Residential)!, reserved.Select(b => b.Area).ToArray()).Count *
            (Map.Content.Buildings.ForZone(ZoneType.Residential)?.Capacity ?? 0);
    }

    public string? WallDefensePlacementError(Pos cell, WallDefenseKind kind, int? wallId = null)
    {
        if (!Enum.IsDefined(kind)) return "Unknown wall defense.";
        var wall = wallId is { } id ? Settlement?.Walls.FirstOrDefault(w => w.Id == id && w.Cell == cell) : Settlement?.WallAt(cell);
        if (wall is null) return "Choose a settlement wall or tower.";
        if (wall.IsGateWing) return "Choose the gate centre when mounting a defense.";
        if (wall.Health <= 0) return "Repair this wall before mounting a defense.";
        if (!wall.IsTower && wall.Start is not null && wall.Length < 1.2f && kind != WallDefenseKind.None)
            return "This section is too short for a safe weapon platform. Choose a longer span or tower.";
        if (wall.Defense == kind) return "This wall already has that defense.";
        var definition = WallDefenses.Get(kind);
        if (HighestPopulation < definition.RequiredPopulation)
            return $"{definition.Name} unlocks permanently at {definition.RequiredPopulation:N0} souls.";
        if (Money < definition.Cost) return $"{definition.Name} costs {definition.Cost:N0} gold.";
        return null;
    }

    public ActionResult PlaceWallDefense(Pos cell, WallDefenseKind kind, int? wallId = null, int facing = 1)
    {
        if (facing is not (-1 or 1)) return ActionResult.Fail("Choose one of the wall's two defended faces.");
        var error = WallDefensePlacementError(cell, kind, wallId);
        if (error is not null) return ActionResult.Fail(error);
        var wall = wallId is { } id ? Settlement!.Walls.Single(w => w.Id == id) : Settlement!.WallAt(cell)!;
        var definition = WallDefenses.Get(kind);
        Money -= definition.Cost;
        wall.Defense = kind;
        wall.DefenseFacing = facing;
        Settlement.InvalidateAttackGeometry();
        // Remounting must not reset a spent weapon's reload.
        wall.DefenseCooldown = Math.Max(wall.DefenseCooldown, definition.ReloadSeconds);
        Settlement.Revision++;
        NotifyStateChanged();
        SiegeChanged?.Invoke();
        return ActionResult.Ok(kind == WallDefenseKind.None ? "Removed the mounted defense." :
            $"Mounted {definition.Name} for {definition.Cost:N0} gold.", definition.Cost);
    }

    public int WallUpgradeCost => Settlement is not { } settlement || settlement.Material == WallMaterial.ReinforcedStone
        ? 0 : (int)Math.Ceiling(settlement.Walls.Sum(w => (double)w.Length) *
            (settlement.Material == WallMaterial.Wood ? 60 : 120));

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

public static partial class SettlementDebugSetup
{
    public const int DefaultWallSize = 64;

    public static string? EnclosureSizeError(CityGame game, int? wallWidth, int? wallHeight)
    {
        if (wallWidth is null && wallHeight is null) return null;
        if (wallWidth is not { } width || wallHeight is not { } height)
            return "Enter both starting wall width and height.";
        if (width is < SettlementState.MinimumEnclosureSize or > SettlementState.MaximumEnclosureSize ||
            height is < SettlementState.MinimumEnclosureSize or > SettlementState.MaximumEnclosureSize)
            return $"Wall width and height must each be {SettlementState.MinimumEnclosureSize}-{SettlementState.MaximumEnclosureSize} cells.";
        if (width > game.Map.Width - 4 || height > game.Map.Height - 4)
            return $"This map supports starting walls up to {Math.Min(SettlementState.MaximumEnclosureSize, game.Map.Width - 4)}x" +
                $"{Math.Min(SettlementState.MaximumEnclosureSize, game.Map.Height - 4)} cells, leaving space outside the walls.";
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
            : siege ? new[] { (64, 64), (56, 56), (48, 48), (40, 40), (36, 36) } :
                new[] { (64, 64), (56, 56), (48, 48), (40, 40), (36, 36) };
        foreach (var (width, height) in sizes)
        {
            for (int y = 1; y + height < game.Map.Height && chosen is null; y++)
                for (int x = 1; x + width < game.Map.Width; x++)
                {
                    var bounds = new CellRect(x, y, width, height);
                    if (game.SettlementPlacementError(bounds, gateSide) is not null) continue;
                    if (game.QuoteSettlement(bounds, gateSide).Cost > game.Money) continue;
                    if (siege && game.SettlementResidentialCapacity(bounds, gateSide) < 100) continue;
                    chosen = bounds;
                    break;
                }
            if (chosen is not null) break;
        }
        if (chosen is not { } area)
            throw new DebugSettlementSiteUnavailableException(wallWidth is { } w && wallHeight is { } h
                ? $"No affordable {w}x{h} enclosure with dry, reachable starter sites fits this map. Choose smaller walls or a different map."
                : "No affordable enclosure with dry, reachable starter sites fits this map.");
        var result = game.FoundSettlement(area, gateSide);
        if (!result.Success) throw new InvalidOperationException(result.Message);
        if (siege)
        {
            var garrisons = GarrisonSites(game, area);
            int remaining = 100;
            var type = game.Map.Content.Buildings.ForZone(ZoneType.Residential)!;
            int homeCount = (100 + type.Capacity - 1) / type.Capacity;
            foreach (var plot in SpreadPlots(game, ZoneType.Residential, homeCount, garrisons.Select(g => g.Area).ToArray()))
            {
                if (remaining <= 0) break;
                var home = game.Map.Content.Buildings.Find(CellHash.Pick(plot.X, plot.Y, 2) == 0 ? "Timber Home" : "Stone Home") ?? type;
                game.Map.SetBuildingFootprint(home, plot);
                int people = Math.Min(remaining, type.Capacity);
                int adults = Math.Min(people, 4);
                game.Map.SetHousehold(plot.X, plot.Y, new Household((byte)adults, (byte)(people - adults), 0));
                remaining -= people;
            }
            if (remaining != 0)
                throw new DebugSettlementSiteUnavailableException("The debug enclosure cannot house 100 residents with both garrisons. Choose larger walls.");
            game.Touch();
            foreach (var garrison in garrisons)
            {
                var placed = game.PlaceBuilding(garrison.Type, garrison.Area);
                if (!placed.Success)
                    throw new DebugSettlementSiteUnavailableException($"Debug siege {garrison.Type.Name} placement failed: {placed.Message}");
            }
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

    private static List<(BuildingType Type, CellRect Area)> GarrisonSites(CityGame game, CellRect bounds)
    {
        var result = new List<(BuildingType Type, CellRect Area)>();
        var gate = game.Settlement!.Walls.Single(w => w.IsGate).Cell;
        foreach (string name in new[] { "Guard Post", "Archer Barracks" })
        {
            var type = game.Map.Content.Buildings.Get(name);
            var site = bounds.Cells().OrderBy(p => Math.Abs(p.X - gate.X) + Math.Abs(p.Y - gate.Y))
                .ThenBy(p => p.Y).ThenBy(p => p.X)
                .Select(p => type.FootprintAt(p.X, p.Y))
                .Where(footprint => footprint.Cells().All(p => game.CanBuildOn(p.X, p.Y)) &&
                    !result.Any(other => footprint.Cells().Any(p => other.Area.Contains(p))) &&
                    game.Settlement.GarrisonPlacementError(game, type, footprint) is null)
                .Select(footprint => (CellRect?)footprint).FirstOrDefault();
            if (site is not { } area)
                throw new DebugSettlementSiteUnavailableException($"The debug enclosure has no safe interior site for {name}.");
            result.Add((type, area));
        }
        return result;
    }

    private static IEnumerable<CellRect> SpreadPlots(CityGame game, ZoneType zone, int count,
        IReadOnlyList<CellRect>? reserved = null)
    {
        var bounds = game.Settlement!.Bounds;
        double centerX = bounds.X + bounds.Width / 2.0, centerY = bounds.Y + bounds.Height / 2.0;
        var candidates = game.VacantPlots(zone).Where(plot =>
            reserved is null || !reserved.Any(other => plot.Cells().Any(p => other.Contains(p)))).ToList();
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
