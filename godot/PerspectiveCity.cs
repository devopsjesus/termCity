using Godot;
using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

/// <summary>A supersampled orthographic 3D city view sharing the existing map and session.</summary>
public partial class PerspectiveCity : Node
{
    public const int SupersampleScale = 2;
    private DiagnosticRenderQuality _renderQuality;
    public DiagnosticRenderQuality RenderQuality
    {
        get => _renderQuality;
        init
        {
            _renderQuality = value;
            _viewport.Msaa3D = value == DiagnosticRenderQuality.Native4x ? Viewport.Msaa.Msaa4X : Viewport.Msaa.Msaa8X;
        }
    }
    public int RenderScale => _renderQuality == DiagnosticRenderQuality.Reference ? SupersampleScale : 1;
    public const float NormalMagnification = 1.9f;
    public const float CloseMagnification = 3.2f;
    public static float Magnification(double zoomLevel) => zoomLevel > 0 ? CloseMagnification :
        zoomLevel == 0 ? NormalMagnification :
        zoomLevel == GameSession.ThreeQuarterZoom ? NormalMagnification * .75f : 1.8f * MathF.Pow(2, (float)zoomLevel);
    public const float InitialAzimuth = 325;
    public const float ZoneOverlayOpacity = 0.58f;
    private readonly SubViewport _viewport = new()
    {
        OwnWorld3D = true,
        TransparentBg = false,
        RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        Msaa3D = Viewport.Msaa.Msaa8X
    };
    private readonly Camera3D _camera = new() { Projection = Camera3D.ProjectionType.Orthogonal, Near = 0.1f, Far = 600 };
    private readonly Node3D _world = new();
    private readonly Node3D _indicators = new();
    private readonly BoxMesh _cube = new() { Size = Vector3.One };
    private readonly Dictionary<int, List<Transform3D>> _sceneBoxBatches = [];
    private Dictionary<int, List<Transform3D>> _boxBatches => _objectCapture?.Instances ?? _sceneBoxBatches;
    private readonly Dictionary<int, List<SurfaceVertex>> _sceneRoofBatches = [];
    private Dictionary<int, List<SurfaceVertex>> _roofBatches => _objectCapture?.Roofs ?? _sceneRoofBatches;
    private readonly Dictionary<(int Color, bool Solid), StandardMaterial3D> _materials = [];
    private readonly StandardMaterial3D _instanceColorMaterial = new()
    {
        AlbedoColor = Colors.White, VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true,
        Roughness = 1, CullMode = BaseMaterial3D.CullModeEnum.Back,
    };
    private readonly StandardMaterial3D _instanceUnshadedMaterial = new()
    {
        AlbedoColor = Colors.White, VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true,
        Roughness = 1, CullMode = BaseMaterial3D.CullModeEnum.Back,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
    };
    private readonly Dictionary<CellVisual, StandardMaterial3D> _zoneMaterials = [];
    private readonly Dictionary<CellVisual, List<SurfaceVertex>> _sceneZoneBatches = [];
    private Dictionary<CellVisual, List<SurfaceVertex>> _zoneBatches => _objectCapture?.Zones ?? _sceneZoneBatches;
    private readonly Dictionary<int, List<SurfaceVertex>> _sceneDirtBatches = [];
    private Dictionary<int, List<SurfaceVertex>> _footpathBatches => _objectCapture?.Dirt ?? _sceneDirtBatches;
    public int FootpathTrianglesDrawn { get; private set; }
    internal float MaximumFootpathTriangleRise { get; private set; }
    internal bool MeasureFootpathFaces { get; set; }
    internal readonly List<(Vector3 A, Vector3 B, Vector3 C)> FootpathFaces = [];
    private sealed record GroundMaterial(ImageTexture Texture, ImageTexture Variation, ImageTexture Flow, ImageTexture Shore,
        ShaderMaterial Material, bool RiverFlow) : IDisposable
    {
        public void Dispose() { Texture.Dispose(); Variation.Dispose(); Flow.Dispose(); Shore.Dispose(); Material.Dispose(); }
    }
    private readonly Dictionary<(int X, int Y), GroundMaterial> _groundMaterials = [];
    private readonly HashSet<(int X, int Y)> _mountedGroundChunks = [];
    private readonly Shader _terrainShader = new();
    private ImageTexture? _terrainAtlas;
    private const string TerrainShaderBody = """
            uniform sampler2D terrain_atlas : filter_nearest, repeat_disable;
            uniform sampler2D terrain_data : filter_nearest, repeat_disable;
            uniform sampler2D terrain_variation : filter_nearest, repeat_disable;
            uniform sampler2D flow_image : filter_nearest, repeat_disable;
            uniform sampler2D shore_image : filter_linear, repeat_disable;
            uniform float flow_clock = 0.0;
            uniform bool terrain_balanced = false;
            varying vec4 terrain_tint;
            varying vec3 terrain_world;
            void vertex() { terrain_tint = COLOR; terrain_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }
            void fragment() {
                vec4 category = texture(terrain_data, UV);
                float kind = round(category.a * 255.0);
                float variant = round(texture(terrain_variation, UV).r * 255.0);
                vec4 base = kind == 1.0 || kind == 2.0
                    ? texture(terrain_atlas, vec2((variant + 0.5) / 5.0, kind == 2.0 ? 0.75 : 0.25))
                    : vec4(category.rgb, 1.0);
                if (terrain_balanced && kind == 1.0) {
                    float grouping = fract(sin(dot(floor(terrain_world.xz / 3.0), vec2(12.9898, 78.233))) * 43758.5453);
                    base.rgb *= 0.94 + floor(grouping * 3.0) * 0.04;
                }
                vec4 flow = texture(flow_image, UV);
                vec2 encoded = round(flow.rg * 8.0);
                vec2 direction = floor(encoded / 3.0) - 1.0;
                vec2 incoming = mod(encoded, 3.0) - 1.0;
                vec2 patch = floor(terrain_world.xz * 2.0);
                float tone = fract(sin(dot(patch, vec2(12.9898, 78.233))) * 43758.5453);
                if (terrain_tint.a < 0.5 && kind != 2.0) {
                    base.rgb = mix(vec3(32.0, 87.0, 118.0), vec3(48.0, 117.0, 142.0), floor(tone * 5.0) / 4.0) / 255.0;
                } else if (terrain_tint.a >= 0.5 && kind == 2.0) {
                    base.rgb = mix(vec3(56.0, 92.0, 47.0), vec3(80.0, 120.0, 60.0), floor(tone * 5.0) / 4.0) / 255.0;
                }
                vec2 cell_pixels = vec2(8.0);
                vec2 cell_position = terrain_world.xz;
                if (terrain_tint.a < 0.5 && flow.b > 0.5) {
                    float travel = flow_clock * 0.75 + flow.a;
                    float wave = floor(travel / 8.0);
                    float seed = mod(wave, 251.0);
                    seed = mod((seed + 19.0) * (seed + 73.0), 251.0);
                    seed = mod(seed * (seed + 47.0) + 31.0, 251.0);
                    float phase = mod(travel, 8.0) - (0.25 + seed / 251.0 * 2.5);
                    if (phase >= 0.0 && phase < 1.0) {
                        vec2 center = -incoming * 0.5 * (1.0 - phase) * (1.0 - phase) + direction * 0.5 * phase * phase;
                        vec2 offset = (floor(fract(cell_position) * cell_pixels) + 0.5) / cell_pixels - 0.5 - center;
                        vec2 tangent = normalize(mix(incoming, direction, phase));
                        float across = abs(dot(offset, vec2(-tangent.y, tangent.x)));
                        float along = abs(dot(offset, tangent));
                        float variation = mod(wave * 47.0 + floor(cell_position.x) * 13.0 +
                            floor(cell_position.y) * 29.0, 101.0) / 100.0;
                        float width = mix(0.15, 0.27, variation);
                        float length = mix(0.08, 0.15, mod(wave * 31.0 + 7.0, 97.0) / 96.0);
                        float ripple = max((1.0 - step(width, across)) * (1.0 - step(length, along)),
                            (1.0 - step(width * 0.4, across)) * (1.0 - step(length * 1.6, along)));
                        base.rgb = mix(base.rgb, vec3(63.0, 133.0, 159.0) / 255.0, ripple * mix(0.4, 0.58, variation));
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
                ROUGHNESS = 1.0;
                SPECULAR = 0.0;
            }
            """;
    private bool _waterAnimating;
    private bool _visibleRiverFlow;
    private double _flowClock;
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
    private readonly StandardMaterial3D _roadPreviewMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = new Color(1, 1, 1, 0.8f),
        TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        TextureRepeat = true,
    };
    private readonly StandardMaterial3D _invalidRoadPreviewMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = new Color(1, 0.35f, 0.35f, 0.8f),
        TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        TextureRepeat = true,
    };
    internal RoadMeshLayout? RoadPreviewLayout { get; private set; }
    internal int RoadPreviewBorderTriangles { get; private set; }
    internal IReadOnlyList<Vector3> RoadPreviewTriangles { get; private set; } = [];
    internal IReadOnlyList<Vector2[]> RoadPreviewFootprint { get; private set; } = [];
    internal IReadOnlyList<Pos> RoadPreviewChangedCells { get; private set; } = [];
    private RoadMeshLayout? _roadLayout;
    private FootpathSurfaceLayout? _footpathLayout;
    private RoadSurfaceMeshes? _roadSurfaces;
    private readonly Dictionary<int, Vector3[]> _groundFaces = [];
    private readonly Dictionary<int, Vector3[]> _zoneGroundFaces = [];
    internal IReadOnlyList<Vector3> ZoneGroundFaces(Pos cell) =>
        _map is { } map && _zoneGroundFaces.TryGetValue(map.Index(cell.X, cell.Y), out var faces) ? faces : [];
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
    private readonly List<(Aabb Bounds, Pos Anchor, int Color)> _sceneBoxes = [], _scenePickBoxes = [], _sceneYardBoxes = [];
    private List<(Aabb Bounds, Pos Anchor, int Color)> _boxes => _objectCapture?.Boxes ?? _sceneBoxes;
    private List<(Aabb Bounds, Pos Anchor, int Color)> _pickBoxes => _objectCapture?.Picks ?? _scenePickBoxes;
    private List<(Aabb Bounds, Pos Anchor, int Color)> _yardBoxes => _objectCapture?.Yards ?? _sceneYardBoxes;
    private readonly List<(Vector3 A, Vector3 B, Vector3 C, Pos Anchor)> _sceneRoofFaces = [];
    private List<(Vector3 A, Vector3 B, Vector3 C, Pos Anchor)> _roofFaces => _objectCapture?.Faces ?? _sceneRoofFaces;
    private readonly List<(Transform3D Transform, RoadPath Path)> _bridgeRails = [];
    internal IReadOnlyList<(Transform3D Transform, RoadPath Path)> BridgeRails => _bridgeRails;
    private readonly RoadVectorLayer _roads = new();
    private GameMap? _map;
    private int _version = -1;
    private int _sceneTerrainVersion = -1;
    private CellRect _view;
    private CellRect? _selection;
    private Pos? _selectedPropertyAnchor;
    internal IReadOnlyList<HouseOutlineEdge> PropertySelectionOutline { get; private set; } = [];
    private Pos _lastGroundCell;
    private System.Numerics.Vector2 _lastGroundPoint;
    private Pos _selectedSurfaceCell;
    private System.Numerics.Vector2? _selectedSurfacePoint;
    private GameMap? _selectedSurfaceMap;
    private int _selectedSurfaceRevision;
    internal System.Numerics.Vector2[] RoadSelectionOutline { get; private set; } = [];
    internal CellRect? SelectionArea => _selection;
    private PlacementPreview? _preview;
    private MapOverlay _overlay;
    private int _analysisVersion = -1;
    private bool _areaOverlaysVisible;
    private bool _drawingPreview;
    private bool _drawingYard;
    private Transform3D _buildingTransform = Transform3D.Identity;
    private IReadOnlyDictionary<Pos, HouseYard> _houseYards = new Dictionary<Pos, HouseYard>();
    private GameMap? _yardMap;
    private int _yardVersion = -1;
    private HashSet<Pos> _yardCells = [];
    private bool _cameraValid;
    private (int X, int Y, double Zoom, float Width, float Height, int CellsX, int CellsY,
        float Azimuth, float Elevation, Vector2 Offset, Vector2 PixelScale) _cameraState;
    private CellRect _cameraVisibleView;
    private CellRect _cameraGroundView;
    private bool _cameraViewDirty = true;
    private bool _cameraGeometryPending;
    internal double GroundFrameMilliseconds { get; private set; }
    internal double PathFrameMilliseconds { get; private set; }
    internal double ObjectFrameMilliseconds { get; private set; }
    internal double RendererFrameMilliseconds { get; private set; }
    private Vector2 _cameraOffset;
    private (int X, int Y, double Zoom) _offsetCamera;
    private int _renderFrames;
    private int _groundOverlayVersion = -1;
    private MapOverlay _groundOverlay;
    private int _builtCount;
    private float _indicatorThickness;
    public float SelectionOutlineThickness => _indicatorThickness;
    public float SelectionOutlineHeight { get; private set; }
    private float? _selectedWallTop;
    public int GeometryBuilds { get; private set; }
    public double GroundBuildMilliseconds { get; private set; }
    public double ObjectBuildMilliseconds { get; private set; }
    internal double SolidUploadMilliseconds { get; private set; }
    internal double FootpathBuildMilliseconds { get; private set; }
    internal double YardBuildMilliseconds { get; private set; }
    internal double FootpathLayoutMilliseconds { get; private set; }
    internal double FootpathPrepareMilliseconds { get; private set; }
    public double TerrainPixelsMilliseconds { get; private set; }
    public double RoadPixelsMilliseconds { get; private set; }
    public int SceneObjectCount => _world.GetChildCount() + _retainedSolids.GetChildCount() +
        _streamedSolids.GetChildCount() + _paths.GetChildCount() + _indicators.GetChildCount() +
        _fortifications.GetChildCount() + _siegeActors.GetChildCount() +
        GroundRenderNodeCount;
    public int IndicatorObjectCount => _indicators.GetChildCount();
    public float CameraSpan => _camera.Size;
    public Vector2I RenderSize => _viewport.Size;
    public Vector2I NativeRenderSize { get; private set; }
    public Vector2 DisplayPixelScale { get; private set; } = Vector2.One;
    internal Viewport.Msaa EdgeAntialiasing => _viewport.Msaa3D;

    internal static Vector2I NativePixelSize(Vector2 logicalSize, Vector2 pixelScale)
    {
        if (!float.IsFinite(logicalSize.X) || !float.IsFinite(logicalSize.Y) ||
            logicalSize.X < 0 || logicalSize.Y < 0 ||
            !float.IsFinite(pixelScale.X) || !float.IsFinite(pixelScale.Y) ||
            pixelScale.X <= 0 || pixelScale.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(logicalSize), "Display size and pixel scale must be finite and valid.");
        return new(Math.Max(2, checked((int)Math.Ceiling(logicalSize.X * pixelScale.X))),
            Math.Max(2, checked((int)Math.Ceiling(logicalSize.Y * pixelScale.Y))));
    }

    internal Image CaptureDisplayImage()
    {
        var image = Texture.GetImage();
        if (image is null || image.IsEmpty())
        {
            image?.Dispose();
            throw new InvalidOperationException("The 3D renderer did not produce an image.");
        }
        int width = Math.Max(2, (int)Math.Ceiling(DisplaySize.X));
        int height = Math.Max(2, (int)Math.Ceiling(DisplaySize.Y));
        if (image.GetWidth() != width || image.GetHeight() != height)
            image.Resize(width, height, Image.Interpolation.Bilinear);
        return image;
    }

    internal Rect2I DisplayRegionToRender(Rect2I region)
    {
        var scale = new Vector2(RenderSize.X / DisplaySize.X, RenderSize.Y / DisplaySize.Y);
        int left = Math.Clamp((int)Math.Floor(region.Position.X * scale.X), 0, RenderSize.X - 1);
        int top = Math.Clamp((int)Math.Floor(region.Position.Y * scale.Y), 0, RenderSize.Y - 1);
        int right = Math.Clamp((int)Math.Ceiling(region.End.X * scale.X), left + 1, RenderSize.X);
        int bottom = Math.Clamp((int)Math.Ceiling(region.End.Y * scale.Y), top + 1, RenderSize.Y);
        return new(left, top, right - left, bottom - top);
    }
    public CellRect RenderedView => _view;
    public float Azimuth { get; private set; } = InitialAzimuth;
    public float Elevation { get; private set; } = 35;
    public int BuildingsDrawn { get; private set; }
    public int ZonePlotsDrawn { get; private set; }
    internal int PreviewBuildingsDrawn { get; private set; }
    internal bool MeasureZoneTopFaces { get; set; }
    private readonly List<(Pos Cell, Vector3 A, Vector3 B, Vector3 C)> _sceneZoneTopFaces = [];
    private List<(Pos Cell, Vector3 A, Vector3 B, Vector3 C)> _zoneTopFaces =>
        _objectCapture?.ZoneFaces ?? _sceneZoneTopFaces;
    internal IReadOnlyList<(Pos Cell, Vector3 A, Vector3 B, Vector3 C)> ZoneTopFaces
    {
        get
        {
            CompletePendingFootpaths();
            return _sceneZoneTopFaces.Concat(VisibleObjectData.SelectMany(chunk => chunk.Geometry.ZoneFaces)).ToArray();
        }
    }
    internal bool MeasureZoneRoadOverlap { get; set; }
    internal bool MeasureZoneFootpathOverlap { get; set; }
    internal double ZoneFootpathOverlapArea { get; private set; }
    internal double MaximumZoneTriangleFootpathOverlapArea { get; private set; }
    internal double ZoneRoadOverlapArea { get; private set; }
    internal double MaximumZoneTriangleRoadOverlapArea { get; private set; }
    internal int ZoneTrianglesChecked { get; private set; }
    public int RoadMeshChunksDrawn { get; private set; }
    public int RoadBorderTrianglesDrawn { get; private set; }
    public double RoadBorderMilliseconds { get; private set; }
    public int BridgeSegmentsDrawn { get; private set; }
    private readonly HashSet<TreeShape> _sceneTrees = [];
    internal HashSet<TreeShape> TreeShapesDrawn => _objectCapture?.Trees ?? _sceneTrees;
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
    public IReadOnlyList<Aabb> BuildingVolumes(Pos anchor, int? color = null)
    {
        CompletePendingFootpaths();
        return SceneBoxes.Where(box => box.Anchor == anchor && (color is null || box.Color == color))
            .Select(box => box.Bounds).ToArray();
    }

    internal IReadOnlyList<Vector3> BuildingRoofVertices(Pos anchor)
    {
        CompletePendingFootpaths();
        return SceneRoofFaces.Where(face => face.Anchor == anchor)
            .SelectMany(face => new[] { face.A, face.B, face.C }).ToArray();
    }

    internal int PickingVolumeCount(Pos anchor) => ScenePickingBoxes.Count(box => box.Anchor == anchor);

    internal IReadOnlyList<Aabb> YardVolumes(Pos anchor, int? color = null)
    {
        CompletePendingFootpaths();
        return SceneYards.Where(box => box.Anchor == anchor && (color is null || box.Color == color))
            .Select(box => box.Bounds).ToArray();
    }

    public void SetActive(bool active)
    {
        if (!active)
        {
            _waterAnimating = false;
            CancelGroundPreparation();
            CancelMinimapJump();
            ClearTerrainRetention();
        }
        _renderFrames = active ? 2 : 0;
        _viewport.RenderTargetUpdateMode = active ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
    }

    public override void _Process(double delta)
    {
        _actorFrameDelta = delta;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        PollCameraPaths();
        if (_cameraPathSession is { } pathSession) PollCameraGround(pathSession);
        if (_cameraGroundWork is null && !_cameraGeometryPending) _groundBatchContext = null;
        MountPendingDestinationGround();
        MountBufferedGround();
        MountTerrainHalo();
        ApplyGroundDrawPages();
        AdvancePersistentTerrain();
        GroundFrameMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long paths = System.Diagnostics.Stopwatch.GetTimestamp();
        ApplyPathMesh();
        PathFrameMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(paths).TotalMilliseconds;
        long objects = System.Diagnostics.Stopwatch.GetTimestamp();
        AdvanceObjectChunks(delta * 1000);
        ObjectFrameMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(objects).TotalMilliseconds;
        SampleCacheMemory();
        PrepareCompleteFrame();
        if (_waterAnimating)
        {
            _flowClock += delta;
            WaterFlowRenderClock = _flowClock;
            foreach (var material in _groundMaterials.Values)
                if (material.RiverFlow)
                    material.Material.SetShaderParameter("flow_clock", (float)WaterFlowRenderClock);
            UpdateRiverActivity();
            RequestRender();
        }
        if (_renderFrames > 0) _renderFrames--;
        else if (_renderFrames == 0)
        {
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            _renderFrames = -1;
        }
        RendererFrameMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    public override void _Ready()
    {
        ConfigureTerrainStudy();
        _terrainShader.Code = "shader_type spatial;\nrender_mode " +
            (DepthStudyArtwork.UsesLighting(DepthTreatment) ? "" : "unshaded, ") +
            "cull_disabled;\n" + TerrainShaderBody;
        AddChild(_viewport);
        AddChild(_lastCompleteFrame);
        _lastCompleteFrame.AddChild(_frameCopy);
        _frameCopy.Texture = Texture;
        RenderingServer.FramePostDraw += CompleteFrameDrawn;
        _viewport.AddChild(_world);
        _viewport.AddChild(_groundRoot);
        _viewport.AddChild(_retainedSolids);
        _viewport.AddChild(_streamedSolids);
        _viewport.AddChild(_paths);
        _viewport.AddChild(_indicators);
        _ = Material(FootpathArtwork.FreshColor);
        _ = Material(FootpathArtwork.WornColor);
        InitializeSiegeRendering();
        InitializeSelectionHighlight();
        InitializeWeaponRange();
        InitializeRiverActivity();
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
        _roadMaterial.AlbedoTexture = PixelTexture(textureSize, textureSize, roadPixels);
        _roadMaterial.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic;
        _roadMaterial.TextureRepeat = true;
        _roadMaterial.AlbedoColor = new Color(1.05f, 1.05f, 1.05f);
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color("#14221c"),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color("#d3d5bc"),
            AmbientLightEnergy = DepthStudyArtwork.UsesLighting(DepthTreatment) ? 0.38f : 0.7f,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        _viewport.AddChild(new WorldEnvironment { Environment = environment });
        _sunlight = new DirectionalLight3D
        {
            RotationDegrees = DepthStudyArtwork.UsesLighting(DepthTreatment) ? new(-50, -65, 0) : new(-55, -35, 0),
            LightColor = new Color("#ffe4ba"),
            LightEnergy = DepthStudyArtwork.UsesLighting(DepthTreatment) ? 1.05f : 0.8f,
            ShadowEnabled = DepthStudyArtwork.UsesLighting(DepthTreatment),
            DirectionalShadowMaxDistance = 220,
        };
        _viewport.AddChild(_sunlight);
    }

    public void Rotate(float degrees) => Azimuth = Mathf.PosMod(Azimuth + degrees, 360);
    public void Tilt() => Elevation = Elevation == 20 ? 35 : Elevation == 35 ? 50 : 20;

    public override void _ExitTree()
    {
        RenderingServer.FramePostDraw -= CompleteFrameDrawn;
        _frameCopy.Texture = null;
        CancelMinimapJump();
        ClearTerrainRetention();
        CancelGroundPreparation();
        _cameraPathWork?.Task.GetAwaiter().GetResult();
        _pathMeshTask?.GetAwaiter().GetResult();
        _pathPageTask?.GetAwaiter().GetResult();
        _cameraPathWork = _cameraPathsIncompatible = null;
        _cameraPathsReady = null;
        _cameraPathSession = _pathSession = null;
        _pathMeshTask = null;
        _queuedPathMesh = _activePathMesh = null;
        _pathMeshMap = null;
        _pathElevations = null;
        _footpathLayout = null;
        ClearPathMesh();
        ClearObjectStreaming();
        foreach (var key in _preparedGround.Keys.ToArray()) RemovePreparedGround(key);
        _buildingCache.Clear();
        foreach (var key in _roadChunks.Keys.Concat(_groundMaterials.Keys).Distinct().ToArray()) RetireGpuGround(key);
        _terrainAtlas?.Dispose();
        _terrainAtlas = null;
        DisposeGroundDrawPages();
        _streamMap = null;
        _streamSession = null;
        _houseYards = new Dictionary<Pos, HouseYard>();
        _yardCells.Clear();
        _roadSurfaces = null;
        _roadLayout = null;
        _groundFaces.Clear();
        _zoneGroundFaces.Clear();
        _buildingCacheMap = _yardMap = _groundMap = null;
        _preparedGroundEpoch = null;
        _cacheMemoryProcess?.Dispose();
        _cacheMemoryProcess = null;
        DisposeStaticBatches();
        DisposeSelectionHighlight();
        DisposeWeaponRange();
        DisposeWallTops();
        _oilSurface?.Dispose();
        _oilSheetMesh.Mesh?.Dispose();
        _oilMaterial.Dispose();
        foreach (var instance in _wallInstances.Values.Concat(_actorInstances.Values).Concat(_splashInstances.Values))
            instance.Multimesh.Dispose();
        _siegeBuffers.Clear();
        _siegeTransforms.Clear();
        _actorFacings.Clear();
        _actorRigs.Clear();
        _trollModels.Clear();
        _wallArtifacts.Clear();
        DisposeSiegePages();
        _localTopBounds.Clear();
        _localTopIndex.Clear();
        _localTopOrder.Clear();
        _localTopVertices.Clear();
        _clippedWallTops.Clear();
        _uploadedWallTops.Clear();
        _aimStarts.Clear();
        _splashDisk.Dispose();
        foreach (var material in _groundMaterials.Values)
        {
            material.Dispose();
        }
        _groundMaterials.Clear();
        _riverInstances?.Dispose();
        _riverMaterial.Dispose();
        _terrainShader.Dispose();
        foreach (var mesh in _groundMeshes.Values) mesh.Dispose();
        _groundMeshes.Clear();
        foreach (var mesh in _cliffMeshes.Values) mesh.Dispose();
        _cliffMeshes.Clear();
        foreach (var chunk in _roadChunks.Values) chunk.Roads?.Dispose();
        _roadChunks.Clear();
        _roadMaterial.AlbedoTexture?.Dispose();
        _roadMaterial.Dispose();
        _roadPreviewMaterial.Dispose();
        _invalidRoadPreviewMaterial.Dispose();
        _cliffMaterial.Dispose();
        _cube.Dispose();
        _instanceColorMaterial.Dispose();
        _instanceUnshadedMaterial.Dispose();
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

    private bool _cameraPresentationDirty;

    public void Sync(GameSession session, Vector2 displaySize) => SyncCore(session, displaySize, cameraOnly: false);

    public void SyncCamera(GameSession session, Vector2 displaySize) =>
        SyncCore(session, displaySize, cameraOnly: ReferenceEquals(_map, session.Game.Map));

    private void SyncCore(GameSession session, Vector2 displaySize, bool cameraOnly)
    {
        _cameraPathSession = session;
        UpdateOverviewLod(session);
        DisplaySize = displaySize;
        DisplayPixelScale = GetParent() is CanvasItem canvas ? canvas.GetScreenTransform().Scale.Abs() : Vector2.One;
        NativeRenderSize = NativePixelSize(displaySize, DisplayPixelScale);
        _waterAnimating = WaterMotionEnabled && _visibleRiverFlow;
        if (_offsetCamera != (session.CameraX, session.CameraY, session.ZoomLevel) ||
            !ReferenceEquals(_map, session.Game.Map))
            ResetCameraOffset();
        var cameraState = (session.CameraX, session.CameraY, session.ZoomLevel,
            displaySize.X, displaySize.Y, session.ViewWidth, session.ViewHeight, Azimuth, Elevation, _cameraOffset, DisplayPixelScale);
        bool scaleChanged = !_cameraValid || _cameraState.Zoom != session.ZoomLevel;
        bool cameraChanged = !_cameraValid || _cameraState != cameraState || !cameraOnly && _cameraPresentationDirty;
        if (_persistentResidency && (!_cameraValid || _cameraState != cameraState))
            _lastPersistentCameraMotion = System.Diagnostics.Stopwatch.GetTimestamp();
        if (cameraChanged)
        {
            if (MinimapJumpPending)
            {
                _jumpReadyToPublish = false;
                _lastCompleteFrame.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            }
            _cameraState = cameraState;
            _cameraValid = true;
            if (NativeRenderSize.X > 16384 / RenderScale || NativeRenderSize.Y > 16384 / RenderScale)
                throw new InvalidOperationException(
                    $"Requested {RenderScale}x rendering of {NativeRenderSize} exceeds the 16384-pixel texture limit.");
            var renderSize = NativeRenderSize * RenderScale;
            _viewport.Size = renderSize;
            _camera.Size = Math.Max(4, displaySize.Y / (22 * Magnification(session.ZoomLevel)));
            float azimuth = Mathf.DegToRad(Azimuth), elevation = Mathf.DegToRad(Elevation);
            var target = new Vector3(session.CameraX + _cameraOffset.X + session.ViewWidth * session.Stride / 2f, DepthStudyFocusHeight,
                session.CameraY + _cameraOffset.Y + session.ViewHeight * session.Stride / 2f);
            _camera.Position = target + new Vector3(Mathf.Sin(azimuth) * Mathf.Cos(elevation),
                Mathf.Sin(elevation), Mathf.Cos(azimuth) * Mathf.Cos(elevation)) * 150;
            _camera.LookAt(target, Elevation >= 89
                ? new Vector3(-Mathf.Sin(azimuth), 0, -Mathf.Cos(azimuth)) : Vector3.Up);
        }
        if (cameraOnly)
        {
            _cameraPresentationDirty |= cameraChanged;
            _cameraViewDirty |= cameraChanged;
            return;
        }
        _cameraPresentationDirty = false;
        var map = session.Game.Map;
        if (_cameraViewDirty && !_cameraGeometryPending || cameraChanged || !ReferenceEquals(_map, map) || _version != session.Game.MapVersion ||
            _sceneTerrainVersion != map.TerrainVersion)
        {
            var corners = new[] { Vector2.Zero, new Vector2(displaySize.X, 0),
                displaySize, new Vector2(0, displaySize.Y) }.Select(GroundPoint).ToArray();
            int actualX = Math.Clamp((int)Math.Floor(corners.Min(p => p.X)), 0, map.Width - 1);
            int actualY = Math.Clamp((int)Math.Floor(corners.Min(p => p.Z)), 0, map.Height - 1);
            int actualRight = Math.Clamp((int)Math.Ceiling(corners.Max(p => p.X)), actualX, map.Width - 1);
            int actualBottom = Math.Clamp((int)Math.Ceiling(corners.Max(p => p.Z)), actualY, map.Height - 1);
            _cameraGroundView = new(actualX, actualY, actualRight - actualX + 1, actualBottom - actualY + 1);
            int x = Math.Clamp((int)Math.Floor(corners.Min(p => p.X)) - CameraVisibilityMargin, 0, map.Width - 1);
            int y = Math.Clamp((int)Math.Floor(corners.Min(p => p.Z)) - CameraVisibilityMargin, 0, map.Height - 1);
            int edgeX = Math.Clamp((int)Math.Ceiling(corners.Max(p => p.X)) + CameraVisibilityMargin, x, map.Width - 1);
            int edgeY = Math.Clamp((int)Math.Ceiling(corners.Max(p => p.Z)) + CameraVisibilityMargin, y, map.Height - 1);
            _cameraVisibleView = new(x, y, edgeX - x + 1, edgeY - y + 1);
            _cameraViewDirty = false;
        }
        var view = _cameraVisibleView;
        var selection = session.HasCursor && !session.FixedPlacementToolActive ? session.ActiveArea : (CellRect?)null;
        var selectedWall = session.HasCursor &&
            (selection == session.BlockAt(session.Cursor) && session.Anchor is null || session.WallDefenseToolActive)
            ? session.SelectedWallComponent : null;
        float? selectedWallTop = selectedWall is null ? null :
            WallDamageLayout.GroundHeight(selectedWall, map) +
            WallDamageLayout.TopHeight(selectedWall) + (selectedWall.Health > 0 ? 0.56f : 0.06f);
        if (selection is not null && selectedWall is not null)
            selection = selectedWall.SectionBounds;
        Pos? propertyAnchor = selection == session.BlockAt(session.Cursor) &&
            map.BuildingAt(session.Cursor.X, session.Cursor.Y) is not null
            ? map.PosOf(map.BuildingAnchor(map.Index(session.Cursor.X, session.Cursor.Y))) : null;
        bool reuseStaticWindow = !_cameraGeometryPending && ReferenceEquals(_map, map) && _version >= 0 &&
            _sceneTerrainVersion == map.TerrainVersion && !scaleChanged &&
            _overlay == session.Overlay && !session.AreaOverlaysVisible && !_areaOverlaysVisible &&
            session.Overlay == MapOverlay.Off && session.Preview is null && _preview is null && map.Footbridges.Count == 0 &&
            view.X >= _view.X && view.Y >= _view.Y && view.Right <= _view.Right && view.Bottom <= _view.Bottom;
        bool cameraWindowOnly = ReferenceEquals(_map, map) && _version == session.Game.MapVersion &&
            _sceneTerrainVersion == map.TerrainVersion &&
            _overlay == session.Overlay && _areaOverlaysVisible == session.AreaOverlaysVisible &&
            session.Preview is null && _preview is null;
        bool geometryChanged = _cameraGeometryPending || !ReferenceEquals(_map, map) || _version != session.Game.MapVersion ||
            session.Overlay != MapOverlay.Off && _analysisVersion != session.Game.AnalysisVersion ||
            _sceneTerrainVersion != map.TerrainVersion ||
            _overlay != session.Overlay || _areaOverlaysVisible != session.AreaOverlaysVisible ||
            view.X < _view.X || view.Y < _view.Y ||
            view.Right > _view.Right || view.Bottom > _view.Bottom ||
            scaleChanged;
        if (geometryChanged)
            GeometryInvalidationReason = $"pending={_cameraGeometryPending} map={ReferenceEquals(_map, map)} " +
                $"version={_version}/{session.Game.MapVersion} terrain={_sceneTerrainVersion}/{map.TerrainVersion} " +
                $"scale={scaleChanged} view={view} buffered={_view} overlay={_overlay}/{session.Overlay} " +
                $"areas={_areaOverlaysVisible}/{session.AreaOverlaysVisible}";
        if (!geometryChanged) _waitingForCameraPaths = false;
        float outlineThickness = 2 * _camera.Size / Math.Max(1, displaySize.Y);
        bool indicatorsChanged = geometryChanged || _selection != selection ||
            _selectedWallTop != selectedWallTop ||
            _selectedPropertyAnchor != propertyAnchor ||
            _indicatorThickness != outlineThickness || !ReferenceEquals(_preview, session.Preview);
        _indicatorThickness = outlineThickness;
        _map = map; _version = session.Game.MapVersion;
        _analysisVersion = session.Game.AnalysisVersion;
        _sceneTerrainVersion = map.TerrainVersion;
        _selection = selection; _preview = session.Preview; _overlay = session.Overlay;
        _selectedWallTop = selectedWallTop;
        _areaOverlaysVisible = session.AreaOverlaysVisible;
        _selectedPropertyAnchor = propertyAnchor;
        if (geometryChanged)
        {
            reuseStaticWindow &= !_roads.Sync(session.Game);
            if (reuseStaticWindow)
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                PreparePathScene(session);
                if (_staticSceneKey == StaticArtworkKey(map))
                {
                    DrawFootpaths(session);
                    PathSceneBuilds++;
                    GroundBuildMilliseconds = 0;
                    ObjectBuildMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    geometryChanged = false;
                }
                else Rebuild(session, pathsPrepared: true);
            }
            else
            {
                var buffered = BufferedView(view, map);
                if (cameraWindowOnly && !MeasureFootpathFaces && !_cameraPathsFailed &&
                    _roadLayout is not null && session.Game.CanSnapshotSimulation &&
                    !PrepareCameraPresentation(session, buffered))
                {
                    _cameraGeometryPending = true;
                    _cameraViewDirty = true;
                    SyncSiege(session);
                    RequestRender();
                    return;
                }
                _view = buffered;
                Rebuild(session);
                _cameraGeometryPending = false;
            }
            _cameraViewDirty = true;
            _waterAnimating = WaterMotionEnabled && _visibleRiverFlow;
        }
        SyncSiege(session);
        indicatorsChanged |= SyncWallSelectionBounds(session, selectedWall);
        if (indicatorsChanged) RebuildIndicators(session);
        if (geometryChanged) CacheRiverActivitySites();
        RefreshObjectRequests(session);
        if (!geometryChanged && !_cameraGeometryPending && cameraChanged && _pathPresentationView is { } pathView &&
            (_cameraGroundView.X < pathView.X || _cameraGroundView.Y < pathView.Y ||
                _cameraGroundView.Right > pathView.Right || _cameraGroundView.Bottom > pathView.Bottom))
            DrawFootpaths(session);
        if (geometryChanged || cameraChanged) SelectRiverActivitySites();
        if (geometryChanged || cameraChanged || !_waterAnimating) UpdateRiverActivity();
        SyncSelectionHighlight(session);
        bool weaponRangeChanged = SyncWeaponRange(session);
        if (cameraChanged || geometryChanged || indicatorsChanged || weaponRangeChanged)
        {
            _renderFrames = 2;
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        }
    }

    private Vector2 ViewportPoint(Vector2 pointer) => new(pointer.X * _viewport.Size.X / Math.Max(1, DisplaySize.X),
        pointer.Y * _viewport.Size.Y / Math.Max(1, DisplaySize.Y));

    public Vector3 GroundPlanePoint(Vector2 pointer, float elevation = 0)
    {
        var pixel = ViewportPoint(pointer);
        var ray = _camera.ProjectRayNormal(pixel);
        var origin = _camera.ProjectRayOrigin(pixel);
        return origin + ray * ((elevation - origin.Y) / ray.Y);
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
        int reach = (int)Math.Ceiling(TerrainSprite.MaximumHillFor(map) *
            new Vector2(ray.X, ray.Z).Length() / Math.Abs(ray.Y)) + 1;
        for (int dy = -reach; dy <= reach; dy++)
            for (int dx = -reach; dx <= reach; dx++)
            {
                int cx = x + dx, cy = y + dy;
                if (!map.InBounds(cx, cy)) continue;
                var faces = GroundFaces(map, cx, cy, land: false);
                if (faces is not null)
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
        for (int i = 0; i + 2 < _footbridgeFaces.Count; i += 3)
        {
            float distance = RayTriangleDistance(origin, ray, _footbridgeFaces[i], _footbridgeFaces[i + 1], _footbridgeFaces[i + 2]);
            if (distance < nearest)
            {
                nearest = distance;
                ground = origin + ray * distance;
            }
        }
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
            var origin = new Vector3(x, TerrainSprite.MaximumHillFor(map) + 2, z);
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
        var point = new System.Numerics.Vector2(hit.Point.X, hit.Point.Z);
        _lastGroundCell = cell;
        _lastGroundPoint = hit.Bridge is { } bridge && _roadLayout is { } layout
            ? layout.NearestPoint(bridge, point) : point;
        return cell;
    }

    internal void RememberSelectionSurface(Pos cell)
    {
        _selectedSurfaceCell = cell;
        _selectedSurfacePoint = cell == _lastGroundCell &&
            _map?.BuildingAt(cell.X, cell.Y) is null ? _lastGroundPoint : null;
        _selectedSurfaceMap = _map;
        _selectedSurfaceRevision = _roads.Revision;
        _selection = null;
    }

    internal System.Numerics.Vector2? SelectedSurfacePoint(Pos cell) =>
        cell == _selectedSurfaceCell && ReferenceEquals(_selectedSurfaceMap, _map) &&
        _selectedSurfaceRevision == _roads.Revision ? _selectedSurfacePoint : null;

    public Vector2 ScreenPoint(Vector3 world)
    {
        var pixel = _camera.UnprojectPosition(world);
        return new(pixel.X * DisplaySize.X / _viewport.Size.X, pixel.Y * DisplaySize.Y / _viewport.Size.Y);
    }

    public bool PickBuilding(Vector2 pointer, out Pos anchor)
    {
        anchor = default;
        PickedWallId = null;
        var pixel = ViewportPoint(pointer);
        var origin = _camera.ProjectRayOrigin(pixel);
        var direction = _camera.ProjectRayNormal(pixel);
        float nearest = float.PositiveInfinity;
        foreach (var box in StructureCandidates(origin, direction))
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
            if (hit && near <= far && near < nearest)
            { nearest = near; anchor = box.Anchor; PickedWallId = null; }
        }
        for (int i = 0; i < _siegePickBoxes.Count; i++)
        {
            var box = _siegePickBoxes[i];
            var transform = _siegePickTransforms.GetValueOrDefault(i,
                new Transform3D(Basis.Identity.Scaled(box.Bounds.Size), box.Bounds.GetCenter()));
            var inverse = transform.AffineInverse();
            var localOrigin = inverse * origin;
            var localDirection = inverse.Basis * direction;
            float near = 0, far = float.PositiveInfinity;
            bool hit = true;
            for (int axis = 0; axis < 3; axis++)
            {
                if (Math.Abs(localDirection[axis]) < .000001f)
                {
                    if (Math.Abs(localOrigin[axis]) > .5f) hit = false;
                    continue;
                }
                float a = (-.5f - localOrigin[axis]) / localDirection[axis];
                float b = (.5f - localOrigin[axis]) / localDirection[axis];
                near = Math.Max(near, Math.Min(a, b)); far = Math.Min(far, Math.Max(a, b));
            }
            if (hit && near <= far && near < nearest)
            { nearest = near; anchor = box.Anchor; PickedWallId = _siegePickWallIds.GetValueOrDefault(i); }
        }
        foreach (var face in SceneRoofFaces)
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
            { nearest = distance; anchor = face.Anchor; PickedWallId = null; }
        }
        if (!float.IsFinite(nearest)) return false;
        var terrain = GroundHit(pointer).Point;
        bool terrainBlocks = _map is { } map && map.InBounds((int)MathF.Floor(terrain.X), (int)MathF.Floor(terrain.Z)) &&
            origin.DistanceTo(terrain) + .001f < nearest;
        if (terrainBlocks || NaturalHit(origin, direction, out _, out float natural) && natural < nearest)
        {
            anchor = default;
            PickedWallId = null;
            return false;
        }
        return true;
    }

    private static ImageTexture PixelTexture(int width, int height, byte[] pixels)
    {
        using var image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, pixels);
        var error = image.GenerateMipmaps();
        if (error != Error.Ok) throw new InvalidOperationException($"Could not generate pixel-texture mipmaps: {error}.");
        return ImageTexture.CreateFromImage(image);
    }

    internal IEnumerable<StandardMaterial3D> PixelMaterials =>
        _materials.Values.Where(material => material.AlbedoTexture is not null);

    internal bool StablePixelMaterials
    {
        get
        {
            foreach (var material in PixelMaterials)
            {
                using var image = material.AlbedoTexture.GetImage();
                if (material.TextureFilter != BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic ||
                    !image.HasMipmaps()) return false;
            }
            return true;
        }
    }

    private StandardMaterial3D Material(int rgb, bool solid = false)
    {
        if (!_materials.TryGetValue((rgb, solid), out var material))
        {
            material = new StandardMaterial3D
            {
                AlbedoColor = new Color(
                ((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f),
                Roughness = 1,
                CullMode = solid ? BaseMaterial3D.CullModeEnum.Back : BaseMaterial3D.CullModeEnum.Disabled
            };
            if (rgb is 0x58d068 or 0x58a6ff or 0xf2c94c or 0xff6b25 or 0xffaa33 or 0xffdd66)
                material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            else if (FootpathArtwork.IsTexturedColor(rgb))
            {
                material.AlbedoTexture = PixelTexture(FootpathArtwork.TextureSize, FootpathArtwork.TextureSize,
                    FootpathArtwork.MaterialTexture(rgb));
                material.TextureRepeat = true;
            }
            else if (PalisadeArtwork.IsTexturedColor(rgb))
            {
                material.AlbedoTexture = PixelTexture(PalisadeArtwork.TextureSize, PalisadeArtwork.TextureSize,
                    PalisadeArtwork.MaterialTexture(rgb));
            }
            else if (HouseAppearance.IsTexturedColor(rgb))
            {
                material.AlbedoTexture = PixelTexture(HouseAppearance.TextureSize, HouseAppearance.TextureSize,
                    HouseAppearance.MaterialTexture(rgb));
            }
            else if (MapMaterialArtwork.IsStoneColor(rgb) || MapMaterialArtwork.IsTimberColor(rgb))
            {
                material.AlbedoTexture = PixelTexture(16, 16, MapMaterialArtwork.Texture(rgb));
                material.TextureRepeat = true;
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
                material.AlbedoTexture = PixelTexture(size, size, pixels);
            }
            else if (TreeAppearance.IsFoliageColor(rgb))
            {
                material.AlbedoTexture = PixelTexture(16, 16, TerrainSprite.FoliageTexture(rgb));
            }
            if (material.AlbedoTexture is not null)
            {
                material.AlbedoColor = Colors.White;
                material.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic;
            }
            _materials.Add((rgb, solid), material);
        }
        return material;
    }

    private Aabb BuildingBounds(Vector3 center, Vector3 size)
    {
        var rotatedCenter = _buildingTransform * center;
        var rotatedSize = _buildingTransform.Basis.X.Abs() * size.X +
            _buildingTransform.Basis.Y.Abs() * size.Y + _buildingTransform.Basis.Z.Abs() * size.Z;
        return new(rotatedCenter - rotatedSize / 2, rotatedSize);
    }

    private void PickVolume(Vector3 center, Vector3 size, Pos anchor)
    {
        if (!_drawingPreview) _pickBoxes.Add((BuildingBounds(center, size), anchor, 0));
    }

    private void Box(Vector3 center, Vector3 size, int rgb, Pos? anchor = null, bool pick = true, bool foundation = false)
    {
        if (_drawingPreview) rgb = PreviewColor(rgb);
        BatchBox(rgb, new Transform3D(Basis.Identity.Scaled(size), center));
        if (!_drawingPreview && anchor is { } owner)
        {
            if (!_drawingYard) _structureHighlightBoxes.Add((_buildingTransform * new Transform3D(Basis.Identity.Scaled(size), center), owner));
            var volume = (BuildingBounds(center, size), owner, rgb);
            if (foundation) _foundations.Add((volume.Item1, owner));
            (_drawingYard ? _yardBoxes : _boxes).Add(volume);
            if (pick && !_drawingYard) _pickBoxes.Add(volume);
        }
    }

    private void BatchBox(int rgb, Transform3D transform)
    {
        if (!_boxBatches.TryGetValue(rgb, out var batch))
        {
            batch = [];
            _boxBatches.Add(rgb, batch);
        }
        var world = _buildingTransform * transform;
        batch.Add(world);
        if (_capturingBuilding is { } cached)
        {
            if (!cached.Batches.TryGetValue(rgb, out var captured)) cached.Batches.Add(rgb, captured = []);
            captured.Add(world);
        }
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
            var normal = _buildingTransform.Basis * (q - p).Cross(r - p).Normalized();
            void Vertex(Vector3 vertex)
            {
                var uv = gable && gableColor.HasValue
                    ? new((vertex.Z - center.Z) / depth + 0.5f, 1 - (vertex.Y - center.Y) / rise)
                    : new Vector2((vertex.X - center.X) / width + 0.5f, (vertex.Z - center.Z) / depth + 0.5f);
                var rendered = new SurfaceVertex(color, _buildingTransform * vertex, normal, uv);
                AppendRoofVertex(rendered);
                _capturingBuilding?.Roof.Add(rendered);
            }
            Vertex(p); Vertex(q); Vertex(r);
            if (!_drawingPreview) _roofFaces.Add((_buildingTransform * p, _buildingTransform * q, _buildingTransform * r, anchor));
        }
        Face(a, e, f); Face(a, f, b); Face(d, c, f); Face(d, f, e);
        Face(a, d, e, true); Face(b, f, c, true);
    }

    private void FlushBatches(Node3D root)
    {
        foreach (var (visual, vertices) in _zoneBatches)
        {
            using var mesh = ZonePacket(vertices).Upload();
            root.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = ZoneMaterial(visual) });
        }
        _zoneBatches.Clear();
        FlushVertexBatches(root, _footpathBatches);
        FlushSolidBatches(root);
    }

    private void FlushVertexBatches(Node3D root, Dictionary<int, List<SurfaceVertex>> batches)
    {
        bool retain = ReferenceEquals(root, _world) && ReferenceEquals(batches, _roofBatches);
        foreach (var (color, vertices) in batches)
        {
            if (retain && ReuseRoof(color, vertices)) continue;
            using var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices.Select(vertex => vertex.Point).ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = vertices.Select(vertex => vertex.Normal).ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = vertices.Select(vertex => vertex.Uv).ToArray();
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            if (retain) UploadRoof(color, vertices, mesh);
            else
            {
                root.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = Material(color) });
                mesh.Dispose();
            }
        }
        batches.Clear();
    }

    private void FlushSolidBatches(Node3D root)
    {
        bool retain = ReferenceEquals(root, _world);
        if (retain)
        {
            _usedInstances.Clear();
            _usedRoofs.Clear();
            StaticBatchesReusedLastBuild = 0;
            StaticBatchesUploadedLastBuild = 0;
        }
        var plain = new Dictionary<bool, List<(int Color, List<Transform3D> Transforms, float[] Buffer)>>();
        foreach (var (color, transforms) in _boxBatches)
        {
            var material = Material(color, solid: true);
            if (material.AlbedoTexture is null)
            {
                bool unshaded = material.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded;
                if (!plain.TryGetValue(unshaded, out var values)) plain.Add(unshaded, values = []);
                values.Add((color, transforms, InstanceBuffer(color, transforms, colors: true, cachedSpans: retain)));
                continue;
            }
            var buffer = InstanceBuffer(color, transforms, colors: false, cachedSpans: retain);
            var instances = UploadSolidInstances(root, color, buffer, transforms.Count, colors: false, material);
            if (ValidateCityInstanceBuffers && transforms.Where((transform, index) =>
                !transform.IsEqualApprox(instances.GetInstanceTransform(index))).Any())
                throw new InvalidOperationException("Bulk city instance transforms do not match their authored geometry.");
            if (!retain) instances.Dispose();
        }
        foreach (var (unshaded, values) in plain)
        {
            int count = values.Sum(value => value.Transforms.Count);
            var buffer = new float[count * 16];
            int offset = 0;
            foreach (var value in values)
            {
                Array.Copy(value.Buffer, 0, buffer, offset, value.Buffer.Length);
                offset += value.Buffer.Length;
            }
            var instances = UploadSolidInstances(root, unshaded ? -1 : -2, buffer, count, colors: true,
                unshaded ? _instanceUnshadedMaterial : _instanceColorMaterial);
            if (ValidateCityInstanceBuffers)
            {
                int index = 0;
                foreach (var value in values)
                {
                    var rgb = Rgb.Hex(value.Color);
                    var expected = Color.Color8(rgb.R, rgb.G, rgb.B);
                    foreach (var transform in value.Transforms)
                    {
                        var actual = instances.GetInstanceColor(index);
                        if (!transform.IsEqualApprox(instances.GetInstanceTransform(index)) ||
                            Math.Abs(actual.R - expected.R) > .005f || Math.Abs(actual.G - expected.G) > .005f ||
                            Math.Abs(actual.B - expected.B) > .005f || Math.Abs(actual.A - expected.A) > .005f)
                            throw new InvalidOperationException("Bulk city instance transforms or colors do not match their authored geometry.");
                        index++;
                    }
                }
            }
            if (!retain) instances.Dispose();
        }
        _boxBatches.Clear();
        FlushVertexBatches(root, _roofBatches);
        if (retain)
        {
            PruneStaticBatches();
            _cachedInstanceSpans.Clear();
        }
    }

    private static int PreviewColor(int rgb)
    {
        var color = Rgb.Blend(Rgb.Hex(rgb), Rgb.Hex(0x64c788), 0.6);
        return color.R << 16 | color.G << 8 | color.B;
    }

    private ulong _staticSceneKey;
    internal int PathSceneBuilds { get; private set; }
    internal string GeometryInvalidationReason { get; private set; } = "";

    internal PathSceneRequest? PathPresentationRequest(GameSession session) =>
        _roadLayout is not null && ReferenceEquals(_map, session.Game.Map) && _roads.IsCurrent(session.Game)
            ? new(MeasureFootpathFaces ? _view : PathLayoutView(_view, session.Game.Map), _roadLayout) : null;

    private ulong StaticArtworkKey(GameMap map)
    {
        ulong hash = 14695981039346656037;
        void Mix(uint value) => hash = unchecked((hash ^ value) * 1099511628211);
        foreach (var cell in _view.Cells())
        {
            var type = map.BuildingAt(cell.X, cell.Y);
            Mix((uint)((type?.Id ?? 0) | (map.FeatureAt(cell.X, cell.Y)?.Id ?? 0) << 8 |
                (int)map.ZoneAt(cell.X, cell.Y) << 16));
            if (type is null) continue;
            int owner = map.BuildingAnchor(map.Index(cell.X, cell.Y));
            var area = map.BuildingFootprintAt(cell.X, cell.Y);
            Mix((uint)owner);
            Mix((uint)(area.Width | area.Height << 16));
            Mix((uint)map.BuildingRotations.GetValueOrDefault(owner));
        }
        foreach (var (anchor, yard) in _houseYards)
        {
            var area = map.BuildingFootprintAt(anchor.X, anchor.Y);
            if (area.Right >= _view.X && area.X <= _view.Right &&
                area.Bottom >= _view.Y && area.Y <= _view.Bottom || _view.Contains(yard.Cell))
            {
                Mix((uint)map.Index(anchor.X, anchor.Y));
                Mix((uint)map.Index(yard.Cell.X, yard.Cell.Y));
            }
        }
        return hash;
    }

    private void PreparePathScene(GameSession session)
    {
        var map = session.Game.Map;
        var pathView = MeasureFootpathFaces ? _view : PathLayoutView(_view, map);
        if ((session.Game.TakePreparedPaths(pathView) ?? TakeCameraPaths(map, _view)) is { } prepared)
        {
            _footpathLayout = prepared.Footpaths;
            _pathElevations = prepared.Elevations;
            _houseYards = prepared.Yards;
            _yardCells = prepared.YardCells.ToHashSet();
            _yardMap = map;
            _yardVersion = map.NavigationVersion;
            FootpathBuildMilliseconds = FootpathLayoutMilliseconds = YardBuildMilliseconds = 0;
            return;
        }
        if (session.BackgroundPresentationPending && _footpathLayout is not null) return;
        _pathElevations = null;
        long pathsStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        _footpathLayout = new FootpathSurfaceLayout(map, _roadLayout, pathView);
        FootpathBuildMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(pathsStarted).TotalMilliseconds;
        FootpathLayoutMilliseconds = FootpathBuildMilliseconds;
        long yardsStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!ReferenceEquals(_yardMap, map) || _yardVersion != map.NavigationVersion)
        {
            var strokes = FootpathLayout.Strokes(map);
            var yardPaths = new Dictionary<(int X, int Y), FootpathSurfaceLayout>();
            _houseYards = HouseYardLayout.CreateLazy(map, _roadLayout, cell =>
            {
                var key = (X: cell.X / 16, Y: cell.Y / 16);
                if (!yardPaths.TryGetValue(key, out var layout))
                    yardPaths[key] = layout = new FootpathSurfaceLayout(map, strokes, _roadLayout,
                        new CellRect(key.X * 16, key.Y * 16, 16, 16));
                return layout;
            });
            _yardCells = _houseYards.Values.Select(yard => yard.Cell).ToHashSet();
            _yardMap = map;
            _yardVersion = map.NavigationVersion;
        }
        YardBuildMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(yardsStarted).TotalMilliseconds;
    }

    internal void PublishSimulationPaths(GameSession session)
    {
        if (!ReferenceEquals(_map, session.Game.Map)) return;
        PreparePathScene(session);
        _pathPresentationView = null;
        DrawFootpaths(session);
        RequestRender();
    }

    private void Rebuild(GameSession session, bool pathsPrepared = false)
    {
        _waitingForCameraPaths = false;
        _cachedInstanceSpans.Clear();
        GeometryBuilds++;
        DoorApproachTrianglesDrawn = 0;
        _entranceThresholds.Clear();
        ExposedCutTriangles = 0;
        _exposedCutCenters.Clear();
        _wallSamples.Clear();
        _cliffEdges.Clear();
        foreach (var child in _world.GetChildren()) { _world.RemoveChild(child); child.QueueFree(); }
        ReconcileGroundVisibility(_view);
        if (!_groundDrawPagesEnabled) _mountedGroundChunks.Clear();
        _stagingGroundView = null;
        _boxes.Clear(); _pickBoxes.Clear(); _yardBoxes.Clear(); _roofFaces.Clear(); _bridgeRails.Clear(); BuildingsDrawn = 0; ZonePlotsDrawn = 0;
        _sceneFoundations.Clear();
        _naturalPickGroups.Clear();
        _structureHighlightBoxes.Clear();
        ZoneRoadOverlapArea = MaximumZoneTriangleRoadOverlapArea = 0;
        ZoneFootpathOverlapArea = MaximumZoneTriangleFootpathOverlapArea = 0;
        ZoneTrianglesChecked = 0;
        _zoneTopFaces.Clear();
        TreeShapesDrawn.Clear();
        var map = session.Game.Map;
        _roads.Sync(session.Game);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        DrawGround(session);
        GroundBuildMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = System.Diagnostics.Stopwatch.GetTimestamp();
        RefreshBuildingCache(map, _roads.Revision);
        if (!pathsPrepared) PreparePathScene(session);
        DrawBridges(map);
        if (!MeasureFootpathFaces)
        {
            RebuildStreamedObjects(session);
            ObjectBuildMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return;
        }
        if (_streamingObjects) ClearObjectStreaming();
        foreach (var p in _view.Cells())
        {
            float level = RenderedGroundHeight(map, p.X + 0.5f, p.Y + 0.5f);
            var zone = map.ZoneAt(p.X, p.Y);
            if (session.AreaOverlaysVisible && zone != ZoneType.None &&
                map.BuildingAt(p.X, p.Y) is null && !_yardCells.Contains(p))
                ZonePlot(session, p);
            if (map.FeatureAt(p.X, p.Y) is { } feature && TerrainSprite.NaturalFeatureVisible(session.Game, p.X, p.Y))
            {
                var at = new Vector3(p.X + 0.5f, level, p.Y + 0.5f);
                if (feature.Name == "Tree") Tree(at);
                else Rock(at, CellHash.Pick(p.X, p.Y, 6),
                    0.8f + CellHash.Pick(p.X, p.Y, 4) * 0.1f, CellHash.Pick(p.X, p.Y, 4));
            }
        }
        long pathsStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        DrawFootpaths(session);
        FootpathBuildMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(pathsStarted).TotalMilliseconds;
        DrawCivilianFootbridges(map);
        foreach (var pair in map.BuildingFootprints)
            if (pair.Value.Right >= _view.X && pair.Value.X <= _view.Right &&
                pair.Value.Bottom >= _view.Y && pair.Value.Y <= _view.Bottom)
                Building(map, pair.Value, map.BuildingAt(pair.Value.X, pair.Value.Y)!);
        foreach (var p in _view.Cells())
            if (map.BuildingAt(p.X, p.Y) is { } type && map.IsBuildingAnchor(map.Index(p.X, p.Y)) &&
                !map.BuildingFootprints.ContainsKey(map.Index(p.X, p.Y))) Building(map, new(p.X, p.Y, 1, 1), type);
        foreach (var (anchor, yard) in _houseYards)
            if (yard.Cell.X >= _view.X && yard.Cell.X <= _view.Right &&
                yard.Cell.Y >= _view.Y && yard.Cell.Y <= _view.Bottom)
            {
                _drawingYard = true;
                try { HouseYard(map, yard, anchor); }
                finally { _drawingYard = false; }
            }
        DrawDefenseModelPreview();
        DrawWallConstructionPreview();
        DrawDepthStudySubjects();
        BuildStructurePicking();
        long uploadStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        FlushBatches(_world);
        SolidUploadMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(uploadStarted).TotalMilliseconds;
        _builtCount = BuildingsDrawn;
        ObjectBuildMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _staticSceneKey = StaticArtworkKey(map);
    }

    private void RebuildIndicators(GameSession session)
    {
        PreviewBuildingsDrawn = 0;
        foreach (var child in _indicators.GetChildren()) { _indicators.RemoveChild(child); child.QueueFree(); }
        var map = session.Game.Map;
        BuildingsDrawn = _builtCount;
        RoadSelectionOutline = [];
        PropertySelectionOutline = [];
        SelectionVisualFootprint = null;
        RoadPreviewLayout = null;
        RoadPreviewBorderTriangles = 0;
        RoadPreviewTriangles = [];
        RoadPreviewFootprint = [];
        RoadPreviewChangedCells = [];
        if (_selection is { } selected)
        {
            var cell = new Pos(selected.X, selected.Y);
            var point = SelectedSurfacePoint(session.Cursor) ??
                new System.Numerics.Vector2(session.Cursor.X + 0.5f, session.Cursor.Y + 0.5f);
            if (session.Stride > 1 && selected.Area == session.Stride * session.Stride &&
                map.BuildingAt(cell.X, cell.Y) is null && _roadLayout is { } layout &&
                layout.RoadAt(point)?.Type.WidthCells > 1)
                RoadSelectionOutline = layout.SelectionPolygon(point, session.Stride);
            if (_selectedPropertyAnchor is { } property)
            {
                var footprint = map.BuildingFootprintAt(property.X, property.Y);
                PropertySelectionOutline = HouseYardLayout.Outline(footprint,
                    _houseYards.TryGetValue(property, out var yard) ? yard.Cell : null);
                OutlineProperty(map, PropertySelectionOutline, 0xe5cf79);
            }
            else if (RoadSelectionOutline.Length >= 3) OutlineRoad(map, RoadSelectionOutline, 0xe5cf79);
            else if (_selectedWallTop is not null) OutlineSelectedObject(session, selected, 0xe5cf79);
            else Outline(map, selected, 0xe5cf79);
        }
        if (_preview is { } preview)
        {
            DrawSettlementGhost(session, preview);
            if (preview.Building is { } building)
            {
                _drawingPreview = true;
                foreach (var area in session.Game.PlanBuildings(building, preview.Area, preview.Rotation))
                    Building(map, area, building, preview.Rotation);
                _drawingPreview = false;
            }
            if (preview.Template is { } template && preview.Road is { } road)
            {
                DrawRoadGhost(template.Cells(new(preview.Area.X, preview.Area.Y))
                    .Where(map.InBounds).ToDictionary(p => map.Index(p.X, p.Y), p =>
                        map.RoadTypeAt(p.X, p.Y) is { } existing && existing.Rank > road.Rank ? existing : road),
                    preview.Quote.Cells > 0, preview.Area);
            }
            if (preview.RoadStroke is { } stroke)
            {
                var roadType = preview.Road ?? session.Game.DefaultRoad;
                var planned = new Dictionary<int, TermCity.Core.Roads.RoadType>();
                foreach (var p in stroke)
                {
                    bool valid = preview.ValidCells?.Contains(p) == true || map.HasRoad(p.X, p.Y);
                    if (map.InBounds(p) && (valid || preview.Quote.Cells == 0))
                        planned[map.Index(p.X, p.Y)] = map.RoadTypeAt(p.X, p.Y) is { } existing && existing.Rank > roadType.Rank
                            ? existing : roadType;
                    if (!valid) Outline(map, CellRect.Single(p), 0xe47676);
                }
                bool waitingForNextStroke = stroke.Count == 1 && preview.Quote.Cells == 0 &&
                    map.RoadTypeAt(stroke[0].X, stroke[0].Y)?.Rank >= roadType.Rank;
                if (waitingForNextStroke) Outline(map, CellRect.Single(stroke[0]), 0x64c788);
                else DrawRoadGhost(planned, preview.Quote.Cells > 0, preview.Area);
            }
            else if (preview.Kind == PlacementKind.Road && preview.Road is { } roadType)
            {
                var cells = session.Game.PlanRoad(preview.Area.Cells(), roadType);
                var proposed = cells.Count > 0 ? cells : preview.Area.Cells().Where(map.InBounds).ToList();
                DrawRoadGhost(proposed.ToDictionary(p => map.Index(p.X, p.Y), _ => roadType),
                    preview.Quote.Cells > 0, preview.Area);
            }
            if (preview.WallPlan is { } wallPlan &&
                System.Numerics.Vector2.DistanceSquared(wallPlan.Start, wallPlan.End) >= .0025f)
            {
                var ghost = WallDamageLayout.Preview(wallPlan.Start, wallPlan.End,
                    session.Game.Settlement?.UpgradeTargetMaterial ?? session.Game.Settlement?.Material ?? WallMaterial.Wood, wallPlan.Gate);
                var normal = new System.Numerics.Vector2(-ghost.Tangent.Y, ghost.Tangent.X);
                var side = normal * (WallDamageLayout.Depth(ghost) / 2);
                OutlineRoad(map, [wallPlan.Start - side, wallPlan.End - side, wallPlan.End + side, wallPlan.Start + side],
                    wallPlan.Valid ? 0x64c788 : 0xe47676);
                foreach (var enclosure in preview.WallEnclosures ?? [])
                    OutlineRoad(map, enclosure, 0xe5cf79);
            }
            else if (preview.Kind != PlacementKind.Road || preview.RoadStroke is null)
            {
                int color = preview.Quote.Cells > 0 ? 0x64c788 : 0xe47676;
                if (preview.Kind == PlacementKind.WallDefense) OutlineSelectedObject(session, preview.Area, color);
                else Outline(map, preview.Area, color);
            }
        }
        void DrawRoadGhost(IReadOnlyDictionary<int, TermCity.Core.Roads.RoadType> planned, bool valid, CellRect bounds)
        {
            var changed = planned.Where(pair =>
                map.RoadTypeAt(pair.Key % map.Width, pair.Key / map.Width) != pair.Value).ToArray();
            if (changed.Length == 0) return;
            RoadPreviewChangedCells = changed.Select(pair => new Pos(pair.Key % map.Width, pair.Key / map.Width)).ToArray();
            int minX = RoadPreviewChangedCells.Min(p => p.X), minY = RoadPreviewChangedCells.Min(p => p.Y);
            bounds = new(minX, minY, RoadPreviewChangedCells.Max(p => p.X) - minX + 1,
                RoadPreviewChangedCells.Max(p => p.Y) - minY + 1);
            int reach = (int)Math.Ceiling(planned.Values.Max(type => type.BendRadiusCells + type.WidthCells)) + 2;
            const int span = TerrainSprite.ChunkCells;
            int left = Math.Max(_view.X, bounds.X - reach), top = Math.Max(_view.Y, bounds.Y - reach);
            int right = Math.Min(_view.Right, bounds.Right + reach), bottom = Math.Min(_view.Bottom, bounds.Bottom + reach);
            if (left > right || top > bottom) return;
            int pathLeft = left / span * span - reach, pathTop = top / span * span - reach;
            int pathRight = (right / span + 1) * span + reach, pathBottom = (bottom / span + 1) * span + reach;
            var context = new CellRect(pathLeft, pathTop, pathRight - pathLeft, pathBottom - pathTop);
            var paths = RoadCurves.Extract(map, (_, _) => false, planned).Where(path =>
                RoadCurves.Intersects(path, context)).ToArray();
            var layout = new RoadMeshLayout(map, paths);
            RoadPreviewLayout = layout;
            var surfaces = new RoadSurfaceMeshes(layout);
            var baseline = _roadLayout ?? new RoadMeshLayout(map, RoadCurves.Extract(map, (_, _) => false));
            var triangles = new List<Vector3>();
            var footprint = new List<Vector2[]>();
            _roadPreviewMaterial.AlbedoTexture = _roadMaterial.AlbedoTexture;
            _invalidRoadPreviewMaterial.AlbedoTexture = _roadMaterial.AlbedoTexture;
            for (int cy = top / span; cy <= bottom / span; cy++)
                for (int cx = left / span; cx <= right / span; cx++)
            {
                var chunk = surfaces.Build(session, cx, cy, baseline);
                chunk.Ground.Dispose();
                chunk.Banks?.Dispose();
                if (chunk.Roads is not { } mesh) continue;
                triangles.AddRange(chunk.PreviewTriangles);
                footprint.AddRange(chunk.PreviewFootprint);
                RoadPreviewBorderTriangles += chunk.RoadBorderTriangles;
                _indicators.AddChild(new MeshInstance3D
                {
                    Mesh = mesh,
                    MaterialOverride = valid ? _roadPreviewMaterial : _invalidRoadPreviewMaterial,
                    Position = new(0, 0.025f, 0),
                });
                mesh.Dispose();
            }
            RoadPreviewTriangles = triangles;
            RoadPreviewFootprint = footprint;
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
        var patches = ZoneSurfaceLayout.Patches(_roadLayout, cell.X, cell.Y, _footpathLayout);
        System.Numerics.Vector3[] terrain;
        if (GroundFaces(map, cell.X, cell.Y, land: true) is { } ground)
            terrain = ground.Select(point => new System.Numerics.Vector3(point.X, point.Y, point.Z)).ToArray();
        else
        {
            float height = TerrainSprite.Elevation(map, cell.X, cell.Y);
            terrain = [new(cell.X, height, cell.Y), new(cell.X, height, cell.Y + 1), new(cell.X + 1, height, cell.Y + 1),
                new(cell.X, height, cell.Y), new(cell.X + 1, height, cell.Y + 1), new(cell.X + 1, height, cell.Y)];
        }
        var triangles = ZoneSurfaceLayout.Conform(patches, terrain);
        ZonePlotsDrawn++;
        if (triangles.Count == 0) return;
        var visual = CellRenderer.RenderZone(session.Game, cell.X, cell.Y, session.Overlay);
        if (!_zoneBatches.TryGetValue(visual, out var surface))
        {
            surface = [];
            _zoneBatches.Add(visual, surface);
        }
        Vector3 Vertex(System.Numerics.Vector3 point)
        {
            var vertex = new Vector3(point.X, point.Y, point.Z);
            surface.Add(new(0xffffff, vertex, Vector3.Up,
                new(point.X * ZoneOverlayArtwork.Across, point.Z * ZoneOverlayArtwork.Down)));
            return vertex;
        }
        foreach (var triangle in triangles)
        {
            var a = Vertex(triangle.A); var b = Vertex(triangle.B); var c = Vertex(triangle.C);
            if (MeasureZoneTopFaces || _objectCapture is not null) _zoneTopFaces.Add((cell, a, b, c));
            if (MeasureZoneRoadOverlap || MeasureZoneFootpathOverlap)
                MeasureTriangle(new(triangle.A.X, triangle.A.Z), new(triangle.B.X, triangle.B.Z), new(triangle.C.X, triangle.C.Z));
        }

        void MeasureTriangle(System.Numerics.Vector2 a, System.Numerics.Vector2 b, System.Numerics.Vector2 c)
        {
            ZoneTrianglesChecked++;
            // Local scaled coordinates retain subpixel polygon precision at distant
            // map cells. Measure the emitted fan, not just its source patch.
            const float scale = 1024;
            Vector2 Local(System.Numerics.Vector2 point) =>
                new((point.X - cell.X) * scale, (point.Y - cell.Y) * scale);
            Vector2[] triangle = [Local(a), Local(b), Local(c)];
            if (Geometry2D.IsPolygonClockwise(triangle)) Array.Reverse(triangle);
            double Overlap(IEnumerable<System.Numerics.Vector2[]> polygons)
            {
                double triangleArea = 0;
                foreach (var polygon in polygons)
                    foreach (var overlap in Geometry2D.IntersectPolygons(triangle, polygon.Select(Local).ToArray()))
                    {
                        double twiceArea = 0;
                        for (int i = 1; i < overlap.Length - 1; i++)
                        {
                            var first = overlap[i] - overlap[0]; var second = overlap[i + 1] - overlap[0];
                            twiceArea += (double)first.X * second.Y - (double)first.Y * second.X;
                        }
                        triangleArea += Math.Abs(twiceArea) / (2 * scale * scale);
                    }
                return triangleArea;
            }
            if (MeasureZoneRoadOverlap && _roadLayout is not null)
            {
                double area = Overlap(_roadLayout.AtCell(cell.X, cell.Y).Select(road => road.Road));
                ZoneRoadOverlapArea += area;
                MaximumZoneTriangleRoadOverlapArea = Math.Max(MaximumZoneTriangleRoadOverlapArea, area);
            }
            if (MeasureZoneFootpathOverlap && _footpathLayout is not null)
            {
                double area = Overlap(_footpathLayout.AtCell(cell.X, cell.Y));
                ZoneFootpathOverlapArea += area;
                MaximumZoneTriangleFootpathOverlapArea = Math.Max(MaximumZoneTriangleFootpathOverlapArea, area);
            }
        }
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
                var a = new Vector3(start.X, _roadLayout.HeightAt(start) + WorldScale.Feet(3), start.Y);
                var b = new Vector3(end.X, _roadLayout.HeightAt(end) + WorldScale.Feet(3), end.Y);
                var delta = b - a;
                var transform = new Transform3D(Basis.LookingAt(delta, Vector3.Up) *
                    Basis.Identity.Scaled(new(0.13f, 0.18f, delta.Length() + 0.035f)), (a + b) / 2);
                BatchBox(material, transform);
                _bridgeRails.Add((transform, segment.Path));
                var lower = new Transform3D(Basis.LookingAt(delta, Vector3.Up) *
                    Basis.Identity.Scaled(new(segment.Type.Rank >= 2 ? .16f : .10f,
                        segment.Type.Rank >= 2 ? .60f : .10f, delta.Length() + .035f)),
                    (a + b) / 2 - Vector3.Up * .38f);
                BatchBox(material, lower);
                _bridgeRails.Add((lower, segment.Path));
            }
            Rail(segment.LeftA, segment.LeftB);
            Rail(segment.RightA, segment.RightB);
            void Post(float t)
            {
                foreach (var point in new[] { System.Numerics.Vector2.Lerp(segment.LeftA, segment.LeftB, t),
                    System.Numerics.Vector2.Lerp(segment.RightA, segment.RightB, t) })
                {
                    var transform = new Transform3D(Basis.Identity.Scaled(new(0.15f, WorldScale.Feet(3.5f), 0.15f)),
                        new(point.X, _roadLayout.HeightAt(point) + WorldScale.Feet(1.75f), point.Y));
                    BatchBox(material, transform);
                    _bridgeRails.Add((transform, segment.Path));
                    var cap = new Transform3D(Basis.Identity.Scaled(new(.21f, .07f, .21f)),
                        new(point.X, _roadLayout.HeightAt(point) + WorldScale.Feet(3.5f) + .035f, point.Y));
                    BatchBox(material, cap);
                    _bridgeRails.Add((cap, segment.Path));
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
            {
                var left = (segment.LeftA + segment.LeftB) / 2;
                var right = (segment.RightA + segment.RightB) / 2;
                foreach (float t in new[] { .25f, .75f })
                {
                    var p = System.Numerics.Vector2.Lerp(left, right, t);
                    var pier = new Transform3D(Basis.Identity.Scaled(new(.30f, top - bottom, .30f)),
                        new(p.X, (top + bottom) / 2, p.Y));
                    BatchBox(material, pier);
                    _bridgeRails.Add((pier, segment.Path));
                }
                var across = new Vector3(right.X - left.X, 0, right.Y - left.Y);
                var beam = new Transform3D(Basis.LookingAt(across, Vector3.Up) *
                    Basis.Identity.Scaled(new(.24f, .18f, across.Length())), new(midpoint.X, top - .09f, midpoint.Y));
                BatchBox(material, beam);
                _bridgeRails.Add((beam, segment.Path));
            }
        }
    }

    private void Tree(Vector3 at, Pos? anchor = null)
    {
        var appearance = TreeAppearance.At((int)(at.X * 5), (int)(at.Z * 5));
        TreeShapesDrawn.Add(appearance.Shape);
        _capturingBuilding?.TreeShapes.Add(appearance.Shape);
        bool overview = UseOverviewLod && anchor is null;
        if (overview) OverviewTreesDrawn++;
        foreach (var part in appearance.Parts(overview: overview))
        {
            float scale = 1f / _buildingModelScale;
            Box(at + new Vector3(part.Center.X, part.Center.Y, part.Center.Z) * scale,
                new Vector3(part.Size.X, part.Size.Y, part.Size.Z) * scale, part.Color, anchor);
            if (anchor is null)
            {
                var center = at + new Vector3(part.Center.X, part.Center.Y, part.Center.Z);
                var size = new Vector3(part.Size.X, part.Size.Y, part.Size.Z);
                RecordPickBox(_naturalPickGroups, new((int)MathF.Floor(at.X), (int)MathF.Floor(at.Z)),
                    new(center - size / 2, size));
            }
        }
    }

    private void Rock(Vector3 at, int variant, float scale, int turn)
    {
        var rotation = new Basis(Vector3.Up, turn * Mathf.Pi / 2);
        foreach (var part in RockArtwork.Parts(variant))
        {
            var transform = new Transform3D(rotation * Basis.Identity.Scaled(new Vector3(part.Size.X, part.Size.Y, part.Size.Z) * scale),
                at + rotation * (new Vector3(part.Center.X, part.Center.Y, part.Center.Z) * scale));
            BatchBox(part.Color, transform);
            RecordPickBox(_naturalPickGroups, new((int)MathF.Floor(at.X), (int)MathF.Floor(at.Z)), DefenseBounds(transform));
        }
    }

    private void DrawGround(GameSession session)
    {
        TerrainPixelsMilliseconds = RoadPixelsMilliseconds = 0;
        RoadMeshChunksDrawn = 0;
        RoadBorderTrianglesDrawn = 0;
        RoadBorderMilliseconds = 0;
        _visibleRiverFlow = false;
        var map = session.Game.Map;
        if (!ReferenceEquals(_groundMap, map) || _groundVersion != map.TerrainVersion || _groundRoadRevision != _roads.Revision ||
            _groundOverlay != session.Overlay || session.Overlay != MapOverlay.Off && _groundOverlayVersion != session.Game.AnalysisVersion)
        {
            if (!ReferenceEquals(_groundMap, map)) _cameraGroundFailed = false;
            _roadLayout = new RoadMeshLayout(map, RoadRenderingEnabled ? _roads.Paths : []);
            _roadSurfaces = new RoadSurfaceMeshes(_roadLayout, litGround: DepthStudyArtwork.UsesLighting(DepthTreatment),
                litInfrastructure: TerrainStudyFixture.LitInfrastructure(TerrainTreatment),
                edgeRelief: TerrainStudyFixture.EdgeRelief(TerrainTreatment));
            ValidatePreparedGround(session);
            ReconcileGroundEpoch(session);
            _groundMap = map; _groundVersion = map.TerrainVersion;
            _groundRoadRevision = _roads.Revision;
            _groundOverlay = session.Overlay; _groundOverlayVersion = session.Game.AnalysisVersion;
        }
        const int span = TerrainSprite.ChunkCells;
        TrimGpuResidency();
        for (int cy = _view.Y / span; cy <= _view.Bottom / span; cy++)
            for (int cx = _view.X / span; cx <= _view.Right / span; cx++)
            {
                if (!WaitingForGround((cx, cy), session)) MountGroundChunk(session, cx, cy);
            }
    }

    private void MountGroundChunk(GameSession session, int cx, int cy, bool publish = true)
    {
        var key = (cx, cy);
        bool mounted = _mountedGroundChunks.Contains(key);
        if (mounted && !_groundDrawPagesEnabled || publish && _pendingGroundPublication.Contains(key)) return;
        var map = session.Game.Map;
        const int span = TerrainSprite.ChunkCells;
        if (!_groundMaterials.TryGetValue(key, out var material))
        {
            long sampled = System.Diagnostics.Stopwatch.GetTimestamp();
            var packet = PrepareGroundPacket(session, key);
            var pixels = packet.Textures;
            if (pixels is null)
            {
                if (GroundTextureCachingEnabled && !_lazyOffscreenTextures)
                    throw new InvalidOperationException("Prepared ground has no terrain texture packet.");
                pixels = GroundTexturePacket.Prepare(map, cx, cy);
                GroundTexturePacketsBuilt++;
                if (GroundTextureCachingEnabled)
                {
                    _preparedGround[key] = packet with { Textures = pixels };
                    _groundBytes[key] = GroundBytes(_preparedGround[key]);
                }
            }
            TerrainPixelsMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(sampled).TotalMilliseconds;
            using var flowImage = Image.CreateFromData(span, span, false, Image.Format.Rgbaf, pixels.Flow);
            const int shoreSize = span + 2;
            using var shoreImage = Image.CreateFromData(shoreSize, shoreSize, false, Image.Format.Rgbaf, pixels.Shore);
            using var dataImage = Image.CreateFromData(pixels.Width, pixels.Height, false, Image.Format.Rgba8, pixels.Terrain);
            using var variationImage = Image.CreateFromData(WorldTerrainTexture.VariationPixels,
                WorldTerrainTexture.VariationPixels, false, Image.Format.L8, pixels.Variations);
            if (_terrainAtlas is null)
            {
                using var atlasImage = Image.CreateFromData(WorldTerrainTexture.AtlasWidth, WorldTerrainTexture.AtlasHeight,
                    false, Image.Format.Rgba8, WorldTerrainTexture.PrepareAtlas());
                _terrainAtlas = ImageTexture.CreateFromImage(atlasImage);
            }
            var shaderMaterial = new ShaderMaterial { Shader = _terrainShader };
            material = new GroundMaterial(ImageTexture.CreateFromImage(dataImage), ImageTexture.CreateFromImage(variationImage),
                ImageTexture.CreateFromImage(flowImage),
                ImageTexture.CreateFromImage(shoreImage), shaderMaterial, pixels.RiverFlow);
            _groundGpuBytes[key] = _groundGpuBytes.GetValueOrDefault(key) + pixels.NativeBytes;
            GroundUploadBytes += pixels.NativeBytes;
            shaderMaterial.SetShaderParameter("terrain_atlas", _terrainAtlas);
            shaderMaterial.SetShaderParameter("terrain_balanced", TerrainStudyFixture.EdgeRelief(TerrainTreatment));
            shaderMaterial.SetShaderParameter("terrain_data", material.Texture);
            shaderMaterial.SetShaderParameter("terrain_variation", material.Variation);
            shaderMaterial.SetShaderParameter("flow_image", material.Flow);
            shaderMaterial.SetShaderParameter("shore_image", material.Shore);
            shaderMaterial.SetShaderParameter("flow_clock", (float)WaterFlowRenderClock);
            _groundMaterials.Add(key, material);
        }
        material.Material.SetShaderParameter("terrain_balanced", TerrainStudyFixture.EdgeRelief(TerrainTreatment));
        if (publish) _visibleRiverFlow |= material.RiverFlow;
        if (!_roadChunks.TryGetValue(key, out var chunk))
        {
            chunk = CacheRoadChunk(session, cx, cy);
        }
        if (!_groundNodes.TryGetValue(key, out var groundNode))
        {
            groundNode = new Node3D();
            _groundRoot.AddChild(groundNode);
            _groundNodes[key] = groundNode;
            if (!_groundDrawPagesEnabled)
            {
                groundNode.AddChild(new MeshInstance3D { Mesh = chunk.Ground, MaterialOverride = material.Material });
                if (chunk.Roads is not null)
                    groundNode.AddChild(new MeshInstance3D { Mesh = chunk.Roads, MaterialOverride = _roadMaterial });
                if (chunk.Banks is not null)
                    groundNode.AddChild(new MeshInstance3D { Mesh = chunk.Banks, MaterialOverride = _cliffMaterial });
            }
        }
        else GroundNodesReused++;
        groundNode.Visible = publish;
        if (_groundDrawPagesEnabled && _stableGroundPages && GroundPageHasOwner(key) && publish)
        {
            _mountedGroundChunks.Add(key);
            mounted = true;
            SyncStableGroundPageVisibility();
        }
        if (_groundDrawPagesEnabled && _stableGroundPages && !GroundPageHasOwner(key))
            MarkGroundPageDirty(key);
        if (_groundDrawPagesEnabled && publish && !mounted)
        {
            _pendingGroundPublication.Add(key);
            MarkGroundPageDirty(key);
        }
        _groundGpuUse[key] = ++_cacheUse;
        if (!publish) return;
        if (_persistentResidency
            ? _groundDrawPages.TryGetValue(GroundPageKey(key), out var page) &&
                page.Surfaces.TryGetValue(key, out var surfaces) && surfaces[1] >= 0 ||
                _preparedGround.GetValueOrDefault(key)?.Roads is not null
            : _groundDrawPagesEnabled ? _preparedGround[key].Roads is not null : chunk.Roads is not null)
        {
            RoadMeshChunksDrawn++;
            RoadBorderTrianglesDrawn += chunk.RoadBorderTriangles;
        }
        _cliffEdges.AddRange(chunk.CutEdges);
        _exposedCutCenters.AddRange(chunk.CutCenters);
        _wallSamples.AddRange(chunk.WallSamples);
        ExposedCutTriangles += chunk.CutCenters.Count * 2;
        if (!_groundDrawPagesEnabled) _mountedGroundChunks.Add(key);
        if (material.RiverFlow && WaterMotionEnabled) _waterAnimating = true;
    }

    private RoadSurfaceMeshes.Chunk CacheRoadChunk(GameSession session, int x, int y)
    {
        var key = (x, y);
        if (_roadChunks.TryGetValue(key, out var cached)) return cached;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var prepared = PrepareGroundPacket(session, key);
        var chunk = prepared.Upload(geometry: !_groundDrawPagesEnabled);
        GroundChunksUploaded++;
        long bytes = MeshUploadBytes(prepared.Ground) + MeshUploadBytes(prepared.Roads) + MeshUploadBytes(prepared.Banks);
        if (!_groundDrawPagesEnabled) GroundUploadBytes += bytes;
        _groundGpuBytes[key] = _groundGpuBytes.GetValueOrDefault(key) + bytes;
        _groundGpuStamps[key] = GroundStamp(session, key);
        RoadPixelsMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        RoadBorderMilliseconds += chunk.RoadBorderMilliseconds;
        _roadChunks.Add(key, chunk);
        RecordGroundMetadata(key, chunk);
        _groundMeshes.Add(key, chunk.Ground);
        if (chunk.Banks is not null) _cliffMeshes.Add(key, chunk.Banks);
        foreach (var (index, faces) in chunk.Faces) _groundFaces[index] = faces;
        foreach (var (index, faces) in chunk.LandFaces) _zoneGroundFaces[index] = faces;
        return chunk;
    }

    private void OutlineRoad(GameMap map, IReadOnlyList<System.Numerics.Vector2> polygon, int color)
    {
        SelectionOutlineHeight = float.NegativeInfinity;
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            if (System.Numerics.Vector2.DistanceSquared(a, b) < 0.000001f) continue;
            Vector3 Point(System.Numerics.Vector2 p)
            {
                float height = RenderedGroundHeight(map, p.X, p.Y) + _indicatorThickness + 0.025f;
                SelectionOutlineHeight = Math.Max(SelectionOutlineHeight, height);
                return new(p.X, height, p.Y);
            }
            var start = Point(a); var end = Point(b); var delta = end - start;
            BatchBox(color, new Transform3D(Basis.LookingAt(delta, Vector3.Up) *
                Basis.Identity.Scaled(new(_indicatorThickness, _indicatorThickness, delta.Length() + _indicatorThickness)),
                (start + end) / 2));
        }
    }

    private void Outline(GameMap map, CellRect area, int color, float? surfaceHeight = null)
        => Outline(map, new Rect2(area.X, area.Y, area.Width, area.Height), color, surfaceHeight);

    private void Outline(GameMap map, Rect2 area, int color, float? surfaceHeight = null)
    {
        float x = area.Position.X, z = area.Position.Y, w = area.Size.X, d = area.Size.Y;
        SelectionOutlineHeight = float.NegativeInfinity;
        Vector3 Point(float px, float pz)
        {
            float height = surfaceHeight ?? RenderedGroundHeight(map, Math.Clamp(px, x + 0.001f, x + w - 0.001f),
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
        Edge(x, z, x + w, z, Math.Max(1, (int)Math.Ceiling(w * 4)));
        Edge(x, z + d, x + w, z + d, Math.Max(1, (int)Math.Ceiling(w * 4)));
        Edge(x, z, x, z + d, Math.Max(1, (int)Math.Ceiling(d * 4)));
        Edge(x + w, z, x + w, z + d, Math.Max(1, (int)Math.Ceiling(d * 4)));
    }

    private void OutlineProperty(GameMap map, IReadOnlyList<HouseOutlineEdge> edges, int color)
    {
        SelectionOutlineHeight = float.NegativeInfinity;
        foreach (var edge in edges)
        {
            Vector3 Point(float t)
            {
                float x = Mathf.Lerp(edge.Start.X, edge.End.X, t);
                float z = Mathf.Lerp(edge.Start.Y, edge.End.Y, t);
                float height = RenderedGroundHeight(map,
                    Math.Clamp(x, edge.Cell.X + 0.001f, edge.Cell.X + 0.999f),
                    Math.Clamp(z, edge.Cell.Y + 0.001f, edge.Cell.Y + 0.999f)) +
                    _indicatorThickness + 0.025f;
                SelectionOutlineHeight = Math.Max(SelectionOutlineHeight, height);
                return new(x, height, z);
            }
            var previous = Point(0);
            for (int step = 1; step <= 4; step++)
            {
                var next = Point(step / 4f);
                var delta = next - previous;
                BatchBox(color, new Transform3D(Basis.LookingAt(delta, Vector3.Up) *
                    Basis.Identity.Scaled(new(_indicatorThickness, _indicatorThickness,
                        delta.Length() + _indicatorThickness)), (previous + next) / 2));
                previous = next;
            }
        }
    }

    private float _buildingModelScale = 1;

    private void BuildBuilding(GameMap map, CellRect area, BuildingType type, int? previewRotation = null)
    {
        BuildingsDrawn++;
        if (_drawingPreview) PreviewBuildingsDrawn++;
        var anchor = new Pos(area.X, area.Y);
        var location = BuildingEntranceLayout.Center(map, area, type);
        var center = new Vector3(location.X, location.Y, location.Z);
        float ground = center.Y;
        int rotation = previewRotation ?? map.BuildingRotations.GetValueOrDefault(map.Index(area.X, area.Y));
        float modelScale = BuildingEntranceLayout.ModelScale(type, area, rotation);
        var modelArea = rotation % 2 == 0 ? area : new CellRect(area.X, area.Y, area.Height, area.Width);
        if (type.Zone == ZoneType.Residential)
            HouseFoundation(map, area, modelArea, rotation, center, type, anchor, modelScale);
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
        basis = basis.Scaled(Vector3.One * modelScale);
        _buildingTransform = new(basis, center - basis * center);
        var authoredArea = new CellRect(modelArea.X, modelArea.Y,
            Math.Max(1, (int)(modelArea.Width / modelScale)),
            Math.Max(1, (int)(modelArea.Height / modelScale)));
        _buildingModelScale = modelScale;
        try
        {
            if (UseOverviewLod && !type.IsService && type.Zone == ZoneType.Residential) OverviewHouse(authoredArea, type, anchor, center);
            else if (UseOverviewLod && !type.IsService && type.Zone is ZoneType.Commercial or ZoneType.Industrial)
                OverviewWorkplace(authoredArea, type, anchor, center);
            else BuildingModel(authoredArea, type, anchor, center);
        }
        finally { _buildingTransform = Transform3D.Identity; _buildingModelScale = 1; }
        DrawEntranceApproaches(map, area, type, anchor, previewRotation);
    }

    private void HouseFoundation(GameMap map, CellRect area, CellRect modelArea, int rotation,
        Vector3 center, BuildingType type, Pos anchor, float modelScale)
    {
        var appearance = HouseAppearance.At(type, anchor.X, anchor.Y);
        foreach (var part in appearance.BaseParts(
            Math.Max(1, (int)(modelArea.Width / modelScale)),
            Math.Max(1, (int)(modelArea.Height / modelScale)), type.Level))
        {
            float scale = modelScale;
            var offset = rotation switch
            {
                1 => new Vector2(-part.Center.Z, part.Center.X) * scale,
                2 => new Vector2(-part.Center.X, -part.Center.Z) * scale,
                3 => new Vector2(part.Center.Z, -part.Center.X) * scale,
                _ => new Vector2(part.Center.X, part.Center.Z) * scale,
            };
            float width = (rotation % 2 == 0 ? part.Size.X : part.Size.Z) * scale;
            float depth = (rotation % 2 == 0 ? part.Size.Z : part.Size.X) * scale;
            Support(center.X + offset.X - width / 2, center.X + offset.X + width / 2,
                center.Z + offset.Y - depth / 2, center.Z + offset.Y + depth / 2, true);
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
                Box(new((segmentLeft + segmentRight) / 2, (center.Y + lower) / 2 - 0.02f,
                        (segmentBack + segmentFront) / 2),
                    new(segmentRight - segmentLeft, drop + 0.04f, segmentFront - segmentBack),
                    HouseAppearance.StoneColor, anchor, foundation: true);
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

    private void DrawFootpaths(GameSession session)
    {
        if (!MeasureFootpathFaces)
        {
            DrawPathPages(session);
            return;
        }
        var map = session.Game.Map;
        long prepareStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_footpathLayout is null) return;
        _pathSession = session;
        _groundPathsPending = PathGroundPending(session);
        if (_groundPathsPending) return;
        var source = CapturePathSource(map);
        FootpathPrepareMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(prepareStarted).TotalMilliseconds;
        QueuePathMesh(session, source);
    }

    private void House(CellRect area, BuildingType type, Pos anchor, Vector3 center)
    {
        var appearance = HouseAppearance.At(type, anchor.X, anchor.Y);
        var dimensions = appearance.OnPlot(area.Width, area.Height, type.Level);
        var blocks = appearance.BlocksOnPlot(area.Width, area.Height, type.Level);
        foreach (var part in appearance.BaseParts(area.Width, area.Height, type.Level))
            Box(center + new Vector3(part.Center.X, part.Center.Y, part.Center.Z),
                new(part.Size.X, part.Size.Y, part.Size.Z), part.Color, anchor);
        for (int index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            var origin = center + new Vector3(block.X, 0, block.Z);
            if (DepthStudyArtwork.UsesRelief(DepthTreatment))
            {
                foreach (var part in DepthStudyArtwork.HouseParts(appearance, block, index == 0))
                    Box(origin + new Vector3(part.Center.X, part.Center.Y, part.Center.Z),
                        new(part.Size.X, part.Size.Y, part.Size.Z), part.Color, anchor, pick: !part.Roof);
                for (int step = 0; step < block.RoofSteps; step++)
                {
                    var course = block.RoofCourse(step);
                    PickVolume(origin + new Vector3(course.Center.X, course.Center.Y, course.Center.Z),
                        new(course.Size.X * DepthStudyArtwork.RoofReliefScale, course.Size.Y,
                            course.Size.Z * DepthStudyArtwork.RoofReliefScale), anchor);
                }
                if (index == 0 && appearance.Chimney)
                    Box(origin + new Vector3(-block.Width * .25f,
                            block.Height + block.RoofRise - .04f, -block.Depth * .15f),
                        new(.12f, .24f, .12f), HouseAppearance.StoneColor, anchor);
                continue;
            }
            Box(origin + new Vector3(0, block.Height / 2, 0),
                new(block.Width - 2 * HouseAppearance.WallCoreInset, block.Height,
                    block.Depth - 2 * HouseAppearance.WallCoreInset), appearance.PlasterColor, anchor);
            foreach (var part in appearance.Parts(block))
                Box(origin + new Vector3(part.Center.X, part.Center.Y, part.Center.Z),
                    new(part.Size.X, part.Size.Y, part.Size.Z), part.Color, anchor, pick: !part.Roof);
            // Each tiled course is a solid cuboid; pick its exact union, not every constituent block.
            for (int step = 0; step < block.RoofSteps; step++)
            {
                var course = block.RoofCourse(step);
                PickVolume(origin + new Vector3(course.Center.X, course.Center.Y, course.Center.Z),
                    new(course.Size.X, course.Size.Y, course.Size.Z), anchor);
            }
            HouseOpenings(origin, block, index == 0, anchor);
            if (appearance.Style is HouseStyle.Shanty or HouseStyle.WallHouse)
                for (int floor = 0; floor < block.Stories; floor++)
                {
                    float y = (floor + 0.7f) * block.Height / block.Stories;
                    float offset = floor % 2 == 0 ? -0.035f : 0.035f;
                    Box(origin + new Vector3(offset, y, block.Depth / 2 + 0.025f),
                        new(block.Width + 0.06f, 0.055f, 0.08f), HouseAppearance.TimberColor, anchor);
                    Box(origin + new Vector3(block.Width / 2 + 0.025f, y - 0.08f, offset),
                        new(0.08f, 0.045f, block.Depth + 0.06f), HouseAppearance.SillColor, anchor);
                }
            if (index == 0 && appearance.Chimney)
                Box(origin + new Vector3(-block.Width * 0.25f,
                        block.Height + block.RoofRise - 0.04f, -block.Depth * 0.15f),
                    new(0.12f, 0.24f, 0.12f), HouseAppearance.StoneColor, anchor);
        }
    }

    private void HouseYard(GameMap map, HouseYard yard, Pos anchor)
    {
        var origin = new Vector3(yard.Cell.X + 0.5f, 0, yard.Cell.Y + 0.5f);
        var forward = new Vector3(yard.Direction.X, 0, yard.Direction.Y);
        var right = new Vector3(forward.Z, 0, -forward.X);
        var basis = new Basis(right, Vector3.Up, forward);
        var appearance = HouseAppearance.At(anchor.X, anchor.Y, map.BuildingAt(anchor.X, anchor.Y)!.Level);
        void Detail(float x, float z, Vector3 size, int color, float lift = 0)
        {
            var point = origin + right * x + forward * z;
            float ground = RenderedGroundHeight(map, point.X, point.Z);
            point.Y = ground + 0.012f + lift + size.Y / 2;
            var transform = _buildingTransform;
            _buildingTransform = new Transform3D(basis, point - basis * point);
            try { Box(point, size, color, anchor); }
            finally { _buildingTransform = transform; }
        }

        for (int row = 0; row < 2; row++)
            for (int column = 0; column < 2; column++)
                Detail(column * 0.5f - 0.25f, row * 0.5f - 0.25f, new(0.5f, 0.014f, 0.5f),
                    ((row + column + (int)appearance.Style) % 3) switch
                    {
                        0 => HouseAppearance.YardGrassColor, 1 => HouseAppearance.YardGrassShadeColor,
                        _ => HouseAppearance.YardGrassLightColor,
                    }, -0.012f);
        Detail(-HouseYardLayout.BedCenter, 0.02f, new(HouseYardLayout.BedWidth, 0.025f, 0.66f), HouseAppearance.GardenSoilColor, 0.018f);
        Detail(HouseYardLayout.BedCenter, 0.10f, new(HouseYardLayout.BedWidth, 0.025f, 0.5f), HouseAppearance.GardenSoilColor, 0.018f);
        for (int plant = 0; plant < 3; plant++)
        {
            float z = -0.20f + plant * 0.21f;
            Detail(-HouseYardLayout.BedCenter, z, new(0.14f, 0.055f, 0.12f), HouseAppearance.GardenLeafColor, 0.04f);
            Detail(-HouseYardLayout.BedCenter, z, new(0.10f, 0.035f, 0.07f), HouseAppearance.GardenCropColor, 0.09f);
            float flowerZ = -0.07f + plant * 0.16f;
            Detail(HouseYardLayout.BedCenter, flowerZ, new(0.14f, 0.06f, 0.12f), HouseAppearance.GardenLeafColor, 0.04f);
            int flower = ((plant + (int)appearance.Style) % 3) switch
            {
                0 => HouseAppearance.FlowerRoseColor, 1 => HouseAppearance.FlowerGoldColor,
                _ => HouseAppearance.FlowerCreamColor,
            };
            Detail(HouseYardLayout.BedCenter, flowerZ, new(0.12f, 0.035f, 0.08f), flower, 0.095f);
            Detail(HouseYardLayout.BedCenter, flowerZ, new(0.055f, 0.025f, 0.055f), HouseAppearance.FlowerGoldColor, 0.13f);
        }

        foreach (int side in new[] { -1, 1 })
        {
            Detail(side * 0.45f, 0, new(0.035f, 0.035f, 0.9f), HouseAppearance.TimberColor, 0.13f);
            for (int post = 0; post < 3; post++)
                Detail(side * 0.45f, -0.44f + post * 0.44f,
                    new(0.045f, 0.20f, 0.045f), HouseAppearance.TimberColor);
            Detail(side * 0.38f, 0.45f, new(0.14f, 0.035f, 0.035f),
                HouseAppearance.TimberColor, 0.13f);
        }
    }

    private void HouseOpenings(Vector3 center, HouseBlock block, bool entrance, Pos anchor)
    {
        void Face(bool alongX, int side)
        {
            float span = alongX ? block.Width : block.Depth;
            var bays = BuildingDetailLayout.HalfTimberFacade(span);
            Vector3 Position(float along, float y, float outwards) => center + (alongX
                ? new Vector3(along, y, side * (block.Depth / 2 + outwards))
                : new Vector3(side * (block.Width / 2 + outwards), y, along));
            Vector3 Size(float across, float tall, float thick) => alongX
                ? new(across, tall, thick) : new(thick, tall, across);
            for (int floor = 0; floor < block.Stories; floor++)
            {
                var panel = block.WallPanel(floor);
                for (int bay = 0; bay < bays.Count; bay++)
                {
                    float x = bays.Center(bay);
                    if (entrance && alongX && side == 1 && floor == 0 && bay == bays.Count / 2)
                    {
                        float doorHeight = Math.Min(0.30f, panel.Height);
                        Box(Position(x, panel.Bottom + doorHeight / 2, -0.005f),
                            Size(BuildingDetailLayout.DoorWidth, doorHeight, 0.04f), 0x514637, anchor);
                        continue;
                    }
                    if (alongX && side == -1 || !alongX && bay != bays.Count / 2) continue;
                    float y = panel.Bottom + panel.Height / 2;
                    Box(Position(x, y, -0.015f), Size(0.14f, 0.14f, 0.02f), 0x35494b, anchor);
                }
            }
        }
        Face(true, -1); Face(true, 1); Face(false, -1); Face(false, 1);
    }

    private void Workplace(CellRect area, BuildingType type, Pos anchor, Vector3 center)
    {
        var look = WorkplaceAppearance.At(type, anchor.X, anchor.Y, area.Width, area.Height);
        float w = look.Width, d = look.Depth, h = look.Height;
        var body = center + new Vector3(0, 0, -0.06f);
        Box(center + new Vector3(0, 0.025f, 0), new(w + 0.06f, 0.05f, d + 0.10f),
            HouseAppearance.StoneColor, anchor);
        void Gable(Vector3 p, float width, float depth, float height, float rise)
        {
            var transform = _buildingTransform;
            if (look.CrosswiseRoof)
            {
                var basis = new Basis(Vector3.Back, Vector3.Up, Vector3.Left);
                _buildingTransform *= new Transform3D(basis, p - basis * p);
            }
            try
            {
                Roof(p + new Vector3(0, height, 0),
                    (look.CrosswiseRoof ? depth : width) + 0.18f,
                    (look.CrosswiseRoof ? width : depth) + 0.18f, rise, look.RoofColor, anchor, look.WallColor);
                Box(p + new Vector3(0, height + rise - 0.01f, 0),
                    new((look.CrosswiseRoof ? depth : width) + 0.18f, 0.03f, 0.055f),
                    HouseAppearance.At(anchor.X + 3, anchor.Y + 11, type.Level).RoofCapColor, anchor);
            }
            finally { _buildingTransform = transform; }
        }
        void Counter(float z, float span, float offset = 0)
        {
            Box(center + new Vector3(offset, 0.16f, z), new(span, 0.18f, 0.12f), HouseAppearance.TimberColor, anchor);
            Box(center + new Vector3(offset, 0.26f, z), new(span + 0.025f, 0.035f, 0.15f), HouseAppearance.SillColor, anchor);
            foreach (float x in BuildingDetailLayout.Centers(span - 0.04f, 0.08f, 0.13f))
            {
                int color = CellHash.Pick(anchor.X + (int)(x * 100), anchor.Y, 3) switch
                {
                    0 => 0xb19b64, 1 => 0x8c9c68, _ => 0xa87758,
                };
                Box(center + new Vector3(x + offset, 0.30f, z), new(0.08f, 0.055f, 0.085f), color, anchor);
            }
        }
        void Canvas(float z, float span, float canopyDepth, float y)
        {
            int stripes = Math.Max(4, (int)(span / 0.11f));
            float pitch = span / stripes;
            for (int stripe = 0; stripe < stripes; stripe++)
                Box(center + new Vector3(-span / 2 + (stripe + 0.5f) * pitch, y, z),
                    new(pitch, 0.04f, canopyDepth), stripe % 2 == 0 ? look.CanvasColor : 0xc9bf9f, anchor);
            foreach (int side in new[] { -1, 1 })
                Box(center + new Vector3(side * (span / 2 - 0.025f), y / 2, z + canopyDepth / 2 - 0.025f),
                    new(0.035f, y, 0.035f), HouseAppearance.TimberColor, anchor);
        }
        void Barrel(Vector3 p)
        {
            Box(p + new Vector3(0, 0.105f, 0), new(0.13f, 0.21f, 0.13f), 0x967a56, anchor);
            foreach (float y in new[] { 0.05f, 0.16f })
                Box(p + new Vector3(0, y, 0), new(0.14f, 0.022f, 0.14f), 0x5f6259, anchor);
            Box(p + new Vector3(0, 0.213f, 0), new(0.11f, 0.012f, 0.11f), HouseAppearance.SillColor, anchor);
        }
        if (look.Form == WorkplaceForm.Stall)
        {
            Box(body + new Vector3(0, 0.17f, -d * 0.3f), new(w * 0.8f, 0.34f, d * 0.30f),
                look.WallColor, anchor);
            Gable(body + new Vector3(0, 0, -d * 0.3f), w * 0.8f, d * 0.30f, 0.34f, 0.13f);
            Canvas(d * 0.15f, w + 0.04f, d * 0.62f, h + 0.06f);
            Counter(d * 0.39f, w * 0.82f);
            Barrel(center + new Vector3(-w * 0.36f, 0.03f, -d * 0.32f));
            return;
        }
        bool shop = look.Form is WorkplaceForm.Shop or WorkplaceForm.MarketHall or WorkplaceForm.SupplyShop;
        float bodyDepth = Math.Max(.45f, d * .72f);
        FacadedBody(body, w, bodyDepth, h, 1, look.WallColor, true, anchor);
        Gable(body, w, bodyDepth, h, look.RoofRise);
        // One broad doorway bay remains unobstructed; trade takes place under a separate low canopy.
        if (shop)
        {
            Canvas(d * 0.31f, w * 0.88f, d * 0.35f, h * 0.70f);
            float counterWidth = (w * 0.88f - 0.22f) / 2;
            Counter(d * 0.40f, counterWidth, -(0.22f + counterWidth) / 2);
            Counter(d * 0.40f, counterWidth, (0.22f + counterWidth) / 2);
            var sign = center + new Vector3(w * 0.34f, h * 0.83f, d * 0.33f);
            Box(sign, new(0.15f, 0.12f, 0.035f), HouseAppearance.TimberColor, anchor);
            Box(sign + new Vector3(0, 0, 0.022f), new(0.075f, 0.04f, 0.015f), 0xc5ab70, anchor);
            if (look.Form == WorkplaceForm.MarketHall)
                Barrel(center + new Vector3(-w * 0.4f, 0.025f, -d * 0.25f));
            if (look.Form == WorkplaceForm.SupplyShop)
                foreach (float x in new[] { -w * 0.30f, w * 0.30f })
                {
                    Barrel(center + new Vector3(x, 0.025f, -d * 0.30f));
                    Box(center + new Vector3(x, 0.16f, d * 0.10f), new(0.18f, 0.24f, 0.17f), 0x967a56, anchor);
                }
            return;
        }
        if (look.Form == WorkplaceForm.Armory)
        {
            for (int rack = 0; rack < 3; rack++)
            {
                if (rack == 1) continue;
                float x = (rack - 1) * w * 0.25f;
                Box(center + new Vector3(x, 0.25f, d * 0.38f), new(0.035f, 0.48f, 0.035f), 0x967a56, anchor);
                Box(center + new Vector3(x, 0.52f, d * 0.38f), new(0.07f, 0.12f, 0.04f), 0x768183, anchor);
                Box(center + new Vector3(x, 0.22f, d * 0.41f), new(0.13f, 0.18f, 0.035f), look.CanvasColor, anchor);
            }
            return;
        }
        if (look.Form is WorkplaceForm.Smithy or WorkplaceForm.Forge)
        {
            var hearth = center + new Vector3(-w * 0.28f, 0, d * 0.24f);
            Box(hearth + new Vector3(0, .04f, 0), new(.20f, .05f, .18f), HouseAppearance.StoneColor, anchor);
            foreach (int side in new[] { -1, 1 })
                Box(hearth + new Vector3(side * .085f, .165f, 0), new(.03f, .20f, .18f), HouseAppearance.StoneColor, anchor);
            Box(hearth + new Vector3(0, .165f, -.0725f), new(.14f, .20f, .035f), HouseAppearance.StoneColor, anchor);
            Box(hearth + new Vector3(0, .285f, 0), new(.20f, .04f, .18f), HouseAppearance.StoneColor, anchor);
            Box(hearth + new Vector3(0, .14f, -.04f), new(.14f, .15f, .015f), 0x343932, anchor);
            Box(hearth + new Vector3(0, .092f, .015f), new(.12f, .045f, .035f), 0xb87745, anchor);
            var flue = body + new Vector3(-w * 0.28f, h + 0.12f, 0);
            Box(flue, new(0.13f, 0.32f, 0.14f), HouseAppearance.StoneColor, anchor);
            Box(flue + new Vector3(0, 0.17f, 0), new(0.17f, 0.04f, 0.17f), 0x685d4c, anchor);
            var anvil = center + new Vector3(w * 0.22f, 0, d * 0.37f);
            Box(anvil + new Vector3(0, 0.08f, 0), new(0.11f, 0.16f, 0.11f), HouseAppearance.TimberColor, anchor);
            Box(anvil + new Vector3(0, 0.17f, 0), new(0.20f, 0.055f, 0.095f), 0x5a6566, anchor);
            Box(anvil + new Vector3(0.11f, 0.17f, 0), new(0.065f, 0.032f, 0.06f), 0x768183, anchor);
            return;
        }
        if (look.Form == WorkplaceForm.Mill)
        {
            float radius = area.Area > 1 ? 0.21f : 0.17f;
            var wheel = body + new Vector3(w / 2 + 0.055f, radius + 0.05f, 0.02f);
            Box(wheel, new(0.13f, 0.055f, radius * 1.7f), HouseAppearance.TimberColor, anchor);
            Box(wheel, new(0.13f, radius * 1.7f, 0.055f), HouseAppearance.TimberColor, anchor);
            for (int step = 0; step < 8; step++)
            {
                float angle = step * Mathf.Tau / 8;
                var rim = wheel + new Vector3(0, Mathf.Sin(angle) * radius, Mathf.Cos(angle) * radius);
                Box(rim, new(0.12f, 0.08f, 0.08f), 0x8b7252, anchor);
            }
            Box(wheel + new Vector3(0.07f, 0, 0), new(0.05f, 0.07f, 0.07f), 0x5f6259, anchor);
            foreach (float x in new[] { -w * 0.25f, 0f })
                Box(center + new Vector3(x, 0.10f, d * 0.32f), new(0.12f, 0.17f, 0.13f), 0xb2a273, anchor);
            return;
        }
        if (look.Form == WorkplaceForm.Cooper)
        {
            Barrel(center + new Vector3(-w * 0.32f, 0.025f, d * 0.32f));
            Barrel(center + new Vector3(w * 0.32f, 0.025f, d * 0.34f));
        }
        else
        {
            Box(center + new Vector3(-w * .28f, 0.18f, d * 0.34f), new(w * 0.40f, 0.04f, 0.16f),
                HouseAppearance.SillColor, anchor);
            foreach (int side in new[] { -1, 1 })
                Box(center + new Vector3(-w * .28f + side * w * .17f, 0.10f, d * 0.34f), new(0.04f, 0.20f, 0.1f),
                    HouseAppearance.TimberColor, anchor);
            for (int plank = 0; plank < 3; plank++)
                Box(center + new Vector3(-w * 0.28f, 0.06f + plank * 0.035f, d * 0.16f),
                    new(w * 0.40f, 0.03f, 0.07f), plank % 2 == 0 ? 0x967a56 : 0xb3a58a, anchor);
        }
    }

    private void BuildingModel(CellRect area, BuildingType type, Pos anchor, Vector3 center)
    {
        var dimensions = BuildingEntranceLayout.Body(area.Width, area.Height, type.Level);
        float w = dimensions.Width, d = dimensions.Depth, h = dimensions.Height;
        if (type.Name is "Archer Barracks" or "Guard Post")
        {
            bool archers = type.Name == "Archer Barracks";
            float height = archers ? 0.65f : 0.85f;
            FacadedBody(center, w, d * .70f, height, 1, archers ? 0xc9bf9f : 0x99917c, archers, anchor);
            Roof(center + Vector3.Up * height, w + .18f, d * .70f + .18f, .24f, 0x687574, anchor);
            Box(center + new Vector3(w * 0.35f, height + 0.32f, 0), new(0.035f, 0.65f, 0.035f), 0x79634b, anchor);
            Box(center + new Vector3(w * 0.35f + 0.12f, height + 0.48f, 0), new(0.24f, 0.18f, 0.035f),
                archers ? 0x59777d : 0x996454, anchor);
            if (archers)
                for (int target = -1; target <= 1; target++)
                {
                    if (target == 0) continue;
                    var at = center + new Vector3(target * w * 0.24f, 0.15f, d * 0.43f);
                    Box(at, new(0.15f, 0.24f, 0.05f), 0xa69666, anchor);
                    Box(at + new Vector3(0, 0, 0.028f), new(0.07f, 0.09f, 0.015f), 0x996454, anchor);
                }
            else
                Box(center + new Vector3(-w * 0.25f, 0.22f, d * 0.43f), new(0.18f, 0.30f, 0.045f), 0x59777d, anchor);
            return;
        }
        if (type.Name == "Tavern") h = Math.Max(h, 0.85f);
        if (type.Zone == ZoneType.Residential)
        {
            House(area, type, anchor, center);
            return;
        }
        if (type.Zone is ZoneType.Commercial or ZoneType.Industrial)
        {
            Workplace(area, type, anchor, center);
            return;
        }
        if (type.Name == "Woodlot")
        {
            foreach (var part in WoodlotArtwork.Parts(area.Width, area.Height))
                Box(center + new Vector3(part.Center.X, part.Center.Y, part.Center.Z),
                    new(part.Size.X, part.Size.Y, part.Size.Z), part.Color, anchor);
            return;
        }
        Box(center + new Vector3(0, 0.035f, 0), new(area.Width * 0.93f, 0.06f, area.Height * 0.93f), 0x8c8b74, anchor);
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
        if (type.Name is "Village Green" or "Charcoal Burners")
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
                Box(center + new Vector3(0, .81f, 0), new(w + .2f, .08f, .34f), 0xa4a599, anchor);
                foreach (int side in new[] { -1, 1 })
                    Box(center + new Vector3(0, .91f, side * .14f), new(w + .2f, .12f, .06f), 0xa4a599, anchor);
                Box(center + new Vector3(0, .875f, 0), new(w, .012f, .2f), 0x456d83, anchor);
            }
            else
            {
                foreach (int side in new[] { -1, 1 })
                {
                    Box(center + new Vector3(0, .22f, side * .21f), new(.5f, .44f, .08f), 0xa4a599, anchor);
                    Box(center + new Vector3(side * .21f, .22f, 0), new(.08f, .44f, .34f), 0xa4a599, anchor);
                    Box(center + new Vector3(0, .48f, side * .25f), new(.58f, .08f, .08f), 0xa4a599, anchor);
                    Box(center + new Vector3(side * .25f, .48f, 0), new(.08f, .08f, .42f), 0xa4a599, anchor);
                }
                Box(center + new Vector3(0, .2f, 0), new(.34f, .02f, .34f), 0x304b53, anchor);
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
            int material = 0xa4a599;
            int battlement = 0xb2b5a7;
            const float wallThickness = 0.18f;
            float towerInset = (BuildingDetailLayout.TowerWidth - wallThickness) / 2;
            if (palisade)
            {
                foreach (var part in PalisadeArtwork.Parts(w, d, CellHash.Pick(anchor.X, anchor.Y, 1_000_003)))
                    Box(center + new Vector3(part.Center.X, part.Center.Y, part.Center.Z),
                        new(part.Size.X, part.Size.Y, part.Size.Z), part.Color, anchor);
            }
            else
            {
                float cornerReach = BuildingDetailLayout.TowerWidth - wallThickness / 2;
                bool gaol = type.Name == "Gaol";
                // End wall runs at tower bodies instead of sharing exposed stone faces.
                foreach (int side in new[] { -1, 1 })
                {
                    float span = gaol && side < 0 ? w + wallThickness : w - cornerReach * 2;
                    if (span > 0) Box(center + new Vector3(0, .38f, side * d / 2),
                        new(span, .75f, wallThickness), material, anchor);
                    float depthSpan = gaol ? d - cornerReach - wallThickness / 2 : d - cornerReach * 2;
                    if (depthSpan > 0) Box(center + new Vector3(side * w / 2, .38f,
                        gaol ? (wallThickness / 2 - cornerReach) / 2 : 0),
                        new(wallThickness, .75f, depthSpan), material, anchor);
                }
                foreach (float x in BuildingDetailLayout.Centers(Math.Max(0, w - 1.02f), 0.16f, 0.3f))
                    foreach (int side in new[] { -1, 1 })
                        Box(center + new Vector3(x, 0.83f, side * d / 2), new(0.16f, 0.15f, wallThickness), battlement, anchor);
                foreach (float z in BuildingDetailLayout.Centers(Math.Max(0, d - 1.02f), 0.16f, 0.3f))
                    foreach (int side in new[] { -1, 1 })
                        Box(center + new Vector3(side * w / 2, 0.83f, z), new(wallThickness, 0.15f, 0.16f), battlement, anchor);
            }
            Box(center + new Vector3(0, 0.25f, d / 2 + BuildingEntranceLayout.GateFrontOffset - .0125f),
                new(BuildingEntranceLayout.GateWidth, 0.46f, 0.025f),
                palisade ? PalisadeArtwork.BarkShadeColor : 0x584738, anchor);
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
        w = Math.Max(.45f, w);
        d = Math.Max(.45f, d);
        int wall = castle || church ? 0xa4a599 : 0xc6bf9c;
        int roof = church ? 0x586b71 : 0x956b50;
        FacadedBody(center, w, d, h, type.Level > 1 ? 2 : 1, wall, !castle && !church, anchor);
        Roof(center + new Vector3(0, h, 0), w + .18f, d + .18f, .35f, roof, anchor);
        foreach (float x in BuildingDetailLayout.Centers(w + 0.12f, 0.22f, 0.24f))
            Box(center + new Vector3(x, h + 0.36f, 0), new(0.22f, 0.035f, 0.08f), roof, anchor);
        if (church || type.Name == "Fire Watch")
        {
            float towerHeight = type.Name == "Chapel" ? 1.3f : type.Name == "Fire Watch" ? 1.45f : 1.6f;
            var tower = center + new Vector3(-w / 2 + .22f, 0, -d / 2 + .22f);
            FacadedBody(tower, .56f, .56f, towerHeight, 3, wall, false, anchor, plinth: false, entrance: false);
            Roof(tower + new Vector3(0, towerHeight, 0), 0.64f, 0.64f, 0.45f, roof, anchor);
        }
        if (type.Name == "Tavern")
            Box(center + new Vector3(0, 0.42f, d / 2 + 0.17f), new(w * 0.9f, 0.06f, 0.35f), 0x64808a, anchor);
        if (type.Service == ServiceKind.Health)
        {
            Box(center + new Vector3(0, 0.49f, d / 2 + 0.03f), new(0.16f, 0.04f, 0.035f), 0xb86a64, anchor);
            Box(center + new Vector3(0, 0.49f, d / 2 + 0.03f), new(0.04f, 0.16f, 0.035f), 0xb86a64, anchor);
        }
    }
}
