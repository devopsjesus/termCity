using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public partial class CityMinimap : Control
{
    public GameSession Session { get; set; } = null!;
    private readonly MinimapImage _image = new();
    private ImageTexture? _texture;
    private int _version = -1;
    private TermCity.Core.Simulation.CityGame? _game;
    private Rect2 _picture;
    private bool _dragging;
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
        CustomMinimumSize = new Vector2(240, Size.X * PictureAspect);
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
            _version = game.MapVersion;
            _game = game;
        }
        var pictureSize = new Vector2(Size.X, Size.X * PictureAspect);
        _picture = new Rect2(new Vector2(0, (Size.Y - pictureSize.Y) / 2), pictureSize);
        DrawTextureRect(_texture!, _picture, false);
        var view = Session.ViewRect;
        var start = new Vector2(view.X / (float)game.Map.Width, view.Y / (float)game.Map.Height);
        var end = new Vector2(Math.Min(1, (view.X + view.Width) / (float)game.Map.Width),
            Math.Min(1, (view.Y + view.Height) / (float)game.Map.Height));
        DrawRect(new Rect2(_picture.Position + start * pictureSize, (end - start) * pictureSize),
            Colors.White, false, 2);
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

    public override void _ExitTree() => _texture?.Dispose();

    private void Jump(Vector2 position)
    {
        if (_picture.Size.X <= 0 || _picture.Size.Y <= 0) return;
        var fraction = (position - _picture.Position) / _picture.Size;
        var map = Session.Game.Map;
        Session.CenterOn(new Pos(Math.Clamp((int)(fraction.X * map.Width), 0, map.Width - 1),
            Math.Clamp((int)(fraction.Y * map.Height), 0, map.Height - 1)));
    }
}
