using Godot;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private const int CameraVisibilityMargin = 6;
    private bool _recentRegionEnabled;
    private CellRect? _recentRouteBounds;
    private double _recentRouteZoom;
    private bool InRecentRegion(Pos key) => _recentRegionEnabled && _recentRouteBounds is { } bounds &&
        key.X * 16 <= bounds.Right && (key.X + 1) * 16 > bounds.X &&
        key.Y * 16 <= bounds.Bottom && (key.Y + 1) * 16 > bounds.Y;
    private bool NativeNodeLimitReached => !_recentRegionEnabled && !_persistentResidency && SceneObjectCount >= 480;
    internal int ActiveSceneObjectCount => _world.GetChildCount() + _retainedSolids.GetChildCount() +
        _objectPools.Values.Count(pool => pool.Node.Visible) + _objectRoofPages.Values.Count(node => node.Visible) +
        _paths.GetChildCount() + _indicators.GetChildCount() + _fortifications.GetChildCount() +
        _siegeActors.GetChildCount() + (_groundDrawPagesEnabled
            ? _groundDrawPages.Values.Where(page => page.Root.Visible).Sum(page => page.Root.GetChildCount())
            : _groundNodes.Values.Where(node => node.Visible).Sum(node => node.GetChildCount()));
    internal long RecentRegionManagedBytes => _objectChunks.Where(pair => InRecentRegion(pair.Key)).Sum(pair => pair.Value.ManagedBytes) +
        _groundBytes.Where(pair => InRecentRegion(new(pair.Key.X, pair.Key.Y))).Sum(pair => pair.Value);
    internal long RecentRegionNativeBytes => _groundGpuBytes.Where(pair => InRecentRegion(new(pair.Key.X, pair.Key.Y))).Sum(pair => pair.Value) +
        _objectPools.Where(pair =>
            _visibleObjectChunks.Values.Any(chunk => InRecentRegion(chunk.Key) && chunk.Slots.ContainsKey(pair.Key)))
            .Sum(pair => pair.Value.Data.LongLength * sizeof(float)) +
        _visibleObjectChunks.Values.Where(chunk => InRecentRegion(chunk.Key))
            .Sum(chunk => chunk.RoofBuffers.Values.Sum(buffer => buffer.Points.LongLength * 32) +
                chunk.Geometry.Zones.Values.Sum(vertices => vertices.Count * 32L));

    private void RefreshRecentRegion()
    {
        if (_persistentObjectResidency) RefreshResidentObjectVisibility();
        if (!_recentRegionEnabled || _map is null) return;
        var visible = _cameraVisibleView;
        int margin = TerrainHaloExitChunks * 16;
        int left = Math.Max(0, visible.X - margin), top = Math.Max(0, visible.Y - margin);
        int rightEdge = Math.Min(_map.Width - 1, visible.Right + margin);
        int bottomEdge = Math.Min(_map.Height - 1, visible.Bottom + margin);
        var current = new CellRect(left, top, rightEdge - left + 1, bottomEdge - top + 1);
        var corners = GroundViewCorners();
        int maxWidth = ((int)Math.Ceiling(corners.Max(point => point.X) - corners.Min(point => point.X)) +
            CameraVisibilityMargin * 2) * 3 + margin * 2;
        int maxHeight = ((int)Math.Ceiling(corners.Max(point => point.Y) - corners.Min(point => point.Y)) +
            CameraVisibilityMargin * 2) * 3 + margin * 2;
        if (_recentRouteBounds is null || _recentRouteZoom != _cameraState.Zoom)
        {
            _recentRouteBounds = current;
            _recentRouteZoom = _cameraState.Zoom;
        }
        else
        {
            var old = _recentRouteBounds.Value;
            int x = Math.Min(old.X, current.X), y = Math.Min(old.Y, current.Y);
            int right = Math.Max(old.Right, current.Right), bottom = Math.Max(old.Bottom, current.Bottom);
            // Map-edge clipping must not shrink the revisit allowance or discard the route behind us.
            if (right - x + 1 > maxWidth || bottom - y + 1 > maxHeight)
                _recentRouteBounds = current;
            else _recentRouteBounds = new(x, y, right - x + 1, bottom - y + 1);
        }
        // Resident pools remain allocated, but only requested material pages submit drawing.
        foreach (var pair in _objectPools)
            pair.Value.Node.Visible =
                _visibleObjectChunks.Values.Any(chunk => _wantedObjectChunks.Contains(chunk.Key) && chunk.Slots.ContainsKey(pair.Key)) ||
                _uploadingObject is { } upload && upload.Slots.ContainsKey(pair.Key);
        foreach (var (page, node) in _objectRoofPages)
            node.Visible = _wantedObjectChunks.Any(key => key.X / 2 == page.X && key.Y / 2 == page.Y);
    }

    private void RestoreNativeVisibility()
    {
        foreach (var pool in _objectPools.Values) pool.Node.Visible = true;
        foreach (var node in _objectRoofPages.Values) node.Visible = true;
    }
}
