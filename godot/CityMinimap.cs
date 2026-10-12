using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public partial class CityMinimap : Control
{
    public GameSession Session { get; set; } = null!;
    public Func<(float Azimuth, float Elevation)?> CameraOrientation { get; set; } = () => null;
    public Func<Vector2[]?> CameraFootprint { get; set; } = () => null;
    private readonly MinimapImage _image = new();
    private ImageTexture? _texture;
    private int _version = -1;
    private TermCity.Core.Simulation.CityGame? _game;
    internal int TextureBuilds { get; private set; }
    private bool _dragging;
    private MinimapProjection Projection
    {
        get
        {
            var camera = CameraOrientation();
            int turns = camera is { } orientation
                ? (int)Math.Round(Mathf.PosMod(orientation.Azimuth - PerspectiveCity.InitialAzimuth, 360) / 90f) : 0;
            return new(Session.Game.Map.Width, Session.Game.Map.Height, turns);
        }
    }
    internal Rect2 Picture
    {
        get
        {
            var size = Vector2.One * Math.Min(Size.X, Size.Y);
            return new((Size - size) / 2, size);
        }
    }
    internal Vector2[] ViewOutline
    {
        get
        {
            var view = Session.ViewRect;
            var corners = CameraFootprint() ?? [new(view.X, view.Y), new(view.X + view.Width, view.Y),
                new(view.X + view.Width, view.Y + view.Height), new(view.X, view.Y + view.Height)];
            return corners.Select(MapPointToScreen).ToArray();
        }
    }
    public void SetHeight(float height)
    {
        if (!float.IsFinite(height) || height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        var size = new Vector2(80, height);
        if (CustomMinimumSize != size)
        {
            CustomMinimumSize = size;
            QueueRedraw();
        }
    }
    private int ImageWidth => Math.Min(240, Session.Game.Map.Width);
    private float PictureAspect => Session.Game.Map.Height * 1.5f / Session.Game.Map.Width;
    private int ImageHeight => Math.Min(Session.Game.Map.Height,
        Math.Max(1, (int)Math.Round(ImageWidth * PictureAspect)));

    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        TextureFilter = TextureFilterEnum.Linear;
        MouseFilter = MouseFilterEnum.Stop;
        Resized += UpdateSize;
        GuiInput += HandlePointer;
        UpdateSize();
    }

    private void UpdateSize()
    {
        QueueRedraw();
    }

    public override void _Draw()
    {
        var game = Session.Game;
        int width = ImageWidth;
        int height = ImageHeight;
        if (_version != game.MapVersion || !ReferenceEquals(game, _game))
        {
            UpdateSize();
            _image.Update(game, width, height);
            using var image = Image.CreateEmpty(width, height, false, Image.Format.Rgb8);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var color = _image[x, y];
                    image.SetPixel(x, y, new Color(color.R / 255f, color.G / 255f, color.B / 255f));
                }
            }
            _texture?.Dispose();
            _texture = ImageTexture.CreateFromImage(image);
            TextureBuilds++;
            _version = game.MapVersion;
            _game = game;
        }
        var picture = Picture;
        if (picture.Size.X <= 0) return;
        DrawPolygon([MapPointToScreen(new(0, 0)), MapPointToScreen(new(game.Map.Width, 0)),
            MapPointToScreen(new(game.Map.Width, game.Map.Height)), MapPointToScreen(new(0, game.Map.Height))],
            [Colors.White], [Vector2.Zero, Vector2.Right, Vector2.One, Vector2.Down], _texture);
        var outline = ViewOutline;
        for (int corner = 0; corner < outline.Length; corner++)
        {
            var start = (outline[corner] - picture.Position) / picture.Size;
            var end = (outline[(corner + 1) % outline.Length] - picture.Position) / picture.Size;
            if (MinimapProjection.ClipSegment(new(start.X, start.Y), new(end.X, end.Y)) is { } segment)
                DrawLine(picture.Position + new Vector2(segment.Start.X, segment.Start.Y) * picture.Size,
                    picture.Position + new Vector2(segment.End.X, segment.End.Y) * picture.Size, Colors.White, 2);
        }
    }

    private void HandlePointer(InputEvent input)
    {
        if (Session.Prompt is not null || Session.Preview is not null)
        {
            _dragging = false;
            return;
        }
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            _dragging = button.Pressed;
            if (_dragging) Jump(button.Position);
            AcceptEvent();
        }
        else if (_dragging && input is InputEventMouseMotion motion)
        {
            Jump(motion.Position);
            AcceptEvent();
        }
    }

    public void StopDrag() => _dragging = false;

    internal Vector2 ScreenPoint(Pos position) => MapPointToScreen(new(position.X + 0.5f, position.Y + 0.5f));

    internal Vector2 MapPointToScreen(Vector2 position)
    {
        var fraction = Projection.Fraction(new(position.X, position.Y));
        var picture = Picture;
        return picture.Position + new Vector2(fraction.X, fraction.Y) * picture.Size;
    }

    public override void _ExitTree() => _texture?.Dispose();

    private void Jump(Vector2 position)
    {
        var picture = Picture;
        if (picture.Size.X <= 0) return;
        var fraction = (position - picture.Position) / picture.Size;
        var point = Projection.MapPoint(new(fraction.X, fraction.Y));
        var map = Session.Game.Map;
        Session.CenterOn(new Pos(Math.Clamp((int)Math.Floor(point.X), 0, map.Width - 1),
            Math.Clamp((int)Math.Floor(point.Y), 0, map.Height - 1)));
    }
}
