using Godot;
using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private const int MaximumCachedBuildings = 4096;
    private readonly record struct SurfaceVertex(int Color, Vector3 Point, Vector3 Normal, Vector2 Uv);
    private sealed class CachedBuilding(CellRect area, BuildingType type, int rotation, HouseYard? yard)
    {
        internal CellRect Area { get; } = area;
        internal BuildingType Type { get; } = type;
        internal int Rotation { get; } = rotation;
        internal HouseYard? Yard { get; } = yard;
        internal readonly Dictionary<int, List<Transform3D>> Batches = [];
        internal readonly Dictionary<int, float[]> InstanceBuffers = [];
        internal readonly List<SurfaceVertex> Roof = [];
        internal Dictionary<int, SurfaceVertex[]> RoofGroups = [];
        internal readonly List<SurfaceVertex> Dirt = [];
        internal (Pos Owner, Vector3 Threshold)[] Entrances = [];
        internal readonly HashSet<TreeShape> TreeShapes = [];
        internal (Aabb Bounds, Pos Anchor, int Color)[] Boxes = [], Picks = [];
        internal (Aabb Bounds, Pos Anchor)[] Foundations = [];
        internal (Vector3 A, Vector3 B, Vector3 C, Pos Anchor)[] RoofFaces = [];
        internal (Transform3D Transform, Pos Anchor)[] Highlights = [];
    }
    private readonly Dictionary<Pos, CachedBuilding> _buildingCache = [];
    private readonly Dictionary<int, List<(int Start, float[] Buffer)>> _sceneInstanceSpans = [];
    private Dictionary<int, List<(int Start, float[] Buffer)>> _cachedInstanceSpans =>
        _objectCapture?.InstanceSpans ?? _sceneInstanceSpans;
    private CachedBuilding? _capturingBuilding;
    private readonly List<(Aabb Bounds, Pos Anchor)> _sceneFoundations = [];
    private List<(Aabb Bounds, Pos Anchor)> _foundations => _objectCapture?.Foundations ?? _sceneFoundations;
    internal IReadOnlyList<Aabb> BuildingFoundationVolumes(Pos anchor)
    {
        CompletePendingFootpaths();
        return _sceneFoundations.Concat(VisibleObjectData.SelectMany(chunk => chunk.Geometry.Foundations))
            .Where(item => item.Anchor == anchor).Select(item => item.Bounds).ToArray();
    }
    private GameMap? _buildingCacheMap;
    private int _buildingCacheRoadRevision = -1, _buildingCacheTerrainVersion = -1;
    private CellRect? _buildingCacheBounds;
    internal int BuildingsReusedLastBuild { get; private set; }
    internal bool ValidateCityInstanceBuffers { get; set; }
    internal IReadOnlyList<(Aabb Bounds, Pos Anchor, int Color)> BuildingGeometry => SceneBoxes.ToArray();
    internal IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C, Pos Anchor)> BuildingRoofGeometry => SceneRoofFaces.ToArray();

    internal void InvalidateStaticScene(bool discardBuildingCache)
    {
        if (discardBuildingCache)
        {
            _buildingCache.Clear();
            _objectCacheEpoch++;
        }
        _version = -1;
    }

    private void RefreshBuildingCache(GameMap map, int roadRevision)
    {
        BuildingsReusedLastBuild = 0;
        if (!ReferenceEquals(_buildingCacheMap, map) || _buildingCacheRoadRevision != roadRevision ||
            _buildingCacheTerrainVersion != map.TerrainVersion || _buildingCacheBounds != map.SettlementBounds)
        {
            _buildingCache.Clear();
            _buildingCacheMap = map;
            _buildingCacheRoadRevision = roadRevision;
            _buildingCacheTerrainVersion = map.TerrainVersion;
            _buildingCacheBounds = map.SettlementBounds;
        }
        else if (_buildingCache.Count >= MaximumCachedBuildings)
            foreach (var pair in _buildingCache.Where(pair => pair.Value.Area.Right < _view.X ||
                pair.Value.Area.X > _view.Right || pair.Value.Area.Bottom < _view.Y || pair.Value.Area.Y > _view.Bottom).ToArray())
                _buildingCache.Remove(pair.Key);
    }

    private void Building(GameMap map, CellRect area, BuildingType type, int? previewRotation = null)
    {
        if (_drawingPreview || _objectCapture is not null || UseOverviewLod)
        {
            BuildBuilding(map, area, type, previewRotation);
            return;
        }
        var anchor = new Pos(area.X, area.Y);
        int rotation = previewRotation ?? map.BuildingRotations.GetValueOrDefault(map.Index(area.X, area.Y));
        HouseYard? yard = _houseYards.TryGetValue(anchor, out var extension) ? extension : null;
        if (_buildingCache.TryGetValue(anchor, out var cached) && cached.Area == area &&
            ReferenceEquals(cached.Type, type) && cached.Rotation == rotation && cached.Yard == yard)
        {
            BuildingsDrawn++;
            BuildingsReusedLastBuild++;
            foreach (var pair in cached.Batches)
            {
                if (!_boxBatches.TryGetValue(pair.Key, out var batch)) _boxBatches.Add(pair.Key, batch = []);
                if (!_cachedInstanceSpans.TryGetValue(pair.Key, out var spans))
                    _cachedInstanceSpans.Add(pair.Key, spans = []);
                spans.Add((batch.Count, cached.InstanceBuffers[pair.Key]));
                batch.AddRange(pair.Value);
            }
            foreach (var (color, vertices) in cached.RoofGroups)
            {
                if (!_roofBatches.TryGetValue(color, out var batch)) _roofBatches.Add(color, batch = []);
                batch.AddRange(vertices);
            }
            foreach (var vertex in cached.Dirt) AppendDirtVertex(vertex);
            DoorApproachTrianglesDrawn += cached.Dirt.Count / 3;
            _entranceThresholds.AddRange(cached.Entrances);
            _boxes.AddRange(cached.Boxes);
            _foundations.AddRange(cached.Foundations);
            _pickBoxes.AddRange(cached.Picks);
            _roofFaces.AddRange(cached.RoofFaces);
            _structureHighlightBoxes.AddRange(cached.Highlights);
            TreeShapesDrawn.UnionWith(cached.TreeShapes);
            return;
        }
        bool enclosed = area.X > _view.X && area.Y > _view.Y && area.Right < _view.Right && area.Bottom < _view.Bottom;
        if (!enclosed || _buildingCache.Count >= MaximumCachedBuildings)
        {
            BuildBuilding(map, area, type, previewRotation);
            return;
        }
        cached = new(area, type, rotation, yard);
        int boxes = _boxes.Count, picks = _pickBoxes.Count, roofs = _roofFaces.Count, highlights = _structureHighlightBoxes.Count,
            entrances = _entranceThresholds.Count, foundations = _foundations.Count;
        _capturingBuilding = cached;
        try { BuildBuilding(map, area, type, previewRotation); }
        finally { _capturingBuilding = null; }
        cached.RoofGroups = cached.Roof.GroupBy(vertex => vertex.Color)
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var (color, transforms) in cached.Batches)
        {
            bool colors = Material(color, solid: true).AlbedoTexture is null;
            var buffer = InstanceBuffer(color, transforms, colors, cachedSpans: false);
            cached.InstanceBuffers.Add(color, buffer);
            if (!_cachedInstanceSpans.TryGetValue(color, out var spans))
                _cachedInstanceSpans.Add(color, spans = []);
            spans.Add((_boxBatches[color].Count - transforms.Count, buffer));
        }
        cached.Boxes = _boxes.Skip(boxes).ToArray();
        cached.Foundations = _foundations.Skip(foundations).ToArray();
        cached.Picks = _pickBoxes.Skip(picks).ToArray();
        cached.RoofFaces = _roofFaces.Skip(roofs).ToArray();
        cached.Highlights = _structureHighlightBoxes.Skip(highlights).ToArray();
        cached.Entrances = _entranceThresholds.Skip(entrances).ToArray();
        _buildingCache[anchor] = cached;
    }

    private void AppendRoofVertex(SurfaceVertex vertex)
    {
        if (!_roofBatches.TryGetValue(vertex.Color, out var vertices))
            _roofBatches.Add(vertex.Color, vertices = []);
        vertices.Add(vertex);
    }

    private float[] InstanceBuffer(int color, List<Transform3D> transforms, bool colors, bool cachedSpans)
        => EncodeInstanceBuffer(color, transforms, colors,
            cachedSpans && _cachedInstanceSpans.TryGetValue(color, out var spans) ? spans : null);

    private static float[] EncodeInstanceBuffer(int color, List<Transform3D> transforms, bool colors,
        IReadOnlyList<(int Start, float[] Buffer)>? spans)
    {
        int stride = colors ? 16 : 12;
        var buffer = new float[transforms.Count * stride];
        EncodeInstanceBufferInto(buffer, 0, color, transforms, colors, spans);
        return buffer;
    }

    private static void EncodeInstanceBufferInto(float[] buffer, int offset, int color,
        List<Transform3D> transforms, bool colors, IReadOnlyList<(int Start, float[] Buffer)>? spans)
    {
        int stride = colors ? 16 : 12;
        if (offset < 0 || buffer.Length - offset < transforms.Count * stride)
            throw new ArgumentException("Instance destination buffer cannot hold the authored transforms.", nameof(buffer));
        var rgb = Rgb.Hex(color);
        Color? tint = colors ? Color.Color8(rgb.R, rgb.G, rgb.B) : null;
        int next = 0;
        if (spans is not null)
            foreach (var span in spans)
            {
                int count = span.Buffer.Length / stride;
                if (span.Start < next || span.Start + count > transforms.Count)
                    throw new InvalidOperationException("Cached building instance spans do not match their authored transforms.");
                for (; next < span.Start; next++) WriteInstance(buffer, offset + next * stride, transforms[next], tint);
                Array.Copy(span.Buffer, 0, buffer, offset + span.Start * stride, span.Buffer.Length);
                next = span.Start + count;
            }
        for (; next < transforms.Count; next++) WriteInstance(buffer, offset + next * stride, transforms[next], tint);
    }

    private static void WriteInstance(float[] buffer, int offset, Transform3D transform, Color? color = null)
    {
        // Godot stores a 3D instance transform as three rows of four floats.
        buffer[offset] = transform.Basis.X.X;
        buffer[offset + 1] = transform.Basis.Y.X;
        buffer[offset + 2] = transform.Basis.Z.X;
        buffer[offset + 3] = transform.Origin.X;
        buffer[offset + 4] = transform.Basis.X.Y;
        buffer[offset + 5] = transform.Basis.Y.Y;
        buffer[offset + 6] = transform.Basis.Z.Y;
        buffer[offset + 7] = transform.Origin.Y;
        buffer[offset + 8] = transform.Basis.X.Z;
        buffer[offset + 9] = transform.Basis.Y.Z;
        buffer[offset + 10] = transform.Basis.Z.Z;
        buffer[offset + 11] = transform.Origin.Z;
        if (color is { } tint)
        {
            buffer[offset + 12] = tint.R;
            buffer[offset + 13] = tint.G;
            buffer[offset + 14] = tint.B;
            buffer[offset + 15] = tint.A;
        }
    }
}
