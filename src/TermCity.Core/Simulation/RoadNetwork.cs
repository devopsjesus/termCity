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

    private RoadNetwork(bool[] connected, bool[] served, int connectedCount)
    {
        _connected = connected;
        _served = served;
        ConnectedRoadCount = connectedCount;
    }

    public int ConnectedRoadCount { get; }

    public bool IsConnected(GameMap map, int x, int y) => map.InBounds(x, y) && _connected[map.Index(x, y)];

    public bool IsServed(GameMap map, int x, int y) => map.InBounds(x, y) && _served[map.Index(x, y)];

    public bool IsServed(int index) => _served[index];

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

        var frontier = new List<int>(connectedCells);
        foreach (int i in frontier)
        {
            served[i] = true;
        }

        for (int step = 0; step < serviceReach && frontier.Count > 0; step++)
        {
            var next = new List<int>();
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

            frontier = next;
        }

        return new RoadNetwork(connected, served, connectedCells.Count);
    }

    public static readonly (int Dx, int Dy)[] Neighbors = [(0, -1), (1, 0), (0, 1), (-1, 0)];
}
