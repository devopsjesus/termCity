using System.Diagnostics;
using Godot;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class PerspectiveCity
{
    internal static async Task VerifyPersistentResidency(Main host, CacheExperiment experiment)
    {
        VerifyIndexedSurfaces();
        await VerifyPressureDrivenTerrain(host);
        await VerifyPressureDrivenObjects(host);
        await VerifyStablePathPageLifecycle(host);
        var game = CityGame.New(new GameConfig { MapWidth = 80, MapHeight = 64, Seed = 42, StartingYear = 1226 });
        game.Paused = true;
        var map = game.Map;
        var house = map.Content.Buildings.Get("Cottage");
        foreach (var at in new[] { new Pos(12, 24), new Pos(60, 24) })
        {
            var area = new CellRect(at.X, at.Y, 6, 6);
            foreach (var cell in area.Cells())
            {
                map.ClearCell(cell.X, cell.Y);
                map.SetTerrain(cell.X, cell.Y, map.Content.Terrains.Base);
                map.SetZone(cell.X, cell.Y, ZoneType.Residential);
            }
            map.SetBuildingFootprint(house, area);
            map.SetHousehold(at.X, at.Y, new(4, 2, 0));
        }
        game.Touch();
        var session = new GameSession(game, showGuide: false);
        session.SetCameraEdgePadding(true);
        var probe = new PerspectiveCity { WaterMotionEnabled = false };
        host.AddChild(probe);
        try
        {
            probe.ConfigureCacheExperiment(experiment);
            session.CenterOn(new(20, 32));
            session.SetZoom(0);
            probe.Sync(session, new(600, 480));
            await probe.AwaitFootpathMesh();
            GD.Print("TERMCITY_PERSISTENT_STAGE visible-ready");
            var watch = Stopwatch.StartNew();
            while (probe._groundNodes.Count != 20 || probe._dirtyGroundPages.Count > 0)
            {
                if (watch.Elapsed.TotalSeconds > 30)
                    throw new InvalidOperationException($"Fitting terrain did not warm: native={probe._groundNodes.Count}/20 " +
                        $"pending={probe.PreparationPendingDescription} gpu={probe.DestinationGpuBytes}.");
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            GD.Print("TERMCITY_PERSISTENT_STAGE native-warmed");
            foreach (var center in new[] { new Pos(60, 32), new Pos(20, 32) })
            {
                session.CenterOn(center);
                probe.Sync(session, new(600, 480));
                await probe.AwaitFootpathMesh();
            }
            probe.ValidateGroundDrawPages();
            if (probe._groundMaterials.Values.Any(material => material.Texture.GetWidth() != WorldTerrainTexture.DataPixels ||
                material.Texture.GetHeight() != WorldTerrainTexture.DataPixels ||
                material.Variation.GetWidth() != WorldTerrainTexture.VariationPixels) ||
                probe._terrainAtlas?.GetWidth() != WorldTerrainTexture.AtlasWidth)
                throw new InvalidOperationException("Native terrain does not use compact category/variation data and a shared palette.");
            using (var atlas = probe._terrainAtlas.GetImage())
                if (!atlas.GetData().SequenceEqual(WorldTerrainTexture.PrepareAtlas()))
                    throw new InvalidOperationException("Shared native palette differs from its original generated colors.");
            foreach (var (key, material) in probe._groundMaterials)
            {
                using var data = material.Texture.GetImage();
                using var variations = material.Variation.GetImage();
                if (!data.GetData().SequenceEqual(WorldTerrainTexture.PrepareCellData(map, key.X, key.Y)) ||
                    !variations.GetData().SequenceEqual(WorldTerrainTexture.PrepareVariations(map, key.X, key.Y)))
                    throw new InvalidOperationException("Native category/variation packets differ from world-space data.");
            }
            string saved = SaveGameStore.Serialize(game);
            var ground = probe._groundDrawPages.ToDictionary(pair => pair.Key, pair => pair.Value.Meshes[0]);
            int uploaded = probe.GroundChunksUploaded, pages = probe.GroundPagesRebuilt;
            long bytes = probe.GroundUploadBytes;
            int texturesPrepared = probe.GroundTexturePacketsBuilt;
            double preparation = probe.PreparationProfile.Ground;
            var home = probe.CaptureNativeDestination();
            for (int step = 0; step < 8; step++)
            {
                var previous = probe.CaptureTerrainPublication();
                session.CenterOn(new(step % 2 == 0 ? 60 : 20, 32));
                probe.Sync(session, new(600, 480));
                probe.ValidateTerrainContinuity(previous);
                if (DisplayServer.GetName() != "headless")
                {
                    probe.RequestRender();
                    await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    probe.ValidateTerrainContinuity(previous);
                    using var image = probe.CaptureDisplayImage();
                    var background = new Color("#14221c");
                    var samples = probe._groundFaces.Values.Where(faces => faces.Length >= 3)
                        .Select(faces => probe.ScreenPoint((faces[0] + faces[1] + faces[2]) / 3))
                        .Where(point => new Rect2(8, 8, 584, 464).HasPoint(point)).Take(16).ToArray();
                    if (samples.Length == 0) throw new InvalidOperationException("Warm continuity fixture has no visible native terrain samples.");
                    foreach (var point in samples)
                        if (image.GetPixel((int)point.X, (int)point.Y).IsEqualApprox(background))
                            throw new InvalidOperationException($"A warmed native terrain surface rendered empty at {point} during step {step}.");
                }
                await probe.AwaitFootpathMesh();
                probe.ValidateTerrainContinuity(previous);
                probe.ValidateObjectBuffers();
                probe.ValidateGroundDrawPages();
            }
            if (probe.GroundChunksUploaded != uploaded || probe.GroundPagesRebuilt != pages ||
                probe.GroundUploadBytes != bytes || probe.GroundTexturePacketsBuilt != texturesPrepared ||
                probe.PreparationProfile.Ground != preparation || ground.Any(pair =>
                    !ReferenceEquals(pair.Value, probe._groundDrawPages[pair.Key].Meshes[0])))
                throw new InvalidOperationException("Camera-only navigation rebuilt warmed native terrain.");
            if (probe._persistentObjectResidency) probe.ValidateNativeDestinationReuse(home);
            if (saved != SaveGameStore.Serialize(game)) throw new InvalidOperationException("Residency changed the paused save.");
            if (probe._groundDrawPages.Values.Any(page => page.Sources.Count != 0))
                throw new InvalidOperationException("Persistent pages pinned complete managed preparation packets.");
            probe.PrintPersistentResidency("warmed-fit");
            foreach (double zoom in new[] { 0d, -1d, (double)GameSession.MinZoom })
                for (int turn = 0; turn < 4; turn++)
                {
                    session.SetZoom(zoom);
                    probe.Rotate(90);
                    probe.Sync(session, new(600, 480));
                    await probe.AwaitFootpathMesh();
                    probe.ValidateGroundDrawPages();
                    probe.ValidateObjectBuffers();
                    bool visibleHouse = false, roofHit = false;
                    foreach (var group in probe.BuildingGeometry.Where(box => map.BuildingAt(box.Anchor.X, box.Anchor.Y) is not null)
                        .GroupBy(box => box.Anchor))
                    {
                        var roof = group.OrderByDescending(box => box.Bounds.End.Y).First().Bounds;
                        var point = probe.ScreenPoint(new Vector3(roof.GetCenter().X, roof.End.Y, roof.GetCenter().Z));
                        if (!new Rect2(0, 0, 600, 480).HasPoint(point)) continue;
                        visibleHouse = true;
                        if (probe.PickBuilding(point, out var owner) && owner == group.Key) roofHit = true;
                    }
                    if (visibleHouse && !roofHit)
                        throw new InvalidOperationException("Persistent building roofs could not be picked after rotation/zoom.");
                }
            session.SetZoom(0);
            probe.Sync(session, new(600, 480));
            await probe.AwaitFootpathMesh();
            if (probe.GroundUploadBytes != bytes)
                throw new InvalidOperationException("Rotation/zoom uploaded unchanged resident terrain.");

            if (probe._persistentObjectResidency)
            {
                var other = ObjectKey(new(60, 24));
                var otherSlots = probe._visibleObjectChunks[other].Slots.ToDictionary(pair => pair.Key, pair => pair.Value);
                int allocations = 0;
                for (int edit = 0; edit < 8; edit++)
                {
                    map.SetBuildingFootprint(map.Content.Buildings.Get(edit % 2 == 0 ? "Timber Home" : "Cottage"),
                        new CellRect(12, 24, 6, 6));
                    game.Touch();
                    probe.Sync(session, new(600, 480));
                    await probe.AwaitFootpathMesh();
                    probe.ValidateObjectBuffers();
                    if (otherSlots.Any(pair => probe._visibleObjectChunks[other].Slots.GetValueOrDefault(pair.Key) != pair.Value))
                        throw new InvalidOperationException("A local building edit relocated an unrelated resident owner's slots.");
                    if (edit == 1) allocations = probe.ObjectPoolGrowths;
                }
                if (probe.ObjectPoolGrowths > allocations)
                    throw new InvalidOperationException("Repeated building edits grew native pools instead of reusing retired ranges.");
            }

            var roadCell = new Pos(20, 20);
            GD.Print("TERMCITY_PERSISTENT_STAGE road-edit");
            var roadKey = (roadCell.X / 16, roadCell.Y / 16);
            var texture = probe._groundMaterials[roadKey].Texture;
            var roadType = map.Content.Roads.Get("King's Road");
            map.SetRoad(roadCell.X, roadCell.Y, roadType);
            game.Touch();
            probe.Sync(session, new(600, 480));
            await probe.AwaitFootpathMesh();
            if (!ReferenceEquals(texture, probe._groundMaterials[roadKey].Texture))
                throw new InvalidOperationException("A road-only edit replaced unchanged native terrain textures.");
            probe.ValidateGroundDrawPages();
            GD.Print("TERMCITY_PERSISTENT_STAGE road-remove");
            map.SetRoad(roadCell.X, roadCell.Y, false);
            game.Touch();
            probe.Sync(session, new(600, 480));
            await probe.AwaitFootpathMesh();

            var edited = new Pos(20, 20);
            GD.Print("TERMCITY_PERSISTENT_STAGE terrain-edit");
            map.SetTerrain(edited.X, edited.Y, map.Content.Terrains.Get("Water"));
            game.Touch();
            probe.Sync(session, new(600, 480));
            await probe.AwaitFootpathMesh();
            probe.ValidateGroundDrawPages();
            if (ground.All(pair => probe._groundDrawPages.TryGetValue(pair.Key, out var page) &&
                ReferenceEquals(pair.Value, page.Meshes[0])))
                throw new InvalidOperationException("A terrain edit failed to replace its native page.");
            foreach (var (key, mesh) in ground.Where(pair => pair.Key.X > 0))
                if (probe._groundDrawPages.TryGetValue(key, out var page) && !ReferenceEquals(mesh, page.Meshes[0]))
                    throw new InvalidOperationException("A local terrain edit replaced unrelated ground pages.");

            session.CenterOn(new(20, 32));
            probe.Sync(session, new(600, 480));
            await probe.AwaitFootpathMesh();
            probe.GpuCacheBudgetBytes = 1024;
            probe.CpuCacheBudgetBytes = 1024;
            probe.TrimGpuResidency();
            probe.ApplyGroundDrawPages();
            if (probe._groundNodes.Keys.Any(key => !probe.GroundNearby(key)))
                throw new InvalidOperationException("Pressure failed to reclaim discretionary native terrain.");
            probe.ValidateGpuResidencyBudget();
            probe.GpuCacheBudgetBytes = 128L * 1024 * 1024;
            probe.CpuCacheBudgetBytes = 256L * 1024 * 1024;
            session.CenterOn(new(60, 32));
            probe.Sync(session, new(600, 480));
            await probe.AwaitFootpathMesh();
            probe.ValidateGroundDrawPages();

            session.NewGame(new GameConfig { MapWidth = 80, MapHeight = 48, Seed = 43, StartingYear = 1226 });
            probe.Sync(session, new(600, 480));
            await probe.AwaitFootpathMesh();
            if (ReferenceEquals(probe._groundMap, map)) throw new InvalidOperationException("Map replacement retained old native ownership.");
            probe.ValidateGroundDrawPages();
            probe.ConfigureCacheExperiment(CacheExperiment.Tuned);
            probe.Sync(session, new(600, 480));
            await probe.AwaitFootpathMesh();
            if (probe._groundDrawPages.Count != 0 || probe._persistentResidency)
                throw new InvalidOperationException("Leaving the residency policy retained its native pages.");
            GD.Print($"TERMCITY_PERSISTENT_LIFECYCLE_OK experiment={experiment} camera=zero-terrain-uploads " +
                $"object-return={(experiment == CacheExperiment.PersistentScene ? "native-reused" : "baseline")} " +
                "cpu-sources=detached edit=local pressure=reclaimed map=replaced restore=normal");
        }
        finally
        {
            host.RemoveChild(probe);
            probe.Free();
        }

    }

    private static async Task VerifyPressureDrivenTerrain(Main host)
    {
        var game = CityGame.New(new GameConfig { MapWidth = 160, MapHeight = 64, Seed = 42, StartingYear = 1226 });
        game.Paused = true;
        var session = new GameSession(game, showGuide: false);
        session.SetCameraEdgePadding(true);
        session.SetZoom(0);
        var probe = new PerspectiveCity { WaterMotionEnabled = false };
        host.AddChild(probe);
        try
        {
            probe.ConfigureCacheExperiment(CacheExperiment.Tuned);
            probe.WholeMapWarmingEnabled = false;
            async Task Visit(int x)
            {
                session.CenterOn(new(x, 32));
                probe.Sync(session, new(300, 240));
                await probe.AwaitFootpathMesh();
                probe.TrimGpuResidency();
                probe.ValidateObjectBuffers();
            }
            await Visit(20);
            string saved = SaveGameStore.Serialize(game);
            var home = probe.CaptureNativeDestination();
            await Visit(140);
            await Visit(80);
            probe.ReleaseResidentGroundReplay();
            var distant = home.Ground.Keys.Where(key => !probe.GroundNearby(key)).ToArray();
            if (distant.Length == 0 || distant.Any(key => !probe._groundNodes.ContainsKey(key) ||
                probe._preparedGround.ContainsKey(key) || probe._terrainPixels.ContainsKey(key)))
                throw new InvalidOperationException("Normal terrain retention failed to detach offscreen CPU replay while keeping native ground.");
            var nodePressure = Enumerable.Range(0, 480).Select(_ => new Node3D()).ToArray();
            foreach (var node in nodePressure) probe._world.AddChild(node);
            try
            {
                if (!probe.NativeNodeLimitReached)
                    throw new InvalidOperationException("Terrain node-pressure fixture did not reach its diagnostic node limit.");
                probe.TrimGpuResidency();
                if (home.Ground.Keys.Any(key => !probe._groundNodes.ContainsKey(key)))
                    throw new InvalidOperationException("A node-count heuristic evicted native terrain without memory pressure.");
            }
            finally
            {
                foreach (var node in nodePressure) node.Free();
            }
            int uploads = probe.GroundChunksUploaded;
            long bytes = probe.GroundUploadBytes;
            double preparation = probe.PreparationProfile.Ground;
            await Visit(20);
            foreach (var (key, cached) in home.Ground)
                if (!ReferenceEquals(cached.Geometry, probe.NativeGroundGeometry(key)) ||
                    !ReferenceEquals(cached.Material, probe._groundMaterials[key]))
                    throw new InvalidOperationException($"Normal warmed terrain lost native identity at {key}.");
            if (uploads != probe.GroundChunksUploaded || bytes != probe.GroundUploadBytes ||
                preparation != probe.PreparationProfile.Ground)
                throw new InvalidOperationException("Normal warmed terrain return regenerated or reuploaded ground.");
            if (saved != SaveGameStore.Serialize(game))
                throw new InvalidOperationException("Normal terrain retention changed paused simulation state.");
            var cell = new Pos(20, 32);
            var keyAt = (cell.X / 16, cell.Y / 16);
            game.Map.SetTerrain(cell.X, cell.Y, game.Map.Content.Terrains.Base);
            game.Map.SetRoad(cell.X, cell.Y, game.Map.Content.Roads.Get("King's Road"));
            game.Touch();
            await Visit(20);
            var roadTexture = probe._groundMaterials[keyAt].Texture;
            game.Map.SetRoad(cell.X, cell.Y, false);
            game.Touch();
            await Visit(20);
            if (!ReferenceEquals(roadTexture, probe._groundMaterials[keyAt].Texture))
                throw new InvalidOperationException("Normal road-only invalidation replaced unchanged terrain textures.");
            game.Map.SetTerrain(cell.X, cell.Y, game.Map.Content.Terrains.Get("Water"));
            game.Touch();
            await Visit(20);
            if (ReferenceEquals(roadTexture, probe._groundMaterials[keyAt].Texture))
                throw new InvalidOperationException("Normal terrain invalidation retained stale native textures.");
            probe.GpuCacheBudgetBytes = 1024;
            probe.TrimGpuResidency();
            probe.ValidateGpuResidencyBudget();
            if (probe._groundNodes.Keys.Any(key => !probe.GroundNearby(key)))
                throw new InvalidOperationException("Native byte pressure failed to reclaim normal offscreen terrain.");
            probe.GpuCacheBudgetBytes = 128L * 1024 * 1024;
            await Visit(140);
            await Visit(20);
            probe.CpuCacheBudgetBytes = 1024;
            probe.TrimGpuResidency();
            if (probe._groundNodes.Keys.Any(key => !probe.GroundNearby(key)))
                throw new InvalidOperationException("CPU byte pressure failed to reclaim normal offscreen terrain after replay trimming.");
            probe.CpuCacheBudgetBytes = 256L * 1024 * 1024;
            session.NewGame(new GameConfig { MapWidth = 80, MapHeight = 48, Seed = 43, StartingYear = 1226 });
            await Visit(20);
            if (ReferenceEquals(probe._groundMap, game.Map))
                throw new InvalidOperationException("Normal map replacement retained old terrain ownership.");
            GD.Print("TERMCITY_PRESSURE_TERRAIN_OK return=zero-generation-and-uploads replay=detached " +
                "node-heuristic=ignored road-textures=retained terrain-edit=invalidated memory=reclaimed map=replaced");
        }
        finally
        {
            host.RemoveChild(probe);
            probe.Free();
        }
    }

    private static async Task VerifyPressureDrivenObjects(Main host)
    {
        var game = CityGame.New(new GameConfig { MapWidth = 160, MapHeight = 64, Seed = 42, StartingYear = 1226 });
        game.Paused = true;
        var map = game.Map;
        foreach (var owner in new[] { new Pos(12, 24), new Pos(140, 24) })
        {
            var area = new CellRect(owner.X, owner.Y, 6, 6);
            foreach (var cell in area.Cells())
            {
                map.ClearCell(cell.X, cell.Y);
                map.SetTerrain(cell.X, cell.Y, map.Content.Terrains.Base);
                map.SetZone(cell.X, cell.Y, ZoneType.Residential);
            }
            map.SetBuildingFootprint(map.Content.Buildings.Get("Cottage"), area);
            map.SetHousehold(owner.X, owner.Y, new(4, 2, 0));
        }
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
                probe.Sync(session, new(300, 240));
                await probe.AwaitFootpathMesh();
                probe.TrimGpuResidency();
                probe.ValidateObjectBuffers();
            }
            await Visit(20);
            await Visit(140);
            await Visit(20);
            var home = probe.CaptureNativeDestination();
            var roofs = probe._objectRoofPages.ToDictionary();
            long bytes = probe.ObjectUploadBytes, roofBytes = probe.ObjectRoofUploadBytes;
            int growths = probe.ObjectPoolGrowths, compactions = probe.ObjectPoolCompactions;
            string saved = SaveGameStore.Serialize(game);
            var nodePressure = Enumerable.Range(0, 480).Select(_ => new Node3D()).ToArray();
            foreach (var node in nodePressure) probe._world.AddChild(node);
            try
            {
                if (!probe.NativeNodeLimitReached)
                    throw new InvalidOperationException("Object node-pressure fixture did not reach its diagnostic node limit.");
                probe.TrimGpuResidency();
                probe.ValidateNativeDestinationReuse(home);
            }
            finally
            {
                foreach (var node in nodePressure) node.Free();
            }
            for (int step = 0; step < 4; step++)
            {
                await Visit(step % 2 == 0 ? 140 : 20);
                probe.ValidateNativeDestinationReuse(home);
                var wanted = probe._wantedObjectChunks.Select(key => new Pos(key.X / probe._objectMaterialPageSpan,
                    key.Y / probe._objectMaterialPageSpan)).ToHashSet();
                if (probe._objectPools.Any(pair => pair.Value.Node.Visible != wanted.Contains(pair.Key.Page)))
                    throw new InvalidOperationException("Retained object pages draw outside their requested spatial pages.");
            }
            if (probe.ObjectUploadBytes != bytes || probe.ObjectRoofUploadBytes != roofBytes ||
                probe.ObjectPoolGrowths != growths || probe.ObjectPoolCompactions != compactions ||
                roofs.Any(pair => !ReferenceEquals(pair.Value, probe._objectRoofPages.GetValueOrDefault(pair.Key))))
                throw new InvalidOperationException("Camera-only object returns uploaded or relocated unchanged native geometry.");
            if (saved != SaveGameStore.Serialize(game))
                throw new InvalidOperationException("Normal object residency changed the paused save.");

            probe.ConfigureCacheExperiment(CacheExperiment.Tuned);
            await Visit(140);
            probe.ValidateNativeDestinationReuse(home);
            var other = ObjectKey(new(140, 24));
            var retained = probe._visibleObjectChunks[other];
            var otherSlots = retained.Slots.ToDictionary();
            for (int edit = 0; edit < 6; edit++)
            {
                map.SetBuildingFootprint(map.Content.Buildings.Get(edit % 2 == 0 ? "Timber Home" : "Cottage"),
                    new(12, 24, 6, 6));
                game.Touch();
                await Visit(20);
                if (!ReferenceEquals(retained, probe._visibleObjectChunks[other]) ||
                    otherSlots.Any(pair => retained.Slots.GetValueOrDefault(pair.Key) != pair.Value))
                    throw new InvalidOperationException("A local building edit relocated an unrelated resident object's slots.");
            }
            await Visit(140);
            probe.GpuCacheBudgetBytes = 1024;
            probe.TrimGpuResidency();
            probe.ValidateGpuResidencyBudget();
            if (probe._visibleObjectChunks.Keys.Any(key => !probe._wantedObjectChunks.Any(wanted =>
                wanted.X / probe._objectMaterialPageSpan == key.X / probe._objectMaterialPageSpan &&
                wanted.Y / probe._objectMaterialPageSpan == key.Y / probe._objectMaterialPageSpan)))
                throw new InvalidOperationException("Native byte pressure retained evictable object pages.");
            probe.GpuCacheBudgetBytes = 128L * 1024 * 1024;
            await Visit(20);
            await Visit(140);
            probe.CpuCacheBudgetBytes = 1024;
            probe.TrimGpuResidency();
            if (probe._visibleObjectChunks.Keys.Any(key => !probe._wantedObjectChunks.Any(wanted =>
                wanted.X / probe._objectMaterialPageSpan == key.X / probe._objectMaterialPageSpan &&
                wanted.Y / probe._objectMaterialPageSpan == key.Y / probe._objectMaterialPageSpan)))
                throw new InvalidOperationException("CPU byte pressure retained evictable object pages.");
            probe.CpuCacheBudgetBytes = 256L * 1024 * 1024;
            var previous = probe._visibleObjectChunks.Values.ToHashSet();
            session.NewGame(new GameConfig { MapWidth = 80, MapHeight = 48, Seed = 43, StartingYear = 1226 });
            await Visit(20);
            if (ReferenceEquals(probe._streamMap, map) || probe._visibleObjectChunks.Values.Any(previous.Contains))
                throw new InvalidOperationException("Map replacement retained old native object owners.");
            GD.Print("TERMCITY_PRESSURE_OBJECTS_OK default=resident return=zero-instance-and-roof-uploads " +
                "slots=stable node-heuristic=ignored hidden=culled edit=local cpu-pressure=reclaimed native-pressure=reclaimed map=replaced");
        }
        finally
        {
            host.RemoveChild(probe);
            probe.Free();
        }
    }

    private static void VerifyIndexedSurfaces()
    {
        var points = new List<Vector3>();
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                points.AddRange([new(x, 0, y), new(x, 0, y + 1), new(x + 1, 0, y + 1),
                        new(x, 0, y), new(x + 1, 0, y + 1), new(x + 1, 0, y)]);
        var source = new RoadSurfaceMeshes.MeshData(points.ToArray(),
            Enumerable.Repeat(Colors.White, points.Count).ToArray(),
            points.Select(point => new Vector2(point.X / 16, point.Z / 16)).ToArray(),
            Enumerable.Repeat(Vector3.Up, points.Count).ToArray());
        var indexed = source.Indexed();
        if (indexed.Indices is null || indexed.Points.Length != 289 ||
            MeshUploadBytes(indexed) >= MeshUploadBytes(source) * .4)
            throw new InvalidOperationException("Indexed terrain did not meet the planar memory-reduction threshold.");
        for (int i = 0; i < source.Points.Length; i++)
        {
            int vertex = indexed.Indices[i];
            if (indexed.Points[vertex] != source.Points[i] || indexed.Colors[vertex] != source.Colors[i] ||
                indexed.Uvs![vertex] != source.Uvs![i] || indexed.Normals![vertex] != source.Normals![i])
                throw new InvalidOperationException("Indexing changed original triangle attributes or order.");
        }
        var seam = source with { Colors = source.Colors.ToArray(), Normals = source.Normals!.ToArray(), Uvs = source.Uvs!.ToArray() };
        seam.Colors[3] = Colors.Red;
        seam.Normals![4] = Vector3.Right;
        seam.Uvs![5] = new Vector2(10, 10);
        var split = seam.Indexed();
        if (split.Points.Length != indexed.Points.Length + 3)
            throw new InvalidOperationException("Indexing merged color, UV or hard-normal seams.");
        var combined = RoadSurfaceMeshes.MeshData.Combine([indexed, split]);
        if (combined.Indices is null || combined.Indices.Length != source.Points.Length * 2 ||
            combined.Indices.Skip(source.Points.Length).Where((index, i) => index != split.Indices![i] + indexed.Points.Length).Any())
            throw new InvalidOperationException("Native draw-page combination did not rebase mesh indices correctly.");
        using var uploaded = indexed.Upload();
        using var arrays = uploaded.SurfaceGetArrays(0);
        if (!arrays[(int)Mesh.ArrayType.Index].AsInt32Array().SequenceEqual(indexed.Indices))
            throw new InvalidOperationException("Native index buffer differs from the prepared topology.");
        GD.Print($"TERMCITY_INDEXED_TERRAIN_OK triangles={points.Count / 3} vertices={indexed.Points.Length} " +
            $"before-bytes={MeshUploadBytes(source)} after-bytes={MeshUploadBytes(indexed)} seams=preserved indices=exact");
    }
}
