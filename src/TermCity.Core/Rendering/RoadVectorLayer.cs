using TermCity.Core.Simulation;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

/// <summary>A square block of the map's road drawing, as straight-alpha RGBA pixels.</summary>
public sealed class RoadChunk(int width, int height, byte[] rgba)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public byte[] Rgba { get; } = rgba;
}

/// <summary>
/// The curved drawing of the roads. It extracts the road curves from the map and rasterises them in blocks of cells,
/// each a transparent image to lay over the terrain. Every road is the same signed-distance field, and the fields of
/// different roads are blended with a smooth minimum, so merges and crossings grow fillets instead of sharp corners.
/// </summary>
public sealed class RoadVectorLayer
{
    public const int ChunkCells = 16;

    /// <summary>Distance from the middle of the road to each of its two edge lines.</summary>
    public const double LineOffset = 5.0;

    public const double LineHalfThickness = 0.9;

    /// <summary>Half the width of the paved bed, in pixels at normal size: it ends at the outer edge of the lines.</summary>
    public const double BedHalfWidth = LineOffset + LineHalfThickness;

    /// <summary>Zoomed out, half the width of a highway's bed, the offset of each of its two yellow lines, and their half thickness, in screen pixels.</summary>
    public const double HighwayBedHalf = 2.8, HighwayLineOffset = 1.5, HighwayLineHalf = 0.6;

    /// <summary>How far apart two roads can be and still be pulled together into one shape.</summary>
    public const double Blend = 6.0;

    /// <summary>
    /// How much of its normal width a road keeps when each screen cell stands for <paramref name="stride"/> map cells.
    /// Zoomed out, the roads thin out so they stay a quiet backdrop to the terrain and buildings.
    /// </summary>
    public static double Weight(int stride) => stride <= 1 ? 1.0 : Math.Max(0.15, 1.0 / stride);

    /// <summary>How opaque the paved bed is when zoomed out; the terrain shows through more with every step.</summary>
    public static double Opacity(int stride) => stride <= 1 ? 1.0 : Math.Max(0.4, 1.0 - 0.15 * Math.Log2(stride));

    /// <summary>
    /// Whether a road of this rank is drawn at all when zoomed out: the minor streets drop out first, leaving the
    /// main roads, and at the widest zoom only the highest rank remains.
    /// </summary>
    public static bool Shown(int rank, int topRank, int stride) =>
        stride < 8 || (stride < 16 ? rank * 2 > topRank : rank >= topRank);

    /// <summary>Minor roads are fainter than major ones when zoomed out.</summary>
    public static double RankOpacity(int rank, int topRank, int stride) =>
        stride <= 1 || topRank <= 0 ? 1.0 : 0.45 + 0.55 * Math.Clamp((double)rank / topRank, 0, 1);

    private GameMap? _map;
    private RoadNetwork? _network;
    private IReadOnlyList<RoadPath> _paths = [];

    /// <summary>Changes whenever the road curves do, so callers know when to discard what they rendered.</summary>
    public int Revision { get; private set; }

    public IReadOnlyList<RoadPath> Paths => _paths;

    /// <summary>Re-reads the roads if they changed since last time. Returns true when they did.</summary>
    public bool Sync(CityGame game)
    {
        var network = game.Network;
        if (ReferenceEquals(game.Map, _map) && ReferenceEquals(network, _network))
        {
            return false;
        }

        var map = game.Map;
        _map = map;
        _network = network;
        _paths = network.DrawnPaths;
        Revision++;
        return true;
    }

    /// <summary>
    /// Renders one block of the road drawing, or null if no road touches it. Scale is 1 normally and 2 when zoomed in.
    /// When the map is zoomed out each screen cell stands for <paramref name="stride"/> x <paramref name="stride"/> map
    /// cells: the block then spans <see cref="ChunkCells"/> screen cells and the road keeps its width on screen.
    /// </summary>
    public RoadChunk? Render(int chunkX, int chunkY, int scale, int stride = 1)
    {
        if (_map is not { } map || _paths.Count == 0)
        {
            return null;
        }

        const int cw = RoadCurves.CellWidth, ch = RoadCurves.CellHeight;
        double x0 = (double)chunkX * ChunkCells * cw * stride, y0 = (double)chunkY * ChunkCells * ch * stride;
        int width = ChunkCells * cw * scale, height = ChunkCells * ch * scale;
        double weight = Weight(stride), opacity = Opacity(stride);
        double bedHalf = Math.Max(0.9, BedHalfWidth * weight), blend = Blend * weight;
        int topRank = map.Content.Roads.Max(r => r.Rank);
        bool lines = stride <= 1;
        bool marked = stride > 1;
        double reach = (Math.Max(bedHalf + blend, HighwayBedHalf) + 2) * stride;
        double unit = (double)stride / scale;

        var near = new List<RoadPath>();
        foreach (var path in _paths)
        {
            if (!Shown(path.Type.Rank, topRank, stride))
            {
                continue;
            }

            if (path.MaxX + reach >= x0 && path.MinX - reach <= x0 + width * unit &&
                path.MaxY + reach >= y0 && path.MinY - reach <= y0 + height * unit)
            {
                near.Add(path);
            }
        }

        if (near.Count == 0)
        {
            return null;
        }

        int total = width * height;
        var blended = new float[total];
        var nearest = new float[total];
        var owner = new ushort[total];
        var top = marked ? new ushort[total] : [];
        var local = new float[total];
        var highway = marked ? new float[total] : [];
        if (marked)
        {
            Array.Fill(highway, float.PositiveInfinity);
        }
        Array.Fill(blended, float.PositiveInfinity);
        Array.Fill(nearest, float.PositiveInfinity);

        for (int p = 0; p < near.Count; p++)
        {
            var path = near[p];
            int rx0 = Math.Max(0, (int)Math.Floor((path.MinX - reach - x0) / unit));
            int ry0 = Math.Max(0, (int)Math.Floor((path.MinY - reach - y0) / unit));
            int rx1 = Math.Min(width - 1, (int)Math.Ceiling((path.MaxX + reach - x0) / unit));
            int ry1 = Math.Min(height - 1, (int)Math.Ceiling((path.MaxY + reach - y0) / unit));
            for (int y = ry0; y <= ry1; y++)
            {
                Array.Fill(local, float.PositiveInfinity, y * width + rx0, rx1 - rx0 + 1);
            }

            int segments = path.Closed ? path.Count : Math.Max(1, path.Count - 1);
            for (int i = 0; i < segments; i++)
            {
                int j = (i + 1) % path.Count;
                double ax = path.Xs[i], ay = path.Ys[i], bx = path.Xs[j], by = path.Ys[j];
                int sx0 = Math.Max(rx0, (int)Math.Floor((Math.Min(ax, bx) - reach - x0) / unit));
                int sy0 = Math.Max(ry0, (int)Math.Floor((Math.Min(ay, by) - reach - y0) / unit));
                int sx1 = Math.Min(rx1, (int)Math.Ceiling((Math.Max(ax, bx) + reach - x0) / unit));
                int sy1 = Math.Min(ry1, (int)Math.Ceiling((Math.Max(ay, by) + reach - y0) / unit));
                double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
                for (int y = sy0; y <= sy1; y++)
                {
                    double qy = y0 + (y + 0.5) * unit;
                    for (int x = sx0; x <= sx1; x++)
                    {
                        double qx = x0 + (x + 0.5) * unit;
                        double t = len2 < 1e-12 ? 0 : Math.Clamp(((qx - ax) * dx + (qy - ay) * dy) / len2, 0, 1);
                        double ex = qx - (ax + dx * t), ey = qy - (ay + dy * t);
                        float d = (float)(Math.Sqrt(ex * ex + ey * ey) / stride);
                        int at = y * width + x;
                        if (d < local[at])
                        {
                            local[at] = d;
                        }
                    }
                }
            }

            for (int y = ry0; y <= ry1; y++)
            {
                for (int x = rx0; x <= rx1; x++)
                {
                    int at = y * width + x;
                    float d = local[at];
                    if (float.IsPositiveInfinity(d))
                    {
                        continue;
                    }

                    if (d < nearest[at])
                    {
                        nearest[at] = d;
                        owner[at] = (ushort)p;
                    }

                    if (marked && path.Type.Rank >= topRank && d < highway[at])
                    {
                        highway[at] = d;
                        top[at] = (ushort)p;
                    }

                    blended[at] = SmoothMin(blended[at], d, (float)blend);
                }
            }
        }

        var rgba = new byte[total * 4];
        bool any = false;
        for (int y = 0; y < height; y++)
        {
            double qy = y0 + (y + 0.5) * unit;
            int cy = (int)Math.Floor(qy / ch);
            for (int x = 0; x < width; x++)
            {
                int at = y * width + x;
                float field = blended[at];
                float hw = marked ? highway[at] : float.PositiveInfinity;
                if (field > bedHalf + 1 && hw > HighwayBedHalf + 1)
                {
                    continue;
                }

                int cx = (int)Math.Floor((x0 + (x + 0.5) * unit) / cw);
                if (!map.InBounds(cx, cy))
                {
                    continue;
                }

                double bed = Coverage(bedHalf - field, scale);
                double line = lines ? Coverage(LineHalfThickness - Math.Abs(field - LineOffset), scale) : 0;
                double red = 0, green = 0, blue = 0, alpha = 0;
                if (bed > 0 || line > 0)
                {
                    var path = near[owner[at]];
                    var bedColour = path.Type.Background;
                    var lineColour = path.Connected ? path.Type.Foreground : CellRenderer.DisconnectedRoad;

                    // The bed is opaque: no terrain shows through between the lines, whatever lies under the road.
                    red = Blend8(bedColour.R, lineColour.R, line);
                    green = Blend8(bedColour.G, lineColour.G, line);
                    blue = Blend8(bedColour.B, lineColour.B, line);
                    alpha = Math.Max(bed, line) * opacity * RankOpacity(path.Type.Rank, topRank, stride);
                }

                if (hw <= HighwayBedHalf + 1)
                {
                    // Zoomed out, a highway keeps a narrow dark bed and a thin double line down its middle.
                    var path = near[top[at]];
                    var lineColour = path.Connected ? path.Type.Foreground : CellRenderer.DisconnectedRoad;
                    (red, green, blue, alpha) = Over(red, green, blue, alpha, path.Type.Background.R, path.Type.Background.G, path.Type.Background.B, Coverage(HighwayBedHalf - hw, scale) * 0.9);
                    double center = Coverage(HighwayLineHalf - Math.Abs(hw - HighwayLineOffset), scale);
                    (red, green, blue, alpha) = Over(red, green, blue, alpha, lineColour.R, lineColour.G, lineColour.B, center);
                }

                if (alpha <= 0)
                {
                    continue;
                }

                int o = at * 4;
                rgba[o] = (byte)Math.Round(red);
                rgba[o + 1] = (byte)Math.Round(green);
                rgba[o + 2] = (byte)Math.Round(blue);
                rgba[o + 3] = (byte)Math.Round(Math.Min(alpha, 1) * 255);
                any = true;
            }
        }

        return any ? new RoadChunk(width, height, rgba) : null;
    }

    /// <summary>Pixel coverage of a shape whose signed inset at the pixel centre is <paramref name="inside"/> (positive is inside).</summary>
    private static double Coverage(double inside, int scale) => Math.Clamp(0.5 + inside * scale, 0, 1);

    private static byte Blend8(byte from, byte to, double t) => (byte)Math.Round(from + (to - from) * t);

    private static (double R, double G, double B, double A) Over(double r, double g, double b, double a, double sr, double sg, double sb, double sa)
    {
        double outA = sa + a * (1 - sa);
        if (outA <= 1e-9)
        {
            return (0, 0, 0, 0);
        }

        return ((sr * sa + r * a * (1 - sa)) / outA, (sg * sa + g * a * (1 - sa)) / outA, (sb * sa + b * a * (1 - sa)) / outA, outA);
    }

    /// <summary>The smaller of two distances, pulled a little lower where they are close so that shapes fuse with a rounded join.</summary>
    internal static float SmoothMin(float a, float b, float k)
    {
        if (float.IsPositiveInfinity(a))
        {
            return b;
        }

        float h = Math.Max(k - Math.Abs(a - b), 0) / k;
        return Math.Min(a, b) - h * h * k * 0.25f;
    }
}
