using TermCity.Core.Rendering;
using TermCity.Core.World;

namespace TermCity.Core.Simulation;

/// <summary>
/// Which road cells are connected to the outside world (via a road that reaches the map edge),
/// and which cells are close enough to such a road to be served by it.
/// Computation only visits roads and their surroundings, so it does not scale with the size of the map.
/// </summary>
public sealed class RoadNetwork
{
    private readonly bool[] _connected;
    private readonly bool[] _served;
    private readonly byte[] _access;
    private readonly RoadGeometry _geometry;
    private readonly int _width;

    private RoadNetwork(bool[] connected, bool[] served, byte[] access, int connectedCount, int trafficCapacity,
        RoadGeometry geometry, int width, IReadOnlyList<RoadPath> paths)
    {
        _connected = connected;
        _served = served;
        _access = access;
        _geometry = geometry;
        _width = width;
        DrawnPaths = paths;
        ConnectedRoadCount = connectedCount;
        TrafficCapacity = trafficCapacity;
    }

    public int ConnectedRoadCount { get; }

    /// <summary>Total trip capacity of every connected road (bigger road types carry more).</summary>
    public int TrafficCapacity { get; }

    internal IReadOnlyList<RoadPath> DrawnPaths { get; }

    /// <summary>Rank (1 street, 2 avenue, 3 highway...) of the best connected road reaching a cell; 0 when unserved.</summary>
    public int AccessRank(int index) => _access[index];

    public bool IsConnected(GameMap map, int x, int y) => map.InBounds(x, y) && _connected[map.Index(x, y)];

    public bool IsServed(GameMap map, int x, int y) => map.InBounds(x, y) && _served[map.Index(x, y)];

    public bool IsServed(int index) => _served[index];

    public bool RoadOverlapsCell(int index) => _geometry.OverlapsCell(index % _width, index / _width);

    public static RoadNetwork Compute(GameMap map, int serviceReach)
    {
        int width = map.Width, height = map.Height;
        var connected = new bool[width * height];
        var served = new bool[width * height];
        var connectedCells = new List<int>();

        // Start from road cells on the map border: those lead off the map to the outside world.
        void Seed(int x, int y)
        {
            int i = y * width + x;
            if (map.RoadLayer[i] && !connected[i])
            {
                connected[i] = true;
                connectedCells.Add(i);
            }
        }

        for (int x = 0; x < width; x++)
        {
            Seed(x, 0);
            Seed(x, height - 1);
        }

        for (int y = 1; y < height - 1; y++)
        {
            Seed(0, y);
            Seed(width - 1, y);
        }

        for (int head = 0; head < connectedCells.Count; head++)
        {
            int index = connectedCells[head];
            int x = index % width, y = index / width;
            foreach (var (dx, dy) in Neighbors)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                {
                    continue;
                }

                int ni = ny * width + nx;
                if (map.RoadLayer[ni] && !connected[ni])
                {
                    connected[ni] = true;
                    connectedCells.Add(ni);
                }
            }
        }

        // Service spreads outward from connected roads across open ground, one ring per step, up to the reach.
        var buildable = new bool[256];
        foreach (var terrain in map.Content.Terrains)
        {
            buildable[terrain.Id] = terrain.Buildable;
        }

        int connectedCount = connectedCells.Count;
        foreach (int i in connectedCells)
        {
            served[i] = true;
        }

        var ranks = new int[256];
        var capacities = new int[256];
        foreach (var road in map.Content.Roads)
        {
            ranks[road.Id] = road.Rank;
            capacities[road.Id] = road.TrafficCapacity;
        }

        int trafficCapacity = 0;
        int maxRank = 0;
        foreach (int i in connectedCells)
        {
            int id = map.RoadTypeLayer[i];
            trafficCapacity += capacities[id];
            maxRank = Math.Max(maxRank, ranks[id]);
        }

        var access = new byte[width * height];
        for (int rank = maxRank; rank >= 1; rank--)
        {
            var ring = new List<int>();
            foreach (int i in connectedCells)
            {
                if (ranks[map.RoadTypeLayer[i]] == rank && access[i] == 0)
                {
                    access[i] = (byte)rank;
                    ring.Add(i);
                }
            }

            var after = new List<int>();
            for (int step = 0; step < serviceReach && ring.Count > 0; step++)
            {
                after.Clear();
                foreach (int index in ring)
                {
                    int x = index % width, y = index / width;
                    foreach (var (dx, dy) in Neighbors)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        {
                            continue;
                        }

                        int ni = ny * width + nx;
                        if (access[ni] == 0 && buildable[map.TerrainLayer[ni]])
                        {
                            access[ni] = (byte)rank;
                            after.Add(ni);
                        }
                    }
                }

                (ring, after) = (after, ring);
            }
        }

        var frontier = connectedCells;
        var next = new List<int>();
        for (int step = 0; step < serviceReach && frontier.Count > 0; step++)
        {
            next.Clear();
            foreach (int index in frontier)
            {
                int x = index % width, y = index / width;
                foreach (var (dx, dy) in Neighbors)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    {
                        continue;
                    }

                    int ni = ny * width + nx;
                    if (!served[ni] && buildable[map.TerrainLayer[ni]])
                    {
                        served[ni] = true;
                        next.Add(ni);
                    }
                }
            }

            (frontier, next) = (next, frontier);
        }

        var paths = RoadCurves.Extract(map, (x, y) => connected[map.Index(x, y)]);
        var geometry = new RoadGeometry(map, paths);
        foreach (var (index, rank) in geometry.NearbyAccess)
        {
            served[index] = true;
            access[index] = (byte)Math.Max(access[index], rank);
        }

        return new RoadNetwork(connected, served, access, connectedCount, trafficCapacity, geometry, width, paths);
    }

    public static readonly (int Dx, int Dy)[] Neighbors = [(0, -1), (1, 0), (0, 1), (-1, 0)];
}
