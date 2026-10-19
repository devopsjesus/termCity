using System.Numerics;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

public static class RiverRippleLayout
{
    public const float Speed = 0.75f;
    public const int Spacing = 8;
    private static readonly Pos[] Directions = [new(1, 0), new(0, 1), new(-1, 0), new(0, -1)];

    public static double WaveOffset(int wave)
    {
        int seed = (wave % 251 + 251) % 251;
        seed = (seed + 19) * (seed + 73) % 251;
        seed = (seed * (seed + 47) + 31) % 251;
        return 0.25 + seed / 251.0 * 2.5;
    }

    public readonly record struct Track(Pos Incoming, Pos Outgoing, int Distance, bool River)
    {
        public double Phase(double clock)
        {
            double travel = clock * Speed + Distance;
            int wave = (int)Math.Floor(travel / Spacing);
            return travel - wave * Spacing - WaveOffset(wave);
        }

        public Vector2? Position(double clock)
        {
            double phase = Phase(clock);
            if (!River || phase < -0.000000001 || phase >= 1) return null;
            float amount = (float)Math.Max(0, phase);
            return new Vector2(0.5f) - new Vector2(Incoming.X, Incoming.Y) * (0.5f * (1 - amount) * (1 - amount)) +
                new Vector2(Outgoing.X, Outgoing.Y) * (0.5f * amount * amount);
        }
    }

    public static Track At(GameMap map, WaterFlowField flow, int x, int y)
    {
        var cell = flow.At(x, y);
        var incoming = cell.Direction;
        bool found = false;
        foreach (var direction in Directions)
        {
            int upstreamX = x - direction.X, upstreamY = y - direction.Y;
            if (!map.InBounds(upstreamX, upstreamY)) continue;
            var upstream = flow.At(upstreamX, upstreamY);
            if (!upstream.River || upstream.Direction != direction || upstream.Distance != cell.Distance + 1) continue;
            if (!found || direction == cell.Direction) incoming = direction;
            found = true;
            if (direction == cell.Direction) break;
        }
        return new(incoming, cell.Direction, cell.Distance, cell.River && cell.Direction != new Pos(0, 0));
    }
}
