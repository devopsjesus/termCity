using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private const int PathOwnerCells = 8, PathPageOwners = 4, PathDependencyMargin = 2;
    private readonly record struct PathPageEpoch(GameMap Map, int Navigation, int Terrain,
        int TerrainStyle, int DepthStyle, bool Roads);
    private sealed record PathOwner(ulong Stamp, DirtMesh Mesh, PathPageEpoch Epoch);
    private sealed record NativePathPage(MeshInstance3D? Node, long Bytes, long Used);
    private sealed record PathPageWork(PathPageEpoch Epoch, PathMeshWork Input, Dictionary<Pos, ulong> Stamps);
    private sealed record PathPageResult(PathPageWork Work, Dictionary<Pos, PathOwner> Owners);
    private sealed record PathPageStage(Pos Key, MeshInstance3D? Node, Queue<DirtBuffer> Buffers, long Bytes);
    private sealed record PathPageUpload(PathPageResult Result, Queue<PathPageStage> Pending, List<PathPageStage> Pages);
    private readonly Dictionary<Pos, PathOwner> _pathOwners = [];
    private readonly Dictionary<Pos, NativePathPage> _nativePathPages = [];
    private readonly Dictionary<Pos, ulong> _wantedPathOwners = [];
    private PathPageEpoch? _pathPageEpoch;
    private PathPageWork? _queuedPathPages;
    private Task<PathPageResult>? _pathPageTask;
    private PathPageUpload? _pathPageUpload;
    private Exception? _pathPageFailure;
    internal int PathOwnersBuilt { get; private set; }
    internal int PathOwnersReused { get; private set; }
    internal int PathPagesPublished { get; private set; }
    private bool PathPagesPending => _pathPageTask is not null || _queuedPathPages is not null || _pathPageUpload is not null;
    private bool RequiredPathPagesPending => _groundPathsPending || _wantedPathOwners.Any(pair =>
        !_pathOwners.TryGetValue(pair.Key, out var owner) ||
        (_pathSession?.Game.Paused != false || owner.Epoch != _pathPageEpoch) && owner.Stamp != pair.Value);
    private static Pos PathPageKey(Pos owner) => new(owner.X / PathPageOwners, owner.Y / PathPageOwners);
    private static Pos PathOwnerAt(float x, float z) =>
        new((int)MathF.Floor(x / PathOwnerCells), (int)MathF.Floor(z / PathOwnerCells));
    private static long DirtBytes(DirtMesh mesh) => 128 + mesh.Faces.Count * 40L +
        mesh.Buffers.Sum(buffer => 128L + buffer.Points.LongLength * 32 + buffer.Indices.LongLength * sizeof(int));
    private long PathManagedBytes => _pathOwners.Count * 64L + _nativePathPages.Count * 128L +
        _pathOwners.Values.Sum(owner => DirtBytes(owner.Mesh)) +
        (_pathPageUpload?.Result.Owners.Values.Sum(owner => DirtBytes(owner.Mesh)) ?? 0);
    private long PathNativeBytes => _nativePathPages.Values.Sum(page => page.Bytes) +
        (_pathPageUpload?.Pages.Sum(page => page.Bytes) ?? 0);

    private static CellRect PathPageView(CellRect visible, GameMap map)
    {
        int left = Math.Max(0, Math.Max(0, visible.X - 1) / PathOwnerCells * PathOwnerCells - PathDependencyMargin);
        int top = Math.Max(0, Math.Max(0, visible.Y - 1) / PathOwnerCells * PathOwnerCells - PathDependencyMargin);
        int right = Math.Min(map.Width, (Math.Min(map.Width - 1, visible.Right + 1) / PathOwnerCells + 1) * PathOwnerCells + PathDependencyMargin);
        int bottom = Math.Min(map.Height, (Math.Min(map.Height - 1, visible.Bottom + 1) / PathOwnerCells + 1) * PathOwnerCells + PathDependencyMargin);
        return new(left, top, right - left, bottom - top);
    }

    private static CellRect PathLayoutView(CellRect view, GameMap map)
    {
        // A retained camera window must still supply complete owners and their relief neighborhood.
        const int margin = PathOwnerCells + PathDependencyMargin * 2;
        int left = Math.Max(0, view.X - margin), top = Math.Max(0, view.Y - margin);
        int right = Math.Min(map.Width, view.Right + 1 + margin);
        int bottom = Math.Min(map.Height, view.Bottom + 1 + margin);
        return new(left, top, right - left, bottom - top);
    }

    private static bool TouchesPathOwners(float left, float back, float right, float front, IReadOnlySet<Pos> owners)
    {
        for (int y = (int)MathF.Floor((back - PathDependencyMargin) / PathOwnerCells);
            y <= (int)MathF.Floor((front + PathDependencyMargin) / PathOwnerCells); y++)
            for (int x = (int)MathF.Floor((left - PathDependencyMargin) / PathOwnerCells);
                x <= (int)MathF.Floor((right + PathDependencyMargin) / PathOwnerCells); x++)
                if (owners.Contains(new(x, y))) return true;
        return false;
    }

    private void DrawPathPages(GameSession session)
    {
        var map = session.Game.Map;
        if (_publishedPathContext is not null ||
            _pathPageEpoch is { } previous && !ReferenceEquals(previous.Map, map)) ClearPathMesh();
        _requestedPathContext = null;
        _queuedPathMesh = null;
        _pathSession = session;
        var epoch = new PathPageEpoch(map, map.NavigationVersion, map.TerrainVersion,
            (int)TerrainTreatment, (int)DepthTreatment, RoadRenderingEnabled);
        _pathPageEpoch = epoch;
        _pathPresentationView = new(_cameraVisibleView.X / PathOwnerCells * PathOwnerCells,
            _cameraVisibleView.Y / PathOwnerCells * PathOwnerCells,
            (_cameraVisibleView.Right / PathOwnerCells + 1) * PathOwnerCells - _cameraVisibleView.X / PathOwnerCells * PathOwnerCells,
            (_cameraVisibleView.Bottom / PathOwnerCells + 1) * PathOwnerCells - _cameraVisibleView.Y / PathOwnerCells * PathOwnerCells);
        var view = PathPageView(_cameraVisibleView, map);
        _groundPathsPending = PathGroundPending(session, view);
        if (_groundPathsPending)
        {
            PrepareCameraGround(session, view);
            return;
        }
        if (_footpathLayout is null) return;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _wantedPathOwners.Clear();
        for (int y = Math.Max(0, _cameraVisibleView.Y - 1) / PathOwnerCells;
            y <= Math.Min(map.Height - 1, _cameraVisibleView.Bottom + 1) / PathOwnerCells; y++)
            for (int x = Math.Max(0, _cameraVisibleView.X - 1) / PathOwnerCells;
                x <= Math.Min(map.Width - 1, _cameraVisibleView.Right + 1) / PathOwnerCells; x++)
                _wantedPathOwners[new(x, y)] = 14695981039346656037;
        foreach (var surface in _footpathLayout.Surfaces)
            foreach (var polygon in surface.Polygons)
            {
                float left = polygon.Min(p => p.X) - PathDependencyMargin;
                float right = polygon.Max(p => p.X) + PathDependencyMargin;
                float back = polygon.Min(p => p.Y) - PathDependencyMargin;
                float front = polygon.Max(p => p.Y) + PathDependencyMargin;
                for (int y = (int)MathF.Floor(back / PathOwnerCells); y <= (int)MathF.Floor(front / PathOwnerCells); y++)
                    for (int x = (int)MathF.Floor(left / PathOwnerCells); x <= (int)MathF.Floor(right / PathOwnerCells); x++)
                    {
                        var key = new Pos(x, y);
                        if (!_wantedPathOwners.TryGetValue(key, out ulong hash)) continue;
                        HashGroundValue(ref hash, FootpathArtwork.ColorForWear(surface.Wear));
                        HashGroundValue(ref hash, polygon.Length);
                        foreach (var point in polygon)
                        {
                            HashGroundValue(ref hash, BitConverter.SingleToInt32Bits(point.X));
                            HashGroundValue(ref hash, BitConverter.SingleToInt32Bits(point.Y));
                        }
                        _wantedPathOwners[key] = hash;
                    }
            }
        foreach (var key in _wantedPathOwners.Keys.ToArray())
        {
            ulong hash = _wantedPathOwners[key];
            HashGroundValue(ref hash, (int)TerrainTreatment);
            HashGroundValue(ref hash, (int)DepthTreatment);
            HashGroundValue(ref hash, RoadRenderingEnabled ? 1 : 0);
            for (int y = Math.Max(0, key.Y * PathOwnerCells - 4); y < Math.Min(map.Height, (key.Y + 1) * PathOwnerCells + 4); y++)
                for (int x = Math.Max(0, key.X * PathOwnerCells - 4); x < Math.Min(map.Width, (key.X + 1) * PathOwnerCells + 4); x++)
                    MixGroundDependency(ref hash, map, x, y);
            _wantedPathOwners[key] = hash;
            if (_pathOwners.TryGetValue(key, out var retained) && retained.Stamp == hash && retained.Epoch != epoch)
                _pathOwners[key] = retained with { Epoch = epoch };
        }
        var dirty = _wantedPathOwners.Where(pair => !_pathOwners.TryGetValue(pair.Key, out var owner) ||
            owner.Stamp != pair.Value).ToDictionary();
        PathOwnersReused += _wantedPathOwners.Count - dirty.Count;
        if (dirty.Count > 0)
        {
            var source = CapturePathSource(map, view) ?? new PathSurfaceSource(view,
                TerrainSprite.MaximumHillFor(map), _pathElevations ??= TerrainSprite.CaptureElevations(map),
                new Dictionary<int, Vector3[]>());
            source = source with { Owners = dirty.Keys.ToHashSet() };
            var context = new PathMeshContext(map, map.NavigationVersion, map.TerrainVersion, view,
                (int)TerrainTreatment, (int)DepthTreatment, RoadRenderingEnabled);
            _queuedPathPages = new(epoch, new(++_pathMeshRevision, context, _footpathLayout, _roadLayout, source), dirty);
            StartPathPages();
        }
        else _queuedPathPages = null;
        RefreshPathPageVisibility();
        FootpathPrepareMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private void StartPathPages()
    {
        if (_pathPageTask is not null || _pathPageUpload is not null || _queuedPathPages is not { } work ||
            _pathPageFailure is not null) return;
        _queuedPathPages = null;
        _pathPageTask = Task.Run(() =>
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var source = BuildPathSource(work.Input);
            var faces = FootpathReliefMesh.Build(source, point => PathBoundaryDistance(work.Input, point));
            // Relief keeps the source triangle's center as A, so ownership never cuts a triangle.
            var owners = faces.GroupBy(face => PathOwnerAt(face.A.X, face.A.Z))
                .Where(group => work.Stamps.ContainsKey(group.Key))
                .ToDictionary(group => group.Key, group => new PathOwner(work.Stamps[group.Key], BuildDirtMesh(group.ToArray()), work.Epoch));
            foreach (var (key, stamp) in work.Stamps)
                owners.TryAdd(key, new(stamp, new DirtMesh([], [], 0, 0), work.Epoch));
            double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return new PathPageResult(work, owners.ToDictionary(pair => pair.Key,
                pair => pair.Value with { Mesh = pair.Value.Mesh with { Milliseconds = elapsed } }));
        });
    }

    private static float PathBoundaryDistance(PathMeshWork work, System.Numerics.Vector2 point)
    {
        float distance = work.Layout.BoundaryDistance(point, FootpathArtwork.ShoulderWidth);
        if (work.Roads is { } roads)
            foreach (var road in roads.AtCell((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y)))
                if (!road.Bridge)
                    for (int i = 0; i < road.Road.Length; i++)
                        distance = Math.Min(distance, WallNetwork.PointSegmentDistance(point,
                            road.Road[i], road.Road[(i + 1) % road.Road.Length]));
        return distance;
    }

    private void ApplyPathPages()
    {
        if (!PathPagesPending) return;
        if (_pathPageTask is { IsCompleted: true } task)
        {
            _pathPageTask = null;
            if (!task.IsCompletedSuccessfully)
            {
                _pathPageFailure = task.Exception is { } error ? error : new TaskCanceledException(task);
                _queuedPathPages = null;
                _pathSession?.SetMessage("Footpath page preparation failed; see the Godot log.", MessageKind.Error);
                if (_pathSession is { } session) session.Game.Paused = true;
                GD.PushError($"Footpath page preparation failed: {_pathPageFailure}");
                return;
            }
            var result = task.GetAwaiter().GetResult();
            if (result.Work.Epoch == _pathPageEpoch)
            {
                var stages = new List<PathPageStage>();
                foreach (var key in result.Owners.Keys.Select(PathPageKey).Distinct())
                {
                    var buffers = _pathOwners.Where(pair => PathPageKey(pair.Key) == key && !result.Owners.ContainsKey(pair.Key))
                        .Select(pair => pair.Value).Concat(result.Owners.Where(pair => PathPageKey(pair.Key) == key).Select(pair => pair.Value))
                        .SelectMany(owner => owner.Mesh.Buffers).ToArray();
                    var node = buffers.Length == 0 ? null : new MeshInstance3D { Mesh = new ArrayMesh(), Visible = false };
                    if (node is not null) _paths.AddChild(node);
                    stages.Add(new(key, node, new(buffers),
                        buffers.Sum(buffer => buffer.Points.LongLength * 32 + buffer.Indices.LongLength * sizeof(int))));
                }
                _pathPageUpload = new(result, new(stages), stages);
            }
        }
        if (_pathPageUpload is { } upload)
        {
            if (upload.Result.Work.Epoch != _pathPageEpoch)
            {
                foreach (var stage in upload.Pages) DisposePathNode(stage.Node);
                _pathPageUpload = null;
            }
            else
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                while (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < 4 &&
                    upload.Pending.TryPeek(out var stage))
                {
                    if (!stage.Buffers.TryDequeue(out var buffer)) { upload.Pending.Dequeue(); continue; }
                    using var arrays = new Godot.Collections.Array();
                    arrays.Resize((int)Mesh.ArrayType.Max);
                    arrays[(int)Mesh.ArrayType.Vertex] = buffer.Points;
                    arrays[(int)Mesh.ArrayType.Normal] = buffer.Normals;
                    arrays[(int)Mesh.ArrayType.TexUV] = buffer.Uvs;
                    arrays[(int)Mesh.ArrayType.Index] = buffer.Indices;
                    var mesh = (ArrayMesh)stage.Node!.Mesh;
                    mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                    mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, Material(buffer.Color));
                    FootpathUploadBytes += buffer.Points.LongLength * 32 + buffer.Indices.LongLength * sizeof(int);
                }
                if (upload.Pending.Count == 0)
                {
                    foreach (var (key, owner) in upload.Result.Owners) _pathOwners[key] = owner;
                    foreach (var stage in upload.Pages)
                    {
                        if (_nativePathPages.Remove(stage.Key, out var retired)) DisposePathNode(retired.Node);
                        _nativePathPages[stage.Key] = new(stage.Node, stage.Bytes, ++_cacheUse);
                        PathPagesPublished++;
                    }
                    PathOwnersBuilt += upload.Result.Owners.Count;
                    FootpathWorkerMilliseconds = upload.Result.Owners.Values.Select(owner => owner.Mesh.Milliseconds).DefaultIfEmpty().Max();
                    FootpathMeshesPublished++;
                    _pathPageUpload = null;
                    RefreshPathPageVisibility();
                    RequestRender();
                    if (_pathSession is { } session) DrawPathPages(session);
                }
            }
        }
        StartPathPages();
        TrimPathPages();
    }

    private void RefreshPathPageVisibility()
    {
        var wanted = _wantedPathOwners.Keys.Select(PathPageKey).ToHashSet();
        foreach (var (key, page) in _nativePathPages.ToArray())
        {
            if (page.Node is { } node) node.Visible = wanted.Contains(key);
            if (wanted.Contains(key)) _nativePathPages[key] = page with { Used = ++_cacheUse };
        }
        var meshes = _pathOwners.Where(pair => wanted.Contains(PathPageKey(pair.Key))).Select(pair => pair.Value.Mesh).ToArray();
        FootpathTrianglesDrawn = meshes.Sum(mesh => mesh.Faces.Count);
        SlopedFootpathTrianglesDrawn = meshes.Sum(mesh => mesh.Sloped);
        MaximumFootpathTriangleRise = meshes.Select(mesh => mesh.MaximumRise).DefaultIfEmpty().Max();
    }

    private void TrimPathPages()
    {
        if (_pathPageUpload is not null) return;
        long cpu = CpuCacheBytes, native = DestinationGpuBytes;
        if (cpu <= CpuRetentionTargetBytes && native <= GpuRetentionTargetBytes) return;
        foreach (var (key, page) in _nativePathPages.Where(pair =>
            !_wantedPathOwners.Keys.Any(owner => PathPageKey(owner) == pair.Key)).OrderBy(pair => pair.Value.Used).ToArray())
        {
            if (cpu <= CpuRetentionTargetBytes && native <= GpuRetentionTargetBytes) break;
            _nativePathPages.Remove(key);
            DisposePathNode(page.Node);
            native -= page.Bytes;
            foreach (var owner in _pathOwners.Keys.Where(owner => PathPageKey(owner) == key).ToArray())
            {
                cpu -= DirtBytes(_pathOwners[owner].Mesh);
                _pathOwners.Remove(owner);
            }
        }
    }

    private void DisposePathNode(MeshInstance3D? node)
    {
        if (node is null) return;
        _paths.RemoveChild(node);
        node.QueueFree();
        node.Mesh?.Dispose();
    }

    private void ClearPathPages()
    {
        foreach (var page in _nativePathPages.Values) DisposePathNode(page.Node);
        if (_pathPageUpload is { } upload)
            foreach (var stage in upload.Pages) DisposePathNode(stage.Node);
        _nativePathPages.Clear();
        _pathOwners.Clear();
        _wantedPathOwners.Clear();
        _pathPageEpoch = null;
        _pathPageUpload = null;
        _queuedPathPages = null;
        _pathPageFailure = null;
    }

    private void ThrowPathPageFailure()
    {
        if (_pathPageFailure is { } error)
            throw new InvalidOperationException("Footpath page preparation failed; see the Godot log.", error);
    }

    internal async Task VerifyPathPagePublication()
    {
        await AwaitFootpathMesh();
        ValidatePathPageBuffers();
        var session = _pathSession ?? throw new InvalidOperationException("Path pages require an owning session.");
        long bytes = FootpathUploadBytes;
        int built = PathOwnersBuilt;
        var pages = _nativePathPages.ToDictionary();
        DrawPathPages(session);
        DrawPathPages(session);
        await AwaitFootpathMesh();
        if (FootpathUploadBytes != bytes || PathOwnersBuilt != built ||
            pages.Any(pair => !ReferenceEquals(pair.Value.Node, _nativePathPages.GetValueOrDefault(pair.Key)?.Node)))
            throw new InvalidOperationException("Unchanged path presentation rebuilt or reuploaded resident pages.");
        var view = PathPageView(_cameraVisibleView, session.Game.Map);
        if (CapturePathSource(session.Game.Map, view) is { } source)
        {
            var context = new PathMeshContext(session.Game.Map, session.Game.Map.NavigationVersion,
                session.Game.Map.TerrainVersion, view, (int)TerrainTreatment, (int)DepthTreatment, RoadRenderingEnabled);
            var input = new PathMeshWork(0, context, _footpathLayout!, _roadLayout, source);
            var reference = FootpathReliefMesh.Build(BuildPathSource(input), point => PathBoundaryDistance(input, point))
                .Where(face => _wantedPathOwners.ContainsKey(PathOwnerAt(face.A.X, face.A.Z)));
            var expected = BuildDirtMesh(reference.ToArray()).Faces.GroupBy(face => face).ToDictionary(group => group.Key, group => group.Count());
            var actual = _pathOwners.Where(pair => _wantedPathOwners.ContainsKey(pair.Key)).SelectMany(pair => pair.Value.Mesh.Faces)
                .GroupBy(face => face).ToDictionary(group => group.Key, group => group.Count());
            if (expected.Count != actual.Count || expected.Any(pair => actual.GetValueOrDefault(pair.Key) != pair.Value))
                throw new InvalidOperationException("Paged path geometry differs from complete reference clipping/relief.");
        }
        var epoch = _pathPageEpoch ?? throw new InvalidOperationException("Path validation lost its epoch.");
        var selected = _wantedPathOwners.Keys.First(key => _pathOwners[key].Epoch == epoch);
        bool paused = session.Game.Paused;
        ulong stamp = _wantedPathOwners[selected];
        try
        {
            _wantedPathOwners[selected] = stamp ^ 1;
            session.Game.Paused = true;
            if (!RequiredPathPagesPending) throw new InvalidOperationException("Paused paths did not await changed content.");
            session.Game.Paused = false;
            if (RequiredPathPagesPending) throw new InvalidOperationException("Compatible live path wear blocked existing presentation.");
        }
        finally { session.Game.Paused = paused; _wantedPathOwners[selected] = stamp; }
        foreach (var incompatible in new[]
        {
            epoch with { Map = new GameMap(32, 32, session.Game.Map.Content) },
            epoch with { Navigation = epoch.Navigation + 1 },
            epoch with { Terrain = epoch.Terrain + 1 },
            epoch with { TerrainStyle = epoch.TerrainStyle + 1 },
            epoch with { DepthStyle = epoch.DepthStyle + 1 },
            epoch with { Roads = !epoch.Roads },
        })
        {
            var original = _pathOwners[selected];
            _pathOwners.Remove(selected);
            DrawPathPages(session);
            var task = _pathPageTask ?? throw new InvalidOperationException("Path invalidation fixture did not start its worker.");
            int published = FootpathMeshesPublished;
            _pathPageEpoch = incompatible;
            try
            {
                await task;
                ApplyPathPages();
                if (FootpathMeshesPublished != published || _pathPageUpload is not null)
                    throw new InvalidOperationException("An incompatible path worker was published.");
            }
            finally
            {
                _pathPageEpoch = epoch;
                _pathOwners[selected] = original;
                _queuedPathPages = null;
            }
        }
        _pathOwners.Remove(selected);
        DrawPathPages(session);
        var removed = _pathPageTask ?? throw new InvalidOperationException("Path removal fixture did not start its worker.");
        ClearPathMesh();
        await removed;
        ApplyPathPages();
        if (_pathOwners.Count != 0 || _nativePathPages.Count != 0 || FootpathTrianglesDrawn != 0)
            throw new InvalidOperationException("An obsolete worker restored removed path pages.");
        DrawPathPages(session);
        DrawPathPages(session);
        await AwaitFootpathMesh();
        GD.Print("TERMCITY_LIVE_PATH_PUBLICATION_OK owners=stable unchanged=zero-generation-and-uploads reference=exact " +
            "paused=latest live=compatible invalidation=map/navigation/terrain/style/roads/removal");
    }

    internal void PrintPathPageProfile(string label) =>
        GD.Print($"TERMCITY_PATH_PAGE_PROFILE phase={label} owners={_pathOwners.Count} owners-built={PathOwnersBuilt} " +
            $"owners-reused={PathOwnersReused} pages={_nativePathPages.Count} pages-published={PathPagesPublished} " +
            $"active-pages={_nativePathPages.Values.Count(page => page.Node is { Visible: true })} " +
            $"managed-bytes={PathManagedBytes} native-bytes={PathNativeBytes} upload-bytes={FootpathUploadBytes}");

    private void ValidatePathPageBuffers()
    {
        foreach (var (key, page) in _nativePathPages)
        {
            var expected = _pathOwners.Where(pair => PathPageKey(pair.Key) == key).SelectMany(pair => pair.Value.Mesh.Faces)
                .GroupBy(face => face).ToDictionary(group => group.Key, group => group.Count());
            if (page.Node is null)
            {
                if (expected.Count != 0) throw new InvalidOperationException("Nonempty path page has no native mesh.");
                continue;
            }
            var actual = new Dictionary<FootpathReliefTriangle, int>();
            var mesh = page.Node.Mesh;
            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            {
                var material = mesh.SurfaceGetMaterial(surface);
                int color = material == Material(FootpathArtwork.FreshColor) ? FootpathArtwork.FreshColor :
                    material == Material(FootpathArtwork.WornColor) ? FootpathArtwork.WornColor :
                    throw new InvalidOperationException("Native path page lost its original dirt material.");
                using var arrays = mesh.SurfaceGetArrays(surface);
                var points = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
                var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                for (int i = 0; i < points.Length; i++)
                    if (normals[i].Y <= 0 ||
                        !uvs[i].IsEqualApprox(new Vector2(points[i].X, points[i].Z) * FootpathArtwork.UvScale))
                        throw new InvalidOperationException("Native path page lost its upward normals or world-space UVs.");
                for (int i = 0; i < indices.Length; i += 3)
                {
                    System.Numerics.Vector3 Point(int index) => new(points[index].X, points[index].Y, points[index].Z);
                    var face = new FootpathReliefTriangle(Point(indices[i]), Point(indices[i + 1]), Point(indices[i + 2]), color);
                    actual[face] = actual.GetValueOrDefault(face) + 1;
                }
            }
            if (expected.Count != actual.Count || expected.Any(pair => actual.GetValueOrDefault(pair.Key) != pair.Value))
                throw new InvalidOperationException("Native path-page triangles differ from their committed owner packets.");
        }
    }

    private static async Task VerifyStablePathPageLifecycle(Main host)
    {
        var game = TermCity.Core.Simulation.CityGame.New(new()
            { MapWidth = 160, MapHeight = 64, Seed = 42, StartingYear = 1226 });
        game.Paused = true;
        var map = game.Map;
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
            {
                map.ClearCell(x, y);
                map.SetTerrain(x, y, map.Content.Terrains.Base);
            }
        foreach (int start in new[] { 12, 124 })
            for (int x = start; x < start + 16; x++) map.SetPathWear(x, 32, 6);
        game.Touch();
        var session = new GameSession(game, showGuide: false);
        session.SetCameraEdgePadding(true);
        session.SetZoom(0);
        var probe = new PerspectiveCity { WaterMotionEnabled = false };
        host.AddChild(probe);
        try
        {
            probe.WholeMapWarmingEnabled = false;
            async Task Visit(int x)
            {
                session.CenterOn(new(x, 32));
                probe.Sync(session, new(400, 300));
                await probe.AwaitFootpathMesh();
            }
            await Visit(20);
            await Visit(132);
            await Visit(20);
            long bytes = probe.FootpathUploadBytes;
            int built = probe.PathOwnersBuilt;
            var native = probe._nativePathPages.ToDictionary();
            string saved = TermCity.Core.Persistence.SaveGameStore.Serialize(game);
            for (int step = 0; step < 4; step++) await Visit(step % 2 == 0 ? 132 : 20);
            if (probe.FootpathUploadBytes != bytes || probe.PathOwnersBuilt != built ||
                native.Any(pair => !ReferenceEquals(pair.Value.Node, probe._nativePathPages[pair.Key].Node)))
                throw new InvalidOperationException("Warmed path navigation regenerated or reuploaded resident geometry.");
            if (saved != TermCity.Core.Persistence.SaveGameStore.Serialize(game))
                throw new InvalidOperationException("Path page navigation changed paused simulation state.");
            for (int x = 12; x < 28; x++) map.SetPathWear(x, 32, 7);
            game.Touch();
            await Visit(20);
            if (probe.FootpathUploadBytes != bytes || probe.PathOwnersBuilt != built)
                throw new InvalidOperationException("Visually unchanged wear regenerated or uploaded path pages.");
            await probe.VerifyPathPagePublication();
            await Visit(132);
            var far = probe._nativePathPages.Where(pair => pair.Value.Bytes > 0 &&
                probe._wantedPathOwners.Keys.Any(owner => PathPageKey(owner) == pair.Key)).ToDictionary();
            await Visit(20);
            for (int x = 12; x < 28; x++) map.SetPathWear(x, 32, 1);
            game.Touch();
            await Visit(20);
            foreach (var (key, page) in far)
                if (!ReferenceEquals(page.Node, probe._nativePathPages[key].Node))
                    throw new InvalidOperationException("A local wear edit replaced an unrelated path page.");
            await probe.VerifyPathPagePublication();
            await Visit(132);
            probe.GpuCacheBudgetBytes = 1024;
            probe.TrimGpuResidency();
            if (probe._nativePathPages.Any(pair => pair.Value.Bytes > 0 &&
                !probe._wantedPathOwners.Keys.Any(owner => PathPageKey(owner) == pair.Key)))
                throw new InvalidOperationException("Native byte pressure retained evictable path pages.");
            probe.GpuCacheBudgetBytes = 128L * 1024 * 1024;
            await Visit(20);
            await Visit(132);
            probe.CpuCacheBudgetBytes = 1024;
            probe.TrimGpuResidency();
            if (probe._pathOwners.Keys.Any(owner =>
                !probe._wantedPathOwners.Keys.Any(wanted => PathPageKey(wanted) == PathPageKey(owner))))
                throw new InvalidOperationException("CPU byte pressure retained evictable path packets.");
            probe.CpuCacheBudgetBytes = 256L * 1024 * 1024;
            var previous = probe._pathPageEpoch;
            session.NewGame(new() { MapWidth = 80, MapHeight = 48, Seed = 43, StartingYear = 1226 });
            await Visit(20);
            if (probe._pathPageEpoch?.Map == previous?.Map)
                throw new InvalidOperationException("Map replacement retained old path-page ownership.");
            GD.Print("TERMCITY_PATH_PAGE_LIFECYCLE_OK return=zero-generation-and-uploads reference=exact " +
                "wear-edit=local cpu-pressure=reclaimed native-pressure=reclaimed map=replaced");
        }
        finally
        {
            host.RemoveChild(probe);
            probe.Free();
        }
    }
}
