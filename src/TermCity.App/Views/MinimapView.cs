using System.Drawing;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Input;
using TermCity.Core.Session;
using TermCity.Core.Util;
using TermCity.Core.World;

using Pos = TermCity.Core.Util.Pos;

namespace TermCity.App.Views;

/// <summary>
/// Whole-map overview drawn with half-block characters (two map "pixels" per terminal cell), in the proportions of the map's grid of cells, stretched a little taller.
/// The current camera window is outlined; click or drag to jump the camera.
/// </summary>
internal sealed class MinimapView : PanelView
{
    private static readonly Rgb CameraOutline = Rgb.Hex(0xffffff);

    private readonly GameSession _session;
    private readonly MinimapImage _image = new();
    private readonly bool _seamlessCells;
    private bool _dragging;

    public MinimapView(GameSession session, bool? seamlessCells = null)
    {
        _session = session;
        _seamlessCells = seamlessCells ?? OperatingSystem.IsMacOS();
        CanFocus = false;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        int width = Viewport.Width;
        int height = Viewport.Height;
        if (width <= 0 || height <= 1)
        {
            return true;
        }

        var map = _session.Game.Map;
        int pixelRows = PixelRows(height);

        // The map keeps roughly the shape of its grid: it is drawn as large as fits and centred, with the panel showing around it.
        var (left, mapWidth, mapHeight) = FitInPanel(map.Width, map.Height, width, pixelRows, _seamlessCells);
        _image.Update(_session.Game, mapWidth, mapHeight);
        var box = CameraBox(map.Width, map.Height, _session.ViewRect, mapWidth, mapHeight);

        DrawText(0, 0, " MINIMAP", Colors.Heading, Colors.PanelBackground);
        Rgb Pixel(int cx, int py)
        {
            int px = cx - left;
            if (px < 0 || px >= mapWidth || py >= mapHeight)
            {
                return Colors.PanelBackground;
            }

            var color = _image[px, py];
            return box.OnBorder(px, py) ? Rgb.Blend(color, CameraOutline, 0.6) : color;
        }

        for (int cy = 1; cy < height; cy++)
        {
            for (int cx = 0; cx < width; cx++)
            {
                if (_seamlessCells)
                {
                    var color = Pixel(cx, cy - 1);
                    SetAttribute(Colors.Attr(color, color));
                    AddStr(cx, cy, " ");
                }
                else
                {
                    SetAttribute(Colors.Attr(Pixel(cx, (cy - 1) * 2), Pixel(cx, (cy - 1) * 2 + 1)));
                    AddStr(cx, cy, "▀");
                }
            }
        }

        return true;
    }

    /// <summary>Columns left empty on each side of the picture, so it does not run edge to edge of the panel.</summary>
    internal const int SideMargin = 3;

    private int PixelRows(int viewHeight) => Math.Max(1, (viewHeight - 1) * (_seamlessCells ? 1 : 2));

    /// <summary>The picture of the map within a panel of the given size, inside the margin; the offset is from the panel's left edge.</summary>
    internal static (int Left, int Width, int Height) FitInPanel(int mapWidth, int mapHeight, int panelWidth, int availableHeight)
        => FitInPanel(mapWidth, mapHeight, panelWidth, availableHeight, seamlessCells: false);

    /// <summary>The picture size, accounting for full-height terminal cells when half-blocks are not used.</summary>
    internal static (int Left, int Width, int Height) FitInPanel(
        int mapWidth, int mapHeight, int panelWidth, int availableHeight, bool seamlessCells)
    {
        double stretch = seamlessCells ? VerticalStretch / 2 : VerticalStretch;
        var (left, width, height) = FitMap(
            mapWidth, mapHeight, Math.Max(1, panelWidth - 2 * SideMargin), availableHeight, stretch);
        return (left + SideMargin, width, height);
    }

    /// <summary>
    /// How much taller the picture is than the map's grid of cells would make it. A terminal character is about twice
    /// as tall as it is wide, so a picture with the grid's exact shape looks squashed flat and its camera box looks
    /// far wider than the screen it stands for; true screen proportions (a stretch of 2) would make a tall portrait.
    /// This is in between.
    /// </summary>
    internal const double VerticalStretch = 1.5;

    /// <summary>
    /// The largest picture of the map that fits in the space, as (offset from the left, width, height) in minimap
    /// pixels, keeping the map's shape apart from <see cref="VerticalStretch"/>.
    /// </summary>
    internal static (int Left, int Width, int Height) FitMap(int mapWidth, int mapHeight, int availableWidth, int availableHeight)
        => FitMap(mapWidth, mapHeight, availableWidth, availableHeight, VerticalStretch);

    private static (int Left, int Width, int Height) FitMap(
        int mapWidth, int mapHeight, int availableWidth, int availableHeight, double verticalStretch)
    {
        double tall = mapHeight * verticalStretch;
        double scale = Math.Min(availableWidth / (double)mapWidth, availableHeight / tall);
        int w = Math.Clamp((int)Math.Round(mapWidth * scale, MidpointRounding.AwayFromZero), 1, availableWidth);
        int h = Math.Clamp((int)Math.Round(tall * scale, MidpointRounding.AwayFromZero), 1, availableHeight);
        return ((availableWidth - w) / 2, w, h);
    }
    /// <summary>A rectangle of minimap pixels.</summary>
    internal readonly record struct PixelBox(int Left, int Top, int Width, int Height)
    {
        public bool OnBorder(int px, int py) =>
            px >= Left && px < Left + Width && py >= Top && py < Top + Height &&
            (px == Left || px == Left + Width - 1 || py == Top || py == Top + Height - 1);
    }

    /// <summary>
    /// The outline of the part of the map that is on screen. Its size depends only on how much of the map is visible,
    /// so it stays the same size while scrolling; only its position changes.
    /// </summary>
    internal static PixelBox CameraBox(int mapWidth, int mapHeight, CellRect view, int pixelsWide, int pixelsHigh)
    {
        double visibleX = Math.Min(view.Width, mapWidth), visibleY = Math.Min(view.Height, mapHeight);
        int w = Math.Clamp((int)Math.Round(visibleX * pixelsWide / mapWidth, MidpointRounding.AwayFromZero), 1, pixelsWide);
        int h = Math.Clamp((int)Math.Round(visibleY * pixelsHigh / mapHeight, MidpointRounding.AwayFromZero), 1, pixelsHigh);
        int left = Math.Clamp((int)Math.Round(view.X * (double)pixelsWide / mapWidth, MidpointRounding.AwayFromZero), 0, pixelsWide - w);
        int top = Math.Clamp((int)Math.Round(view.Y * (double)pixelsHigh / mapHeight, MidpointRounding.AwayFromZero), 0, pixelsHigh - h);
        return new PixelBox(left, top, w, h);
    }
    protected override bool OnMouseEvent(Mouse mouse)
    {
        var flags = mouse.Flags;
        var position = mouse.Position ?? Point.Empty;

        if (flags.HasFlag(MouseFlags.LeftButtonPressed))
        {
            if (!_dragging)
            {
                _dragging = true;
                App?.Mouse.GrabMouse(this);
            }

            Jump(position);
            return true;
        }

        if (_dragging && flags.HasFlag(MouseFlags.PositionReport))
        {
            Jump(position);
            return true;
        }

        if (_dragging && (flags.HasFlag(MouseFlags.LeftButtonReleased) || flags.HasFlag(MouseFlags.LeftButtonClicked)))
        {
            _dragging = false;
            App?.Mouse.UngrabMouse();
            return true;
        }

        return base.OnMouseEvent(mouse);
    }

    private void Jump(Point position)
    {
        var map = _session.Game.Map;
        int width = Math.Max(1, Viewport.Width);
        int pixelRows = PixelRows(Viewport.Height);
        var (left, mapWidth, mapHeight) = FitInPanel(map.Width, map.Height, width, pixelRows, _seamlessCells);

        // Clicks beside or below the picture go to its nearest edge.
        int px = Math.Clamp(position.X - left, 0, mapWidth - 1);
        int py = Math.Clamp((position.Y - 1) * (_seamlessCells ? 1 : 2) + (_seamlessCells ? 0 : 1), 0, mapHeight - 1);
        int x = (int)((long)px * map.Width / mapWidth);
        int y = (int)((long)py * map.Height / mapHeight);
        _session.CenterOn(new Pos(x, y));
    }
}
