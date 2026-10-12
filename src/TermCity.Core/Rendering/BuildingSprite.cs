using TermCity.Core.Buildings;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.Core.Rendering;

/// <summary>Original oblique city-map artwork with roof-dominant volumes and compact walled plots.</summary>
public sealed record BuildingSprite(int Width, int Height, int Lift, byte[] Pixels)
{
    public Pos? Pick(CellRect footprint, double worldPixelX, double worldPixelY)
    {
        if (!double.IsFinite(worldPixelX) || !double.IsFinite(worldPixelY)) return null;
        int x = (int)Math.Floor(worldPixelX - footprint.X * RoadCurves.CellWidth);
        int y = (int)Math.Floor(worldPixelY - footprint.Y * RoadCurves.CellHeight + Lift);
        return x >= 0 && y >= 0 && x < Width && y < Height && Pixels[(y * Width + x) * 4 + 3] >= 128
            ? new Pos(footprint.X, footprint.Y) : null;
    }

    public static BuildingSprite Render(BuildingType type, int plotWidth, int plotHeight, int variant = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(plotWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(plotHeight);
        int width = Math.Max(16, plotWidth * RoadCurves.CellWidth);
        const int lift = 8;
        int height = plotHeight * RoadCurves.CellHeight + lift;
        var pixels = new byte[width * height * 4];
        var ink = Rgb.Hex(0x293534);
        var timber = Rgb.Hex(0x79634b);
        var plaster = Rgb.Hex(0xc6bf9c);
        var stone = Rgb.Hex(0x9b9e90);
        var slate = Rgb.Hex(0x586b71);
        var tile = Rgb.Hex(variant % 2 == 0 ? 0x956b50 : 0x866953);
        var thatch = Rgb.Hex(0xa69666);
        var grass = Rgb.Hex(0x53694a);
        var paving = Rgb.Hex(0x797d69);
        var glass = Rgb.Hex(0x304b53);
        int bottom = height - 3;

        void Pixel(int x, int y, Rgb color, byte alpha = 255)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            int i = (y * width + x) * 4;
            pixels[i] = color.R; pixels[i + 1] = color.G; pixels[i + 2] = color.B; pixels[i + 3] = alpha;
        }
        void Rect(int x, int y, int w, int h, Rgb color, byte alpha = 255)
        {
            for (int row = y; row < y + h; row++)
                for (int col = x; col < x + w; col++) Pixel(col, row, color, alpha);
        }
        void Line(int ax, int ay, int bx, int by, Rgb color)
        {
            foreach (var p in CellLines.Between(new(ax, ay), new(bx, by))) Pixel(p.X, p.Y, color);
        }
        void Polygon(Rgb color, params Pos[] points)
        {
            int minY = Math.Max(0, points.Min(p => p.Y)), maxY = Math.Min(height - 1, points.Max(p => p.Y));
            int minX = Math.Max(0, points.Min(p => p.X)), maxX = Math.Min(width - 1, points.Max(p => p.X));
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    bool inside = false;
                    for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
                    {
                        var a = points[i]; var b = points[j];
                        if ((a.Y > y + 0.5) != (b.Y > y + 0.5) &&
                            x + 0.5 < (b.X - a.X) * (y + 0.5 - a.Y) / (b.Y - a.Y) + a.X)
                            inside = !inside;
                    }
                    if (inside) Pixel(x, y, color);
                }
        }
        void Yard(int x, int y, int w, int d, Rgb color)
        {
            Rect(x + 1, y + 2, w, d, ink, 80);
            Rect(x, y, w, d, color);
            Line(x, y, x + w - 1, y, color.Scale(1.15));
            Line(x + w - 1, y, x + w - 1, y + d - 1, color.Scale(0.75));
            for (int row = y + 2; row < y + d; row += 4)
                for (int col = x + 2 + (row % 3); col < x + w - 1; col += 5)
                    Pixel(col, row, color.Scale(0.9));
        }
        void House(int x, int y, int w, int d, int wallHeight, Rgb wall, Rgb roof, bool framed = false)
        {
            int skew = Math.Min(4, Math.Max(2, w / 5)), rise = Math.Max(3, Math.Min(6, d / 3));
            int front = y + d, eave = front - wallHeight;
            int frontRight = x + w - skew;
            int ridgeY = y + d / 2 - wallHeight - rise;
            int ridgeLeft = x + skew / 2, ridgeRight = x + w - skew / 2;
            Polygon(ink, new(x + skew + 2, y + 3), new(x + w + 2, y + 3),
                new(frontRight + 2, front + 3), new(x + 2, front + 3));
            Polygon(wall.Scale(0.62), new(x + w, y - wallHeight), new(frontRight, eave),
                new(frontRight, front), new(x + w, y));
            Rect(x, eave, w - skew, wallHeight, wall);
            Polygon(wall.Scale(0.78), new(x + w, y - wallHeight), new(ridgeRight, ridgeY), new(frontRight, eave));
            Polygon(roof.Scale(0.75), new(x + skew, y - wallHeight), new(x + w, y - wallHeight),
                new(ridgeRight, ridgeY), new(ridgeLeft, ridgeY));
            Polygon(roof, new(ridgeLeft, ridgeY), new(ridgeRight, ridgeY),
                new(frontRight, eave), new(x, eave));
            Line(ridgeLeft, ridgeY, ridgeRight, ridgeY, roof.Scale(1.2));
            Line(x, eave, frontRight, eave, ink);
            for (int row = ridgeY + 3; row < eave - 1; row += 3)
            {
                int inset = (eave - row) * skew / Math.Max(1, eave - ridgeY) / 2;
                Line(x + inset + 1, row, frontRight + inset - 1, row, roof.Scale(0.9));
            }
            for (int col = x + 2; col < frontRight - 1; col += 5)
            {
                Rect(col, eave + 2, 2, Math.Min(3, Math.Max(1, wallHeight - 3)), glass);
                Pixel(col, eave + 2, wall.Scale(0.85));
                if (framed) Rect(col - 1, eave + 1, 1, wallHeight - 1, timber);
            }
            if (framed && wallHeight > 7) Line(x, eave + wallHeight / 2, frontRight - 1, eave + wallHeight / 2, timber);
            Rect(x + (w - skew) / 2, front - 3, 2, 3, timber.Scale(0.65));
            Line(x, front, frontRight, front, ink);
        }
        void FlatRoof(int x, int y, int w, int d, int wallHeight, Rgb wall)
        {
            Rect(x + 2, y + 3, w, d, ink, 100);
            Rect(x, y + d - wallHeight, w, wallHeight, wall);
            Rect(x + w - 3, y - wallHeight + 2, 3, d + wallHeight - 2, wall.Scale(0.65));
            Rect(x, y - wallHeight, w - 2, d, wall.Scale(1.1));
            Line(x, y - wallHeight, x + w - 3, y - wallHeight, plaster);
            Line(x, y - wallHeight, x, y + d - wallHeight - 1, plaster);
            for (int col = x + 2; col < x + w - 4; col += 4) Rect(col, y + d - wallHeight + 2, 2, 2, glass);
        }
        void Tower(int x, int y, int w, int wallHeight, bool spire = false)
        {
            FlatRoof(x, y, w, 7, wallHeight, stone);
            if (spire)
            {
                Polygon(slate, new(x - 1, y - wallHeight + 1), new(x + w / 2 - 1, y - wallHeight - 5),
                    new(x + w - 1, y - wallHeight + 1));
                Line(x, y - wallHeight, x + w / 2 - 1, y - wallHeight - 5, slate.Scale(1.35));
            }
            else
            {
                for (int col = x; col < x + w - 1; col += 3) Rect(col, y - wallHeight - 2, 2, 2, stone.Scale(1.2));
                for (int row = y - wallHeight; row < y + 6; row += 3) Pixel(x + w - 2, row, ink);
            }
            Rect(x + 2, y + 7 - wallHeight + 2, 1, 2, ink);
        }
        void Tree(int x, int y)
        {
            Rect(x + 2, y + 5, 1, 3, timber);
            Rect(x, y + 1, 5, 5, Rgb.Hex(0x34583e));
            Rect(x + 1, y, 3, 4, Rgb.Hex(0x718452));
            Rect(x, y + 2, 2, 2, Rgb.Hex(0x8c965c));
        }
        void Chimney(int x, int y, int h)
        {
            Rect(x + 1, y - h, 3, h, stone.Scale(0.6));
            Rect(x, y - h, 2, h, stone);
            Rect(x, y - h - 1, 4, 2, ink);
            Rect(x, y - h - 1, 3, 1, stone.Scale(1.15));
        }
        int lotTop = lift + 5;
        int lotDepth = height - lotTop - 3;
        int bodyWidth = Math.Max(8, width - 9);
        int bodyDepth = Math.Clamp(plotHeight * 9, 8, 22);
        int bodyY = Math.Max(lift + 9, bottom - bodyDepth - 5);

        if (type.IsMonument)
        {
            Yard(2, lotTop, width - 4, lotDepth, paving);
            int mid = width / 2;
            Rect(mid - 26, bottom - 12, 52, 10, stone.Scale(0.65));
            Rect(mid - 22, bottom - 16, 44, 5, stone);
            stone = Rgb.Hex(0xe3be48);
            Rect(mid - 15, bottom - 26, 12, 10, stone);
            Rect(mid + 3, bottom - 26, 12, 10, stone);
            Rect(mid - 15, bottom - 58, 30, 33, stone);
            Rect(mid - 19, bottom - 51, 38, 21, stone.Scale(0.85));
            Rect(mid - 17, bottom - 89, 34, 31, stone);
            Rect(mid - 14, bottom - 94, 28, 6, slate);
            Rect(mid - 19, bottom - 85, 38, 4, slate);
            Rect(mid - 12, bottom - 66, 24, 6, stone.Scale(0.8));
            Rect(mid - 7, bottom - 78, 3, 3, ink);
            Rect(mid + 4, bottom - 78, 3, 3, ink);
            Rect(mid - 23, bottom - 76, 7, 25, stone);
            Rect(mid + 16, bottom - 76, 7, 25, stone);
            Rect(mid - 23, bottom - 86, 9, 13, stone.Scale(1.1));
            Rect(mid + 14, bottom - 86, 9, 13, stone.Scale(1.1));
            Rect(mid - 12, bottom - 54, 24, 4, slate);
        }
        else if (type.Name is "Woodlot" or "Village Green" or "Charcoal Burners")
        {
            Yard(1, lotTop, width - 3, lotDepth, type.Name == "Charcoal Burners" ? paving.Scale(0.65) : grass);
            for (int y = lotTop + 2; y < bottom - 6; y += 9)
                for (int x = 3 + y % 3; x < width - 5; x += 7) Tree(x, y);
            if (type.Name == "Village Green")
            {
                Rect(width / 2, lotTop + 3, 2, lotDepth - 5, paving);
                Rect(3, lotTop + lotDepth / 2, width - 6, 2, paving);
                Rect(width / 2, lotTop + lotDepth / 2 - 4, 1, 6, plaster);
            }
            else if (type.Name == "Woodlot")
            {
                for (int n = 0; n < 3; n++) Rect(width - 9, bottom - 5 + n * 2, 5, 1, thatch);
            }
            else
            {
                Polygon(ink, new(5, bottom - 2), new(width / 2, bottom - 11), new(width - 4, bottom - 2));
                Polygon(slate, new(5, bottom - 2), new(width / 2, bottom - 11), new(width / 2 + 1, bottom - 2));
                Rect(width / 2 - 1, bottom - 5, 3, 2, Rgb.Hex(0xa46b43));
                Chimney(width - 7, bottom - 3, 8);
            }
        }
        else if (type.Name == "Town Well")
        {
            Yard(1, lift + 9, width - 3, 12, paving);
            Rect(4, lift + 11, 7, 6, stone.Scale(0.7));
            Rect(4, lift + 9, 7, 4, stone);
            Rect(5, lift + 10, 4, 2, glass);
            Rect(3, lift + 5, 1, 8, timber);
            Rect(11, lift + 5, 1, 9, timber);
            Polygon(tile, new(2, lift + 5), new(6, lift), new(13, lift + 5));
            Line(2, lift + 5, 6, lift, thatch);
            Rect(10, lift + 17, 3, 2, stone.Scale(0.8));
        }
        else if (type.Name == "Aqueduct")
        {
            Yard(1, lift + 7, width - 3, 15, paving);
            for (int x = 3; x < width - 3; x += 6)
            {
                Rect(x + 1, lift + 8, 3, 10, stone.Scale(0.65));
                Rect(x, lift + 8, 2, 10, stone);
                Pixel(x + 2, lift + 10, plaster);
            }
            Polygon(stone, new(1, lift + 1), new(width - 3, lift + 1), new(width - 1, lift + 5), new(3, lift + 5));
            Line(2, lift + 2, width - 4, lift + 2, glass);
            Line(3, lift + 3, width - 3, lift + 3, Rgb.Hex(0x7199a1));
            Rect(3, lift + 5, width - 4, 2, stone.Scale(0.7));
        }
        else if (type.Name == "Market Cross")
        {
            Yard(1, lift + 6, width - 3, 16, paving);
            Rect(width / 2 - 3, lift + 15, 6, 3, stone.Scale(0.75));
            Rect(width / 2 - 2, lift + 13, 4, 2, stone);
            Rect(width / 2 - 1, lift + 3, 2, 11, plaster);
            Rect(width / 2 - 3, lift + 5, 6, 2, plaster);
            Rect(2, lift + 8, 3, 2, timber);
            Rect(width - 5, lift + 10, 3, 2, tile);
        }
        else if (type.Service == ServiceKind.Defence || type.Name == "Gaol")
        {
            bool palisade = type.Name == "Motte and Bailey";
            int wallTop = lift + 10, wallBottom = bottom - 4;
            var material = palisade ? timber : stone;
            Yard(2, wallTop, width - 4, wallBottom - wallTop, paving.Scale(0.85));
            Rect(2, wallTop - 3, width - 4, 5, material.Scale(0.9));
            Rect(2, wallTop, 3, wallBottom - wallTop, material);
            Rect(width - 5, wallTop, 3, wallBottom - wallTop, material.Scale(0.65));
            if (width >= 30)
                House(8, wallTop + 6, width - 22, Math.Max(8, lotDepth / 3), 7, stone, slate);
            else
                FlatRoof(7, wallTop + 3, Math.Max(8, width - 14), Math.Max(7, lotDepth / 3), 8, material);
            Rect(2, wallBottom - 4, width - 4, 5, material);
            for (int x = 3; x < width - 3; x += 3)
            {
                Rect(x, wallTop - 5, 2, 2, material.Scale(1.15));
                Rect(x, wallBottom - 6, 2, 2, material.Scale(1.15));
            }
            Rect(width / 2 - 2, wallBottom - 2, 4, 4, ink);
            Line(width / 2 - 2, wallBottom - 3, width / 2 + 1, wallBottom - 3, plaster);
            if (!palisade)
            {
                Tower(2, wallTop, 7, 10);
                Tower(width - 9, wallTop, 7, 10);
                if (plotHeight >= 2)
                {
                    Tower(2, wallBottom - 3, 7, 9);
                    Tower(width - 9, wallBottom - 3, 7, 9);
                }
            }
        }
        else if (type.Service == ServiceKind.Faith || type.Name == "Monastery")
        {
            Yard(1, lotTop, width - 3, lotDepth, paving.Scale(0.95));
            int naveWidth = Math.Max(7, width - 14);
            int naveDepth = Math.Max(8, Math.Min(lotDepth - 3, 25));
            House(6, lotTop + 3, naveWidth, naveDepth, 6, stone, slate);
            if (type.Name == "Monastery")
            {
                House(2, bottom - 13, width - 9, 7, 5, plaster, tile);
                Rect(10, lotTop + 14, Math.Max(5, width - 20), 8, grass);
            }
            Tower(2, lift + 9, 8, 13, spire: true);
            if (type.Name == "Cathedral")
            {
                Tower(width - 10, lift + 11, 8, 12, spire: true);
                House(3, lotTop + 14, width - 9, 7, 5, stone, slate);
            }
            Rect(6, lift + 8, 1, 4, ink);
            Pixel(6, lift + 8, plaster);
        }
        else if (type.Name == "Fire Watch")
        {
            Yard(1, lotTop, width - 3, lotDepth, paving);
            House(3, lift + 12, bodyWidth, 7, 7, timber, tile, framed: true);
            Rect(7, lift + 18, 1, 6, timber);
            Rect(width - 7, lift + 19, 1, 5, timber);
            Line(7, lift + 19, width - 7, lift + 23, timber.Scale(0.6));
            Rect(4, bottom - 4, 2, 2, stone);
            Rect(width - 7, bottom - 4, 2, 2, stone);
        }
        else if (type.Zone == ZoneType.Industrial)
        {
            Yard(1, lotTop, width - 3, lotDepth, paving.Scale(0.85));
            House(2, bodyY, bodyWidth, bodyDepth, 5, stone, type.Name == "Mill" ? thatch : slate);
            if (type.Name == "Mill")
            {
                Rect(width - 6, bottom - 10, 4, 7, timber.Scale(0.7));
                for (int row = bottom - 9; row < bottom - 3; row += 2) Rect(width - 6, row, 4, 1, thatch);
                Rect(2, bottom - 4, width - 4, 2, Rgb.Hex(0x416f83));
            }
            else
            {
                Chimney(width - 7, bodyY + bodyDepth - 2, type.Level >= 3 ? 14 : 9);
                Rect(3, bottom - 4, 5, 2, timber);
                Rect(width / 2, bottom - 4, 3, 2, stone.Scale(0.7));
                if (type.Level >= 3)
                {
                    House(3, lotTop + 7, bodyWidth - 5, 7, 4, stone, slate);
                    Chimney(5, lotTop + 12, 8);
                }
            }
        }
        else
        {
            bool home = type.Zone == ZoneType.Residential;
            Yard(1, lotTop, width - 3, lotDepth, home ? grass : paving);
            int floors = home ? 4 + type.Level * 2 : 5;
            var roof = type.Name == "Cottage" ? thatch :
                type.Name is "Infirmary" or "Hospice" or "Chantry School" ? slate : tile;
            if (type.Level >= 3 && plotHeight >= 2)
            {
                House(2, lotTop + 8, bodyWidth, Math.Max(8, bodyDepth - 4), floors, plaster, roof, framed: home);
                House(2, bottom - 12, bodyWidth, 8, 5, plaster, roof, framed: home);
            }
            else House(2, bodyY, bodyWidth, bodyDepth, floors, plaster, roof, framed: home && type.Level > 1);
            if (home)
            {
                Rect(width / 2, bottom - 3, 2, 3, paving);
                if (width >= 24) Tree(width - 7, bottom - 10);
                Line(2, bottom, width - 3, bottom, timber.Scale(0.8));
            }
            if (type.Zone == ZoneType.Commercial || type.Name is "Tavern" or "Guildhall")
            {
                int awningY = bodyY + bodyDepth - floors + 3;
                Rect(3, awningY, bodyWidth - 2, 3, plaster);
                for (int x = 3; x < bodyWidth; x += 4) Rect(x, awningY, 2, 3, Rgb.Hex(0x59777d));
                Rect(3, bottom - 4, bodyWidth - 2, 2, timber);
                if (type.Name == "Market Stall")
                    Rect(3, bodyY + bodyDepth - 1, bodyWidth - 2, 3, timber);
            }
            if (type.Service == ServiceKind.Health)
            {
                Rect(5, bodyY + bodyDepth - floors + 1, 3, 3, plaster.Scale(1.1));
                Rect(6, bodyY + bodyDepth - floors + 1, 1, 3, Rgb.Hex(0x9c584f));
                Rect(5, bodyY + bodyDepth - floors + 2, 3, 1, Rgb.Hex(0x9c584f));
            }
            if (type.Service == ServiceKind.Police)
            {
                Rect(3, bottom - 4, bodyWidth, 2, stone);
                Rect(width - 7, bottom - 8, 3, 3, timber.Scale(0.65));
                Pixel(width - 6, bottom - 7, plaster);
            }
            if (type.Service == ServiceKind.Education)
            {
                Rect(4, bottom - 5, bodyWidth - 3, 1, stone);
                Rect(width - 7, bottom - 6, 3, 2, plaster);
            }
            if (type.Name == "Granary")
            {
                for (int x = 3; x < bodyWidth; x += 5) Rect(x, bottom - 4, 2, 3, stone.Scale(0.65));
                Rect(4, bottom - 6, 4, 2, thatch);
                Rect(width - 8, bottom - 5, 3, 2, thatch.Scale(0.8));
            }
        }
        return new(width, height, lift, pixels);
    }
}
