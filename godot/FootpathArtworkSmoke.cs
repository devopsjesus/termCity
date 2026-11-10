using System.Diagnostics;
using System.Reflection;
using Godot;
using TermCity.Core.Persistence;
using TermCity.Core.Roads;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using static TermCity.GodotApp.SettlementSmoke;

namespace TermCity.GodotApp;

internal static class FootpathArtworkSmoke
{
    private static CityGame FlatGame()
    {
        var game = CityGame.New(new GameConfig { MapWidth = 128, MapHeight = 128, Seed = 42, StartingMoney = 500_000 });
        var map = game.Map;
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
            {
                map.ClearCell(x, y);
                map.SetTerrain(x, y, map.Content.Terrains.Base);
            }
        game.Touch();
        return game;
    }

    internal static async Task Run(Main host, string? capture)
    {
        if (host.Session.Game.Config.Scenario == CityScenario.Top)
        {
            await VerifyTopLivePublication(host);
            return;
        }
        var game = FlatGame();
        var map = game.Map;
        Require(game.FoundSettlement(new(24, 24, 72, 72)).Success, "Path artwork requires a real dry bailey.");
        var house = map.Content.Buildings.Get("Cottage");
        var plots = new List<CellRect>();
        foreach (var target in new[] { new Pos(42, 42), new(58, 42), new(42, 58), new(58, 58) })
        {
            var plot = game.VacantPlots(ZoneType.Residential).OrderBy(area =>
                Math.Abs(area.X - target.X) + Math.Abs(area.Y - target.Y)).First();
            map.SetBuildingFootprint(house, plot);
            map.SetHousehold(plot.X, plot.Y, new(4, 2, 0));
            plots.Add(plot);
            game.Touch();
        }
        var wear = typeof(TravelRoutes).GetMethod("WearPaths", BindingFlags.Static | BindingFlags.NonPublic)!;
        for (int step = 0; step < 2; step++) wear.Invoke(null, [map]);
        game.Touch();
        game.Paused = true;
        var session = host.Session;
        typeof(GameSession).GetMethod("ReplaceGame", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, [game, true]);
        session.ClosePrompt();
        host.Map.SetPerspective(true);
        session.SetZoom(0);
        host.Map.RefreshCells();
        session.CenterOn(new(50, 50));
        var view = host.Map.Perspective!;
        await Frames(host);
        await Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".fresh.png"));
        await view.VerifyPathPublicationUnderCoalescedUpdates();
        for (int step = 0; step < 12; step++) wear.Invoke(null, [map]);
        game.Touch();
        string saved = SaveGameStore.Serialize(game);
        Require(FootpathLayout.Strokes(map).Count > 0, "The fixture must render actual journey-generated paths.");
        double worstBuild = 0;
        foreach (double zoom in new[] { 0, 1, GameSession.ThreeQuarterZoom, -1, -2 })
        {
            session.SetZoom(zoom);
            host.Map.RefreshCells();
            session.CenterOn(new(50, 50));
            for (int turn = 0; turn < 4; turn++)
            {
                view.Rotate(90);
                var watch = Stopwatch.StartNew();
                view.Sync(session, host.Map.Size);
                await Frames(host);
                worstBuild = Math.Max(worstBuild, watch.Elapsed.TotalMilliseconds);
                Require(view.FootpathTrianglesDrawn > 0 && view.MaximumFootpathTriangleRise <= TravelRoutes.MaximumFootpathStep,
                    "Rendered dirt must preserve supported terrain grades.");
                Require(view.SlopedFootpathTrianglesDrawn > 0 && view.DoorApproachTrianglesDrawn > 0 &&
                    view.LitDirtMaterials && view.StablePixelMaterials,
                    "Generated paths and door approaches must have lit, mipmapped dirt artwork and shallow shoulders.");
                if (turn == 0 || zoom == 0)
                    await Capture(host, capture is null ? null : Path.ChangeExtension(capture, $".zoom{zoom}-turn{turn}.png"));
            }
        }
        session.SetZoom(0);
        host.Map.RefreshCells();
        session.CenterOn(new(50, 50));
        await Frames(host);
        var geometry = view.BuildingGeometry;
        var dirt = view.DirtGeometry;
        Require(dirt.Count > 0 && dirt.Any(vertex => vertex.Normal.Y < .999f) &&
            dirt.All(vertex => vertex.Normal.Y > 0 && vertex.Uv.IsEqualApprox(new Vector2(vertex.Point.X, vertex.Point.Z) * FootpathArtwork.UvScale)),
            "Actual dirt meshes must carry upward geometry-derived normals and stable world-space UVs.");
        int pickedApproaches = 0;
        var homes = map.BuildingFootprints.Values.Select(plot => new Pos(plot.X, plot.Y)).ToHashSet();
        for (int i = 0; i + 2 < dirt.Count; i += 3)
        {
            if (dirt[i].Color != FootpathArtwork.FreshColor) continue;
            var point = view.ScreenPoint((dirt[i].Point + dirt[i + 1].Point + dirt[i + 2].Point) / 3);
            if (!new Rect2(Vector2.Zero, view.DisplaySize).HasPoint(point)) continue;
            Require(host.Map.TryPickBuilding(point, out var owner) && homes.Contains(owner),
                "The visible textured entrance approach must remain clickable as part of its building.");
            pickedApproaches++;
        }
        Require(pickedApproaches > 0, "Doorway picking must test a visible rendered approach.");
        int reusedChunks = view.ObjectChunksReused, publishedChunks = view.ObjectChunksPublished;
        view.InvalidateStaticScene(discardBuildingCache: false);
        view.Sync(session, host.Map.Size);
        await Frames(host);
        Require((view.BuildingsReusedLastBuild > 0 || view.ObjectChunksReused > reusedChunks) &&
            geometry.SequenceEqual(view.BuildingGeometry),
            "Doorway artwork must survive a cached scroll-buffer rebuild with unchanged picking.");
        Require(view.StaticBatchesUploadedLastBuild == 0 &&
            (view.StaticBatchesReusedLastBuild > 0 || view.ObjectChunksPublished == publishedChunks),
            "An unchanged buffer rebuild must reuse its exact native instance and roof batches.");
        Require(dirt.SequenceEqual(view.DirtGeometry), "Cached paths and door approaches must retain exact triangles, normals, colors and UVs.");
        view.InvalidateStaticScene(discardBuildingCache: true);
        view.Sync(session, host.Map.Size);
        await Frames(host);
        Require(geometry.SequenceEqual(view.BuildingGeometry), "Fresh and cached doorway geometry must agree.");
        Require(dirt.SequenceEqual(view.DirtGeometry), "A fresh rebuild must match the retained dirt mesh exactly.");
        Require(saved == SaveGameStore.Serialize(game), "Rendering artwork must not change wear, travel or saved city state.");
        Require(view.SceneObjectCount < 512, "Path artwork must retain material batching.");
        GD.Print($"TERMCITY_FOOTPATH_ART_METRICS strokes={FootpathLayout.Strokes(map).Count} " +
            $"triangles={view.FootpathTrianglesDrawn} nodes={view.SceneObjectCount} cold-worst={worstBuild:0.0}ms");
        await VerifyShadows(host, capture);
        await VerifyRoadJoin(host, capture);
        await VerifyEntranceCatalogue(host, capture);
    }

    private static async Task VerifyTopLivePublication(Main host)
    {
        var session = host.Session;
        var game = session.Game;
        game.Paused = true;
        game.AdvanceDay();
        if (DisplayServer.GetName() == "headless")
            host.GetWindow().ContentScaleSize = new(2940, 1846);
        host.Map.SetPerspective(true);
        await Frames(host);
        var view = host.Map.Perspective!;
        view.ConfigureCacheExperiment(CacheExperiment.RecentRegionPagedLodLarge);
        var targets = new[] { new Pos(128, 128), new Pos(512, 512), new Pos(128, 512), new Pos(320, 320) };
        foreach (double zoom in new[] { 0d, (double)GameSession.MinZoom })
        {
            session.SetZoom(zoom);
            host.Map.RefreshCells();
            for (int step = 0; step < 24; step++)
            {
                if (step == 12)
                {
                    game.Paused = true;
                    while (session.BackgroundSimulationPending || session.PendingSimulationSeconds > 0)
                        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                    game.AdvanceDay();
                }
                game.Paused = step < 12;
                session.CenterOn(targets[step % targets.Length]);
                view.Sync(session, host.Map.Size);
                var started = Stopwatch.GetTimestamp();
                while (view.FootpathMeshPending)
                {
                    if (Stopwatch.GetElapsedTime(started).TotalSeconds > 60)
                        throw new InvalidOperationException($"TOP live path publication stalled at zoom {zoom}, step {step}: " +
                            view.PreparationPendingDescription);
                    await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                    view.Sync(session, host.Map.Size);
                }
                view.ValidateObjectBuffers();
                view.ValidateGroundDrawPages();
                GD.Print($"TERMCITY_TOP_PATH_SAMPLE zoom={zoom} step={step} live={!game.Paused} " +
                    $"published={view.FootpathMeshesPublished} destination-pending={view.FootpathMeshPending}");
            }
            game.Paused = true;
            await view.AwaitFootpathMesh();
            Require(view.FootpathTrianglesDrawn > 0, "TOP must publish actual journey-generated dirt, not omit paths.");
            GD.Print($"TERMCITY_TOP_LIVE_PATH_OK zoom={zoom} jumps=24 " +
                $"triangles={view.FootpathTrianglesDrawn} publications={view.FootpathMeshesPublished} " +
                $"days={game.ElapsedDays:0.00} panel={host.Map.Size}");
        }
        await view.VerifyPathPublicationUnderCoalescedUpdates();
        GD.Print("TERMCITY_TOP_PATH_CORRECTNESS_OK performance=not-measured");
    }

    private static async Task VerifyShadows(Main host, string? capture)
    {
        if (DisplayServer.GetName() == "headless") return;
        var view = host.Map.Perspective!;
        await GodotSmoke.RenderedFrame(host);
        using var before = view.CaptureDisplayImage();
        var vertices = view.DirtGeometry;
        var samples = new List<Vector2I>();
        for (int i = 0; i + 2 < vertices.Count; i += 3)
        {
            var point = view.ScreenPoint((vertices[i].Point + vertices[i + 1].Point + vertices[i + 2].Point) / 3);
            if (!new Rect2(Vector2.Zero, view.DisplaySize).HasPoint(point) || host.Map.TryPickBuilding(point, out _)) continue;
            var pixel = new Vector2I((int)point.X, (int)point.Y);
            if (!samples.Contains(pixel)) samples.Add(pixel);
        }
        try
        {
            view.SetDepthStudyShadows(false);
            await GodotSmoke.RenderedFrame(host);
            using var after = view.CaptureDisplayImage();
            int shaded = samples.Count(pixel =>
            {
                var a = before.GetPixelv(pixel); var b = after.GetPixelv(pixel);
                return a.R + a.G + a.B < b.R + b.G + b.B - .06f;
            });
            Require(shaded > 0, "Actual visible generated dirt must receive building shadows, not merely have a nominally lit material.");
            if (capture is not null)
                Require(after.SavePng(Path.ChangeExtension(capture, ".shadows-off.png")) == Error.Ok,
                    "The matched shadow comparison must save successfully.");
            GD.Print($"TERMCITY_FOOTPATH_SHADOW_OK dirt-pixels={samples.Count} shadowed={shaded}");
        }
        finally
        {
            view.SetDepthStudyShadows(true);
            await GodotSmoke.RenderedFrame(host);
        }
    }

    private static async Task VerifyRoadJoin(Main host, string? capture)
    {
        var game = FlatGame();
        var map = game.Map;
        var house = map.Content.Buildings.Get("Cottage");
        var plot = house.FootprintAt(48, 58);
        foreach (var cell in plot.Cells()) map.SetZone(cell.X, cell.Y, ZoneType.Residential);
        map.SetBuildingFootprint(house, plot);
        map.SetHousehold(plot.X, plot.Y, new(4, 2, 0));
        game.Touch();
        var result = game.BuildRoad(new CellRect(24, 50, 56, 1), map.Content.Roads.Get(DefaultRoads.TrackName));
        Require(result.Success, result.Message);
        var wear = typeof(TravelRoutes).GetMethod("WearPaths", BindingFlags.Static | BindingFlags.NonPublic)!;
        for (int step = 0; step < 40; step++) wear.Invoke(null, [map]);
        game.Touch();
        game.Paused = true;
        typeof(GameSession).GetMethod("ReplaceGame", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(host.Session, [game, true]);
        host.Session.ClosePrompt();
        var stroke = FootpathLayout.Strokes(map).First(path => path.StartsAtRoad || path.EndsAtRoad);
        var end = stroke.EndsAtRoad ? stroke.Points[^1] : stroke.Points[0];
        host.Session.CenterOn(new((int)end.X, (int)end.Y));
        host.Map.RefreshCells();
        var view = host.Map.Perspective!;
        view.Sync(host.Session, host.Map.Size);
        await Frames(host);
        var roads = new RoadMeshLayout(map, RoadCurves.Extract(map, (_, _) => false));
        var dirt = view.DirtGeometry;
        for (int i = 0; i + 2 < dirt.Count; i += 3)
        {
            var polygon = new[] { new Vector2(dirt[i].Point.X, dirt[i].Point.Z),
                new(dirt[i + 1].Point.X, dirt[i + 1].Point.Z), new(dirt[i + 2].Point.X, dirt[i + 2].Point.Z) };
            int x = (int)MathF.Floor(polygon.Average(point => point.X)), y = (int)MathF.Floor(polygon.Average(point => point.Y));
            foreach (var road in roads.AtCell(x, y).Where(road => !road.Bridge))
                foreach (var overlap in Geometry2D.IntersectPolygons(polygon, road.Road.Select(point => new Vector2(point.X, point.Y)).ToArray()))
                {
                    double area = 0;
                    for (int v = 1; v + 1 < overlap.Length; v++)
                    {
                        var a = overlap[v] - overlap[0]; var b = overlap[v + 1] - overlap[0];
                        area += Math.Abs((double)a.X * b.Y - (double)a.Y * b.X) / 2;
                    }
                    Require(area <= .000002, "Shallow dirt shoulders must remain clipped outside the actual road surface.");
                }
        }
        await Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".road-join.png"));
        GD.Print("TERMCITY_FOOTPATH_ROAD_RELIEF_OK clipping=actual-mesh footprint=unchanged");
    }

    private static async Task VerifyEntranceCatalogue(Main host, string? capture)
    {
        var game = FlatGame();
        game.Paused = true;
        var map = game.Map;
        var session = host.Session;
        typeof(GameSession).GetMethod("ReplaceGame", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, [game, true]);
        session.ClosePrompt();
        var view = host.Map.Perspective!;
        var anchor = new Pos(48, 48);
        CellRect? previous = null;
        int checkedEntries = 0;
        foreach (var type in map.Content.Buildings)
            for (int rotation = 0; rotation < 4; rotation++)
            {
                if (previous is { } old)
                {
                    map.SetBuilding(old.X, old.Y, null);
                    foreach (var cell in old.Cells()) map.SetZone(cell.X, cell.Y, ZoneType.None);
                }
                var area = type.FootprintAt(anchor.X, anchor.Y, rotation);
                foreach (var cell in area.Cells()) map.SetZone(cell.X, cell.Y, type.Zone);
                map.SetBuildingFootprint(type, area);
                if (rotation != 0) map.BuildingRotations[map.Index(anchor.X, anchor.Y)] = rotation;
                previous = area;
                game.Touch();
                session.CenterOn(new(area.X + area.Width / 2, area.Y + area.Height / 2));
                host.Map.RefreshCells();
                view.Sync(session, host.Map.Size);
                await view.AwaitFootpathMesh();
                var entries = BuildingEntranceLayout.ForBuilding(map, area, type);
                foreach (var entry in entries)
                {
                    var point = new Vector3(entry.Threshold.X, entry.Threshold.Y, entry.Threshold.Z);
                    Require(view.RenderedEntranceThresholds.Any(rendered => rendered.Owner == anchor &&
                        rendered.Threshold.IsEqualApprox(point)),
                        $"The actual model must render its shared entrance: {type.Name}, rotation {rotation}.");
                    if (entry.Kind == BuildingAccessKind.Door)
                    {
                        float scale = BuildingEntranceLayout.ModelScale(type, area, rotation);
                        var panels = view.BuildingVolumes(anchor, 0x514637);
                        Require(panels.Any(panel => Math.Abs(panel.Position.Y - point.Y) < .001f &&
                            Math.Abs(panel.GetCenter().X - point.X) <= .12f * scale + .001f &&
                            Math.Abs(panel.GetCenter().Z - point.Z) <= .12f * scale + .001f),
                            $"Entrance metadata must meet the real rendered door opening: {type.Name}, rotation {rotation}, threshold={point}.");
                    }
                    if (entry.Threshold.Y > .05f && entry.Kind is BuildingAccessKind.Door or BuildingAccessKind.Gate)
                    {
                        var sample = point + new Vector3(entry.Direction.X, 0, entry.Direction.Y) * .001f;
                        Require(view.BuildingVolumes(anchor, HouseAppearance.StoneColor).Any(step =>
                            Math.Abs(step.End.Y - point.Y) < .001f &&
                            sample.X >= step.Position.X - .001f && sample.X <= step.End.X + .001f &&
                            sample.Z >= step.Position.Z - .001f && sample.Z <= step.End.Z + .001f),
                            $"Approach steps must meet the actual threshold without a floating end: {type.Name}, rotation {rotation}.");
                    }
                    checkedEntries++;
                }
                var dirt = view.DirtGeometry;
                var thresholds = view.RenderedEntranceThresholds.ToArray();
                view.InvalidateStaticScene(discardBuildingCache: false);
                view.Sync(session, host.Map.Size);
                Require(dirt.SequenceEqual(view.DirtGeometry) && thresholds.SequenceEqual(view.RenderedEntranceThresholds),
                    $"Cached approaches must retain exact entrance geometry: {type.Name}, rotation {rotation}.");
                if (capture is not null && rotation == 0 &&
                    type.Name is "Courtyard House" or "Granary" or "Archer Barracks" or "Workshop" or "Motte and Bailey")
                {
                    await Frames(host);
                    await Capture(host, Path.ChangeExtension(capture, $".entry-{type.Name.Replace(' ', '-')}.png"));
                }
            }
        GD.Print($"TERMCITY_ENTRANCE_CATALOGUE_OK entrances={checkedEntries} rotations=4 doors=actual-model steps=threshold cache=exact");
    }
}
