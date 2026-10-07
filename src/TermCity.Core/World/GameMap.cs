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
    internal readonly byte[] FeatureLayer;
    internal readonly bool[] RoadLayer;
    internal readonly byte[] RoadTypeLayer;
    internal readonly ZoneType[] ZoneLayer;
    internal readonly byte[] BuildingLayer;
    internal readonly Household[] HouseholdLayer;
    internal readonly Dictionary<int, ZoneRemoval> ZoneRemovals = [];

    // Sparse indexes so work that only concerns roads or zones scales with how much has been built,
    // not with the size of the map. They are kept in step by SetRoad/SetZone/ClearCell.
    private readonly HashSet<int> _roadCells = [];
    private readonly HashSet<int>[] _zoneCells = [[], [], [], []];
    private readonly HashSet<int> _serviceCells = [];

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
        FeatureLayer = new byte[count];
        RoadLayer = new bool[count];
        RoadTypeLayer = new byte[count];
        ZoneLayer = new ZoneType[count];
        BuildingLayer = new byte[count];
        HouseholdLayer = new Household[count];

        Array.Fill(TerrainLayer, content.Terrains.Base.Id);
    }

    public int Width { get; }

    public int Height { get; }

    public GameContent Content { get; }

    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public bool InBounds(Pos p) => InBounds(p.X, p.Y);

    public bool IsEdge(int x, int y) => x == 0 || y == 0 || x == Width - 1 || y == Height - 1;

    public int Index(int x, int y) => y * Width + x;

    public Pos PosOf(int index) => new(index % Width, index / Width);

    public TerrainType TerrainAt(int x, int y) => Content.Terrains[TerrainLayer[Index(x, y)]];

    public void SetTerrain(int x, int y, TerrainType terrain) => TerrainLayer[Index(x, y)] = terrain.Id;

    public FeatureType? FeatureAt(int x, int y)
    {
        byte id = FeatureLayer[Index(x, y)];
        return id == 0 ? null : Content.Features[id];
    }

    public void SetFeature(int x, int y, FeatureType? feature) => FeatureLayer[Index(x, y)] = feature?.Id ?? 0;

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
        if (value)
        {
            RoadTypeLayer[i] = Content.Roads.Default.Id;
            _roadCells.Add(i);
        }
        else
        {
            RoadTypeLayer[i] = 0;
            _roadCells.Remove(i);
        }
    }

    /// <summary>Places a road of the given type, replacing the type of any road already there.</summary>
    public void SetRoad(int x, int y, RoadType type)
    {
        SetRoad(x, y, true);
        RoadTypeLayer[Index(x, y)] = type.Id;
    }

    /// <summary>The type of road at a cell, or null if there is none.</summary>
    public RoadType? RoadTypeAt(int x, int y)
    {
        if (!InBounds(x, y))
        {
            return null;
        }

        int i = Index(x, y);
        return RoadLayer[i] ? Content.Roads[RoadTypeLayer[i] == 0 ? Content.Roads.Default.Id : RoadTypeLayer[i]] : null;
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

            if (BuildingLayer[i] != 0 && Content.Buildings[BuildingLayer[i]].IsService)
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
        int i = Index(x, y);
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

    public Household HouseholdAt(int x, int y) => HouseholdLayer[Index(x, y)];

    public void SetHousehold(int x, int y, Household household) => HouseholdLayer[Index(x, y)] = household;

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
    }
}
