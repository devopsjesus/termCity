using Godot;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private sealed class GroundDrawPage
    {
        internal readonly Node3D Root = new();
        internal readonly HashSet<(int X, int Y)> Published = [];
        internal readonly List<ArrayMesh> Meshes = [];
        internal readonly Dictionary<(int X, int Y), RoadSurfaceMeshes.PreparedChunk> Sources = [];
        internal readonly Dictionary<(int X, int Y), int[]> Surfaces = [];
        internal readonly Dictionary<(int X, int Y), long> NativeBytes = [];
        internal readonly Dictionary<(int X, int Y), ulong> Stamps = [];
        internal bool Validated;
    }
    private bool _groundDrawPagesEnabled;
    private bool _stableGroundPages;
    private int _groundPageSpan = 2;
    private readonly Dictionary<(int X, int Y), GroundDrawPage> _groundDrawPages = [];
    private readonly HashSet<(int X, int Y)> _dirtyGroundPages = [], _pendingGroundPublication = [];
    internal long GroundPageUploadBytes { get; private set; }
    internal int GroundPagesRebuilt { get; private set; }
    private long GroundPageManagedBytes => _groundDrawPages.Values.Sum(page => page.Sources.Where(pair =>
        !_preparedGround.TryGetValue(pair.Key, out var current) || !ReferenceEquals(current, pair.Value))
        .Sum(pair => GroundBytes(pair.Value) - (pair.Value.Textures is { } pixels &&
            ReferenceEquals(_terrainPixels.GetValueOrDefault(pair.Key), pixels) ? pixels.Bytes : 0)));
    private long GroundPageRetiringGpuBytes => _groundDrawPages.Values.Sum(page => page.NativeBytes.Where(pair =>
        !_groundNodes.ContainsKey(pair.Key)).Sum(pair => Math.Max(0, pair.Value - _groundGpuBytes.GetValueOrDefault(pair.Key))) + page.Sources.Where(pair =>
        !_groundNodes.ContainsKey(pair.Key)).Sum(pair => MeshUploadBytes(pair.Value.Ground) +
            MeshUploadBytes(pair.Value.Roads) + MeshUploadBytes(pair.Value.Banks) +
            (pair.Value.Textures?.NativeBytes ?? 0)));
    private int GroundRenderNodeCount => _groundDrawPagesEnabled
        ? _groundDrawPages.Values.Sum(page => page.Root.GetChildCount())
        : _groundNodes.Values.Sum(node => node.GetChildCount());
    private (int X, int Y) GroundPageKey((int X, int Y) key) => (key.X / _groundPageSpan, key.Y / _groundPageSpan);

    private void MarkGroundPageDirty((int X, int Y) key)
    {
        if (!_groundDrawPagesEnabled) return;
        _dirtyGroundPages.Add(GroundPageKey(key));
    }

    private void ConfigureGroundDrawPages(bool enabled)
    {
        if (_groundDrawPagesEnabled == enabled) return;
        foreach (var key in _roadChunks.Keys.ToArray()) RetireGpuGround(key);
        DisposeGroundDrawPages();
        _groundDrawPagesEnabled = enabled;
        InvalidateStaticScene(discardBuildingCache: false);
    }

    private void DisposeGroundDrawPages()
    {
        foreach (var page in _groundDrawPages.Values)
        {
            _groundRoot.RemoveChild(page.Root);
            page.Root.QueueFree();
            foreach (var mesh in page.Meshes) mesh.Dispose();
        }
        _groundDrawPages.Clear();
        _dirtyGroundPages.Clear();
        _pendingGroundPublication.Clear();
    }

    private void ApplyGroundDrawPages()
    {
        if (!_groundDrawPagesEnabled || _dirtyGroundPages.Count == 0) return;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var key in _dirtyGroundPages.OrderBy(key => _groundNodes.Any(pair =>
            GroundPageKey(pair.Key) == key && pair.Value.Visible) ? 0 : 1).ToArray())
        {
            RebuildGroundDrawPage(key);
            _dirtyGroundPages.Remove(key);
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 4) break;
        }
    }

    private void RebuildGroundDrawPage((int X, int Y) key)
    {
        if (_persistentResidency)
        {
            RebuildPersistentGroundPage(key);
            return;
        }
        var owners = _groundNodes.Where(pair => GroundPageKey(pair.Key) == key && (_stableGroundPages || pair.Value.Visible))
            .OrderBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.X).Select(pair => pair.Key).ToArray();
        if (owners.Length == 0)
        {
            if (_groundDrawPages.Remove(key, out var empty))
            {
                _groundRoot.RemoveChild(empty.Root);
                empty.Root.QueueFree();
                foreach (var mesh in empty.Meshes) mesh.Dispose();
            }
            return;
        }
        var page = new GroundDrawPage();
        var ground = new ArrayMesh();
        page.Meshes.Add(ground);
        foreach (var owner in owners)
        {
            var packet = _preparedGround[owner];
            page.Sources.Add(owner, packet);
            AddSurface(ground, packet.Ground);
            ground.SurfaceSetMaterial(ground.GetSurfaceCount() - 1, _groundMaterials[owner].Material);
        }
        page.Root.AddChild(new MeshInstance3D { Mesh = ground });
        AddInfrastructure(owners.Select(owner => _preparedGround[owner].Roads), _roadMaterial);
        AddInfrastructure(owners.Select(owner => _preparedGround[owner].Banks), _cliffMaterial);
        foreach (var owner in owners)
        {
            page.Published.Add(owner);
            _pendingGroundPublication.Remove(owner);
            if (_groundNodes[owner].Visible) _mountedGroundChunks.Add(owner);
        }
        page.Root.Visible = owners.Any(owner => _groundNodes[owner].Visible);
        _groundRoot.AddChild(page.Root);
        if (_groundDrawPages.Remove(key, out var old))
        {
            _groundRoot.RemoveChild(old.Root);
            old.Root.QueueFree();
            foreach (var mesh in old.Meshes) mesh.Dispose();
        }

        _groundDrawPages.Add(key, page);
        GroundPagesRebuilt++;
        RequestRender();

        void AddInfrastructure(IEnumerable<RoadSurfaceMeshes.MeshData?> source, Material material)
        {
            var packets = source.OfType<RoadSurfaceMeshes.MeshData>().Where(packet => packet.Points.Length > 0).ToArray();
            if (packets.Length == 0) return;
            var combined = RoadSurfaceMeshes.MeshData.Combine(packets);
            var mesh = new ArrayMesh();
            AddSurface(mesh, combined);
            mesh.SurfaceSetMaterial(0, material);
            page.Meshes.Add(mesh);
            page.Root.AddChild(new MeshInstance3D { Mesh = mesh });
        }
    }

    private void RebuildPersistentGroundPage((int X, int Y) key)
    {
        _groundDrawPages.TryGetValue(key, out var previous);
        var owners = _groundNodes.Keys.Where(owner => GroundPageKey(owner) == key)
            .OrderBy(owner => owner.Y).ThenBy(owner => owner.X).ToArray();
        if (previous is not null && previous.Published.All(owner => _groundNodes.ContainsKey(owner) &&
            previous.Stamps.GetValueOrDefault(owner) == _groundGpuStamps.GetValueOrDefault(owner)))
        {
            foreach (var owner in owners.Where(owner => !previous.Published.Contains(owner)))
            {
                var packet = _preparedGround[owner];
                var indices = new[] { -1, -1, -1 };
                for (int kind = 0; kind < 3; kind++)
                {
                    var data = kind == 0 ? packet.Ground : kind == 1 ? packet.Roads : packet.Banks;
                    if (data is not { Points.Length: > 0 }) continue;
                    var mesh = previous.Meshes[kind];
                    indices[kind] = mesh.GetSurfaceCount();
                    AddSurface(mesh, data);
                    mesh.SurfaceSetMaterial(indices[kind], kind == 0 ? _groundMaterials[owner].Material :
                        kind == 1 ? _roadMaterial : _cliffMaterial);
                    if (indices[kind] == 0) previous.Root.AddChild(new MeshInstance3D { Mesh = mesh });
                }
                previous.Surfaces.Add(owner, indices);
                previous.Published.Add(owner);
                previous.NativeBytes.Add(owner, _groundGpuBytes[owner]);
                previous.Stamps.Add(owner, _groundGpuStamps[owner]);
                _pendingGroundPublication.Remove(owner);
                if (_groundNodes[owner].Visible) _mountedGroundChunks.Add(owner);
            }
            previous.Validated = false;
            previous.Root.Visible = owners.Any(owner => _groundNodes[owner].Visible);
            GroundPagesRebuilt++;
            ReleaseResidentGroundReplay();
            RequestRender();
            return;
        }
        var page = new GroundDrawPage();
        for (int kind = 0; kind < 3; kind++) page.Meshes.Add(new ArrayMesh());
        foreach (var owner in owners)
        {
            var indices = new[] { -1, -1, -1 };
            var packet = _preparedGround.GetValueOrDefault(owner);
            for (int kind = 0; kind < 3; kind++)
            {
                var mesh = page.Meshes[kind];
                var data = kind == 0 ? packet?.Ground : kind == 1 ? packet?.Roads : packet?.Banks;
                if (data is { Points.Length: > 0 })
                {
                    indices[kind] = mesh.GetSurfaceCount();
                    AddSurface(mesh, data);
                }
                else if (packet is null && previous is not null &&
                    previous.Surfaces.TryGetValue(owner, out var old) && old[kind] >= 0)
                {
                    indices[kind] = mesh.GetSurfaceCount();
                    using var arrays = previous.Meshes[kind].SurfaceGetArrays(old[kind]);
                    mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                    long bytes = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().LongLength * 12 +
                        arrays[(int)Mesh.ArrayType.Color].AsColorArray().LongLength * 16 +
                        arrays[(int)Mesh.ArrayType.Index].AsInt32Array().LongLength * 4;
                    if (arrays[(int)Mesh.ArrayType.Normal].VariantType != Variant.Type.Nil)
                        bytes += arrays[(int)Mesh.ArrayType.Normal].AsVector3Array().LongLength * 12;
                    if (arrays[(int)Mesh.ArrayType.TexUV].VariantType != Variant.Type.Nil)
                        bytes += arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array().LongLength * 8;
                    GroundPageUploadBytes += bytes;
                    GroundUploadBytes += bytes;
                }
                else if (kind == 0)
                    throw new InvalidOperationException($"Persistent ground owner {owner} has neither prepared nor installed geometry.");
                if (indices[kind] >= 0)
                    mesh.SurfaceSetMaterial(indices[kind], kind == 0 ? _groundMaterials[owner].Material :
                        kind == 1 ? _roadMaterial : _cliffMaterial);
            }
            page.Surfaces.Add(owner, indices);
            page.NativeBytes.Add(owner, _groundGpuBytes[owner]);
            page.Stamps.Add(owner, _groundGpuStamps[owner]);
            page.Published.Add(owner);
            _pendingGroundPublication.Remove(owner);
            if (_groundNodes[owner].Visible) _mountedGroundChunks.Add(owner);
        }
        for (int kind = 0; kind < 3; kind++)
            if (page.Meshes[kind].GetSurfaceCount() > 0)
                page.Root.AddChild(new MeshInstance3D { Mesh = page.Meshes[kind] });
        page.Root.Visible = owners.Any(owner => _groundNodes[owner].Visible);
        if (owners.Length > 0)
        {
            _groundRoot.AddChild(page.Root);
            _groundDrawPages[key] = page;
        }
        else
        {
            page.Root.Free();
            foreach (var mesh in page.Meshes) mesh.Dispose();
            _groundDrawPages.Remove(key);
        }
        if (previous is not null)
        {
            _groundRoot.RemoveChild(previous.Root);
            previous.Root.QueueFree();
            foreach (var mesh in previous.Meshes) mesh.Dispose();
        }
        GroundPagesRebuilt++;
        ReleaseResidentGroundReplay();
        RequestRender();
    }

    private bool GroundPageHasOwner((int X, int Y) key) =>
        _groundDrawPages.TryGetValue(GroundPageKey(key), out var page) && page.Published.Contains(key);

    private void SyncStableGroundPageVisibility()
    {
        if (!_groundDrawPagesEnabled || !_stableGroundPages) return;
        foreach (var page in _groundDrawPages.Values)
            page.Root.Visible = page.Published.Any(owner => _groundNodes.TryGetValue(owner, out var node) && node.Visible);
    }

    private void AddSurface(ArrayMesh mesh, RoadSurfaceMeshes.MeshData packet)
    {
        packet.AppendTo(mesh);
        long bytes = MeshUploadBytes(packet);
        GroundPageUploadBytes += bytes;
        GroundUploadBytes += bytes;
    }

    internal void ValidateGroundDrawPages()
    {
        if (!_groundDrawPagesEnabled) return;
        foreach (var page in _groundDrawPages.Values)
        {
            if (page.Validated) continue;
            if (_persistentResidency)
            {
                var session = _streamSession ?? throw new InvalidOperationException("Native page validation requires a session.");
                var fresh = _roadSurfaces!.Fresh();
                foreach (var (owner, surfaces) in page.Surfaces)
                {
                    if (!_groundNodes.ContainsKey(owner)) continue;
                    var packet = fresh.Prepare(session, owner.X, owner.Y);
                    for (int kind = 0; kind < 3; kind++)
                    {
                        var expected = kind == 0 ? packet.Ground : kind == 1 ? packet.Roads : packet.Banks;
                        if (expected is { Points.Length: > 0 })
                        {
                            if (surfaces[kind] < 0) throw new InvalidOperationException("Persistent page omitted a nonempty ground surface.");
                            try { ValidateSurface(page.Meshes[kind], surfaces[kind], expected, normalTolerance: .001f); }
                            catch (InvalidOperationException error)
                            {
                                throw new InvalidOperationException($"Persistent owner={owner} kind={kind} " +
                                    $"published={page.Stamps.GetValueOrDefault(owner)} current={_groundGpuStamps.GetValueOrDefault(owner)}: {error.Message}", error);
                            }
                        }
                        else if (surfaces[kind] >= 0) throw new InvalidOperationException("Persistent page retained an unexpected surface.");
                    }
                }
                page.Validated = true;
                continue;
            }
            var owners = page.Published.OrderBy(owner => owner.Y).ThenBy(owner => owner.X).ToArray();
            if (owners.Length < 1 || owners.Length > _groundPageSpan * _groundPageSpan)
                throw new InvalidOperationException("Ground page ownership exceeds its configured chunk grid.");
            for (int surface = 0; surface < owners.Length; surface++)
            {
                var expected = page.Sources[owners[surface]].Ground;
                ValidateSurface(page.Meshes[0], surface, expected);
            }
            int infrastructure = 1;
            foreach (bool roads in new[] { true, false })
            {
                var packets = owners.Select(owner => roads ? page.Sources[owner].Roads : page.Sources[owner].Banks)
                    .OfType<RoadSurfaceMeshes.MeshData>().Where(packet => packet.Points.Length > 0).ToArray();
                if (packets.Length == 0) continue;
                var expected = RoadSurfaceMeshes.MeshData.Combine(packets);
                ValidateSurface(page.Meshes[infrastructure++], 0, expected);
            }
            if (infrastructure != page.Meshes.Count)
                throw new InvalidOperationException("Ground page has unexpected infrastructure surfaces.");
            page.Validated = true;

            static void ValidateSurface(ArrayMesh mesh, int surface, RoadSurfaceMeshes.MeshData expected, float normalTolerance = 0)
            {
                using var arrays = mesh.SurfaceGetArrays(surface);
                var points = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var colors = arrays[(int)Mesh.ArrayType.Color].AsColorArray();
                var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                var normals = expected.Normals is null ? null : arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                using var referenceMesh = expected.Upload();
                using var referenceArrays = referenceMesh.SurfaceGetArrays(0);
                var referenceNormals = expected.Normals is null ? null : referenceArrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                if (!points.SequenceEqual(expected.Points) || !indices.SequenceEqual(expected.Indices ?? []) ||
                    colors.Length != expected.Colors.Length ||
                    expected.Uvs is not null && !arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array().SequenceEqual(expected.Uvs) ||
                    expected.Normals is not null && normals!.Zip(referenceNormals!).Any(pair =>
                        pair.First.DistanceTo(pair.Second) > normalTolerance) ||
                    colors.Zip(expected.Colors).Any(pair => Math.Abs(pair.First.R - pair.Second.R) > .005f ||
                        Math.Abs(pair.First.G - pair.Second.G) > .005f || Math.Abs(pair.First.B - pair.Second.B) > .005f ||
                        Math.Abs(pair.First.A - pair.Second.A) > .005f))
                    throw new InvalidOperationException($"Paged terrain/road/bank native geometry differs at surface {surface}: " +
                        $"vertices={points.Length}/{expected.Points.Length}, points-exact={points.SequenceEqual(expected.Points)}, " +
                        $"uv-exact={expected.Uvs is null || arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array().SequenceEqual(expected.Uvs)}, " +
                        $"normal-error={(normals is null ? 0 : normals.Zip(expected.Normals!).Max(pair => pair.First.DistanceTo(pair.Second)))}, " +
                        $"color-error={colors.Zip(expected.Colors).Max(pair => Math.Max(Math.Abs(pair.First.R - pair.Second.R), Math.Max(Math.Abs(pair.First.G - pair.Second.G), Math.Abs(pair.First.B - pair.Second.B))))}.");
            }
        }
    }
}
