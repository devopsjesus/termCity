using Godot;
using TermCity.Core.Buildings;
using TermCity.Core.Roads;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Terrain;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

/// <summary>A lazy, noninteractive snapshot of the same model used by the city renderer.</summary>
public partial class RenderedModelPreview : TextureRect
{
    internal RenderedModelPreviewCache Cache { get; init; } = null!;
    internal ModelPreviewSubject Subject { get; init; } = null!;
    public string ModelName => Subject.Name;
    public string PreviewKind => Subject.Building is not null ? "Building" :
        Subject.Template is not null ? "RoadTemplate" : "Road";
    public bool MetadataReady { get; internal set; }
    public bool GraphicsAvailable => Cache.GraphicsAvailable;
    public bool TextureReady => Texture is not null;
    public bool PreviewReady => TextureReady;
    public string PreviewError { get; internal set; } = "";
    private bool _requested;

    public RenderedModelPreview()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ExpandMode = ExpandModeEnum.IgnoreSize;
        StretchMode = StretchModeEnum.KeepAspectCentered;
        TextureFilter = TextureFilterEnum.Linear;
    }

    public override void _Ready()
    {
        Cache.RegisterMetadata(this);
        if (!GraphicsAvailable) SetProcess(false);
    }

    public override void _Process(double delta)
    {
        if (_requested || !IsVisibleInTree()) return;
        Rect2 bounds = GetGlobalRect();
        if (!bounds.Intersects(GetViewportRect())) return;
        for (Node? parent = GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll && !bounds.Intersects(scroll.GetGlobalRect())) return;
        _requested = true;
        Cache.Request(this);
        SetProcess(false);
    }
}

internal sealed record ModelPreviewSubject(GameContent Content, BuildingType? Building = null,
    RoadType? Road = null, RoadTemplate? Template = null)
{
    public string Name => Building?.Name ?? Template?.Name ?? Road!.Name;
}

/// <summary>Serializes thumbnail renders and retains only their static textures, never live preview viewports.</summary>
public partial class RenderedModelPreviewCache : Node
{
    private readonly Dictionary<ModelPreviewSubject, ImageTexture> _textures = [];
    private readonly HashSet<ModelPreviewSubject> _metadata = [];
    private readonly Dictionary<ModelPreviewSubject, List<RenderedModelPreview>> _waiting = [];
    private readonly Queue<ModelPreviewSubject> _queue = [];
    private CityGame? _game;
    private GameSession? _session;
    private PerspectiveCity? _renderer;
    private ModelPreviewSubject? _active;
    private Rect2I _crop;
    private int _draws;
    public int CachedPreviewCount => _textures.Count;
    public int PendingPreviewCount => _queue.Count;
    public int ActivePreviewViewportCount => _renderer is null ? 0 : 1;
    public int CompletedRenderCount { get; private set; }
    public int FailedRenderCount { get; private set; }
    public int CompletedMetadataCount => _metadata.Count;
    public bool GraphicsAvailable => DisplayServer.GetName() != "headless";

    public override void _Ready()
    {
        if (GraphicsAvailable) RenderingServer.FramePostDraw += CaptureAfterDraw;
        else SetProcess(false);
    }

    internal void RegisterMetadata(RenderedModelPreview preview)
    {
        _metadata.Add(preview.Subject);
        preview.MetadataReady = true;
    }

    internal void Request(RenderedModelPreview preview)
    {
        RegisterMetadata(preview);
        // Dummy rendering has no post-draw signal or pixels; do not enqueue an unfinishable capture.
        if (!GraphicsAvailable) return;
        if (_textures.TryGetValue(preview.Subject, out var texture))
        {
            preview.Texture = texture;
            return;
        }
        if (!_waiting.TryGetValue(preview.Subject, out var controls))
        {
            controls = [];
            _waiting.Add(preview.Subject, controls);
            _queue.Enqueue(preview.Subject);
        }
        controls.Add(preview);
    }

    private static bool Alive(RenderedModelPreview preview) =>
        GodotObject.IsInstanceValid(preview) && preview.IsInsideTree() && !preview.IsQueuedForDeletion();

    public override void _Process(double delta)
    {
        if (_renderer is not null)
        {
            _renderer.RequestRender();
            return;
        }
        while (_queue.TryDequeue(out var subject))
        {
            if (!_waiting[subject].Any(Alive))
            {
                _waiting.Remove(subject);
                continue;
            }
            Render(subject);
            return;
        }
    }

    private void Render(ModelPreviewSubject subject)
    {
        // Generate one tiny private staging map per content registry, not a map per choice.
        if (_game is null || !ReferenceEquals(_game.Map.Content, subject.Content))
        {
            int side = Math.Max(16, subject.Content.Buildings
                .Select(building => Math.Max(building.Width, building.Height) + 8).DefaultIfEmpty(16).Max());
            _game = CityGame.New(new GameConfig
            {
                MapWidth = side, MapHeight = side, Seed = 1, StartingYear = 1200,
                Scenario = CityScenario.Random, Rules = CityRules.Classic,
            }, subject.Content);
            _game.Paused = true;
            _session = new GameSession(_game);
        }
        var map = _game.Map;
        var meadow = subject.Content.Terrains.Get(DefaultTerrains.GrassName);
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
            {
                map.ClearCell(x, y);
                map.SetTerrain(x, y, meadow);
            }
        int width = subject.Building?.Width ?? subject.Template?.Width ?? 6;
        int height = subject.Building?.Height ?? subject.Template?.Height ?? 1;
        var area = new CellRect((map.Width - width) / 2, (map.Height - height) / 2, width, height);
        if (subject.Building is { } building)
        {
            if (building.Zone != ZoneType.None)
                foreach (var p in area.Cells()) map.SetZone(p.X, p.Y, building.Zone);
            if (building.Zone == ZoneType.Residential)
                foreach (var p in new CellRect(area.X - 1, area.Y - 1, area.Width + 2, area.Height + 2).Cells())
                    map.SetZone(p.X, p.Y, ZoneType.Residential);
            map.SetBuildingFootprint(building, area);
        }
        else
        {
            var road = subject.Road ?? subject.Content.Roads.Get(subject.Template!.RoadName);
            var cells = subject.Template?.Cells(new Pos(area.X, area.Y)) ?? area.Cells().ToArray();
            foreach (var p in cells) map.SetRoad(p.X, p.Y, road, playerBuilt: true);
        }
        _game.Touch();
        _session!.SetViewport(8, 8);
        _session.CenterOn(new Pos(map.Width / 2, map.Height / 2));
        int extent = Math.Max(width, height);
        _session.SetZoom(subject.Building is not null && extent <= 2 ? 1 : extent <= 5 ? 0 : -1);
        _renderer = new PerspectiveCity { Name = "CanonicalModelPreviewRenderer", WaterMotionEnabled = false };
        AddChild(_renderer);
        var renderSize = new Vector2(768, 576);
        _renderer.Sync(_session, renderSize);
        var center = new Vector2(area.X + width / 2f, area.Y + height / 2f);
        _renderer.SetCameraOffset(_session, center - new Vector2(
            _session.CameraX + _session.ViewWidth * _session.Stride / 2f,
            _session.CameraY + _session.ViewHeight * _session.Stride / 2f));
        _renderer.Sync(_session, renderSize);
        float shoulder = subject.Building is not null ? 0.25f :
            (float)(subject.Road ?? subject.Content.Roads.Get(subject.Template!.RoadName)).WidthCells / 2 + 0.25f;
        var bounds = new Aabb(new Vector3(area.X - shoulder, 0, area.Y - shoulder),
            new Vector3(width + shoulder * 2, 0.25f, height + shoulder * 2));
        IReadOnlyList<Aabb> volumes = subject.Building is not null
            ? _renderer.BuildingVolumes(new Pos(area.X, area.Y))
                .Concat(_renderer.YardVolumes(new Pos(area.X, area.Y))).ToArray() : [];
        if (subject.Building is not null)
        {
            foreach (var volume in volumes)
                bounds = bounds.Merge(volume);
            // Roof triangles are not in BuildingVolumes; include their peaks and overhangs.
            bounds = bounds.Grow(0.35f);
            bounds.Size += Vector3.Up * 1.2f;
        }
        var points = Enumerable.Range(0, 8).Select(index => _renderer.ScreenPoint(bounds.GetEndpoint(index))).ToArray();
        int left = Math.Max(0, (int)MathF.Floor(points.Min(p => p.X)) - 6);
        int top = Math.Max(0, (int)MathF.Floor(points.Min(p => p.Y)) - 6);
        int right = Math.Min((int)renderSize.X, (int)MathF.Ceiling(points.Max(p => p.X)) + 6);
        int bottom = Math.Min((int)renderSize.Y, (int)MathF.Ceiling(points.Max(p => p.Y)) + 6);
        _crop = new Rect2I(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
        if (subject.Building is not null && volumes.Count > 0)
            _crop = FitBuildingCrop(_crop, volumes, new Pos(area.X, area.Y));
        _active = subject;
        _draws = 0;
    }

    private Rect2I FitBuildingCrop(Rect2I conservative, IReadOnlyList<Aabb> volumes, Pos anchor)
    {
        var renderer = _renderer!;
        var points = volumes.SelectMany(volume => Enumerable.Range(0, 8)
            .Select(index => renderer.ScreenPoint(volume.GetEndpoint(index)))).ToArray();
        var projected = new Rect2(points[0], Vector2.Zero);
        foreach (var point in points) projected = projected.Expand(point);
        var fitted = projected;
        const int step = 4, padding = 8;
        // Individual volumes avoid the empty upper corners of a merged 3D box. Pick outside
        // their projection to retain canonical roof triangles, including peaks and overhangs.
        for (int y = conservative.Position.Y; y < conservative.End.Y; y += step)
            for (int x = conservative.Position.X; x < conservative.End.X; x += step)
            {
                var pixel = new Vector2(x, y);
                if (!projected.HasPoint(pixel) && renderer.PickBuilding(pixel, out var owner) && owner == anchor)
                    fitted = fitted.Expand(pixel);
            }
        int left = Math.Max(conservative.Position.X, (int)MathF.Floor(fitted.Position.X) - padding);
        int top = Math.Max(conservative.Position.Y, (int)MathF.Floor(fitted.Position.Y) - padding);
        int right = Math.Min(conservative.End.X, (int)MathF.Ceiling(fitted.End.X) + padding);
        int bottom = Math.Min(conservative.End.Y, (int)MathF.Ceiling(fitted.End.Y) + padding);
        return new Rect2I(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private void CaptureAfterDraw()
    {
        if (_renderer is null || _active is null || ++_draws < 4) return;
        using var image = _renderer.Texture.GetImage();
        if (image is null || image.IsEmpty())
        {
            FailIfTimedOut();
            return;
        }
        using var cropped = image.GetRegion(_crop);
        if (!HasSubjectPixels(cropped))
        {
            FailIfTimedOut();
            return;
        }
        var texture = ImageTexture.CreateFromImage(cropped);
        _textures.Add(_active, texture);
        foreach (var preview in _waiting[_active].Where(Alive)) preview.Texture = texture;
        _waiting.Remove(_active);
        CompletedRenderCount++;
        ReleaseRenderer();
    }

    private static bool HasSubjectPixels(Image image)
    {
        int subjectPixels = 0;
        for (int y = 0; y < image.GetHeight(); y += 2)
            for (int x = 0; x < image.GetWidth(); x += 2)
            {
                var pixel = image.GetPixel(x, y);
                // Meadow and the background are green; allow canonical blue-gray cobbled stone.
                if (pixel.A > 0.1f && pixel.R > 0.12f &&
                    pixel.R >= pixel.G * 0.90f && pixel.R > pixel.B * 0.90f && ++subjectPixels > 8)
                    return true;
            }
        return false;
    }

    private void FailIfTimedOut()
    {
        if (_draws < 60 || _active is null) return;
        string error = $"Canonical preview for {_active.Name} did not produce model pixels after {_draws} rendered frames.";
        foreach (var preview in _waiting[_active].Where(Alive)) preview.PreviewError = error;
        _waiting.Remove(_active);
        FailedRenderCount++;
        GD.PushError(error);
        ReleaseRenderer();
    }

    private void ReleaseRenderer()
    {
        if (_renderer is null) return;
        _renderer.SetActive(false);
        RemoveChild(_renderer);
        _renderer.QueueFree();
        _renderer = null;
        _active = null;
    }

    public override void _ExitTree()
    {
        if (GraphicsAvailable) RenderingServer.FramePostDraw -= CaptureAfterDraw;
        foreach (var texture in _textures.Values) texture.Dispose();
        _textures.Clear();
        _metadata.Clear();
        _waiting.Clear();
        _queue.Clear();
        _game = null;
        _session = null;
    }
}
