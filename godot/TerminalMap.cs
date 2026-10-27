using Godot;
using TermCity.Core.Effects;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Util;
using TermCity.Core.World;
using TermCity.Core.Features;
using TermCity.Core.Terrain;

namespace TermCity.GodotApp;

public partial class TerminalMap : Control
{
    public const int FontSize = 18;
    public TerminalGrid Grid { get; } = new();
    public GameSession Session { get; set; } = null!;
    public Font CellFont { get; set; } = null!;
    private bool _cellsDirty = true;
    private int _siegeAnimationRevision = -1;
    private readonly List<GlyphDraw> _overlays = [];
    private Vector2 _shakeOffset;
    private readonly RoadVectorLayer _roads = new();
    private readonly Dictionary<(int X, int Y, int Scale, int Stride), ImageTexture?> _roadChunks = [];
    private readonly List<GlyphDraw> _mains = [];
    private ImageTexture? _areaFade;
    private readonly Dictionary<(byte Type, int Width, int Height, int Variant), (ImageTexture Texture, BuildingSprite Sprite)> _buildings = [];
    private readonly Dictionary<(int X, int Y, int Stride, int Phase), ImageTexture> _terrain = [];
    private readonly HashSet<(int X, int Y, int Stride)> _waterChunks = [];
    private GameMap? _terrainMap;
    private int _terrainVersion = -1;
    private readonly Dictionary<(byte Type, int Variant), (ImageTexture Texture, TerrainSprite Sprite)> _features = [];
    public int AreasDrawn { get; private set; }
    private readonly List<Rect2> _perspectiveRangeBounds = [];
    public IReadOnlyList<Rect2> PerspectiveRangeBounds => _perspectiveRangeBounds;

    /// <summary>Optional visual effects. Null or inactive means the map draws exactly as it always did.</summary>
    public EffectSystem? Effects { get; set; }

    /// <summary>Frames drawn so far (diagnostics and the smoke test).</summary>
    public int DrawCount { get; private set; }

    /// <summary>How many glyphs the last frame drew because of effects (modified cells, ghosts and sprites).</summary>
    public int EffectGlyphsDrawn { get; private set; }

    /// <summary>How many blocks of curved road the last frame laid over the map (diagnostics and the smoke test).</summary>
    public int RoadChunksDrawn { get; private set; }
    public int BuildingsDrawn { get; private set; }
    public bool PerspectiveEnabled { get; private set; }
    public PerspectiveCity? Perspective { get; private set; }

    public void SetPerspective(bool enabled)
    {
        if (enabled && Perspective is null)
        {
            Perspective = new PerspectiveCity();
            AddChild(Perspective);
        }
        PerspectiveEnabled = enabled;
        Session.SetCameraEdgePadding(enabled);
        Perspective?.SetActive(enabled);
        if (Perspective is not null) Perspective.ProcessMode = enabled ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
        Invalidate(true);
    }

    public bool TryMapPosition(Vector2 pointer, out Pos position)
    {
        RefreshCells();
        position = default;
        if (!Grid.TryCell(pointer.X, pointer.Y, out var screen)) return false;
        if (PerspectiveEnabled && Perspective is { } perspective)
        {
            perspective.Sync(Session, Size);
            position = perspective.GroundCell(pointer);
        }
        else position = Session.ScreenToMap(screen.X, screen.Y);
        return Session.Game.Map.InBounds(position);
    }

    public void TiltMap()
    {
        if (!PerspectiveEnabled) SetPerspective(true);
        Perspective!.Tilt();
        QueueRedraw();
    }

    public void RotateMap() => RotateMap(90);

    public void RotateMap(float degrees)
    {
        if (!PerspectiveEnabled) SetPerspective(true);
        Perspective!.Rotate(degrees);
        QueueRedraw();
    }

    public Vector3 BeginPerspectivePan(Vector2 pointer)
    {
        RefreshCells();
        if (Perspective is not { } perspective)
            throw new InvalidOperationException("Perspective panning requires an active perspective renderer.");
        perspective.Sync(Session, Size);
        return perspective.GroundPlanePoint(pointer);
    }

    public void PanPerspective(Vector3 anchor, Vector2 pointer)
    {
        if (!PerspectiveEnabled || Perspective is not { } perspective) return;
        RefreshCells();
        perspective.Sync(Session, Size);
        var current = perspective.GroundPlanePoint(pointer);
        var target = new Vector2(Session.CameraX, Session.CameraY) + perspective.CameraOffset +
            new Vector2(anchor.X - current.X, anchor.Z - current.Z);
        int paddingX = Session.CameraEdgePadding ? Session.VisibleCellsX / 2 : 0;
        int paddingY = Session.CameraEdgePadding ? Session.VisibleCellsY / 2 : 0;
        target.X = Math.Clamp(target.X, -paddingX,
            Math.Max(-paddingX, Session.Game.Map.Width - Session.VisibleCellsX + paddingX));
        target.Y = Math.Clamp(target.Y, -paddingY,
            Math.Max(-paddingY, Session.Game.Map.Height - Session.VisibleCellsY + paddingY));
        Session.ScrollCamera((int)Math.Round(target.X) - Session.CameraX,
            (int)Math.Round(target.Y) - Session.CameraY);
        perspective.SetCameraOffset(Session, target - new Vector2(Session.CameraX, Session.CameraY));
        perspective.Sync(Session, Size);
        QueueRedraw();
    }

    public void ScrollPerspective(Vector2 pixels)
    {
        var center = Size / 2;
        PanPerspective(BeginPerspectivePan(center), center - pixels);
    }

    public override void _ExitTree()
    {
        foreach (var cached in _buildings.Values) cached.Texture.Dispose();
        _buildings.Clear();
        foreach (var texture in _roadChunks.Values) texture?.Dispose();
        _roadChunks.Clear();
        _areaFade?.Dispose();
        foreach (var texture in _terrain.Values) texture.Dispose();
        _terrain.Clear();
        foreach (var cached in _features.Values) cached.Texture.Dispose();
        _features.Clear();
    }

    public override void _Ready()
    {
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Stop;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        Resized += ResizeGrid;
        ResizeGrid();
        SetPerspective(PerspectiveEnabled);
    }

    public void Invalidate(bool cellsChanged)
    {
        _cellsDirty |= cellsChanged;
        QueueRedraw();
    }

    public void AdvanceAnimation(double delta, bool focused)
    {
        int siegeRevision = Session.Game.Settlement?.Revision ?? -1;
        bool siegeChanged = siegeRevision != _siegeAnimationRevision;
        _siegeAnimationRevision = siegeRevision;
        bool siegeMoving = focused && !Session.ClockSuspended &&
            Session.Game.Settlement is { } siege && (siege.Goblins.Count > 0 || siege.Projectiles.Count > 0 ||
                siege.RepairingWall is not null || siege.UpgradingWall is not null || siege.Walls.Any(WallDamageLayout.IsBurning));
        if (Grid.AdvanceAnimation(delta, focused) || siegeChanged || siegeMoving)
        {
            QueueRedraw();
        }
    }

    public void RefreshCells()
    {
        Session.SetCameraEdgePadding(PerspectiveEnabled);
        if (Perspective is { } perspective)
        {
            perspective.WaterMotionEnabled = Effects?.Settings.ReducedMotion != true && Effects?.Settings.Level != EffectLevel.Off;
            perspective.SiegeAnimationEnabled = Effects?.Settings.ReducedMotion != true;
        }
        if (!_cellsDirty)
        {
            return;
        }

        Grid.Fill(Session);
        _cellsDirty = false;
    }

    public void ZoomBy(int delta, Vector2? pointer = null)
    {
        if (delta == 0) return;
        RefreshCells();
        var anchor = pointer ?? Size / 2;
        if (PerspectiveEnabled && Perspective is { } perspective)
        {
            perspective.Sync(Session, Size);
            var beforeWorld = perspective.GroundCell(anchor);
            Session.ZoomBy(delta);
            RefreshCells();
            perspective.Sync(Session, Size);
            var afterWorld = perspective.GroundCell(anchor);
            Session.ScrollCamera(beforeWorld.X - afterWorld.X, beforeWorld.Y - afterWorld.Y);
            return;
        }
        if (!Grid.TryCell(anchor.X, anchor.Y, out var before)) return;
        var world = Session.ScreenToMap(before.X, before.Y);
        Session.ZoomBy(delta, before.X, before.Y);
        RefreshCells();
        if (Grid.TryCell(anchor.X, anchor.Y, out var after))
            Session.PanCamera(world, after.X, after.Y);
    }

    private void ResizeGrid()
    {
        Grid.Resize(Size.X, Size.Y, Session.ZoomLevel > 0 ? 2 : 1);
        Invalidate(true);
    }

    public override void _Draw()
    {
        RefreshCells();
        DrawCount++;
        DrawRect(new Rect2(Vector2.Zero, Size), Colors.Black);
        if (PerspectiveEnabled && Perspective is { } perspective)
        {
            perspective.Sync(Session, Size);
            TextureFilter = TextureFilterEnum.Nearest;
            DrawTextureRect(perspective.Texture, new Rect2(Vector2.Zero, Size), false);
            DrawPerspectiveAreas(perspective);
            DrawSiegeOverlay();
            BuildingsDrawn = perspective.BuildingsDrawn;
            return;
        }

        int scale = Grid.PixelWidth / TerminalGrid.CellWidth;
        int fontSize = FontSize * scale;
        int cellWidth = Grid.PixelWidth, cellHeight = Grid.PixelHeight;
        float baseline = (cellHeight - CellFont.GetHeight(fontSize)) / 2 + CellFont.GetAscent(fontSize);
        // Effects only exist at normal and zoomed-in views, where one screen cell is one map cell.
        var fx = Effects is { Settings.Active: true, HasVisuals: true } && Session.Stride == 1 ? Effects : null;
        var shake = fx is null ? (0f, 0f) : EffectDraw.ShakePixels(fx.Shake, cellWidth, cellHeight);
        _shakeOffset = new Vector2(shake.Item1, shake.Item2);
        int drawn = 0;
        _overlays.Clear();
        DrawSetTransform(_shakeOffset, 0f, Vector2.One);
        DrawTerrain(cellWidth, cellHeight);
        for (int y = 0; y < Grid.Rows; y++)
        {
            for (int x = 0; x < Grid.Columns; x++)
            {
                if (Grid.VisualAt(Session, x, y) is not { } visual)
                {
                    continue;
                }
                var background = visual.Background;
                if (fx is not null)
                {
                    var effectAt = Session.ScreenToMap(x, y);
                    if (fx.TryGetCell(effectAt.X, effectAt.Y, out var cellEffect) && cellEffect.BackgroundAmount > 0)
                    {
                        background = EffectDraw.Background(background, cellEffect);
                    }
                }
                var position = new Vector2(x * cellWidth, y * cellHeight);
                var at = Session.ScreenToMap(x, y);
                if (Session.Game.Map.InBounds(at) &&
                    background != Session.Game.Map.TerrainAt(at.X, at.Y).Background)
                    DrawRect(new Rect2(position, new Vector2(cellWidth, cellHeight)),
                        ToColor(background) with { A = 0.6f });
            }
        }
        _mains.Clear();
        for (int y = 0; y < Grid.Rows; y++)
        {
            for (int x = 0; x < Grid.Columns; x++)
            {
                if (Grid.VisualAt(Session, x, y) is not { } visual)
                {
                    continue;
                }
                if (fx is not null)
                {
                    var at = Session.ScreenToMap(x, y);
                    if (fx.TryGetCell(at.X, at.Y, out var cellEffect) && !cellEffect.IsIdentity)
                    {
                        EffectDraw.PlanCell(cellEffect, visual, x, y, cellWidth, cellHeight, shake, out var main, out var overlay);
                        if (main is { } mainDraw)
                        {
                            _mains.Add(mainDraw);
                        }
                        if (overlay is { } ghost)
                        {
                            _overlays.Add(ghost);
                        }
                        continue;
                    }
                }
                var offset = Grid.OffsetAt(Session, x, y);
                var world = Session.ScreenToMap(x, y);
                if (Session.Game.Map.InBounds(world))
                {
                    var map = Session.Game.Map;
                    bool natural = map.BuildingAt(world.X, world.Y) is null &&
                        (!Session.AreaOverlaysVisible || map.ZoneAt(world.X, world.Y) == ZoneType.None);
                    if (natural && (map.FeatureAt(world.X, world.Y) is { Name: DefaultFeatures.TreeName or DefaultFeatures.RockName } ||
                        map.FeatureAt(world.X, world.Y) is null &&
                        map.TerrainAt(world.X, world.Y).Name is DefaultTerrains.GrassName or DefaultTerrains.HillName or DefaultTerrains.WaterName))
                        continue;
                }
                var glyphPosition = new Vector2(x * cellWidth + offset.X * scale,
                    y * cellHeight + offset.Y * scale);
                DrawCellGlyph(visual.Glyph, glyphPosition, cellWidth, cellHeight, fontSize, baseline, ToColor(visual.Foreground));
            }
        }
        // The roads cover whatever terrain they run over; effects on cells go over the roads.
        DrawNaturalFeatures(cellWidth, cellHeight);
        DrawRoadCurves(scale, cellWidth, cellHeight);
        DrawPaths(cellWidth, cellHeight);
        DrawBuildingFootprints(cellWidth, cellHeight);
        DrawSiegeOverlay();
        DrawAreas(cellWidth, cellHeight, fontSize);
        foreach (var mainDraw in _mains)
        {
            DrawEffectGlyph(mainDraw, cellWidth, cellHeight, baseline, fontSize);
            drawn++;
        }

        if (fx is not null)
        {
            // Ghosts and sprites go on top of the whole map so they can float across neighbouring cells.
            foreach (var ghost in _overlays)
            {
                DrawEffectGlyph(ghost, cellWidth, cellHeight, baseline, fontSize);
                drawn++;
            }
            var origin = Session.ScreenToMap(0, 0);
            foreach (var sprite in fx.Sprites)
            {
                if (EffectDraw.PlanSprite(sprite, origin.X, origin.Y, cellWidth, cellHeight, shake,
                        Grid.Columns, Grid.Rows) is { } draw)
                {
                    DrawEffectGlyph(draw, cellWidth, cellHeight, baseline, fontSize);
                    drawn++;
                }
            }
        }
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
        EffectGlyphsDrawn = drawn;
    }

    private void DrawPerspectiveAreas(PerspectiveCity perspective)
    {
        AreasDrawn = 0;
        _perspectiveRangeBounds.Clear();
        if (!Session.AreaOverlaysVisible) return;
        IEnumerable<EffectArea> areas = Session.HasCursor && !Session.FixedPlacementToolActive
            ? AreaOfEffect.InView(Session.Game, perspective.RenderedView, Session.ActiveArea) : [];
        if (Session.Preview is { Building: { } type } preview && Session.BuildingPlacementError is null)
            areas = areas.Concat(Session.Game.PlanBuildings(type, preview.Area)
                .SelectMany(area => AreaOfEffect.ForBuilding(type, new(area.X, area.Y))));
        AreasDrawn = 0;
        _perspectiveRangeBounds.Clear();
        float pixelsPerUnit = Size.Y / perspective.CameraSpan;
        foreach (var area in areas)
        {
            var center = perspective.ScreenPoint(new(area.Center.X + 0.5f,
                TerrainSprite.Elevation(Session.Game.Map, area.Center.X, area.Center.Y), area.Center.Y + 0.5f));
            var points = new Vector2[65];
            float radius = area.Radius * pixelsPerUnit;
            _perspectiveRangeBounds.Add(new Rect2(center - Vector2.One * radius, Vector2.One * radius * 2));
            for (int i = 0; i < points.Length; i++)
            {
                var offset = AreaOfEffect.GroundOffset(area.Radius, i * Mathf.Tau / 64);
                points[i] = center + new Vector2(offset.X, offset.Y) * pixelsPerUnit;
            }
            DrawPolyline(points, new Color(1, 1, 1, AreaOfEffect.RingAlpha), 1.5f, antialiased: true);
            string glyphs = string.Join(" ", area.Glyphs);
            float labelWidth = CellFont.GetStringSize(glyphs, fontSize: FontSize).X;
            DrawString(CellFont, center + new Vector2(-labelWidth / 2, radius + FontSize),
                glyphs, fontSize: FontSize, modulate: Colors.White);
            AreasDrawn++;
        }
    }

    private void DrawTerrain(int cellWidth, int cellHeight)
    {
        var map = Session.Game.Map;
        if (!ReferenceEquals(_terrainMap, map) || _terrainVersion != map.TerrainVersion)
        {
            foreach (var texture in _terrain.Values) texture.Dispose();
            _terrain.Clear();
            _waterChunks.Clear();
            _terrainMap = map;
            _terrainVersion = map.TerrainVersion;
        }
        var origin = Session.ScreenToMap(0, 0);
        int phase = (int)(Grid.AnimationSeconds / TerminalGrid.BeatSeconds);
        int stride = Session.Stride, span = TerrainSprite.ChunkCells * stride;
        int lastX = Math.Min(map.Width - 1, origin.X + Grid.Columns * stride - 1);
        int lastY = Math.Min(map.Height - 1, origin.Y + Grid.Rows * stride - 1);
        TextureFilter = TextureFilterEnum.Nearest;
        for (int cy = Math.Max(0, origin.Y) / span; cy <= lastY / span; cy++)
            for (int cx = Math.Max(0, origin.X) / span; cx <= lastX / span; cx++)
            {
                var key = (cx, cy, stride, _waterChunks.Contains((cx, cy, stride)) ? phase : 0);
                if (!_terrain.TryGetValue(key, out var texture))
                {
                    var sprite = TerrainSprite.Chunk(map, cx, cy, stride, key.Item4);
                    using var image = Image.CreateFromData(sprite.Width, sprite.Height, false, Image.Format.Rgba8, sprite.Pixels);
                    texture = ImageTexture.CreateFromImage(image);
                    _terrain.Add(key, texture);
                    if (sprite.AnimatedWater) _waterChunks.Add((cx, cy, stride));
                }
                DrawTextureRect(texture, new Rect2((cx * span - origin.X) * cellWidth / (float)stride,
                    (cy * span - origin.Y) * cellHeight / (float)stride,
                    TerrainSprite.ChunkCells * cellWidth, TerrainSprite.ChunkCells * cellHeight), false);
            }
    }

    private void DrawNaturalFeatures(int cellWidth, int cellHeight)
    {
        var map = Session.Game.Map;
        var origin = Session.ScreenToMap(0, 0);
        float pixelScale = cellWidth / (12f * Session.Stride);
        float sx = cellWidth / (float)Session.Stride, sy = cellHeight / (float)Session.Stride;
        for (int y = Math.Max(0, origin.Y - 1); y < Math.Min(map.Height, origin.Y + Grid.Rows * Session.Stride + 2); y++)
            for (int x = Math.Max(0, origin.X - 1); x < Math.Min(map.Width, origin.X + Grid.Columns * Session.Stride + 1); x++)
            {
                if (map.FeatureAt(x, y) is not { } type || !TerrainSprite.NaturalFeatureVisible(Session.Game, x, y) ||
                    type.Name is not (DefaultFeatures.TreeName or DefaultFeatures.RockName)) continue;
                var key = (type.Id, CellHash.Pick(x, y, 6));
                if (!_features.TryGetValue(key, out var cached))
                {
                    var sprite = TerrainSprite.Feature(type, key.Item2);
                    using var image = Image.CreateFromData(sprite.Width, sprite.Height, false, Image.Format.Rgba8, sprite.Pixels);
                    cached = (ImageTexture.CreateFromImage(image), sprite);
                    _features.Add(key, cached);
                }
                var position = new Vector2((x - origin.X) * sx - 3 * pixelScale,
                    (y - origin.Y) * sy - cached.Sprite.Lift * pixelScale);
                DrawTextureRect(cached.Texture,
                    new Rect2(position, new Vector2(cached.Sprite.Width, cached.Sprite.Height) * pixelScale), false);
            }
    }

    private void DrawBuildingFootprints(int cellWidth, int cellHeight)
    {
        BuildingsDrawn = 0;
        var origin = Session.ScreenToMap(0, 0);
        var map = Session.Game.Map;
        foreach (var area in BuildingAreasInView().OrderBy(a => a.Bottom).ThenBy(a => a.X))
        {
            var type = map.BuildingAt(area.X, area.Y)!;
            float pixelScale = cellWidth / (12f * Session.Stride);
            int variant = CellHash.Pick(area.X, area.Y, 2);
            var position = new Vector2((area.X - origin.X) * cellWidth / (float)Session.Stride,
                (area.Y - origin.Y) * cellHeight / (float)Session.Stride);
            if (!new Rect2(position - new Vector2(0, 24 * pixelScale),
                new Vector2(area.Width * cellWidth / (float)Session.Stride,
                    (area.Height * 22 + 24) * pixelScale)).Intersects(new Rect2(Vector2.Zero, Size))) continue;
            var key = (type.Id, area.Width, area.Height, variant);
            if (!_buildings.TryGetValue(key, out var cached))
            {
                var sprite = BuildingSprite.Render(type, area.Width, area.Height, variant);
                using var image = Image.CreateFromData(sprite.Width, sprite.Height, false, Image.Format.Rgba8, sprite.Pixels);
                cached = (ImageTexture.CreateFromImage(image), sprite);
                _buildings.Add(key, cached);
            }

            TextureFilter = TextureFilterEnum.Nearest;
            var rect = new Rect2(position - new Vector2(0, cached.Sprite.Lift * pixelScale),
                new Vector2(cached.Sprite.Width, cached.Sprite.Height) * pixelScale);
            bool selected = !Session.FixedPlacementToolActive && Session.Selection is not null && Session.HasCursor &&
                Session.ActiveArea == Session.BlockAt(Session.Cursor) && area.Contains(Session.Cursor);
            DrawTextureRect(cached.Texture, rect, false, selected ? new Color(1.18f, 1.14f, 1.05f) : Colors.White);
            BuildingsDrawn++;
            var center = new Pos((area.X - origin.X) / Session.Stride, (area.Y - origin.Y) / Session.Stride);
            if (center.X >= 0 && center.Y >= 0 && center.X < Grid.Columns && center.Y < Grid.Rows &&
                Grid.HighlightAt(Session, center.X, center.Y) is { } highlight)
                DrawRect(rect, ToColor(highlight) with { A = 0.7f }, filled: false);
        }
        if (Session.Preview is { Building: { } previewType } preview)
            foreach (var area in Session.Game.PlanBuildings(previewType, preview.Area))
            {
                float pixelScale = cellWidth / (12f * Session.Stride);
                var key = (previewType.Id, area.Width, area.Height, 0);
                if (!_buildings.TryGetValue(key, out var cached))
                {
                    var sprite = BuildingSprite.Render(previewType, area.Width, area.Height);
                    using var image = Image.CreateFromData(sprite.Width, sprite.Height, false, Image.Format.Rgba8, sprite.Pixels);
                    cached = (ImageTexture.CreateFromImage(image), sprite);
                    _buildings.Add(key, cached);
                }
                var position = new Vector2((area.X - origin.X) * cellWidth / (float)Session.Stride,
                    (area.Y - origin.Y) * cellHeight / (float)Session.Stride - cached.Sprite.Lift * pixelScale);
                DrawTextureRect(cached.Texture,
                    new Rect2(position, new Vector2(cached.Sprite.Width, cached.Sprite.Height) * pixelScale),
                    false, new Color(0.7f, 1, 0.8f, 0.6f));
            }
    }

    private IEnumerable<CellRect> BuildingAreasInView()
    {
        var map = Session.Game.Map;
        var origin = Session.ScreenToMap(0, 0);
        var view = new CellRect(origin.X - 5, origin.Y - 3,
            Grid.Columns * Session.Stride + 10, Grid.Rows * Session.Stride + 6);
        foreach (var area in map.BuildingFootprints.Values)
            if (area.Right >= view.X && area.X <= view.Right && area.Bottom >= view.Y && area.Y <= view.Bottom)
                yield return area;
        for (int y = Math.Max(0, view.Y); y <= Math.Min(map.Height - 1, view.Bottom); y++)
            for (int x = Math.Max(0, view.X); x <= Math.Min(map.Width - 1, view.Right); x++)
                if (map.BuildingAt(x, y) is not null && map.IsBuildingAnchor(map.Index(x, y)) &&
                    !map.BuildingFootprints.ContainsKey(map.Index(x, y)))
                    yield return new(x, y, 1, 1);
    }

    public bool TryPickBuilding(Vector2 pointer, out Pos anchor)
    {
        anchor = default;
        if (!new Rect2(Vector2.Zero, Size).HasPoint(pointer)) return false;
        RefreshCells();
        if (PerspectiveEnabled && Perspective is { } perspective)
        {
            perspective.Sync(Session, Size);
            return perspective.PickBuilding(pointer, out anchor);
        }
        var origin = Session.ScreenToMap(0, 0);
        double worldX = origin.X * RoadCurves.CellWidth +
            (pointer.X - _shakeOffset.X) * RoadCurves.CellWidth * Session.Stride / Grid.PixelWidth;
        double worldY = origin.Y * RoadCurves.CellHeight +
            (pointer.Y - _shakeOffset.Y) * RoadCurves.CellHeight * Session.Stride / Grid.PixelHeight;
        var wallCell = new Pos((int)Math.Floor(worldX / RoadCurves.CellWidth), (int)Math.Floor(worldY / RoadCurves.CellHeight));
        if (Session.Game.Settlement?.Walls.Any(w => w.Cell == wallCell) == true)
        {
            anchor = wallCell;
            return true;
        }
        foreach (var area in BuildingAreasInView().OrderByDescending(a => a.Bottom).ThenByDescending(a => a.X))
        {
            if (worldX < area.X * RoadCurves.CellWidth || worldX >= (area.Right + 1) * RoadCurves.CellWidth + 4 ||
                worldY < area.Y * RoadCurves.CellHeight - 24 || worldY >= (area.Bottom + 1) * RoadCurves.CellHeight + 4)
                continue;
            var type = Session.Game.Map.BuildingAt(area.X, area.Y)!;
            var key = (type.Id, area.Width, area.Height, CellHash.Pick(area.X, area.Y, 2));
            if (!_buildings.TryGetValue(key, out var cached))
            {
                var sprite = BuildingSprite.Render(type, area.Width, area.Height, key.Item4);
                using var image = Image.CreateFromData(sprite.Width, sprite.Height, false, Image.Format.Rgba8, sprite.Pixels);
                cached = (ImageTexture.CreateFromImage(image), sprite);
                _buildings.Add(key, cached);
            }
            if (cached.Sprite.Pick(area, worldX, worldY) is { } picked)
            {
                anchor = picked;
                return true;
            }
        }
        return false;
    }

    private void DrawPaths(int cellWidth, int cellHeight)
    {
        var map = Session.Game.Map;
        var origin = Session.ScreenToMap(0, 0);
        float sx = cellWidth / (float)Session.Stride, sy = cellHeight / (float)Session.Stride;
        for (int y = Math.Max(0, origin.Y); y < Math.Min(map.Height, origin.Y + Grid.Rows * Session.Stride); y++)
            for (int x = Math.Max(0, origin.X); x < Math.Min(map.Width, origin.X + Grid.Columns * Session.Stride); x++)
            {
                int wear = map.PathWearAt(x, y);
                if (wear == 0 || !map.FootpathsAllowedAt(x, y) || map.HasRoad(x, y) || map.BuildingAt(x, y) is not null) continue;
                var center = new Vector2((x - origin.X + 0.5f) * sx, (y - origin.Y + 0.5f) * sy);
                var color = new Color("#b19564") with { A = Math.Min(0.85f, 0.25f + wear * 0.05f) };
                float thickness = Math.Max(1, sx * 0.2f);
                DrawRect(new Rect2(center - Vector2.One * thickness / 2, Vector2.One * thickness), color);
                foreach (var (dx, dy) in TermCity.Core.Simulation.RoadNetwork.Neighbors)
                    if (map.FootpathsAllowedAt(x + dx, y + dy) && (map.PathWearAt(x + dx, y + dy) > 0 ||
                        map.HasRoad(x + dx, y + dy) || map.BuildingAt(x + dx, y + dy) is not null))
                        DrawLine(center, center + new Vector2(dx * sx, dy * sy) / 2, color, thickness);
            }
    }

    private void DrawAreas(int cellWidth, int cellHeight, int fontSize)
    {
        AreasDrawn = 0;
        if (!Session.AreaOverlaysVisible) return;
        var origin = Session.ScreenToMap(0, 0);
        var view = new CellRect(origin.X, origin.Y, Grid.Columns * Session.Stride, Grid.Rows * Session.Stride);
        IEnumerable<EffectArea> areas = Session.HasCursor && !Session.FixedPlacementToolActive
            ? AreaOfEffect.InView(Session.Game, view, Session.ActiveArea) : [];
        if (Session.BuildingPlacementError is null && Session.Preview is { Building: { } type } preview)
            areas = areas.Concat(Session.Game.PlanBuildings(type, preview.Area)
                .SelectMany(area => AreaOfEffect.ForBuilding(type, new(area.X, area.Y))));
        AreasDrawn = 0;
        const int pixel = 3;
        foreach (var group in areas.GroupBy(area => area.Center))
        {
            var center = new Vector2((group.Key.X - origin.X + 0.5f) * cellWidth / Session.Stride,
                (group.Key.Y - origin.Y + 0.5f) * cellHeight / Session.Stride);
            foreach (var area in group)
            {
                if (_areaFade is null)
                {
                    const int size = 128;
                    using var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                        {
                            double dx = (x + 0.5 - size / 2d) / (size / 2d);
                            double dy = (y + 0.5 - size / 2d) / (size / 2d);
                            image.SetPixel(x, y, new Color(1, 1, 1,
                                AreaOfEffect.FadeAlpha(Math.Sqrt(dx * dx + dy * dy) * AreaOfEffect.FadeExtent)));
                        }
                    _areaFade = ImageTexture.CreateFromImage(image);
                }
                float radius = area.Radius * cellWidth / (float)Session.Stride * (float)AreaOfEffect.FadeExtent;
                DrawTextureRect(_areaFade, new Rect2(center - Vector2.One * radius, Vector2.One * radius * 2), false);
                AreasDrawn++;
            }
            string glyphs = string.Join(" ", group.SelectMany(area => area.Glyphs));
            float width = CellFont.GetStringSize(glyphs, fontSize: fontSize).X;
            var label = center + new Vector2(-width / 2,
                group.Max(area => area.Radius) * cellWidth / (float)Session.Stride + pixel + CellFont.GetAscent(fontSize));
            DrawString(CellFont, label, glyphs, fontSize: fontSize,
                modulate: new Color(1, 1, 1, AreaOfEffect.IconAlpha));
        }
    }

    /// <summary>
    /// Lays the curved roads over the terrain, then lets the cursor, selection and previews show through them.
    /// </summary>
    private void DrawRoadCurves(int scale, int cellWidth, int cellHeight)
    {
        RoadChunksDrawn = 0;
        if (!Grid.VectorRoads)
        {
            return;
        }

        if (_roads.Sync(Session.Game))
        {
            foreach (var texture in _roadChunks.Values) texture?.Dispose();
            _roadChunks.Clear();
        }

        if (_roads.Paths.Count == 0)
        {
            return;
        }

        const int chunk = RoadVectorLayer.ChunkCells;
        int stride = Session.Stride, span = chunk * stride;
        var origin = Session.ScreenToMap(0, 0);
        int lastX = origin.X + Grid.Columns * stride - 1, lastY = origin.Y + Grid.Rows * stride - 1;
        for (int cy = Math.Max(0, origin.Y) / span; cy <= lastY / span; cy++)
        {
            for (int cx = Math.Max(0, origin.X) / span; cx <= lastX / span; cx++)
            {
                if (!_roadChunks.TryGetValue((cx, cy, scale, stride), out var texture))
                {
                    texture = _roads.Render(cx, cy, scale, stride) is { } rendered
                        ? ImageTexture.CreateFromImage(Image.CreateFromData(
                            rendered.Width, rendered.Height, false, Image.Format.Rgba8, rendered.Rgba))
                        : null;
                    _roadChunks[(cx, cy, scale, stride)] = texture;
                }

                if (texture is not null)
                {
                    var at = new Vector2((cx * chunk - origin.X / stride) * cellWidth, (cy * chunk - origin.Y / stride) * cellHeight);
                    DrawTexture(texture, at);
                    RoadChunksDrawn++;
                }
            }
        }

        for (int y = 0; y < Grid.Rows; y++)
        {
            for (int x = 0; x < Grid.Columns; x++)
            {
                if (Grid.IsVectorRoad(x, y) && Grid.HighlightAt(Session, x, y) is { } highlight)
                {
                    var tint = ToColor(highlight);
                    tint.A = 0.6f;
                    DrawRect(new Rect2(x * cellWidth, y * cellHeight, cellWidth, cellHeight), tint);
                }
            }
        }
    }

    /// <summary>Draws one effect glyph scaled about its own centre.</summary>
    private void DrawEffectGlyph(in GlyphDraw draw, int cellWidth, int cellHeight, float baseline, int fontSize)
    {
        DrawSetTransform(new Vector2(draw.CentreX, draw.CentreY), 0f, new Vector2(draw.Scale, draw.Scale));
        var colour = ToColor(draw.Color);
        colour.A = draw.Alpha;
        DrawCellGlyph(draw.Glyph, new Vector2(-cellWidth / 2f, -cellHeight / 2f),
            cellWidth, cellHeight, fontSize, baseline, colour);
        DrawSetTransform(_shakeOffset, 0f, Vector2.One);
    }

    private void DrawCellGlyph(string glyph, Vector2 topLeft, int cellWidth, int cellHeight, int fontSize,
        float baseline, Color colour)
    {
        if (!glyph.Contains('\n'))
        {
            DrawString(CellFont, topLeft + new Vector2(0, baseline), glyph,
                HorizontalAlignment.Center, cellWidth, fontSize, colour);
            return;
        }
        var rows = glyph.Split('\n');
        int size = fontSize / 2;
        float rowHeight = cellHeight / (float)rows.Length;
        float artBaseline = (rowHeight - CellFont.GetHeight(size)) / 2 + CellFont.GetAscent(size);
        for (int row = 0; row < rows.Length; row++)
            DrawString(CellFont, topLeft + new Vector2(0, row * rowHeight + artBaseline), rows[row],
                HorizontalAlignment.Center, cellWidth, size, colour);
    }

    private static Color ToColor(Rgb color) => new(color.R / 255f, color.G / 255f, color.B / 255f);

    public void VerifyGlyphs(GameContent content)
    {
        var glyphs = content.Terrains.SelectMany(t => t.Glyphs)
            .Concat(content.Features.SelectMany(f => f.Glyphs))
            .Concat(content.Buildings.SelectMany(b => b.Glyphs))
            .Concat(content.Buildings.SelectMany(b => b.FootprintArt))
            .Concat(new[] { "♧", "Ψ" })
            .Concat(content.Roads.SelectMany(r => r.Glyphs))
            .Concat(Zones.Placeable.Select(zone => Zones.Get(zone).EmptyGlyph));
        var missing = glyphs.SelectMany(glyph => glyph.EnumerateRunes())
            .Where(rune => !CellFont.HasChar(rune.Value))
            .Distinct()
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"Map font is missing glyphs: {string.Join(", ", missing)}.");
        }
    }
}
