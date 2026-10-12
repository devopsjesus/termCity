using TermCity.Core.Terrain;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

public sealed class WaterFlowField
{
    public readonly record struct Cell(Pos Direction, int Distance, bool River, float Level);
    private readonly Cell[] _cells;
    private readonly GameMap _map;

    public WaterFlowField(GameMap map)
    {
        _map = map;
        int count = map.Width * map.Height;
        _cells = new Cell[count];
        var water = new bool[count];
        byte waterId = map.Content.Terrains.Get(DefaultTerrains.WaterName).Id;
        for (int i = 0; i < count; i++) water[i] = map.TerrainLayer[i] == waterId;
        IEnumerable<int> Neighbors(int cell)
        {
            int x = cell % map.Width, y = cell / map.Width;
            foreach (var (dx, dy) in Simulation.RoadNetwork.Neighbors)
                if (map.InBounds(x + dx, y + dy)) yield return map.Index(x + dx, y + dy);
        }
        var width = new int[count];
        var queue = new Queue<int>();
        for (int i = 0; i < count; i++)
            if (water[i] && Neighbors(i).Any(next => !water[next]))
            {
                width[i] = 1;
                queue.Enqueue(i);
            }
        while (queue.TryDequeue(out int cell))
            foreach (int next in Neighbors(cell))
                if (water[next] && width[next] == 0)
                {
                    width[next] = width[cell] + 1;
                    queue.Enqueue(next);
                }
        var visited = new bool[count];
        var distance = new int[count];
        var downstream = new int[count];
        Array.Fill(distance, -1);
        Array.Fill(downstream, -1);
        for (int start = 0; start < count; start++)
        {
            if (!water[start] || visited[start]) continue;
            var component = new List<int>();
            queue.Enqueue(start);
            visited[start] = true;
            while (queue.TryDequeue(out int cell))
            {
                component.Add(cell);
                foreach (int next in Neighbors(cell))
                    if (water[next] && !visited[next]) { visited[next] = true; queue.Enqueue(next); }
            }
            var outlets = component.Where(cell => map.SeaLayer[cell]).ToArray();
            if (outlets.Length == 0)
                outlets = component.Where(cell => !map.RiverLayer[cell] && width[cell] >= 4).ToArray();
            if (component.Count == count) outlets = component.ToArray();
            bool broadBody = outlets.Length > 0;
            if (!broadBody)
            {
                var edges = component.Where(cell => map.IsEdge(cell % map.Width, cell / map.Width)).ToArray();
                var candidates = edges.Length > 0 ? edges : component.ToArray();
                outlets = [candidates.OrderByDescending(cell => width[cell])
                    .ThenByDescending(cell => cell / map.Width).ThenBy(cell => cell % map.Width).First()];
            }
            foreach (int outlet in outlets) { distance[outlet] = 0; queue.Enqueue(outlet); }
            while (queue.TryDequeue(out int cell))
                foreach (int next in Neighbors(cell))
                    if (water[next] && distance[next] < 0)
                    {
                        distance[next] = distance[cell] + 1;
                        downstream[next] = cell;
                        queue.Enqueue(next);
                    }
            bool classifiedRiver = component.Any(cell => map.RiverLayer[cell]);
            int riverStart = broadBody ? classifiedRiver ? 3 : 6 : 0;
            int ChannelCost(int cell, int next)
            {
                int column = cell % map.Width, row = cell / map.Width;
                int directionX = next % map.Width - column, directionY = next / map.Width - row;
                int cost = Math.Abs(width[next] - width[cell]) * 8;
                for (int step = 2; step <= 4; step++)
                {
                    int aheadX = column + directionX * step, aheadY = row + directionY * step;
                    if (!map.InBounds(aheadX, aheadY)) break;
                    int ahead = map.Index(aheadX, aheadY);
                    if (!water[ahead])
                    {
                        cost += width[cell] + 4;
                        break;
                    }
                    cost += Math.Abs(width[ahead] - width[cell]);
                }
                return cost;
            }
            foreach (int cell in component)
            {
                bool river = !map.SeaLayer[cell] && (!classifiedRiver || map.RiverLayer[cell]) &&
                    distance[cell] > riverStart;
                if (river && downstream[cell] >= 0)
                {
                    int bestCost = ChannelCost(cell, downstream[cell]);
                    foreach (int next in Neighbors(cell))
                    {
                        if (!water[next] || distance[next] != distance[cell] - 1) continue;
                        int cost = ChannelCost(cell, next);
                        if (cost >= bestCost) continue;
                        bestCost = cost;
                        downstream[cell] = next;
                    }
                }
                var direction = downstream[cell] < 0 ? new Pos(0, 0) :
                    map.PosOf(downstream[cell]).Offset(-(cell % map.Width), -(cell / map.Width));
                float level = -0.16f + Math.Min(0.12f, Math.Max(0, distance[cell] - 3) * 0.001f);
                _cells[cell] = new(direction, distance[cell], river, level);
            }
        }
    }

    public Cell At(int x, int y)
    {
        if (!_map.InBounds(x, y)) throw new ArgumentOutOfRangeException(nameof(x), "Flow lookup must be inside the map.");
        return _cells[_map.Index(x, y)];
    }
}
