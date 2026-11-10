using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.World;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private readonly record struct PathMeshContext(GameMap Map, int Navigation, int Terrain, CellRect View,
        int TerrainStyle, int DepthStyle, bool Roads);
    private sealed record PathMeshWork(long Revision, PathMeshContext Context, FootpathSurfaceLayout Layout, RoadMeshLayout? Roads,
        PathSurfaceSource Source);
    private sealed record DirtBuffer(int Color, Vector3[] Points, Vector3[] Normals, Vector2[] Uvs, int[] Indices);
    private sealed record DirtMesh(IReadOnlyList<DirtBuffer> Buffers, IReadOnlyList<FootpathReliefTriangle> Faces,
        int Sloped, float MaximumRise)
    {
        internal double Milliseconds { get; init; }
    }
    private readonly Node3D _paths = new();
    private sealed record PathUpload(long Revision, PathMeshContext Context, DirtMesh Mesh, Queue<DirtBuffer> Buffers,
        List<MeshInstance3D> Nodes);
    private PathUpload? _pathUpload;
    private bool _groundPathsPending;
    private CellRect? _pathPresentationView;
    private long _pathMeshRevision;
    private PathMeshContext? _requestedPathContext, _publishedPathContext;
    internal int FootpathMeshesPublished { get; private set; }
    internal long FootpathUploadBytes { get; private set; }
    private PathMeshWork? _queuedPathMesh, _activePathMesh;
    private Task<DirtMesh>? _pathMeshTask;
    private GameSession? _pathSession;
    private GameMap? _pathMeshMap;
    private bool SceneGeometryPending => ObjectStreamingPending || _waitingForCameraPaths || GroundVisiblePending ||
        RequiredPathWorkPending;
    internal bool FootpathMeshPending => SceneGeometryPending || MinimapJumpPending;
    private bool PathWorkPending => _groundPathsPending || _pathMeshTask is not null || _queuedPathMesh is not null ||
        _pathUpload is not null || PathPagesPending;
    private bool RequiredPathWorkPending => !MeasureFootpathFaces ? RequiredPathPagesPending : _groundPathsPending ||
        PathWorkPending && (_pathSession?.Game.Paused != false || _requestedPathContext != _publishedPathContext);

    private void QueuePathMesh(GameSession session, PathSurfaceSource? source)
    {
        if (!ReferenceEquals(_pathMeshMap, session.Game.Map))
        {
            ClearPathMesh();
            _pathMeshMap = session.Game.Map;
        }
        _pathSession = session;
        _pathPresentationView = source?.View;
        if (source is null)
        {
            _pathMeshRevision++;
            _requestedPathContext = null;
            _queuedPathMesh = null;
            ClearPathMesh();
            return;
        }
        var map = session.Game.Map;
        _requestedPathContext = new(map, map.NavigationVersion, map.TerrainVersion, source.View,
            (int)TerrainTreatment, (int)DepthTreatment, RoadRenderingEnabled);
        _queuedPathMesh = new(++_pathMeshRevision, _requestedPathContext.Value, _footpathLayout!, _roadLayout, source);
        StartPathMesh();
        if (MeasureFootpathFaces) CompletePendingFootpaths();
    }

    private void StartPathMesh()
    {
        if (_pathMeshTask is not null || _pathUpload is not null || _queuedPathMesh is not { } work) return;
        _queuedPathMesh = null;
        _activePathMesh = work;
        // Geometry2D math uses only thread-local arrays. No mutable map, scene
        // tree or rendering-resource access occurs on the worker.
        _pathMeshTask = Task.Run(() =>
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            var source = BuildPathSource(work);
            var mesh = BuildDirtMesh(FootpathReliefMesh.Build(source, point => PathBoundaryDistance(work, point)));
            return mesh with
            {
                Milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            };
        });
    }

    private static DirtMesh BuildDirtMesh(IReadOnlyList<FootpathReliefTriangle> source)
    {
        var groups = new Dictionary<int, (Dictionary<SurfaceVertex, int> Lookup, List<SurfaceVertex> Vertices, List<int> Indices)>();
        var faces = new List<FootpathReliefTriangle>();
        int sloped = 0;
        float rise = 0;
        foreach (var face in source)
        {
            if (!TryDirtTriangle(face, out var a, out var b, out var c)) continue;
            if (!groups.TryGetValue(face.Color, out var group))
                groups[face.Color] = group = ([], [], []);
            void Vertex(SurfaceVertex vertex)
            {
                if (!group.Lookup.TryGetValue(vertex, out int index))
                {
                    index = group.Vertices.Count;
                    group.Lookup.Add(vertex, index);
                    group.Vertices.Add(vertex);
                }
                group.Indices.Add(index);
            }
            Vertex(a); Vertex(b); Vertex(c);
            if (a.Normal.Y < .999f) sloped++;
            rise = Math.Max(rise, Math.Max(a.Point.Y, Math.Max(b.Point.Y, c.Point.Y)) -
                Math.Min(a.Point.Y, Math.Min(b.Point.Y, c.Point.Y)));
            System.Numerics.Vector3 Point(Vector3 point) => new(point.X, point.Y, point.Z);
            faces.Add(new(Point(a.Point), Point(b.Point), Point(c.Point), face.Color));
        }
        return new(groups.Select(pair => new DirtBuffer(pair.Key, pair.Value.Vertices.Select(v => v.Point).ToArray(),
            pair.Value.Vertices.Select(v => v.Normal).ToArray(), pair.Value.Vertices.Select(v => v.Uv).ToArray(),
            pair.Value.Indices.ToArray())).SelectMany(SplitDirtBuffer).ToArray(), faces, sloped, rise);
    }

    private static IReadOnlyList<DirtBuffer> SplitDirtBuffer(DirtBuffer source)
    {
        const int maximumVertices = 16_384, maximumIndices = 49_152;
        if (source.Points.Length <= maximumVertices && source.Indices.Length <= maximumIndices) return [source];
        var pages = new List<DirtBuffer>();
        var lookup = new Dictionary<int, int>();
        var points = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();
        void Flush()
        {
            if (indices.Count == 0) return;
            pages.Add(new(source.Color, points.ToArray(), normals.ToArray(), uvs.ToArray(), indices.ToArray()));
            lookup.Clear();
            points.Clear(); normals.Clear(); uvs.Clear(); indices.Clear();
        }
        for (int triangle = 0; triangle < source.Indices.Length; triangle += 3)
        {
            if (points.Count + 3 > maximumVertices || indices.Count + 3 > maximumIndices) Flush();
            for (int corner = 0; corner < 3; corner++)
            {
                int original = source.Indices[triangle + corner];
                if (!lookup.TryGetValue(original, out int index))
                {
                    index = points.Count;
                    lookup.Add(original, index);
                    points.Add(source.Points[original]);
                    normals.Add(source.Normals[original]);
                    uvs.Add(source.Uvs[original]);
                }
                indices.Add(index);
            }
        }
        Flush();
        return pages;
    }

    private void ApplyPathMesh()
    {
        ApplyPathPages();
        if (_groundPathsPending && _pathSession is { } session)
        {
            _cameraGroundWork?.Task.GetAwaiter().GetResult();
            PollCameraGround(session);
            DrawFootpaths(session);
        }
        if (_pathMeshTask is { IsCompleted: true } task)
        {
            if (!task.IsCompletedSuccessfully)
            {
                GD.PushError($"Footpath geometry failed: {task.Exception}");
                _pathSession?.SetMessage("Footpath geometry failed; see the Godot log.", MessageKind.Error);
                _pathMeshTask = null;
                _activePathMesh = _queuedPathMesh = null;
                return;
            }
            var mesh = task.GetAwaiter().GetResult();
            var work = _activePathMesh;
            _pathMeshTask = null;
            _activePathMesh = null;
            // Wear updates may outpace preparation. Publish a complete compatible snapshot,
            // then prepare the latest; camera, terrain and navigation changes still cancel it.
            if (work is not null && work.Context == _requestedPathContext)
                _pathUpload = new(work.Revision, work.Context, mesh, new(mesh.Buffers), []);
        }
        if (_pathUpload is { } upload)
        {
            if (upload.Context != _requestedPathContext)
            {
                foreach (var node in upload.Nodes) { _paths.RemoveChild(node); node.QueueFree(); }
                _pathUpload = null;
            }
            else
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                while (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < 4 &&
                    upload.Buffers.TryDequeue(out var buffer))
                {
                    using var arrays = new Godot.Collections.Array();
                    arrays.Resize((int)Mesh.ArrayType.Max);
                    arrays[(int)Mesh.ArrayType.Vertex] = buffer.Points;
                    arrays[(int)Mesh.ArrayType.Normal] = buffer.Normals;
                    arrays[(int)Mesh.ArrayType.TexUV] = buffer.Uvs;
                    arrays[(int)Mesh.ArrayType.Index] = buffer.Indices;
                    using var resource = new ArrayMesh();
                    resource.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                    FootpathUploadBytes += buffer.Points.LongLength * 32 + buffer.Indices.LongLength * sizeof(int);
                    var node = new MeshInstance3D { Mesh = resource, MaterialOverride = Material(buffer.Color), Visible = false };
                    _paths.AddChild(node);
                    upload.Nodes.Add(node);
                }
                if (upload.Buffers.Count == 0)
                {
                    foreach (var node in _paths.GetChildren().OfType<MeshInstance3D>().Where(node => !upload.Nodes.Contains(node)))
                    {
                        _paths.RemoveChild(node);
                        node.QueueFree();
                    }
                    foreach (var node in upload.Nodes) node.Visible = true;
                    FootpathWorkerMilliseconds = upload.Mesh.Milliseconds;
                    FootpathTrianglesDrawn = upload.Mesh.Faces.Count;
                    SlopedFootpathTrianglesDrawn = upload.Mesh.Sloped;
                    MaximumFootpathTriangleRise = upload.Mesh.MaximumRise;
                    FootpathFaces.Clear();
                    if (MeasureFootpathFaces)
                        foreach (var face in upload.Mesh.Faces)
                            FootpathFaces.Add((new(face.A.X, face.A.Y, face.A.Z), new(face.B.X, face.B.Y, face.B.Z),
                                new(face.C.X, face.C.Y, face.C.Z)));
                    _publishedPathContext = upload.Context;
                    FootpathMeshesPublished++;
                    _pathUpload = null;
                    RequestRender();
                }
            }
        }
        StartPathMesh();
    }

    private void ClearPathMesh()
    {
        ClearPathPages();
        _requestedPathContext = null;
        _queuedPathMesh = null;
        foreach (var child in _paths.GetChildren()) { _paths.RemoveChild(child); child.QueueFree(); }
        _pathUpload = null;
        _publishedPathContext = null;
        FootpathTrianglesDrawn = SlopedFootpathTrianglesDrawn = 0;
        MaximumFootpathTriangleRise = 0;
        FootpathFaces.Clear();
    }

    internal async Task VerifyPathPublicationUnderCoalescedUpdates()
    {
        if (!MeasureFootpathFaces)
        {
            await VerifyPathPagePublication();
            return;
        }
        if (_pathSession is not { } session) throw new InvalidOperationException("Path publication requires an owning session.");
        await AwaitFootpathMesh();
        int before = FootpathMeshesPublished;
        DrawFootpaths(session);
        if (_pathMeshTask is not { } first) throw new InvalidOperationException("Path publication requires nonempty dirt geometry.");
        DrawFootpaths(session);
        await first;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (FootpathMeshesPublished == before)
        {
            DrawFootpaths(session);
            ApplyPathMesh();
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds > 5)
                throw new InvalidOperationException("Coalesced path updates starved complete compatible mesh publication.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        await AwaitFootpathMesh();
        var context = _requestedPathContext ?? throw new InvalidOperationException("Path regression lost its captured context.");
        bool paused = session.Game.Paused;
        try
        {
            DrawFootpaths(session);
            session.Game.Paused = true;
            if (!RequiredPathWorkPending) throw new InvalidOperationException("Paused inspection must wait for the latest footpath snapshot.");
            session.Game.Paused = false;
            if (RequiredPathWorkPending) throw new InvalidOperationException("Live compatible updates must not invalidate a complete published destination.");
            _requestedPathContext = context with { View = context.View with { X = context.View.X + 1 } };
            if (!RequiredPathWorkPending) throw new InvalidOperationException("A changed camera destination must await matching footpaths.");
        }
        finally
        {
            _requestedPathContext = context;
            session.Game.Paused = paused;
        }
        await AwaitFootpathMesh();
        foreach (var incompatible in new[]
        {
            context with { Map = CityGame.New(new GameConfig { MapWidth = 32, MapHeight = 32, Seed = 42 }).Map },
            context with { View = context.View with { X = context.View.X + 1 } },
            context with { Navigation = context.Navigation + 1 },
            context with { Terrain = context.Terrain + 1 },
            context with { TerrainStyle = context.TerrainStyle + 1 },
            context with { DepthStyle = context.DepthStyle + 1 },
            context with { Roads = !context.Roads },
        })
        {
            before = FootpathMeshesPublished;
            DrawFootpaths(session);
            var task = _pathMeshTask ?? throw new InvalidOperationException("Path regression requires a captured worker.");
            _requestedPathContext = incompatible;
            try
            {
                task.GetAwaiter().GetResult();
                ApplyPathMesh();
                if (FootpathMeshesPublished != before || _pathUpload is not null)
                    throw new InvalidOperationException("An incompatible path snapshot was published.");
            }
            finally { _requestedPathContext = context; }
        }
        DrawFootpaths(session);
        var removedTask = _pathMeshTask ?? throw new InvalidOperationException("Path removal regression requires a captured worker.");
        QueuePathMesh(session, null);
        removedTask.GetAwaiter().GetResult();
        ApplyPathMesh();
        if (_pathUpload is not null || FootpathTrianglesDrawn != 0)
            throw new InvalidOperationException("Removing paths allowed an obsolete worker to restore them.");
        DrawFootpaths(session);
        await AwaitFootpathMesh();
        GD.Print("TERMCITY_LIVE_PATH_PUBLICATION_OK superseded-worker=published coalesced-updates=bounded " +
            "paused=latest live=compatible invalidation=map/camera/navigation/terrain/style/roads/removal");
    }

    internal void CompletePendingFootpaths()
    {
        ThrowPathPageFailure();
        CompleteCameraPaths();
        CompleteObjectStreaming();
        ThrowPathPageFailure();
        while (PathWorkPending)
        {
            ThrowPathPageFailure();
            if (_groundPathsPending && _cameraGroundWork is { } ground) ground.Task.GetAwaiter().GetResult();
            _pathMeshTask?.GetAwaiter().GetResult();
            _pathPageTask?.GetAwaiter().GetResult();
            ApplyPathMesh();
        }
    }

    internal async Task AwaitFootpathMesh()
    {
        ThrowPathPageFailure();
        await AwaitCameraPaths();
        await AwaitObjectStreaming();
        ThrowPathPageFailure();
        while (PathWorkPending)
        {
            ThrowPathPageFailure();
            if (_groundPathsPending) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_pathMeshTask is { } task) await task;
            if (_pathPageTask is { } pageTask) await pageTask;
            ApplyPathMesh();
            if (PathWorkPending) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        while (MinimapJumpPending)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    internal int DoorApproachTrianglesDrawn { get; private set; }
    internal int SlopedFootpathTrianglesDrawn { get; private set; }
    internal bool LitDirtMaterials => new[] { FootpathArtwork.FreshColor, FootpathArtwork.WornColor }.All(color =>
        Material(color) is { ShadingMode: not BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoTexture: not null, TextureRepeat: true });
    internal IReadOnlyList<(int Color, Vector3 Point, Vector3 Normal, Vector2 Uv)> DirtGeometry
    {
        get
        {
            CompletePendingFootpaths();
            var vertices = new List<(int, Vector3, Vector3, Vector2)>();
            foreach (var instance in _world.GetChildren().Concat(_retainedSolids.GetChildren())
                .Concat(_paths.GetChildren()).OfType<MeshInstance3D>().Concat(_objectRoofPages
                    .OrderBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.X).Select(pair => pair.Value)))
            {
                if (!instance.Visible || instance.Mesh is not { } mesh) continue;
                for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                {
                    var material = instance.MaterialOverride ?? mesh.SurfaceGetMaterial(surface);
                    int color = material == Material(FootpathArtwork.FreshColor) ? FootpathArtwork.FreshColor :
                        material == Material(FootpathArtwork.WornColor) ? FootpathArtwork.WornColor : 0;
                    if (color == 0) continue;
                    using var arrays = mesh.SurfaceGetArrays(surface);
                    var points = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                    var uv = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
                    if (arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil)
                        for (int i = 0; i < points.Length; i++) vertices.Add((color, points[i], normals[i], uv[i]));
                    else
                        foreach (int i in arrays[(int)Mesh.ArrayType.Index].AsInt32Array())
                            vertices.Add((color, points[i], normals[i], uv[i]));
                }
            }
            return vertices;
        }
    }

    private void AppendDirtVertex(SurfaceVertex vertex)
    {
        if (!_footpathBatches.TryGetValue(vertex.Color, out var surface)) _footpathBatches.Add(vertex.Color, surface = []);
        surface.Add(vertex);
        _capturingBuilding?.Dirt.Add(vertex);
    }

    private static bool TryDirtTriangle(FootpathReliefTriangle face,
        out SurfaceVertex first, out SurfaceVertex second, out SurfaceVertex third)
    {
        var a = new Vector3(face.A.X, face.A.Y, face.A.Z);
        var b = new Vector3(face.B.X, face.B.Y, face.B.Z);
        var c = new Vector3(face.C.X, face.C.Y, face.C.Z);
        var normal = (c - a).Cross(b - a);
        if (normal.LengthSquared() < 1e-14f) { first = second = third = default; return false; }
        if (normal.Y < 0) { (b, c) = (c, b); normal = -normal; }
        normal = normal.Normalized();
        SurfaceVertex Vertex(Vector3 point) =>
            new(face.Color, point, normal, new Vector2(point.X, point.Z) * FootpathArtwork.UvScale);
        first = Vertex(a); second = Vertex(b); third = Vertex(c);
        return true;
    }

    private bool EmitDirtTriangle(FootpathReliefTriangle face, bool measurePath = false)
    {
        if (!TryDirtTriangle(face, out var a, out var b, out var c)) return false;
        AppendDirtVertex(a); AppendDirtVertex(b); AppendDirtVertex(c);
        if (measurePath)
        {
            if (MeasureFootpathFaces) FootpathFaces.Add((a.Point, b.Point, c.Point));
            MaximumFootpathTriangleRise = Math.Max(MaximumFootpathTriangleRise,
                Math.Max(a.Point.Y, Math.Max(b.Point.Y, c.Point.Y)) - Math.Min(a.Point.Y, Math.Min(b.Point.Y, c.Point.Y)));
            if (a.Normal.Y < .999f) SlopedFootpathTrianglesDrawn++;
        }
        return true;
    }
}
