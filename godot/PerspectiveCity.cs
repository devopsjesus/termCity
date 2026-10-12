using Godot;
using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

/// <summary>A low-resolution orthographic 3D city view sharing the existing map and session.</summary>
public partial class PerspectiveCity : Node
{
    public const float NormalMagnification = 1.8f;
    public const float InitialAzimuth = 325;
    public const float ZoneOverlayOpacity = 0.58f;
    private readonly SubViewport _viewport = new()
    {
        OwnWorld3D = true,
        TransparentBg = false,
        RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        Msaa3D = Viewport.Msaa.Msaa2X
    };
    private readonly Camera3D _camera = new() { Projection = Camera3D.ProjectionType.Orthogonal, Near = 0.1f, Far = 600 };
    private readonly Node3D _world = new();
    private readonly Node3D _indicators = new();
    private readonly BoxMesh _cube = new() { Size = Vector3.One };
    private readonly Dictionary<int, List<Transform3D>> _boxBatches = [];
    private readonly Dictionary<int, SurfaceTool> _roofBatches = [];
    private readonly Dictionary<int, StandardMaterial3D> _materials = [];
    private readonly Dictionary<CellVisual, StandardMaterial3D> _zoneMaterials = [];
    private readonly Dictionary<CellVisual, SurfaceTool> _zoneBatches = [];
    private readonly Dictionary<int, SurfaceTool> _footpathBatches = [];
    public int FootpathTrianglesDrawn { get; private set; }
    private sealed record GroundMaterial(ImageTexture Texture, ImageTexture Flow, ImageTexture Shore,
        ShaderMaterial Material, bool RiverFlow) : IDisposable
    {
        public void Dispose() { Texture.Dispose(); Flow.Dispose(); Shore.Dispose(); Material.Dispose(); }
    }
    private readonly Dictionary<(int X, int Y), GroundMaterial> _groundMaterials = [];
    private readonly Shader _terrainShader = new()
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, cull_disabled;
            uniform sampler2D terrain_image : filter_nearest, repeat_disable;
            uniform sampler2D flow_image : filter_nearest, repeat_disable;
            uniform sampler2D shore_image : filter_linear, repeat_disable;
            uniform float flow_clock = 0.0;
            varying vec4 terrain_tint;
            void vertex() { terrain_tint = COLOR; }
            void fragment() {
                vec4 base = texture(terrain_image, UV);
                vec4 flow = texture(flow_image, UV);
                vec2 encoded = round(flow.rg * 8.0);
                vec2 direction = floor(encoded / 3.0) - 1.0;
                vec2 incoming = mod(encoded, 3.0) - 1.0;
                vec2 pixel = floor(UV * vec2(textureSize(terrain_image, 0)));
                vec2 patch = floor(pixel / vec2(4.0, 8.0));
                float tone = fract(sin(dot(patch, vec2(12.9898, 78.233))) * 43758.5453);
                if (terrain_tint.a < 0.5 && base.b <= base.g * 1.05) {
                    base.rgb = mix(vec3(32.0, 87.0, 118.0), vec3(48.0, 117.0, 142.0), floor(tone * 5.0) / 4.0) / 255.0;
                } else if (terrain_tint.a >= 0.5 && (base.b > base.g * 1.05 || base.r >= base.g)) {
                    base.rgb = mix(vec3(56.0, 92.0, 47.0), vec3(80.0, 120.0, 60.0), floor(tone * 5.0) / 4.0) / 255.0;
                }
                vec2 cell_pixels = vec2(12.0, 22.0);
                vec2 cell_position = UV * vec2(textureSize(terrain_image, 0)) / cell_pixels;
                if (terrain_tint.a < 0.5 && flow.b > 0.5) {
                    float phase = mod(flow_clock * 0.75 + round(flow.a * 8.0), 8.0);
                    if (phase < 1.0) {
                        vec2 center = -incoming * 0.5 * (1.0 - phase) * (1.0 - phase) + direction * 0.5 * phase * phase;
                        vec2 offset = (floor(fract(cell_position) * cell_pixels) + 0.5) / cell_pixels - 0.5 - center;
                        vec2 tangent = normalize(mix(incoming, direction, phase));
                        float across = abs(dot(offset, vec2(-tangent.y, tangent.x)));
                        float along = abs(dot(offset, tangent));
                        float ripple = max((1.0 - step(0.24, across)) * (1.0 - step(0.12, along)),
                            (1.0 - step(0.10, across)) * (1.0 - step(0.20, along)));
                        base.rgb = mix(base.rgb, vec3(63.0, 133.0, 159.0) / 255.0, ripple * 0.55);
                    }
                }
                if (terrain_tint.a >= 0.5) {
                    vec3 shore = texture(shore_image, (floor(UV * 16.0) + 1.5) / 18.0).rgb;
                    float bank = step(0.5, shore.r);
                    float sand = (1.0 - smoothstep(3.25, 3.5, shore.g)) * smoothstep(0.6, 0.7, shore.b);
                    sand *= 1.0 - step(0.76, terrain_tint.a);
                    vec3 earth = mix(vec3(62.0, 65.0, 42.0), vec3(76.0, 78.0, 48.0), floor(tone * 5.0) / 4.0) / 255.0;
                    vec3 beach = mix(vec3(125.0, 111.0, 77.0), vec3(150.0, 135.0, 96.0), floor(tone * 5.0) / 4.0) / 255.0;
                    base.rgb = mix(mix(base.rgb, earth, bank), beach, sand);
                }
                ALBEDO = base.rgb * terrain_tint.rgb;
            }
            """,
    };
    private bool _waterAnimating;
    private bool _visibleRiverFlow;
    private double _flowClock, _flowFrame;
    public double WaterFlowClock => _flowClock;
    internal double WaterFlowRenderClock { get; private set; }
    public bool WaterMotionEnabled { get; set; } = true;
    internal void RequestRender()
    {
        _renderFrames = 2;
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
    }
    private readonly Dictionary<(int X, int Y), ArrayMesh> _groundMeshes = [];
    private readonly Dictionary<(int X, int Y), ArrayMesh> _cliffMeshes = [];
    private readonly Dictionary<(int X, int Y), RoadSurfaceMeshes.Chunk> _roadChunks = [];
    private readonly StandardMaterial3D _roadMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };
    private RoadMeshLayout? _roadLayout;
    private RoadSurfaceMeshes? _roadSurfaces;
    private readonly Dictionary<int, Vector3[]> _groundFaces = [];
    internal bool GradedPanelAt(GameMap map, float x, float y)
        => _roadLayout?.InCorridor(new(x, y)) == true || _roadSurfaces?.IsRemnant(map, new(x, y)) == true;
    private readonly List<(Vector2 A, Vector2 B)> _cliffEdges = [];
    internal IReadOnlyList<(Vector2 A, Vector2 B)> CliffEdges => _cliffEdges;
    public int ExposedCutTriangles { get; private set; }
    private readonly List<Vector3> _exposedCutCenters = [];
    private readonly List<Vector3> _wallSamples = [];
    internal IReadOnlyList<Vector3> WallSamples => _wallSamples;
    internal IReadOnlyList<Vector3> ExposedCutCenters => _exposedCutCenters;
    internal bool RoadSurfaceAt(Vector2 point, int clearance = 0) => _roadLayout?.InRoadInterior(new(point.X, point.Y)) == true;
    internal bool BridgeSurfaceAt(Vector2 point) => _roadLayout?.RoadAt(new(point.X, point.Y))?.Bridge == true;
    private readonly StandardMaterial3D _cliffMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };
    private GameMap? _groundMap;
    private int _groundVersion = -1;
    private int _groundRoadRevision = -1;
    private readonly List<(Aabb Bounds, Pos Anchor, int Color)> _boxes = [];
    private readonly List<(Vector3 A, Vector3 B, Vector3 C, Pos Anchor)> _roofFaces = [];
    private readonly List<(Transform3D Transform, RoadPath Path)> _bridgeRails = [];
    internal IReadOnlyList<(Transform3D Transform, RoadPath Path)> BridgeRails => _bridgeRails;
    private readonly RoadVectorLayer _roads = new();
    private GameMap? _map;
    private int _version = -1;
    private CellRect _view;
    private CellRect? _selection;
    private PlacementPreview? _preview;
    private MapOverlay _overlay;
    private bool _drawingPreview;
    private Transform3D _buildingTransform = Transform3D.Identity;
    private bool _cameraValid;
    private (int X, int Y, int Zoom, int Width, int Height, int CellsX, int CellsY, float Azimuth, float Elevation, Vector2 Offset) _cameraState;
    private Vector2 _cameraOffset;
    private (int X, int Y, int Zoom) _offsetCamera;
    private int _renderFrames;
    private int _groundOverlayVersion = -1;
    private MapOverlay _groundOverlay;
    private int _builtCount;
    private float _indicatorThickness;
    public float SelectionOutlineThickness => _indicatorThickness;
    public float SelectionOutlineHeight { get; private set; }
    public int GeometryBuilds { get; private set; }
    public double GroundBuildMilliseconds { get; private set; }
    public double ObjectBuildMilliseconds { get; private set; }
    public double TerrainPixelsMilliseconds { get; private set; }
    public double RoadPixelsMilliseconds { get; private set; }
    public int SceneObjectCount => _world.GetChildCount() + _indicators.GetChildCount();
    public int IndicatorObjectCount => _indicators.GetChildCount();
    public float CameraSpan => _camera.Size;
    public Vector2I RenderSize => _viewport.Size;
    public CellRect RenderedView => _view;
    public float Azimuth { get; private set; } = InitialAzimuth;
    public float Elevation { get; private set; } = 35;
    public int BuildingsDrawn { get; private set; }
    public int ZonePlotsDrawn { get; private set; }
    public int RoadMeshChunksDrawn { get; private set; }
    public int BridgeSegmentsDrawn { get; private set; }
    internal HashSet<TreeShape> TreeShapesDrawn { get; } = [];
    internal IReadOnlyList<Vector3> SurfaceVertices(Pos cell) =>
        _map is { } map && _groundFaces.TryGetValue(map.Index(cell.X, cell.Y), out var vertices)
            ? vertices : Array.Empty<Vector3>();
    public ViewportTexture Texture => _viewport.GetTexture();
    public Vector2 DisplaySize { get; private set; }
    public Vector2 CameraOffset => _cameraOffset;

    public void ResetCameraOffset() => _cameraOffset = Vector2.Zero;

    public void SetCameraOffset(GameSession session, Vector2 offset)
    {
        _cameraOffset = offset;
        _offsetCamera = (session.CameraX, session.CameraY, session.ZoomLevel);
    }
    public IReadOnlyList<Aabb> BuildingVolumes(Pos anchor, int? color = null) =>
        _boxes.Where(box => box.Anchor == anchor && (color is null || box.Color == color))
            .Select(box => box.Bounds).ToArray();

    public void SetActive(bool active)
    {
        if (!active) _waterAnimating = false;
        _renderFrames = active ? 2 : 0;
        _viewport.RenderTargetUpdateMode = active ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
    }

    public override void _Process(double delta)
    {
        if (_waterAnimating)
        {
            _flowClock += delta;
            _flowFrame += delta;
            if (_flowFrame >= 0.125)
            {
                _flowFrame = 0;
                WaterFlowRenderClock = _flowClock;
                foreach (var material in _groundMaterials.Values)
                    material.Material.SetShaderParameter("flow_clock", (float)WaterFlowRenderClock);
                _renderFrames = 2;
                _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            }
        }
        if (_renderFrames > 0) _renderFrames--;
        else if (_renderFrames == 0)
        {
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            _renderFrames = -1;
        }
    }

    public override void _Ready()
    {
        AddChild(_viewport);
        _viewport.AddChild(_world);
        _viewport.AddChild(_indicators);
        _viewport.AddChild(_camera);
        _camera.Current = true;
        const int textureSize = 32;
        var roadPixels = new byte[textureSize * textureSize * 4];
        for (int y = 0; y < textureSize; y++)
            for (int x = 0; x < textureSize; x++)
            {
                int shift = CellHash.Pick(y / 4, 19, 3);
                byte shade = (byte)(230 + CellHash.Pick((x + shift) / 3, y / 4, 5) * 6);
                int pixel = (y * textureSize + x) * 4;
                roadPixels[pixel] = roadPixels[pixel + 1] = roadPixels[pixel + 2] = shade;
                roadPixels[pixel + 3] = 255;
            }
        using (var image = Image.CreateFromData(textureSize, textureSize, false, Image.Format.Rgba8, roadPixels))
            _roadMaterial.AlbedoTexture = ImageTexture.CreateFromImage(image);
        _roadMaterial.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;
        _roadMaterial.TextureRepeat = true;
        _roadMaterial.AlbedoColor = new Color(1.05f, 1.05f, 1.05f);
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color("#14221c"),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color("#d3d5bc"),
            AmbientLightEnergy = 0.7f,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        _viewport.AddChild(new WorldEnvironment { Environment = environment });
        _viewport.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new(-55, -35, 0),
            LightColor = new Color("#ffe4ba"),
            LightEnergy = 0.8f,
            ShadowEnabled = false
        });
    }

    public void Rotate(float degrees) => Azimuth = Mathf.PosMod(Azimuth + degrees, 360);
    public void Tilt() => Elevation = Elevation == 20 ? 35 : Elevation == 35 ? 50 : 20;

    public override void _ExitTree()
    {
        foreach (var material in _groundMaterials.Values)
        {
            material.Dispose();
        }
        _groundMaterials.Clear();
        _terrainShader.Dispose();
        foreach (var mesh in _groundMeshes.Values) mesh.Dispose();
        _groundMeshes.Clear();
        foreach (var mesh in _cliffMeshes.Values) mesh.Dispose();
        _cliffMeshes.Clear();
        foreach (var chunk in _roadChunks.Values) chunk.Roads?.Dispose();
        _roadChunks.Clear();
        _roadMaterial.AlbedoTexture?.Dispose();
        _roadMaterial.Dispose();
        _cliffMaterial.Dispose();
        _cube.Dispose();
        foreach (var material in _materials.Values)
        {
            material.AlbedoTexture?.Dispose();
            material.Dispose();
        }
        _materials.Clear();
        foreach (var material in _zoneMaterials.Values)
        {
            material.AlbedoTexture?.Dispose();
            material.Dispose();
        }
        _zoneMaterials.Clear();
    }

    public void Sync(GameSession session, Vector2 displaySize)
    {
        DisplaySize = displaySize;
        _waterAnimating = WaterMotionEnabled && _visibleRiverFlow;
        if (_offsetCamera != (session.CameraX, session.CameraY, session.ZoomLevel) ||
            !ReferenceEquals(_map, session.Game.Map))
            ResetCameraOffset();
        var cameraState = (session.CameraX, session.CameraY, session.ZoomLevel,
            (int)displaySize.X, (int)displaySize.Y, session.ViewWidth, session.ViewHeight, Azimuth, Elevation, _cameraOffset);
        bool scaleChanged = !_cameraValid || _cameraState.Zoom != session.ZoomLevel;
        bool cameraChanged = !_cameraValid || _cameraState != cameraState;
        if (cameraChanged)
        {
            _cameraState = cameraState;
            _cameraValid = true;
            _viewport.Size = new Vector2I(Math.Max(2, (int)displaySize.X), Math.Max(2, (int)displaySize.Y));
            float zoom = session.ZoomLevel > 0 ? 2 : 1f / session.Stride;
            _camera.Size = Math.Max(4, displaySize.Y / (22 * zoom * NormalMagnification));
            float azimuth = Mathf.DegToRad(Azimuth), elevation = Mathf.DegToRad(Elevation);
            var target = new Vector3(session.CameraX + _cameraOffset.X + session.ViewWidth * session.Stride / 2f, 0,
                session.CameraY + _cameraOffset.Y + session.ViewHeight * session.Stride / 2f);
            _camera.Position = target + new Vector3(Mathf.Sin(azimuth) * Mathf.Cos(elevation),
                Mathf.Sin(elevation), Mathf.Cos(azimuth) * Mathf.Cos(elevation)) * 150;
            _camera.LookAt(target, Vector3.Up);
        }
        var corners = new[] { Vector2.Zero, new Vector2(displaySize.X, 0),
            displaySize, new Vector2(0, displaySize.Y) }.Select(GroundPoint).ToArray();
        var map = session.Game.Map;
        int left = Math.Clamp((int)Math.Floor(corners.Min(p => p.X)) - 6, 0, map.Width - 1);
        int top = Math.Clamp((int)Math.Floor(corners.Min(p => p.Z)) - 6, 0, map.Height - 1);
        int right = Math.Clamp((int)Math.Ceiling(corners.Max(p => p.X)) + 6, left, map.Width - 1);
        int bottom = Math.Clamp((int)Math.Ceiling(corners.Max(p => p.Z)) + 6, top, map.Height - 1);
        var view = new CellRect(left, top, right - left + 1, bottom - top + 1);
        var selection = session.HasCursor && !session.FixedPlacementToolActive ? session.ActiveArea : (CellRect?)null;
        bool geometryChanged = !ReferenceEquals(_map, map) || _version != session.Game.MapVersion ||
            _overlay != session.Overlay || view.X < _view.X || view.Y < _view.Y ||
            view.Right > _view.Right || view.Bottom > _view.Bottom ||
            scaleChanged;
        float outlineThickness = 2 * _camera.Size / Math.Max(1, displaySize.Y);
        bool indicatorsChanged = geometryChanged || _selection != selection ||
            _indicatorThickness != outlineThickness || !ReferenceEquals(_preview, session.Preview);
        _indicatorThickness = outlineThickness;
        _map = map; _version = session.Game.MapVersion;
        _selection = selection; _preview = session.Preview; _overlay = session.Overlay;
        if (geometryChanged)
        {
            const int chunk = TerrainSprite.ChunkCells, margin = 12;
            left = Math.Max(0, left / chunk * chunk - margin);
            top = Math.Max(0, top / chunk * chunk - margin);
            right = Math.Min(map.Width - 1, (right / chunk + 1) * chunk + margin - 1);
            bottom = Math.Min(map.Height - 1, (bottom / chunk + 1) * chunk + margin - 1);
            _view = new(left, top, right - left + 1, bottom - top + 1);
            Rebuild(session);
            _waterAnimating = WaterMotionEnabled && _visibleRiverFlow;
        }
        if (indicatorsChanged) RebuildIndicators(session);
        if (cameraChanged || geometryChanged || indicatorsChanged)
        {
            _renderFrames = 2;
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        }
    }

    private Vector2 ViewportPoint(Vector2 pointer) => new(pointer.X * _viewport.Size.X / Math.Max(1, DisplaySize.X),
        pointer.Y * _viewport.Size.Y / Math.Max(1, DisplaySize.Y));

    public Vector3 GroundPlanePoint(Vector2 pointer)
    {
        var pixel = ViewportPoint(pointer);
        var ray = _camera.ProjectRayNormal(pixel);
        var origin = _camera.ProjectRayOrigin(pixel);
        return origin + ray * (-origin.Y / ray.Y);
    }

    internal Vector2[] GroundViewCorners() => new[] { Vector2.Zero, new Vector2(DisplaySize.X, 0),
        DisplaySize, new Vector2(0, DisplaySize.Y) }.Select(pointer =>
        {
            var ground = GroundPlanePoint(pointer);
            return new Vector2(ground.X, ground.Z);
        }).ToArray();

    public Vector3 GroundPoint(Vector2 pointer) => GroundHit(pointer).Point;

    private (Vector3 Point, RoadPath? Bridge) GroundHit(Vector2 pointer)
    {
        var pixel = ViewportPoint(pointer);
        var ray = _camera.ProjectRayNormal(pixel);
        var origin = _camera.ProjectRayOrigin(pixel);
        var ground = GroundPlanePoint(pointer);
        if (_map is not { } map) return (ground, null);
        int x = (int)Math.Floor(ground.X), y = (int)Math.Floor(ground.Z);
        float nearest = float.PositiveInfinity;
        int reach = (int)Math.Ceiling(TerrainSprite.MaximumHillHeight *
            new Vector2(ray.X, ray.Z).Length() / Math.Abs(ray.Y)) + 1;
        for (int dy = -reach; dy <= reach; dy++)
            for (int dx = -reach; dx <= reach; dx++)
            {
                int cx = x + dx, cy = y + dy;
                if (!map.InBounds(cx, cy)) continue;
                if (_groundFaces.TryGetValue(map.Index(cx, cy), out var faces))
                {
                    for (int i = 0; i < faces.Length; i += 3)
                    {
                        float distance = RayTriangleDistance(origin, ray, faces[i], faces[i + 1], faces[i + 2]);
                        if (distance < nearest)
                        {
                            nearest = distance;
                            ground = origin + ray * distance;
                        }
                    }
                }
                else
                {
                    float distance = (TerrainSprite.Elevation(map, cx, cy) - origin.Y) / ray.Y;
                    var point = origin + ray * distance;
                    if (distance >= 0 && distance < nearest && point.X >= cx && point.X < cx + 1 &&
                        point.Z >= cy && point.Z < cy + 1)
                    {
                        nearest = distance;
                        ground = point;
                    }
                }
            }
        RoadPath? bridge = null;
        foreach (var rail in _bridgeRails)
        {
            float distance = RayBoxDistance(origin, ray, rail.Transform);
            if (distance < nearest)
            {
                nearest = distance;
                ground = origin + ray * distance;
                bridge = rail.Path;
            }
        }
        return (ground, bridge);
    }

    private static float RayBoxDistance(Vector3 origin, Vector3 direction, Transform3D transform)
    {
        var inverse = transform.AffineInverse();
        var localOrigin = inverse * origin;
        var localDirection = inverse.Basis * direction;
        float near = 0, far = float.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            if (Math.Abs(localDirection[axis]) < 0.000001f)
            {
                if (Math.Abs(localOrigin[axis]) > 0.5f) return float.PositiveInfinity;
                continue;
            }
            float start = (-0.5f - localOrigin[axis]) / localDirection[axis];
            float end = (0.5f - localOrigin[axis]) / localDirection[axis];
            near = Math.Max(near, Math.Min(start, end));
            far = Math.Min(far, Math.Max(start, end));
            if (near > far) return float.PositiveInfinity;
        }
        return near;
    }

    private static float RayTriangleDistance(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
    {
        var edge1 = b - a; var edge2 = c - a;
        var cross = direction.Cross(edge2);
        float determinant = edge1.Dot(cross);
        if (Math.Abs(determinant) < 0.000001f) return float.PositiveInfinity;
        var offset = origin - a;
        float u = offset.Dot(cross) / determinant;
        var q = offset.Cross(edge1);
        float v = direction.Dot(q) / determinant;
        float distance = edge2.Dot(q) / determinant;
        return u >= -0.001f && v >= -0.001f && u + v <= 1.001f && distance >= 0
            ? distance : float.PositiveInfinity;
    }

    internal float RenderedGroundHeight(GameMap map, float x, float z)
    {
        int cx = Math.Clamp((int)Math.Floor(x), 0, map.Width - 1);
        int cy = Math.Clamp((int)Math.Floor(z), 0, map.Height - 1);
        if (_groundFaces.TryGetValue(map.Index(cx, cy), out var faces))
        {
            var origin = new Vector3(x, TerrainSprite.MaximumHillHeight + 2, z);
            float nearest = float.PositiveInfinity;
            for (int i = 0; i < faces.Length; i += 3)
                nearest = Math.Min(nearest, RayTriangleDistance(origin, Vector3.Down, faces[i], faces[i + 1], faces[i + 2]));
            if (!float.IsPositiveInfinity(nearest)) return origin.Y - nearest;
        }
        return TerrainSprite.Elevation(map, cx, cy);
    }

    public Pos GroundCell(Vector2 pointer)
    {
        var hit = GroundHit(pointer);
        var cell = new Pos((int)Math.Floor(hit.Point.X), (int)Math.Floor(hit.Point.Z));
        if (hit.Bridge is { } path && _map is { } map)
        {
            float nearest = float.PositiveInfinity;
            for (int index = 0; index < path.Count; index++)
            {
                var candidate = new Pos((int)Math.Floor(path.Xs[index] / RoadCurves.CellWidth),
                    (int)Math.Floor(path.Ys[index] / RoadCurves.CellHeight));
                if (!map.HasRoad(candidate.X, candidate.Y)) continue;
                float distance = new Vector2(hit.Point.X - candidate.X - 0.5f,
                    hit.Point.Z - candidate.Y - 0.5f).LengthSquared();
                if (distance < nearest) { nearest = distance; cell = candidate; }
            }
        }
        return cell;
    }

    public Vector2 ScreenPoint(Vector3 world)
    {
        var pixel = _camera.UnprojectPosition(world);
        return new(pixel.X * DisplaySize.X / _viewport.Size.X, pixel.Y * DisplaySize.Y / _viewport.Size.Y);
    }

    public bool PickBuilding(Vector2 pointer, out Pos anchor)
    {
        anchor = default;
        var pixel = ViewportPoint(pointer);
        var origin = _camera.ProjectRayOrigin(pixel);
        var direction = _camera.ProjectRayNormal(pixel);
        float nearest = float.PositiveInfinity;
        foreach (var box in _boxes)
        {
            float near = 0, far = float.PositiveInfinity;
            bool hit = true;
            for (int axis = 0; axis < 3; axis++)
            {
                if (Math.Abs(direction[axis]) < 0.00001f)
                {
                    if (origin[axis] < box.Bounds.Position[axis] || origin[axis] > box.Bounds.End[axis]) hit = false;
                    continue;
                }
                float a = (box.Bounds.Position[axis] - origin[axis]) / direction[axis];
                float b = (box.Bounds.End[axis] - origin[axis]) / direction[axis];
                near = Math.Max(near, Math.Min(a, b)); far = Math.Min(far, Math.Max(a, b));
            }
            if (hit && near <= far && near < nearest) { nearest = near; anchor = box.Anchor; }
        }
        foreach (var face in _roofFaces)
        {
            var edge1 = face.B - face.A; var edge2 = face.C - face.A;
            var cross = direction.Cross(edge2);
            float determinant = edge1.Dot(cross);
            if (Math.Abs(determinant) < 0.00001f) continue;
            var offset = origin - face.A;
            float u = offset.Dot(cross) / determinant;
            var q = offset.Cross(edge1);
            float v = direction.Dot(q) / determinant;
            float distance = edge2.Dot(q) / determinant;
            if (u >= 0 && v >= 0 && u + v <= 1 && distance >= 0 && distance < nearest)
            { nearest = distance; anchor = face.Anchor; }
        }
        return float.IsFinite(nearest);
    }

    private StandardMaterial3D Material(int rgb)
    {
        if (!_materials.TryGetValue(rgb, out var material))
        {
            material = new StandardMaterial3D
            {
                AlbedoColor = new Color(
                ((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f),
                Roughness = 1,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            };
            if (rgb is 0x58d068 or 0x58a6ff or 0xf2c94c)
                material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            else if (HouseAppearance.IsTexturedColor(rgb))
            {
                using var image = Image.CreateFromData(HouseAppearance.TextureSize, HouseAppearance.TextureSize,
                    false, Image.Format.Rgba8, HouseAppearance.MaterialTexture(rgb));
                material.AlbedoTexture = ImageTexture.CreateFromImage(image);
                material.AlbedoColor = Colors.White;
                material.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;
            }
            else if (rgb is 0xa4a599 or 0xc6bf9c or 0x79634b or 0x956b50 or 0xa69666 or 0x586b71)
            {
                const int size = 16;
                var pixels = new byte[size * size * 4];
                var color = Rgb.Hex(rgb);
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        double shade = 1;
                        if (rgb == 0xa4a599 && (y % 4 == 0 || (x + y / 4 * 3) % 8 == 0))
                            shade = 0.7;
                        else if (rgb is 0x956b50 or 0x586b71 && y % 4 == 0) shade = 0.72;
                        else if (rgb == 0xa69666 && x % 3 == 0) shade = 0.85;
                        else if (rgb == 0x79634b && x % 4 == 0) shade = 0.75;
                        else if (CellHash.Pick(x, y, 9) == 0) shade = 1.1;
                        var pixel = color.Scale(shade);
                        int i = (y * size + x) * 4;
                        pixels[i] = pixel.R; pixels[i + 1] = pixel.G; pixels[i + 2] = pixel.B; pixels[i + 3] = 255;
                    }
                using var image = Image.CreateFromData(size, size, false, Image.Format.Rgba8, pixels);
                material.AlbedoTexture = ImageTexture.CreateFromImage(image);
                material.AlbedoColor = Colors.White;
                material.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;
            }
            else if (TreeAppearance.IsFoliageColor(rgb))
            {
                using var image = Image.CreateFromData(16, 16, false, Image.Format.Rgba8, TerrainSprite.FoliageTexture(rgb));
                material.AlbedoTexture = ImageTexture.CreateFromImage(image);
                material.AlbedoColor = Colors.White;
                material.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;
            }
            _materials.Add(rgb, material);
        }
        return material;
    }

    private void Box(Vector3 center, Vector3 size, int rgb, Pos? anchor = null)
    {
        if (_drawingPreview) rgb = PreviewColor(rgb);
        BatchBox(rgb, new Transform3D(Basis.Identity.Scaled(size), center));
        if (!_drawingPreview && anchor is { } owner)
        {
            var rotatedCenter = _buildingTransform * center;
            var rotatedSize = _buildingTransform.Basis.X.Abs() * size.X +
                _buildingTransform.Basis.Y.Abs() * size.Y + _buildingTransform.Basis.Z.Abs() * size.Z;
            _boxes.Add((new Aabb(rotatedCenter - rotatedSize / 2, rotatedSize), owner, rgb));
        }
    }

    private void BatchBox(int rgb, Transform3D transform)
    {
        if (!_boxBatches.TryGetValue(rgb, out var batch))
        {
            batch = [];
            _boxBatches.Add(rgb, batch);
        }
        batch.Add(_buildingTransform * transform);
    }

    private void Roof(Vector3 center, float width, float depth, float rise, int rgb, Pos anchor, int? gableColor = null)
    {
        var a = center + new Vector3(-width / 2, 0, -depth / 2);
        var b = center + new Vector3(width / 2, 0, -depth / 2);
        var c = center + new Vector3(width / 2, 0, depth / 2);
        var d = center + new Vector3(-width / 2, 0, depth / 2);
        var e = center + new Vector3(-width / 2, rise, 0);
        var f = center + new Vector3(width / 2, rise, 0);
        void Face(Vector3 p, Vector3 q, Vector3 r, bool gable = false)
        {
            int faceColor = gable ? gableColor ?? rgb : rgb;
            int color = _drawingPreview ? PreviewColor(faceColor) : faceColor;
            if (!_roofBatches.TryGetValue(color, out var surface))
            {
                surface = new SurfaceTool();
                surface.Begin(Mesh.PrimitiveType.Triangles);
                _roofBatches.Add(color, surface);
            }
            surface.SetNormal(_buildingTransform.Basis * (q - p).Cross(r - p).Normalized());
            void Vertex(Vector3 vertex)
            {
                surface.SetUV(gable && gableColor.HasValue
                    ? new((vertex.Z - center.Z) / depth + 0.5f, 1 - (vertex.Y - center.Y) / rise)
                    : new((vertex.X - center.X) / width + 0.5f, (vertex.Z - center.Z) / depth + 0.5f));
                surface.AddVertex(_buildingTransform * vertex);
            }
            Vertex(p); Vertex(q); Vertex(r);
            if (!_drawingPreview) _roofFaces.Add((_buildingTransform * p, _buildingTransform * q, _buildingTransform * r, anchor));
        }
        Face(a, e, f); Face(a, f, b); Face(d, c, f); Face(d, f, e);
        Face(a, d, e, true); Face(b, f, c, true);
    }

    private void FlushBatches(Node3D root)
    {
        foreach (var (visual, surface) in _zoneBatches)
        {
            using var mesh = surface.Commit();
            root.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = ZoneMaterial(visual) });
            surface.Dispose();
        }
        _zoneBatches.Clear();
        foreach (var (color, surface) in _footpathBatches)
        {
            using var mesh = surface.Commit();
            root.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = Material(color) });
            surface.Dispose();
        }
        _footpathBatches.Clear();
        foreach (var (color, transforms) in _boxBatches)
        {
            using var instances = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = _cube,
                InstanceCount = transforms.Count
            };
            for (int i = 0; i < transforms.Count; i++) instances.SetInstanceTransform(i, transforms[i]);
            root.AddChild(new MultiMeshInstance3D { Multimesh = instances, MaterialOverride = Material(color) });
        }
        _boxBatches.Clear();
        foreach (var (color, surface) in _roofBatches)
        {
            using var mesh = surface.Commit();
            root.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = Material(color) });
            surface.Dispose();
        }
        _roofBatches.Clear();
    }

    private static int PreviewColor(int rgb)
    {
        var color = Rgb.Blend(Rgb.Hex(rgb), Rgb.Hex(0x64c788), 0.6);
        return color.R << 16 | color.G << 8 | color.B;
    }

    private void Rebuild(GameSession session)
    {
        GeometryBuilds++;
        ExposedCutTriangles = 0;
        _exposedCutCenters.Clear();
        _wallSamples.Clear();
        _cliffEdges.Clear();
        foreach (var child in _world.GetChildren()) { _world.RemoveChild(child); child.QueueFree(); }
        _boxes.Clear(); _roofFaces.Clear(); _bridgeRails.Clear(); BuildingsDrawn = 0; ZonePlotsDrawn = 0;
        TreeShapesDrawn.Clear();
        var map = session.Game.Map;
        _roads.Sync(session.Game);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        DrawGround(session);
        GroundBuildMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        DrawBridges(map);
        foreach (var p in _view.Cells())
        {
            float level = RenderedGroundHeight(map, p.X + 0.5f, p.Y + 0.5f);
            var zone = map.ZoneAt(p.X, p.Y);
            if (zone != ZoneType.None && map.BuildingAt(p.X, p.Y) is null)
                ZonePlot(session, p);
            if (map.FeatureAt(p.X, p.Y) is { } feature && TerrainSprite.NaturalFeatureVisible(session.Game, p.X, p.Y))
            {
                var at = new Vector3(p.X + 0.5f, level, p.Y + 0.5f);
                if (feature.Name == "Tree") Tree(at);
                else Rock(at, CellHash.Pick(p.X, p.Y, 6),
                    0.8f + CellHash.Pick(p.X, p.Y, 4) * 0.1f, CellHash.Pick(p.X, p.Y, 4));
            }
        }
        DrawFootpaths(map);
        foreach (var pair in map.BuildingFootprints)
            if (pair.Value.Right >= _view.X && pair.Value.X <= _view.Right &&
                pair.Value.Bottom >= _view.Y && pair.Value.Y <= _view.Bottom)
                Building(map, pair.Value, map.BuildingAt(pair.Value.X, pair.Value.Y)!);
        foreach (var p in _view.Cells())
            if (map.BuildingAt(p.X, p.Y) is { } type && map.IsBuildingAnchor(map.Index(p.X, p.Y)) &&
                !map.BuildingFootprints.ContainsKey(map.Index(p.X, p.Y))) Building(map, new(p.X, p.Y, 1, 1), type);
        FlushBatches(_world);
        _builtCount = BuildingsDrawn;
        ObjectBuildMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private void RebuildIndicators(GameSession session)
    {
        foreach (var child in _indicators.GetChildren()) { _indicators.RemoveChild(child); child.QueueFree(); }
        var map = session.Game.Map;
        BuildingsDrawn = _builtCount;
        if (_selection is { } selected)
            Outline(map, selected, 0xe5cf79);
        if (_preview is { } preview)
        {
            if (preview.Building is { } building)
            {
                _drawingPreview = true;
                foreach (var area in session.Game.PlanBuildings(building, preview.Area, preview.Rotation))
                    Building(map, area, building, preview.Rotation);
                _drawingPreview = false;
            }
            if (preview.Template is { } template && preview.Road is { } road)
            {
                var ghost = new GameMap(8, 8, map.Content);
                foreach (var p in template.Cells(new(1, 1))) ghost.SetRoad(p.X, p.Y, road);
                var paths = RoadCurves.Extract(ghost, (_, _) => false).Select(path =>
                    new RoadPath(road, false, path.Closed,
                        path.Xs.Select(x => x + (preview.Area.X - 1) * RoadCurves.CellWidth).ToArray(),
                        path.Ys.Select(y => y + (preview.Area.Y - 1) * RoadCurves.CellHeight).ToArray())).ToArray();
                var layout = new RoadMeshLayout(map, paths);
                int color = preview.Quote.Cells > 0 ? 0x64c788 : 0xe47676;
                foreach (var segment in layout.Segments)
                {
                    var a = new Vector3(segment.A.X, segment.HeightA + 0.06f, segment.A.Y);
                    var b = new Vector3(segment.B.X, segment.HeightB + 0.06f, segment.B.Y);
                    var delta = b - a;
                    BatchBox(color, new Transform3D(Basis.LookingAt(delta, Vector3.Up) *
                        Basis.Identity.Scaled(new(0.6f, 0.025f, delta.Length() + 0.02f)), (a + b) / 2));
                }
            }
            if (preview.RoadStroke is { } stroke)
                foreach (var p in stroke)
                    Outline(map, CellRect.Single(p), preview.ValidCells?.Contains(p) == true || map.HasRoad(p.X, p.Y)
                        ? 0x64c788 : 0xe47676);
            else Outline(map, preview.Area, preview.Quote.Cells > 0 ? 0x64c788 : 0xe47676);
        }
        FlushBatches(_indicators);
    }

    private StandardMaterial3D ZoneMaterial(CellVisual visual)
    {
        if (_zoneMaterials.TryGetValue(visual, out var material)) return material;
        using var image = Image.CreateFromData(ZoneOverlayArtwork.TextureSize, ZoneOverlayArtwork.TextureSize, false,
            Image.Format.Rgba8, ZoneOverlayArtwork.Pixels(visual));
        material = new StandardMaterial3D
        {
            AlbedoTexture = ImageTexture.CreateFromImage(image),
            AlbedoColor = new Color(1, 1, 1, ZoneOverlayOpacity),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            TextureRepeat = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _zoneMaterials.Add(visual, material);
        return material;
    }

    private void ZonePlot(GameSession session, Pos cell)
    {
        var map = session.Game.Map;
        var visual = CellRenderer.RenderZone(session.Game, cell.X, cell.Y, session.Overlay);
        if (!_zoneBatches.TryGetValue(visual, out var surface))
        {
            surface = new SurfaceTool();
            surface.Begin(Mesh.PrimitiveType.Triangles);
            _zoneBatches.Add(visual, surface);
        }
        void Vertex(Vector2 local)
        {
            float worldX = cell.X + local.X, worldZ = cell.Y + local.Y;
            surface.SetUV(new(worldX * ZoneOverlayArtwork.Across, worldZ * ZoneOverlayArtwork.Down));
            surface.SetNormal(Vector3.Up);
            surface.AddVertex(new(worldX, RenderedGroundHeight(map, worldX, worldZ) + 0.018f, worldZ));
        }
        for (int row = 0; row < 2; row++)
            for (int column = 0; column < 2; column++)
            {
                var topLeft = new Vector2(column / 2f, row / 2f);
                var topRight = topLeft + new Vector2(0.5f, 0);
                var bottomLeft = topLeft + new Vector2(0, 0.5f);
                var bottomRight = topLeft + new Vector2(0.5f, 0.5f);
                Vertex(topLeft); Vertex(bottomLeft); Vertex(topRight);
                Vertex(topRight); Vertex(bottomLeft); Vertex(bottomRight);
            }
        ZonePlotsDrawn++;
    }

    private void DrawBridges(GameMap map)
    {
        BridgeSegmentsDrawn = 0;
        if (_roadLayout is null) return;
        float walked = 0;
        for (int index = 0; index < _roadLayout.Segments.Count; index++)
        {
            var segment = _roadLayout.Segments[index];
            if (!segment.Bridge) { walked = 0; continue; }
            bool first = index == 0 || !_roadLayout.Segments[index - 1].Bridge ||
                !ReferenceEquals(_roadLayout.Segments[index - 1].Path, segment.Path) ||
                _roadLayout.Segments[index - 1].B != segment.A;
            bool last = index == _roadLayout.Segments.Count - 1 || !_roadLayout.Segments[index + 1].Bridge ||
                !ReferenceEquals(_roadLayout.Segments[index + 1].Path, segment.Path) ||
                _roadLayout.Segments[index + 1].A != segment.B;
            if (first) walked = 0;
            float startDistance = walked;
            float length = System.Numerics.Vector2.Distance(segment.A, segment.B);
            walked += length;
            if (Math.Max(segment.A.X, segment.B.X) < _view.X ||
                Math.Min(segment.A.X, segment.B.X) > _view.Right ||
                Math.Max(segment.A.Y, segment.B.Y) < _view.Y || Math.Min(segment.A.Y, segment.B.Y) > _view.Bottom)
                continue;
            BridgeSegmentsDrawn++;
            int material = segment.Type.Rank >= 2 ? 0x89877b : 0x806d4d;
            void Rail(System.Numerics.Vector2 start, System.Numerics.Vector2 end)
            {
                var a = new Vector3(start.X, _roadLayout.HeightAt(start) + 0.18f, start.Y);
                var b = new Vector3(end.X, _roadLayout.HeightAt(end) + 0.18f, end.Y);
                var delta = b - a;
                var transform = new Transform3D(Basis.LookingAt(delta, Vector3.Up) *
                    Basis.Identity.Scaled(new(0.13f, 0.18f, delta.Length() + 0.035f)), (a + b) / 2);
                BatchBox(material, transform);
                _bridgeRails.Add((transform, segment.Path));
            }
            Rail(segment.LeftA, segment.LeftB);
            Rail(segment.RightA, segment.RightB);
            void Post(float t)
            {
                foreach (var point in new[] { System.Numerics.Vector2.Lerp(segment.LeftA, segment.LeftB, t),
                    System.Numerics.Vector2.Lerp(segment.RightA, segment.RightB, t) })
                {
                    var transform = new Transform3D(Basis.Identity.Scaled(new(0.15f, 0.3f, 0.15f)),
                        new(point.X, _roadLayout.HeightAt(point) + 0.14f, point.Y));
                    BatchBox(material, transform);
                    _bridgeRails.Add((transform, segment.Path));
                }
            }
            for (float distance = MathF.Ceiling(startDistance / 2) * 2; distance < walked - 0.0001f; distance += 2)
                Post((distance - startDistance) / length);
            if (last) Post(1);
            var midpoint = (segment.A + segment.B) / 2;
            int x = Math.Clamp((int)Math.Floor(midpoint.X), 0, map.Width - 1);
            int y = Math.Clamp((int)Math.Floor(midpoint.Y), 0, map.Height - 1);
            float bottom = (map.TerrainAt(x, y).Buildable ? TerrainSprite.Elevation(map, x, y) :
                TerrainSprite.WaterCornerLevel(map, x, y)) - 0.04f;
            float top = _roadLayout.HeightAt(midpoint) - 0.025f;
            if ((x + y) % 2 == 0 && top - bottom > 0.2f)
                Box(new(midpoint.X, (top + bottom) / 2, midpoint.Y), new(0.22f, top - bottom, 0.22f), material);
        }
    }

    private void Tree(Vector3 at, Pos? anchor = null)
    {
        var appearance = TreeAppearance.At((int)(at.X * 5), (int)(at.Z * 5));
        TreeShapesDrawn.Add(appearance.Shape);
        foreach (var part in appearance.Parts())
            Box(at + new Vector3(part.Center.X, part.Center.Y, part.Center.Z),
                new(part.Size.X, part.Size.Y, part.Size.Z), part.Color, anchor);
    }

    private void Rock(Vector3 at, int variant, float scale, int turn)
    {
        var stone = TerrainSprite.RockColor(variant);
        var rotation = new Basis(Vector3.Up, turn * Mathf.Pi / 2);
        void Piece(float x, float y, float z, float width, float height, float depth, double shade = 1)
        {
            var color = stone.Scale(shade);
            BatchBox(color.R << 16 | color.G << 8 | color.B, new Transform3D(
                rotation * Basis.Identity.Scaled(new Vector3(width, height, depth) * scale),
                at + rotation * (new Vector3(x, y + height / 2, z) * scale)));
        }
        switch (variant)
        {
            case 0:
                Piece(0, 0, 0, 0.48f, 0.27f, 0.44f);
                Piece(-0.07f, 0.27f, -0.04f, 0.3f, 0.18f, 0.29f, 1.15);
                Piece(0.23f, 0, 0.1f, 0.18f, 0.12f, 0.2f, 0.8);
                break;
            case 1:
                Piece(0, 0, 0, 0.72f, 0.12f, 0.48f);
                Piece(-0.08f, 0.12f, -0.04f, 0.5f, 0.07f, 0.34f, 1.2);
                break;
            case 2:
                Piece(0, 0, 0, 0.32f, 0.47f, 0.32f);
                Piece(0.02f, 0.47f, -0.02f, 0.22f, 0.2f, 0.23f, 1.15);
                break;
            case 3:
                Piece(-0.2f, 0, 0.12f, 0.3f, 0.22f, 0.3f, 0.85);
                Piece(0.2f, 0, -0.08f, 0.33f, 0.4f, 0.32f);
                Piece(0.18f, 0.4f, -0.1f, 0.23f, 0.08f, 0.22f, 1.2);
                break;
            case 4:
                Piece(-0.25f, 0, 0.2f, 0.2f, 0.1f, 0.22f);
                Piece(0.08f, 0, 0.05f, 0.24f, 0.18f, 0.25f, 1.15);
                Piece(0.27f, 0, -0.2f, 0.18f, 0.12f, 0.21f, 0.85);
                Piece(-0.18f, 0, -0.23f, 0.19f, 0.15f, 0.18f);
                break;
            case 5:
                Piece(0, 0, 0, 0.72f, 0.15f, 0.32f, 0.85);
                Piece(-0.05f, 0.15f, 0, 0.53f, 0.15f, 0.27f);
                Piece(-0.12f, 0.3f, 0, 0.31f, 0.1f, 0.22f, 1.2);
                break;
        }
    }

    private void DrawGround(GameSession session)
    {
        TerrainPixelsMilliseconds = RoadPixelsMilliseconds = 0;
        RoadMeshChunksDrawn = 0;
        _visibleRiverFlow = false;
        var map = session.Game.Map;
        if (!ReferenceEquals(_groundMap, map) || _groundVersion != map.TerrainVersion || _groundRoadRevision != _roads.Revision ||
            _groundOverlay != session.Overlay || session.Overlay != MapOverlay.Off && _groundOverlayVersion != session.Game.MapVersion)
        {
            foreach (var material in _groundMaterials.Values)
            {
                material.Dispose();
            }
            _groundMaterials.Clear();
            foreach (var mesh in _groundMeshes.Values) mesh.Dispose();
            _groundMeshes.Clear();
            _groundFaces.Clear();
            foreach (var chunk in _roadChunks.Values) chunk.Roads?.Dispose();
            _roadChunks.Clear();
            foreach (var mesh in _cliffMeshes.Values) mesh.Dispose();
            _cliffMeshes.Clear();
            _groundMap = map; _groundVersion = map.TerrainVersion;
            _groundRoadRevision = _roads.Revision;
            _roadLayout = new RoadMeshLayout(map, _roads.Paths);
            _roadSurfaces = new RoadSurfaceMeshes(_roadLayout);
            _groundOverlay = session.Overlay; _groundOverlayVersion = session.Game.MapVersion;
        }
        const int span = TerrainSprite.ChunkCells;
        if (_groundMaterials.Count > 384)
        {
            var unused = new List<(int X, int Y)>();
            foreach (var key in _groundMaterials.Keys)
                if (key.X < _view.X / span || key.X > _view.Right / span ||
                    key.Y < _view.Y / span || key.Y > _view.Bottom / span) unused.Add(key);
            foreach (var key in unused)
            {
                var material = _groundMaterials[key];
                material.Dispose();
                _groundMaterials.Remove(key);
                _groundMeshes[key].Dispose();
                _groundMeshes.Remove(key);
                if (_cliffMeshes.Remove(key, out var cliffMesh)) cliffMesh.Dispose();
                if (_roadChunks.Remove(key, out var roadChunk)) roadChunk.Roads?.Dispose();
                for (int y = key.Y * span; y < Math.Min(map.Height, (key.Y + 1) * span); y++)
                    for (int x = key.X * span; x < Math.Min(map.Width, (key.X + 1) * span); x++)
                    {
                        _groundFaces.Remove(map.Index(x, y));
                    }
            }
        }
        for (int cy = _view.Y / span; cy <= _view.Bottom / span; cy++)
            for (int cx = _view.X / span; cx <= _view.Right / span; cx++)
            {
                var key = (cx, cy);
                if (!_groundMaterials.TryGetValue(key, out var material))
                {
                    long sampled = System.Diagnostics.Stopwatch.GetTimestamp();
                    var sprite = TerrainSprite.Chunk(map, cx, cy, 1, 0, hillEdges: false, shorelineBlend: false);
                    TerrainPixelsMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(sampled).TotalMilliseconds;
                    using var image = Image.CreateFromData(sprite.Width, sprite.Height, false, Image.Format.Rgba8, sprite.Pixels);
                    var flowPixels = new byte[span * span * 4];
                    bool hasRiverFlow = false;
                    for (int row = 0; row < span; row++)
                        for (int column = 0; column < span; column++)
                        {
                            int x = cx * span + column, y = cy * span + row;
                            int pixel = (row * span + column) * 4;
                            flowPixels[pixel] = flowPixels[pixel + 1] = 128;
                            flowPixels[pixel + 3] = 255;
                            if (!map.InBounds(x, y) || map.TerrainAt(x, y).Name != TermCity.Core.Terrain.DefaultTerrains.WaterName) continue;
                            var flow = TerrainSprite.Flow(map).At(x, y);
                            var track = RiverRippleLayout.At(map, TerrainSprite.Flow(map), x, y);
                            flowPixels[pixel] = (byte)(((flow.Direction.X + 1) * 3 + track.Incoming.X + 1) * 255 / 8);
                            flowPixels[pixel + 1] = (byte)(((flow.Direction.Y + 1) * 3 + track.Incoming.Y + 1) * 255 / 8);
                            flowPixels[pixel + 2] = flow.River ? (byte)255 : (byte)0;
                            flowPixels[pixel + 3] = (byte)((flow.Distance % 8) * 255 / 8);
                            hasRiverFlow |= flow.River;
                        }
                    using var flowImage = Image.CreateFromData(span, span, false, Image.Format.Rgba8, flowPixels);
                    const int shoreSize = span + 2;
                    var shorePixels = new float[shoreSize * shoreSize * 4];
                    for (int row = 0; row < shoreSize; row++)
                        for (int column = 0; column < shoreSize; column++)
                        {
                            float x = cx * span + column - 0.5f, y = cy * span + row - 0.5f;
                            int pixel = (row * shoreSize + column) * 4;
                            shorePixels[pixel] = ShorelineLayout.BankCell(map, cx * span + column - 1, cy * span + row - 1) ? 1 : 0;
                            shorePixels[pixel + 1] = ShorelineLayout.CoastalCellDistance(map, cx * span + column - 1, cy * span + row - 1);
                            shorePixels[pixel + 2] = ShorelineLayout.BeachStrength(x, y);
                            shorePixels[pixel + 3] = 1;
                        }
                    using var shoreImage = Image.CreateFromData(shoreSize, shoreSize, false, Image.Format.Rgbaf,
                        System.Runtime.InteropServices.MemoryMarshal.AsBytes(shorePixels.AsSpan()).ToArray());
                    var shaderMaterial = new ShaderMaterial { Shader = _terrainShader };
                    material = new GroundMaterial(ImageTexture.CreateFromImage(image), ImageTexture.CreateFromImage(flowImage),
                        ImageTexture.CreateFromImage(shoreImage), shaderMaterial, hasRiverFlow);
                    shaderMaterial.SetShaderParameter("terrain_image", material.Texture);
                    shaderMaterial.SetShaderParameter("flow_image", material.Flow);
                    shaderMaterial.SetShaderParameter("shore_image", material.Shore);
                    shaderMaterial.SetShaderParameter("flow_clock", (float)WaterFlowRenderClock);
                    _groundMaterials.Add(key, material);
                }
                _visibleRiverFlow |= material.RiverFlow;
                if (!_roadChunks.TryGetValue(key, out var chunk))
                {
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    chunk = _roadSurfaces!.Build(session, cx, cy);
                    RoadPixelsMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    _roadChunks.Add(key, chunk);
                    _groundMeshes.Add(key, chunk.Ground);
                    if (chunk.Banks is not null) _cliffMeshes.Add(key, chunk.Banks);
                    foreach (var (index, faces) in chunk.Faces) _groundFaces[index] = faces;
                }
                _world.AddChild(new MeshInstance3D { Mesh = chunk.Ground, MaterialOverride = material.Material });
                if (chunk.Roads is not null)
                {
                    _world.AddChild(new MeshInstance3D { Mesh = chunk.Roads, MaterialOverride = _roadMaterial });
                    RoadMeshChunksDrawn++;
                }
                if (chunk.Banks is not null)
                    _world.AddChild(new MeshInstance3D { Mesh = chunk.Banks, MaterialOverride = _cliffMaterial });
                _cliffEdges.AddRange(chunk.CutEdges);
                _exposedCutCenters.AddRange(chunk.CutCenters);
                _wallSamples.AddRange(chunk.WallSamples);
                ExposedCutTriangles += chunk.CutCenters.Count * 2;
            }
    }

    private void Outline(GameMap map, CellRect area, int color)
    {
        float x = area.X, z = area.Y, w = area.Width, d = area.Height;
        SelectionOutlineHeight = float.NegativeInfinity;
        Vector3 Point(float px, float pz)
        {
            float height = RenderedGroundHeight(map, Math.Clamp(px, x + 0.001f, x + w - 0.001f),
                Math.Clamp(pz, z + 0.001f, z + d - 0.001f)) + _indicatorThickness + 0.025f;
            SelectionOutlineHeight = Math.Max(SelectionOutlineHeight, height);
            return new(px, height, pz);
        }
        void Edge(float ax, float az, float bx, float bz, int segments)
        {
            var previous = Point(ax, az);
            for (int step = 1; step <= segments; step++)
            {
                float t = step / (float)segments;
                var next = Point(Mathf.Lerp(ax, bx, t), Mathf.Lerp(az, bz, t));
                var delta = next - previous;
                BatchBox(color, new Transform3D(Basis.LookingAt(delta, Vector3.Up) *
                    Basis.Identity.Scaled(new(_indicatorThickness, _indicatorThickness, delta.Length() + _indicatorThickness)),
                    (previous + next) / 2));
                previous = next;
            }
        }
        Edge(x, z, x + w, z, area.Width * 4);
        Edge(x, z + d, x + w, z + d, area.Width * 4);
        Edge(x, z, x, z + d, area.Height * 4);
        Edge(x + w, z, x + w, z + d, area.Height * 4);
    }

    private void Facades(Vector3 center, float width, float depth, float height, int stories,
        bool framed, bool attachedTower, Pos anchor)
    {
        float storyHeight = height / stories;
        void Face(bool alongX, int side)
        {
            var bays = BuildingDetailLayout.Facade(alongX ? width : depth);
            Vector3 Position(float along, float y, float offset) => center + (alongX
                ? new Vector3(along, y, side * (depth / 2 + offset))
                : new Vector3(side * (width / 2 + offset), y, along));
            for (int story = 0; story < stories; story++)
                for (int bay = 0; bay < bays.Count; bay++)
                {
                    if (alongX && side == 1 && story == 0 && bay == bays.Count / 2) continue;
                    if (attachedTower && side == -1 && bay == 0) continue;
                    float windowHeight = Math.Min(0.2f, storyHeight * 0.4f);
                    Box(Position(bays.Center(bay), story * storyHeight + storyHeight * 0.68f, 0.012f),
                        alongX ? new(BuildingDetailLayout.WindowWidth, windowHeight, 0.03f) :
                            new(0.03f, windowHeight, BuildingDetailLayout.WindowWidth), 0x304b53, anchor);
                }
            if (!framed) return;
            for (int post = 0; post <= bays.Count; post++)
                Box(Position(bays.Post(post), height / 2, 0.018f),
                    new(BuildingDetailLayout.BeamWidth, height, BuildingDetailLayout.BeamWidth), 0x79634b, anchor);
            for (int floor = 0; floor < stories; floor++)
                Box(Position(0, floor == 0 ? 0.035f : floor * storyHeight, 0.02f),
                    alongX ? new(width, 0.045f, 0.045f) : new(0.045f, 0.045f, depth), 0x79634b, anchor);
        }
        Face(true, -1); Face(true, 1); Face(false, -1); Face(false, 1);
        Box(center + new Vector3(0, 0.18f, depth / 2 + 0.016f), new(0.2f, 0.36f, 0.04f), 0x584738, anchor);
    }

    private void Building(GameMap map, CellRect area, BuildingType type, int? previewRotation = null)
    {
        BuildingsDrawn++;
        var anchor = new Pos(area.X, area.Y);
        float ground = area.Cells().Max(p => TerrainSprite.Elevation(map, p.X, p.Y));
        var center = new Vector3(area.X + area.Width / 2f, ground, area.Y + area.Height / 2f);
        int rotation = previewRotation ?? map.BuildingRotations.GetValueOrDefault(map.Index(area.X, area.Y));
        var modelArea = rotation % 2 == 0 ? area : new CellRect(area.X, area.Y, area.Height, area.Width);
        if (type.Zone == ZoneType.Residential)
            HouseFoundation(map, area, modelArea, rotation, center, type, anchor);
        else foreach (var p in area.Cells())
        {
            float level = TerrainSprite.Elevation(map, p.X, p.Y);
            if (TerrainSprite.WaterSurface(map, p.X + 0.5, p.Y + 0.5) > 0)
                foreach (var sample in new[] { new Vector2(p.X + 0.01f, p.Y + 0.01f),
                    new Vector2(p.X + 0.99f, p.Y + 0.01f), new Vector2(p.X + 0.01f, p.Y + 0.99f),
                    new Vector2(p.X + 0.99f, p.Y + 0.99f), new Vector2(p.X + 0.5f, p.Y + 0.5f) })
                    level = Math.Min(level, RenderedGroundHeight(map, sample.X, sample.Y));
            if (ground > level)
                Box(new(p.X + 0.5f, (ground + level) / 2, p.Y + 0.5f),
                    new(1, ground - level + 0.02f, 1), 0x79634b, anchor);
        }
        var basis = rotation switch
        {
            1 => new Basis(Vector3.Back, Vector3.Up, Vector3.Left),
            2 => new Basis(Vector3.Left, Vector3.Up, Vector3.Forward),
            3 => new Basis(Vector3.Forward, Vector3.Up, Vector3.Right),
            _ => Basis.Identity,
        };
        _buildingTransform = new(basis, center - basis * center);
        try { BuildingModel(modelArea, type, anchor, center); }
        finally { _buildingTransform = Transform3D.Identity; }
    }

    private void HouseFoundation(GameMap map, CellRect area, CellRect modelArea, int rotation,
        Vector3 center, BuildingType type, Pos anchor)
    {
        var appearance = HouseAppearance.At(anchor.X, anchor.Y, type.Level);
        float modelWidth = modelArea.Width * appearance.Width + 0.045f;
        float modelDepth = modelArea.Height * appearance.Depth + 0.045f;
        float width = rotation % 2 == 0 ? modelWidth : modelDepth;
        float depth = rotation % 2 == 0 ? modelDepth : modelWidth;
        Support(center.X - width / 2, center.X + width / 2, center.Z - depth / 2, center.Z + depth / 2, true);
        if (appearance.Wing)
        {
            float wingX = -modelArea.Width * appearance.Width * 0.36f;
            float wingZ = -modelArea.Height * appearance.Depth * 0.36f;
            var offset = rotation switch
            {
                1 => new Vector2(-wingZ, wingX), 2 => new Vector2(-wingX, -wingZ),
                3 => new Vector2(wingZ, -wingX), _ => new Vector2(wingX, wingZ),
            };
            float wingWidth = (width - 0.045f) * 0.5f, wingDepth = (depth - 0.045f) * 0.5f;
            Support(center.X + offset.X - wingWidth / 2, center.X + offset.X + wingWidth / 2,
                center.Z + offset.Y - wingDepth / 2, center.Z + offset.Y + wingDepth / 2, false);
        }
        void Support(float left, float right, float back, float front, bool openings)
        {
            foreach (var cell in area.Cells())
            {
                float segmentLeft = Math.Max(left, cell.X), segmentRight = Math.Min(right, cell.X + 1);
                float segmentBack = Math.Max(back, cell.Y), segmentFront = Math.Min(front, cell.Y + 1);
                if (segmentRight <= segmentLeft || segmentFront <= segmentBack) continue;
                float lower = TerrainSprite.Elevation(map, cell.X, cell.Y);
                foreach (float sampleX in new[] { segmentLeft + 0.005f, segmentRight - 0.005f })
                    foreach (float sampleZ in new[] { segmentBack + 0.005f, segmentFront - 0.005f })
                        lower = Math.Min(lower, RenderedGroundHeight(map, sampleX, sampleZ));
                float drop = center.Y - lower;
                if (drop < 0.015f) continue;
                Box(new((segmentLeft + segmentRight) / 2, (center.Y + lower) / 2,
                        (segmentBack + segmentFront) / 2),
                    new(segmentRight - segmentLeft, drop + 0.08f, segmentFront - segmentBack),
                    HouseAppearance.StoneColor, anchor);
                void Opening(bool side, float edge, float along)
                {
                    float exteriorX = side ? edge : along, exteriorZ = side ? along : edge;
                    float outside = side ? (edge < center.X ? -0.03f : 0.03f) : (edge < center.Z ? -0.03f : 0.03f);
                    float terrain = RenderedGroundHeight(map, exteriorX + (side ? outside : 0),
                        exteriorZ + (side ? 0 : outside));
                    if (center.Y - terrain < 0.45f) return;
                    var position = new Vector3(exteriorX, center.Y - 0.24f, exteriorZ);
                    Box(position, side ? new(0.065f, 0.27f, 0.29f) : new(0.29f, 0.27f, 0.065f),
                        HouseAppearance.TimberColor, anchor);
                    Box(position + (side ? new Vector3(outside, 0, 0) : new Vector3(0, 0, outside)),
                        side ? new(0.025f, 0.18f, 0.2f) : new(0.2f, 0.18f, 0.025f), 0x35494b, anchor);
                }
                if (openings && segmentFront - segmentBack > 0.35f)
                {
                    if (segmentLeft == left) Opening(true, left, (segmentBack + segmentFront) / 2);
                    if (segmentRight == right) Opening(true, right, (segmentBack + segmentFront) / 2);
                }
                if (openings && segmentRight - segmentLeft > 0.35f)
                {
                    if (segmentBack == back) Opening(false, back, (segmentLeft + segmentRight) / 2);
                    if (segmentFront == front) Opening(false, front, (segmentLeft + segmentRight) / 2);
                }
            }
        }
    }

    private void DrawFootpaths(GameMap map)
    {
        FootpathTrianglesDrawn = 0;
        foreach (var stroke in FootpathLayout.Strokes(map))
        {
            int color = stroke.Wear >= 3 ? 0xb19564 : 0x81735c;
            var sections = FootpathLayout.RibbonSections(stroke, _roadLayout);
            Vector2 Convert(System.Numerics.Vector2 point) => new(point.X, point.Y);
            void Emit(Vector2[] polygon)
            {
                float left = polygon.Min(point => point.X), right = polygon.Max(point => point.X);
                float back = polygon.Min(point => point.Y), front = polygon.Max(point => point.Y);
                if (right < _view.X || left > _view.Right + 1 || front < _view.Y || back > _view.Bottom + 1) return;
                var pieces = new List<Vector2[]> { polygon };
                var roads = new HashSet<RoadMeshLayout.Segment>();
                if (_roadLayout is not null)
                    for (int cellY = (int)Math.Floor(back); cellY <= (int)Math.Floor(front); cellY++)
                        for (int cellX = (int)Math.Floor(left); cellX <= (int)Math.Floor(right); cellX++)
                            foreach (var segment in _roadLayout.AtCell(cellX, cellY))
                                if (!segment.Bridge) roads.Add(segment);
                foreach (var road in roads)
                {
                    var boundary = road.Road.Select(Convert).ToArray();
                    pieces = pieces.SelectMany(piece => Geometry2D.ClipPolygons(piece, boundary)).ToList();
                    if (pieces.Count == 0) return;
                }
                foreach (var piece in pieces)
                {
                    var triangles = Geometry2D.TriangulatePolygon(piece);
                    if (triangles.Length == 0) continue;
                    if (!_footpathBatches.TryGetValue(color, out var surface))
                    {
                        surface = new SurfaceTool();
                        surface.Begin(Mesh.PrimitiveType.Triangles);
                        _footpathBatches.Add(color, surface);
                    }
                    foreach (int vertex in triangles)
                    {
                        var point = piece[vertex];
                        surface.SetNormal(Vector3.Up);
                        surface.AddVertex(new(point.X, RenderedGroundHeight(map, point.X, point.Y) + 0.014f, point.Y));
                    }
                    FootpathTrianglesDrawn += triangles.Length / 3;
                }
            }
            for (int index = 1; index < sections.Count; index++)
                Emit([Convert(sections[index - 1].Left), Convert(sections[index - 1].Right),
                    Convert(sections[index].Right), Convert(sections[index].Left)]);
            void Cap(FootpathLayout.Section section)
            {
                var center = Convert(section.Center);
                float radius = System.Numerics.Vector2.Distance(section.Left, section.Right) / 2;
                var ring = Enumerable.Range(0, 12).Select(step =>
                    center + new Vector2(Mathf.Cos(step * Mathf.Tau / 12), Mathf.Sin(step * Mathf.Tau / 12)) * radius).ToArray();
                Emit(ring);
            }
            if (sections.Count > 0)
            {
                if (!stroke.StartsAtRoad) Cap(sections[0]);
                if (!stroke.EndsAtRoad && sections.Count > 1) Cap(sections[^1]);
            }
        }
    }

    private void House(CellRect area, BuildingType type, Pos anchor, Vector3 center)
    {
        var appearance = HouseAppearance.At(anchor.X, anchor.Y, type.Level);
        float width = area.Width * appearance.Width, depth = area.Height * appearance.Depth;
        float height = appearance.Height;
        Box(center + new Vector3(0, height / 2, 0), new(width, height, depth), appearance.WallColor, anchor);
        Box(center + new Vector3(0, 0.065f, 0), new(width + 0.045f, 0.13f, depth + 0.045f),
            HouseAppearance.StoneColor, anchor);
        foreach (int sideX in new[] { -1, 1 })
            foreach (int sideZ in new[] { -1, 1 })
                Box(center + new Vector3(sideX * (width / 2 - 0.03f), height / 2, sideZ * (depth / 2 - 0.03f)),
                    new(0.085f, height, 0.085f), HouseAppearance.TimberColor, anchor);
        foreach (int side in new[] { -1, 1 })
        {
            Box(center + new Vector3(0, height - 0.04f, side * depth / 2),
                new(width + 0.045f, 0.09f, 0.075f), HouseAppearance.TimberColor, anchor);
            Box(center + new Vector3(side * width / 2, height - 0.04f, 0),
                new(0.075f, 0.09f, depth + 0.045f), HouseAppearance.TimberColor, anchor);
        }
        var transform = _buildingTransform;
        if (appearance.CrosswiseRoof)
        {
            var basis = new Basis(Vector3.Back, Vector3.Up, Vector3.Left);
            _buildingTransform *= new Transform3D(basis, center - basis * center);
        }
        try
        {
            Roof(center + new Vector3(0, height, 0),
                (appearance.CrosswiseRoof ? depth : width) + 0.1f,
                (appearance.CrosswiseRoof ? width : depth) + 0.1f,
                appearance.RoofRise, appearance.RoofColor, anchor, appearance.WallColor);
            Box(center + new Vector3(0, height + appearance.RoofRise - 0.018f, 0),
                new((appearance.CrosswiseRoof ? depth : width) + 0.12f, 0.075f, 0.09f),
                appearance.RoofCapColor, anchor);
        }
        finally { _buildingTransform = transform; }
        Box(center + new Vector3(-width * 0.18f, 0.235f, depth / 2 + 0.018f),
            new(0.29f, 0.47f, 0.065f), HouseAppearance.TimberColor, anchor);
        Box(center + new Vector3(-width * 0.18f, 0.22f, depth / 2 + 0.055f),
            new(0.2f, 0.38f, 0.04f), 0x514637, anchor);
        void Window(Vector3 position, bool side = false)
        {
            Box(position, side ? new(0.065f, 0.27f, 0.29f) : new(0.29f, 0.27f, 0.065f),
                HouseAppearance.TimberColor, anchor);
            Box(position + (side ? new Vector3(0.04f, 0, 0) : new Vector3(0, 0, 0.04f)),
                side ? new(0.025f, 0.18f, 0.2f) : new(0.2f, 0.18f, 0.025f), 0x35494b, anchor);
            Box(position + new Vector3(0, -0.12f, 0),
                side ? new(0.12f, 0.065f, 0.31f) : new(0.31f, 0.065f, 0.12f),
                HouseAppearance.SillColor, anchor);
        }
        Window(center + new Vector3(width * 0.24f, height * 0.6f, depth / 2 + 0.018f));
        Window(center + new Vector3(width / 2 + 0.018f, height * 0.6f, -depth * 0.14f), true);
        if (type.Level > 1)
            Window(center + new Vector3(-width * 0.18f, height * 0.84f, depth / 2 + 0.018f));
        if (appearance.Wing)
        {
            var wing = center + new Vector3(-width * 0.36f, 0, -depth * 0.36f);
            Box(wing + new Vector3(0, height * 0.3f, 0), new(width * 0.5f, height * 0.6f, depth * 0.5f),
                appearance.WallColor, anchor);
            Roof(wing + new Vector3(0, height * 0.6f, 0), width * 0.5f + 0.08f, depth * 0.5f + 0.08f,
                appearance.RoofRise * 0.65f, appearance.RoofColor, anchor, appearance.WallColor);
        }
        if (appearance.Chimney)
        {
            var chimney = center + new Vector3(width * 0.2f, height + appearance.RoofRise * 0.8f, -depth * 0.18f);
            Box(chimney, new(0.2f, 0.38f, 0.22f), HouseAppearance.StoneColor, anchor);
            Box(chimney + new Vector3(0, 0.19f, 0), new(0.26f, 0.085f, 0.28f), HouseAppearance.TimberColor, anchor);
        }
    }

    private void BuildingModel(CellRect area, BuildingType type, Pos anchor, Vector3 center)
    {
        float w = Math.Max(0.55f, area.Width * 0.84f), d = Math.Max(0.55f, area.Height * 0.78f);
        float h = 0.45f + type.Level * 0.2f;
        if (type.Zone == ZoneType.Commercial || type.Name == "Tavern") h = Math.Max(h, 0.85f);
        if (type.Zone == ZoneType.Residential)
        {
            House(area, type, anchor, center);
            return;
        }
        Box(center + new Vector3(0, 0.035f, 0), new(area.Width * 0.93f, 0.06f, area.Height * 0.93f), 0x8c8b74);
        if (type.IsMonument)
        {
            const int sculpture = 0xe3be48, shadow = 0xb38d32, armor = 0x647875;
            Box(center + new Vector3(0, 0.15f, 0), new(5.8f, 0.3f, 4.6f), 0xa4a599, anchor);
            Box(center + new Vector3(0, 0.43f, 0), new(5.1f, 0.26f, 3.9f), 0xc7c7b5, anchor);
            Box(center + new Vector3(0, 0.8f, 0), new(4.4f, 0.48f, 3.3f), 0xa3ac9d, anchor);
            foreach (int side in new[] { -1, 1 })
            {
                Box(center + new Vector3(side * 0.7f, 1.16f, 0.55f), new(1.15f, 0.3f, 1.45f), sculpture, anchor);
                Box(center + new Vector3(side * 0.6f, 1.42f, 0), new(0.6f, 0.54f, 0.7f), shadow, anchor);
            }
            Box(center + new Vector3(0, 2.4f, 0), new(2.6f, 1.7f, 2), sculpture, anchor);
            Box(center + new Vector3(0, 2.95f, 0), new(2.2f, 0.8f, 1.8f), sculpture, anchor);
            Box(center + new Vector3(0, 2.55f, -1.02f), new(2, 1.7f, 0.14f), armor, anchor);
            Box(center + new Vector3(0, 3.94f, 0), new(2.05f, 1.45f, 1.7f), sculpture, anchor);
            Box(center + new Vector3(0, 4.7f, 0), new(2.3f, 0.18f, 1.95f), armor, anchor);
            Box(center + new Vector3(0, 4.95f, 0), new(1.8f, 0.36f, 1.5f), armor, anchor);
            Box(center + new Vector3(0, 3.56f, 1.07f), new(1.35f, 0.24f, 0.8f), shadow, anchor);
            foreach (int side in new[] { -1, 1 })
            {
                Box(center + new Vector3(side * 0.43f, 4.15f, 0.87f), new(0.15f, 0.15f, 0.035f), 0x415350, anchor);
                Box(center + new Vector3(side * 1.42f, 3.14f, 0.1f), new(0.62f, 0.7f, 0.7f), shadow, anchor);
                Box(center + new Vector3(side * 1.45f, 3.8f, 0.14f), new(0.52f, 0.85f, 0.65f), sculpture, anchor);
                Box(center + new Vector3(side * 1.15f, 4.42f, 0.15f), new(0.68f, 0.48f, 0.7f), sculpture, anchor);
                for (int finger = 0; finger < 3; finger++)
                    Box(center + new Vector3(side * 1.03f, 4.6f, -0.1f + finger * 0.2f),
                        new(0.5f, 0.18f, 0.13f), sculpture, anchor);
            }
            return;
        }
        if (type.Name is "Woodlot" or "Village Green" or "Charcoal Burners")
        {
            for (int n = 0; n < Math.Max(2, area.Area); n++)
            {
                var p = center + new Vector3((n % area.Width - (area.Width - 1) / 2f) * 0.65f,
                    0, (n / area.Width - (area.Height - 1) / 2f) * 0.6f);
                Tree(p, anchor);
            }
            if (type.Name == "Charcoal Burners") Box(center + new Vector3(0, 0.25f, 0), new(w * 0.6f, 0.45f, d * 0.6f), 0x384446, anchor);
            return;
        }
        if (type.Name is "Town Well" or "Market Cross" or "Aqueduct")
        {
            if (type.Name == "Market Cross")
            {
                Box(center + new Vector3(0, 0.05f, 0), new(0.55f, 0.1f, 0.55f), 0xa4a599, anchor);
                Box(center + new Vector3(0, 0.13f, 0), new(0.38f, 0.08f, 0.38f), 0xc6bf9c, anchor);
                Box(center + new Vector3(0, 0.6f, 0), new(0.13f, 1.2f, 0.13f), 0xc6bf9c, anchor);
                Box(center + new Vector3(0, 0.9f, 0), new(0.55f, 0.13f, 0.13f), 0xc6bf9c, anchor);
            }
            else if (type.Name == "Aqueduct")
            {
                for (int n = 0; n < area.Width + 1; n++)
                {
                    Box(center + new Vector3(-w / 2 + n * w / area.Width, 0.4f, 0), new(0.16f, 0.8f, 0.28f), 0xa4a599, anchor);
                    Box(center + new Vector3(-w / 2 + n * w / area.Width, 0.08f, 0), new(0.28f, 0.16f, 0.4f), 0xa4a599, anchor);
                }
                Box(center + new Vector3(0, 0.85f, 0), new(w + 0.2f, 0.16f, 0.34f), 0xa4a599, anchor);
                Box(center + new Vector3(0, 0.95f, 0), new(w, 0.025f, 0.18f), 0x456d83, anchor);
            }
            else
            {
                Box(center + new Vector3(0, 0.22f, 0), new(0.5f, 0.44f, 0.5f), 0xa4a599, anchor);
                Box(center + new Vector3(0, 0.46f, 0), new(0.3f, 0.035f, 0.3f), 0x304b53, anchor);
                Box(center + new Vector3(-0.25f, 0.48f, 0), new(0.08f, 0.08f, 0.58f), 0xa4a599, anchor);
                Box(center + new Vector3(0.25f, 0.48f, 0), new(0.08f, 0.08f, 0.58f), 0xa4a599, anchor);
                Box(center + new Vector3(0, 0.48f, -0.25f), new(0.44f, 0.08f, 0.08f), 0xa4a599, anchor);
                Box(center + new Vector3(0, 0.48f, 0.25f), new(0.44f, 0.08f, 0.08f), 0xa4a599, anchor);
                Roof(center + new Vector3(0, 0.9f, 0), 0.72f, 0.65f, 0.3f, 0x956b50, anchor);
                Box(center + new Vector3(-0.27f, 0.65f, 0), new(0.06f, 0.6f, 0.06f), 0x79634b, anchor);
                Box(center + new Vector3(0.27f, 0.65f, 0), new(0.06f, 0.6f, 0.06f), 0x79634b, anchor);
            }
            return;
        }
        bool castle = type.Service == ServiceKind.Defence || type.Name == "Gaol";
        bool church = type.Service == ServiceKind.Faith || type.Name == "Monastery";
        if (type.Name == "Granary")
        {
            foreach (int sx in new[] { -1, 1 }) foreach (int sz in new[] { -1, 1 })
                Box(center + new Vector3(sx * w * 0.35f, 0.1f, sz * d * 0.35f),
                    new(0.2f, 0.2f, 0.2f), 0xa4a599, anchor);
            center.Y += 0.15f;
        }
        if (castle)
        {
            bool palisade = type.Name == "Motte and Bailey";
            int material = palisade ? 0x79634b : 0xa4a599;
            int battlement = palisade ? 0x8c7453 : 0xb2b5a7;
            const float wallThickness = 0.18f;
            float towerInset = (BuildingDetailLayout.TowerWidth - wallThickness) / 2;
            Box(center + new Vector3(0, 0.38f, -d / 2), new(w, 0.75f, 0.18f), material, anchor);
            Box(center + new Vector3(0, 0.38f, d / 2), new(w, 0.75f, 0.18f), material, anchor);
            Box(center + new Vector3(-w / 2, 0.38f, 0), new(0.18f, 0.75f, d), material, anchor);
            Box(center + new Vector3(w / 2, 0.38f, 0), new(0.18f, 0.75f, d), material, anchor);
            float cornerClearance = palisade ? 0.12f : 1.02f;
            foreach (float x in BuildingDetailLayout.Centers(Math.Max(0, w - cornerClearance), 0.16f, 0.3f))
                foreach (int side in new[] { -1, 1 })
                    Box(center + new Vector3(x, 0.83f, side * d / 2), new(0.16f, 0.15f, wallThickness), battlement, anchor);
            foreach (float z in BuildingDetailLayout.Centers(Math.Max(0, d - cornerClearance), 0.16f, 0.3f))
                foreach (int side in new[] { -1, 1 })
                    Box(center + new Vector3(side * w / 2, 0.83f, z), new(wallThickness, 0.15f, 0.16f), battlement, anchor);
            Box(center + new Vector3(0, 0.25f, d / 2 + 0.105f), new(0.28f, 0.46f, 0.025f), 0x584738, anchor);
            if (!palisade)
            {
                var merlons = BuildingDetailLayout.Centers(BuildingDetailLayout.TowerCapWidth,
                    BuildingDetailLayout.MerlonWidth, 0.25f);
                foreach (int sx in new[] { -1, 1 }) foreach (int sz in new[] { -1, 1 })
                {
                    if (type.Name == "Gaol" && sz < 0) continue;
                    var p = center + new Vector3(sx * (w / 2 - towerInset), 0, sz * (d / 2 - towerInset));
                    Box(p + new Vector3(0, 0.55f, 0), new(BuildingDetailLayout.TowerWidth, 1.1f,
                        BuildingDetailLayout.TowerWidth), material, anchor);
                    Box(p + new Vector3(0, 0.08f, 0), new(0.68f, 0.16f, 0.68f), material, anchor);
                    Box(p + new Vector3(0, 1.1f, 0), new(BuildingDetailLayout.TowerCapWidth, 0.1f,
                        BuildingDetailLayout.TowerCapWidth), 0x969d8f, anchor);
                    for (int ix = 0; ix < merlons.Length; ix++)
                        for (int iz = 0; iz < merlons.Length; iz++)
                        {
                            if (ix == 1 && iz == 1) continue;
                            Box(p + new Vector3(merlons[ix], 1.23f, merlons[iz]),
                                Vector3.One * BuildingDetailLayout.MerlonWidth, battlement, anchor);
                        }
                }
            }
            if (palisade) { w *= 0.7f; d *= 0.6f; h = 0.7f; }
            else if (type.Name == "Gaol") { center.Z -= d * 0.2f; w *= 0.65f; d *= 0.5f; h = 0.85f; }
            else { w *= 0.46f; d *= 0.5f; h = 1.05f; }
        }
        int wall = castle || church || type.Zone == ZoneType.Industrial ? 0xa4a599 : 0xc6bf9c;
        int roof = type.Name == "Cottage" ? 0xa69666 : church || type.Zone == ZoneType.Industrial ? 0x586b71 : 0x956b50;
        Box(center + new Vector3(0, h / 2, 0), new(w, h, d), wall, anchor);
        Roof(center + new Vector3(0, h, 0), w + 0.12f, d + 0.12f, 0.35f, roof, anchor);
        foreach (float x in BuildingDetailLayout.Centers(w + 0.12f, 0.22f, 0.24f))
            Box(center + new Vector3(x, h + 0.36f, 0), new(0.22f, 0.035f, 0.08f), roof, anchor);
        Facades(center, w, d, h, type.Level > 1 ? 2 : 1,
            !castle && !church && type.Zone != ZoneType.Industrial, church || type.Name == "Fire Watch", anchor);
        if (church || type.Name == "Fire Watch")
        {
            float towerHeight = type.Name == "Chapel" ? 1.3f : type.Name == "Fire Watch" ? 1.45f : 1.6f;
            var tower = center + new Vector3(-w / 2 + 0.28f, 0, -d / 2 + 0.28f);
            Box(tower + new Vector3(0, towerHeight / 2, 0), new(0.56f, towerHeight, 0.56f), wall, anchor);
            Roof(tower + new Vector3(0, towerHeight, 0), 0.64f, 0.64f, 0.45f, roof, anchor);
            foreach (int side in new[] { -1, 1 })
                Box(tower + new Vector3(0, towerHeight - 0.24f, side * 0.29f),
                    new(0.12f, 0.2f, 0.025f), 0x304b53, anchor);
        }
        if (type.Zone == ZoneType.Industrial)
            Box(center + new Vector3(w / 3, 0.85f, -d / 3), new(0.18f, 1.7f, 0.18f), 0x7b7f76, anchor);
        if (type.Zone == ZoneType.Commercial || type.Name == "Tavern")
            Box(center + new Vector3(0, 0.42f, d / 2 + 0.17f), new(w * 0.9f, 0.06f, 0.35f), 0x64808a, anchor);
        if (type.Service == ServiceKind.Health)
        {
            Box(center + new Vector3(0, 0.49f, d / 2 + 0.03f), new(0.16f, 0.04f, 0.035f), 0xb86a64, anchor);
            Box(center + new Vector3(0, 0.49f, d / 2 + 0.03f), new(0.04f, 0.16f, 0.035f), 0xb86a64, anchor);
        }
    }
}
