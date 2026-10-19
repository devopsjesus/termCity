using TermCity.Core.Buildings;
using TermCity.Core.Features;
using TermCity.Core.Roads;
using TermCity.Core.Terrain;
using TermCity.Core.Util;

namespace TermCity.Core.World;

/// <summary>The set of registries that define what exists in the game world.</summary>
public sealed class GameContent
{
    public TerrainRegistry Terrains { get; init; } = TerrainRegistry.CreateDefault();

    public FeatureRegistry Features { get; init; } = FeatureRegistry.CreateDefault();

    public BuildingRegistry Buildings { get; init; } = BuildingRegistry.CreateDefault();

    public RoadRegistry Roads { get; init; } = RoadRegistry.CreateDefault();
}

public sealed record ZoneRemoval(ZoneType Zone, double RemoveAtDay);

/// <summary>
/// The layered world grid: terrain, natural features, roads, zones, buildings and the households living in them.
/// Each layer is a flat array indexed by <c>y * Width + x</c>.
/// </summary>
public sealed class GameMap
{
    // Internal so persistence can read and write layers directly.
    internal readonly byte[] TerrainLayer;
    internal readonly bool[] SeaLayer;
    internal readonly bool[] RiverLayer;
    internal readonly byte[] FeatureLayer;
    internal readonly bool[] RoadLayer;
    internal readonly byte[] RoadTypeLayer;
    internal readonly bool[] PlayerRoadLayer;
    internal readonly ZoneType[] ZoneLayer;
    internal readonly byte[] BuildingLayer;
    internal readonly Household[] HouseholdLayer;
    internal readonly byte[] PathLayer;
    internal readonly byte[] PathLinkLayer;
    internal readonly Dictionary<int, double> BuildingEstablishedDays = [];
    internal readonly Dictionary<string, Simulation.TravelRoutes> TravelCache = [];
    internal Simulation.TravelRoutes? PathRoutes;
    internal int[][]? BuildingPaths;
    internal byte[]? HillLevels;
    internal byte[]? CoastalHillCaps; // 0 unknown, 1 coastal, 2 collapsed, 3 inland.
    internal double[]? WaterDensities;
    internal sbyte[]? CoastDistances;
    internal double[]? JunctionDelays;
    internal IReadOnlyList<TermCity.Core.Rendering.RoadPath>? TravelRoadPaths;
    internal TermCity.Core.Rendering.RoadMeshLayout? JunctionRoadLayout;
    internal float[]? FootpathHeights;
    internal bool[]? FootpathBridgeCells;
    internal TermCity.Core.Rendering.WaterFlowField? WaterFlow;
    internal readonly Dictionary<int, ZoneRemoval> ZoneRemovals = [];

    // Sparse indexes so work that only concerns roads or zones scales with how much has been built,
    // not with the size of the map. They are kept in step by SetRoad/SetZone/ClearCell.
    private readonly HashSet<int> _roadCells = [];
    private readonly HashSet<int>[] _zoneCells = [[], [], [], []];
    private readonly HashSet<int> _serviceCells = [];
    private readonly Dictionary<int, CellRect> _buildingFootprints = [];
    private readonly Dictionary<int, int> _buildingOwners = [];
    private readonly Dictionary<int, int> _houseYardCells = [];
    private readonly Dictionary<int, int> _houseYardOwners = [];

    public IReadOnlyDictionary<int, CellRect> BuildingFootprints => _buildingFootprints;
    public IReadOnlyDictionary<int, int> HouseYardCells => _houseYardCells;
    public Dictionary<int, int> BuildingRotations { get; } = [];

    public int BuildingAnchor(int index) => _buildingOwners.GetValueOrDefault(index, index);
    public bool IsBuildingAnchor(int index) => BuildingAnchor(index) == index;
    public int PathWearAt(int x, int y) => PathLayer[Index(x, y)];

    internal int HouseYardOwner(int index)
    {
        if (!_houseYardOwners.TryGetValue(index, out int owner) ||
            !_buildingFootprints.TryGetValue(owner, out var area) ||
            !Rendering.HouseYardLayout.IsAdjoining(area, PosOf(index)) ||
            ZoneLayer[index] != ZoneType.Residential || BuildingLayer[index] != 0 ||
            FeatureLayer[index] != 0 || RoadLayer[index] || !Content.Terrains[TerrainLayer[index]].Buildable)
            return -1;
        var cell = PosOf(index);
        return JunctionRoadLayout?.AtCell(cell.X, cell.Y).Any(segment =>
            Rendering.RoadMeshLayout.OverlapsCell(segment.Corridor, cell.X, cell.Y)) == true ? -1 : owner;
    }

    internal bool IsHouseYardCell(int index) => HouseYardOwner(index) >= 0;

    public void SetPathWear(int x, int y, int wear)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, Width);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        ArgumentOutOfRangeException.ThrowIfNegative(wear);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(wear, 12);
        int index = Index(x, y);
        if ((PathLayer[index] >= 3) != (wear >= 3)) TravelCache.Clear();
        PathLayer[index] = (byte)wear;
        if (wear == 0) ClearPathLinks(index);
    }

    internal static int PathDirection(int dx, int dy) => (dx, dy) switch
    {
        (0, -1) => 1, (1, 0) => 2, (0, 1) => 4, (-1, 0) => 8, _ => 0,
    };

    internal bool HasPathLink(int first, int last)
    {
        int mask = PathDirection(last % Width - first % Width, last / Width - first / Width);
        int reverse = (mask << 2 | mask >> 2) & 15;
        return mask != 0 && (PathLinkLayer[first] & mask) != 0 && (PathLinkLayer[last] & reverse) != 0;
    }

    internal bool LinkPathCells(int first, int last)
    {
        int mask = PathDirection(last % Width - first % Width, last / Width - first / Width);
        if (mask == 0) throw new ArgumentException("Path links require orthogonally adjacent cells.", nameof(last));
        int reverse = (mask << 2 | mask >> 2) & 15;
        bool changed = (PathLinkLayer[first] & mask) == 0 || (PathLinkLayer[last] & reverse) == 0;
        PathLinkLayer[first] |= (byte)mask;
        PathLinkLayer[last] |= (byte)reverse;
        return changed;
    }

    private void ClearPathLinks(int index)
    {
        var cell = PosOf(index);
        foreach (var (dx, dy) in Simulation.RoadNetwork.Neighbors)
        {
            if (!InBounds(cell.X + dx, cell.Y + dy)) continue;
            int reverse = PathDirection(-dx, -dy);
            PathLinkLayer[Index(cell.X + dx, cell.Y + dy)] &= (byte)(15 ^ reverse);
        }
        PathLinkLayer[index] = 0;
    }

    public CellRect BuildingFootprintAt(int x, int y)
    {
        int index = Index(x, y);
        return _buildingOwners.TryGetValue(index, out int owner)
            ? _buildingFootprints[owner] : new CellRect(x, y, 1, 1);
    }

    public bool BuildingIsServed(int index, Func<int, bool> served) =>
        BuildingFootprintAt(index % Width, index / Width).Cells().Any(p => served(Index(p.X, p.Y)));

    public void SetBuildingFootprint(BuildingType building, CellRect area)
    {
        int owner = Index(area.X, area.Y);
        int? yard = building.Zone == ZoneType.Residential && _houseYardCells.TryGetValue(owner, out int cell)
            ? cell : null;
        foreach (var p in area.Cells())
        {
            SetFeature(p.X, p.Y, null);
            SetBuilding(p.X, p.Y, building);
        }
        RegisterBuildingFootprint(area);
        if (yard is { } yardCell) RegisterHouseYard(owner, yardCell);
    }

    internal void RegisterHouseYard(int owner, int cell)
    {
        if (_houseYardCells.TryGetValue(owner, out int existing))
        {
            if (existing != cell) throw new InvalidOperationException("An established house yard cannot be relocated.");
            return;
        }
        _houseYardCells.Add(owner, cell);
        _houseYardOwners.Add(cell, owner);
        PathRoutes = null;
        BuildingPaths = null;
    }

    private void RemoveHouseYard(int owner)
    {
        if (_houseYardCells.Remove(owner, out int cell)) _houseYardOwners.Remove(cell);
    }

    internal void RegisterBuildingFootprint(CellRect area)
    {
        int owner = Index(area.X, area.Y);
        _buildingFootprints.Add(owner, area);
        foreach (var p in area.Cells())
        {
            int index = Index(p.X, p.Y);
            _buildingOwners.Add(index, owner);
            if (index != owner) _serviceCells.Remove(index);
        }
    }

    public GameMap(int width, int height, GameContent content)
    {
        if (width < 8 || height < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Map must be at least 8x8.");
        }

        Width = width;
        Height = height;
        Content = content;

        int count = width * height;
        TerrainLayer = new byte[count];
        SeaLayer = new bool[count];
        RiverLayer = new bool[count];
        FeatureLayer = new byte[count];
        RoadLayer = new bool[count];
        RoadTypeLayer = new byte[count];
        PlayerRoadLayer = new bool[count];
        ZoneLayer = new ZoneType[count];
        BuildingLayer = new byte[count];
        HouseholdLayer = new Household[count];
        PathLayer = new byte[count];
        PathLinkLayer = new byte[count];

        Array.Fill(TerrainLayer, content.Terrains.Base.Id);
    }

    public int Width { get; }

    public int Height { get; }

    public GameContent Content { get; }
    public int TerrainVersion { get; private set; }

    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public bool InBounds(Pos p) => InBounds(p.X, p.Y);

    /// <summary>True when open water lies within <paramref name="reach"/> cells (a square) of the cell.</summary>
    public bool NearWater(int x, int y, int reach)
    {
        var water = Content.Terrains.Find(DefaultTerrains.WaterName);
        if (water is null)
        {
            return false;
        }

        for (int dy = -reach; dy <= reach; dy++)
        {
            for (int dx = -reach; dx <= reach; dx++)
            {
                if (InBounds(x + dx, y + dy) && TerrainLayer[Index(x + dx, y + dy)] == water.Id)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool IsEdge(int x, int y) => x == 0 || y == 0 || x == Width - 1 || y == Height - 1;

    public int Index(int x, int y) => y * Width + x;

    public Pos PosOf(int index) => new(index % Width, index / Width);

    public TerrainType TerrainAt(int x, int y) => Content.Terrains[TerrainLayer[Index(x, y)]];

    public bool IsSea(int x, int y) => InBounds(x, y) && SeaLayer[Index(x, y)];
    public bool IsRiver(int x, int y) => InBounds(x, y) && RiverLayer[Index(x, y)];

    public void SetRiverWater(int x, int y, bool river)
    {
        if (river && (TerrainAt(x, y).Name != DefaultTerrains.WaterName || IsSea(x, y)))
            throw new InvalidOperationException("River classification requires non-sea water.");
        int index = Index(x, y);
        if (RiverLayer[index] == river) return;
        RiverLayer[index] = river;
        WaterFlow = null;
        TerrainVersion++;
    }

    public void SetSeaWater(int x, int y, bool sea)
    {
        if (sea && TerrainAt(x, y).Name != DefaultTerrains.WaterName)
            throw new InvalidOperationException("Sea classification requires water terrain.");
        int index = Index(x, y);
        if (SeaLayer[index] == sea) return;
        SeaLayer[index] = sea;
        if (sea) RiverLayer[index] = false;
        WaterFlow = null;
        CoastDistances = null;
        TerrainVersion++;
    }

    public void SetTerrain(int x, int y, TerrainType terrain)
    {
        if (TerrainLayer[Index(x, y)] == terrain.Id) return;
        TerrainLayer[Index(x, y)] = terrain.Id;
        SeaLayer[Index(x, y)] = false;
        RiverLayer[Index(x, y)] = false;
        CoastDistances = null;
        TerrainVersion++;
        HillLevels = null;
        CoastalHillCaps = null;
        WaterDensities = null;
        WaterFlow = null;
        InvalidateTravel();
    }

    internal void InvalidateTravel()
    {
        TravelCache.Clear();
        PathRoutes = null;
        BuildingPaths = null;
        JunctionDelays = null;
        TravelRoadPaths = null;
        JunctionRoadLayout = null;
        FootpathHeights = null;
        FootpathBridgeCells = null;
    }

    public FeatureType? FeatureAt(int x, int y)
    {
        byte id = FeatureLayer[Index(x, y)];
        return id == 0 ? null : Content.Features[id];
    }

    public void SetFeature(int x, int y, FeatureType? feature)
    {
        int index = Index(x, y);
        if (FeatureLayer[index] == (feature?.Id ?? 0)) return;
        FeatureLayer[index] = feature?.Id ?? 0;
        if (_houseYardOwners.ContainsKey(index))
        {
            PathRoutes = null;
            BuildingPaths = null;
        }
    }

    public bool HasRoad(int x, int y) => InBounds(x, y) && RoadLayer[Index(x, y)];

    /// <summary>Adds (as the default road type) or removes a road. An existing road keeps its type.</summary>
    public void SetRoad(int x, int y, bool value)
    {
        int i = Index(x, y);
        if (RoadLayer[i] == value)
        {
            return;
        }

        RoadLayer[i] = value;
        InvalidateTravel();
        if (value)
        {
            RoadTypeLayer[i] = Content.Roads.Default.Id;
            _roadCells.Add(i);
        }
        else
        {
            RoadTypeLayer[i] = 0;
            PlayerRoadLayer[i] = false;
            _roadCells.Remove(i);
        }
    }

    /// <summary>Places a road of the given type, replacing the type of any road already there.</summary>
    public void SetRoad(int x, int y, RoadType type, bool playerBuilt = false)
    {
        bool wasRoad = HasRoad(x, y);
        SetRoad(x, y, true);
        int i = Index(x, y);
        RoadTypeLayer[i] = type.Id;
        InvalidateTravel();
        if (!wasRoad)
        {
            PlayerRoadLayer[i] = playerBuilt;
        }
    }

    /// <summary>The type of road at a cell, or null if there is none.</summary>
    public RoadType? RoadTypeAt(int x, int y)
    {
        if (!InBounds(x, y))
        {
            return null;
        }

        int i = Index(x, y);
        return RoadLayer[i] ? Content.Roads[RoadTypeLayer[i]] : null;
    }

    /// <summary>Indexes of all road cells.</summary>
    public IReadOnlyCollection<int> RoadCells => _roadCells;

    public int RoadCount => _roadCells.Count;

    /// <summary>Indexes of every civic (service) building.</summary>
    public IReadOnlyCollection<int> ServiceCells => _serviceCells;

    /// <summary>Indexes of all cells designated for a zone type (enumeration order is not significant).</summary>
    public IReadOnlyCollection<int> ZoneCells(ZoneType zone) => _zoneCells[(int)zone];

    /// <summary>Rebuilds the sparse indexes after the layers were filled in directly (loading a save).</summary>
    internal void RebuildIndexes()
    {
        _roadCells.Clear();
        _serviceCells.Clear();
        foreach (var set in _zoneCells)
        {
            set.Clear();
        }

        for (int i = 0; i < RoadLayer.Length; i++)
        {
            if (RoadLayer[i])
            {
                _roadCells.Add(i);
            }

            if (ZoneLayer[i] != ZoneType.None)
            {
                _zoneCells[(int)ZoneLayer[i]].Add(i);
            }

            if (BuildingLayer[i] != 0 && Content.Buildings[BuildingLayer[i]].IsService &&
                (!_buildingOwners.TryGetValue(i, out int owner) || owner == i))
            {
                _serviceCells.Add(i);
            }
        }
    }

    public ZoneType ZoneAt(int x, int y) => ZoneLayer[Index(x, y)];

    public void SetZone(int x, int y, ZoneType zone)
    {
        int i = Index(x, y);
        var old = ZoneLayer[i];
        if (old == zone)
        {
            return;
        }

        ZoneLayer[i] = zone;
        if (_houseYardOwners.ContainsKey(i))
        {
            PathRoutes = null;
            BuildingPaths = null;
        }
        _zoneCells[(int)old].Remove(i);
        _zoneCells[(int)zone].Add(i);
        if (zone != ZoneType.None)
        {
            ZoneRemovals.Remove(i);
        }
    }

    public BuildingType? BuildingAt(int x, int y)
    {
        byte id = BuildingLayer[Index(x, y)];
        return id == 0 ? null : Content.Buildings[id];
    }

    public void SetBuilding(int x, int y, BuildingType? building)
    {
        PathRoutes = null;
        BuildingPaths = null;
        int i = Index(x, y);
        if (_buildingOwners.TryGetValue(i, out int owner))
        {
            var footprint = _buildingFootprints[owner];
            _buildingFootprints.Remove(owner);
            foreach (var p in footprint.Cells())
            {
                int cell = Index(p.X, p.Y);
                _buildingOwners.Remove(cell);
                BuildingLayer[cell] = 0;
                HouseholdLayer[cell] = default;
                ZoneRemovals.Remove(cell);
                _serviceCells.Remove(cell);
            }
            BuildingEstablishedDays.Remove(owner);
            BuildingRotations.Remove(owner);
            RemoveHouseYard(owner);
        }
        BuildingRotations.Remove(i);
        RemoveHouseYard(i);
        BuildingLayer[i] = building?.Id ?? 0;
        ZoneRemovals.Remove(i);
        if (building is { IsService: true })
        {
            _serviceCells.Add(i);
        }
        else
        {
            _serviceCells.Remove(i);
        }
    }

    public ZoneRemoval? ZoneRemovalAt(int x, int y) => ZoneRemovals.GetValueOrDefault(Index(x, y));

    public Household HouseholdAt(int x, int y) => HouseholdLayer[BuildingAnchor(Index(x, y))];

    public void SetHousehold(int x, int y, Household household) => HouseholdLayer[BuildingAnchor(Index(x, y))] = household;

    /// <summary>A zoned cell is "filled" once residents (or businesses) have moved in and built something.</summary>
    public bool IsFilled(int x, int y) => ZoneAt(x, y) != ZoneType.None && BuildingLayer[Index(x, y)] != 0;

    /// <summary>Removes everything the player can build from a cell, including natural features.</summary>
    public void ClearCell(int x, int y)
    {
        int i = Index(x, y);
        SetRoad(x, y, false);
        SetZone(x, y, ZoneType.None);
        SetBuilding(x, y, null);
        HouseholdLayer[i] = default;
        FeatureLayer[i] = 0;
        SetPathWear(x, y, 0);
        InvalidateTravel();
    }
}
