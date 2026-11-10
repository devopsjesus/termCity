using Godot;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    private readonly record struct ObjectPoolKey(int Material, Pos Page);
    private bool _adaptiveObjectUploads;
    private ObjectPoolKey PoolKey(int material, Pos owner) =>
        new(material, material < 0 || _persistentObjectResidency
            ? new(owner.X / _objectMaterialPageSpan, owner.Y / _objectMaterialPageSpan) : default);
    private sealed class ObjectPool(MultiMesh mesh, MultiMeshInstance3D node, int stride)
    {
        internal readonly MultiMesh Mesh = mesh;
        internal readonly MultiMeshInstance3D Node = node;
        internal readonly int Stride = stride;
        internal float[] Data = [];
        internal int Tail;
        internal readonly List<(int Start, int Count)> FreeRanges = [];
    }

    private readonly Node3D _streamedSolids = new();
    private readonly Dictionary<ObjectPoolKey, ObjectPool> _objectPools = [];
    private readonly Dictionary<Pos, MeshInstance3D> _objectRoofPages = [];
    private ObjectChunk? _uploadingObject;
    private IEnumerator<bool>? _objectUpload;
    internal int ObjectChunksPublished { get; private set; }
    internal int ObjectInstancesUploaded { get; private set; }
    internal long ObjectUploadBytes { get; private set; }
    internal long ObjectRoofUploadBytes { get; private set; }
    internal int ObjectWholeBufferWrites { get; private set; }
    internal int ObjectInstanceWrites { get; private set; }
    internal int ObjectPoolGrowths { get; private set; }
    internal int ObjectPoolCompactions { get; private set; }
    internal long ObjectResidentBufferBytes => _objectPools.Values.Sum(pool => (long)pool.Data.Length * sizeof(float));
    private long ObjectPoolMetadataBytes => _objectPools.Values.Sum(pool => 128L + pool.FreeRanges.Capacity * 8L);

    internal void PrintObjectUploadProfile(string label)
    {
        int capacity = _objectPools.Values.Sum(pool => pool.Data.Length / pool.Stride);
        int tail = _objectPools.Values.Sum(pool => pool.Tail);
        int live = _visibleObjectChunks.Values.Sum(chunk => chunk.Slots.Values.Sum(range => range.Count));
        if (_uploadingObject is { } upload && _visibleObjectChunks.GetValueOrDefault(upload.Key) != upload)
            live += upload.Slots.Values.Sum(range => range.Count);
        GD.Print($"TERMCITY_OBJECT_UPLOAD_PROFILE phase={label} whole-writes={ObjectWholeBufferWrites} " +
            $"instance-writes={ObjectInstanceWrites} growths={ObjectPoolGrowths} compactions={ObjectPoolCompactions} " +
            $"capacity={capacity} live={live} holes={Math.Max(0, tail - live)} " +
            $"slack={Math.Max(0, capacity - tail)} resident-bytes={ObjectResidentBufferBytes} " +
            $"roof-upload-bytes={ObjectRoofUploadBytes} path-upload-bytes={FootpathUploadBytes} " +
            $"resident-object-pools={_objectPools.Count} active-object-pools={_objectPools.Values.Count(pool => pool.Node.Visible)} " +
            $"resident-roof-pages={_objectRoofPages.Count} active-roof-pages={_objectRoofPages.Values.Count(node => node.Visible)}");
    }

    private void RemoveUnusedObjectPools()
    {
        var used = _visibleObjectChunks.Values.SelectMany(chunk => chunk.Slots.Keys).ToHashSet();
        if (_uploadingObject is { } pending) used.UnionWith(pending.Slots.Keys);
        foreach (var key in _objectPools.Keys.Where(key => !used.Contains(key)).ToArray())
        {
            var pool = _objectPools[key];
            _streamedSolids.RemoveChild(pool.Node);
            pool.Node.QueueFree();
            pool.Mesh.Dispose();
            _objectPools.Remove(key);
        }
    }
    internal void ValidateObjectBuffers()
    {
        if (_persistentObjectResidency)
            foreach (var (key, pool) in _objectPools)
                foreach (var free in pool.FreeRanges)
                {
                    if (free.Start < 0 || free.Count <= 0 || free.Start + free.Count > pool.Tail ||
                        _visibleObjectChunks.Values.Any(chunk => chunk.Slots.TryGetValue(key, out var range) &&
                            free.Start < range.Start + range.Count && range.Start < free.Start + free.Count))
                        throw new InvalidOperationException("Reusable native slots overlap a published owner or exceed capacity.");
                }
        foreach (var chunk in _visibleObjectChunks.Values)
            foreach (var (key, data) in chunk.Buffers)
            {
                var poolKey = PoolKey(key, chunk.Key);
                var pool = _objectPools[poolKey];
                var range = chunk.Slots[poolKey];
                for (int index = 0; index < range.Count; index++)
                {
                    int offset = index * pool.Stride;
                    if (!data.AsSpan(offset, pool.Stride).SequenceEqual(
                        pool.Data.AsSpan((range.Start + index) * pool.Stride, pool.Stride)))
                        throw new InvalidOperationException("Published instance backing arrays differ from retained geometry.");
                    if (DisplayServer.GetName() == "headless") continue;
                    if (!InstanceTransform(data, offset).IsEqualApprox(pool.Mesh.GetInstanceTransform(range.Start + index)))
                        throw new InvalidOperationException("Streamed instance geometry does not match its published picking payload.");
                    if (pool.Stride == 16)
                    {
                        var expected = new Color(data[offset + 12], data[offset + 13], data[offset + 14], data[offset + 15]);
                        var actual = pool.Mesh.GetInstanceColor(range.Start + index);
                        if (Math.Abs(expected.R - actual.R) > .005f || Math.Abs(expected.G - actual.G) > .005f ||
                            Math.Abs(expected.B - actual.B) > .005f || Math.Abs(expected.A - actual.A) > .005f)
                            throw new InvalidOperationException(
                                $"Streamed instance colors differ: chunk={chunk.Key}, group={key}, index={index}, expected={expected}, actual={actual}.");
                    }
                }
            }
    }

    private void CompactIdleObjectPool()
    {
        if (_persistentObjectResidency) return;
        foreach (var (key, pool) in _objectPools)
        {
            int live = _visibleObjectChunks.Values.Sum(chunk => chunk.Slots.TryGetValue(key, out var range) ? range.Count : 0);
            if (pool.Tail <= live + Math.Max(32, live / 10)) continue;
            var packed = new float[Math.Max(1024, live * 2) * pool.Stride];
            int next = 0;
            foreach (var chunk in _visibleObjectChunks.Values)
                if (chunk.Slots.TryGetValue(key, out var range))
                {
                    Array.Copy(pool.Data, range.Start * pool.Stride, packed, next * pool.Stride, range.Count * pool.Stride);
                    chunk.Slots[key] = (next, range.Count);
                    next += range.Count;
                }
            pool.Data = packed;
            pool.Tail = next;
            pool.Mesh.InstanceCount = packed.Length / pool.Stride;
            pool.Mesh.Buffer = packed;
            ObjectWholeBufferWrites++;
            ObjectPoolCompactions++;
            ObjectUploadBytes += (long)packed.Length * sizeof(float);
            pool.Mesh.VisibleInstanceCount = next;
            break;
        }
    }

    private ObjectPool Pool(ObjectPoolKey key)
    {
        if (_objectPools.TryGetValue(key, out var pool)) return pool;
        bool colors = key.Material < 0;
        var mesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = colors,
            Mesh = _cube, VisibleInstanceCount = 0,
        };
        var node = new MultiMeshInstance3D
        {
            Multimesh = mesh, MaterialOverride = key.Material == -1 ? _instanceUnshadedMaterial :
                key.Material == -2 ? _instanceColorMaterial : Material(key.Material, solid: true),
        };
        _streamedSolids.AddChild(node);
        pool = new(mesh, node, colors ? 16 : 12);
        _objectPools.Add(key, pool);
        return pool;
    }

    private int ReserveObjectInstances(ObjectPoolKey key, int count)
    {
        var pool = Pool(key);
        if (_persistentObjectResidency)
        {
            int best = -1;
            for (int index = 0; index < pool.FreeRanges.Count; index++)
                if (pool.FreeRanges[index].Count >= count &&
                    (best < 0 || pool.FreeRanges[index].Count < pool.FreeRanges[best].Count)) best = index;
            if (best >= 0)
            {
                var range = pool.FreeRanges[best];
                pool.FreeRanges.RemoveAt(best);
                if (range.Count > count) pool.FreeRanges.Add((range.Start + count, range.Count - count));
                return range.Start;
            }
        }
        int live = _visibleObjectChunks.Values.Sum(chunk => chunk.Slots.TryGetValue(key, out var range) ? range.Count : 0);
        if (!_persistentObjectResidency && pool.Tail > Math.Max(1024, live * 2))
        {
            int next = 0;
            var packed = new float[Math.Max(1024, (live + count) * 2) * pool.Stride];
            foreach (var chunk in _visibleObjectChunks.Values)
                if (chunk.Slots.TryGetValue(key, out var range))
                {
                    Array.Copy(pool.Data, range.Start * pool.Stride, packed, next * pool.Stride, range.Count * pool.Stride);
                    chunk.Slots[key] = (next, range.Count);
                    next += range.Count;
                }
            pool.Data = packed;
            pool.Tail = next;
            pool.Mesh.InstanceCount = packed.Length / pool.Stride;
            pool.Mesh.Buffer = packed;
            ObjectWholeBufferWrites++;
            ObjectPoolCompactions++;
            ObjectUploadBytes += (long)packed.Length * sizeof(float);
            pool.Mesh.VisibleInstanceCount = next;
        }
        int start = pool.Tail;
        int needed = (start + count) * pool.Stride;
        if (pool.Data.Length < needed)
        {
            int capacity = Math.Max(_persistentObjectResidency ? 64 : 1024, pool.Data.Length / pool.Stride);
            while (capacity < start + count) capacity *= 2;
            Array.Resize(ref pool.Data, capacity * pool.Stride);
            ObjectPoolGrowths++;
            int visible = pool.Mesh.VisibleInstanceCount;
            pool.Mesh.InstanceCount = capacity;
            pool.Mesh.Buffer = pool.Data;
            ObjectWholeBufferWrites++;
            ObjectUploadBytes += (long)pool.Data.Length * sizeof(float);
            pool.Mesh.VisibleInstanceCount = Math.Max(0, visible);
        }
        pool.Tail += count;
        return start;
    }

    private static Transform3D InstanceTransform(float[] data, int offset) => new(
        new Basis(new(data[offset], data[offset + 4], data[offset + 8]),
            new(data[offset + 1], data[offset + 5], data[offset + 9]),
            new(data[offset + 2], data[offset + 6], data[offset + 10])),
        new(data[offset + 3], data[offset + 7], data[offset + 11]));

    private void BeginObjectUpload(ObjectChunk chunk)
    {
        _usefulResidencyDirty = true;
        chunk.NativeUploads++;
        chunk.State = ChunkLifecycle.GpuStaging;
        _uploadingObject = chunk;
        chunk.Slots.Clear();
        _objectUpload = UploadObjectChunk(chunk);
    }

    private IEnumerator<bool> UploadObjectChunk(ObjectChunk chunk)
    {
        foreach (var (key, data) in chunk.Buffers)
        {
            var poolKey = PoolKey(key, chunk.Key);
            var pool = Pool(poolKey);
            int count = data.Length / pool.Stride;
            int start = ReserveObjectInstances(poolKey, count);
            chunk.Slots.Add(poolKey, (start, count));
            Array.Copy(data, 0, pool.Data, start * pool.Stride, data.Length);
            if (_persistentObjectResidency)
                yield return true;
            else if (count > 128 && (!_adaptiveObjectUploads || count * 4 >= pool.Data.Length / pool.Stride))
            {
                pool.Mesh.Buffer = pool.Data;
                ObjectWholeBufferWrites++;
                ObjectUploadBytes += (long)pool.Data.Length * sizeof(float);
                ObjectInstancesUploaded += count;
                yield return true;
            }
            else
                for (int index = 0; index < count; index++)
                {
                    int offset = index * pool.Stride;
                    pool.Mesh.SetInstanceTransform(start + index, InstanceTransform(data, offset));
                    if (pool.Stride == 16)
                        pool.Mesh.SetInstanceColor(start + index,
                            new(data[offset + 12], data[offset + 13], data[offset + 14], data[offset + 15]));
                    ObjectInstancesUploaded++;
                    ObjectInstanceWrites++;
                    ObjectUploadBytes += pool.Stride * sizeof(float);
                    yield return true;
                }
        }
        var page = new Pos(chunk.Key.X / 2, chunk.Key.Y / 2);
        var pageChunks = _visibleObjectChunks.Values.Where(other => other.Key != chunk.Key &&
            other.Key.X / 2 == page.X && other.Key.Y / 2 == page.Y).Append(chunk)
            .OrderBy(other => other.Key.Y).ThenBy(other => other.Key.X).ToArray();
        if (pageChunks.Any(other => other.RoofBuffers.Count > 0 || other.Geometry.Zones.Count > 0))
        {
            using var mesh = new ArrayMesh();
            var node = new MeshInstance3D { Mesh = mesh, Visible = false };
            _streamedSolids.AddChild(node);
            chunk.RoofNode = node;
            foreach (var group in pageChunks.SelectMany(other => other.RoofBuffers).GroupBy(pair => pair.Key))
            {
                int count = group.Sum(pair => pair.Value.Points.Length);
                var points = new Vector3[count];
                var normals = new Vector3[count];
                var uvs = new Vector2[count];
                int offset = 0;
                foreach (var (_, buffer) in group)
                {
                    Array.Copy(buffer.Points, 0, points, offset, buffer.Points.Length);
                    Array.Copy(buffer.Normals, 0, normals, offset, buffer.Normals.Length);
                    Array.Copy(buffer.Uvs, 0, uvs, offset, buffer.Uvs.Length);
                    offset += buffer.Points.Length;
                }
                using var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = points;
                arrays[(int)Mesh.ArrayType.Normal] = normals;
                arrays[(int)Mesh.ArrayType.TexUV] = uvs;
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                ObjectRoofUploadBytes += (long)count * 32;
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, Material(group.Key));
                yield return true;
            }
            foreach (var (visual, surface) in pageChunks.SelectMany(other => other.Geometry.Zones))
            {
                using var zone = ZonePacket(surface).Upload();
                if (zone.GetSurfaceCount() > 0)
                {
                    using var arrays = zone.SurfaceGetArrays(0);
                    mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                    mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, ZoneMaterial(visual));
                }
                yield return true;
            }
        }
        if (_streamSession is not { } session || !_wantedObjectChunks.Contains(chunk.Key) ||
            ObjectStamp(session, chunk.Key) != chunk.Stamp)
        {
            foreach (var (key, range) in chunk.Slots)
            {
                var pool = _objectPools[key];
                if (_persistentObjectResidency) ReleaseInstanceRange(pool, range);
                else pool.Tail = range.Start;
            }
            chunk.Slots.Clear();
            chunk.RoofNode?.QueueFree();
            chunk.RoofNode = null;
            _objectChunks.Remove(chunk.Key);
            DisposeObjectGeometry(chunk);
            if (_wantedObjectChunks.Contains(chunk.Key)) _objectRequests.Enqueue(chunk.Key);
            yield break;
        }
        if (_visibleObjectChunks.TryGetValue(chunk.Key, out var replaced))
        {
            RetireObjectChunk(chunk.Key);
            if (replaced != chunk) RetainAlternateLod(replaced);
        }
        foreach (var (key, range) in chunk.Slots)
        {
            var pool = _objectPools[key];
            if (_persistentObjectResidency)
            {
                pool.Mesh.Buffer = pool.Data;
                ObjectWholeBufferWrites++;
                ObjectUploadBytes += pool.Data.LongLength * sizeof(float);
                ObjectInstancesUploaded += range.Count;
            }
            pool.Mesh.VisibleInstanceCount = pool.Tail;
        }
        if (chunk.RoofNode is { } roof)
        {
            if (_objectRoofPages.Remove(page, out var previous))
            {
                _streamedSolids.RemoveChild(previous);
                previous.QueueFree();
            }
            roof.Visible = true;
            _objectRoofPages.Add(page, roof);
            chunk.RoofNode = null;
        }
        else if (_objectRoofPages.Remove(page, out var empty))
        {
            _streamedSolids.RemoveChild(empty);
            empty.QueueFree();
        }
        _visibleObjectChunks[chunk.Key] = chunk;
        chunk.State = ChunkLifecycle.Published;
        ObjectChunksPublished++;
        _sceneTrees.UnionWith(chunk.Geometry.Trees);
        RefreshPublishedObjects();
        RefreshResidentObjectVisibility();
        RequestRender();
    }

    private bool AdvanceObjectUpload()
    {
        if (_objectUpload is null) return true;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool more;
        try { more = _objectUpload.MoveNext(); }
        finally { _objectUploadMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
        if (more) return false;
        _objectUpload.Dispose();
        _objectUpload = null;
        _uploadingObject = null;
        return true;
    }

    private void RetireObjectChunk(Pos key, bool upload = true)
    {
        if (!_visibleObjectChunks.Remove(key, out var chunk)) return;
        var page = new Pos(key.X / 2, key.Y / 2);
        if (!_visibleObjectChunks.Keys.Any(other => other.X / 2 == page.X && other.Y / 2 == page.Y) &&
            _objectRoofPages.Remove(page, out var roof))
        {
            _streamedSolids.RemoveChild(roof);
            roof.QueueFree();
        }
        foreach (var (material, range) in chunk.Slots)
        {
            var pool = _objectPools[material];
            Array.Clear(pool.Data, range.Start * pool.Stride, range.Count * pool.Stride);
            if (_persistentObjectResidency) ReleaseInstanceRange(pool, range);
            if (upload)
            {
                pool.Mesh.Buffer = pool.Data;
                ObjectWholeBufferWrites++;
                ObjectUploadBytes += (long)pool.Data.Length * sizeof(float);
            }
        }
        chunk.Slots.Clear();
        chunk.State = ChunkLifecycle.CpuReady;
    }

    private static void ReleaseInstanceRange(ObjectPool pool, (int Start, int Count) range)
    {
        if (range.Count == 0) return;
        Array.Clear(pool.Data, range.Start * pool.Stride, range.Count * pool.Stride);
        pool.FreeRanges.Add(range);
        pool.FreeRanges.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int index = pool.FreeRanges.Count - 2; index >= 0; index--)
        {
            var first = pool.FreeRanges[index]; var next = pool.FreeRanges[index + 1];
            if (first.Start + first.Count != next.Start) continue;
            pool.FreeRanges[index] = (first.Start, first.Count + next.Count);
            pool.FreeRanges.RemoveAt(index + 1);
        }
    }

    private void ClearObjectStreaming()
    {
        foreach (var work in _objectEncodings)
        {
            work.Task.GetAwaiter().GetResult();
            DisposeObjectGeometry(work.Chunk);
        }
        _objectEncodings.Clear();
        _objectPreparation?.Dispose();
        _objectUpload?.Dispose();
        _objectPreparation = _objectUpload = null;
        DisposeObjectGeometry(_preparingObject);
        _uploadingObject?.RoofNode?.QueueFree();
        _preparingObject = _uploadingObject = null;
        _objectRequests.Clear();
        _objectRequestState = null;
        _objectStreamingFailure = null;
        _objectRequestCenter = null;
        _objectTravelDirection = Vector2.Zero;
        _warmingCursor = 0;
        _recentPopulatedDestination.Clear();
        _previousDestination.Clear();
        _warmingVersion = null;
        _warmingVisited.Clear();
        _preparationGround.Clear();
        _ownedBuildings.Clear();
        _ownedYards.Clear();
        _ownershipVersion = null;
        _ownershipYards = null;
        _requiredObjectChunks.Clear();
        _wantedObjectChunks.Clear();
        foreach (var chunk in _objectChunks.Values.Concat(_visibleObjectChunks.Values).Distinct())
            DisposeObjectGeometry(chunk);
        foreach (var chunk in _alternateLodChunks.Values) DisposeObjectGeometry(chunk);
        _alternateLodChunks.Clear();
        _objectChunks.Clear();
        _visibleObjectChunks.Clear();
        foreach (var child in _streamedSolids.GetChildren())
        {
            _streamedSolids.RemoveChild(child);
            child.QueueFree();
        }
        foreach (var pool in _objectPools.Values) pool.Mesh.Dispose();
        _objectPools.Clear();
        _objectRoofPages.Clear();
        _streamingObjects = false;
    }
}
