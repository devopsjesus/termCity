using TermCity.Core.Roads;
using TermCity.Core.Buildings;
using TermCity.Core.Effects;
using Godot;
using TermCity.Core.Rendering;
using System.Diagnostics;
using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

internal static class GodotSmoke
{
    private static async Task RenderedFrame(Main host)
    {
        if (host.Map.PerspectiveEnabled) host.Map.Perspective?.RequestRender();
        host.Map.QueueRedraw();
        await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
    }

    public static async Task VerifyPerspectiveRendering(Main host, string? capturePath = null)
    {
        var session = host.Session;
        var map = session.Game.Map;
        host.Map.SetPerspective(true);
        var view = host.Map.Perspective!;
        session.ClearSelection();
        await VerifyBankColors(host, capturePath);
        await VerifyNaturalInclineBanks(host, capturePath);
        await VerifyNaturalBridgeBanks(host, capturePath);
        var area = map.BuildingFootprints.Values.First(a => map.BuildingAt(a.X, a.Y)!.Name == "Castle");
        var anchor = new Pos(area.X, area.Y);
        if (CityGame.DebugToolsAvailable)
        {
            var monument = map.BuildingFootprints.Values.First(a => map.BuildingAt(a.X, a.Y)!.IsMonument);
            var monumentAnchor = new Pos(monument.X, monument.Y);
            session.CenterOn(monumentAnchor);
            host.Map.RefreshCells();
            view.Sync(session, host.Map.Size);
            var head = new Vector3(monument.X + monument.Width / 2f, 4.3f, monument.Y + monument.Height / 2f);
            Require(view.BuildingVolumes(monumentAnchor).Any(bounds => bounds.End.Y > 4.5f),
                "The monument must render as a colossal sculpture, not a normal service building.");
            Require(host.Map.TryPickBuilding(view.ScreenPoint(head), out var pickedMonument) && pickedMonument == monumentAnchor,
                "The entire monument sculpture must be pickable.");
        }
        session.CenterOn(anchor);
        host.Map.RefreshCells();
        view.Sync(session, host.Map.Size);
        Require(view.Azimuth == 325, "The default viewing angle must face buildings toward the right.");
        var buildingCenter = new Vector3(area.X + area.Width / 2f, 0.5f, area.Y + area.Height / 2f);
        Require(view.ScreenPoint(buildingCenter + Vector3.Back).X > view.ScreenPoint(buildingCenter).X,
            "Building fronts must project toward screen-right at the default angle.");
        var zoomPointer = view.ScreenPoint(new(area.X + area.Width / 2f, 0, area.Y + area.Height / 2f));
        foreach (int targetZoom in new[] { -1, 0, 1, 0, -1, 0 })
        {
            host.Map.ZoomBy(targetZoom - session.ZoomLevel, zoomPointer);
            for (int frame = 0; frame < 3; frame++)
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            var roofPointer = view.ScreenPoint(new(area.X + area.Width / 2f, 1.4f, area.Y + area.Height / 2f));
            Require(session.ZoomLevel == targetZoom && host.Map.TryPickBuilding(roofPointer, out var zoomedBuilding) && zoomedBuilding == anchor,
                $"Using the actual zoom control must keep buildings pickable at {session.ZoomLabel}.");
            if (DisplayServer.GetName() != "headless")
            {
                await RenderedFrame(host);
                using var image = view.Texture.GetImage();
                bool roofVisible = false;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int x = (int)roofPointer.X + dx, y = (int)roofPointer.Y + dy;
                        if (x < 0 || y < 0 || x >= image.GetWidth() || y >= image.GetHeight()) continue;
                        var pixel = image.GetPixel(x, y);
                        roofVisible |= pixel.R > pixel.G * 1.03f;
                    }
                Require(roofVisible, $"The castle roof must be present in rendered pixels at {session.ZoomLabel}, not only picking metadata.");
            }
        }
        int viewportWidth = session.ViewWidth, viewportHeight = session.ViewHeight;
        session.SetViewport(Math.Max(1, viewportWidth - 1), viewportHeight);
        view.Sync(session, host.Map.Size);
        var cameraCenter = new Vector3(session.CameraX + session.ViewWidth * session.Stride / 2f,
            0, session.CameraY + session.ViewHeight * session.Stride / 2f);
        Require(view.ScreenPoint(cameraCenter).DistanceTo(host.Map.Size / 2) < 1,
            "Camera projection must refresh when viewport cell dimensions change, even without a new zoom level.");
        session.SetViewport(viewportWidth, viewportHeight);
        foreach (int zoom in new[] { 0, 1, -1 })
        {
            session.SetZoom(zoom);
            host.Map.Invalidate(true);
            host.Map.RefreshCells();
            session.CenterOn(anchor);
            for (int turn = 0; turn < 4; turn++)
            {
                view.Rotate(90);
                view.Sync(session, host.Map.Size);
                var pointer = view.ScreenPoint(new(area.X + area.Width / 2f, 1.3f, area.Y + area.Height / 2f));
                bool buildingPicked = host.Map.TryPickBuilding(pointer, out var picked);
                Require(buildingPicked && picked == anchor,
                    $"3D building roofs and walls must remain pickable after turn {turn}, zoom {zoom}: " +
                    $"pointer {pointer}, hit {picked}, expected {anchor}, size {host.Map.Size}.");
                host.Map.EmitSignal(Control.SignalName.GuiInput,
                    new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = pointer });
                Require(session.Prompt?.Title == BuildingReport.Title,
                    "Right-clicking a rotated building must open the same Building Status.");
                session.ClosePrompt();
                var groundPointer = view.ScreenPoint(new(area.X + 0.5f, 0, area.Y + 0.5f));
                Require(host.Map.TryMapPosition(groundPointer, out var ground) && ground == anchor,
                    "3D placement must project back to the actual map cell, independently of camera rotation.");
                Require(view.BuildingVolumes(anchor).All(bounds => bounds.Size.Y <= 1.8f),
                    "Large building footprints must not stretch their towers.");
                var merlons = view.BuildingVolumes(anchor).Where(bounds =>
                    Math.Abs(bounds.Size.X - BuildingDetailLayout.MerlonWidth) < 0.0001f &&
                    Math.Abs(bounds.Size.Y - BuildingDetailLayout.MerlonWidth) < 0.0001f &&
                    Math.Abs(bounds.Size.Z - BuildingDetailLayout.MerlonWidth) < 0.0001f).ToArray();
                Require(merlons.Length == 32, "Castle towers must each have eight evenly spaced square merlons.");
                for (int i = 0; i < merlons.Length; i++)
                    for (int j = i + 1; j < merlons.Length; j++)
                        Require(!merlons[i].Intersects(merlons[j]), "Tower merlons must not overlap.");
                host.Map.Invalidate(true);
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                Require(view.BuildingsDrawn > 0, "The perspective viewport must render real building volumes.");
            }
        }
        session.SetZoom(0);
        var house = map.BuildingFootprints.Values.First(a => map.BuildingAt(a.X, a.Y)!.Name == "Burgage House");
        var houseAnchor = new Pos(house.X, house.Y);
        session.CenterOn(houseAnchor);
        host.Map.RefreshCells();
        view.Sync(session, host.Map.Size);
        var glazing = view.BuildingVolumes(houseAnchor, 0x35494b);
        var houseAppearance = HouseAppearance.At(houseAnchor.X, houseAnchor.Y, map.BuildingAt(houseAnchor.X, houseAnchor.Y)!.Level);
        Require(glazing.Count > 0 && view.BuildingVolumes(houseAnchor, houseAppearance.WallColor).Count > 0,
            "Houses must retain broad visible windows and their stable varied wall palette.");
        foreach (var window in glazing)
            Require(new[] { window.Size.X, window.Size.Y, window.Size.Z }.OrderDescending().Take(2).Min() >= 0.17f,
                "House windows must be broad shapes rather than fine-line detail.");
        view.Tilt();
        Require(view.Elevation == 50, "TILT must provide an alternate camera elevation.");
        view.Tilt();
        Require(view.Elevation == 20, "TILT must provide a low 20-degree camera elevation.");
        view.Tilt();
        Require(view.Elevation == 35, "TILT must restore the lower, wall-visible camera elevation.");
        session.CenterOn(new(map.Width / 2, map.Height / 2));
        view.Sync(session, host.Map.Size);
        var well = map.Content.Buildings.Get("Town Well");
        var previewPosition = Enumerable.Range(0, map.Width * map.Height).Select(map.PosOf)
            .Where(p => Math.Abs(p.X - anchor.X) < 8 && Math.Abs(p.Y - anchor.Y) < 8)
            .First(p => session.Game.CanPlaceBuilding(well, p.X, p.Y));
        int beforePreview = view.BuildingsDrawn;
        session.PlaceCursor(previewPosition);
        session.PreviewBuilding(well);
        view.Sync(session, host.Map.Size);
        Require(view.BuildingsDrawn == beforePreview + 1,
            "A valid 3D placement must show the complete building preview, not only a grid outline.");
        Require(view.IndicatorObjectCount > 0,
            "Placement models and selection outlines must flush to the visible indicator layer even without range rings.");
        session.CancelPreview();
        var originalTerrain = map.TerrainAt(previewPosition.X, previewPosition.Y);
        session.CenterOn(previewPosition);
        foreach (string terrainName in new[] { "Meadow", "Hill", "Water" })
        {
            map.SetTerrain(previewPosition.X, previewPosition.Y, map.Content.Terrains.Get(terrainName));
            session.Game.Touch();
            host.Map.RefreshCells();
            view.Sync(session, host.Map.Size);
            var point = new Vector3(previewPosition.X + 0.5f,
                TerrainSprite.Elevation(map, previewPosition.X, previewPosition.Y), previewPosition.Y + 0.5f);
            var pointer = view.ScreenPoint(point);
            bool hit = host.Map.TryMapPosition(pointer, out var pickedTerrain);
            Require(hit && pickedTerrain == previewPosition,
                $"Ground picking must follow {terrainName}: expected {previewPosition}, got {pickedTerrain}, " +
                $"pointer {pointer}, ground {view.GroundPoint(pointer)}.");
        }
        map.SetTerrain(previewPosition.X, previewPosition.Y, originalTerrain);
        session.Game.Touch();
        session.ClearSelection();
        session.PlaceCursor(new(map.Width / 2 + 23, map.Height / 2 + 14));
        view.Sync(session, host.Map.Size);
        host.Map.Invalidate(true);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        var green = map.BuildingFootprints.Values.First(a => map.BuildingAt(a.X, a.Y)!.Name == "Village Green");
        foreach (int tilt in new[] { 0, 1, 2 })
        {
            if (tilt > 0) view.Tilt();
            session.SelectCell(new(green.X, green.Y));
            session.CenterOn(new(green.X, green.Y));
            host.Map.Invalidate(true);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            Require(host.Map.PerspectiveRangeBounds.Count > 0 &&
                host.Map.PerspectiveRangeBounds.All(bounds => Math.Abs(bounds.Size.X - bounds.Size.Y) < 0.001f),
                "Service range guides must remain screen-space circles at either camera tilt.");
        }
        view.Tilt();
        session.ClearSelection();
        foreach (int zoom in new[] { -2, -1, 0, 1 })
        {
            session.SetZoom(zoom);
            host.Map.RefreshCells();
            for (int tilt = 0; tilt < 3; tilt++)
            {
                foreach (var edge in new[] { new Pos(0, 0), new Pos(map.Width - 1, 0),
                    new Pos(0, map.Height - 1), new Pos(map.Width - 1, map.Height - 1) })
                {
                    session.CenterOn(edge);
                    host.Map.RefreshCells();
                    view.Sync(session, host.Map.Size);
                    var projected = view.ScreenPoint(new(edge.X + 0.5f, 0, edge.Y + 0.5f));
                    Require(projected.DistanceTo(host.Map.Size / 2) < host.Map.Grid.PixelHeight * 2,
                        $"Map edge {edge} must reach the middle at zoom {zoom}, tilt {view.Elevation}.");
                    Require(host.Map.TryMapPosition(projected, out var cell) && cell == edge,
                        "Map-edge panning must retain correct ground picking.");
                    var outside = view.ScreenPoint(new(edge.X == 0 ? -2 : map.Width + 2, 0,
                        edge.Y + 0.5f));
                    Require(!host.Map.TryMapPosition(outside, out _), "Off-map ground must not be selectable.");
                }
                view.Tilt();
            }
        }
        session.SetZoom(0);
        host.Map.RefreshCells();
        session.CenterOn(new(5, 5));
        view.Sync(session, host.Map.Size);
        var offMapPointer = view.ScreenPoint(new(-2, 0, -2));
        host.Map.PanPerspective(new(0, 0, 0), offMapPointer);
        Require(session.CameraX > -session.VisibleCellsX / 2 && session.CameraY > -session.VisibleCellsY / 2,
            "Dragging over off-map ground must still move the camera.");
        VerifySmoothPerspectivePanning(host);
        await VerifyTerraceFoundations(host, capturePath);
        await VerifyZoneRendering(host, capturePath);
        await VerifyClosedTerrainEdges(host, capturePath);
        await VerifyRiverBeaches(host, capturePath);
        await VerifyShoreRoadSeams(host, capturePath);
        await VerifyHillRoadRamps(host, capturePath);
        await VerifyDiagonalHillCuts(host, capturePath);
        await VerifyBridgeRendering(host, capturePath);
        await VerifyTrafficCirclePlacement(host, capturePath);
        await VerifyMinimapRotation(host, capturePath);
        await VerifyWaterFlowMotion(host, capturePath);
        await VerifyTreeVariation(host, capturePath);
        await VerifyFootpathRoadJoins(host, capturePath);
        session.PlaceCursor(new(map.Width / 2 + 23, map.Height / 2 + 14));
        session.CenterOn(new(map.Width / 2, map.Height / 2));
        host.Map.Invalidate(true);
        GD.Print("TERMCITY_PERSPECTIVE_RENDER_OK");
    }

    private static async Task VerifyFootpathRoadJoins(Main host, string? capturePath)
    {
        var session = host.Session;
        var map = session.Game.Map;
        var view = host.Map.Perspective!;
        var origin = Enumerable.Range(0, map.Width * map.Height).Select(map.PosOf).Where(position =>
            position.X >= 4 && position.X < map.Width - 13 && position.Y >= 4 && position.Y < map.Height - 13 &&
            new CellRect(position.X, position.Y, 9, 9).Cells().All(cell => session.Game.CanBuildOn(cell.X, cell.Y)))
            .OrderBy(position => Math.Abs(position.X - map.Width / 2) + Math.Abs(position.Y - map.Height / 2)).First();
        var fixture = new CellRect(origin.X, origin.Y, 9, 9);
        var original = fixture.Cells().Select(cell => (Cell: cell, Terrain: map.TerrainAt(cell.X, cell.Y),
            Feature: map.FeatureAt(cell.X, cell.Y), Zone: map.ZoneAt(cell.X, cell.Y))).ToArray();
        foreach (var cell in fixture.Cells())
        {
            map.SetTerrain(cell.X, cell.Y, map.Content.Terrains.Get("Meadow"));
            map.SetFeature(cell.X, cell.Y, null);
            map.SetZone(cell.X, cell.Y, ZoneType.None);
        }
        for (int offset = 1; offset <= 7; offset++)
            map.SetRoad(origin.X + offset, origin.Y + 2, map.Content.Roads.Get(DefaultRoads.KingsRoadName));
        var anchor = new Pos(origin.X + 4, origin.Y + 5);
        var well = map.Content.Buildings.Get("Town Well");
        map.SetBuildingFootprint(well, well.FootprintAt(anchor.X, anchor.Y));
        session.ClearSelection();
        session.PlaceCursor(anchor);
        session.SetZoom(1);
        session.Game.Touch();
        host.Map.RefreshCells();
        session.CenterOn(new(origin.X + 4, origin.Y + 4));
        view.ResetCameraOffset();
        view.Sync(session, host.Map.Size);
        Image? baseline = null;
        if (DisplayServer.GetName() != "headless")
        {
            await RenderedFrame(host);
            baseline = view.Texture.GetImage();
        }
        for (int day = 0; day < 10; day++) session.Game.AdvanceDay();
        host.Map.RefreshCells();
        session.CenterOn(new(origin.X + 4, origin.Y + 4));
        view.ResetCameraOffset();
        view.Sync(session, host.Map.Size);
        var stroke = FootpathLayout.Strokes(map).First(path => path.EndsAtRoad &&
            Math.Abs(path.Points[^1].Y - (origin.Y + 2.5f)) < 0.01f &&
            path.Points[^1].X >= origin.X + 1 && path.Points[^1].X <= origin.X + 8 &&
            path.Points.Any(point => Math.Abs(point.X - (anchor.X + 0.5f)) < 0.01f && point.Y > origin.Y + 3));
        var roads = new RoadVectorLayer();
        roads.Sync(session.Game);
        var layout = new RoadMeshLayout(map, roads.Paths);
        var sections = FootpathLayout.RibbonSections(stroke, layout);
        Require(view.FootpathTrianglesDrawn > 0 && layout.InRoadInterior(sections[^1].Center),
            "Real sim-generated trails must render terrain-following ribbons that reach the actual road.");
        if (baseline is not null)
        {
            using (baseline)
            {
                await RenderedFrame(host);
                using var image = view.Texture.GetImage();
                if (capturePath is not null)
                    Require(image.SavePng(Path.ChangeExtension(capturePath, ".paths.png")) == Error.Ok,
                        "Smoothed road/path intersection screenshot must save successfully.");
                var contact = sections.Where(section => !layout.InRoadInterior(section.Center))
                    .OrderBy(section => System.Numerics.Vector2.DistanceSquared(section.Center, sections[^1].Center)).First();
                foreach (var edge in new[] { contact.Left, contact.Right })
                {
                    var pointer = view.ScreenPoint(new(edge.X, view.RenderedGroundHeight(map, edge.X, edge.Y) + 0.014f, edge.Y));
                    bool dirt = false;
                    for (int offsetY = -2; offsetY <= 2; offsetY++)
                        for (int offsetX = -2; offsetX <= 2; offsetX++)
                        {
                            int pixelX = (int)pointer.X + offsetX, pixelY = (int)pointer.Y + offsetY;
                            if (pixelX < 0 || pixelY < 0 || pixelX >= image.GetWidth() || pixelY >= image.GetHeight()) continue;
                            var pixel = image.GetPixel(pixelX, pixelY);
                            dirt |= pixel.R > pixel.G * 1.08f && pixel.G > pixel.B * 1.1f;
                        }
                    Require(dirt, $"Both flared edges of a generated trail must meet the visible road without a grass gap: {edge}, screen {pointer}, wear {stroke.Wear}, triangles {view.FootpathTrianglesDrawn}.");
                }
                var roadPoint = sections[^1].Center;
                var roadPointer = view.ScreenPoint(new(roadPoint.X, layout.HeightAt(roadPoint), roadPoint.Y));
                var before = baseline.GetPixel((int)roadPointer.X, (int)roadPointer.Y);
                var after = image.GetPixel((int)roadPointer.X, (int)roadPointer.Y);
                Require(new Vector3(before.R - after.R, before.G - after.G, before.B - after.B).Length() < 0.03f,
                    "Generated trail ribbons must stop at the road edge, not paint a dirt stripe over its surface.");
            }
        }
        map.SetBuilding(anchor.X, anchor.Y, null);
        foreach (var entry in original)
        {
            map.SetRoad(entry.Cell.X, entry.Cell.Y, false);
            map.SetTerrain(entry.Cell.X, entry.Cell.Y, entry.Terrain);
            map.SetFeature(entry.Cell.X, entry.Cell.Y, entry.Feature);
            map.SetZone(entry.Cell.X, entry.Cell.Y, entry.Zone);
        }
        session.Game.Touch();
        GD.Print("TERMCITY_FOOTPATH_ROAD_JOINS_OK");
    }

    private static async Task VerifyWaterFlowMotion(Main host, string? capturePath)
    {
        var session = host.Session;
        var map = session.Game.Map;
        var view = host.Map.Perspective!;
        int originalZoom = session.ZoomLevel;
        var river = Enumerable.Range(0, map.Width * map.Height).Select(map.PosOf)
            .Where(p => map.TerrainAt(p.X, p.Y).Name == "Water" && TerrainSprite.Flow(map).At(p.X, p.Y).River)
            .OrderBy(p => Math.Abs(p.X - map.Width / 2) + Math.Abs(p.Y - map.Height / 2)).First();
        session.PlaceCursor(river);
        var settings = host.Map.Effects!.Settings;
        var originalEffects = (settings.Enabled, settings.ReducedMotion, settings.Intensity);
        bool paused = session.Game.Paused;
        session.Game.Paused = true;
        settings.Level = EffectLevel.Low;
        settings.ReducedMotion = false;
        foreach (int zoom in new[] { 0, 1 })
        {
            session.SetZoom(zoom);
            host.Map.Invalidate(true);
            host.Map.RefreshCells();
            session.CenterOn(river);
            view.ResetCameraOffset();
            view.Sync(session, host.Map.Size);
            double clock = view.WaterFlowClock;
            view._Process(0.3);
            Require(view.WaterFlowClock > clock, "Visible rivers must advance downstream flow with low effects at both playable zooms.");
            if (capturePath is null || DisplayServer.GetName() == "headless") continue;
            for (int frame = 0; frame < 3; frame++)
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await RenderedFrame(host);
            using var first = view.Texture.GetImage();
            double firstClock = view.WaterFlowRenderClock;
            double firstDays = session.Game.ElapsedDays;
            int firstBuilds = view.GeometryBuilds;
            await host.ToSignal(host.GetTree().CreateTimer(1.5), SceneTreeTimer.SignalName.Timeout);
            for (int frame = 0; frame < 2; frame++)
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await RenderedFrame(host);
            using var second = view.Texture.GetImage();
            double travel = (view.WaterFlowRenderClock - firstClock) * 0.75;
            Require(travel > 0.5 && session.Game.ElapsedDays == firstDays && view.GeometryBuilds == firstBuilds,
                "Real rendered river frames must advance while paused, without advancing gameplay or rebuilding geometry.");
            string suffix = zoom == 0 ? ".flow-normal" : ".flow";
            if (first.SavePng(Path.ChangeExtension(capturePath, suffix + "-before.png")) != Error.Ok ||
                second.SavePng(Path.ChangeExtension(capturePath, suffix + "-after.png")) != Error.Ok)
                throw new IOException("River-flow screenshots failed.");
            int moving = 0, blue = 0;
            for (int y = 0; y < first.GetHeight(); y += 2)
                for (int x = 0; x < first.GetWidth(); x += 2)
                {
                    var a = first.GetPixel(x, y); var b = second.GetPixel(x, y);
                    if (a.B <= a.G * 1.05f || a.B <= a.R * 1.3f) continue;
                    blue++;
                    if (Math.Abs(a.B - b.B) > 1f / 255) moving++;
                    Require(Math.Abs(a.B - b.B) < 0.11f, "Traveling water clusters must remain within the existing muted blue palette.");
                }
            VerifyDownstreamRipples(map, view, river, first, second, firstClock, view.WaterFlowRenderClock, zoom);
            Require(moving > 20 && moving < blue * 0.30f, $"Sparse river ripples must move visibly while most water pixels remain stationary; changed samples: {moving}, " +
                $"water samples: {blue}, zoom {zoom}, river {river}, flow {TerrainSprite.Flow(map).At(river.X, river.Y)}, clock {view.WaterFlowClock}.");
            GD.Print($"TERMCITY_WATER_FLOW_ZOOM_{zoom}_OK changed={moving} water={blue}");
        }
        session.Game.Paused = true;
        view.Sync(session, host.Map.Size);
        double before = view.WaterFlowClock;
        double elapsedDays = session.Game.ElapsedDays;
        int geometryBuilds = view.GeometryBuilds;
        view._Process(0.3);
        Require(view.WaterFlowClock > before, "Paused cities must keep decorative river flow moving without advancing gameplay.");
        Require(session.Game.ElapsedDays == elapsedDays && view.GeometryBuilds == geometryBuilds,
            "Paused river animation must not advance gameplay or rebuild the terrain.");
        before = view.WaterFlowClock;
        session.Game.Paused = false;
        settings.Level = EffectLevel.Off;
        host.Map.RefreshCells();
        view.Sync(session, host.Map.Size);
        view._Process(0.3);
        Require(view.WaterFlowClock == before, "Reduced-motion/effects-off mode must suppress water animation.");
        settings.Level = EffectLevel.Low;
        settings.ReducedMotion = true;
        host.Map.RefreshCells();
        view.Sync(session, host.Map.Size);
        view._Process(0.3);
        Require(view.WaterFlowClock == before, "Reduced motion must suppress river animation even when effects are otherwise enabled.");
        session.Game.Paused = paused;
        (settings.Enabled, settings.ReducedMotion, settings.Intensity) = originalEffects;
        session.SetZoom(originalZoom);
        host.Map.Invalidate(true);
        host.Map.RefreshCells();
        view.Sync(session, host.Map.Size);
        GD.Print("TERMCITY_WATER_FLOW_OK");
    }

    private static void VerifyDownstreamRipples(GameMap map, PerspectiveCity view, Pos river,
        Image first, Image second, double firstClock, double secondClock, int zoom)
    {
        int samples = 0, movingMatches = 0;
        var flow = TerrainSprite.Flow(map);
        double travel = (secondClock - firstClock) * RiverRippleLayout.Speed;
        for (int row = Math.Max(0, river.Y - 8); row <= Math.Min(map.Height - 1, river.Y + 8); row++)
            for (int column = Math.Max(0, river.X - 8); column <= Math.Min(map.Width - 1, river.X + 8); column++)
            {
                if (map.TerrainAt(column, row).Buildable) continue;
                var track = RiverRippleLayout.At(map, flow, column, row);
                if (track.Position(firstClock) is not { } initial) continue;
                double phase = (firstClock * RiverRippleLayout.Speed + track.Distance) % RiverRippleLayout.Spacing;
                int endX = column, endY = row;
                for (int step = 0; step < (int)Math.Floor(phase + travel); step++)
                {
                    var direction = flow.At(endX, endY).Direction;
                    endX += direction.X;
                    endY += direction.Y;
                    if (!map.InBounds(endX, endY)) break;
                }
                if (!map.InBounds(endX, endY) || map.TerrainAt(endX, endY).Buildable) continue;
                var downstream = RiverRippleLayout.At(map, flow, endX, endY);
                if (downstream.Position(secondClock) is not { } final) continue;
                var start = new Vector2(column + initial.X, row + initial.Y);
                var end = new Vector2(endX + final.X, endY + final.Y);
                var beforePoint = view.ScreenPoint(new(start.X, view.RenderedGroundHeight(map, start.X, start.Y), start.Y));
                var afterPoint = view.ScreenPoint(new(end.X, view.RenderedGroundHeight(map, end.X, end.Y), end.Y));
                if (beforePoint.X < 1 || beforePoint.X >= first.GetWidth() - 1 || beforePoint.Y < 1 || beforePoint.Y >= first.GetHeight() - 1 ||
                    afterPoint.X < 1 || afterPoint.X >= second.GetWidth() - 1 || afterPoint.Y < 1 || afterPoint.Y >= second.GetHeight() - 1)
                    continue;
                var original = first.GetPixel((int)beforePoint.X, (int)beforePoint.Y);
                var moved = second.GetPixel((int)afterPoint.X, (int)afterPoint.Y);
                var leftBehind = second.GetPixel((int)beforePoint.X, (int)beforePoint.Y);
                var destinationBefore = first.GetPixel((int)afterPoint.X, (int)afterPoint.Y);
                if (!Blue(original) || !Blue(moved) || !Blue(leftBehind) || !Blue(destinationBefore)) continue;
                samples++;
                if (original.G > leftBehind.G + 0.012f && moved.G > destinationBefore.G + 0.012f) movingMatches++;
            }
        Require(samples >= 3 && movingMatches > samples * 0.55f,
            $"Sparse ripple centers must travel along downstream tracks, not brighten in place: zoom {zoom}, samples {samples}, tracked {movingMatches}.");
        GD.Print($"TERMCITY_DOWNSTREAM_RIPPLES_OK zoom={zoom} samples={samples} tracked={movingMatches}");
        static bool Blue(Color color) => color.B > color.G * 1.05f && color.B > color.R * 1.3f;
    }

    private static async Task VerifyMinimapRotation(Main host, string? capturePath)
    {
        var session = host.Session;
        var map = session.Game.Map;
        var view = host.Map.Perspective!;
        var center = new Pos(map.Width / 2, map.Height / 2);
        float originalAzimuth = view.Azimuth;
        session.CenterOn(center);
        host.Minimap.QueueRedraw();
        for (int frame = 0; frame < 3; frame++) await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        var originalPicture = host.Minimap.Picture;
        int initialTextureBuilds = host.Minimap.TextureBuilds;
        for (int heading = 0; heading < 4; heading++)
        {
            view.Rotate(Mathf.PosMod(originalAzimuth + heading * 90 - view.Azimuth, 360));
            host.Map.RefreshCells();
            view.Sync(session, host.Map.Size);
            host.Minimap.QueueRedraw();
            for (int frame = 0; frame < 3; frame++) await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            int textureBuilds = host.Minimap.TextureBuilds;
            var origin = host.Minimap.ScreenPoint(center);
            var picture = host.Minimap.Picture;
            Require(picture.Size.X == picture.Size.Y && picture == originalPicture,
                "The minimap must remain a fixed square at every camera heading.");
            int turns = (int)Math.Round(Mathf.PosMod(view.Azimuth - PerspectiveCity.InitialAzimuth, 360) / 90f);
            var projection = new MinimapProjection(map.Width, map.Height, turns);
            Require(host.Minimap.MapPointToScreen(Vector2.Zero).DistanceTo(picture.Position +
                new Vector2(projection.Fraction(System.Numerics.Vector2.Zero).X,
                    projection.Fraction(System.Numerics.Vector2.Zero).Y) * picture.Size) < 0.01f,
                "The full square minimap contents must rotate once per camera quarter turn.");
            VerifyFootprint();
            var beforeTilt = host.Minimap.ViewOutline;
            var beforePicture = host.Minimap.Picture;
            view.Tilt();
            view.Sync(session, host.Map.Size);
            host.Minimap.QueueRedraw();
            for (int frame = 0; frame < 3; frame++) await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            var afterTilt = host.Minimap.ViewOutline;
            Require(host.Minimap.Picture == beforePicture && afterTilt.Where((corner, index) => corner.DistanceTo(beforeTilt[index]) > 0.5f).Any(),
                "Tilt must reshape the view indicator without tilting or resizing the square minimap.");
            VerifyFootprint();
            view.Tilt();
            view.Tilt();
            view.Sync(session, host.Map.Size);
            host.Minimap.QueueRedraw();

            void VerifyFootprint()
            {
                var outline = host.Minimap.ViewOutline;
                var expected = new[] { Vector2.Zero, new Vector2(host.Map.Size.X, 0), host.Map.Size, new Vector2(0, host.Map.Size.Y) };
                for (int corner = 0; corner < 4; corner++)
                {
                    var fraction = (outline[corner] - picture.Position) / picture.Size;
                    var world = projection.MapPoint(new(fraction.X, fraction.Y));
                    Require(view.ScreenPoint(new(world.X, 0, world.Y)).DistanceTo(expected[corner]) < 0.2f,
                        "The white indicator must trace the actual displayed camera footprint, not a rotated session bounding box.");
                }
            }
            var destination = new Pos(center.X + map.Width / 8, center.Y - map.Height / 8);
            host.Minimap.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
                { ButtonIndex = MouseButton.Left, Pressed = true, Position = host.Minimap.ScreenPoint(destination) });
            Require(session.CameraX + session.VisibleCellsX / 2 == destination.X &&
                session.CameraY + session.VisibleCellsY / 2 == destination.Y, "Rotated minimap clicks must reach the selected world cell.");
            host.Minimap.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion { Position = origin });
            host.Minimap.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
            Require(session.CameraX + session.VisibleCellsX / 2 == center.X && session.CameraY + session.VisibleCellsY / 2 == center.Y,
                "Rotated minimap dragging must retain the inverse world transform.");
            Require(host.Minimap.TextureBuilds == textureBuilds, "Camera rotation/navigation must reuse the cached minimap texture.");
            if (capturePath is not null && DisplayServer.GetName() != "headless")
            {
                await RenderedFrame(host);
                using var image = host.GetViewport().GetTexture().GetImage();
                Require(image.SavePng(Path.ChangeExtension(capturePath, $".minimap-{heading}.png")) == Error.Ok,
                    "Rotated minimap capture failed.");
                var outline = host.Minimap.ViewOutline;
                for (int edge = 0; edge < 4; edge++)
                {
                    var midpoint = (outline[edge] + outline[(edge + 1) % 4]) / 2;
                    Require(picture.HasPoint(midpoint), "The centered fixture must keep all four indicator edges inside the square.");
                    var global = host.Minimap.GlobalPosition + midpoint;
                    var scale = new Vector2(image.GetWidth() / host.Size.X, image.GetHeight() / host.Size.Y);
                    var pixel = global * scale;
                    bool white = false;
                    for (int offsetY = -2; offsetY <= 2; offsetY++)
                        for (int offsetX = -2; offsetX <= 2; offsetX++)
                        {
                            var color = image.GetPixel((int)pixel.X + offsetX, (int)pixel.Y + offsetY);
                            white |= color.R > 0.9f && color.G > 0.9f && color.B > 0.9f;
                        }
                    Require(white, "All four camera-footprint edges must have visible white indicator pixels.");
                }
            }
        }
        Require(host.Minimap.TextureBuilds == initialTextureBuilds,
            "Camera heading and tilt changes must not rebuild the minimap terrain texture.");
        view.Rotate(Mathf.PosMod(originalAzimuth - view.Azimuth, 360));
        host.Map.RefreshCells();
        host.Minimap.QueueRedraw();
        GD.Print("TERMCITY_MINIMAP_ROTATION_OK headings=4 click-drag=camera-aligned cached=true");
    }

    private static async Task VerifyNaturalInclineBanks(Main host, string? capturePath)
    {
        if (capturePath is null || DisplayServer.GetName() == "headless") return;
        var session = host.Session;
        var map = session.Game.Map;
        var view = host.Map.Perspective!;
        var vectors = new RoadVectorLayer();
        vectors.Sync(session.Game);
        var layout = new RoadMeshLayout(map, vectors.Paths);
        var inclines = layout.Segments.Where(segment => !segment.Bridge &&
            Math.Max(segment.HeightA, segment.HeightB) > 0.1f)
            .SelectMany(segment => new[] { 0.25f, 0.5f, 0.75f }
                .Select(amount => System.Numerics.Vector2.Lerp(segment.A, segment.B, amount)))
            .Where(center => center.X > 12 && center.Y > 12 && center.X < map.Width - 12 && center.Y < map.Height - 12)
            .OrderBy(center => System.Numerics.Vector2.DistanceSquared(center,
                map.Width > 320 ? new(250, 54) : new(99, 149)))
            .GroupBy(center => ((int)center.X / 6, (int)center.Y / 6)).Select(group => group.First()).Take(6).ToArray();
        Require(inclines.Length > 0, "The generated map must exercise natural road inclines.");
        int originalZoom = session.ZoomLevel;
        session.SetZoom(1);
        host.Map.RefreshCells();
        for (int incline = 0; incline < inclines.Length; incline++)
        {
            var center = inclines[incline];
            session.CenterOn(new((int)center.X, (int)center.Y));
            host.Map.RefreshCells();
            for (int heading = 0; heading < 4; heading++)
            {
                view.Sync(session, host.Map.Size);
                for (int frame = 0; frame < 3; frame++)
                    await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                await RenderedFrame(host);
                using var image = view.Texture.GetImage();
                Require(image.SavePng(Path.ChangeExtension(capturePath, $".natural-incline-{incline}-{heading}.png")) == Error.Ok,
                    "The natural incline capture must save successfully.");
                for (int pixelY = 40; pixelY < image.GetHeight() - 40; pixelY++)
                    for (int pixelX = 40; pixelX < image.GetWidth() - 40; pixelX++)
                    {
                        var color = image.GetPixel(pixelX, pixelY);
                        if (Math.Abs(color.R - 20 / 255f) <= 0.005f && Math.Abs(color.G - 34 / 255f) <= 0.005f &&
                            Math.Abs(color.B - 28 / 255f) <= 0.005f)
                            throw new InvalidOperationException(
                                $"Natural incline banks must not expose black background at {center}, heading {heading}, pixel {pixelX},{pixelY}, " +
                                $"ground hit {view.GroundPoint(new(pixelX + 0.5f, pixelY + 0.5f))}.");
                    }
                view.Rotate(90);
            }
        }
        session.SetZoom(originalZoom);
        host.Map.RefreshCells();
        GD.Print($"TERMCITY_NATURAL_INCLINE_BANKS_OK inclines={inclines.Length}");
    }

    private static async Task VerifyNaturalBridgeBanks(Main host, string? capturePath)
    {
        if (capturePath is null || DisplayServer.GetName() == "headless") return;
        var session = host.Session;
        var map = session.Game.Map;
        var view = host.Map.Perspective!;
        var vectors = new RoadVectorLayer();
        vectors.Sync(session.Game);
        var layout = new RoadMeshLayout(map, vectors.Paths);
        var approaches = layout.Segments.Where(segment => segment.Bridge)
            .Select(segment => (segment.Path, Midpoint: (segment.A + segment.B) / 2))
            .Where(sample => sample.Midpoint.X >= 5 && sample.Midpoint.X < map.Width - 5 &&
                sample.Midpoint.Y >= 5 && sample.Midpoint.Y < map.Height - 5 &&
                TerrainSprite.WaterSurface(map, sample.Midpoint.X, sample.Midpoint.Y) is >= 0.38f and < 0.95f)
            .GroupBy(sample => sample.Path).Select(group => group.First()).Take(6).ToArray();
        Require(approaches.Length > 0, "The generated-map fixture must contain a real river bridge approach.");
        int originalZoom = session.ZoomLevel;
        for (int index = 0; index < approaches.Length; index++)
        {
            var center = approaches[index].Midpoint;
            var anchor = new Pos((int)center.X, (int)center.Y);
            session.PlaceCursor(anchor);
            session.SetZoom(1);
            host.Map.Invalidate(true);
            host.Map.RefreshCells();
            session.CenterOn(anchor);
            view.ResetCameraOffset();
            for (int turn = 0; turn < 4; turn++)
            {
                view.Sync(session, host.Map.Size);
                await RenderedFrame(host);
                using var image = view.Texture.GetImage();
                Require(image.SavePng(Path.ChangeExtension(capturePath, $".natural-bridge-{index}-{turn}.png")) == Error.Ok,
                    "Generated bridge approach capture must save.");
                for (float pz = center.Y - 2; pz <= center.Y + 2; pz += 0.025f)
                    for (float px = center.X - 2; px <= center.X + 2; px += 0.025f)
                    {
                        if (TerrainSprite.WaterSurface(map, px, pz) < ShorelineLayout.WaterThreshold) continue;
                        var pointer = view.ScreenPoint(new(px,
                            TerrainSprite.WaterCornerLevel(map, (int)px, (int)pz), pz));
                        int imageX = (int)pointer.X, imageY = (int)pointer.Y;
                        if (imageX < 1 || imageY < 1 || imageX >= image.GetWidth() - 1 || imageY >= image.GetHeight() - 1) continue;
                        var color = image.GetPixel(imageX, imageY);
                        Require(color.G > 0.15f || color.B > 0.15f,
                            $"Generated bridge banks must not expose black gaps at {px},{pz}, bridge {index}, heading {turn}: {color}, " +
                            $"pixel {imageX},{imageY}, surface hit {view.GroundPoint(new(imageX + 0.5f, imageY + 0.5f))}, " +
                            $"terrain {map.TerrainAt((int)px, (int)pz).Name}, rendered {view.RenderedGroundHeight(map, px, pz)}.");
                    }
                view.Rotate(90);
            }
        }
        session.SetZoom(originalZoom);
        host.Map.Invalidate(true);
        host.Map.RefreshCells();
        GD.Print($"TERMCITY_NATURAL_BRIDGE_BANKS_OK approaches={approaches.Length}");
    }

    private static async Task VerifyTreeVariation(Main host, string? capturePath)
    {
        var session = host.Session;
        var map = session.Game.Map;
        var view = host.Map.Perspective!;
        int originalZoom = session.ZoomLevel;
        var origin = Enumerable.Range(0, map.Width * map.Height).Select(map.PosOf).Where(p =>
            p.X >= 4 && p.X < map.Width - 16 && p.Y >= 4 && p.Y < map.Height - 12 &&
            new CellRect(p.X, p.Y, 13, 9).Cells().All(cell => session.Game.CanBuildOn(cell.X, cell.Y)))
            .OrderBy(p => Math.Abs(p.X - map.Width / 2) + Math.Abs(p.Y - map.Height / 2)).First();
        var area = new CellRect(origin.X, origin.Y, 13, 9);
        var original = area.Cells().Select(p => (Position: p, Feature: map.FeatureAt(p.X, p.Y))).ToArray();
        foreach (var cell in area.Cells()) map.SetFeature(cell.X, cell.Y, null);
        foreach (var shape in Enum.GetValues<TreeShape>())
        {
            var target = new Pos(origin.X + 2 + (int)shape % 3 * 4, origin.Y + 2 + (int)shape / 3 * 4);
            var cell = area.Cells().Where(p => TreeAppearance.At(p.X * 5 + 2, p.Y * 5 + 2).Shape == shape)
                .OrderBy(p => Math.Abs(p.X - target.X) + Math.Abs(p.Y - target.Y)).First();
            map.SetFeature(cell.X, cell.Y, map.Content.Features.Get("Tree"));
        }
        session.Game.Touch();
        session.PlaceCursor(origin);
        session.SetZoom(0);
        host.Map.RefreshCells();
        session.CenterOn(new(origin.X + 6, origin.Y + 4));
        view.ResetCameraOffset();
        view.Sync(session, host.Map.Size);
        Require(view.TreeShapesDrawn.SetEquals(Enum.GetValues<TreeShape>()),
            "The perspective woodland must render all four broad-leaf crowns and both pine silhouettes.");
        if (capturePath is not null && DisplayServer.GetName() != "headless")
        {
            await RenderedFrame(host);
            using var image = view.Texture.GetImage();
            Require(image.SavePng(Path.ChangeExtension(capturePath, ".trees.png")) == Error.Ok,
                "Tree variation screenshot must save successfully.");
        }
        foreach (var entry in original) map.SetFeature(entry.Position.X, entry.Position.Y, entry.Feature);
        session.Game.Touch();
        session.SetZoom(originalZoom);
        host.Map.RefreshCells();
        view.Sync(session, host.Map.Size);
        GD.Print("TERMCITY_TREE_VARIATION_OK");
    }

    private static async Task VerifyZoneRendering(Main host, string? capturePath)
    {
        var session = host.Session;
        var map = session.Game.Map;
        var view = host.Map.Perspective!;
        var origin = Enumerable.Range(0, map.Width * map.Height).Select(map.PosOf).Where(p =>
            p.X >= 4 && p.X < map.Width - 14 && p.Y >= 4 && p.Y < map.Height - 8 &&
            new CellRect(p.X, p.Y, 11, 7).Cells().All(cell => session.Game.CanBuildOn(cell.X, cell.Y)))
            .OrderBy(p => Math.Abs(p.X - map.Width / 2) + Math.Abs(p.Y - map.Height / 2)).First();
        var fixture = new CellRect(origin.X, origin.Y, 11, 7);
        var original = fixture.Cells().Select(p => (Position: p, Terrain: map.TerrainAt(p.X, p.Y),
            Feature: map.FeatureAt(p.X, p.Y), Sea: map.IsSea(p.X, p.Y), River: map.IsRiver(p.X, p.Y))).ToArray();
        for (int index = 0; index < Zones.Placeable.Count; index++)
        {
            var area = new CellRect(origin.X + 1 + index * 3, origin.Y + 1, 2, 2);
            foreach (var cell in area.Cells())
            {
                map.SetTerrain(cell.X, cell.Y, map.Content.Terrains.Get(index == 0 ? "Meadow" : "Hill"));
                map.SetFeature(cell.X, cell.Y, map.Content.Features.Get("Tree"));
            }
            session.BeginDrag(new(area.X, area.Y));
            session.UpdateDrag(new(area.Right, area.Bottom));
            session.EndSelection();
            var result = session.Zone(Zones.Placeable[index]);
            Require(result.Success && result.Cells == area.Area, "Real zoning actions must designate every vacant test plot.");
            Require(area.Cells().All(cell => map.FeatureAt(cell.X, cell.Y) is null), "Zoning must clear trees from the plots.");
        }
        session.ClearSelection();
        session.PlaceCursor(origin);
        session.SetZoom(1);
        host.Map.RefreshCells();
        session.CenterOn(new(origin.X + 5, origin.Y + 2));
        view.ResetCameraOffset();
        view.Sync(session, host.Map.Size);
        Require(view.ZonePlotsDrawn >= 12, "Vacant residential, commercial and industrial plots must remain visible after clearing selection.");
        var restored = SaveGameStore.Deserialize(SaveGameStore.Serialize(session.Game));
        foreach (var zone in Zones.Placeable)
            Require(restored.Map.ZoneCells(zone).Count == map.ZoneCells(zone).Count, "Visible zone designations must survive saving and loading.");
        if (DisplayServer.GetName() != "headless")
        {
            var designations = fixture.Cells().Where(cell => map.ZoneAt(cell.X, cell.Y) != ZoneType.None)
                .Select(cell => (Cell: cell, Zone: map.ZoneAt(cell.X, cell.Y))).ToArray();
            for (int turn = 0; turn < 4; turn++)
            {
                foreach (var designation in designations) map.SetZone(designation.Cell.X, designation.Cell.Y, ZoneType.None);
                session.Game.Touch();
                view.Sync(session, host.Map.Size);
                await RenderedFrame(host);
                using var terrainImage = view.Texture.GetImage();
                foreach (var designation in designations) map.SetZone(designation.Cell.X, designation.Cell.Y, designation.Zone);
                session.Game.Touch();
                for (int index = 0; index < Zones.Placeable.Count; index++)
                {
                    view.Sync(session, host.Map.Size);
                    await RenderedFrame(host);
                    using var image = view.Texture.GetImage();
                    var zone = Zones.Placeable[index];
                    var artwork = CellRenderer.RenderZone(session.Game, origin.X + 1 + index * 3, origin.Y + 1);
                    var foreground = new Vector3(artwork.Foreground.R, artwork.Foreground.G, artwork.Foreground.B) / 255;
                    var background = new Vector3(artwork.Background.R, artwork.Background.G, artwork.Background.B) / 255;
                    bool visible = false;
                    bool transparentGap = false;
                    float x = origin.X + 1 + index * 3 + 0.5f, z = origin.Y + 1.5f;
                    var pointer = view.ScreenPoint(new(x, view.RenderedGroundHeight(map, x, z) + 0.018f, z));
                    for (int dy = -5; dy <= 5; dy++)
                        for (int dx = -5; dx <= 5; dx++)
                        {
                            int px = (int)pointer.X + dx, py = (int)pointer.Y + dy;
                            if (px < 0 || py < 0 || px >= image.GetWidth() || py >= image.GetHeight()) continue;
                            var pixel = image.GetPixel(px, py);
                            var terrain = terrainImage.GetPixel(px, py);
                            var terrainColor = new Vector3(terrain.R, terrain.G, terrain.B);
                            var color = new Vector3(pixel.R, pixel.G, pixel.B);
                            for (int sample = 0; sample <= 20; sample++)
                            {
                                float coverage = sample / 20f;
                                float alpha = PerspectiveCity.ZoneOverlayOpacity *
                                    Mathf.Lerp(ZoneOverlayArtwork.BackgroundCoverage, 1, coverage);
                                var expected = terrainColor.Lerp(background.Lerp(foreground, coverage), alpha);
                                if (color.DistanceTo(expected) >= 0.025f) continue;
                                visible |= coverage > 0.4f;
                                transparentGap |= coverage < 0.1f;
                            }
                        }
                    if (turn == 0 && index == 0 && capturePath is not null)
                    {
                        var error = image.SavePng(Path.ChangeExtension(capturePath, ".zones.png"));
                        if (error != Error.Ok) throw new IOException($"Zone screenshot failed: {error}.");
                    }
                    Require(visible && transparentGap, $"{zone} checkerboard must show both broad colored checks and transparent gaps, including raised plots, after turn {turn}: pointer {pointer}, image {image.GetSize()}.");
                }
                view.Rotate(90);
            }
        }
        view.Sync(session, host.Map.Size);
        int designatedPlots = view.ZonePlotsDrawn;
        session.BeginDrag(origin);
        session.UpdateDrag(new(fixture.Right, fixture.Bottom));
        session.EndSelection();
        Require(session.Dezone().Success, "Vacant zone plots must be removable through the normal UI action.");
        session.ClearSelection();
        host.Map.RefreshCells();
        view.Sync(session, host.Map.Size);
        Require(view.ZonePlotsDrawn == designatedPlots - 12, "Dezoning must remove the rendered plot markers immediately.");
        var building = map.Content.Buildings.Get("Fire Watch");
        var buildingAnchor = new Pos(origin.X + 1, origin.Y + 1);
        for (int rotation = 0; rotation < 4; rotation++)
        {
            session.PlaceCursor(buildingAnchor);
            map.SetBuildingFootprint(building, building.FootprintAt(buildingAnchor.X, buildingAnchor.Y, rotation));
            if (rotation != 0) map.BuildingRotations[map.Index(buildingAnchor.X, buildingAnchor.Y)] = rotation;
            session.Game.Touch();
            host.Map.RefreshCells();
            view.Sync(session, host.Map.Size);
            var area = map.BuildingFootprintAt(buildingAnchor.X, buildingAnchor.Y);
            var center = new Vector3(area.X + area.Width / 2f, 0, area.Y + area.Height / 2f);
            var door = view.BuildingVolumes(buildingAnchor, 0x584738).Single();
            var doorCenter = door.Position + door.Size / 2;
            var facing = rotation switch
            {
                1 => Vector3.Left,
                2 => Vector3.Forward,
                3 => Vector3.Right,
                _ => Vector3.Back,
            };
            var expectedDoor = center + facing * (building.Height * 0.78f / 2 + 0.016f);
            Require(new Vector2(doorCenter.X - expectedDoor.X, doorCenter.Z - expectedDoor.Z).Length() < 0.001f,
                "Actual placed building fronts must rotate in 90-degree steps, including half turns.");
            Require(host.Map.TryPickBuilding(view.ScreenPoint(doorCenter), out var pickedBuilding) && pickedBuilding == buildingAnchor,
                "Picking must follow the rotated visible building faces.");
            Require(session.Demolish().Success, "Rotated buildings must be removable through their actual footprint.");
        }
        session.ClearSelection();
        GD.Print("TERMCITY_BUILDING_ROTATION_OK");
        var cottage = map.Content.Buildings.Get("Cottage");
        var houses = Enumerable.Range(0, 6).Select(index =>
            new Pos(origin.X + 1 + index % 3 * 3, origin.Y + 1 + index / 3 * 3)).ToArray();
        foreach (var anchor in houses)
        {
            var footprint = new CellRect(anchor.X, anchor.Y, 2, 2);
            foreach (var cell in footprint.Cells()) map.SetZone(cell.X, cell.Y, ZoneType.Residential);
            map.SetBuildingFootprint(cottage, footprint);
        }
        session.Game.Touch();
        session.PlaceCursor(origin);
        session.SetZoom(0);
        host.Map.RefreshCells();
        session.CenterOn(new(origin.X + 5, origin.Y + 3));
        view.ResetCameraOffset();
        view.Sync(session, host.Map.Size);
        var wallColors = new HashSet<int>();
        foreach (var anchor in houses)
        {
            var appearance = HouseAppearance.At(anchor.X, anchor.Y, cottage.Level);
            Require(view.BuildingVolumes(anchor, appearance.WallColor).Count > 0,
                "Rendered homes must use their stable varied wall palette.");
            Require(view.BuildingVolumes(anchor, HouseAppearance.TimberColor).Count >= 10 &&
                view.BuildingVolumes(anchor, HouseAppearance.StoneColor).Count >= 1 &&
                view.BuildingVolumes(anchor, HouseAppearance.SillColor).Count >= 2 &&
                view.BuildingVolumes(anchor, appearance.RoofCapColor).Count == 1,
                "Medieval homes must have broad timber corners/eaves, recessed window surrounds and masonry bases/sills.");
            wallColors.Add(appearance.WallColor);
            var door = view.BuildingVolumes(anchor, 0x514637).Single();
            Require(host.Map.TryPickBuilding(view.ScreenPoint(door.Position + door.Size / 2), out var pickedHouse) && pickedHouse == anchor,
                $"Varied house fronts must remain pickable: expected {anchor}, picked {pickedHouse}.");
        }
        Require(wallColors.Count >= 2, "A neighborhood must contain visibly different house palettes.");
        if (capturePath is not null && DisplayServer.GetName() != "headless")
        {
            await RenderedFrame(host);
            using var image = view.Texture.GetImage();
            Require(image.SavePng(Path.ChangeExtension(capturePath, ".houses.png")) == Error.Ok,
                "House variation screenshot must save successfully.");
        }
        foreach (var anchor in houses)
        {
            map.SetBuilding(anchor.X, anchor.Y, null);
            foreach (var cell in cottage.FootprintAt(anchor.X, anchor.Y).Cells()) map.SetZone(cell.X, cell.Y, ZoneType.None);
        }
        GD.Print("TERMCITY_HOUSE_VARIATION_OK");
        foreach (var entry in original)
        {
            map.SetTerrain(entry.Position.X, entry.Position.Y, entry.Terrain);
            map.SetFeature(entry.Position.X, entry.Position.Y, entry.Feature);
            map.SetSeaWater(entry.Position.X, entry.Position.Y, entry.Sea);
            map.SetRiverWater(entry.Position.X, entry.Position.Y, entry.River);
        }
        session.Game.Touch();
        GD.Print("TERMCITY_ZONE_RENDER_OK");
    }

    private static async Task VerifyBridgeRendering(Main host, string? capturePath)
    {
        var session = host.Session;
        var map = session.Game.Map;
        var view = host.Map.Perspective!;
        int x = map.Width / 2 + 29, y = map.Height / 2 + 8;
        var fixture = new CellRect(x - 6, y - 4, 13, 9);
        var original = fixture.Cells().Select(p => (Position: p, Terrain: map.TerrainAt(p.X, p.Y),
            Road: map.RoadTypeAt(p.X, p.Y), Sea: map.IsSea(p.X, p.Y), River: map.IsRiver(p.X, p.Y))).ToArray();
        foreach (bool water in new[] { false, true })
        {
            foreach (var p in fixture.Cells())
            {
                map.SetRoad(p.X, p.Y, false);
                map.SetTerrain(p.X, p.Y, map.Content.Terrains.Get(
                    p.X < x - (water ? 2 : 1) || p.X > x + 2 ? "Hill" : water ? "Water" : "Meadow"));
                if (!map.TerrainAt(p.X, p.Y).Buildable)
                {
                    map.SetSeaWater(p.X, p.Y, false);
                    map.SetRiverWater(p.X, p.Y, true);
                }
            }
            var bridgeRoad = map.Content.Roads.Get(TermCity.Core.Roads.DefaultRoads.KingsRoadName);
            if (water)
            {
                var stroke = CellLines.Between(new(fixture.X, y - 2), new(fixture.Right, y + 2));
                var crossing = TermCity.Core.Persistence.SaveGameStore.Deserialize(
                    TermCity.Core.Persistence.SaveGameStore.Serialize(session.Game));
                var built = crossing.BuildRoad(stroke, crossing.DefaultRoad);
                Require(built.Success && built.Cells == stroke.Count,
                    $"A player-built angled river crossing must include every ordered stroke cell: {built.Message}.");
                foreach (var p in stroke)
                {
                    Require(crossing.Map.HasRoad(p.X, p.Y), $"The player crossing lost {p}.");
                    map.SetRoad(p.X, p.Y, map.Content.Roads.Default);
                }
            }
            else
                for (int cx = fixture.X; cx <= fixture.Right; cx++) map.SetRoad(cx, y, bridgeRoad);
            session.Game.Touch();
            session.SetZoom(1);
            host.Map.RefreshCells();
            session.CenterOn(new(x, y));
            view.Sync(session, host.Map.Size);
            Require(view.BridgeSegmentsDrawn > 0, "Water crossings and short steep valleys must have structural bridge models.");
            Require(view.BridgeSurfaceAt(new(x + 0.5f, y + 0.5f)),
                "The tested crossing itself must be a bridge, not an unrelated bridge elsewhere in the viewport.");
            var bridgeVectors = new RoadVectorLayer();
            bridgeVectors.Sync(session.Game);
            var bridgeLayout = new RoadMeshLayout(map, bridgeVectors.Paths);
            var bridgeMeshes = new RoadSurfaceMeshes(bridgeLayout);
            int solidEdges = 0;
            var sidePixels = new List<Vector3>();
            for (int cy = fixture.Y / TerrainSprite.ChunkCells; cy <= fixture.Bottom / TerrainSprite.ChunkCells; cy++)
                for (int cx = fixture.X / TerrainSprite.ChunkCells; cx <= fixture.Right / TerrainSprite.ChunkCells; cx++)
                {
                    var chunk = bridgeMeshes.Build(session, cx, cy);
                    using var groundMesh = chunk.Ground;
                    using var roadMesh = chunk.Roads;
                    using var bankMesh = chunk.Banks;
                    foreach (var sample in chunk.BridgeSideSamples)
                    {
                        if (sample.Top.X < fixture.X || sample.Top.X > fixture.Right + 1 ||
                            sample.Top.Z < fixture.Y || sample.Top.Z > fixture.Bottom + 1) continue;
                        Require(sample.Top.X == sample.Bottom.X && sample.Top.Z == sample.Bottom.Z &&
                            Math.Abs(sample.Top.Y - sample.Bottom.Y - 0.12f) < 0.001f &&
                            Math.Abs(sample.Top.Y - bridgeLayout.HeightAt(new(sample.Top.X, sample.Top.Z))) < 0.001f,
                            "Bridge decks must have solid vertical edge faces meeting their actual top surface.");
                        Require(chunk.Faces.Values.Any(faces => faces.Contains(sample.Top) && faces.Contains(sample.Bottom)),
                            "Bridge side faces must be retained for picking, not just rendered.");
                        solidEdges++;
                        sidePixels.Add(sample.Top.Lerp(sample.Bottom, 0.5f));
                    }
                }
            Require(solidEdges > 0, "The tested bridge must contain actual deck side faces, not only a thin surface.");
            var vertices = view.SurfaceVertices(new(x, y));
            float ground = vertices.Min(p => p.Y), deck = view.RenderedGroundHeight(map, x + 0.5f, y + 0.5f);
            Require(deck - ground > 0.1f, "A bridge must retain visible water or valley terrain underneath its deck.");
            if (water)
            {
                var waterVertices = vertices.Where(p => p.Y < -0.03f).ToArray();
                Require(waterVertices.Length > 0 && waterVertices.Max(p => p.Y) - waterVertices.Min(p => p.Y) < 0.002f,
                    "Water below the bridge must have a gentle continuous surface, not hill terraces.");
            }
            var pointer = view.ScreenPoint(new(x + 0.5f, deck, y + 0.5f));
            Require(host.Map.TryMapPosition(pointer, out var picked) && picked == new Pos(x, y),
                "Bridge deck picking must select the visible road, not the terrain beneath it.");
            for (int turn = 0; turn < 4; turn++)
            {
                view.Sync(session, host.Map.Size);
                int railsPicked = 0;
                foreach (var rail in view.BridgeRails)
                {
                    if (rail.Transform.Basis.Z.Length() < 0.4f) continue;
                    var point = rail.Transform * new Vector3(0, 0.5f, 0);
                    if (Math.Abs(point.X - x - 0.5f) > 1.5f || Math.Abs(point.Z - y - 0.5f) > 1.5f) continue;
                    var railPointer = view.ScreenPoint(point);
                    if (!new Rect2(Vector2.Zero, host.Map.Size).HasPoint(railPointer)) continue;
                    Require(view.GroundPoint(railPointer).DistanceTo(point) < 0.08f,
                        $"Picking must follow the actual rotated bridge railing, not the deck or water, after turn {turn}.");
                    Require(host.Map.TryMapPosition(railPointer, out var railCell) && map.HasRoad(railCell.X, railCell.Y),
                        "Clicking a bridge railing must select its logical road, not adjacent water or ground.");
                    railsPicked++;
                }
                Require(railsPicked > 0, "Each bridge rotation must exercise visible railing clicks.");
                if (water && capturePath is not null && DisplayServer.GetName() != "headless")
                {
                    await RenderedFrame(host);
                    using var bankImage = view.Texture.GetImage();
                    Require(bankImage.SavePng(Path.ChangeExtension(capturePath, $".bridge-bank-{turn}.png")) == Error.Ok,
                        "Bridge-bank diagnostic screenshot must save.");
                    for (float pz = y - 2; pz <= y + 3; pz += 0.025f)
                        for (float px = x - 3; px <= x + 4; px += 0.025f)
                        {
                            if (TerrainSprite.WaterSurface(map, px, pz) < ShorelineLayout.WaterThreshold) continue;
                            var waterPointer = view.ScreenPoint(new(px,
                                TerrainSprite.WaterCornerLevel(map, (int)px, (int)pz), pz));
                            int ix = (int)waterPointer.X, iy = (int)waterPointer.Y;
                            if (ix < 1 || iy < 1 || ix >= bankImage.GetWidth() - 1 || iy >= bankImage.GetHeight() - 1) continue;
                            var pixel = bankImage.GetPixel(ix, iy);
                            Require(pixel.G > 0.15f || pixel.B > 0.15f,
                                $"Water below bridge approaches must not expose black gaps at {px},{pz}, heading {turn}: {pixel}.");
                        }
                }
                view.Rotate(90);
            }
            view.Sync(session, host.Map.Size);
            for (int frame = 0; frame < 5; frame++)
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            if (capturePath is not null && DisplayServer.GetName() != "headless")
            {
                await RenderedFrame(host);
                using var image = view.Texture.GetImage();
                foreach (var point in sidePixels)
                {
                    var screen = view.ScreenPoint(point);
                    int ix = (int)screen.X, iy = (int)screen.Y;
                    if (ix < 0 || iy < 0 || ix >= image.GetWidth() || iy >= image.GetHeight()) continue;
                    var pixel = image.GetPixel(ix, iy);
                    Require(pixel.G > 0.15f || pixel.B > 0.15f,
                        $"Bridge deck/edge joins must not expose black lines at {point}, pixel {ix},{iy}: {pixel}.");
                }
                var error = image.SavePng(Path.ChangeExtension(capturePath, water ? ".bridge.png" : ".valley-bridge.png"));
                if (error != Error.Ok) throw new IOException($"Bridge screenshot failed: {error}.");
                var waterPoint = view.ScreenPoint(new(x + 0.5f, TerrainSprite.WaterCornerLevel(map, x, y + 2), y + 2.5f));
                if (water && new Rect2(Vector2.Zero, host.Map.Size).HasPoint(waterPoint))
                {
                    var pixel = image.GetPixel((int)waterPoint.X, (int)waterPoint.Y);
                    Require(pixel.B > pixel.R * 1.3f,
                        $"Bridge capture must show the current water surface, not a stale terrain frame: " +
                        $"pixel {pixel} at {waterPoint}, visible ground {view.GroundPoint(waterPoint)}.");
                }
            }
        }
        foreach (var entry in original)
        {
            map.SetTerrain(entry.Position.X, entry.Position.Y, entry.Terrain);
            map.SetSeaWater(entry.Position.X, entry.Position.Y, entry.Sea);
            map.SetRiverWater(entry.Position.X, entry.Position.Y, entry.River);
            map.SetRoad(entry.Position.X, entry.Position.Y, false);
            if (entry.Road is { } road) map.SetRoad(entry.Position.X, entry.Position.Y, road);
        }
        session.Game.Touch();
        GD.Print("TERMCITY_BRIDGE_RENDER_OK");
    }

    private static async Task VerifyTrafficCirclePlacement(Main host, string? capturePath)
    {
        var session = host.Session;
        var map = session.Game.Map;
        var view = host.Map.Perspective!;
        var template = TermCity.Core.Roads.RoadTemplate.TrafficCircle;
        var desired = new Pos(map.Width / 2 + 28, map.Height / 2 - 28);
        var origin = Enumerable.Range(0, map.Width * map.Height).Select(map.PosOf)
            .Where(p => session.Game.RoadTemplatePlacementError(template, p) is null &&
                new CellRect(p.X - 1, p.Y - 1, 7, 7).Cells().All(c => map.InBounds(c) && !map.HasRoad(c.X, c.Y)))
            .OrderBy(p => Math.Abs(p.X - desired.X) + Math.Abs(p.Y - desired.Y)).First();
        session.SetZoom(1);
        host.Map.RefreshCells();
        session.CenterOn(origin.Offset(2, 2));
        session.PlaceCursor(origin);
        session.PreviewRoadTemplate(template);
        host.Map.RefreshCells();
        view.Sync(session, host.Map.Size);
        Require(session.FixedPlacementToolActive && view.IndicatorObjectCount > 0,
            "Traffic-circle placement must expose a live footprint/model preview, not a blocking confirmation dialog.");
        var point = new Vector3(origin.X + 0.5f, view.RenderedGroundHeight(map, origin.X + 0.5f, origin.Y + 0.5f),
            origin.Y + 0.5f);
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        { ButtonIndex = MouseButton.Left, Pressed = true, Position = view.ScreenPoint(point) });
        Require(session.Preview is null && map.HasRoad(origin.X, origin.Y), "Clicking must place the entire traffic circle.");
        host.Map.RefreshCells();
        view.Sync(session, host.Map.Size);
        Require(!view.RoadSurfaceAt(new(origin.X + 2.5f, origin.Y + 2.5f)),
            "The traffic circle's grassy center must remain visibly open.");
        for (int cy = origin.Y; cy < origin.Y + template.Height; cy++)
            for (int cx = origin.X; cx < origin.X + template.Width; cx++)
            {
                var visible = session.Game.Network.RoadAtCellCenter(cx, cy);
                Require((visible is not null) == view.RoadSurfaceAt(new(cx + 0.5f, cy + 0.5f)),
                    "Selected-cell road reporting must match the traffic circle's actual mesh footprint.");
                if (visible is null)
                    Require(!CellInspector.Summary(session.Game, new(cx, cy)).Contains("Road", StringComparison.Ordinal),
                        "Bare selections must not report a nearby logical route.");
            }
        for (int frame = 0; frame < 3; frame++)
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        if (capturePath is not null && DisplayServer.GetName() != "headless")
        {
            await RenderedFrame(host);
            using var image = view.Texture.GetImage();
            var error = image.SavePng(Path.ChangeExtension(capturePath, ".circle.png"));
            if (error != Error.Ok) throw new IOException($"Traffic-circle screenshot failed: {error}.");
        }
        session.SetZoom(0);
        host.Map.RefreshCells();
        GD.Print("TERMCITY_TRAFFIC_CIRCLE_OK");
    }

    private static async Task VerifyHillRoadRamps(Main host, string? capturePath)
    {
        var session = host.Session;
        var map = session.Game.Map;
        var view = host.Map.Perspective!;
        int x = map.Width / 2 + 28, y = map.Height / 2 + 25;
        var fixture = new CellRect(x - 11, y - 11, 23, 23);
        var original = fixture.Cells().Select(p => (Position: p, Terrain: map.TerrainAt(p.X, p.Y),
            Road: map.RoadTypeAt(p.X, p.Y), Sea: map.IsSea(p.X, p.Y), River: map.IsRiver(p.X, p.Y))).ToArray();
        foreach (var p in fixture.Cells())
        {
            map.SetRoad(p.X, p.Y, false);
            map.SetTerrain(p.X, p.Y, map.Content.Terrains.Get(
                p.X > fixture.X && p.X < fixture.Right && p.Y > fixture.Y && p.Y < fixture.Bottom ? "Hill" : "Meadow"));
        }
        foreach (var p in fixture.Cells().Where(p => p.Y == y))
            map.SetRoad(p.X, p.Y, map.Content.Roads.Get(TermCity.Core.Roads.DefaultRoads.KingsRoadName));
        session.Game.Touch();
        session.SetZoom(1);
        host.Map.RefreshCells();
        session.CenterOn(new(x, y));
        view.Sync(session, host.Map.Size);
        Require(view.RoadMeshChunksDrawn > 0, "Roads must be drawn as dedicated mesh surfaces, not terrain paint.");
        for (float px = x - 2; px <= x + 2; px += 0.25f)
            foreach (float pz in new[] { y + 0.5f, y + 0.5f - RoadMeshLayout.HalfWidth - RoadMeshLayout.ShoulderWidth,
                y + 0.5f + RoadMeshLayout.HalfWidth + RoadMeshLayout.ShoulderWidth })
                Require(Math.Abs(view.RenderedGroundHeight(map, px, pz) -
                    TerrainSprite.Elevation(map, (int)px, y)) < 0.001f,
                    "The summit carriageway and both outer shoulders must meet level hilltop ground exactly.");
        Require(view.CliffEdges.All(edge =>
        {
            var midpoint = (edge.A + edge.B) / 2;
            return midpoint.X < x - 2 || midpoint.X > x + 2 || Math.Abs(midpoint.Y - y - 0.5f) > 1;
        }), "A level hilltop must not have thin cutting-wall lines beside the road.");
        Require(view.RenderedGroundHeight(map, fixture.X + 0.5f, y + 0.5f) >
            TerrainSprite.Elevation(map, fixture.X, y),
            "The incline must build a supported approach above low ground at the foot of the terraces.");
        for (int cx = fixture.X + 1; cx <= fixture.Right; cx++)
        {
            if (view.RoadSurfaceAt(new(cx, y + 0.5f)))
                Require(Math.Abs(view.RenderedGroundHeight(map, cx - 0.0001f, y + 0.5f) -
                    view.RenderedGroundHeight(map, cx + 0.0001f, y + 0.5f)) < 0.001f,
                    "Dedicated road meshes must share continuous heights across cell and chunk boundaries.");
            foreach (int cy in new[] { y - 1, y, y + 1 })
            {
                float px = cx + 0.5f, pz = cy + 0.5f;
                float height = view.RenderedGroundHeight(map, px, pz);
                var pointer = view.ScreenPoint(new(px, height, pz));
                if (!new Rect2(Vector2.Zero, host.Map.Size).HasPoint(pointer)) continue;
                Require(host.Map.TryMapPosition(pointer, out var picked) && picked == new Pos(cx, cy),
                    $"Picking must match the rendered road-cutting triangle: expected {cx},{cy} at {height}, " +
                    $"got {picked}, pointer {pointer}, ground {view.GroundPoint(pointer)}.");
                Require(Math.Abs(view.GroundPoint(pointer).Y - height) < 0.001f,
                    "Ground picking must intersect the exact rendered ramp height.");
                if (cy != y && !view.GradedPanelAt(map, px, pz) &&
                    TerrainSprite.WaterSurface(map, cx, cy) == 0 &&
                    TerrainSprite.WaterSurface(map, cx + 1, cy) == 0 &&
                    TerrainSprite.WaterSurface(map, cx, cy + 1) == 0 &&
                    TerrainSprite.WaterSurface(map, cx + 1, cy + 1) == 0)
                    Require(Math.Abs(height - TerrainSprite.Elevation(map, cx, cy)) < 0.001f,
                        "A steep cutting must restore the terrace within a quarter-cell of the road boundary.");
            }
        }
        foreach (var edge in view.CliffEdges)
            Require(!view.RoadSurfaceAt((edge.A + edge.B) / 2),
                $"No exposed cutting wall may intersect the road mesh: {edge.A} to {edge.B}.");
        Require(view.ExposedCutTriangles > 0,
            "Steep road-cut banks must expose shaded earth instead of stretched grass.");
        Require(view.ExposedCutCenters.Any(p => p.Z < y + 0.45f) && view.ExposedCutCenters.Any(p => p.Z > y + 0.55f),
            "A hill road must expose tight cut faces on both sides of the carriageway.");
        var inclineVectors = new RoadVectorLayer();
        inclineVectors.Sync(session.Game);
        var inclineLayout = new RoadMeshLayout(map, inclineVectors.Paths);
        var inclineMeshes = new RoadSurfaceMeshes(inclineLayout);
        for (int chunkY = fixture.Y / TerrainSprite.ChunkCells; chunkY <= fixture.Bottom / TerrainSprite.ChunkCells; chunkY++)
            for (int chunkX = fixture.X / TerrainSprite.ChunkCells; chunkX <= fixture.Right / TerrainSprite.ChunkCells; chunkX++)
            {
                var chunk = inclineMeshes.Build(session, chunkX, chunkY);
                using var ground = chunk.Ground;
                using var roads = chunk.Roads;
                using var banks = chunk.Banks;
                using var groundArrays = ground.SurfaceGetArrays(0);
                var groundVertices = groundArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                for (int triangle = 0; triangle < groundVertices.Length; triangle += 3)
                {
                    var center = (groundVertices[triangle] + groundVertices[triangle + 1] + groundVertices[triangle + 2]) / 3;
                    int cellX = (int)Math.Floor(center.X), cellY = (int)Math.Floor(center.Z);
                    if (!fixture.Contains(new(cellX, cellY)) || !map.TerrainAt(cellX, cellY).Buildable ||
                        !inclineLayout.AtCell(cellX, cellY).Any(segment => segment.HeightA != segment.HeightB)) continue;
                    Require(!inclineLayout.InGroundCorridor(new(center.X, center.Z)),
                        $"Incline shoulders must use earth rather than grass, on both sides of the road at {center}.");
                }
                if (banks is null) continue;
                using var arrays = banks.SurfaceGetArrays(0);
                var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                for (int triangle = 0; triangle < vertices.Length; triangle += 3)
                {
                    var midpoint = (vertices[triangle] + vertices[triangle + 1] + vertices[triangle + 2]) / 3;
                    if (midpoint.X < fixture.X || midpoint.X > fixture.Right + 1 ||
                        midpoint.Z < fixture.Y || midpoint.Z > fixture.Bottom + 1) continue;
                    var delta = vertices[triangle + 1] - vertices[triangle];
                    if (new Vector2(delta.X, delta.Z).LengthSquared() < 0.000001f)
                        delta = vertices[triangle + 2] - vertices[triangle];
                    var normal = new Vector2(-delta.Z, delta.X).Normalized() * 0.002f;
                    if (normal.LengthSquared() == 0) continue;
                    var point = new Vector2(midpoint.X, midpoint.Z);
                    Require(!inclineLayout.InGroundCorridor(new(point.X + normal.X, point.Y + normal.Y)) ||
                        !inclineLayout.InGroundCorridor(new(point.X - normal.X, point.Y - normal.Y)),
                        $"Terrace faces must not jut through the continuous tan road shoulder at {midpoint}.");
                }
            }
        foreach (var cell in fixture.Cells())
            if (TerrainSprite.Elevation(map, cell.X, cell.Y) > 0 &&
                TerrainSprite.WaterSurface(map, cell.X, cell.Y) == 0 &&
                TerrainSprite.WaterSurface(map, cell.X + 1, cell.Y) == 0 &&
                TerrainSprite.WaterSurface(map, cell.X, cell.Y + 1) == 0 &&
                TerrainSprite.WaterSurface(map, cell.X + 1, cell.Y + 1) == 0)
            {
                for (int row = 0; row < 4; row++)
                    for (int column = 0; column < 4; column++)
                    {
                        float px = cell.X + (column + 0.5f) / 4, pz = cell.Y + (row + 0.5f) / 4;
                        if (view.GradedPanelAt(map, px, pz)) continue;
                        Require(Math.Abs(view.RenderedGroundHeight(map, px, pz) -
                            TerrainSprite.Elevation(map, cell.X, cell.Y)) < 0.001f,
                            "Hilltop panels outside the road cutting must remain level, not blend into the incline.");
                    }
            }
        for (int frame = 0; frame < 3; frame++)
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        if (capturePath is not null && DisplayServer.GetName() != "headless")
        {
            await RenderedFrame(host);
            using var image = view.Texture.GetImage();
            foreach (var sample in view.WallSamples)
            {
                var projected = view.ScreenPoint(sample);
                int ix = (int)projected.X, iy = (int)projected.Y;
                if (ix < 1 || iy < 1 || ix >= image.GetWidth() - 1 || iy >= image.GetHeight() - 1) continue;
                var pixel = image.GetPixel(ix, iy);
                Require(pixel.G > 0.15f || pixel.B > 0.15f,
                    $"Terrace/cutting wall must not expose black wedges at {sample}, pixel {ix},{iy}.");
            }
            for (float pz = y - 0.5f; pz <= y + 1.5f; pz += 0.05f)
                for (float px = fixture.X + 1; px < fixture.Right - 1; px += 0.05f)
                {
                    var pointer = view.ScreenPoint(new(px, view.RenderedGroundHeight(map, px, pz), pz));
                    int ix = (int)pointer.X, iy = (int)pointer.Y;
                    if (ix < 0 || iy < 0 || ix >= image.GetWidth() || iy >= image.GetHeight()) continue;
                    var pixel = image.GetPixel(ix, iy);
                    Require(pixel.G > 0.15f || pixel.B > 0.15f,
                        $"Road-cut surface must not expose black background at {px},{pz}, pixel {ix},{iy}.");
                    if (view.RoadSurfaceAt(new(px, pz)))
                        Require(pixel.G <= pixel.R * 1.2f || pixel.G <= pixel.B * 1.1f,
                            $"Grass must not protrude through the paved hill road at {px},{pz}, pixel {ix},{iy}: {pixel}.");
                }
            var error = image.SavePng(Path.ChangeExtension(capturePath, ".hills.png"));
            if (error != Error.Ok) throw new IOException($"Hill-road screenshot failed: {error}.");
            session.SetZoom(0);
            host.Map.Invalidate(true);
            host.Map.RefreshCells();
            session.CenterOn(new(x - 5, y));
            view.Sync(session, host.Map.Size);
            for (int frame = 0; frame < 3; frame++)
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await RenderedFrame(host);
            using var approach = view.Texture.GetImage();
            Require(view.ScreenPoint(new(x - 4.5f, 0, y + 0.5f)).DistanceTo(host.Map.Size / 2) < host.Map.Grid.PixelHeight * 2,
                "The incline capture must remain centered on the tested hill, not recenter on the old cursor after zooming.");
            for (float pz = y - 0.1f; pz <= y + 1.1f; pz += 0.1f)
                for (float px = fixture.X + 1; px <= x; px += 0.1f)
                {
                    var pointer = view.ScreenPoint(new(px, view.RenderedGroundHeight(map, px, pz), pz));
                    int ix = (int)pointer.X, iy = (int)pointer.Y;
                    if (ix < 0 || iy < 0 || ix >= approach.GetWidth() || iy >= approach.GetHeight()) continue;
                    var pixel = approach.GetPixel(ix, iy);
                    Require(pixel.G > 0.15f || pixel.B > 0.15f,
                        $"The supported incline must not expose black gaps beside its edges at {px},{pz}, pixel {ix},{iy}.");
                    if (view.RoadSurfaceAt(new(px, pz)))
                        Require(pixel.G <= pixel.R * 1.2f || pixel.G <= pixel.B * 1.1f,
                            $"Grass must not protrude through the supported incline at {px},{pz}, pixel {ix},{iy}: {pixel}.");
                }
            error = approach.SavePng(Path.ChangeExtension(capturePath, ".hill-approach.png"));
            if (error != Error.Ok) throw new IOException($"Hill-approach screenshot failed: {error}.");
            session.SetZoom(1);
            host.Map.Invalidate(true);
            host.Map.RefreshCells();
            session.CenterOn(new(x, y));
        }
        for (int cy = fixture.Y; cy <= fixture.Bottom; cy++)
            map.SetRoad(x, cy, map.Content.Roads.Default);
        for (int cy = y - 3; cy <= y; cy++)
        {
            map.SetRoad(fixture.X + 3, cy, map.Content.Roads.Default);
            map.SetRoad(fixture.X + 6, cy, map.Content.Roads.Default);
        }
        for (int cx = fixture.X + 3; cx <= fixture.X + 6; cx++)
            map.SetRoad(cx, y - 3, map.Content.Roads.Default);
        session.Game.Touch();
        view.Sync(session, host.Map.Size);
        Require(view.RoadMeshChunksDrawn > 0 && view.RoadSurfaceAt(new(x + 0.5f, y + 0.5f)),
            "Crossing road centerlines must form one fully covered junction.");
        var roadVectors = new RoadVectorLayer();
        roadVectors.Sync(session.Game);
        var roadLayout = new RoadMeshLayout(map, roadVectors.Paths);
        var roadMeshes = new RoadSurfaceMeshes(roadLayout);
        for (int chunkY = fixture.Y / TerrainSprite.ChunkCells; chunkY <= fixture.Bottom / TerrainSprite.ChunkCells; chunkY++)
            for (int chunkX = fixture.X / TerrainSprite.ChunkCells; chunkX <= fixture.Right / TerrainSprite.ChunkCells; chunkX++)
            {
                var chunk = roadMeshes.Build(session, chunkX, chunkY);
                using var groundMesh = chunk.Ground;
                using var pavingMesh = chunk.Roads;
                using var bankMesh = chunk.Banks;
                var vertices = chunk.Ground.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                for (int i = 0; i < vertices.Length; i += 3)
                {
                    var center = (vertices[i] + vertices[i + 1] + vertices[i + 2]) / 3;
                    int cx = (int)Math.Floor(center.X), cy = (int)Math.Floor(center.Z);
                    if (!map.InBounds(cx, cy) || TerrainSprite.Elevation(map, cx, cy) <= 0) continue;
                    var point = new System.Numerics.Vector2(center.X, center.Z);
                    bool insideClearing = roadLayout.InGroundCorridor(point) || roadMeshes.IsRemnant(map, new(center.X, center.Z));
                    bool interior = insideClearing && new[] { new Vector2(0.001f, 0), new Vector2(-0.001f, 0),
                        new Vector2(0, 0.001f), new Vector2(0, -0.001f) }.All(offset =>
                            roadLayout.InGroundCorridor(point + new System.Numerics.Vector2(offset.X, offset.Y)) ||
                            roadMeshes.IsRemnant(map, new(center.X + offset.X, center.Z + offset.Y)));
                    Require(!interior,
                        $"Grass-textured triangles must not cover cleared hill-road cells at {center}.");
                }
            }
        Require(roadMeshes.RoundedJoinCount > 0,
            "The hill-road fixture must generate curved terrace joins, not only test the standalone curve helper.");
        Require(view.CliffEdges.All(edge => !view.RoadSurfaceAt((edge.A + edge.B) / 2)),
            "A hillside junction must not contain intersecting bank walls.");
        for (int frame = 0; frame < 3; frame++)
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        if (capturePath is not null && DisplayServer.GetName() != "headless")
        {
            session.SetZoom(0);
            host.Map.Invalidate(true);
            host.Map.RefreshCells();
            session.CenterOn(new(x - 5, y));
            view.Sync(session, host.Map.Size);
            for (int frame = 0; frame < 3; frame++)
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await RenderedFrame(host);
            using var junction = view.Texture.GetImage();
            var error = junction.SavePng(Path.ChangeExtension(capturePath, ".junction.png"));
            if (error != Error.Ok) throw new IOException($"Junction screenshot failed: {error}.");
            for (float pz = y - 4; pz <= y + 1; pz += 0.125f)
                for (float px = fixture.X + 2; px <= x + 1; px += 0.125f)
                {
                    var point = new Vector2(px, pz);
                    if (TerrainSprite.Elevation(map, (int)px, (int)pz) <= 0 ||
                        !view.GradedPanelAt(map, px + 0.06f, pz) ||
                        !view.GradedPanelAt(map, px - 0.06f, pz) ||
                        !view.GradedPanelAt(map, px, pz + 0.06f) ||
                        !view.GradedPanelAt(map, px, pz - 0.06f)) continue;
                    var ground = new Vector3(px, view.RenderedGroundHeight(map, px, pz), pz);
                    var pointer = view.ScreenPoint(ground);
                    int ix = (int)pointer.X, iy = (int)pointer.Y;
                    if (ix < 0 || iy < 0 || ix >= junction.GetWidth() || iy >= junction.GetHeight() ||
                        view.GroundPoint(pointer).DistanceTo(ground) > 0.02f) continue;
                    var pixel = junction.GetPixel(ix, iy);
                    Require(pixel.G <= pixel.R * 1.2f || pixel.G <= pixel.B * 1.1f,
                        $"Hill-road carriageways, shoulders and cleared remnants must not render grass at {point}: {pixel}.");
                }
        }
        foreach (var entry in original)
        {
            map.SetTerrain(entry.Position.X, entry.Position.Y, entry.Terrain);
            map.SetSeaWater(entry.Position.X, entry.Position.Y, entry.Sea);
            map.SetRiverWater(entry.Position.X, entry.Position.Y, entry.River);
            map.SetRoad(entry.Position.X, entry.Position.Y, false);
            if (entry.Road is { } road) map.SetRoad(entry.Position.X, entry.Position.Y, road);
        }
        session.Game.Touch();
        session.SetZoom(0);
        host.Map.RefreshCells();
        GD.Print("TERMCITY_HILL_ROAD_RAMPS_OK");
    }

    private static async Task VerifyDiagonalHillCuts(Main host, string? capturePath)
    {
        var session = host.Session;
        var map = session.Game.Map;
        var view = host.Map.Perspective!;
        int x = map.Width / 2 + 27, y = map.Height / 2 + 24;
        var fixture = new CellRect(x - 9, y - 9, 19, 19);
        var original = fixture.Cells().Select(p => (Position: p, Terrain: map.TerrainAt(p.X, p.Y),
            Sea: map.IsSea(p.X, p.Y), River: map.IsRiver(p.X, p.Y), Road: map.RoadTypeAt(p.X, p.Y), Feature: map.FeatureAt(p.X, p.Y))).ToArray();
        foreach (var p in fixture.Cells())
        {
            map.SetRoad(p.X, p.Y, false);
            map.SetFeature(p.X, p.Y, null);
            map.SetTerrain(p.X, p.Y, map.Content.Terrains.Get(
                p.X >= x - 5 && p.Y >= y - 5 ? "Hill" : "Meadow"));
        }
        foreach (var p in CellLines.Between(new(x - 8, y - 7), new(x + 8, y + 7)))
            map.SetRoad(p.X, p.Y, map.Content.Roads.Get(TermCity.Core.Roads.DefaultRoads.KingsRoadName));
        session.Game.Touch();
        session.SetZoom(1);
        host.Map.RefreshCells();
        session.CenterOn(new(x, y));
        view.Sync(session, host.Map.Size);
        var cutVectors = new RoadVectorLayer();
        cutVectors.Sync(session.Game);
        var cutMeshes = new RoadSurfaceMeshes(new RoadMeshLayout(map, cutVectors.Paths));
        for (int cy = fixture.Y / TerrainSprite.ChunkCells; cy <= fixture.Bottom / TerrainSprite.ChunkCells; cy++)
            for (int cx = fixture.X / TerrainSprite.ChunkCells; cx <= fixture.Right / TerrainSprite.ChunkCells; cx++)
            {
                var chunk = cutMeshes.Build(session, cx, cy);
                using var ground = chunk.Ground;
                using var roads = chunk.Roads;
                using var banks = chunk.Banks;
            }
        Require(cutMeshes.NarrowCutFacesClosed > 0,
            "The diagonal hill fixture must close narrow cut joins that fixed-width side probes miss.");
        for (int frame = 0; frame < 3; frame++)
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        if (capturePath is not null && DisplayServer.GetName() != "headless")
        {
            await RenderedFrame(host);
            using var image = view.Texture.GetImage();
            var error = image.SavePng(Path.ChangeExtension(capturePath, ".diagonal-hill-cuts.png"));
            if (error != Error.Ok) throw new IOException($"Diagonal hill-cut screenshot failed: {error}.");
            foreach (var sample in view.WallSamples)
            {
                if (Math.Abs(sample.X - x) > 7 || Math.Abs(sample.Z - y) > 7) continue;
                var pointer = view.ScreenPoint(sample);
                int ix = (int)pointer.X, iy = (int)pointer.Y;
                if (ix < 0 || iy < 0 || ix >= image.GetWidth() || iy >= image.GetHeight()) continue;
                var pixel = image.GetPixel(ix, iy);
                Require(pixel.G > 0.15f || pixel.B > 0.15f,
                    $"Diagonal hill-cut joins must not leave fine black lines at {sample}, pixel {ix},{iy}: {pixel}.");
            }
        }
        foreach (var entry in original)
        {
            map.SetTerrain(entry.Position.X, entry.Position.Y, entry.Terrain);
            map.SetSeaWater(entry.Position.X, entry.Position.Y, entry.Sea);
            map.SetRiverWater(entry.Position.X, entry.Position.Y, entry.River);
            map.SetRoad(entry.Position.X, entry.Position.Y, false);
            if (entry.Road is { } road) map.SetRoad(entry.Position.X, entry.Position.Y, road);
            map.SetFeature(entry.Position.X, entry.Position.Y, entry.Feature);
        }
        session.Game.Touch();
        session.SetZoom(0);
        host.Map.RefreshCells();
        GD.Print("TERMCITY_DIAGONAL_HILL_CUTS_OK");
    }

    private static void VerifySmoothPerspectivePanning(Main host)
    {
        var session = host.Session;
        var view = host.Map.Perspective!;
        var center = new Pos(session.Game.Map.Width / 2, session.Game.Map.Height / 2);
        foreach (int zoom in new[] { -2, -1, 0, 1 })
        {
            session.SetZoom(zoom);
            host.Map.RefreshCells();
            for (int tilt = 0; tilt < 3; tilt++)
            {
                for (int rotation = 0; rotation < 2; rotation++)
                {
                    foreach (var direction in new[] { Vector2.Right, Vector2.Down, new Vector2(0.6f, 0.8f) })
                    {
                        session.CenterOn(center);
                        view.Sync(session, host.Map.Size);
                        var start = host.Map.Size / 2;
                        var anchor = view.GroundPlanePoint(start);
                        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
                        { ButtonIndex = MouseButton.Middle, Pressed = true, Position = start });
                        int builds = view.GeometryBuilds;
                        for (int step = 1; step <= 48; step++)
                        {
                            var pointer = start + direction * (step * 0.25f);
                            host.Map.EmitSignal(Control.SignalName.GuiInput,
                                new InputEventMouseMotion { Position = pointer });
                            Require(view.ScreenPoint(anchor).DistanceTo(pointer) < 0.1f,
                                $"Slow dragging must follow the pointer within 0.1 pixels at zoom {zoom}, " +
                                $"tilt {view.Elevation}, rotation {view.Azimuth}, step {step}.");
                            host.Map.EmitSignal(Control.SignalName.GuiInput,
                                new InputEventMouseMotion { Position = pointer });
                            Require(view.ScreenPoint(anchor).DistanceTo(pointer) < 0.1f,
                                "Repeating a stationary drag event must not cause camera drift.");
                        }
                        Require(view.GeometryBuilds <= builds + 1,
                            "Subcell panning must retain buffered geometry instead of rebuilding each event.");
                        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
                        { ButtonIndex = MouseButton.Middle, Pressed = false, Position = start });
                    }
                    view.Rotate(90);
                }
                view.Rotate(-180);
                view.Tilt();
            }
        }
        session.SetZoom(0);
        host.Map.RefreshCells();
        session.CenterOn(center);
        view.Sync(session, host.Map.Size);
        var trackpadAnchor = view.GroundPlanePoint(host.Map.Size / 2);
        for (int step = 1; step <= 32; step++)
        {
            host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventPanGesture
            { Position = host.Map.Size / 2, Delta = new(0, 0.01f) });
            var actual = view.ScreenPoint(trackpadAnchor);
            Require(Math.Abs(actual.X - host.Map.Size.X / 2) < 0.1f &&
                actual.Y < host.Map.Size.Y / 2, "Small trackpad scrolls must move in screen space without zigzag.");
        }
        session.CenterOn(center);
        view.Sync(session, host.Map.Size);
        Require(view.CameraOffset == Vector2.Zero, "Recentering must clear fractional pan remainders.");
        GD.Print("TERMCITY_SMOOTH_PANNING_OK");
    }

    private static async Task VerifyTerraceFoundations(Main host, string? capturePath)
    {
        var session = host.Session;
        var map = session.Game.Map;
        var view = host.Map.Perspective!;
        var area = map.BuildingFootprints.Values.First(a => map.BuildingAt(a.X, a.Y)!.Name == "Cottage");
        var fixture = new CellRect(area.X - 1, area.Y - 4, 9, 9);
        var original = fixture.Cells().Select(p => (Position: p, Terrain: map.TerrainAt(p.X, p.Y))).ToArray();
        foreach (var p in fixture.Cells())
            map.SetTerrain(p.X, p.Y, map.Content.Terrains.Get(
                p.X > fixture.X && p.X < fixture.Right && p.Y > fixture.Y && p.Y < fixture.Bottom ? "Hill" : "Meadow"));
        session.Game.Touch();
        session.CenterOn(new(area.X, area.Y));
        host.Map.RefreshCells();
        view.Sync(session, host.Map.Size);
        float highest = area.Cells().Max(p => TerrainSprite.Elevation(map, p.X, p.Y));
        float lowest = area.Cells().Min(p => TerrainSprite.Elevation(map, p.X, p.Y));
        var anchor = new Pos(area.X, area.Y);
        Require(highest > lowest && view.BuildingVolumes(anchor, HouseAppearance.StoneColor).Any(bounds =>
            bounds.Position.Y <= lowest && bounds.End.Y >= highest),
            "Hillside houses must have a stone lower level embedded into the slope up to the upper floor.");
        Require(view.BuildingVolumes(anchor, 0x79634b).Count == 0 && view.BuildingVolumes(anchor, 0x61734c).Count == 0,
            "Hillside houses must not stand on full-cell wooden podiums or floating plot slabs.");
        var appearance = HouseAppearance.At(anchor.X, anchor.Y, map.BuildingAt(anchor.X, anchor.Y)!.Level);
        float houseLeft = area.X + area.Width * (1 - appearance.Width) / 2 - 0.023f;
        float houseRight = area.X + area.Width * (1 + appearance.Width) / 2 + 0.023f;
        float houseBack = area.Y + area.Height * (1 - appearance.Depth) / 2 - 0.023f;
        float houseFront = area.Y + area.Height * (1 + appearance.Depth) / 2 + 0.023f;
        var lowerLevels = view.BuildingVolumes(anchor, HouseAppearance.StoneColor)
            .Where(bounds => bounds.Position.Y < highest - 0.05f).ToArray();
        Require(lowerLevels.Length > 0 && lowerLevels.All(bounds =>
            bounds.Position.X >= houseLeft && bounds.End.X <= houseRight &&
            bounds.Position.Z >= houseBack && bounds.End.Z <= houseFront),
            "Lower stone levels must follow the inset house walls, not fill the entire reserved plot.");
        var groundSamples = area.Cells().Select(cell => new Vector2(cell.X + 0.5f, cell.Y + 0.5f)).ToArray();
        var terrainHeights = groundSamples.Select(sample => view.RenderedGroundHeight(map, sample.X, sample.Y)).ToArray();
        for (int turn = 0; turn < 4; turn++)
        {
            view.Sync(session, host.Map.Size);
            Require(groundSamples.Select(sample => view.RenderedGroundHeight(map, sample.X, sample.Y))
                .SequenceEqual(terrainHeights), "Hillside houses must leave rendered terrain heights untouched.");
            if (turn == 0 && capturePath is not null && DisplayServer.GetName() != "headless")
            {
                await RenderedFrame(host);
                using var image = view.Texture.GetImage();
                Require(image.SavePng(Path.ChangeExtension(capturePath, ".hillside.png")) == Error.Ok,
                    "Split-level hillside house screenshot must save successfully.");
            }
            var wall = lowerLevels.OrderBy(bounds => bounds.Position.Y).First();
            Require(host.Map.TryPickBuilding(view.ScreenPoint(wall.Position + wall.Size / 2), out var lowerHouse) &&
                lowerHouse == anchor, "The exposed hillside lower level must remain pickable at every camera heading.");
            view.Rotate(90);
        }
        var houseType = map.BuildingAt(anchor.X, anchor.Y)!;
        int originalRotation = map.BuildingRotations.GetValueOrDefault(map.Index(anchor.X, anchor.Y));
        for (int rotation = 0; rotation < 4; rotation++)
        {
            map.SetBuilding(anchor.X, anchor.Y, null);
            var rotatedArea = houseType.FootprintAt(anchor.X, anchor.Y, rotation);
            map.SetBuildingFootprint(houseType, rotatedArea);
            map.BuildingRotations[map.Index(anchor.X, anchor.Y)] = rotation;
            session.Game.Touch();
            host.Map.RefreshCells();
            view.Sync(session, host.Map.Size);
            float floor = rotatedArea.Cells().Max(cell => TerrainSprite.Elevation(map, cell.X, cell.Y));
            var walls = view.BuildingVolumes(anchor, appearance.WallColor);
            var supports = view.BuildingVolumes(anchor, HouseAppearance.StoneColor)
                .Where(bounds => bounds.Position.Y < floor - 0.05f).ToArray();
            Require(supports.All(support => walls.Any(wall =>
                support.Position.X >= wall.Position.X - 0.024f && support.End.X <= wall.End.X + 0.024f &&
                support.Position.Z >= wall.Position.Z - 0.024f && support.End.Z <= wall.End.Z + 0.024f)),
                "Split-level stone foundations must follow the actual rotated house and wing walls.");
            Require(view.BuildingVolumes(anchor, 0x79634b).Count == 0 && view.BuildingVolumes(anchor, 0x61734c).Count == 0,
                "Rotated hillside houses must not regain a full-plot podium.");
            Require(groundSamples.Select(sample => view.RenderedGroundHeight(map, sample.X, sample.Y))
                .SequenceEqual(terrainHeights), "Rotating a hillside house must not reshape its terrain.");
        }
        map.SetBuilding(anchor.X, anchor.Y, null);
        map.SetBuildingFootprint(houseType, area);
        if (originalRotation != 0) map.BuildingRotations[map.Index(anchor.X, anchor.Y)] = originalRotation;
        session.Game.Touch();
        session.SelectCell(anchor);
        foreach (int zoom in new[] { -2, -1, 0, 1 })
        {
            session.SetZoom(zoom);
            host.Map.RefreshCells();
            view.Sync(session, host.Map.Size);
            Require(Math.Abs(view.SelectionOutlineThickness * host.Map.Size.Y / view.CameraSpan - 2) < 0.001f,
                "Selection marker must retain two-pixel nominal thickness at every zoom.");
            float selectedHeight = session.ActiveArea.Cells().Where(p => map.InBounds(p.X, p.Y))
                .Max(p => TerrainSprite.Elevation(map, p.X, p.Y));
            Require(view.SelectionOutlineHeight > selectedHeight,
                "Selection marker must remain above the highest selected terrain after zooming.");
        }
        session.SetZoom(0);
        host.Map.RefreshCells();
        view.Sync(session, host.Map.Size);
        var pointer = view.ScreenPoint(new(area.X + area.Width / 2f,
            highest + 0.8f, area.Y + area.Height / 2f));
        Require(host.Map.TryPickBuilding(pointer, out var picked) && picked == anchor,
            "A building raised onto its highest terrace must remain pickable.");
        foreach (var entry in original) map.SetTerrain(entry.Position.X, entry.Position.Y, entry.Terrain);
        session.Game.Touch();
        GD.Print("TERMCITY_TERRACE_FOUNDATIONS_OK");
    }

    private static async Task VerifyBankColors(Main host, string? capturePath)
    {
        var session = host.Session;
        var map = session.Game.Map;
        var view = host.Map.Perspective!;
        int originalZoom = session.ZoomLevel;
        var origin = Enumerable.Range(0, map.Width * map.Height).Select(map.PosOf)
            .Where(p => p.X >= 2 && p.Y >= 2 && p.X < map.Width - 13 && p.Y < map.Height - 11)
            .OrderBy(p => Math.Abs(p.X - map.Width / 2) + Math.Abs(p.Y - map.Height / 2))
            .First(p => new CellRect(p.X, p.Y, 11, 9).Cells().All(cell => session.Game.CanBuildOn(cell.X, cell.Y)));
        var fixture = new CellRect(origin.X, origin.Y, 11, 9);
        var original = fixture.Cells().Select(p => (Position: p, Terrain: map.TerrainAt(p.X, p.Y),
            Feature: map.FeatureAt(p.X, p.Y), Sea: map.IsSea(p.X, p.Y), River: map.IsRiver(p.X, p.Y))).ToArray();
        foreach (var cell in fixture.Cells())
        {
            int edge = origin.Y + 4 + (cell.X - origin.X) / 3 % 2;
            map.SetTerrain(cell.X, cell.Y, map.Content.Terrains.Get(cell.Y >= edge ? "Water" : "Meadow"));
            map.SetFeature(cell.X, cell.Y, null);
            map.SetRiverWater(cell.X, cell.Y, cell.Y >= edge);
        }
        session.Game.Touch();
        session.SetZoom(0);
        host.Map.RefreshCells();
        session.CenterOn(new(origin.X + 5, origin.Y + 4));
        int samples = 0;
        for (int heading = 0; heading < 4; heading++)
        {
            view.Sync(session, host.Map.Size);
            for (int frame = 0; frame < 3; frame++)
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await RenderedFrame(host);
            using var image = view.Texture.GetImage();
            if (capturePath is not null)
                Require(image.SavePng(Path.ChangeExtension(capturePath, $".bank-color-{heading}.png")) == Error.Ok,
                    "Stepped-bank color captures must save successfully.");
            foreach (var cell in fixture.Cells().Where(p => p.X > fixture.X && p.X < fixture.Right &&
                p.Y > fixture.Y && p.Y < fixture.Bottom && ShorelineLayout.BankCell(map, p.X, p.Y)))
            {
                float centerX = cell.X + 0.5f, centerY = cell.Y + 0.5f;
                var pointer = view.ScreenPoint(new(centerX, view.RenderedGroundHeight(map, centerX, centerY), centerY));
                Require(pointer.X >= 0 && pointer.X < image.GetWidth() && pointer.Y >= 0 && pointer.Y < image.GetHeight(),
                    "The entire bank color fixture must fit in the viewport at every heading.");
                var color = image.GetPixel((int)pointer.X, (int)pointer.Y);
                Require(color.R > color.G * 0.75f && color.G > color.B * 1.15f,
                    $"Every block adjoining river water must share the earthy bank palette: {cell}, heading {heading}, {color}.");
                samples++;
            }
            view.Rotate(90);
        }
        foreach (var entry in original)
        {
            map.SetTerrain(entry.Position.X, entry.Position.Y, entry.Terrain);
            map.SetFeature(entry.Position.X, entry.Position.Y, entry.Feature);
            map.SetSeaWater(entry.Position.X, entry.Position.Y, entry.Sea);
            map.SetRiverWater(entry.Position.X, entry.Position.Y, entry.River);
        }
        session.Game.Touch();
        session.SetZoom(originalZoom);
        host.Map.RefreshCells();
        Require(samples >= 36, "Bank color checks must cover straight and stepped shores at all four headings.");
        GD.Print($"TERMCITY_BANK_COLORS_OK samples={samples}");
    }

    private static async Task VerifyClosedTerrainEdges(Main host, string? capturePath)
    {
        var session = host.Session;
        var map = session.Game.Map;
        var view = host.Map.Perspective!;
        int x = map.Width / 2 + 17, y = map.Height / 2 + 12;
        var fixture = new CellRect(x - 3, y - 3, 7, 7);
        var terrains = fixture.Cells().Select(p => (Position: p, Terrain: map.TerrainAt(p.X, p.Y),
            Sea: map.IsSea(p.X, p.Y), River: map.IsRiver(p.X, p.Y))).ToArray();
        foreach (var p in fixture.Cells())
            map.SetTerrain(p.X, p.Y, map.Content.Terrains.Get(p.X <= x ? "Hill" : "Water"));
        session.Game.Touch();
        session.SetZoom(1);
        host.Map.RefreshCells();
        session.CenterOn(new(x, y));
        view.Sync(session, host.Map.Size);
        var bankVectors = new RoadVectorLayer();
        bankVectors.Sync(session.Game);
        var bankLayout = new RoadMeshLayout(map, bankVectors.Paths);
        var bankMeshes = new RoadSurfaceMeshes(bankLayout);
        int shoreSamples = 0;
        int upperCoastalTriangles = 0;
        for (int cy = fixture.Y / TerrainSprite.ChunkCells; cy <= fixture.Bottom / TerrainSprite.ChunkCells; cy++)
            for (int cx = fixture.X / TerrainSprite.ChunkCells; cx <= fixture.Right / TerrainSprite.ChunkCells; cx++)
            {
                var bank = bankMeshes.Build(session, cx, cy);
                using var groundMesh = bank.Ground;
                using var pavingMesh = bank.Roads;
                using var cliffMesh = bank.Banks;
                var vertices = groundMesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                for (int vertex = 0; vertex < vertices.Length; vertex += 3)
                {
                    var center = (vertices[vertex] + vertices[vertex + 1] + vertices[vertex + 2]) / 3;
                    int cellX = (int)Math.Floor(center.X), cellY = (int)Math.Floor(center.Z);
                    if (!fixture.Contains(new(cellX, cellY)) ||
                        TerrainSprite.Elevation(map, cellX, cellY) <= TerrainSprite.HillBaseHeight ||
                        TerrainSprite.WaterSurface(map, center.X, center.Z) >= ShorelineLayout.WaterThreshold ||
                        bankLayout.InCorridor(new(center.X, center.Z)) ||
                        bankMeshes.IsRemnant(map, new(center.X, center.Z))) continue;
                    float level = TerrainSprite.Elevation(map, cellX, cellY);
                    for (int corner = 0; corner < 3; corner++)
                    {
                        var point = vertices[vertex + corner];
                        Require(Math.Abs(point.Y - level) < 0.001f,
                            $"Upper ridge panels must remain level at their coastal edges, not slope down: {point}, expected {level}.");
                        if (TerrainSprite.WaterSurface(map, point.X, point.Z) > 0.02)
                            upperCoastalTriangles++;
                    }
                }
                foreach (var sample in bank.ShoreSamples)
                {
                    Require(sample.Top.X == sample.Bottom.X && sample.Top.Z == sample.Bottom.Z &&
                        sample.Top.Y >= sample.Bottom.Y,
                        "Riverbanks must drop vertically at the contour, not slope into the water.");
                    shoreSamples++;
                }
                for (int sample = 0; sample < bank.ShoreSamples.Count; sample += 2)
                {
                    var start = bank.ShoreSamples[sample].Top;
                    var end = bank.ShoreSamples[sample + 1].Top;
                    Require(start.X == end.X && start.X == MathF.Round(start.X) ||
                        start.Z == end.Z && start.Z == MathF.Round(start.Z),
                        "Rendered water's edges must follow whole-cell block boundaries, not smoothed diagonals.");
                }
            }
        Require(shoreSamples > 0, "The hill/river fixture must contain actual vertical shoreline walls.");
        Require(upperCoastalTriangles > 0, "The ridge fixture must exercise upper terrace edges near water.");
        foreach (var p in fixture.Cells())
        {
            float center = view.RenderedGroundHeight(map, p.X + 0.5f, p.Y + 0.5f);
            if (!map.TerrainAt(p.X, p.Y).Buildable)
                Require(center < 0, "Water must remain below the land rather than inheriting terrace elevations.");
            if (map.TerrainAt(p.X, p.Y).BuildCostModifier > 1 &&
                TerrainSprite.WaterSurface(map, p.X, p.Y) == 0 &&
                TerrainSprite.WaterSurface(map, p.X + 1, p.Y) == 0 &&
                TerrainSprite.WaterSurface(map, p.X, p.Y + 1) == 0 &&
                TerrainSprite.WaterSurface(map, p.X + 1, p.Y + 1) == 0 &&
                !view.GradedPanelAt(map, p.X + 0.5f, p.Y + 0.5f))
                Require(Math.Abs(center - TerrainSprite.Elevation(map, p.X, p.Y)) < 0.001f,
                    "Coastal hill tops must stay level, not collapse into shoreline wedges.");
        }
        Require(view.CliffEdges.All(edge =>
        {
            var midpoint = (edge.A + edge.B) / 2;
            var normal = new Vector2(-(edge.B - edge.A).Y, (edge.B - edge.A).X).Normalized() * 0.001f;
            bool Land(Vector2 point) => map.InBounds((int)Math.Floor(point.X), (int)Math.Floor(point.Y)) &&
                map.TerrainAt((int)Math.Floor(point.X), (int)Math.Floor(point.Y)).Buildable;
            return Land(midpoint + normal) || Land(midpoint - normal);
        }),
            "No land cliff may extend into the visible river surface.");
        if (DisplayServer.GetName() != "headless")
        {
            for (int frame = 0; frame < 3; frame++)
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await RenderedFrame(host);
            using var image = view.Texture.GetImage();
            if (capturePath is not null)
            {
                var error = image.SavePng(Path.ChangeExtension(capturePath, ".river-cliff.png"));
                if (error != Error.Ok) throw new IOException($"River-cliff screenshot failed: {error}.");
            }
            var midpoint = view.ScreenPoint(new(x + 0.5f, 0, y + 0.5f));
            for (int dy = -30; dy <= 30; dy++)
                for (int dx = -50; dx <= 50; dx++)
                {
                    var pixel = image.GetPixel((int)midpoint.X + dx, (int)midpoint.Y + dy);
                    Require(pixel.G > 0.15f || pixel.B > 0.15f,
                        "The rendered hill/river junction must not expose dark background cracks.");
                }
            if (capturePath is not null)
            {
                view.Tilt();
                Require(view.Elevation == 50, "Riverbank capture must begin from the normal 35-degree view.");
                view.Tilt();
                view.Sync(session, host.Map.Size);
                for (int frame = 0; frame < 3; frame++)
                    await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                await RenderedFrame(host);
                using var lowView = view.Texture.GetImage();
                var error = lowView.SavePng(Path.ChangeExtension(capturePath, ".river-cliff-low.png"));
                if (error != Error.Ok) throw new IOException($"Low-angle river-cliff screenshot failed: {error}.");
                view.Tilt();
                view.Sync(session, host.Map.Size);
            }
            foreach (int zoom in new[] { 0, 1 })
            {
                session.SetZoom(zoom);
                host.Map.RefreshCells();
                session.CenterOn(new(x - 1, y));
                for (int heading = 0; heading < 4; heading++)
                {
                    view.Sync(session, host.Map.Size);
                    await RenderedFrame(host);
                    using var ridge = view.Texture.GetImage();
                    if (capturePath is not null)
                        Require(ridge.SavePng(Path.ChangeExtension(capturePath, $".ridge-{zoom}-{heading}.png")) == Error.Ok,
                            "The level ridge capture must save successfully at each heading and zoom.");
                    view.Rotate(90);
                }
            }
        }
        foreach (var entry in terrains)
        {
            map.SetTerrain(entry.Position.X, entry.Position.Y, entry.Terrain);
            map.SetSeaWater(entry.Position.X, entry.Position.Y, entry.Sea);
            map.SetRiverWater(entry.Position.X, entry.Position.Y, entry.River);
        }
        session.Game.Touch();
        session.SetZoom(0);
        host.Map.RefreshCells();
        GD.Print("TERMCITY_TERRAIN_EDGES_OK");
    }

    private static async Task VerifyRiverBeaches(Main host, string? capturePath)
    {
        var session = host.Session;
        var map = session.Game.Map;
        var view = host.Map.Perspective!;
        var center = Enumerable.Range(0, map.Width * map.Height).Select(map.PosOf).First(p =>
            p.X >= 10 && p.X < map.Width - 10 && p.Y >= 8 && p.Y < map.Height - 8 &&
            ShorelineLayout.BeachStrength(p.X, p.Y) > 0.99f &&
            new CellRect(p.X - 8, p.Y - 6, 17, 13).Cells().All(c => map.BuildingAt(c.X, c.Y) is null));
        int x = center.X, y = center.Y;
        var fixture = new CellRect(x - 8, y - 6, 17, 13);
        var original = fixture.Cells().Select(p => (Position: p, Terrain: map.TerrainAt(p.X, p.Y),
            Road: map.RoadTypeAt(p.X, p.Y), Feature: map.FeatureAt(p.X, p.Y), Zone: map.ZoneAt(p.X, p.Y),
            Sea: map.IsSea(p.X, p.Y), River: map.IsRiver(p.X, p.Y))).ToArray();
        foreach (var p in fixture.Cells())
        {
            map.SetRoad(p.X, p.Y, false);
            map.SetFeature(p.X, p.Y, null);
            int bankX = x + (int)Math.Round((p.Y - y) * 0.3);
            map.SetTerrain(p.X, p.Y, map.Content.Terrains.Get(p.X < bankX ? "Meadow" : p.X < bankX + 5 ? "Water" : "Hill"));
            if (p.X >= bankX && p.X < bankX + 5) map.SetSeaWater(p.X, p.Y, true);
        }
        session.Game.Touch();
        session.SetZoom(1);
        host.Map.RefreshCells();
        session.CenterOn(center);
        view.Sync(session, host.Map.Size);
        var vectors = new RoadVectorLayer();
        vectors.Sync(session.Game);
        var meshes = new RoadSurfaceMeshes(new RoadMeshLayout(map, vectors.Paths));
        int beachVertices = 0, cliffSamples = 0;
        var beachFaces = new Dictionary<int, Vector3[]>();
        for (int cy = fixture.Y / TerrainSprite.ChunkCells; cy <= fixture.Bottom / TerrainSprite.ChunkCells; cy++)
            for (int cx = fixture.X / TerrainSprite.ChunkCells; cx <= fixture.Right / TerrainSprite.ChunkCells; cx++)
            {
                var chunk = meshes.Build(session, cx, cy);
                using var ground = chunk.Ground;
                using var paving = chunk.Roads;
                using var banks = chunk.Banks;
                foreach (var face in chunk.Faces) beachFaces[face.Key] = face.Value;
                using var arrays = ground.SurfaceGetArrays(0);
                var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var colors = arrays[(int)Mesh.ArrayType.Color].AsColorArray();
                for (int i = 0; i < vertices.Length; i++)
                {
                    if (vertices[i].X > x - 2 && vertices[i].X < x && Math.Abs(vertices[i].Z - y) < 2 &&
                        colors[i].A >= 0.5f && colors[i].A < 0.9f && vertices[i].Y < -0.03f)
                        beachVertices++;
                    var vertex = vertices[i];
                    if (colors[i].A < 0.5f || vertex.X < x - 4 || vertex.X >= x || Math.Abs(vertex.Z - y) >= 3) continue;
                    float expected = TerrainSprite.WaterCornerLevel(map, (int)vertex.X, (int)vertex.Z) *
                        ShorelineLayout.BankAmount(map, vertex.X, vertex.Z);
                    Require(Math.Abs(vertex.Y - expected) < 0.002f,
                        $"Beach and inland terrain must share slope heights at every mesh vertex: {vertex}, expected {expected}.");
                }
                foreach (var sample in chunk.ShoreSamples)
                    if (sample.Top.X > x + 4 && sample.Top.X < x + 7 && Math.Abs(sample.Top.Z - y) < 2 &&
                        sample.Top.Y - sample.Bottom.Y > 0.25f) cliffSamples++;
            }
        Require(beachVertices > 0, "Selected sea coasts must contain sandy land next to the narrow water transition.");
        Require(cliffSamples > 0, "Raised hills must retain vertical cliff banks beside the same river.");
        for (int cy = y - 3; cy <= y + 3; cy++)
            for (int cx = x - 4; cx < x; cx++)
                for (int step = 1; step < 8; step++)
                {
                    float offset = step / 8f;
                    CompareEdge(cx, cy, cx + 1, cy, new(cx + 1, cy + offset));
                    CompareEdge(cx, cy, cx, cy + 1, new(cx + offset, cy + 1));
                }
        void CompareEdge(int ax, int ay, int bx, int by, Vector2 point)
        {
            if (!map.TerrainAt(ax, ay).Buildable || !map.TerrainAt(bx, by).Buildable) return;
            float a = EdgeHeight(beachFaces[map.Index(ax, ay)], point);
            float b = EdgeHeight(beachFaces[map.Index(bx, by)], point);
            Require(float.IsFinite(a) && float.IsFinite(b) && Math.Abs(a - b) < 0.001f,
                $"Beach/inland cell edges must meet without cracks at {point}: {a} versus {b}.");
        }
        static float EdgeHeight(Vector3[] faces, Vector2 point)
        {
            float height = float.NegativeInfinity;
            for (int i = 0; i < faces.Length; i += 3)
            {
                var a = faces[i]; var b = faces[i + 1]; var c = faces[i + 2];
                var ab = new Vector2(b.X - a.X, b.Z - a.Z);
                var ac = new Vector2(c.X - a.X, c.Z - a.Z);
                var ap = point - new Vector2(a.X, a.Z);
                float denominator = ab.Cross(ac);
                if (Math.Abs(denominator) < 0.0000001f) continue;
                float u = ap.Cross(ac) / denominator, v = ab.Cross(ap) / denominator;
                if (u < -0.001f || v < -0.001f || u + v > 1.001f) continue;
                height = Math.Max(height, a.Y + u * (b.Y - a.Y) + v * (c.Y - a.Y));
            }
            return height;
        }
        float px = x - 0.5f, pz = y + 0.5f;
        var beachPoint = new Vector3(px, view.RenderedGroundHeight(map, px, pz), pz);
        var pointer = view.ScreenPoint(beachPoint);
        Require(view.GroundPoint(pointer).DistanceTo(beachPoint) < 0.01f,
            "Beach picking must follow the gently lowered sandy surface.");
        map.SetFeature(x - 1, y, map.Content.Features.Get("Tree"));
        Require(!TerrainSprite.NaturalFeatureVisible(session.Game, x - 1, y),
            "Trees from existing saves must not render where their footprint reaches visible water.");
        map.SetFeature(x - 1, y, null);
        for (int frame = 0; frame < 3; frame++)
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        if (capturePath is not null && DisplayServer.GetName() != "headless")
        {
            await RenderedFrame(host);
            using var image = view.Texture.GetImage();
            var pixel = image.GetPixel((int)pointer.X, (int)pointer.Y);
            Require(pixel.R > pixel.B * 1.1f && pixel.G > pixel.B * 1.1f,
                $"The beach must render as visible sand rather than grass or water: {pixel}.");
            for (float pzScan = y - 2; pzScan <= y + 2; pzScan += 0.1f)
                for (float pxScan = x - 4; pxScan <= x + 1; pxScan += 0.025f)
                {
                    var projected = view.ScreenPoint(new(pxScan, view.RenderedGroundHeight(map, pxScan, pzScan), pzScan));
                    int ix = (int)projected.X, iy = (int)projected.Y;
                    if (ix < 0 || iy < 0 || ix >= image.GetWidth() || iy >= image.GetHeight()) continue;
                    var sample = image.GetPixel(ix, iy);
                    Require(sample.G > 0.15f || sample.B > 0.15f,
                        $"Beach/land/water triangles must not leave black seams at {pxScan},{pzScan}: {sample}.");
                }
            var error = image.SavePng(Path.ChangeExtension(capturePath, ".beach.png"));
            if (error != Error.Ok) throw new IOException($"Beach screenshot failed: {error}.");
        }
        var well = map.Content.Buildings.Get("Town Well");
        var plot = new CellRect(x - well.Width, y, well.Width, well.Height);
        var anchor = new Pos(plot.X, plot.Y);
        float supportedGround = view.RenderedGroundHeight(map, x - 0.5f, y + 0.5f);
        map.SetBuildingFootprint(well, plot);
        session.Game.Touch();
        view.Sync(session, host.Map.Size);
        Require(Math.Abs(view.RenderedGroundHeight(map, x - 0.5f, y + 0.5f) - supportedGround) < 0.001f,
            "Beach geometry must not change when a coastal building is placed.");
        Require(view.BuildingVolumes(anchor, 0x79634b).Any(volume =>
            volume.Position.Y <= supportedGround && volume.End.Y >= 0),
            "Coastal building foundations must reach the beach instead of floating above it.");
        foreach (var p in plot.Cells()) map.ClearCell(p.X, p.Y);
        foreach (var entry in original)
        {
            map.SetTerrain(entry.Position.X, entry.Position.Y, entry.Terrain);
            map.SetSeaWater(entry.Position.X, entry.Position.Y, entry.Sea);
            map.SetRiverWater(entry.Position.X, entry.Position.Y, entry.River);
            map.SetRoad(entry.Position.X, entry.Position.Y, false);
            if (entry.Road is { } road) map.SetRoad(entry.Position.X, entry.Position.Y, road);
            map.SetFeature(entry.Position.X, entry.Position.Y, entry.Feature);
            map.SetZone(entry.Position.X, entry.Position.Y, entry.Zone);
        }
        session.Game.Touch();
        session.SetZoom(0);
        host.Map.RefreshCells();
        GD.Print("TERMCITY_RIVER_BEACHES_OK");
    }

    private static async Task VerifyShoreRoadSeams(Main host, string? capturePath)
    {
        var session = host.Session;
        var map = session.Game.Map;
        var view = host.Map.Perspective!;
        int x = map.Width / 2 + 34, y = map.Height / 2 + 18;
        var fixture = new CellRect(x - 4, y - 6, 9, 13);
        var original = fixture.Cells().Select(p => (Position: p, Terrain: map.TerrainAt(p.X, p.Y),
            Sea: map.IsSea(p.X, p.Y), River: map.IsRiver(p.X, p.Y), Road: map.RoadTypeAt(p.X, p.Y), Feature: map.FeatureAt(p.X, p.Y))).ToArray();
        foreach (int profile in new[] { 0, 1, 2 })
        {
            bool raised = profile == 1;
            foreach (var p in fixture.Cells())
            {
                map.SetRoad(p.X, p.Y, false);
                map.SetFeature(p.X, p.Y, null);
                map.SetTerrain(p.X, p.Y, map.Content.Terrains.Get(p.X > x ? "Water" :
                    raised || profile == 2 && p.Y > y + 1 ? "Hill" : "Meadow"));
            }
            for (int cy = y - 5; cy <= y + 5; cy++)
                map.SetRoad(x, cy, map.Content.Roads.Get(TermCity.Core.Roads.DefaultRoads.KingsRoadName));
            session.Game.Touch();
            session.SetZoom(1);
            host.Map.RefreshCells();
            session.CenterOn(new(x, y));
            view.Sync(session, host.Map.Size);
            var vectors = new RoadVectorLayer();
            vectors.Sync(session.Game);
            var layout = new RoadMeshLayout(map, vectors.Paths);
            var meshes = new RoadSurfaceMeshes(layout);
            int tested = 0;
            for (int cy = fixture.Y / TerrainSprite.ChunkCells; cy <= fixture.Bottom / TerrainSprite.ChunkCells; cy++)
                for (int cx = fixture.X / TerrainSprite.ChunkCells; cx <= fixture.Right / TerrainSprite.ChunkCells; cx++)
                {
                    var chunk = meshes.Build(session, cx, cy);
                    using var ground = chunk.Ground;
                    using var roads = chunk.Roads;
                    using var banks = chunk.Banks;
                    for (int sample = 0; sample < chunk.ShoreSamples.Count; sample += 2)
                    {
                        var start = chunk.ShoreSamples[sample].Top;
                        var end = chunk.ShoreSamples[sample + 1].Top;
                        var center = (start + end) / 2;
                        if (Math.Abs(center.Z - y) > 3 || Math.Abs(center.X - x - 1) > 0.001f) continue;
                        foreach (float amount in new[] { 0.25f, 0.5f, 0.75f })
                        {
                            var point = start.Lerp(end, amount);
                            float expected = view.RenderedGroundHeight(map, point.X - 0.0001f, point.Z);
                            Require(Math.Abs(point.Y - expected) < 0.001f,
                                $"Block-bank walls beside the road must close against its actual land/shoulder surface: {point}, expected {expected}.");
                        }
                        tested++;
                    }
                }
            Require(tested > 0, "The shoreline-road fixture must test actual block-bank faces alongside the road.");
            for (int frame = 0; frame < 3; frame++)
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            if (capturePath is not null && DisplayServer.GetName() != "headless")
            {
                await RenderedFrame(host);
                using var image = view.Texture.GetImage();
                for (float pz = y - 3; pz <= y + 3; pz += 0.05f)
                    for (float px = x + 0.5f; px <= x + 1.2f; px += 0.025f)
                    {
                        var pointer = view.ScreenPoint(new(px, view.RenderedGroundHeight(map, px, pz), pz));
                        int ix = (int)pointer.X, iy = (int)pointer.Y;
                        if (ix < 0 || iy < 0 || ix >= image.GetWidth() || iy >= image.GetHeight()) continue;
                        var pixel = image.GetPixel(ix, iy);
                        Require(pixel.G > 0.15f || pixel.B > 0.15f,
                            $"Road/shore junction must not expose fine black gaps at {px},{pz}: {pixel}.");
                    }
                var error = image.SavePng(Path.ChangeExtension(capturePath, profile == 2 ? ".incline-shore-road.png" :
                    raised ? ".hill-shore-road.png" : ".shore-road.png"));
                if (error != Error.Ok) throw new IOException($"Shore-road screenshot failed: {error}.");
            }
        }
        foreach (var entry in original)
        {
            map.SetTerrain(entry.Position.X, entry.Position.Y, entry.Terrain);
            map.SetSeaWater(entry.Position.X, entry.Position.Y, entry.Sea);
            map.SetRiverWater(entry.Position.X, entry.Position.Y, entry.River);
            map.SetRoad(entry.Position.X, entry.Position.Y, false);
            if (entry.Road is { } road) map.SetRoad(entry.Position.X, entry.Position.Y, road);
            map.SetFeature(entry.Position.X, entry.Position.Y, entry.Feature);
        }
        session.Game.Touch();
        session.SetZoom(0);
        host.Map.RefreshCells();
        GD.Print("TERMCITY_SHORE_ROAD_SEAMS_OK");
    }

    public static async Task VerifyBuildingRendering(Main host)
    {
        var session = host.Session;
        var map = session.Game.Map;
        int cx = map.Width / 2, cy = map.Height / 2;
        var gallery = new CellRect(cx - 24, cy - 15, 48, 30);
        foreach (var p in session.Game.DemolitionCells(gallery.Cells()))
        {
            map.ClearCell(p.X, p.Y);
            map.SetTerrain(p.X, p.Y, map.Content.Terrains.Base);
        }
        int n = 0;
        foreach (var type in map.Content.Buildings.Where(type => !type.DebugOnly || CityGame.DebugToolsAvailable))
        {
            int x = gallery.X + n % 6 * 8, y = gallery.Y + n / 6 * 5;
            var area = new CellRect(x, y, type.Width, type.Height);
            foreach (var p in area.Cells()) map.SetZone(p.X, p.Y, type.Zone);
            map.SetBuildingFootprint(type, area);
            if (type.Zone == ZoneType.Residential) map.SetHousehold(x, y, new Household(2, 2, 1));
            n++;
        }
        for (int y = gallery.Y + 2; y < gallery.Y + 4; y++)
            for (int x = gallery.X + 5; x < gallery.X + 8; x++)
                map.SetTerrain(x, y, map.Content.Terrains.Get("Hill"));
        map.SetFeature(gallery.X + 5, gallery.Y + 3, map.Content.Features.Get("Rock"));
        map.SetFeature(gallery.X + 6, gallery.Y + 2, map.Content.Features.Get("Tree"));
        for (int y = gallery.Y + 7; y < gallery.Y + 9; y++)
            for (int x = gallery.X + 6; x < gallery.X + 8; x++)
                map.SetTerrain(x, y, map.Content.Terrains.Get("Water"));
        foreach (var p in CellLines.Between(new(cx - 24, cy + 19), new(cx + 24, cy + 27)))
            for (int dy = -1; dy <= 1; dy++)
                if (map.InBounds(p.X, p.Y + dy))
                {
                    map.SetTerrain(p.X, p.Y + dy, map.Content.Terrains.Get("Water"));
                    map.SetFeature(p.X, p.Y + dy, null);
                }
        session.Game.Touch();
        foreach (int zoom in new[] { 1, 0, -1, -2 })
        {
            session.SetZoom(zoom);
            session.CenterOn(new(cx, cy));
            host.Map.Invalidate(true);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            Require(host.Map.BuildingsDrawn > 0, $"No building sprites drawn at zoom {zoom}.");
            var area = map.BuildingFootprints.Values.First(a => map.BuildingAt(a.X, a.Y)!.Name == "Parish Church");
            var type = map.BuildingAt(area.X, area.Y)!;
            var sprite = BuildingSprite.Render(type, area.Width, area.Height, CellHash.Pick(area.X, area.Y, 2));
            int roofPixel = Enumerable.Range(0, sprite.Width * sprite.Lift)
                .First(i => sprite.Pixels[i * 4 + 3] == 255);
            var origin = session.ScreenToMap(0, 0);
            float pixelScale = host.Map.Grid.PixelWidth / (12f * session.Stride);
            var pointer = new Vector2((area.X - origin.X) * 12 + roofPixel % sprite.Width + 0.5f,
                (area.Y - origin.Y) * 22 - sprite.Lift + roofPixel / sprite.Width + 0.5f) * pixelScale;
            if (new Rect2(Vector2.Zero, host.Map.Size).HasPoint(pointer))
            {
                Require(host.Map.TryPickBuilding(pointer, out var picked) && picked == new Pos(area.X, area.Y),
                    $"Raised roofs must pick their building at zoom {zoom}.");
                host.Map.EmitSignal(Control.SignalName.GuiInput,
                    new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = pointer });
                Require(session.Prompt?.Title == BuildingReport.Title, "Right-clicking a raised roof must open Building Status.");
                session.ClosePrompt();
            }
        }
        session.SetZoom(0);
        session.CenterOn(new(cx, cy));
        session.ClearSelection();
        session.PlaceCursor(new(cx + 23, cy + 14));
        host.Effects.Clear();
        host.Map.Invalidate(true);
        GD.Print("TERMCITY_BUILDING_RENDER_OK");
    }

    public static async Task RunNativeInput(Main host)
    {
        var session = host.Session;
        Require(host.Theme.GetStylebox("panel", "TooltipPanel") is StyleBoxTexture,
            "Hover tooltips must use the shared double-white terminal style.");
        var tooltipStyle = (StyleBoxTexture)host.Theme.GetStylebox("panel", "TooltipPanel");
        using (var tooltipImage = tooltipStyle.Texture.GetImage())
        {
            Require(tooltipImage.GetPixel(0, 16) == Colors.White &&
                tooltipImage.GetPixel(4, 16) == Colors.White &&
                tooltipImage.GetPixel(2, 16) == Colors.Black &&
                tooltipImage.GetPixel(16, 16) == Colors.Black &&
                new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }.All(side =>
                    tooltipStyle.GetContentMargin(side) == TerminalFrame.Inset &&
                    tooltipStyle.GetTextureMargin(side) == TerminalFrame.Inset),
                "Tooltip borders must be white, separated by black, with the shared content inset.");
        }
        async Task Frames()
        {
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        async Task KeyEvent(Key code, bool ctrl = false, uint unicode = 0, bool meta = false, Key physical = Key.None,
            bool shift = false)
        {
            Input.ParseInputEvent(new InputEventKey
            {
                Keycode = code,
                PhysicalKeycode = physical,
                Pressed = true,
                CtrlPressed = ctrl,
                MetaPressed = meta,
                Unicode = unicode,
                ShiftPressed = shift
            });
            Input.ParseInputEvent(new InputEventKey
            {
                Keycode = code,
                PhysicalKeycode = physical,
                Pressed = false,
                CtrlPressed = ctrl,
                MetaPressed = meta,
                Unicode = unicode,
                ShiftPressed = shift
            });
            await Frames();
        }
        await Frames();
        host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
        await VerifyConstructionCatalog(host, Frames, KeyEvent);
        var treasuryLink = Descendants(host).OfType<Label>().Single(label => label.Name == "Budget");
        var populationLink = Descendants(host).OfType<Label>().Single(label => label.Name == "Population");
        foreach (var fraction in new[] { 0.05f, 0.5f, 0.95f })
        {
            float textWidth = Math.Min(treasuryLink.Size.X,
                treasuryLink.GetThemeFont("font").GetStringSize(treasuryLink.Text,
                    fontSize: treasuryLink.GetThemeFontSize("font_size")).X);
            var click = treasuryLink.GetGlobalRect().Position +
                new Vector2(treasuryLink.Size.X - textWidth + textWidth * fraction, treasuryLink.Size.Y / 2);
            foreach (bool pressed in new[] { true, false })
                host.GetViewport().PushInput(new InputEventMouseButton
                {
                    ButtonIndex = MouseButton.Left,
                    Pressed = pressed,
                    Position = click,
                    GlobalPosition = click,
                }, true);
            await Frames();
            Require(session.Prompt?.Title == (session.Game.Config.FullRules ? "Treasury, tithes and loans" : "Treasury"),
                "Clicking gold text must open the economy menu.");
            populationLink.EmitSignal(Control.SignalName.GuiInput,
                new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
            Require(session.Prompt?.Title != "City menu", "Header links must not replace an active dialog.");
            await KeyEvent(Key.Escape);
        }
        float emptyGoldWidth = treasuryLink.Size.X - treasuryLink.GetThemeFont("font").GetStringSize(
            treasuryLink.Text, fontSize: treasuryLink.GetThemeFontSize("font_size")).X;
        Require(emptyGoldWidth > 0, "Smoke must exercise empty space beside gold text.");
        treasuryLink.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = new Vector2(emptyGoldWidth / 2, treasuryLink.Size.Y / 2),
        });
        Require(session.Prompt is null, "Empty space in the gold label must not open the economy dialog.");
        var populationClick = populationLink.GetGlobalRect().GetCenter();
        var cityRows = Descendants(host).OfType<GridContainer>().Single(table => table.Name == "CityTable");
        int sidebarCellCount = cityRows.GetChildCount();
        for (int cellIndex = 0; cellIndex < sidebarCellCount; cellIndex++)
        {
            cityRows = Descendants(host).OfType<GridContainer>().Single(table => table.Name == "CityTable");
            var target = cityRows.GetChild<PanelContainer>(cellIndex);
            var metric = cityRows.GetChild<PanelContainer>(cellIndex - cellIndex % 2).GetChild<Label>(0).Text;
            if (string.IsNullOrEmpty(metric)) continue;
            var clickedControl = cellIndex % 2 == 0 ? (Control)target.GetChild<Label>(0) : target;
            clickedControl.EmitSignal(Control.SignalName.GuiInput,
                new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
            await Frames();
            Require(session.Prompt?.Title == "Weekly Report" &&
                session.Prompt.ActiveTab == SidebarReport.ReportTabFor(metric),
                $"Clicking sidebar metric {metric} must open the matching report section.");
            var openedReport = session.Prompt;
            clickedControl.EmitSignal(Control.SignalName.GuiInput,
                new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
            Require(ReferenceEquals(session.Prompt, openedReport),
                "Sidebar clicks must not replace an already open report.");
            await KeyEvent(Key.Escape);
        }
        foreach (bool pressed in new[] { true, false })
            host.GetViewport().PushInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = pressed,
                Position = populationClick,
                GlobalPosition = populationClick,
            }, true);
        await Frames();
        Require(session.Prompt is null, "Clicking population must not open a menu.");
        session.ShowSessionMenu();
        Require(session.Prompt!.Choices.Any(choice => choice.Label == "Report") &&
            session.Prompt.Choices.Any(choice => choice.Label == "Growth") &&
            session.Prompt.Choices.Any(choice => choice.Label == "Health") &&
            !session.Prompt.Choices.Any(choice => choice.Label == "Budget"),
            "City menu must contain city information and reports, with economy in the treasury menu.");
        await KeyEvent(Key.Escape);
        Require(host.MusicVolume == Main.DefaultMusicVolume && host.SoundVolume == Main.DefaultSoundVolume &&
            host.MusicVolume < 100 && host.SoundVolume < 100, "Default audio levels were not reduced.");
        Require(!host.CelebrationsEnabled && !host.Effects.Settings.Celebrations,
            "Celebration bursts must be off by default.");
        int skips = host.MusicSkips;
        // Longer than the entire buffer: the render loop cannot help during this stall.
        Thread.Sleep(1500);
        Require(host.MusicSkips == skips, "Music starved while the main thread was stalled.");
        Require(host.GetNode<AudioStreamPlayer>("CityMusic").Playing,
            "Music player stopped while the main thread was stalled.");

        Require(!Descendants(host).OfType<Button>().Any(b =>
            b.Text.StartsWith("F1 ", StringComparison.Ordinal) || b.Text.StartsWith("F3 ", StringComparison.Ordinal) ||
            b.Text.StartsWith("F5 ", StringComparison.Ordinal) || b.Text.StartsWith("F6 ", StringComparison.Ordinal) ||
            b.Text.StartsWith("F7 ", StringComparison.Ordinal) || b.Text.StartsWith("F8 ", StringComparison.Ordinal) ||
            b.Text.StartsWith("F9 ", StringComparison.Ordinal) || b.Text == "ESC GAME"),
            "The keyboard shortcut toolbar must not be displayed.");
        Require(!Descendants(host).OfType<Label>().Any(l => l.Text.StartsWith("Arrows: move", StringComparison.Ordinal)),
            "Header still contains the movement guidance row.");
        var zoomIndicator = Descendants(host).OfType<Label>().Single(l => l.Name == "ZoomIndicator");
        bool perspectiveEnabled = host.Map.PerspectiveEnabled;
        host.Map.SetPerspective(true);
        await Frames();
        float heading = host.Map.Perspective!.Azimuth;
        await KeyEvent(Key.Bracketleft, unicode: '[');
        Require(host.Map.Perspective.Azimuth == Mathf.PosMod(heading - 90, 360),
            "[ must rotate the map 90 degrees left.");
        await KeyEvent(Key.Bracketright, unicode: ']');
        Require(host.Map.Perspective.Azimuth == heading, "] must rotate the map 90 degrees right.");
        var placementBuilding = session.Game.Map.Content.Buildings.Get("Town Well");
        session.PreviewBuilding(placementBuilding);
        await KeyEvent(Key.Bracketright, unicode: ']');
        Require(session.Preview?.Rotation == 1 && session.Preview.Area.Width == placementBuilding.Height &&
            session.Preview.Area.Height == placementBuilding.Width && host.Map.Perspective.Azimuth == heading,
            "During building placement, ] must rotate the building and its footprint without rotating the map.");
        await KeyEvent(Key.Bracketleft, unicode: '[');
        Require(session.Preview?.Rotation == 0 && host.Map.Perspective.Azimuth == heading,
            "During building placement, [ must restore the building orientation without rotating the map.");
        session.ClearSelection();
        host.Map.SetPerspective(perspectiveEnabled);
        await Frames();
        Require(zoomIndicator.Text == session.ZoomLabel &&
            zoomIndicator.GetParent().GetChild<Button>(0).Text == "[-]" &&
            zoomIndicator.GetParent().GetChild<Button>(2).Text == "[+]",
            "Minimap zoom indicator must be flanked by its buttons.");
        var headerLabels = Descendants(host).OfType<Label>()
            .Where(l => l.Name == "AppTitle" || l.Name == "CityNameDisplay" || l.Name == "Calendar" ||
                l.Name == "WeekProgress" || l.Name == "TimeSpeed" || l.Name == "Population" || l.Name == "ViewsHeader" || l.Name == "GuideHeader" || l.Name == "ReportsHeader" || l.Name == "Budget")
            .ToArray();
        float headerCenter = headerLabels[0].GetGlobalRect().GetCenter().Y;
        Require(headerLabels.All(l => Math.Abs(l.GetGlobalRect().GetCenter().Y - headerCenter) < 1),
            "Top header text must share one horizontal row.");
        foreach (var label in headerLabels)
        {
            Node? ancestor = label.GetParent();
            while (ancestor is not null && ancestor is not TerminalFrame) ancestor = ancestor.GetParent();
            var frame = ancestor as TerminalFrame ??
                throw new InvalidOperationException("Top-bar label is missing its terminal frame.");
            float topPadding = label.GlobalPosition.Y - frame.GlobalPosition.Y - frame.InnerBorderBounds.Position.Y;
            float bottomPadding = frame.GlobalPosition.Y + frame.InnerBorderBounds.End.Y - label.GetGlobalRect().End.Y;
            Require(label.VerticalAlignment == VerticalAlignment.Center &&
                Math.Abs(topPadding - bottomPadding) < 1 &&
                frame.VisibleVerticalContentPadding == 4,
                $"{label.Name} must be centered between visible inner borders: top={topPadding}, bottom={bottomPadding}.");
        }
        Require(headerLabels.Select(l => l.GetThemeFontSize("font_size")).Distinct().Count() == 1,
            "All top header text must have the same font size.");
        Require(headerLabels.Single(l => l.Name == "Calendar").Text ==
            MedievalCalendar.DateLabel(session.Game.Year, session.Game.WeekOfYear, session.Game.Config.WeeksPerYear) &&
            Descendants(host).OfType<CalendarIcon>().Single().Feast ==
                MedievalCalendar.Feast(session.Game.WeekOfYear, session.Game.Config.WeeksPerYear),
            "Time header must use ordinal weeks or the corresponding feast name and icon.");
        var speedHeader = headerLabels.Single(label => label.Name == "TimeSpeed");
        var originalSpeed = session.Game.Speed;
        bool originalPaused = session.Game.Paused;
        foreach (var speed in Enum.GetValues<GameSpeed>())
        {
            session.SetSpeed(speed);
            Require(speedHeader.Text == Fmt.SpeedLabel(speed) &&
                speedHeader.GetParent().GetChild(speedHeader.GetParent().GetChildCount() - 1) == speedHeader &&
                speedHeader.HorizontalAlignment == HorizontalAlignment.Right,
                "The rightmost time-header label must show 1x/2x/3x for Slow/Medium/Fast.");
        }
        session.SetSpeed(originalSpeed);
        session.Game.Paused = originalPaused;
        session.Game.Touch();
        var stableHeader = headerLabels.Select(label => (Label: label, Text: label.Text,
            Minimum: label.CustomMinimumSize, Size: label.Size,
            Position: label.GlobalPosition, FontSize: label.GetThemeFontSize("font_size"))).ToArray();
        var dateHeader = headerLabels.Single(label => label.Name == "Calendar");
        var populationHeader = headerLabels.Single(label => label.Name == "Population");
        var budgetHeader = headerLabels.Single(label => label.Name == "Budget");
        foreach (int week in new[] { 1, 2, 17, 25, 44, 52 })
        {
            dateHeader.Text = MedievalCalendar.DateLabel(1289, week);
            headerLabels.Single(label => label.Name == "WeekProgress").Text = week % 2 == 0
                ? "[PAUSED]" : Fmt.WeekBar(0, session.Game.Config.DaysPerWeek);
            populationHeader.Text = week % 2 == 0 ? "POP 9,999,999" : "POP 0";
            budgetHeader.Text = week % 2 == 0 ? "-9,999,999g [-999,999g] | COFFERS EMPTY" : "0g [0g]";
            host.FitHeaderFonts();
            Require(stableHeader.All(slot => slot.Label.CustomMinimumSize == slot.Minimum &&
                slot.Label.GetThemeFontSize("font_size") == slot.FontSize),
                "Changing weeks, feasts, clock state, population or money must not change header slots or font sizing.");
            await Frames();
            Require(stableHeader.All(slot => slot.Label.Size.IsEqualApprox(slot.Size) &&
                slot.Label.GlobalPosition.IsEqualApprox(slot.Position)),
                "Header text must not shift when displayed values change.");
        }
        foreach (var slot in stableHeader) slot.Label.Text = slot.Text;
        host.FitHeaderFonts();
        await Frames();
        int beforeZoom = session.ZoomLevel;
        zoomIndicator.GetParent().GetChild<Button>(2).EmitSignal(Button.SignalName.Pressed);
        Require(session.ZoomLevel == beforeZoom + 1 && zoomIndicator.Text == session.ZoomLabel,
            "Minimap zoom-in button failed.");
        zoomIndicator.GetParent().GetChild<Button>(0).EmitSignal(Button.SignalName.Pressed);
        Require(session.ZoomLevel == beforeZoom, "Minimap zoom-out button failed.");
        foreach (var zone in Zones.Placeable)
        {
            var letter = Descendants(host).OfType<Label>().Single(l => l.Name == $"Demand{Zones.Get(zone).Letter}");
            var bar = letter.GetParent().GetChild<ProgressBar>(1);
            Require(bar.GetThemeStylebox("fill") is StyleBoxFlat fill &&
                letter.GetThemeColor("font_color") == fill.BgColor,
                "Demand letter does not match its bar color.");
        }
        var demandHeading = Descendants(host).OfType<Label>().Single(b => b.Name == "DemandHeading");
        Require(demandHeading.GetThemeFontSize("font_size") >= 13 &&
            demandHeading.GetThemeColor("font_color") == new Color("#70b7ff"),
            "Sidebar headings must be larger and blue.");
        Require(!Descendants(host).Any(n => n.Name == "CollapseCaret") &&
            demandHeading.GetParent().GetParent().GetChild<Control>(1).Visible,
            "Sidebar sections must remain open without collapse carets.");
        Require(Descendants(host).OfType<Label>().Any(l => l.Name == "AppTitle" && l.Text == "TermCity"),
            "Left header must retain the TermCity title.");
        var calendarLabel = Descendants(host).OfType<Label>().Single(l => l.Name == "WeekProgress");
        Require(calendarLabel.Text == "[PAUSED]" &&
            calendarLabel.GetThemeColor("font_color") == new Color("#ffe066"),
            "PAUSED must replace the week progression bar in yellow.");
        Require(calendarLabel.HorizontalAlignment == HorizontalAlignment.Center &&
            Descendants(host).OfType<Label>().Single(l => l.Name == "Calendar").HorizontalAlignment == HorizontalAlignment.Left &&
            Descendants(host).OfType<Label>().Single(l => l.Name == "Population").HorizontalAlignment == HorizontalAlignment.Left,
            "Calendar, week bar and population alignment is incorrect.");
        Require(speedHeader.Text == Fmt.SpeedLabel(originalSpeed),
            "The time header must retain its speed indicator while paused.");
        Require(populationHeader.GetParent() is TerminalFrame { Name: var popFrame } &&
            popFrame == "PopulationHeaderFrame" &&
            budgetHeader.GetParent() is TerminalFrame { Name: var fundsFrame } &&
            fundsFrame == "FundsHeaderFrame" && populationHeader.GetParent() != budgetHeader.GetParent() &&
            !budgetHeader.Text.Contains("TREASURY", StringComparison.Ordinal),
            "POP and funds must use separate frames and omit the TREASURY label.");
        populationHeader.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
        await Frames();
        Require(session.Prompt is null && populationHeader.MouseDefaultCursorShape != Control.CursorShape.PointingHand &&
            !populationHeader.TooltipText.Contains("Open city menu", StringComparison.Ordinal),
            "The POP header must be display-only and not open the city menu.");
        Require(dateHeader.GetParent<HBoxContainer>().GetThemeConstant("separation") == 12,
            "The time section must have increased spacing between its fields.");
        if (session.Game.Config.FullRules)
            Require(budgetHeader.Text.Contains(
                $" [{(session.Game.Finance.Net > 0 ? "+" : "")}{Fmt.CompactMoney(session.Game.Finance.Net)}]",
                StringComparison.Ordinal) && !budgetHeader.Text.Contains("/wk", StringComparison.Ordinal),
                "Weekly gold must use brackets, omit the weekly suffix and show no plus for zero.");
        var reportsHeader = headerLabels.Single(label => label.Name == "ReportsHeader");
        var viewsHeader = headerLabels.Single(label => label.Name == "ViewsHeader");
        viewsHeader.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
        await Frames();
        var viewsBar = Descendants(host).OfType<TerminalFrame>().Single(frame => frame.Name == "ViewsBar");
        Require(viewsBar.IsVisibleInTree() && !viewsBar.JoinAdjacent &&
            viewsBar.GlobalPosition.IsEqualApprox(host.Map.GlobalPosition) &&
            host.Map.GetGlobalRect().Encloses(viewsBar.GetGlobalRect()) &&
            viewsBar.GetChild<ScrollContainer>(0).GetChild(0) is HBoxContainer,
            "VIEWS must open a horizontal terminal icon bar at the map's top left.");
        foreach (var overlay in Enum.GetValues<MapOverlay>())
        {
            var viewButton = Descendants(viewsBar).OfType<Button>().Single(button => button.Name == $"View{overlay}");
            Require(viewButton.Text.Length == 0 &&
                viewButton.GetChild<ConstructionIcon>(0) is { Overlay: var renderedOverlay } &&
                renderedOverlay == overlay,
                "Every VIEWS option must use rendered artwork for its specific overlay, not a text glyph.");
            viewButton.EmitSignal(Button.SignalName.Pressed);
            await Frames();
            Require(session.Overlay == overlay && viewsBar.IsVisibleInTree(),
                "Clicking each view icon must select the overlay without closing the bar.");
            Require(host.Map.Grid.VectorRoads,
                "Selecting an overlay must retain rendered roads instead of terminal road glyphs.");
        }
        await KeyEvent(Key.Escape);
        Require(!viewsBar.IsVisibleInTree() && session.Prompt is null &&
            session.Overlay == MapOverlay.Happiness,
            "Esc must close VIEWS without clearing its overlay or opening Game Menu.");
        viewsHeader.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
        await Frames();
        await Frames();
        host.GetViewport().PushInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = host.Size - Vector2.One,
            GlobalPosition = host.Size - Vector2.One
        }, true);
        host.GetViewport().PushInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = false,
            Position = host.Size - Vector2.One,
            GlobalPosition = host.Size - Vector2.One
        }, true);
        await Frames();
        Require(viewsBar.IsVisibleInTree() && session.Overlay == MapOverlay.Happiness && session.Prompt is null,
            "Clicking away must keep VIEWS active.");
        viewsHeader.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
        await Frames();
        Require(!viewsBar.IsVisibleInTree() && session.Overlay == MapOverlay.Happiness,
            "Toggling VIEWS must close its bar without clearing the overlay.");
        session.SetOverlay(MapOverlay.Off);
        await KeyEvent(Key.O, shift: true);
        Require(session.Overlay == MapOverlay.Happiness,
            "Shift+O must restore the most recently selected overlay.");
        await KeyEvent(Key.O, shift: true);
        Require(session.Overlay == MapOverlay.Off, "Shift+O must switch overlays off.");
        var guideHeader = headerLabels.Single(label => label.Name == "GuideHeader");
        Require(guideHeader.Text == "GUIDE" && guideHeader.GetParent() is TerminalFrame { Name: var guideFrameName } &&
            guideFrameName == "GuideHeaderFrame", "GUIDE must use a separate terminal header frame.");
        guideHeader.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
        await Frames();
        Require(session.Prompt is { Title: "TermCity guide", Tabs.Count: 9 },
            "Clicking GUIDE must open the complete guide.");
        string beforeGuideDismissal = SaveGameStore.Serialize(session.Game);
        var guideDialog = Descendants(host).OfType<TerminalFrame>()
            .Single(frame => frame.Name == "CityDialog" && frame.IsVisibleInTree());
        var outsideGuide = Vector2.One;
        Require(!guideDialog.GetGlobalRect().HasPoint(outsideGuide),
            "Guide dismissal test must click outside its frame.");
        foreach (bool pressed in new[] { true, false })
            host.GetViewport().PushInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = pressed,
                Position = outsideGuide,
                GlobalPosition = outsideGuide,
            }, true);
        await Frames();
        Require(session.Prompt is null && beforeGuideDismissal == SaveGameStore.Serialize(session.Game),
            "Clicking outside Guide must close it without modifying the city.");
        Require(reportsHeader.Text == "REPORTS" &&
            reportsHeader.GetParent() is TerminalFrame { Name: var reportsFrameName } &&
            reportsFrameName == "ReportsHeaderFrame" &&
            reportsHeader.GetParent().GetIndex() == populationHeader.GetParent().GetIndex() + 2 &&
            guideHeader.GetParent().GetIndex() == reportsHeader.GetParent().GetIndex() + 1 &&
            budgetHeader.GetParent().GetIndex() == guideHeader.GetParent().GetIndex() + 1,
            "GUIDE and REPORTS must have separate frames between population and funds.");
        reportsHeader.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
        await Frames();
        Require(session.Prompt?.Title == "Weekly Report",
            "Clicking REPORTS must open the weekly report.");
        var reportDialog = Descendants(host).OfType<TerminalFrame>()
            .Single(frame => frame.Name == "CityDialog" && frame.IsVisibleInTree());
        float reportWidth = Math.Min(720, host.Size.X - host.Map.GetParent<Control>().GlobalPosition.X - 64) + 28;
        Require(Math.Abs(reportDialog.Size.X - reportWidth) < 1,
            $"Reports must use a wider, screen-clamped layout: {reportDialog.Size.X} vs {reportWidth}.");
        Require(!reportDialog.JoinAdjacent &&
            reportDialog.GetThemeStylebox("panel") is StyleBoxFlat { BgColor: var reportBackground } &&
            reportBackground == Colors.Black &&
            Descendants(reportDialog).OfType<MarginContainer>().Single(m => m.Name == "DialogContentMargin")
                .GetThemeConstant("margin_left") == 2,
            "Reports must use shared terminal frames and compact equal padding.");
        VerifyDialogButtonBorders(reportDialog);
        VerifyMapCenteredDialog(host, reportDialog);
        var reportScroll = Descendants(reportDialog).OfType<ScrollContainer>()
            .Single(scroll => scroll.IsVisibleInTree());
        var scrollbar = reportScroll.GetVScrollBar();
        foreach (string scrollbarState in new[] { "scroll", "scroll_focus", "grabber", "grabber_highlight", "grabber_pressed" })
            Require(scrollbar.GetThemeStylebox(scrollbarState) is StyleBoxFlat scrollbarStyle &&
                scrollbarStyle.CornerRadiusTopLeft == 0 && scrollbarStyle.CornerRadiusTopRight == 0 &&
                scrollbarStyle.CornerRadiusBottomLeft == 0 && scrollbarStyle.CornerRadiusBottomRight == 0 &&
                new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }
                    .All(side => scrollbarStyle.GetBorderWidth(side) == 1),
                "Report scrollbars must use square terminal tracks and block thumbs in all states.");
        Require(scrollbar.GetThemeStylebox("grabber_highlight") is StyleBoxFlat { BgColor: var thumbHighlight } &&
            thumbHighlight == Colors.White, "Terminal scrollbar hover must visibly highlight its thumb.");
        reportScroll.ScrollVertical = 80;
        await Frames();
        Require(scrollbar.Visible ? reportScroll.ScrollVertical > 0 : reportScroll.ScrollVertical == 0,
            "Report scrolling must follow whether the current tab overflows.");
        reportScroll.ScrollVertical = 0;
        await Frames();
        var reportTables = Descendants(reportDialog).OfType<GridContainer>()
            .Where(table => table.Name.ToString().StartsWith("ReportTable", StringComparison.Ordinal)).ToArray();
        var reportSections = ReportTables.Build(session);
        var sizeProgress = Descendants(reportDialog).OfType<ProgressBar>().Single(bar => bar.Name == "CitySizeProgress");
        Require(Math.Abs(sizeProgress.Value - CityProgression.SizeProgress(session.Game).Fraction) < 1e-6 &&
            !sizeProgress.ShowPercentage && reportSections.Count == 1 &&
            !reportSections.SelectMany(section => section.Rows).Any(row => row[0].Text == "Unlocks"),
            "Weekly Report must retain city size progress but move milestone unlocks into the guide.");
        var reportHeaders = Descendants(reportDialog).OfType<TerminalFrame>()
            .Where(frame => frame.Name.ToString().StartsWith("ReportSectionHeader", StringComparison.Ordinal)).ToArray();
        Require(reportHeaders.Length == reportSections.Count &&
            reportHeaders.Select((frame, index) => !frame.JoinAdjacent &&
                frame.GetChild<Label>(0).Text == reportSections[index].Title &&
                frame.GetThemeStylebox("panel") is StyleBoxFlat { BgColor: var background } &&
                background == Colors.Black).All(valid => valid),
            "Every report subheader must use the shared closed double-white terminal frame.");
        Require(reportTables.Length == reportSections.Count,
            "Reports must render every summary, zone and milestone as a table.");
        for (int section = 0; section < reportTables.Length; section++)
        {
            var table = reportTables[section];
            var values = reportSections[section].Rows.SelectMany(row => row).ToArray();
            Require(table.Columns == 2 && table.GetChildCount() == values.Length &&
                table.GetThemeConstant("h_separation") == 0 && table.GetThemeConstant("v_separation") == 0,
                "Report label/value cells must form a connected terminal grid.");
            for (int cellIndex = 0; cellIndex < values.Length; cellIndex++)
            {
                var cell = table.GetChild<PanelContainer>(cellIndex);
                var label = cell.GetChild<Label>(0);
                if (cellIndex % 2 == 0)
                    Require(label.GetThemeFont("font").GetStringSize(label.Text,
                        fontSize: label.GetThemeFontSize("font_size")).X <= label.Size.X + 0.01f,
                        $"Report label '{label.Text}' must fit on one line.");
                Require(label.Text == values[cellIndex].Text &&
                    label.GetThemeColor("font_color") == new Color(values[cellIndex].Color.R / 255f,
                        values[cellIndex].Color.G / 255f, values[cellIndex].Color.B / 255f) &&
                    cell.GetThemeStylebox("panel") is StyleBoxFlat style && style.BorderColor == Colors.White &&
                    style.BorderWidthLeft == (cellIndex % 2 == 0 ? 1 : 0) &&
                    style.BorderWidthTop == (cellIndex < 2 ? 1 : 0) &&
                    style.BorderWidthRight == 1 && style.BorderWidthBottom == 1,
                    "Reports must preserve their data/colors and share City sidebar grid styling.");
            }
        }
        Require(Descendants(reportDialog).OfType<Button>().Single(button => button is MnemonicButton)
            .GetParent() is HBoxContainer { Alignment: BoxContainer.AlignmentMode.End },
            "Reports Close action must sit at the bottom right.");
        Require(session.Prompt!.Tabs!.Select(tab => tab.Title).SequenceEqual(
            new[] { "Overview", "Population", "Economy", "Services", "Growth", "Food & Seasons", "Events" }),
            "Weekly Report must expose its seven reporting tabs.");
        for (int tab = 1; tab < 7; tab++)
        {
            await KeyEvent(Key.Right);
            Require(session.Prompt?.ActiveTab == tab && session.Prompt.Title == "Weekly Report",
                "Arrow keys must select each report tab without changing the city.");
            var tables = Descendants(reportDialog).OfType<GridContainer>()
                .Where(table => table.Name.ToString().StartsWith("ReportTable", StringComparison.Ordinal)).ToArray();
            var groups = ReportTables.Build(session);
            Require(tables.Length == groups.Count, "Every report tab must render its own table groups.");
            for (int group = 0; group < groups.Count; group++)
                Require(Descendants(tables[group]).OfType<Label>().Select(label => label.Text)
                    .SequenceEqual(groups[group].Rows.SelectMany(row => row).Select(cell => cell.Text)),
                    "Report tab tables must display their actual data.");
            Require(!Descendants(reportDialog).OfType<ProgressBar>().Any(bar => bar.Name == "CitySizeProgress"),
                "City-size progress belongs only on the Overview tab.");
            VerifyMapCenteredDialog(host, reportDialog);
            VerifyDialogButtonBorders(reportDialog);
            if (tab == 3 && session.Game.Config.FullRules)
            {
                var serviceScroll = Descendants(reportDialog).OfType<ScrollContainer>()
                    .Single(scroll => scroll.IsVisibleInTree());
                serviceScroll.ScrollVertical = 80;
                await Frames();
                Require(serviceScroll.ScrollVertical > 0, "The detailed Services report must remain scrollable.");
            }
        }
        await KeyEvent(Key.Right);
        Require(session.Prompt?.ActiveTab == 0, "Report tabs must wrap back to Overview.");
        await KeyEvent(Key.Escape);
        Require(!Descendants(host).OfType<PauseDialog>().Any() &&
            !Descendants(host).OfType<PauseStage>().Any(),
            "Paused gameplay must not create an overlay or animation.");
        var pauseHeaderBounds = headerLabels.Select(label => label.GetGlobalRect()).ToArray();
        string beforePause = SaveGameStore.Serialize(session.Game);
        int beforePauseRebuilds = host.Map.Grid.Rebuilds;
        host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
        host._Process(0.5);
        Require(beforePause == SaveGameStore.Serialize(session.Game) && beforePauseRebuilds == host.Map.Grid.Rebuilds,
            "Paused updates must not advance gameplay or rebuild the map.");
        host.GetWindow().EmitSignal(Window.SignalName.FocusExited);
        host._Process(1);
        Require(calendarLabel.Text == "[PAUSED]", "Focus changes must retain the paused header.");
        host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
        await KeyEvent(Key.Space);
        Require(!session.Game.Paused &&
            calendarLabel.Text == Fmt.WeekBar(session.Game.Day, session.Game.Config.DaysPerWeek) &&
            calendarLabel.GetThemeColor("font_color") == new Color("#70d7ff"),
            "Space must resume and restore the week progression bar.");
        Require(headerLabels.Select(label => label.GetGlobalRect()).SequenceEqual(pauseHeaderBounds),
            "Pause/resume must not shift the header layout.");
        foreach (var dialogKey in new[] { Key.F1, Key.F3, Key.F6, Key.F7, Key.F8, Key.Escape })
        {
            await KeyEvent(dialogKey);
            Require(session.Prompt is not null && !session.Game.Paused &&
                calendarLabel.Text == "[PAUSED]" && calendarLabel.GetThemeColor("font_color") == new Color("#ffe066"),
                $"{dialogKey} must display the paused indicator while its dialog suspends a running game.");
            double suspendedDay = session.Game.ElapsedDays;
            host._Process(0.25);
            Require(session.Game.ElapsedDays == suspendedDay, "Dialog must not advance the calendar.");
            var pausedPrompt = session.Prompt;
            await KeyEvent(Key.Space);
            Require(!session.ClockSuspended && ReferenceEquals(session.Prompt, pausedPrompt) &&
                calendarLabel.Text != "[PAUSED]",
                "Space must explicitly resume time without closing the current dialog.");
            await KeyEvent(Key.Space);
            Require(session.ClockSuspended && session.Game.Paused && ReferenceEquals(session.Prompt, pausedPrompt),
                "Space must pause from a dialog without activating its focused action.");
            session.ClosePrompt();
            session.TogglePause();
            await Frames();
            Require(!session.Game.Paused &&
                calendarLabel.Text == Fmt.WeekBar(session.Game.Day, session.Game.Config.DaysPerWeek),
                "Closing a dialog must restore the running progression indicator.");
        }
        session.PreviewRoad();
        await Frames();
        Require(calendarLabel.Text == "[PAUSED]" && !session.Game.Paused,
            "Placement previews must also show the suspended-clock indicator.");
        await KeyEvent(Key.P);
        Require(!session.ClockSuspended && session.Preview is not null,
            "P must resume while preserving a placement preview.");
        await KeyEvent(Key.P);
        Require(session.ClockSuspended && session.Preview is not null,
            "P must pause again without cancelling placement.");
        session.CancelPreview();
        session.TogglePause();
        await Frames();
        await KeyEvent(Key.P);
        Require(session.Game.Paused && calendarLabel.Text == "[PAUSED]",
            "P must pause without obstructing gameplay.");
        await KeyEvent(Key.Escape);
        Require(session.Game.Paused && session.Prompt?.Title == "GAME MENU",
            "Escape while paused must open Game Menu without unpausing.");
        await KeyEvent(Key.Escape);
        session.ShowSessionMenu();
        await Frames();
        Require(calendarLabel.Text == "[PAUSED]", "Other menus must retain the paused header.");
        host._Process(0.5);
        await KeyEvent(Key.Escape);
        Require(session.Game.Paused && session.Prompt is null &&
            !Descendants(host).OfType<PauseDialog>().Any(),
            "Closing a menu while paused must leave the map unobstructed.");
        session.ShowMilestone(0);
        await Frames();
        var growthDialog = Descendants(host).OfType<TerminalFrame>()
            .Single(frame => frame.Name == "CityDialog" && frame.IsVisibleInTree());
        float growthWidth = Math.Min(720,
            host.Size.X - host.Map.GetParent<Control>().GlobalPosition.X - 64) + 28;
        Require(Math.Abs(growthDialog.Size.X - growthWidth) < 1,
            "City Grew! must have a wider, screen-clamped layout.");
        VerifyMapCenteredDialog(host, growthDialog);
        VerifyDialogButtonBorders(growthDialog);
        var growthTitle = Descendants(growthDialog).OfType<Label>()
            .Single(label => label.Text == "Your City Grew!");
        Require(growthTitle.HorizontalAlignment == HorizontalAlignment.Center &&
            growthTitle.Modulate == new Color("#ffe066"),
            "Growth title must be centered and yellow.");
        Require(Descendants(growthDialog).OfType<TerminalFrame>().Single(frame => frame.Name == "ReportSectionHeader0")
            .GetChild<Label>(0).Modulate == new Color("#88ee99") &&
            Descendants(growthDialog).OfType<Label>().Single(label => label.Name == "NewServicesHeader").Text == "New Services",
            "New city size must be green, with a New Services header before unlocked buildings.");
        var citySizeHeading = Descendants(growthDialog).OfType<TerminalFrame>()
            .Single(frame => frame.Name == "ReportSectionHeader0").GetChild<Label>(0);
        var newServicesHeading = Descendants(growthDialog).OfType<Label>().Single(label => label.Name == "NewServicesHeader");
        Require(citySizeHeading.HorizontalAlignment == HorizontalAlignment.Center &&
            citySizeHeading.GetThemeFontSize("font_size") == 24 &&
            newServicesHeading.GetThemeFontSize("font_size") == 24,
            "City size and New Services must use matching enlarged 24-point headings.");
        var unlockedBuildings = session.Game.Map.Content.Buildings.Where(building => building.PlayerPlaceable &&
            building.MinPopulation > 0 && CityProgression.RequiredPopulation(building) == 100).ToArray();
        var milestoneArt = Descendants(growthDialog).OfType<Label>().Where(label => label.Name == "MilestoneBuildingArt").ToArray();
        Require(milestoneArt.Length == unlockedBuildings.Length,
            "Every newly unlocked building must show its artwork.");
        foreach (var building in unlockedBuildings)
        {
            var art = milestoneArt.Single(label => label.TooltipText == building.Name);
            Require(art.Text == (building.FootprintArt.Count > 0
                    ? string.Join("\n", building.FootprintArt) : building.Glyphs[0]) &&
                art.GetParent().GetParent<HBoxContainer>().GetChild<Control>(0).GetGlobalRect().End.X <= art.GlobalPosition.X,
                "Building artwork must match its footprint or icon and sit to the right of its feature table.");
        }
        var growthScroll = Descendants(growthDialog).OfType<ScrollContainer>().Single(control => control.IsVisibleInTree());
        Require(growthDialog.ClipContents && growthScroll.ClipContents,
            "Growth dialog and scrolling viewport must clip overflowing frames and text.");
        foreach (string scrollbarState in new[] { "scroll", "grabber", "grabber_highlight", "grabber_pressed" })
            Require(growthScroll.GetVScrollBar().GetThemeStylebox(scrollbarState) is StyleBoxFlat barStyle &&
                barStyle.CornerRadiusTopLeft == 0 && barStyle.CornerRadiusTopRight == 0 &&
                barStyle.BorderWidthLeft == 1 && barStyle.BorderWidthRight == 1,
                "Growth scrollbar must use the shared square terminal track and thumb styling.");
        Require(session.Prompt!.Title == "Your City Grew!" && session.Game.Paused &&
            Descendants(growthDialog).OfType<GridContainer>().Count() == MilestoneReport.Build(session.Game, 0).Groups.Count,
            "Milestone dialog must pause and render all newly unlocked building feature tables.");
        var previousMilestone = Descendants(growthDialog).OfType<Button>().Single(button => button.Text.StartsWith("Previous", StringComparison.Ordinal));
        Require(previousMilestone.Disabled, "Previous must be disabled at the first milestone.");
        session.SelectPrompt(1);
        await Frames();
        Require(session.Prompt?.ActiveTab == 1 && session.Prompt.Title == "Your City Grew!",
            "Next must browse the next milestone without leaving the announcement.");
        session.SelectPrompt(0);
        await Frames();
        Require(session.Prompt?.ActiveTab == 0, "Previous must return to the newly reached milestone.");
        await KeyEvent(Key.Escape);

        var nameLabel = Descendants(host).OfType<Label>().Single(l => l.Name == "CityNameDisplay");
        var editor = Descendants(host).OfType<LineEdit>().Single(l => l.Name == "CityNameEditor");
        string originalName = session.Game.CityName;
        Require(nameLabel.HorizontalAlignment == HorizontalAlignment.Right &&
            CityGame.IsValidCityName(originalName), "Generated city name must be valid and right-aligned.");
        nameLabel.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
        await Frames();
        Require(!editor.Visible && !Descendants(host).Any(n => n.Name == "NameCursor"),
            "A single click must not show a name cursor or start editing.");
        var titleLabel = Descendants(host).OfType<Label>().Single(l => l.Name == "AppTitle");
        var titleBounds = titleLabel.GetGlobalRect();
        var headerBounds = nameLabel.GetParent().GetParent().GetParent<Control>().GetGlobalRect();
        var font = editor.GetThemeFont("font");
        int nameSize = nameLabel.GetThemeFontSize("font_size");
        float characterWidth = font.GetStringSize("M", fontSize: nameSize).X;
        session.Game.Paused = false;
        nameLabel.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, DoubleClick = true });
        await Frames();
        Require(editor.IsVisibleInTree() && editor.HasFocus() && editor.CaretBlink,
            "Clicking the city name did not activate its blinking-caret editor.");
        Require(session.Game.Paused && titleLabel.GetGlobalRect() == titleBounds &&
            nameLabel.GetParent().GetParent().GetParent<Control>().GetGlobalRect() == headerBounds &&
            editor.Alignment == HorizontalAlignment.Right &&
            Math.Abs(editor.GetGlobalRect().End.X - nameLabel.GetGlobalRect().End.X) < 1 &&
            editor.GetThemeConstant("caret_width") >= characterWidth &&
            editor.GetThemeStylebox("normal").GetMargin(Side.Right) >= characterWidth &&
            editor.GetThemeStylebox("focus").GetMargin(Side.Right) >= characterWidth,
            "Renaming must pause and keep its right-aligned block caret without changing title/header geometry.");
        if (DisplayServer.GetName() != "headless")
        {
            editor.CaretBlink = false;
            await Frames();
            await RenderedFrame(host);
            using var image = host.GetViewport().GetTexture().GetImage();
            var scale = new Vector2(image.GetWidth() / host.Size.X, image.GetHeight() / host.Size.Y);
            float caretWidth = editor.GetThemeConstant("caret_width");
            float caretHeight = font.GetHeight(nameSize);
            var caretCell = new Rect2(editor.GlobalPosition +
                new Vector2(editor.Size.X - caretWidth + 2, (editor.Size.Y - caretHeight) / 2 + 2),
                new Vector2(caretWidth - 4, caretHeight - 4));
            var caretColor = editor.GetThemeColor("caret_color");
            int solid = 0, pixels = 0;
            for (int y = (int)Math.Ceiling(caretCell.Position.Y * scale.Y); y < caretCell.End.Y * scale.Y; y++)
                for (int x = (int)Math.Ceiling(caretCell.Position.X * scale.X); x < caretCell.End.X * scale.X; x++)
                {
                    var pixel = image.GetPixel(x, y);
                    if (Math.Abs(pixel.R - caretColor.R) < 0.02f &&
                        Math.Abs(pixel.G - caretColor.G) < 0.02f && Math.Abs(pixel.B - caretColor.B) < 0.02f) solid++;
                    pixels++;
                }
            Require(pixels > 0 && solid >= pixels * 0.95,
                $"Rename caret is not a solid full-cell block: {solid}/{pixels} pixels.");
            editor.CaretBlink = true;
        }
        await KeyEvent(Key.A, ctrl: true);
        int mapVersionBeforeName = session.Game.MapVersion;
        foreach (char character in "Rivertown") await KeyEvent(Key.None, unicode: character);
        Require(editor.Text == "Rivertown" && session.Game.MapVersion == mapVersionBeforeName,
            $"Name typing failed: text='{editor.Text}', map version {mapVersionBeforeName}->{session.Game.MapVersion}, focus={editor.HasFocus()}.");
        int caretBeforeDelete = editor.CaretColumn;
        float editorStart = editor.GlobalPosition.X;
        await KeyEvent(OS.HasFeature("macos") ? Key.Delete : Key.Backspace);
        Require(editor.Text == "Rivertow" && editor.CaretColumn == caretBeforeDelete - 1 &&
            Math.Abs(editor.GlobalPosition.X - editorStart) < 0.01,
            "Backspacing must remove the rightmost character without moving the right-aligned edit region.");
        await KeyEvent(Key.A, ctrl: true);
        foreach (char character in "12345678901234567") await KeyEvent(Key.None, unicode: character);
        Require(editor.Text == "1234567890123456", "City-name editor exceeded 16 characters.");
        await KeyEvent(Key.Enter);
        Require(session.Game.CityName == "1234567890123456" && !editor.Visible &&
            SaveGameStore.Deserialize(SaveGameStore.Serialize(session.Game)).CityName == session.Game.CityName,
            "Committed city name did not persist.");
        Require(!session.Game.Paused, "Name commit did not restore the running clock.");
        session.Game.Paused = true;
        nameLabel.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, DoubleClick = true });
        await Frames();
        await KeyEvent(Key.A, ctrl: true);
        await KeyEvent(Key.None, unicode: 'X');
        await KeyEvent(Key.Escape);
        Require(session.Game.CityName == "1234567890123456", "Escape did not cancel name editing.");
        session.Game.RenameCity(originalName);
        Require(!Descendants(host).OfType<Label>().Any(l => l.Text == "CELL / SELECTION"),
            "Sidebar still contains duplicate cell inspection.");
        var originalCursor = session.Cursor;
        session.BeginDrag(new Pos(12, 12));
        session.UpdateDrag(new Pos(14, 12));
        session.EndSelection();
        var status = Descendants(host).OfType<Label>().Single(l => l.Name == "Status");
        var petition = Descendants(host).OfType<Label>().Single(label => label.Name == "PeasantStatus");
        string originalStatusText = status.Text, originalPetitionText = petition.Text;
        status.Text = "Cell";
        float petitionCharacterWidth = petition.GetThemeFont("font")
            .GetStringSize("M", fontSize: petition.GetThemeFontSize("font_size")).X;
        petition.Text = new string('M', (int)(host.Size.X * 0.65f / petitionCharacterWidth));
        host.FitStatusRow();
        float petitionTextWidth = petition.GetThemeFont("font")
            .GetStringSize(petition.Text, fontSize: petition.GetThemeFontSize("font_size")).X;
        Require(petition.CustomMinimumSize.X > host.Size.X / 2 &&
            petition.CustomMinimumSize.X >= petitionTextWidth,
            "Petitions must reserve their full text width when available, even beyond half the screen.");
        status.Text = originalStatusText;
        petition.Text = originalPetitionText;
        var savePathLabel = Descendants(host).OfType<Label>().Single(l => l.Name == "QuickSavePath");
        Require(!Descendants(host).Any(node => node.Name == "WeeklyStatus"),
            "Recurring weekly updates must not have a bottom-bar display.");
        void CheckStatusPath()
        {
            if (session.Prompt?.IsSaveDialog != true && session.Prompt?.Title != "GAME MENU")
            {
                Require(!savePathLabel.IsVisibleInTree(), "Quick-save path must be hidden outside game menus and save dialogs.");
                return;
            }
            Require(savePathLabel.IsVisibleInTree() &&
                savePathLabel.Text == $"Quick-save: {session.SaveDisplayPath}" &&
                savePathLabel.TooltipText == Path.GetFullPath(session.SavePath) &&
                savePathLabel.HorizontalAlignment == HorizontalAlignment.Right &&
                savePathLabel.GetParent() == status.GetParent() &&
                Math.Abs(savePathLabel.GlobalPosition.Y - status.GlobalPosition.Y) < 1 &&
                Math.Abs(savePathLabel.GetGlobalRect().End.X - host.Size.X) < 1 &&
                Math.Abs(savePathLabel.GetParent<Control>().GetGlobalRect().End.Y - host.Size.Y) < 1 &&
                savePathLabel.GetParent<Control>().GetGlobalRect().Encloses(savePathLabel.GetGlobalRect()) &&
                status.GetGlobalRect().End.X <= savePathLabel.GlobalPosition.X &&
                savePathLabel.Size.X <= host.Size.X / 2 + 1 &&
                status.Size.X >= host.Size.X / 2 - 8,
                $"Quick-save path must remain at the bottom right beside cell information without overlap or overflow. " +
                $"Window={host.Size}, status={status.GetGlobalRect()}, path={savePathLabel.GetGlobalRect()}, " +
                $"row={savePathLabel.GetParent<Control>().GetGlobalRect()}, text='{savePathLabel.Text}', " +
                $"tooltip='{savePathLabel.TooltipText}'.");
        }
        Require(!savePathLabel.IsVisibleInTree(), "Quick-save path must be hidden during gameplay.");
        Require(status.Text.StartsWith(CellInspector.Summary(session.Game, session.Cursor), StringComparison.Ordinal) &&
            status.Text.Contains("Selection 3x1 (3 cells)", StringComparison.Ordinal),
            "Multi-cell selection details are not beside the cell details in the bottom status line.");
        session.ClearSelection();
        session.PlaceCursor(originalCursor);

        await KeyEvent(Key.F10);
        Require(session.Prompt is null && !host.MusicEnabled && host.SoundEnabled &&
            host.GetNode<AudioStreamPlayer>("CityMusic").StreamPaused &&
            host.MusicVolume == Main.DefaultMusicVolume && host.SoundVolume == Main.DefaultSoundVolume,
            "F10 must mute only music without changing volumes or opening a menu.");
        await KeyEvent(Key.F10);
        Require(host.MusicEnabled && !host.GetNode<AudioStreamPlayer>("CityMusic").StreamPaused,
            "F10 did not unmute music.");
        OpenGameMenu(host);
        await Frames();
        Require(session.Prompt?.Title == "GAME MENU", "Native Esc while running did not open the game menu.");
        Require(session.Prompt!.Choices.Any(c => c.Label == "FONT SIZE") &&
            session.Prompt.Choices.Any(c => c.Label == "RESIZE SIDEBAR") &&
            !session.Prompt.Choices.Any(c => c.Label is "AUDIO CONTROLS" or "CITY MANAGEMENT") &&
            !session.Prompt.Choices.Any(c => c.Label is "Budget" or "Report" or "Undo"),
            "Esc menu must focus on configuration rather than city actions.");
        CheckStatusPath();
        Require(status.Text == $"Seed {session.Game.Config.Seed} | {session.Game.Map.Width}x{session.Game.Map.Height}" &&
            Math.Abs(status.GlobalPosition.X) < 1,
            "Game menu seed and map dimensions must appear at the bottom left.");
        Require(!session.Prompt!.Choices.Any(choice => choice.Label is "Back to game" or "Quit"),
            "Game menu must replace Back to game and Quit rows with footer actions.");
        var menuQuit = Descendants(host).OfType<Button>().Single(button => button.Name == "GameMenuQuit");
        var menuCancel = Descendants(host).OfType<Button>().Single(button => button.Name == "GameMenuCancel");
        Require(menuQuit.GetThemeColor("font_color") == new Color("#ff7777") &&
            menuCancel.Alignment == HorizontalAlignment.Center && menuQuit.Size == menuCancel.Size,
            "Game menu actions must match submenu action sizes, with red Quit text and centered Cancel.");
        Require(host.GetViewport().GuiGetFocusOwner() == menuCancel &&
            menuCancel.GetThemeStylebox("focus") is StyleBoxFlat { BgColor: var cancelFocus } &&
            cancelFocus == new Color("#18232c"),
            "Game menu Cancel must start focused with a subtle blue-gray highlight.");
        Require(menuQuit.Text == "QUIT" && menuCancel.Text == "CANCEL" &&
            menuQuit is MnemonicButton { UnderlineIndex: 0 } &&
            menuCancel is MnemonicButton { UnderlineIndex: 0 } &&
            session.Prompt.Choices.All(choice => choice.Label == choice.Label.ToUpperInvariant() && choice.Cells is null) &&
            session.Prompt.Text.Length == 0,
            "Game menu must use uppercase title/actions/options without descriptions.");
        host.GetViewport().PushInput(new InputEventMouseMotion
        {
            Position = Vector2.Zero,
            GlobalPosition = Vector2.Zero,
        }, true);
        await Frames();
        Require(host.GetViewport().GuiGetFocusOwner() == menuCancel,
            "Moving the pointer away must retain Cancel focus.");
        var gameDialog = Descendants(host).OfType<PanelContainer>()
            .Single(panel => panel.Name == "CityDialog" && panel.IsVisibleInTree());
        var menuFocusControls = Descendants(gameDialog).OfType<Control>()
            .Where(control => control.IsVisibleInTree() && control is Button or HSlider).ToArray();
        Require(menuFocusControls.Length == 16, "Game menu must expose all fourteen buttons and two sliders.");
        foreach (var expectedControl in menuFocusControls)
        {
            await KeyEvent(Key.Tab);
            Require(host.GetViewport().GuiGetFocusOwner() == expectedControl,
                $"Tab must focus game menu control {expectedControl.Name} in display order.");
            if (expectedControl is Button focusedButton)
                Require(focusedButton.GetThemeStylebox("focus") is StyleBoxFlat focusedStyle &&
                    focusedStyle.BgColor == new Color("#18232c") &&
                    focusedStyle.BgColor != ((StyleBoxFlat)focusedButton.GetThemeStylebox("normal")).BgColor,
                    "Every focused game menu button must have a visible blue-gray highlight.");
        }
        for (int index = menuFocusControls.Length - 2; index >= 0; index--)
        {
            await KeyEvent(Key.Tab, shift: true);
            Require(host.GetViewport().GuiGetFocusOwner() == menuFocusControls[index],
                "Shift+Tab must traverse game menu controls in reverse.");
        }
        await KeyEvent(Key.Tab, shift: true);
        Require(host.GetViewport().GuiGetFocusOwner() == menuCancel,
            "Shift+Tab must wrap to Cancel without leaving the menu.");
        foreach (var expectedControl in menuFocusControls)
        {
            await KeyEvent(Key.Down);
            Require(host.GetViewport().GuiGetFocusOwner() == expectedControl,
                $"Down must navigate to {expectedControl.Name}, including sliders.");
        }
        var menuMusicSlider = menuFocusControls.OfType<HSlider>().Single(slider => slider.Name == "MusicVolume");
        menuMusicSlider.GrabFocus();
        double menuVolume = host.MusicVolume;
        await KeyEvent(Key.Right);
        Require(host.GetViewport().GuiGetFocusOwner() == menuMusicSlider && host.MusicVolume == menuVolume + 1,
            "Right must adjust a focused slider instead of moving focus.");
        await KeyEvent(Key.Left);
        Require(host.MusicVolume == menuVolume, "Left must reverse the slider adjustment.");
        await KeyEvent(Key.Enter);
        Require(session.Prompt?.Title == "GAME MENU" && host.GetViewport().GuiGetFocusOwner() == menuMusicSlider,
            "Enter on a slider must not activate an unrelated menu option.");
        await KeyEvent(Key.Up);
        Require(host.GetViewport().GuiGetFocusOwner()?.Name == "SidebarSizePlus",
            "Up must navigate out of the music slider.");
        menuCancel.GrabFocus();
        var newMap = Descendants(gameDialog).OfType<Button>().Single(button => button.Name == "GameMenuNewMap");
        Require(newMap.Text == "NEW MAP" && newMap.GetThemeColor("font_color") == new Color("#88ee99") &&
            newMap.GetIndex() == 1 && newMap.GlobalPosition.X < menuQuit.GlobalPosition.X &&
            Math.Abs(newMap.GetGlobalRect().End.Y - menuCancel.GetGlobalRect().End.Y) < 1,
            "NEW MAP must stay green to the right of SAVE in the game menu footer.");
        var footerSave = Descendants(gameDialog).OfType<Button>().Single(button => button.Name == "GameMenuSave");
        Require(footerSave.GetIndex() == 0 && footerSave.GlobalPosition.X < menuQuit.GlobalPosition.X &&
            Math.Abs(footerSave.GetGlobalRect().End.Y - menuCancel.GetGlobalRect().End.Y) < 1 &&
            footerSave.GetGlobalRect().End.X <= newMap.GlobalPosition.X,
            "SAVE must occupy the bottom left opposite QUIT and CANCEL.");
        Require(Descendants(gameDialog).OfType<Label>().Single(label => label.Text == "GAME MENU")
            .HorizontalAlignment == HorizontalAlignment.Center, "GAME MENU header must be centered.");
        await KeyEvent(Key.Enter);
        Require(session.Prompt is null, "Game menu Cancel must close the dialog without quitting.");
        Require(!savePathLabel.IsVisibleInTree() &&
            status.Text.StartsWith(session.HasCursor ? CellInspector.Summary(session.Game, session.Cursor) : "Press F1 for HELP",
                StringComparison.Ordinal),
            "Closing the game menu must hide metadata and restore normal gameplay status.");
        OpenGameMenu(host);
        await Frames();
        await KeyEvent(Key.C);
        Require(session.Prompt is null, "C must close the game menu without quitting.");
        OpenGameMenu(host);
        await Frames();
        await KeyEvent(Key.Q);
        await Frames();
        Require(session.Prompt is { Title: "Quit", Footer: null }, "Save confirmation still includes the quick-save path.");
        CheckStatusPath();
        var pathPointer = savePathLabel.GetGlobalRect().GetCenter();
        host.GetViewport().PushInput(new InputEventMouseMotion
        {
            Position = pathPointer,
            GlobalPosition = pathPointer,
        }, true);
        await Frames();
        var saveDialog = Descendants(host).OfType<PanelContainer>().Single(panel => panel.Name == "CityDialog");
        VerifyMapCenteredDialog(host, saveDialog);
        VerifyDialogButtonBorders(saveDialog);
        var saveActions = Descendants(saveDialog).OfType<Button>().Where(button => button.IsVisibleInTree()).ToArray();
        Require(saveActions.All(button => button.Alignment == HorizontalAlignment.Center) &&
            saveActions.Single(button => button.Text.StartsWith("Save and quit", StringComparison.Ordinal))
                .GetThemeColor("font_color") == new Color("#88ee99") &&
            saveActions.Single(button => button.Text.StartsWith("Quit without saving", StringComparison.Ordinal))
                .GetThemeColor("font_color") == new Color("#ff7777") &&
            !Descendants(saveDialog).OfType<Label>().Any(label => label.Text == session.Prompt!.Text),
            "Save confirmation must omit descriptive text, center actions, and use green save/red discard text.");
        Require(saveDialog is TerminalFrame { JoinLeft: false, JoinRight: false, JoinTop: false, JoinBottom: false } &&
            saveDialog.GetThemeStylebox("panel") is StyleBoxFlat { BgColor: var saveBackground } &&
            saveBackground == Colors.Black &&
            new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }
                .All(side => saveDialog.GetThemeStylebox("panel").GetContentMargin(side) == TerminalFrame.Inset),
            "Save confirmation must use the shared black, double-white terminal frame and inset.");
        Require(saveDialog.GetParent<Control>().TooltipText == savePathLabel.TooltipText,
            "The quick-save path tooltip is blocked while a save confirmation is open.");
        await KeyEvent(Key.Escape);
        CheckStatusPath();
        var cityMenu = Descendants(host).OfType<PanelContainer>().Single(p => p.Name == "CityDialog" && p.IsVisibleInTree());
        float largeMenuHeight = cityMenu.Size.Y;
        float gameMenuWidth = cityMenu.Size.X;
        var menuButtons = Descendants(cityMenu).OfType<MnemonicButton>()
            .Where(b => b.IsVisibleInTree() && b.Name != "GameMenuQuit" && b.Name != "GameMenuCancel" &&
                b.Name != "GameMenuSave").ToArray();
        VerifyDialogButtonBorders(cityMenu);
        Require(menuButtons.All(b => b.Alignment == HorizontalAlignment.Left),
            "Menu choices must be left-aligned.");
        var menuPrompt = session.Prompt ?? throw new InvalidOperationException("City menu prompt is missing.");
        Require(TextTable.ForPrompt(menuPrompt) is null &&
            !Descendants(cityMenu).OfType<Label>().Any(label => label.Text == "Configure display, sound and music."),
            "Game menu must omit its description and description columns.");
        float expectedMenuWidth = Math.Min(420,
            host.Size.X - host.Map.GetParent<Control>().GlobalPosition.X - 64) + 28;
        Require(Math.Abs(cityMenu.Size.X - expectedMenuWidth) < 1,
            $"Game menu must retain its compact width: {cityMenu.Size.X} vs {expectedMenuWidth}.");
        Require(!Descendants(cityMenu).Any(n => n.Name == "GameMenuHeaders") &&
            Descendants(cityMenu).OfType<TerminalFrame>().All(frame => !frame.JoinAdjacent &&
                !frame.JoinLeft && !frame.JoinRight && !frame.JoinTop && !frame.JoinBottom),
            "Game menu must omit the column-header frame and retain closed, unjoined double borders.");
        var menuMargin = Descendants(cityMenu).OfType<MarginContainer>()
            .Single(m => m.Name == "DialogContentMargin");
        Require(new[] { "left", "top", "right", "bottom" }
            .All(side => menuMargin.GetThemeConstant($"margin_{side}") == 2),
            "Game menu must use matching two-pixel extra padding on every side.");
        var menuScroll = Descendants(menuMargin).OfType<ScrollContainer>().Single(s => s.IsVisibleInTree());
        var menuContent = menuScroll.GetChild<Control>(0).GetChild<Control>(0);
        var menuRect = cityMenu.GetGlobalRect();
        var contentRect = menuContent.GetGlobalRect();
        Require(new[] { contentRect.Position.X - menuRect.Position.X,
            contentRect.Position.Y - menuRect.Position.Y, menuRect.End.X - contentRect.End.X,
            menuRect.End.Y - contentRect.End.Y }.All(padding => Math.Abs(padding - TerminalFrame.Inset - 2) < 1),
            "Game menu content must have equal visible top, bottom, left and right padding.");
        VerifyMapCenteredDialog(host, cityMenu);
        Require(menuButtons.All(button => button is MnemonicButton mnemonic &&
            mnemonic.UnderlineIndex >= 0 && mnemonic.UnderlineIndex < mnemonic.Text.Length &&
            !char.IsDigit(mnemonic.Text[0])), "Menu options must have underlined letters instead of number prefixes.");
        var trappedMenu = session.Prompt;
        var trappedCursor = session.Cursor;
        int trappedZoom = session.ZoomLevel;
        bool trappedDebug = session.InputDebug;
        await KeyEvent(Key.Key9);
        await KeyEvent(Key.F12);
        await KeyEvent(Key.Equal, ctrl: true);
        Require(ReferenceEquals(session.Prompt, trappedMenu) && session.Cursor == trappedCursor &&
            session.ZoomLevel == trappedZoom && session.InputDebug == trappedDebug,
            "Unassigned menu keys leaked into gameplay or activated numbered options.");
        Require(!session.Prompt!.Choices.Any(choice => choice.Label is "Music" or "Sound"),
            "Music and Sound rows must be omitted from the game menu.");
        Require(!session.Prompt!.Choices.Any(choice => choice.Label is "Effects" or "Celebrations"),
            "Game menu must omit Effects and Celebrations rows.");
        var musicVolume = Descendants(host).OfType<HSlider>().Single(s => s.Name == "MusicVolume");
        var soundVolume = Descendants(host).OfType<HSlider>().Single(s => s.Name == "SoundVolume");
        var musicMute = Descendants(host).OfType<Button>().Single(b => b.Name == "MusicMute");
        var soundMute = Descendants(host).OfType<Button>().Single(b => b.Name == "SoundMute");
        var audioModal = Descendants(host).OfType<PanelContainer>()
            .Single(p => p.Name == "CityDialog" && p.IsVisibleInTree());
        VerifyDialogButtonBorders(audioModal);
        Require(session.Prompt?.Title == "GAME MENU" && session.Prompt.Text.Length == 0 &&
            Math.Abs(audioModal.Size.X - gameMenuWidth) < 1 &&
            Descendants(audioModal).OfType<Label>().Any(label => label.Text == "MUSIC") &&
            Descendants(audioModal).OfType<Label>().Any(label => label.Text == "SOUND"),
            "Audio controls must appear directly in the game menu without increasing its width.");
        foreach (var frame in Descendants(audioModal).OfType<PanelContainer>().Where(frame =>
            frame.Name == "MusicControls" || frame.Name == "SoundControls"))
            Require(frame.GetThemeStylebox("panel") is StyleBoxFlat style &&
                style.BorderColor == Colors.White &&
                new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }.All(side =>
                    style.GetBorderWidth(side) == 1 && style.GetContentMargin(side) == 4),
                "Audio channels must have tightly padded single-white borders.");
        Require(audioModal.Position.X >= 0 && audioModal.Position.Y >= 0 &&
            audioModal.Position.X + audioModal.Size.X <= host.Size.X &&
            audioModal.Position.Y + audioModal.Size.Y <= host.Size.Y,
            "Audio controls extend outside the window.");
        Require(audioModal.GetGlobalRect().Encloses(musicMute.GetGlobalRect()) &&
            audioModal.GetGlobalRect().Encloses(soundMute.GetGlobalRect()),
            "Audio mute buttons are clipped.");
        Require(musicVolume.GetParent().GetParent() == musicMute.GetParent() &&
            soundVolume.GetParent().GetParent() == soundMute.GetParent() &&
            musicVolume.GetGlobalRect().End.X <= musicMute.GetGlobalRect().Position.X &&
            soundVolume.GetGlobalRect().End.X <= soundMute.GetGlobalRect().Position.X &&
            !Descendants(audioModal).OfType<Label>().Any(label => label.Text.Contains('%')),
            "Audio sliders must sit beside their mute buttons without percentage displays.");
        foreach (var volumeSlider in new[] { musicVolume, soundVolume })
        {
            var audioRow = volumeSlider.GetParent().GetParent();
            var labelCell = audioRow.GetChild<PanelContainer>(0);
            Require(labelCell.GetChild<Label>(0).Text == (volumeSlider == musicVolume ? "MUSIC" : "SOUND") &&
                labelCell.GetThemeStylebox("panel") is StyleBoxFlat labelStyle &&
                labelStyle.BorderColor == Colors.White &&
                new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }.All(side => labelStyle.GetBorderWidth(side) == 1) &&
                Math.Abs(labelCell.GetGlobalRect().GetCenter().Y - volumeSlider.GetGlobalRect().GetCenter().Y) < 1,
                "Audio labels must have separate single-white cells beside their sliders.");
            var sliderPadding = volumeSlider.GetParent<MarginContainer>();
            Require(sliderPadding.GetThemeConstant("margin_left") == 12 &&
                sliderPadding.GetThemeConstant("margin_right") == 12 &&
                Math.Abs(volumeSlider.Position.X - 12) < 1 &&
                Math.Abs(sliderPadding.Size.X - volumeSlider.Position.X - volumeSlider.Size.X - 12) < 1,
                "Audio sliders must have twelve pixels of breathing room on both sides.");
            Require(volumeSlider.TextureFilter == CanvasItem.TextureFilterEnum.Nearest &&
                volumeSlider.GetThemeIcon("grabber").GetSize() == new Vector2(12, 20),
                "Audio sliders must use crisp rectangular terminal block handles.");
            foreach (string sliderState in new[] { "slider", "grabber_area", "grabber_area_highlight" })
                Require(volumeSlider.GetThemeStylebox(sliderState) is StyleBoxFlat terminalTrack &&
                    terminalTrack.BorderColor == Colors.White &&
                    new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }
                        .All(side => terminalTrack.GetBorderWidth(side) == 1) &&
                    terminalTrack.CornerRadiusTopLeft == 0 && terminalTrack.CornerRadiusTopRight == 0 &&
                    terminalTrack.CornerRadiusBottomLeft == 0 && terminalTrack.CornerRadiusBottomRight == 0,
                    "Audio slider tracks must have square corners and single-white terminal outlines.");
        }
        musicVolume.Value = 37;
        soundVolume.Value = 62;
        Require(host.MusicVolume == 37 && host.SoundVolume == 62, "Independent audio sliders did not update volume.");
        musicVolume.GrabFocus();
        await KeyEvent(Key.Left);
        Require(host.MusicVolume == 36 && host.SoundVolume == 62,
            $"Keyboard volume adjustment affected the wrong channel: music={host.MusicVolume}, sound={host.SoundVolume}, focus={host.GetViewport().GuiGetFocusOwner()}.");
        musicMute.EmitSignal(Button.SignalName.Pressed);
        Require(!host.MusicEnabled && host.SoundEnabled && musicMute.Text == "",
            "Music mute also disabled sounds or did not update its button.");
        musicMute.EmitSignal(Button.SignalName.Pressed);
        soundMute.EmitSignal(Button.SignalName.Pressed);
        Require(host.MusicEnabled && !host.SoundEnabled && soundMute.Text == "",
            "Sound mute also disabled music or did not update its button.");
        soundMute.EmitSignal(Button.SignalName.Pressed);
        Require(host.MusicVolume == 36 && host.SoundVolume == 62, "Mute changed the saved volume levels.");
        await KeyEvent(Key.M);
        Require(!host.MusicEnabled && host.SoundEnabled, "Music mute mnemonic affected the wrong channel.");
        await KeyEvent(Key.M);
        await KeyEvent(Key.U);
        Require(host.MusicEnabled && !host.SoundEnabled, "Sound mute mnemonic affected the wrong channel.");
        await KeyEvent(Key.U);
        musicVolume.Value = 0;
        Require(host.MusicEnabled && host.GetNode<AudioStreamPlayer>("CityMusic").StreamPaused,
            "Zero music volume must be silent without changing the mute preference.");
        musicVolume.Value = Main.DefaultMusicVolume;
        soundVolume.Value = Main.DefaultSoundVolume;
        Require(!host.GetNode<AudioStreamPlayer>("CityMusic").StreamPaused, "Raising zero volume did not resume music.");
        Require(ReferenceEquals(session.Prompt, trappedMenu),
            "Adjusting inline audio controls must not replace the game menu or lose focus.");
        await KeyEvent(Key.F);
        Require(session.Prompt?.Title == "Font size", "F must open font controls from the game menu.");
        await KeyEvent(Key.Escape);
        Require(session.Prompt?.Title == "GAME MENU" &&
            host.GetViewport().GuiGetFocusOwner() is Button { Name: var restoredAction, Text: "CANCEL" } &&
            restoredAction == "GameMenuCancel",
            "Cancelling font controls must restore the parent menu with Cancel focused.");
        var saveMenu = session.Prompt;
        await KeyEvent(Key.S);
        Require(ReferenceEquals(session.Prompt, saveMenu) && !session.HasUnsavedChanges &&
            File.Exists(session.SavePath),
            "Game Menu S must quick-save successfully without closing or replacing the menu.");
        var saveButton = Descendants(host).OfType<MnemonicButton>().Single(button => button.Name == "GameMenuSave");
        Require(saveButton.Text == "SAVE" && saveButton.UnderlineIndex == 0,
            "Game Menu must expose an underlined SAVE action.");
        saveButton.GrabFocus();
        await KeyEvent(Key.Enter);
        Require(ReferenceEquals(session.Prompt, saveMenu) && !session.HasUnsavedChanges,
            "The SAVE button must activate with Enter and preserve the menu.");
        await KeyEvent(Key.Escape);
        session.ShowSessionMenu();
        await Frames();
        await KeyEvent(Key.Down);
        await KeyEvent(Key.Enter);
        Require(session.Prompt is null && !session.HasUnsavedChanges,
            "Native menu keyboard navigation did not select quick-save.");
        session.ShowLoadMenu();
        session.SelectPrompt(4);
        await Frames();
        Require(host.GetViewport().GuiGetFocusOwner() is LineEdit, "Load path field did not receive focus.");
        await KeyEvent(Key.A, ctrl: true);
        foreach (char character in "missing-city.json")
            await KeyEvent(Key.None, unicode: character);
        Require(session.Prompt?.Input == "missing-city.json",
            $"Native text input did not update the load path: '{session.Prompt?.Input}'.");
        await KeyEvent(Key.Enter);
        Require(session.Prompt?.Title == "Load file" && session.MessageKind == MessageKind.Error,
            "Native file submission did not surface a recoverable load error.");
        Require(Descendants(host).OfType<Label>().Any(l => l.IsVisibleInTree() && l.Text.StartsWith("Load failed:", StringComparison.Ordinal)),
            "Load failure is hidden behind the modal.");
        await KeyEvent(Key.Escape);
        Require(session.Prompt?.Title == "Load city", "Native Escape did not return from the path dialog to the load menu.");
        await KeyEvent(Key.Escape);
        Require(session.Prompt is null, "Native Escape did not close the root load menu.");
        bool pausedBeforeCancel = session.Game.Paused;
        session.Game.Paused = true;
        string beforeCancel = SaveGameStore.Serialize(session.Game);
        session.PreviewDemolish();
        await Frames();
        await KeyEvent(Key.Tab);
        Require(host.GetViewport().GuiGetFocusOwner() is Button { Text: "Cancel [Esc]" },
            "Preview Tab did not focus the cancel button.");
        await KeyEvent(Key.Enter);
        Require(session.Preview is null, "Preview Enter did not select the focused cancel button.");
        Require(beforeCancel == SaveGameStore.Serialize(session.Game), "Focused cancel changed the city.");
        session.Game.Paused = pausedBeforeCancel;
        session.ShowSessionMenu();
        int reportChoice = session.Prompt!.Choices.ToList().FindIndex(choice => choice.Label == "Report");
        var reportShortcut = session.Prompt.Shortcuts[reportChoice];
        await KeyEvent((Key)reportShortcut.Letter, shift: reportShortcut.Shift);
        Require(session.Prompt?.Title == "Weekly Report", "Underlined menu shortcut failed.");
        await KeyEvent(Key.Escape);
        Require(session.Prompt?.Title == "City menu" &&
            host.GetViewport().GuiGetFocusOwner() is Button { Text: var restoredReport } &&
            restoredReport.StartsWith("Report", StringComparison.Ordinal),
            "Cancelling the report did not restore its parent and focus.");
        await KeyEvent(Key.Escape);
        int shiftedChoice = -1;
        session.ShowAreaMenu();
        session.SelectPrompt(2);
        await Frames();
        var servicePrompt = session.Prompt!;
        string beforeHelp = SaveGameStore.Serialize(session.Game);
        await KeyEvent(Key.Enter, shift: true);
        Require(ReferenceEquals(servicePrompt, session.Prompt) && session.Preview is null &&
            servicePrompt.HelpIndex == servicePrompt.SelectedIndex &&
            SaveGameStore.Serialize(session.Game) == beforeHelp,
            "Shift+Enter must inspect a choice without activation or simulation changes.");
        var helpPanel = Descendants(host).OfType<ScrollContainer>().Single(n => n.Name == "ChoiceHelpPanel");
        var helpText = Descendants(host).OfType<Label>().Single(n => n.Name == "ChoiceHelpText");
        var focusedChoice = (Button)host.GetViewport().GuiGetFocusOwner();
        Require(helpPanel.IsVisibleInTree() && helpPanel.GlobalPosition.X >=
            focusedChoice.GetGlobalRect().End.X && helpText.Text.Contains("Upkeep:", StringComparison.Ordinal),
            "Contextual help must appear to the right with service details.");
        int clickedIndex = 1;
        var clickedChoice = Descendants(host).OfType<Button>().First(b =>
            b.TooltipText.StartsWith(servicePrompt.Choices[clickedIndex].Label + " (", StringComparison.Ordinal));
        Vector2 helpClick = clickedChoice.GetGlobalRect().GetCenter();
        foreach (bool pressed in new[] { true, false })
            host.GetViewport().PushInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = pressed,
                ShiftPressed = true,
                Position = helpClick,
                GlobalPosition = helpClick,
            }, true);
        await Frames();
        Require(ReferenceEquals(servicePrompt, session.Prompt) && session.Preview is null &&
            servicePrompt.HelpIndex == clickedIndex,
            $"Native Shift+click must open option help without activation: same={ReferenceEquals(servicePrompt, session.Prompt)}, preview={session.Preview?.Name}, help={servicePrompt.HelpIndex}, click={helpClick}, button={clickedChoice.GetGlobalRect()}.");
        bool helpHeadless = DisplayServer.GetName() == "headless";
        var helpWindowSize = helpHeadless ? host.GetWindow().ContentScaleSize : host.GetWindow().Size;
        if (helpHeadless) host.GetWindow().ContentScaleSize = new Vector2I(640, Main.MinimumWindowHeight);
        else host.GetWindow().Size = new Vector2I(640, 480);
        await Frames();
        var helpDialog = Descendants(host).OfType<PanelContainer>().Single(n => n.Name == "CityDialog");
        Require(helpDialog.GetGlobalRect().Position.X >= 0 && helpDialog.GetGlobalRect().End.X <= host.Size.X + 1 &&
            helpDialog.GetGlobalRect().Position.Y >= 0 && helpDialog.GetGlobalRect().End.Y <= host.Size.Y + 1 &&
            helpPanel.Size.X > 0 && helpText.AutowrapMode != TextServer.AutowrapMode.Off,
            $"Compact contextual help must fit: dialog={helpDialog.GetGlobalRect()}, window={host.Size}, help={helpPanel.Size}.");
        if (helpHeadless) host.GetWindow().ContentScaleSize = helpWindowSize;
        else host.GetWindow().Size = helpWindowSize;
        await Frames();
        focusedChoice.GrabFocus();
        await KeyEvent(Key.Enter);
        Require(session.Preview?.Building?.Name == servicePrompt.Choices[0].Label,
            "Plain Enter must still activate the focused placement choice after contextual help.");
        await KeyEvent(Key.Escape);
        Require(ReferenceEquals(servicePrompt, session.Prompt), "Help must preserve placement menu history.");
        for (int index = 0; index < session.Prompt!.Shortcuts.Count; index++)
            if (session.Prompt.Shortcuts[index].Shift &&
                session.Game.Map.Content.Buildings.Find(session.Prompt.Choices[index].Label) is { } shortcutBuilding &&
                CityProgression.IsUnlocked(session.Game, shortcutBuilding))
            { shiftedChoice = index; break; }
        if (shiftedChoice >= 0)
        {
            var shortcut = session.Prompt.Shortcuts[shiftedChoice];
            string buildingName = session.Prompt.Choices[shiftedChoice].Label;
            await KeyEvent((Key)shortcut.Letter, shift: true);
            Require(session.Preview?.Building?.Name == buildingName, "Shift mnemonic selected the wrong unlocked service building.");
            await KeyEvent(Key.Escape);
            Require(session.Prompt?.Title == "Service buildings" && session.Prompt.SelectedIndex == shiftedChoice,
                "Preview cancellation did not restore the service menu and focus.");
        }
        session.ClosePrompt();
        session.CancelPreview();

        var mapCenter = new Pos(session.Game.Map.Width / 2, session.Game.Map.Height / 2);
        session.PlaceCursor(mapCenter);
        session.CenterOn(mapCenter);
        await Frames();
        var pointer = new Vector2(8 * TerminalGrid.CellWidth, 6 * TerminalGrid.CellHeight);
        async Task Trackpad(Vector2 delta, bool shift = false, bool alt = false, bool ctrl = false, bool meta = false)
        {
            host.GetViewport().PushInput(new InputEventPanGesture
            {
                Position = host.Map.GlobalPosition + pointer,
                Delta = delta,
                ShiftPressed = shift,
                AltPressed = alt,
                CtrlPressed = ctrl,
                MetaPressed = meta,
            }, true);
            await Frames();
        }
        Pos ExpectedCamera(int dx, int dy)
        {
            var map = session.Game.Map;
            return new Pos(
                Math.Clamp(session.CameraX + dx, 0, Math.Max(0, map.Width - session.VisibleCellsX)) / session.Stride * session.Stride,
                Math.Clamp(session.CameraY + dy, 0, Math.Max(0, map.Height - session.VisibleCellsY)) / session.Stride * session.Stride);
        }
        for (int zoom = GameSession.MinZoom; zoom <= GameSession.MaxZoom; zoom++)
        {
            session.SetZoom(zoom);
            session.CenterOn(mapCenter);
            await Frames();
            var expected = ExpectedCamera(3 * session.Stride, 3 * session.Stride);
            await Trackpad(new Vector2(1, 1));
            Require(expected == new Pos(session.CameraX, session.CameraY),
                $"Native two-finger trackpad scrolling failed at zoom {zoom}.");
            expected = ExpectedCamera(-3 * session.Stride, -3 * session.Stride);
            await Trackpad(new Vector2(-1, -1));
            Require(expected == new Pos(session.CameraX, session.CameraY),
                $"Reverse trackpad scrolling failed at zoom {zoom}.");
        }
        session.SetZoom(0);
        session.CenterOn(mapCenter);
        await Frames();
        var trackpadStart = new Pos(session.CameraX, session.CameraY);
        await Trackpad(new Vector2(0.125f, 0.125f));
        await Trackpad(new Vector2(0.125f, 0.125f));
        Require(trackpadStart == new Pos(session.CameraX, session.CameraY),
            "Sub-cell trackpad deltas moved too far.");
        await Trackpad(new Vector2(0.125f, 0.125f));
        Require(new Pos(trackpadStart.X + 1, trackpadStart.Y + 1) == new Pos(session.CameraX, session.CameraY),
            "Fractional trackpad deltas were dropped.");
        host.GetWindow().EmitSignal(Window.SignalName.FocusExited);
        host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
        trackpadStart = new Pos(session.CameraX, session.CameraY);
        await Trackpad(new Vector2(0.25f, 0.25f));
        Require(trackpadStart == new Pos(session.CameraX, session.CameraY),
            "Focus loss did not clear fractional trackpad movement.");
        host.GetWindow().EmitSignal(Window.SignalName.FocusExited);
        host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
        await Trackpad(new Vector2(0, 1), shift: true);
        Require(session.CameraX == trackpadStart.X + 3 && session.CameraY == trackpadStart.Y,
            "Shift+trackpad did not scroll horizontally.");
        await Trackpad(new Vector2(0, -1), alt: true);
        Require(trackpadStart == new Pos(session.CameraX, session.CameraY),
            "Option+trackpad did not scroll horizontally.");
        var zoomAnchor = session.ScreenToMap(8, 6);
        for (int i = 0; i < 4; i++) await Trackpad(new Vector2(0, -0.25f), ctrl: true);
        Require(host.Map.Grid.TryCell(pointer.X, pointer.Y, out var zoomCell) &&
            session.ZoomLevel == 1 && session.ScreenToMap(zoomCell.X, zoomCell.Y) == zoomAnchor &&
            host.Map.Grid.PixelWidth == TerminalGrid.CellWidth * 2 &&
            host.Map.Grid.PixelHeight == TerminalGrid.CellHeight * 2,
            "Ctrl+trackpad did not accumulate zoom around the pointer.");
        await Trackpad(new Vector2(0, 1), meta: true);
        Require(session.ZoomLevel == 0, "Command+trackpad zoom did not work.");
        await KeyEvent(Key.Equal, meta: true);
        Require(session.ZoomLevel == 1, "Command+plus did not zoom in.");
        await KeyEvent(Key.Minus, ctrl: true);
        Require(session.ZoomLevel == 0, "Ctrl+minus did not zoom out.");
        await KeyEvent(Key.None, unicode: '+');
        Require(session.ZoomLevel == 1, "Unicode plus did not zoom in.");
        await KeyEvent(Key.None, unicode: '-');
        Require(session.ZoomLevel == 0, "Unicode minus did not zoom out.");
        await KeyEvent(Key.None, physical: Key.Equal);
        Require(session.ZoomLevel == 1, "Physical equal key did not zoom in.");
        await KeyEvent(Key.Key0, meta: true);
        Require(session.ZoomLevel == 0, "Command+0 did not reset zoom.");
        async Task Pinch(float factor)
        {
            host.GetViewport().PushInput(new InputEventMagnifyGesture
            {
                Position = host.Map.GlobalPosition + pointer,
                Factor = factor,
            }, true);
            await Frames();
        }
        var pinchAnchor = session.ScreenToMap(8, 6);
        for (int i = 0; i < 4; i++) await Pinch(1.05f);
        Require(session.ZoomLevel == 1 && host.Map.Grid.TryCell(pointer.X, pointer.Y, out var pinchCell) &&
            session.ScreenToMap(pinchCell.X, pinchCell.Y) == pinchAnchor,
            "Native fractional pinch did not zoom around the pointer.");
        await Pinch(0.82f);
        Require(session.ZoomLevel == 0, "Native contracting pinch did not zoom out.");
        session.ShowReport();
        await Frames();
        await Pinch(1.5f);
        Require(session.ZoomLevel == 0, "Report allowed background pinch zoom.");
        trackpadStart = new Pos(session.CameraX, session.CameraY);
        await Trackpad(new Vector2(1, 1));
        Require(trackpadStart == new Pos(session.CameraX, session.CameraY),
            "A report allowed background trackpad scrolling.");
        await KeyEvent(Key.Escape);
        session.PreviewDemolish();
        await Frames();
        await Trackpad(new Vector2(1, 1));
        Require(trackpadStart == new Pos(session.CameraX, session.CameraY),
            "A confirmation allowed background trackpad scrolling.");
        await KeyEvent(Key.Escape);
        session.CenterOn(mapCenter);
        await Frames();
        var panStart = new Pos(session.CameraX, session.CameraY);
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = pointer,
        });
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion
        {
            Position = pointer + new Vector2(3 * TerminalGrid.CellWidth, TerminalGrid.CellHeight),
        });
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = false,
        });
        Require(session.CameraX == panStart.X - 3 && session.CameraY == panStart.Y - 1,
            "Plain left-drag did not pan the map.");
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = pointer,
            ShiftPressed = true,
        });
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion
        {
            Position = new Vector2((host.Map.Grid.Columns - 1) * TerminalGrid.CellWidth, pointer.Y),
        });
        int edgeCamera = session.CameraX;
        int edgeSelection = session.Selection!.Value.Width;
        host._Process(0.25);
        Require(session.CameraX > edgeCamera && session.Selection!.Value.Width > edgeSelection,
            "Selection drag at the edge did not scroll and extend selection.");
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = false,
        });
        OpenGameMenu(host);
        await Frames();
        Require(session.Prompt?.Title == "GAME MENU", "Esc while running must open the game menu even while cells are selected.");
        await KeyEvent(Key.Escape);
        session.ClearSelection();
        await KeyEvent(Key.T);
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = pointer,
        });
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion
        {
            Position = pointer + new Vector2(3 * TerminalGrid.CellWidth, 0),
        });
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = false,
        });
        Require(session.RoadToolActive && session.Preview?.Area.Width == 4,
            "Road-line preview blocked pointer endpoint dragging.");
        await KeyEvent(Key.Escape);

        session.CenterOn(new Pos(session.Game.Map.Width / 2, session.Game.Map.Height / 2));
        await Frames();
        var camera = new Pos(session.CameraX, session.CameraY);
        host.Minimap.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = host.Minimap.Size / 2,
        });
        host.Minimap.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion
            { Position = host.Minimap.ScreenPoint(new(session.Game.Map.Width - 1, session.Game.Map.Height - 1)) });
        host.Minimap.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = false,
        });
        Require(session.CameraX > camera.X && session.CameraY > camera.Y, "Minimap drag did not move to the map edge.");
        session.ShowSessionMenu();
        camera = new Pos(session.CameraX, session.CameraY);
        host.Minimap.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = Vector2.Zero,
        });
        Require(camera == new Pos(session.CameraX, session.CameraY), "Modal permitted minimap navigation.");
        await KeyEvent(Key.Escape);

        bool headless = DisplayServer.GetName() == "headless";
        var window = host.GetWindow();
        var originalSize = headless ? window.ContentScaleSize : window.Size;
        if (headless) window.ContentScaleSize = new Vector2I(640, Main.MinimumWindowHeight);
        else window.Size = new Vector2I(640, 480);
        await Frames();
        Require(!guideHeader.IsVisibleInTree() &&
            Descendants(host).OfType<TerminalFrame>().Single(frame => frame.Name == "GuideHeaderFrame").Visible == false &&
            !reportsHeader.IsVisibleInTree() &&
            Descendants(host).OfType<Label>().Single(label => label.Name == "ViewsHeader").IsVisibleInTree(),
            "Narrow windows must hide GUIDE and REPORTS while keeping VIEWS visible.");
        int narrowHeaderSize = budgetHeader.GetThemeFontSize("font_size");
        Require(budgetHeader.CustomMinimumSize.X <= budgetHeader.GetThemeFont("font")
            .GetStringSize("99,999g [999g]", fontSize: narrowHeaderSize).X + 1 &&
            narrowHeaderSize > 8 && budgetHeader.TooltipText.Contains(budgetHeader.Text, StringComparison.Ordinal),
            $"Narrow headers must reserve less gold space for larger text and retain the full value tooltip: font={narrowHeaderSize}, width={budgetHeader.CustomMinimumSize.X}, text={budgetHeader.Text}, tooltip={budgetHeader.TooltipText}.");
        await KeyEvent(Key.F6);
        Require(session.Prompt?.Title == "TermCity guide", "F6 must open the guide even when its header is hidden.");
        await KeyEvent(Key.Escape);
        CheckStatusPath();
        session.ShowSessionMenu();
        await Frames();
        var finances = Descendants(host).OfType<Label>().Single(l => l.Name == "Budget");
        Require(finances.HorizontalAlignment == HorizontalAlignment.Right &&
            finances.Text.StartsWith(Fmt.CompactMoney(session.Game.Money), StringComparison.Ordinal) &&
            finances.GlobalPosition.X >= host.Size.X / 2 &&
            finances.GlobalPosition.X + finances.Size.X <= host.Size.X,
            "Budget must remain visible on the right side of the HUD at minimum window size.");
        var modal = Descendants(host).OfType<PanelContainer>().Single(p => p.Name == "CityDialog" && p.IsVisibleInTree());
        Require(modal.Position.X >= 0 && modal.Position.Y >= 0 &&
            modal.Position.X + modal.Size.X <= host.Size.X && modal.Position.Y + modal.Size.Y <= host.Size.Y,
            "City menu does not fit the minimum window size.");
        Require(modal.GlobalPosition.X >= host.Map.GetParent<Control>().GlobalPosition.X,
            "Menus must not cover the construction icons at minimum window size.");
        VerifyMapCenteredDialog(host, modal);
        await KeyEvent(Key.Escape);
        Descendants(host).OfType<ScrollContainer>().Single(n => n.Name == "ConstructionSidebar")
            .FindChild("Roads", true, false).EmitSignal(Button.SignalName.Pressed);
        await Frames();
        Require(modal.GlobalPosition.X >= host.Map.GetParent<Control>().GlobalPosition.X &&
            modal.GetGlobalRect().End.X <= host.Size.X &&
            modal.GetGlobalRect().End.Y <= host.Size.Y &&
            Descendants(modal).OfType<BoxContainer>().Single(n => n.Name == "ConstructionDetails").Vertical,
            $"Narrow construction catalogs must fit beside the icons: {modal.GetGlobalRect()}, window={host.Size}; " +
            string.Join(", ", Descendants(modal).OfType<Control>().Where(c =>
                c.Name == "ConstructionTypes" || c.Name == "ConstructionStats" || c.Name == "ConstructionInformation")
                .Select(c => $"{c.Name}={c.GetCombinedMinimumSize()}")));
        await KeyEvent(Key.Escape);
        session.ShowSessionMenu();
        await Frames();
        Require(host.Map.GlobalPosition.X + host.Map.Size.X <= host.Size.X &&
            host.Map.GlobalPosition.Y + host.Map.Size.Y <= host.Size.Y, "Map does not fit the minimum window size.");
        await KeyEvent(Key.Escape);
        var constructionRail = Descendants(host).OfType<ScrollContainer>().Single(n => n.Name == "ConstructionSidebar");
        Require(!constructionRail.GetVScrollBar().Visible &&
            Descendants(constructionRail).OfType<Button>().All(icon =>
                icon.GetParent<Control>().GetGlobalRect().End.Y <= constructionRail.GetGlobalRect().End.Y),
            "All construction icons must fit at minimum window size without scrolling.");
        Descendants(constructionRail).OfType<Button>()
            .Single(b => b.Name == CityReport.ServiceName(TermCity.Core.Buildings.ServiceKind.Faith))
            .EmitSignal(Button.SignalName.Pressed);
        await Frames();
        var narrowCatalog = Descendants(host).OfType<PanelContainer>().Single(n => n.Name == "CityDialog");
        Require(narrowCatalog.Position.X >= 0 && narrowCatalog.Position.Y >= 0 &&
            narrowCatalog.GetGlobalRect().End.X <= host.Size.X && narrowCatalog.GetGlobalRect().End.Y <= host.Size.Y,
            $"Construction catalog must fit the minimum window: dialog={narrowCatalog.GetGlobalRect()}, window={host.Size}.");
        await KeyEvent(Key.Escape);
        if (headless) window.ContentScaleSize = originalSize;
        else window.Size = originalSize;
        await Frames();
        Require(guideHeader.IsVisibleInTree(), "GUIDE must reappear when the window becomes wide again.");
        if (!headless && OS.GetName() == "Windows")
        {
            GD.Print("TERMCITY_WINDOWS_RESIZE_BEGIN");
            bool angle = RenderingServer.GetCurrentRenderingDriverName() == "opengl3_angle";
            string beforeResize = SaveGameStore.Serialize(session.Game);
            foreach (var size in new[] { new Vector2I(800, 600), new Vector2I(1920, 1040) })
            {
                var timer = Stopwatch.StartNew();
                window.Size = size;
                await Frames();
                CheckStatusPath();
                Require(!angle || timer.Elapsed.TotalMilliseconds < 1000,
                    $"Window resize stalled for {timer.Elapsed.TotalMilliseconds:F0} ms.");
            }
            var maximize = Stopwatch.StartNew();
            window.Mode = Window.ModeEnum.Maximized;
            await Frames();
            CheckStatusPath();
            Require(!angle || maximize.Elapsed.TotalMilliseconds < 1000,
                $"Maximize stalled for {maximize.Elapsed.TotalMilliseconds:F0} ms.");
            window.Mode = Window.ModeEnum.Windowed;
            window.Size = originalSize;
            await Frames();
            double nativeWorst = await WindowsResizeSmoke.Run(window);
            await Frames();
            CheckStatusPath();
            Require(!angle || nativeWorst < 1000, $"Native edge resize stalled for {nativeWorst:F0} ms.");
            Require(beforeResize == SaveGameStore.Serialize(session.Game), "Resizing changed city data.");
            GD.Print($"TERMCITY_WINDOWS_RESIZE_OK driver={RenderingServer.GetCurrentRenderingDriverName()} native_worst={nativeWorst:F0}ms");
        }
        string cityBeforeFontChange = SaveGameStore.Serialize(session.Game);
        host.SetFontSize(Main.DefaultFontSize);
        await KeyEvent(Key.F3);
        Require(session.Prompt?.Title == "Font size", "F3 did not open the font dialog.");
        await Frames();
        var smallDialog = Descendants(host).OfType<PanelContainer>().Single(p => p.Name == "CityDialog" && p.IsVisibleInTree());
        var compactScroll = Descendants(smallDialog).OfType<ScrollContainer>().Single(s => s.IsVisibleInTree());
        float compactContentHeight = Math.Min(compactScroll.GetChild<Control>(0).GetCombinedMinimumSize().Y,
            host.Size.Y - host.Map.GetParent<Control>().GlobalPosition.Y - 64);
        Require(smallDialog.Size.Y < largeMenuHeight &&
            Math.Abs(smallDialog.Size.Y - compactContentHeight - 28) < 1,
            $"Compact dialog geometry: size={smallDialog.Size}, center={smallDialog.GetGlobalRect().GetCenter()}, window={host.Size}.");
        VerifyMapCenteredDialog(host, smallDialog);
        var value = Descendants(host).OfType<Label>().Single(l => l.Name == "FontDialogValue");
        var fontMinus = value.GetParent().GetChild<Button>(0);
        var fontPlus = value.GetParent().GetChild<Button>(2);
        fontMinus.GrabFocus();
        await KeyEvent(Key.Right);
        Require(host.FontSize == Main.DefaultFontSize && host.GetViewport().GuiGetFocusOwner() == fontPlus,
            "Font arrows must navigate to + without changing size.");
        await KeyEvent(Key.Left);
        Require(host.FontSize == Main.DefaultFontSize && host.GetViewport().GuiGetFocusOwner() == fontMinus,
            "Font arrows must navigate to - without changing size.");
        value.GetParent().GetChild<Button>(2).EmitSignal(Button.SignalName.Pressed);
        Require(host.FontSize == Main.DefaultFontSize + 2, "Larger-font dialog button failed.");
        value.GetParent().GetChild<Button>(0).EmitSignal(Button.SignalName.Pressed);
        Require(host.FontSize == Main.DefaultFontSize, "Smaller-font dialog button failed.");
        host.SetFontSize(Main.MaxFontSize);
        value.GetParent().GetChild<Button>(2).EmitSignal(Button.SignalName.Pressed);
        Require(host.FontSize == Main.MaxFontSize, "Font dialog exceeded the maximum size.");
        session.SelectPrompt(1);
        Require(host.FontSize == Main.DefaultFontSize, "RESET did not restore the default font.");
        session.SelectPrompt(0);
        OpenGameMenu(host);
        await Frames();
        var inlineMenu = session.Prompt;
        var inlineFontMinus = Descendants(host).OfType<Button>().Single(b => b.Name == "FontSizeMinus");
        var inlineFontPlus = Descendants(host).OfType<Button>().Single(b => b.Name == "FontSizePlus");
        var inlineSidebarMinus = Descendants(host).OfType<Button>().Single(b => b.Name == "SidebarSizeMinus");
        var inlineSidebarPlus = Descendants(host).OfType<Button>().Single(b => b.Name == "SidebarSizePlus");
        var inlineFontReset = Descendants(host).OfType<Button>().Single(b => b.Name == "FontSizeReset");
        var inlineSidebarReset = Descendants(host).OfType<Button>().Single(b => b.Name == "SidebarSizeReset");
        Require(new[] { inlineFontReset, inlineSidebarReset }.All(reset =>
            new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" }.All(resetState =>
                reset.GetThemeStylebox(resetState) is StyleBoxFlat resetStyle &&
                resetStyle.ContentMarginLeft == 16 && resetStyle.ContentMarginRight == 16 &&
                resetStyle.ContentMarginTop == 10 && resetStyle.ContentMarginBottom == 10)),
            "RESET buttons must retain increased symmetric padding in every state.");
        Require(new[] { inlineFontMinus, inlineFontPlus, inlineSidebarMinus, inlineSidebarPlus }
            .All(button => button.Size.X >= 48 && button.Size.Y >= 44 &&
                button.GetThemeFontSize("font_size") == 28 && button.Alignment == HorizontalAlignment.Center),
            "Game menu +/- controls must have larger targets and centered 28-point symbols.");
        Require(inlineFontReset.GetIndex() == inlineFontMinus.GetIndex() + 1 &&
            inlineFontPlus.GetIndex() == inlineFontReset.GetIndex() + 1 &&
            inlineSidebarReset.GetIndex() == inlineSidebarMinus.GetIndex() + 1 &&
            inlineSidebarPlus.GetIndex() == inlineSidebarReset.GetIndex() + 1,
            "Inline RESET buttons must appear between - and + in each size row.");
        Require(inlineFontMinus.GetParent().GetChild<Button>(0).GetThemeColor("font_color") == Colors.White &&
            inlineSidebarMinus.GetParent().GetChild<Button>(0).GetThemeColor("font_color") == Colors.White,
            "Game menu display-option labels must be white.");
        inlineFontPlus.GrabFocus();
        await KeyEvent(Key.Enter);
        Require(host.FontSize == Main.DefaultFontSize + 2 &&
            host.GetViewport().GuiGetFocusOwner() == inlineFontPlus && ReferenceEquals(session.Prompt, inlineMenu),
            "Inline font + must adjust by two points without replacing the game menu or losing focus.");
        inlineFontMinus.EmitSignal(Button.SignalName.Pressed);
        Require(host.FontSize == Main.DefaultFontSize, "Inline font - must restore the previous size.");
        host.SetFontSize(Main.MinFontSize);
        inlineFontMinus.EmitSignal(Button.SignalName.Pressed);
        Require(host.FontSize == Main.MinFontSize, "Inline font - must respect the minimum.");
        host.SetFontSize(Main.MaxFontSize);
        inlineFontPlus.EmitSignal(Button.SignalName.Pressed);
        Require(host.FontSize == Main.MaxFontSize, "Inline font + must respect the maximum.");
        inlineFontReset.GrabFocus();
        await KeyEvent(Key.Enter);
        Require(host.FontSize == Main.DefaultFontSize && ReferenceEquals(session.Prompt, inlineMenu) &&
            host.GetViewport().GuiGetFocusOwner() == inlineFontReset,
            "Inline font RESET must restore the default without replacing the menu or losing focus.");
        var inlineSplit = Descendants(host).OfType<CitySplit>().Single();
        inlineSplit.SetSidebarWidth(CitySplit.DefaultSidebar);
        await Frames();
        float inlineWidth = inlineSplit.SidebarWidth;
        inlineSidebarPlus.EmitSignal(Button.SignalName.Pressed);
        await Frames();
        Require(Math.Abs(inlineSplit.SidebarWidth - inlineWidth - TerminalGrid.CellWidth) < 1 &&
            ReferenceEquals(session.Prompt, inlineMenu),
            "Inline sidebar + must adjust by one character without replacing the game menu.");
        inlineSidebarMinus.EmitSignal(Button.SignalName.Pressed);
        await Frames();
        Require(Math.Abs(inlineSplit.SidebarWidth - inlineWidth) < 1,
            "Inline sidebar - must restore the previous width.");
        inlineSidebarPlus.EmitSignal(Button.SignalName.Pressed);
        await Frames();
        inlineSidebarReset.GrabFocus();
        await KeyEvent(Key.Enter);
        await Frames();
        Require(Math.Abs(inlineSplit.SidebarWidth - CitySplit.DefaultSidebar) < 1 &&
            ReferenceEquals(session.Prompt, inlineMenu) && host.GetViewport().GuiGetFocusOwner() == inlineSidebarReset,
            "Inline sidebar RESET must restore the default without replacing the menu or losing focus.");
        int fontChoice = session.Prompt!.Choices.ToList().FindIndex(c => c.Label == "FONT SIZE");
        session.SelectPrompt(fontChoice);
        await Frames();
        Require(session.Prompt?.Title == "Font size", "Font size row must open its adjustment dialog.");
        await KeyEvent(Key.Escape);
        int resizeChoice = session.Prompt!.Choices.ToList().FindIndex(c => c.Label == "RESIZE SIDEBAR");
        Require(resizeChoice >= 0, "City menu is missing keyboard sidebar resizing.");
        session.SelectPrompt(resizeChoice);
        await Frames();
        var split = Descendants(host).OfType<CitySplit>().Single();
        split.SetSidebarWidth(CitySplit.DefaultSidebar);
        await Frames();
        float oldSidebar = split.SidebarWidth;
        var sidebarValue = Descendants(host).OfType<Label>().Single(l => l.Name == "SidebarDialogValue");
        var sidebarMinus = sidebarValue.GetParent().GetChild<Button>(0);
        var sidebarPlus = sidebarValue.GetParent().GetChild<Button>(2);
        Require(session.Prompt?.Title == "Sidebar size", "Sidebar size row must open its adjustment dialog.");
        sidebarMinus.GrabFocus();
        await KeyEvent(Key.Right);
        Require(Math.Abs(split.SidebarWidth - oldSidebar) < 1 &&
            host.GetViewport().GuiGetFocusOwner() == sidebarPlus,
            "Right arrow must focus + without resizing the sidebar.");
        await KeyEvent(Key.Enter);
        Require(Math.Abs(split.SidebarWidth - oldSidebar - TerminalGrid.CellWidth) < 1,
            $"Sidebar + step: before={oldSidebar}, after={split.SidebarWidth}, split={split.Size}, " +
            $"minimum={split.GetChild<Control>(1).GetCombinedMinimumSize()}.");
        await KeyEvent(Key.Left);
        Require(host.GetViewport().GuiGetFocusOwner() == sidebarMinus &&
            Math.Abs(split.SidebarWidth - oldSidebar - TerminalGrid.CellWidth) < 1,
            "Left arrow must focus - without resizing.");
        await KeyEvent(Key.Enter);
        Require(Math.Abs(split.SidebarWidth - oldSidebar) < 1,
            "Enter must activate the focused - button.");
        sidebarPlus.EmitSignal(Button.SignalName.Pressed);
        await Frames();
        session.SelectPrompt(1);
        await Frames();
        Require(Math.Abs(split.SidebarWidth - CitySplit.DefaultSidebar) < 1,
            "Sidebar RESET must restore the default width.");
        split.SetSidebarWidth(float.MaxValue);
        await Frames();
        Require(split.SidebarWidth <= CitySplit.MaximumSidebar + 1 &&
            split.GetChild<Control>(0).Size.X >= 320 - 1, "Sidebar exceeded its maximum or map minimum.");
        split.SetSidebarWidth(0);
        await Frames();
        Require(split.SidebarWidth >= CitySplit.MinimumSidebar - 1, "Sidebar fell below its minimum.");
        session.SelectPrompt(0);
        Require(session.Prompt?.Title == "GAME MENU" &&
            host.GetViewport().GuiGetFocusOwner() is Button { Text: "CANCEL" },
            "Sidebar OK must return to the game menu with Cancel focused.");
        await KeyEvent(Key.Escape);
        await Frames();
        var dragger = split.GetDragAreaControls()[0];
        var dragPoint = dragger.GetGlobalRect().GetCenter();
        float beforeDrag = split.SidebarWidth;
        host.GetViewport().PushInput(new InputEventMouseButton
        {
            Position = dragPoint,
            GlobalPosition = dragPoint,
            ButtonIndex = MouseButton.Left,
            Pressed = true,
        }, true);
        for (int step = 1; step <= 5; step++)
        {
            var position = dragPoint - new Vector2(step, 0);
            host.GetViewport().PushInput(new InputEventMouseMotion
            {
                Position = position,
                GlobalPosition = position,
                Relative = new Vector2(-1, 0),
                ButtonMask = MouseButtonMask.Left,
            }, true);
            await Frames();
            Require(Math.Abs(split.SidebarWidth - beforeDrag - step) < 1,
                "Sidebar dragging must advance by pixels, not whole characters.");
            VerifyMapPadding(host);
        }
        host.GetViewport().PushInput(new InputEventMouseButton
        {
            Position = dragPoint - new Vector2(5, 0),
            GlobalPosition = dragPoint - new Vector2(5, 0),
            ButtonIndex = MouseButton.Left,
            Pressed = false,
        }, true);
        await Frames();
        Require(split.SidebarWidth > CitySplit.MinimumSidebar, "Dragging the divider did not resize the sidebar.");
        split.SetSidebarWidth(CitySplit.DefaultSidebar);
        await Frames();
        var sidebar = Descendants(host).OfType<CityPanel>().Single();
        var cityTable = Descendants(sidebar).OfType<GridContainer>().Single(control => control.Name == "CityTable");
        Require(cityTable.GetGlobalRect().End.Y <= sidebar.GetGlobalRect().End.Y + 1 &&
            cityTable.Size.X <= sidebar.Size.X && !Descendants(sidebar).OfType<ScrollContainer>().Any(),
            "The City table must fit without sidebar scrolling.");
        foreach (int fontSize in new[] { Main.DefaultFontSize, Main.MaxFontSize, Main.MinFontSize })
        {
            host.SetFontSize(fontSize);
            if (headless)
                window.ContentScaleSize = new Vector2I(960 * fontSize / 16, Main.MinimumWindowHeight * fontSize / 16);
            else host.FitWindowToFont();
            await Frames();
            CheckStatusPath();
            Require(host.GetWindow().ContentScaleFactor == fontSize / 16f,
                "Font setting did not scale the interface and map together.");
            Require(host.Map.Grid.Columns == (int)Math.Ceiling(host.Map.Size.X / TerminalGrid.CellWidth) &&
                host.Map.Grid.Rows == (int)Math.Ceiling(host.Map.Size.Y / TerminalGrid.CellHeight),
                "Font resizing left stale map dimensions.");
            VerifyMapPadding(host);
            VerifySidebarTables(host);
            Require(host.Map.Grid.TryCell(TerminalGrid.CellWidth * 2, TerminalGrid.CellHeight * 2, out var fontCell) &&
                fontCell == new Pos(2, 2), "Font resizing broke map hit targets.");
            Require(cityBeforeFontChange == SaveGameStore.Serialize(session.Game),
                "Font resizing changed the city.");
        }
        host.SetFontSize(Main.MaxFontSize);
        if (headless)
        {
            window.ContentScaleSize = new Vector2I(640 * Main.MaxFontSize / 16, Main.MinimumWindowHeight * Main.MaxFontSize / 16);
        }
        else host.FitWindowToFont();
        await Frames();
        session.ShowSessionMenu();
        await Frames();
        modal = Descendants(host).OfType<PanelContainer>().Single(p => p.Name == "CityDialog" && p.IsVisibleInTree());
        Require(modal.Position.X >= 0 && modal.Position.Y >= 0 &&
            modal.Position.X + modal.Size.X <= host.Size.X && modal.Position.Y + modal.Size.Y <= host.Size.Y,
            "Large-font menu extends outside the window.");
        Require(host.Map.Grid.Columns >= 8 && host.Map.Grid.Rows >= 6,
            $"Large text left too little room for the map: {host.Map.Grid.Columns}x{host.Map.Grid.Rows}.");
        if (!headless)
        {
            var usable = DisplayServer.ScreenGetUsableRect(window.CurrentScreen);
            Require(window.Position.X >= usable.Position.X && window.Position.Y >= usable.Position.Y &&
                window.Position.X + window.Size.X <= usable.End.X &&
                window.Position.Y + window.Size.Y <= usable.End.Y,
                "Font resizing moved the window outside the usable desktop.");
        }
        await KeyEvent(Key.Escape);
        host.SetFontSize(Main.DefaultFontSize);
        await Frames();
        await VerifyEffects(host, Frames, (code, ctrl, unicode, meta, physical) => KeyEvent(code, ctrl, unicode, meta, physical));
        session.ClosePrompt();
        session.SetZoom(0);
        var green = session.Game.Map.Content.Buildings.Get("Charcoal Burners");
        var areaSpot = Enumerable.Range(0, session.Game.Map.Width * session.Game.Map.Height)
            .Select(session.Game.Map.PosOf)
            .OrderBy(p => Math.Abs(p.X - session.Game.Map.Width / 2) + Math.Abs(p.Y - session.Game.Map.Height / 2))
            .First(p => session.Game.CanPlaceBuilding(green, p.X, p.Y));
        session.PlaceCursor(areaSpot);
        session.CenterOn(areaSpot);
        host.Map.Invalidate(true);
        await Frames();
        var placementCell = new Pos(areaSpot.X - session.CameraX, areaSpot.Y - session.CameraY);
        Require(host.Map.Grid.VisualAt(session, placementCell.X, placementCell.Y)?.Background == Rgb.Hex(0xffdc5a),
            "The normal cursor must retain its yellow highlight.");
        session.PreviewBuilding(green);
        session.ClosePrompt();
        await Frames();
        Require(host.Map.Grid.VisualAt(session, placementCell.X, placementCell.Y)?.Background == Rgb.Hex(0x2a7849),
            "Valid building placement must show green at the cursor, without a yellow highlight.");
        session.Game.Map.SetBuildingFootprint(green, new(areaSpot.X, areaSpot.Y, green.Width, green.Height));
        session.Game.Touch();
        session.MoveBuildingPlacement(areaSpot);
        host.Map.Invalidate(true);
        await Frames();
        Require(host.Map.Grid.VisualAt(session, placementCell.X, placementCell.Y)?.Background == Rgb.Hex(0x8c2d37),
            "Invalid building placement must show red at the cursor, without a yellow highlight.");
        if (!headless) Require(host.Map.AreasDrawn == 0,
            "An invalid placement must not draw preview or underlying-building area effects.");
        session.CancelPreview();
        session.ClosePrompt();
        session.PlaceCursor(areaSpot);
        host.Map.Invalidate(true);
        session.CenterOn(areaSpot);
        await Frames();
        if (!headless) Require(host.Map.AreasDrawn >= 1, "Pollution area circles were not drawn.");
        foreach (var p in session.Game.Map.BuildingFootprintAt(areaSpot.X, areaSpot.Y).Cells())
        {
            var screen = new Pos(p.X - session.CameraX, p.Y - session.CameraY);
            Require(host.Map.Grid.VisualAt(session, screen.X, screen.Y)?.Glyph == " ",
                "Building cells must leave their glyphs to the raised pixel-sprite layer.");
        }
        if (!headless) Require(host.Map.BuildingsDrawn > 0, "Raised building sprites were not drawn.");
        session.PlaceCursor(new Pos(areaSpot.X + green.Width, areaSpot.Y));
        await Frames();
        if (!headless) Require(host.Map.AreasDrawn == 0, "Unselected buildings still show range circles.");
        session.PlaceCursor(new Pos(areaSpot.X + green.Width - 1, areaSpot.Y + green.Height - 1));
        await Frames();
        if (!headless) Require(host.Map.AreasDrawn >= 1, "Selecting a non-anchor footprint cell did not show its circles.");
        var inspectionCell = session.Cursor;
        var inspectionPosition = new Vector2((inspectionCell.X - session.CameraX + 0.5f) * TerminalGrid.CellWidth,
            (inspectionCell.Y - session.CameraY + 0.5f) * TerminalGrid.CellHeight);
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Right,
            Pressed = true,
            Position = inspectionPosition,
        });
        await Frames();
        Require(session.Prompt?.Title == BuildingReport.Title &&
            session.Prompt.Text.Contains(green.Name.ToUpperInvariant(), StringComparison.Ordinal),
            "Right-clicking any building footprint cell must open its status dialog.");
        var buildingStatusDialog = Descendants(host).OfType<TerminalFrame>()
            .Single(frame => frame.Name == "CityDialog" && frame.IsVisibleInTree());
        Require(Descendants(buildingStatusDialog).OfType<GridContainer>().Any(table =>
            table.Name.ToString().StartsWith("ReportTable", StringComparison.Ordinal)),
            "Building status must render its details in terminal tables.");
        var buildingActions = Descendants(buildingStatusDialog).OfType<MnemonicButton>().ToArray();
        Require(buildingActions.Length == 2 && buildingActions.All(button => button.Size.X >= 96 &&
            button.Size.Y >= 36 &&
            button.GetThemeFont("font").GetStringSize(button.Text, fontSize: button.GetThemeFontSize("font_size")).X <=
                button.Size.X - button.GetThemeStylebox("normal").GetContentMargin(Side.Left) -
                button.GetThemeStylebox("normal").GetContentMargin(Side.Right)),
            "Building Status footer actions must have readable labels, not collapse to narrow outlines.");
        await KeyEvent(Key.Escape);
        int zoomSkips = host.MusicSkips;
        for (int i = 0; i < 12; i++)
        {
            host.Map.ZoomBy(i % 2 == 0 ? -1 : 1);
            session.ScrollChars(i % 2 == 0 ? 3 : -3, 1);
            await Frames();
            VerifyMapPadding(host);
        }
        Require(host.MusicSkips == zoomSkips, "Music skipped while zooming and scrolling.");
        if (File.Exists(session.SavePath)) File.Delete(session.SavePath);
    }

    /// <summary>Drives the effect pipeline through the real scene: build, demolish, toggle, drain. Draw checks only apply when the engine draws.</summary>
    private static async Task VerifyEffects(Main host, Func<Task> frames, Func<Key, bool, uint, bool, Key, Task> key)
    {
        var session = host.Session;
        var map = session.Game.Map;
        session.ClosePrompt();
        session.SetZoom(0);
        var openCell = Enumerable.Range(0, map.Width * map.Height).Select(map.PosOf)
            .First(p => p.X >= 2 && p.Y >= 2 && p.X < map.Width - 2 && p.Y < map.Height - 2 &&
                session.Game.Network.IsServed(map.Index(p.X, p.Y)) && session.Game.CanPlaceRoad(p.X, p.Y));
        session.CenterOn(openCell);
        host.Map.RefreshCells();
        await frames();
        Require(host.Effects.Settings.Level == TermCity.Core.Effects.EffectLevel.High && host.Map.Effects == host.Effects,
            "Effects are not wired into the map at the default level.");
        void Drain()
        {
            for (int i = 0; i < 40 && host.Effects.ActiveOneShots > 0; i++) host._Process(0.25);
        }
        Drain();
        Pos Visible()
        {
            for (int y = 2; y < host.Map.Grid.Rows - 2; y++)
                for (int x = 2; x < host.Map.Grid.Columns - 2; x++)
                {
                    var at = session.ScreenToMap(x, y);
                    if (map.InBounds(at) && session.Game.CanBuildOn(at.X, at.Y)) return at;
                }
            throw new InvalidOperationException("No visible vacant cell for the effects check.");
        }
        var spot = Visible();
        int spawned = host.Director.TotalSpawned;
        Require(session.Game.BuildRoad(new CellRect(spot.X, spot.Y, 1, 1)).Success, "Effects check could not build a road.");
        await frames();
        Require(host.Director.TotalSpawned > spawned, "Building a road did not start an effect.");
        Drain();
        Require(host.Effects.ActiveOneShots == 0 && host.Effects.CellCount == 0, "Road effect left cell modifiers behind.");
        int draws = host.Map.DrawCount;
        Require(session.Game.Demolish(new CellRect(spot.X, spot.Y, 1, 1)).Success, "Effects check could not demolish.");
        host.Map.RefreshCells();
        host._Process(0.05);
        Require(host.Effects.Effects.OfType<TermCity.Core.Effects.DemolishEffect>().Any() && host.Effects.HasVisuals,
            "Demolishing did not start the shrink-away effect.");
        await frames();
        if (host.Map.DrawCount > draws)
        {
            Require(host.Map.EffectGlyphsDrawn > 0, "The map drew a frame without the running effect glyphs.");
            var visible = session.ScreenToMap(0, 0);
            var view = new CellRect(visible.X, visible.Y,
                host.Map.Grid.Columns * session.Stride, host.Map.Grid.Rows * session.Stride);
            if (host.Map.Grid.VectorRoads && session.Game.Map.RoadCells.Any(i =>
                view.Contains(session.Game.Map.PosOf(i))))
                Require(host.Map.RoadChunksDrawn > 0, "The map drew a frame without the curved roads.");
        }
        else GD.Print("Godot did not draw during the effects check (headless); effect glyph drawing was not exercised.");
        Drain();
        Require(host.Effects.ActiveOneShots == 0 && host.Effects.CellCount == 0, "Demolish effect did not finish and clean up.");
        string saved = SaveGameStore.Serialize(session.Game);
        host._Process(0.1);
        Require(saved == SaveGameStore.Serialize(session.Game), "Effects mutated gameplay.");

        await key(Key.V, false, 0, false, Key.None);
        Require(host.EffectsLevel == TermCity.Core.Effects.EffectLevel.Low && host.Effects.Settings.Intensity < 1, "V did not lower effects.");
        await key(Key.V, false, 0, false, Key.None);
        Require(host.EffectsLevel == TermCity.Core.Effects.EffectLevel.Off && !host.Effects.Settings.Active, "V did not turn effects off.");
        spot = Visible();
        spawned = host.Director.TotalSpawned;
        session.Game.BuildRoad(new CellRect(spot.X, spot.Y, 1, 1));
        session.Game.Demolish(new CellRect(spot.X, spot.Y, 1, 1));
        await frames();
        Require(host.Director.TotalSpawned == spawned && host.Effects.ActiveEffects == 0 && !host.Effects.HasVisuals,
            "Effects ran while switched off.");
        await key(Key.V, false, 0, false, Key.None);
        Require(host.EffectsLevel == TermCity.Core.Effects.EffectLevel.High && host.Effects.Settings.Active, "V did not restore effects.");
        await frames();
        Require(host.Director.TotalSpawned == spawned, "Re-enabling effects replayed old changes.");
        OpenGameMenu(host);
        await frames();
        Require(session.Prompt?.Title == "GAME MENU" &&
            !session.Prompt.Choices.Any(c => c.Label is "Effects" or "Celebrations"),
            "Effects must remain keyboard-accessible without Effects/Celebrations menu rows.");
        await key(Key.Escape, false, 0, false, Key.None);
        Drain();
    }

    private static void VerifyMapCenteredDialog(Main host, Control dialog)
    {
        var map = host.Map.GetParent<Control>().GetGlobalRect();
        var expected = map.GetCenter() - dialog.Size / 2;
        expected.X = Math.Clamp(expected.X, map.Position.X,
            Math.Max(map.Position.X, host.GetGlobalRect().End.X - dialog.Size.X));
        expected.Y = Math.Clamp(expected.Y, map.Position.Y,
            Math.Max(map.Position.Y, host.GetGlobalRect().End.Y - dialog.Size.Y));
        Require(dialog.GlobalPosition.DistanceTo(expected) < 1,
            $"Regular dialogs must center on the map while remaining on screen: {dialog.GlobalPosition} vs {expected}.");
    }

    private static async Task VerifyConstructionCatalog(Main host, Func<Task> frames,
        Func<Key, bool, uint, bool, Key, bool, Task> key)
    {
        var session = host.Session;
        var rail = Descendants(host).OfType<ScrollContainer>().Single(n => n.Name == "ConstructionSidebar");
        var icons = Descendants(rail).OfType<Button>().ToArray();
        Require(icons.Length == TermCity.Core.Buildings.ServiceKinds.All.Count + (CityGame.DebugToolsAvailable ? 2 : 1) &&
            rail.GetGlobalRect().End.X <= host.Map.GlobalPosition.X,
            "Construction rail must show roads and every service category to the left of the map.");
        Require(icons[0].GetChild<ConstructionIcon>(0).Glyph ==
            session.Game.Map.Content.Roads.OrderByDescending(r => r.Rank).First().GlyphFor(5),
            "Road category must use a straight road icon rather than a cross.");
        Require(Descendants(host).OfType<CitySplit>().Single().MouseDefaultCursorShape == Control.CursorShape.Arrow,
            "The split container must not propagate a resize cursor over the construction rail.");
        Require(icons.All(icon => icon.GetParent() is PanelContainer and not TerminalFrame &&
            icon.GetParent<Control>().GetThemeStylebox("panel") is StyleBoxFlat iconBorder &&
            iconBorder.BorderColor == Colors.White &&
            new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }.All(side => iconBorder.GetBorderWidth(side) == 1) &&
            icon.Position == new Vector2(Main.IconPadding, Main.IconPadding)),
            "Every construction icon must have exactly one white outline.");
        Require(icons.All(icon => Math.Abs(icon.GetParent<Control>().Size.X - icon.Size.X -
            icon.Position.X * 2) < 1) &&
            icons.All(icon => Math.Abs(icon.GetParent<Control>().Size.Y - icon.Size.Y -
                icon.Position.Y * 2) < 1),
            "The icon buttons must have compact, equal insets on all sides.");
        var outer = rail.GetParent<TerminalFrame>();
        var firstIcon = icons[0].GetParent<PanelContainer>();
        var innerBorder = new Rect2(outer.GlobalPosition + outer.InnerBorderBounds.Position,
            outer.InnerBorderBounds.Size);
        float leftGap = firstIcon.GlobalPosition.X + 0.5f - innerBorder.Position.X;
        float rightGap = innerBorder.End.X - (firstIcon.GetGlobalRect().End.X - 0.5f);
        float topGap = firstIcon.GlobalPosition.Y + 0.5f - innerBorder.Position.Y;
        float doubleBorderGap = outer.JoinRight ? outer.BorderBounds.End.X + 2.5f - outer.InnerBorderBounds.End.X
            : outer.Size.X - 0.5f - outer.InnerBorderBounds.End.X;
        Require(Math.Abs(leftGap - rightGap) < 0.01 && Math.Abs(leftGap - topGap) < 0.01 &&
            Math.Abs(leftGap - doubleBorderGap) < 0.01 &&
            icons.All(icon => Math.Abs(icon.Size.X - icon.Size.Y) < 0.01),
            $"Rail content must have equal visible border insets and square icon buttons: left={leftGap}, right={rightGap}, top={topGap}.");
        Require(Math.Abs(innerBorder.End.Y - (rail.GetGlobalRect().End.Y - 0.5f) - doubleBorderGap) < 0.01 &&
            icons.Zip(icons.Skip(1)).All(pair =>
                Math.Abs(pair.Second.GetParent<Control>().GlobalPosition.Y + 0.5f -
                    (pair.First.GetParent<Control>().GetGlobalRect().End.Y - 0.5f) - doubleBorderGap) < 0.01),
            "Bottom content inset and gaps between icon outlines must match the double-border line spacing.");
        Require(rail.VerticalScrollMode == ScrollContainer.ScrollMode.Disabled &&
            !rail.GetVScrollBar().Visible &&
            icons.All(icon => icon.GetParent<Control>().GetGlobalRect().End.Y <= rail.GetGlobalRect().End.Y),
            "All construction icons must fit without a vertical scrollbar.");
        Require(Math.Abs(rail.Size.X - icons.Max(icon => icon.GetParent<Control>().Size.X)) < 1 &&
            Math.Abs(innerBorder.Size.X - firstIcon.Size.X + 1 - Main.RailPadding * 2) < 0.01,
            "Construction rail must fit its icons and compact visible border insets without extra horizontal expansion.");
        Require(icons.All(icon => icon.Size.X >= 40 && icon.Size.Y >= 40 &&
            icon.GetThemeFontSize("font_size") == 24),
            "Construction icons must use enlarged buttons and glyphs.");
        Require(icons.Select(icon => icon.GetChild<ConstructionIcon>(0).Kind)
            .SequenceEqual(new[] { ServiceKind.None }.Concat(ServiceKinds.All)
                .Concat(CityGame.DebugToolsAvailable ? new[] { ServiceKind.Defence } : [])),
            "Construction artwork must match its service category.");
        icons[0].EmitSignal(Control.SignalName.MouseEntered);
        Require(ConstructionIcon.WiggleAngleAt(0.25f) < 0 &&
            ConstructionIcon.WiggleAngleAt(0.5f) > 0 &&
            ConstructionIcon.WiggleAngleAt(0.625f) > 0 &&
            ConstructionIcon.WiggleAngleAt(0.625f) < ConstructionIcon.WiggleAngleAt(0.5f) &&
            ConstructionIcon.WiggleAngleAt(0.75f) == 0,
            "Hover must tilt left, right, then return upright in three quarter-second motions.");
        Require(icons[0].GetChild<ConstructionIcon>(0).Glow == 1,
            "Hovering a construction icon must start its color glow.");
        await host.ToSignal(host.GetTree().CreateTimer(0.75), SceneTreeTimer.SignalName.Timeout);
        Require(icons[0].GetChild<ConstructionIcon>(0).Glow < 0.01,
            "Construction icon glow must fade rather than remaining highlighted.");
        await host.ToSignal(host.GetTree().CreateTimer(0.35), SceneTreeTimer.SignalName.Timeout);
        Require(icons[0].GetChild<ConstructionIcon>(0).WiggleAngle == 0,
            "Hover animation must stop upright without moving the icon's box.");
        int money = session.Game.Money;
        Button Add() => Descendants(host).OfType<Button>().Single(n => n.Name == "ConstructionAdd");
        Button SelectedBuilding() => Descendants(host).OfType<VBoxContainer>()
            .Single(n => n.Name == "ConstructionTypes").GetChildren().OfType<Button>().Single(b => b.ButtonPressed);
        Button Type(string name) => Descendants(host).OfType<VBoxContainer>()
            .Single(n => n.Name == "ConstructionTypes").GetChildren().OfType<Button>()
            .Single(n => n.Text.EndsWith(name, StringComparison.Ordinal));
        icons[0].EmitSignal(Button.SignalName.Pressed);
        Require(icons[0].GetChild<ConstructionIcon>(0).Glow == 1,
            "Clicking a construction icon must restart its glow.");
        await frames();
        var dialog = Descendants(host).OfType<PanelContainer>().Single(n => n.Name == "CityDialog");
        VerifyDialogButtonBorders(dialog);
        var cancelButton = Descendants(dialog).OfType<Button>().Single(n => n.Name == "ConstructionCancel");
        Require(Math.Abs(cancelButton.Size.X - Add().Size.X) < 0.01 &&
            cancelButton.Alignment == HorizontalAlignment.Center,
            "Construction submenu Cancel must be centered and match the Add button's width.");
        var catalogLayout = Descendants(dialog).OfType<HBoxContainer>().Single(n => n.Name == "ConstructionLayout");
        float contentBottom = Descendants(dialog).OfType<Label>()
            .Where(label => label.IsVisibleInTree() && label.GetParent() == catalogLayout.GetParent())
            .Select(label => label.GetGlobalRect().End.Y).Append(catalogLayout.GetGlobalRect().End.Y).Max();
        Require(Math.Abs(Add().GlobalPosition.Y - contentBottom -
            (dialog.GetGlobalRect().End.Y - Add().GetGlobalRect().End.Y)) < 1,
            $"Construction action spacing: top={Add().GlobalPosition.Y - contentBottom}, " +
            $"bottom={dialog.GetGlobalRect().End.Y - Add().GetGlobalRect().End.Y}.");
        Require(dialog.GlobalPosition == host.Map.GetParent<Control>().GlobalPosition &&
            !Descendants(dialog).Any(n => n.Name == "ConstructionHeader") &&
            Descendants(dialog).OfType<Button>().Where(b => b.GetParent().Name == "ConstructionTypes")
                .All(b => b.GetThemeFontSize("font_size") == 22 &&
                    b.GetChild<Label>(0).GetThemeFontSize("font_size") == 30),
            "Catalogs must anchor at the map with larger choices and no duplicate building subheader.");
        Require(dialog is TerminalFrame { JoinAdjacent: false } &&
            Descendants(dialog).OfType<TerminalFrame>().All(frame => !frame.JoinAdjacent &&
                !frame.JoinLeft && !frame.JoinRight && !frame.JoinTop && !frame.JoinBottom),
            "Construction submenu headers must retain closed double-white borders without accidental joins.");
        Require(dialog.Position.X >= 0 && dialog.Position.Y >= 0 &&
            dialog.GetGlobalRect().End.X <= host.Size.X && dialog.GetGlobalRect().End.Y <= host.Size.Y,
            $"Construction catalog must fit the window: dialog={dialog.GetGlobalRect()}, window={host.Size}.");
        Require(session.Preview is null && Type(DefaultRoads.KingsRoadName) is not null &&
            Add().GetThemeStylebox("normal") is StyleBoxFlat { BgColor: var gray } &&
            gray.R == gray.G && gray.G == gray.B &&
            Add().GetThemeColor("font_color").G > Add().GetThemeColor("font_color").R,
            "Road catalog must list road types with green Add text on gray, without starting placement.");
        Require(SelectedBuilding().Text.Contains(DefaultRoads.TrackName, StringComparison.Ordinal),
            "The first road must immediately display its information.");
        var shield = dialog.GetParent<Control>();
        var policeIcon = icons.Single(b => b.Name == CityReport.ServiceName(ServiceKind.Police));
        shield.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion
        {
            GlobalPosition = policeIcon.GetGlobalRect().GetCenter(),
        });
        Require(policeIcon.GetChild<ConstructionIcon>(0).Glow == 1,
            "Hover highlighting must remain available through the construction modal shield.");
        shield.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            GlobalPosition = policeIcon.GetGlobalRect().GetCenter(),
        });
        await frames();
        Require(session.Prompt?.Title == CityReport.ServiceName(ServiceKind.Police) &&
            SelectedBuilding().Text.Contains(session.Game.Map.Content.Buildings
                .Where(b => b.PlayerPlaceable && b.Service == ServiceKind.Police)
                .OrderBy(CityProgression.RequiredPopulation).ThenBy(b => b.Cost).First().Name,
                StringComparison.Ordinal),
            "Clicking another sidebar icon through the modal shield must switch catalogs.");
        await key(Key.Escape, false, 0, false, Key.None, false);
        Require(session.Prompt is null, "Switching catalogs must not stack previously opened categories.");
        icons[0].EmitSignal(Button.SignalName.Pressed);
        await frames();
        policeIcon.EmitSignal(Button.SignalName.Pressed);
        await frames();
        Require(session.Prompt?.Title == CityReport.ServiceName(ServiceKind.Police),
            "Sidebar buttons must allow switching an open construction catalog.");
        icons[0].EmitSignal(Button.SignalName.Pressed);
        await frames();
        Type(DefaultRoads.CobbledName).EmitSignal(Button.SignalName.Pressed);
        Require(SelectedBuilding().Text.Contains(DefaultRoads.CobbledName, StringComparison.Ordinal) &&
            Descendants(host).OfType<Label>().Single(n => n.Name == "ConstructionDescription").Text ==
                session.Game.Map.Content.Roads.Single(r => r.Name == DefaultRoads.CobbledName).Description,
            "Catalog selection must update the icon/name header and full description.");
        var details = Descendants(host).OfType<BoxContainer>().Single(n => n.Name == "ConstructionDetails");
        Require(details.Vertical &&
            details.GetChild<Control>(0).GetGlobalRect().End.Y <= details.GetChild<Control>(1).GlobalPosition.Y &&
            Descendants(details.GetChild(0)).OfType<Label>().Any(l => l.Name == "ConstructionDescription") &&
            Descendants(details.GetChild(1)).OfType<Label>().Any(l => l.Text == "Cost") &&
            Math.Abs(details.GetChild<Control>(0).Size.X - details.Size.X) < 1 &&
            Math.Abs(details.GetChild<Control>(1).Size.X - details.Size.X) < 1,
            "Description must precede statistics, with both sections filling the available width.");
        Add().EmitSignal(Button.SignalName.Pressed);
        Require(session.Prompt is null && session.RoadToolActive &&
            session.Preview?.Road?.Name == DefaultRoads.CobbledName && session.Game.Money == money,
            "Add must enter road placement without charging or building.");
        session.CancelPreview();
        await frames();
        Require(session.Prompt?.Title == "Roads", "Cancelling placement must restore the catalog.");
        Descendants(host).OfType<Button>().Single(b => b.Text == "Cancel").EmitSignal(Button.SignalName.Pressed);
        Require(session.Prompt is null && session.Preview is null, "Cancel did not close the catalog.");
        if (CityGame.DebugToolsAvailable)
        {
            icons.Single(button => button.Name == "Monuments").EmitSignal(Button.SignalName.Pressed);
            await frames();
            Require(session.Prompt?.Title == "Monuments (Debug)" && !Add().Disabled &&
                SelectedBuilding().Text == "Bewildered Duck",
                "Debug launch must offer immediate monument placement without a population gate.");
            Add().EmitSignal(Button.SignalName.Pressed);
            Require(session.Preview?.Building?.IsMonument == true && session.Preview.Area.Width == 8 &&
                session.Preview.Area.Height == 6 && session.Game.Money == money,
                "The debug monument must use the ordinary full-footprint placement preview without spending gold.");
            session.CancelPreview();
            await frames();
            Descendants(host).OfType<Button>().Single(button => button.Text == "Cancel").EmitSignal(Button.SignalName.Pressed);
            await frames();
        }
        foreach (var kind in new[] { ServiceKind.Power, ServiceKind.Water, ServiceKind.Granary })
        {
            icons.Single(button => button.Name == CityReport.ServiceName(kind)).EmitSignal(Button.SignalName.Pressed);
            await frames();
            var capacityChoices = Descendants(host).OfType<VBoxContainer>().Single(node => node.Name == "ConstructionTypes");
            foreach (var building in session.Game.Map.Content.Buildings.Where(type =>
                type.PlayerPlaceable && type.Service == kind && type.Capacity > 0))
            {
                var option = capacityChoices.GetChildren().OfType<Button>().Single(button => button.Text == building.Name);
                var capacityLabel = option.GetChildren().OfType<Label>().Single(label => label.Name == "BuildingCapacity");
                Require(capacityLabel.Text == ChoiceHelpContent.CapacityText(building) &&
                    capacityLabel.Size.X >= capacityLabel.GetThemeFont("font").GetStringSize(capacityLabel.Text,
                        fontSize: capacityLabel.GetThemeFontSize("font_size")).X,
                    "Building capacities and meaningful units must be visible directly in the choice list.");
                option.EmitSignal(Button.SignalName.Pressed);
                await frames();
                var coverageStatus = Descendants(host).OfType<Label>().Single(label => label.Name == "PeasantStatus");
                Require(coverageStatus.IsVisibleInTree() &&
                    coverageStatus.Text == SidebarReport.ServiceStatus(session.Game, kind).Text &&
                    coverageStatus.HorizontalAlignment == HorizontalAlignment.Right &&
                    Math.Abs(coverageStatus.GetGlobalRect().End.X - host.Size.X) < 1,
                    "Building submenus must show relevant service status at the bottom right.");
                var capacityStats = Descendants(host).OfType<GridContainer>().Single(node => node.Name == "ConstructionStats");
                var capacityLabels = capacityStats.GetChildren().OfType<PanelContainer>()
                    .Select(cell => cell.GetChild<Label>(0)).ToArray();
                Require(capacityLabels[4].Text == "Capacity" &&
                    capacityLabels[5].Text == ChoiceHelpContent.CapacityText(building),
                    "Capacity must appear immediately after cost/upkeep in every relevant building's details.");
            }
            Descendants(host).OfType<Button>().Single(button => button.Text == "Cancel").EmitSignal(Button.SignalName.Pressed);
            await frames();
        }
        icons.Single(b => b.Name == CityReport.ServiceName(TermCity.Core.Buildings.ServiceKind.Police))
            .EmitSignal(Button.SignalName.Pressed);
        await frames();
        var types = Descendants(host).OfType<VBoxContainer>().Single(n => n.Name == "ConstructionTypes");
        var typeButtons = types.GetChildren().OfType<Button>().ToArray();
        Require(types.GetParent() is PanelContainer { Name: var typesBoxName } &&
            typesBoxName == "ConstructionTypesBox",
            "All road/building choices must share one enclosing single-white border.");
        Require(typeButtons.All(button => button.GetThemeFontSize("font_size") == 22 &&
            button.Size.X >= button.GetThemeFont("font").GetStringSize(button.Text, fontSize: 22).X + 56),
            "Building names must use the enlarged font and a column wide enough to show every full name.");
        var police = session.Game.Map.Content.Buildings
            .Where(b => b.PlayerPlaceable && b.Service == TermCity.Core.Buildings.ServiceKind.Police)
            .OrderBy(CityProgression.RequiredPopulation).ThenBy(b => b.Cost).ToArray();
        Require(typeButtons.Select(b => b.Text).SequenceEqual(police.Select(b => b.Name)) &&
            typeButtons.Zip(typeButtons.Skip(1)).All(pair => pair.First.GlobalPosition.Y < pair.Second.GlobalPosition.Y) &&
            types.GlobalPosition.X < statsPosition() &&
            SelectedBuilding().Text.Contains(police[0].Name, StringComparison.Ordinal),
            "Buildings must appear vertically on the left in progression/cost order, with the first selected.");
        var stats = Descendants(host).OfType<GridContainer>().Single(n => n.Name == "ConstructionStats");
        float statsPosition() => Descendants(host).OfType<GridContainer>()
            .Single(n => n.Name == "ConstructionStats").GlobalPosition.X;
        var statLabels = stats.GetChildren().OfType<PanelContainer>().Select(cell =>
            cell.GetChild(0) is Label label ? label : Descendants(cell).OfType<Label>().Last()).ToArray();
        var labels = statLabels.Where((_, index) => index % 2 == 0).ToArray();
        int footprintIndex = Array.FindIndex(statLabels, label => label.Text == "Footprint");
        Require(labels[0].Text == "Cost" && labels[1].Text == "Upkeep" &&
            labels.All(label => label.Text is not ("Does" or "Placement" or "Area icons")) &&
            statLabels[1].Text == Fmt.Money(police[0].Cost) &&
            footprintIndex >= 0 &&
            statLabels[footprintIndex + 1].Text == $"{police[0].Width} x {police[0].Height}" &&
            statLabels.Where((_, index) => index % 2 == 1).All(label =>
                !char.IsLetter(label.Text[0]) || char.IsUpper(label.Text[0])),
            "Details must omit redundant rows, simplify cost/footprint and capitalize values.");
        float longestLabel = labels.Max(label => label.GetThemeFont("font").GetStringSize(label.Text,
            fontSize: label.GetThemeFontSize("font_size")).X);
        Require(labels.All(label => label.Size.X >= longestLabel &&
            label.AutowrapMode == TextServer.AutowrapMode.Off),
            "Statistics labels must keep the full width of their longest string without wrapping.");
        Require(statLabels.Where((_, index) => index % 2 == 1)
            .Where(label => label.GetParent() is PanelContainer)
            .All(label => label.Size.X >= 80), "Statistics values must retain a readable minimum column width.");
        Require(statLabels.Where(label => label.GetParent() is PanelContainer).All(label => label.VerticalAlignment == VerticalAlignment.Top &&
            label.SizeFlagsVertical == Control.SizeFlags.Fill &&
            Math.Abs(label.Position.Y - 3) < 0.01),
            "Statistics keys and values must align at the top of their table cells.");
        Require(stats.GetChildren().OfType<PanelContainer>().Select((cell, index) => (cell, index)).All(item =>
            item.cell.GetThemeStylebox("panel") is StyleBoxFlat style && style.BorderColor == Colors.White &&
            style.BorderWidthLeft == 0 &&
            style.BorderWidthTop == (item.index < 2 ? 1 : 0) &&
            style.BorderWidthRight == 1 && style.BorderWidthBottom == 1 && style.ContentMarginTop == 3),
            "Details must draw each shared edge once, with uniform one-pixel white lines.");
        Require(!Descendants(host).Any(n => n.Name == "ConstructionStatsHeader" ||
            n.Name == "ConstructionDescriptionHeader"),
            "Description and statistics/effects headings must be omitted.");
        var descriptionBox = Descendants(host).OfType<PanelContainer>()
            .Single(n => n.Name == "ConstructionDescriptionBox");
        Require(descriptionBox.Size.Y >= typeButtons[0].Size.Y + Main.RailPadding &&
            Descendants(descriptionBox).OfType<Label>().Single(n => n.Name == "ConstructionDescription")
                .GetThemeFontSize("font_size") == 20,
            "Description text must use 20 points and its box must reach at least the first name row's bottom.");
        Require(Descendants(descriptionBox).Any(n => n.Name == "ConstructionDescription") &&
            !Descendants(descriptionBox).OfType<PanelContainer>().Any() &&
            Descendants(descriptionBox).OfType<Label>().Single(n => n.Name == "ConstructionDescription")
                .GetParent() is MarginContainer,
            "The description must have only its outer box, without a header or text-body border.");
        foreach (var panel in Descendants(host).OfType<PanelContainer>().Where(panel =>
            panel.Name == "ConstructionTypesBox" || panel.Name == "ConstructionDescriptionBox" ||
            panel.GetChildren().OfType<Label>().Any(label =>
                label.Name == "ConstructionCategoryHeader")))
            Require(panel.GetThemeStylebox("panel") is StyleBoxFlat style &&
                style.BorderColor == Colors.White &&
                style.BorderWidthLeft == (panel.Name == "ConstructionDescriptionBox" ? 0 : 1) &&
                new[] { Side.Top, Side.Right, Side.Bottom }.All(side => style.GetBorderWidth(side) == 1),
                "Catalog boxes must use single lines, sharing the name list's divider on the left.");
        var typesBox = types.GetParent<Control>();
        Require(Math.Abs(typesBox.GetGlobalRect().End.X - descriptionBox.GlobalPosition.X) < 0.01 &&
            Math.Abs(typesBox.GlobalPosition.Y - descriptionBox.GlobalPosition.Y) < 0.01 &&
            Math.Abs(typesBox.GetGlobalRect().End.Y - stats.GetGlobalRect().End.Y) < 0.01,
            "Name and detail sections must join at one continuous divider rather than sit side by side.");
        foreach (var box in new[] { types.GetParent<Control>(), descriptionBox })
        {
            var child = box.GetChild<Control>(0);
            Require(Math.Abs(child.Position.X - Main.RailPadding) < 0.01 &&
                Math.Abs(child.Position.Y - Main.RailPadding) < 0.01 &&
                Math.Abs(box.Size.X - child.GetRect().End.X - Main.RailPadding) < 0.01 &&
                Math.Abs(box.Size.Y - child.GetRect().End.Y - Main.RailPadding) < 0.01,
                "Catalog enclosing borders must use the same even four-pixel spacing as the icon rail.");
        }
        Require(typeButtons.Zip(typeButtons.Skip(1)).All(pair =>
            Math.Abs(pair.Second.Position.Y - pair.First.GetRect().End.Y - (Main.RailPadding - 1)) < 0.01),
            "Stacked name borders must have four-pixel line-to-line spacing.");
        Type("Gaol").EmitSignal(Button.SignalName.Pressed);
        Require(Add().Disabled && Descendants(host).OfType<Label>().Any(l => l.Text.Contains("Market Town (", StringComparison.Ordinal)) &&
            Type("Gaol").GetChildren().OfType<Control>().Any(control => control.Name == "BuildingLock"),
            "Locked buildings must display their unlock threshold and disable Add.");
        Type("Watch House").EmitSignal(Button.SignalName.Pressed);
        Require(Add().Disabled && Type("Watch House").GetChildren().OfType<Control>()
            .Any(control => control.Name == "BuildingLock"),
            "Starter sheriff buildings must remain locked until Hamlet.");
        Add().EmitSignal(Button.SignalName.Pressed);
        Require(session.Prompt is not null && !session.BuildingToolActive && session.Game.Money == money,
            "Locked buildings must not start placement even through a forced Add activation.");
        await frames();
        await key(Key.Escape, false, 0, false, Key.None, false);
        Require(session.Prompt is null && session.Preview is null, "Escape did not close the catalog.");
        icons[0].EmitSignal(Button.SignalName.Pressed);
        await frames();
        var outside = Vector2.One;
        Require(!Descendants(host).OfType<PanelContainer>().Single(n => n.Name == "CityDialog")
            .GetGlobalRect().HasPoint(outside), "Outside-click test point is inside the catalog.");
        foreach (bool pressed in new[] { true, false })
            host.GetViewport().PushInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = pressed,
                Position = outside,
                GlobalPosition = outside,
            }, true);
        await frames();
        Require(session.Prompt is null && session.Preview is null && session.Game.Money == money,
            $"Clicking outside the catalog must close it without a background action: prompt={session.Prompt?.Title}, preview={session.Preview?.Name}, gold={session.Game.Money}/{money}.");
        session.ShowAreaMenu();
        session.SelectPrompt(1);
        await frames();
        Require(!Descendants(host).Any(n => n.Name == "ConstructionCatalog") &&
            session.Prompt?.Choices.Any(c => c.Cells?[0] == "Fill area") == true,
            "The existing Roads area menu must remain distinct from the construction catalog.");
        session.ClosePrompt();
        float? sharedNameWidth = null;
        foreach (var category in icons)
        {
            category.EmitSignal(Button.SignalName.Pressed);
            await frames();
            var choices = Descendants(host).OfType<VBoxContainer>().Single(n => n.Name == "ConstructionTypes")
                .GetChildren().OfType<Button>().ToArray();
            sharedNameWidth ??= choices[0].Size.X;
            Require(choices.All(choice => Math.Abs(choice.Size.X - sharedNameWidth.Value) < 0.01),
                "Every building and road name cell must have the same width across all catalogs.");
            foreach (var choice in choices)
            {
                choice.EmitSignal(Button.SignalName.Pressed);
                await frames();
                Require(choice.Size.X >= choice.GetThemeFont("font")
                    .GetStringSize(choice.Text, fontSize: choice.GetThemeFontSize("font_size")).X + 56,
                    $"Construction name must fit without clipping: {choice.Text}.");
                var cells = Descendants(host).OfType<GridContainer>().Single(n => n.Name == "ConstructionStats")
                    .GetChildren().OfType<PanelContainer>().Select(cell => cell.HasMeta("details_value")
                        ? cell.GetMeta("details_value").AsString() : cell.GetChild<Label>(0).Text).ToArray();
                var selectedType = session.Game.Map.Content.Buildings.FirstOrDefault(building => building.CatalogName == choice.Text);
                bool hasAreaEffect = choice.Text != "Traffic Circle" &&
                    (selectedType is null || AreaOfEffect.ForBuilding(selectedType, new Pos(0, 0)).Count > 0);
                Require(!cells.Where((_, index) => index % 2 == 0).Contains("Area of Effect") &&
                    !Descendants(host).Any(node => node.Name == "ConstructionEffectIcon") &&
                    cells.Where((_, index) => index % 2 == 0).Count(text => text == "Effect") == (hasAreaEffect ? 1 : 0),
                    "Buildings must have an Effect row only when they have an actual area effect.");
                int effectIndex = Array.FindIndex(cells, text => text == "Effect");
                if (selectedType is not null)
                    foreach (var area in AreaOfEffect.ForBuilding(selectedType, new Pos(0, 0)))
                    {
                        Require(cells[effectIndex + 1].Contains(
                            $"radius {area.Radius} cells. Area: {area.Radius * 2 + 1} x {area.Radius * 2 + 1} cells",
                            StringComparison.Ordinal) ||
                            cells[effectIndex + 1].Contains("No distance cutoff.", StringComparison.Ordinal),
                            "Effects must describe pollution falloff or travel-based service response.");
                    }
                Require(Descendants(host).OfType<Label>().Single(label => label.Name == "ConstructionDescription").Text ==
                    (choice.Text == "Traffic Circle"
                        ? ChoiceHelpContent.RoadTemplate(session.Game, TermCity.Core.Roads.RoadTemplate.TrafficCircle).Description
                        : selectedType?.Description ?? session.Game.Map.Content.Roads.Single(road => road.Name == choice.Text).Description),
                    "Area effects must not be appended to the description.");
                Require(!cells.Contains("Operation") && cells.All(text => !text.Contains("at current funding", StringComparison.Ordinal)),
                    "Catalog details must omit the Operation row and upkeep funding suffix.");
                Require(cells[0] == "Cost" && cells[2] == "Upkeep" &&
                    (choice.Text == "Traffic Circle" ? cells[1].EndsWith("g (current plot)", StringComparison.Ordinal) :
                        cells[1].EndsWith('g') && cells[1][..^1].All(c => char.IsDigit(c) || c == ',')) &&
                    cells.Where((_, index) => index % 2 == 0)
                        .All(text => text is not ("Does" or "Placement" or "Area icons")) &&
                    cells.Where((_, index) => index % 2 == 1)
                        .All(text => text.Length == 0 || !char.IsLetter(text[0]) || char.IsUpper(text[0])),
                    $"Every construction details table must use simplified rows and capitalized values: {choice.Text}.");
                for (int i = 0; i < cells.Length; i += 2)
                {
                    if (category == icons[0])
                        Require(cells[i] != "Needs", "Road details must omit the Needs row.");
                    if (cells[i] == "Footprint")
                        Require(!cells[i + 1].Contains("cells", StringComparison.Ordinal),
                            "Footprints must show dimensions without cells.");
                }
            }
            session.ClosePrompt();
        }
        await key(Key.F6, false, 0, false, Key.None, false);
        Require(session.Prompt?.Title == "TermCity guide", "F6 must open the guide.");
        VerifyMapCenteredDialog(host,
            Descendants(host).OfType<PanelContainer>().Single(n => n.Name == "CityDialog" && n.IsVisibleInTree()));
        await key(Key.Escape, false, 0, false, Key.None, false);
    }

    public static void Run(Main host)
    {
        host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
        var session = host.Session;
        Require(!session.HasCursor && session.Selection is null && session.Anchor is null &&
            Descendants(host).OfType<Label>().Single(l => l.Name == "Status").Text == "Press F1 for HELP",
            "Startup must have no selected cell and show the F1 help hint.");
        host.Map.RefreshCells();
        for (int y = 0; y < host.Map.Grid.Rows; y++)
            for (int x = 0; x < host.Map.Grid.Columns; x++)
                Require(host.Map.Grid.HighlightAt(session, x, y) is null, "Startup painted a cell highlight.");
        var map = session.Game.Map;
        var music = host.GetNode<AudioStreamPlayer>("CityMusic");
        Require(music.Playing && music.Stream is AudioStreamGenerator
        {
            MixRate: MedievalMusicSynth.SampleRate,
        } && host.MusicPhraseCount == 1,
            "Registered medieval songs must use the shared streaming player, not additional WAV players.");
        Require(MusicRegistry.CreateDefault().Songs.Any(song => song.Title == host.CurrentMusicTitle),
            "The streaming player must select its score through the music registry.");
        host.GetWindow().EmitSignal(Window.SignalName.FocusExited);
        Require(music.StreamPaused, "Music continued while the game window was unfocused.");
        host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
        Require(!music.StreamPaused, "Music did not resume when the game regained focus.");
        Require(host.Map.Grid.Columns >= 8 && host.Map.Grid.Rows >= 6,
            "Smoke viewport must be large enough to exercise map input.");
        Require(host.Map.Size.X <= host.Size.X && host.Map.Size.Y <= host.Size.Y,
            "Map layout extends beyond the window.");
        Require(ProjectSettings.GetSetting("rendering/environment/defaults/default_clear_color").AsColor() == Colors.Black,
            "Desktop background is not black.");
        Require(host.Map.GetParent() is TerminalFrame { MouseFilter: Control.MouseFilterEnum.Ignore },
            "Map must use the shared double frame without intercepting input.");
        VerifyTerminalJoins(host);
        VerifyMapPadding(host);
        VerifySidebarTables(host);
        Require(host.Size == new Vector2(960, Main.MinimumWindowHeight), "Window resize did not reach the scene.");
        float minimapAspect = map.Height * 1.5f / map.Width;
        Require(host.Minimap.Size.Y + 0.01 >= host.Minimap.Size.X * minimapAspect,
            $"Minimap must fill sidebar width: minimap={host.Minimap.Size}, panel={host.Minimap.GetParent().GetParent().GetParent<Control>().Size}, " +
            $"window={host.Size}, minimum={Main.MinimumWindowHeight}.");
        Require(host.Map.Grid.Columns == (int)Math.Ceiling(host.Map.Size.X / TerminalGrid.CellWidth) &&
                host.Map.Grid.Rows == (int)Math.Ceiling(host.Map.Size.Y / TerminalGrid.CellHeight),
            "Window resize did not reach the cell grid.");
        Require(Godot.FileAccess.FileExists("res://Assets/DejaVu-LICENSE.txt"),
            "The bundled font license must also be distributed with exported players.");
        GD.Print($"Godot viewport: {host.Map.Grid.Columns}x{host.Map.Grid.Rows} cells");
        var before = session.Cursor;
        host._UnhandledInput(new InputEventKey { Keycode = Key.Right, Pressed = true });
        Require(session.Cursor.X == before.X + 1, "Keyboard movement did not reach the session.");

        var vacant = Enumerable.Range(0, map.Width * map.Height)
            .Select(i => new Pos(i % map.Width, i / map.Width))
            .First(p => session.Game.CanBuildOn(p.X, p.Y));
        session.PlaceCursor(vacant);
        host._UnhandledInput(new InputEventKey { Keycode = Key.B, Pressed = true });
        Require(session.Preview is not null, "Road preview was not created.");
        int zoned = session.Game.Stats.Residential.Zoned;
        host._UnhandledInput(new InputEventKey { Keycode = Key.R, Pressed = true });
        Require(session.Game.Stats.Residential.Zoned == zoned, "Preview allowed a background action.");
        host._UnhandledInput(new InputEventKey { Keycode = Key.Enter, Pressed = true });
        Require(session.Preview is null && map.HasRoad(vacant.X, vacant.Y), "Road confirmation failed.");

        vacant = Enumerable.Range(0, map.Width * map.Height)
            .Select(i => new Pos(i % map.Width, i / map.Width))
            .First(p => session.Game.CanBuildOn(p.X, p.Y));
        session.PlaceCursor(vacant);
        host._UnhandledInput(new InputEventKey { Keycode = Key.R, Pressed = true });
        Require(map.ZoneAt(vacant.X, vacant.Y) == ZoneType.Residential, "Zoning failed.");
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(session.Game));
        Require(loaded.Stats == session.Game.Stats, "Save serialization changed city statistics.");

        host._UnhandledInput(new InputEventKey { Keycode = Key.Key3, Pressed = true });
        double days = session.Game.ElapsedDays;
        for (int i = 0; i < 8; i++)
        {
            session.Update(0.25);
        }
        Require(session.Game.ElapsedDays > days, "Simulation did not advance.");
        session.Game.Paused = true;
        Require(session.Autosave(), "Autosave failed.");
        for (int slot = 1; slot <= GameSession.AutosaveSlots; slot++)
        {
            string path = session.AutosavePath(slot);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        var watch = Stopwatch.StartNew();
        for (int zoom = GameSession.MinZoom; zoom <= GameSession.MaxZoom; zoom++)
        {
            session.SetZoom(zoom);
            session.ScrollCamera(17, 9);
            host.Map.Grid.Fill(session);
            Require(host.Map.Grid.Columns > 0 && host.Map.Grid.Rows > 0, "Viewport grid was empty.");
        }
        GD.Print($"Godot visible-cell rebuilds at all zooms: {watch.Elapsed.TotalMilliseconds:F1} ms");
        session.SetZoom(0);
        session.CenterOn(new Pos(map.Width / 2, map.Height / 2));
        host.Map.Invalidate(true);

        session.ShowPrompt("Smoke prompt", "Dismiss this prompt.", [new("Close", session.ClosePrompt)]);
        host._UnhandledInput(new InputEventKey { Keycode = Key.Enter, Pressed = true });
        Require(session.Prompt is null, "Prompt dismissal failed.");
        host._Notification(checked((int)Node.NotificationWMCloseRequest));
        Require(session.Prompt is not null, "Window close did not guard unsaved progress.");
        int homes = session.Game.Stats.Residential.Zoned;
        host._UnhandledInput(new InputEventKey { Keycode = Key.R, Pressed = true });
        Require(session.Game.Stats.Residential.Zoned == homes, "Quit prompt allowed a background action.");
        host._UnhandledInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        Require(session.Prompt is null, "Quit prompt cancellation failed.");
        host.Map.RefreshCells();

        var pointer = new Vector2(5 * TerminalGrid.CellWidth, 5 * TerminalGrid.CellHeight);
        var expected = session.ScreenToMap(5, 5);
        session.SelectCell(expected);
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = pointer,
            ShiftPressed = true,
        });
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion
        {
            Position = pointer + new Vector2(2 * TerminalGrid.CellWidth, 0),
        });
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = false,
        });
        Require(session.Selection?.Width == 3 && session.Selection.Value.Contains(expected),
            "Mouse drag selection did not reach the session.");
        var farCell = session.ScreenToMap(12, 9);
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = new Vector2(12 * TerminalGrid.CellWidth, 9 * TerminalGrid.CellHeight),
            ShiftPressed = true,
        });
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = false,
        });
        Require(session.Selection is { } grown && grown.Contains(expected) && grown.Contains(farCell),
            "Shift-click did not extend the selection to the clicked cell.");
        session.ClearSelection();
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.WheelDown,
            Pressed = true,
            CtrlPressed = true,
            Position = pointer,
        });
        Require(session.ZoomLevel == -1, "Mouse wheel zoom failed.");
        session.SetZoom(0);
        var center = new Pos(map.Width / 2, map.Height / 2);
        session.PlaceCursor(center);
        session.CenterOn(center);
        host.Map.RefreshCells();
        VerifyAnimation(host);
        VerifySessionWorkflows(host);
    }

    private static void VerifySessionWorkflows(Main host)
    {
        var session = host.Session;
        void Press(Key code, bool ctrl = false) =>
            host._UnhandledInput(new InputEventKey { Keycode = code, Pressed = true, CtrlPressed = ctrl });
        Pos Vacant() => Enumerable.Range(0, session.Game.Map.Width * session.Game.Map.Height)
            .Select(i => session.Game.Map.PosOf(i)).First(p => session.Game.CanBuildOn(p.X, p.Y));

        foreach (var (key, title) in new[]
        {
            (Key.F1, HelpContent.Title), (Key.F7, "Weekly Report"),
            (Key.F8, "Growth and road access"), (Key.Escape, "GAME MENU"),
        })
        {
            if (key == Key.Escape) OpenGameMenu(host);
            else Press(key);
            Require(session.Prompt?.Title == title, $"{key} did not open {title}.");
            bool debug = session.InputDebug;
            Press(Key.F12);
            Require(session.Prompt?.Title == title && session.InputDebug == debug, "Dialog allowed a background action.");
            if (session.Prompt is not null) Press(Key.Escape);
        }
        Press(Key.F6);
        Require(session.Prompt is { Title: "TermCity guide", Tabs.Count: 9, ActiveTab: 0 }, "F6 did not open the tabbed guide.");
        Require(session.Prompt!.Tabs!.Single(tab => tab.Title == "Milestones").Text.Contains("Great City", StringComparison.Ordinal),
            "Guide must include the new Milestones tab with every settlement size.");
        Press(Key.Right);
        Require(session.Prompt?.ActiveTab == 1 && session.Prompt.Tabs![1].Title == "Zones", "Right arrow did not switch guide tab.");
        Press(Key.Left);
        Press(Key.Left);
        Require(session.Prompt?.ActiveTab == 8 && session.Prompt.Tabs![8].Title == "Glossary",
            "Left arrow did not wrap to the glossary.");
        Press(Key.Escape);
        Require(session.Prompt is null, "Esc did not close the guide.");
        if (session.GuideVisible)
        {
            Press(Key.F6);
            session.SelectPrompt(1);
            Require(!session.GuideVisible && session.Game.GuideDismissed, "Guide tip was not dismissed.");
        }
        Press(Key.F12);
        Require(session.InputDebug, "Input diagnostics were not enabled.");
        Press(Key.F12);
        Press(Key.E);
        Require(session.EdgeScrollEnabled, "Edge scrolling was not enabled.");
        Press(Key.E);

        session.PlaceCursor(Vacant());
        Press(Key.S);
        Press(Key.Right);
        Press(Key.S);
        Require(session.Selection?.Width == 2 && session.Anchor is null, "Keyboard selection mode failed.");
        OpenGameMenu(host);
        Require(session.Prompt?.Title == "GAME MENU", "Esc did not open the game menu with an active selection.");
        Press(Key.Escape);
        session.ClearSelection();
        session.PlaceCursor(Vacant());
        var start = session.Cursor;
        Press(Key.T);
        Press(Key.Right);
        Require(session.RoadToolActive && session.Preview?.Area.Width == 2, "Road line endpoint was blocked.");
        Press(Key.R);
        Require(session.RoadToolActive, "Road line allowed a background action.");
        Press(Key.Enter);
        Require(session.Game.Map.HasRoad(start.X, start.Y), "Road line confirmation failed.");
        host._UnhandledInput(new InputEventKey
        {
            Keycode = Key.Z,
            Pressed = true,
            CtrlPressed = !OS.HasFeature("macos"),
            MetaPressed = OS.HasFeature("macos")
        });
        Require(session.Prompt?.Title == "Undo last action", "Undo was not guarded.");
        Press(Key.Enter);
        Require(!session.Game.Map.HasRoad(start.X, start.Y), "Undo did not restore the city.");

        session.PlaceCursor(Vacant());
        Press(Key.R);
        Press(Key.U);
        Require(session.Game.Map.ZoneAt(session.Cursor.X, session.Cursor.Y) == ZoneType.None, "Dezone failed.");
        Press(Key.R);
        Press(Key.D);
        Require(session.Preview?.Kind == PlacementKind.Demolish, "Demolition preview was not shown.");
        Press(Key.N);
        Require(session.Game.Map.ZoneAt(session.Cursor.X, session.Cursor.Y) == ZoneType.Residential,
            "Cancelled demolition changed the map.");
        Press(Key.D);
        Press(Key.Y);
        Require(session.Game.Map.ZoneAt(session.Cursor.X, session.Cursor.Y) == ZoneType.None, "Demolition failed.");

        Press(Key.M);
        Require(session.Prompt?.Title == "Area menu", "Area menu was not opened.");
        session.SelectPrompt(1);
        Require(session.Prompt?.Title == "Roads", "Road menu was not opened.");
        var avenue = session.Game.Map.Content.Roads.Get(DefaultRoads.CobbledName);
        int avenueChoice = session.Prompt!.Choices.ToList().FindIndex(c => c.Label == DefaultRoads.CobbledName && c.Cells?[0] == "Fill area");
        Require(avenueChoice >= 0, "Cobbled Road was missing from the road menu.");
        session.SelectPrompt(avenueChoice);
        Require(session.Preview?.Road == avenue, "Road menu did not preview the selected type.");
        Press(Key.Escape);
        session.ShowAreaMenu();
        session.SelectPrompt(2);
        Require(session.Prompt?.Title == "Service buildings" && (session.Prompt.Text == "Choose a building, then click or press Enter to place it." ||
            session.Prompt.Text == "No service buildings are registered."), "Building menu was not shown or explained.");
        Press(Key.Escape);
        Require(session.Prompt?.Title == "Area menu", "Service cancellation did not restore the area menu.");
        Press(Key.Escape);

        string quickSave = session.SavePath;
        Require(session.QuickSave(), "Quick-save failed.");
        string saved = SaveGameStore.Serialize(session.Game);
        Press(Key.R);
        Press(Key.F9);
        Require(session.Prompt?.Title == "Confirm load city", "Quick-load discarded unsaved changes.");
        session.SelectPrompt(1);
        Require(saved == SaveGameStore.Serialize(session.Game), "Quick-load changed saved city data.");
        session.ShowLoadMenu();
        Require(session.Prompt?.Choices.Count == 6, "Load menu omitted autosaves or file path.");
        session.SelectPrompt(4);
        Require(session.Prompt?.Input == quickSave, "Load file field did not show its initial path.");
        session.Prompt!.Input = Path.Combine(Path.GetDirectoryName(quickSave)!, "missing.json");
        session.SelectPrompt(0);
        Require(session.MessageKind == MessageKind.Error && session.Prompt?.Title == "Load file",
            "Load failure did not leave a recoverable error.");
        Press(Key.Escape);

        int seed = session.Game.Config.Seed;
        session.RequestNewCity(restart: true);
        Require(session.Game.Config.Seed == seed && session.Game.Paused &&
            session.Prompt is null &&
            session.GuideVisible == (session.Game.Config.Scenario == CityScenario.Random),
            "Restart did not preserve the seed and scenario-appropriate guide behavior.");
        if (session.Prompt is not null) Press(Key.Escape);
        session.DismissGuide();
        session.CenterOn(new Pos(session.Game.Map.Width / 2, session.Game.Map.Height / 2));
        host.Map.RefreshCells();
        if (File.Exists(quickSave)) File.Delete(quickSave);
    }

    private static void VerifyAnimation(Main host)
    {
        var session = host.Session;
        var grid = host.Map.Grid;
        var map = session.Game.Map;
        session.Game.Paused = true;
        foreach (var kind in new[] { TerminalGrid.AnimationKind.Hill, TerminalGrid.AnimationKind.Tree, TerminalGrid.AnimationKind.Water })
        {
            if (kind == TerminalGrid.AnimationKind.Hill &&
                !Enumerable.Range(0, map.Width * map.Height)
                    .Any(i => map.TerrainAt(i % map.Width, i / map.Width).Name == "Hill")) continue;
            var position = Enumerable.Range(0, map.Width * map.Height)
                .Select(i => new Pos(i % map.Width, i / map.Width))
                .First(p => TerminalGrid.ShouldAnimate(p) &&
                    (kind != TerminalGrid.AnimationKind.Tree
                        ? map.Content.Terrains.Get(kind.ToString()).Glyphs.Contains(
                            TermCity.Core.Rendering.CellRenderer.Render(session.Game, p.X, p.Y).Glyph)
                        : map.Content.Features.Get("Tree").Glyphs.Contains(
                            TermCity.Core.Rendering.CellRenderer.Render(session.Game, p.X, p.Y).Glyph)));
            session.CenterOn(position);
            host.Map.RefreshCells();
            var screen = Enumerable.Range(0, grid.Columns * grid.Rows)
                .Select(i => new Pos(i % grid.Columns, i / grid.Columns))
                .First(p => session.ScreenToMap(p.X, p.Y) == position);
            Require(grid.AnimationAt(screen.X, screen.Y) == kind, $"{kind} was not marked for animation.");
            var before = grid.OffsetAt(session, screen.X, screen.Y);
            int rebuilds = grid.Rebuilds;
            string saved = SaveGameStore.Serialize(session.Game);
            host._Process(TerminalGrid.BeatSeconds);
            Require(before != grid.OffsetAt(session, screen.X, screen.Y), $"Paused {kind} did not animate.");
            Require(rebuilds == grid.Rebuilds, "Animation rebuilt base cells.");
            Require(saved == SaveGameStore.Serialize(session.Game), "Animation mutated gameplay.");
            var offset = grid.OffsetAt(session, screen.X, screen.Y);
            Require(kind == TerminalGrid.AnimationKind.Hill ? offset.X == 0 : offset.Y == 0,
                $"{kind} moved on the wrong axis.");
        }
        var road = map.RoadCells.Select(map.PosOf)
            .OrderBy(position => Math.Abs(position.X - map.Width / 2) + Math.Abs(position.Y - map.Height / 2)).First();
        session.CenterOn(road);
        host.Map.RefreshCells();
        session.Game.Paused = false;
        for (int i = 0; i < 120; i++) host.Director.Update(0.25);
        var life = host.Effects.Effects.OfType<AmbientLife>().Single();
        Require(life.Cars > 0 && life.Birds > 0, "Pause check needs active traffic and birds.");
        EffectSprite[] Actors() => host.Effects.Sprites.Where(sprite =>
            sprite.Color == EffectGlyphs.CarColor || sprite.Color == EffectGlyphs.PersonColor ||
            sprite.Color == EffectGlyphs.BirdColor).ToArray();
        var actors = Actors();
        host._UnhandledInput(new InputEventKey { Keycode = Key.P, Pressed = true });
        Require(session.Game.Paused, "P did not pause the game for the ambient check.");
        for (int i = 0; i < 30; i++)
        {
            host._Process(0.1);
            Require(life.TrafficAndBirdsPaused && actors.SequenceEqual(Actors()),
                "Traffic or birds moved, spawned or flapped while the game was paused.");
        }
        host._UnhandledInput(new InputEventKey { Keycode = Key.P, Pressed = true });
        host.Director.Update(0.1);
        Require(!session.Game.Paused && !life.TrafficAndBirdsPaused && !actors.SequenceEqual(Actors()),
            "Traffic and birds did not resume with the game.");
        session.Game.Paused = true;
        host.GetWindow().EmitSignal(Window.SignalName.FocusExited);
        double seconds = grid.AnimationSeconds;
        host._Process(TerminalGrid.BeatSeconds);
        Require(seconds == grid.AnimationSeconds, "Unfocused animation continued.");
        host.GetWindow().EmitSignal(Window.SignalName.FocusEntered);
        session.CenterOn(new Pos(map.Width / 2, map.Height / 2));
        host.Map.RefreshCells();
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void VerifyMapPadding(Main host)
    {
        host.Map.RefreshCells();
        var frame = host.Map.GetParent<TerminalFrame>();
        var inner = frame.InnerBorderBounds;
        var padding = new[]
        {
            host.Map.Position.X - inner.Position.X,
            host.Map.Position.Y - inner.Position.Y,
            inner.End.X - host.Map.Position.X - host.Map.Size.X,
            inner.End.Y - host.Map.Position.Y - host.Map.Size.Y,
        };
        Require(frame.VisibleContentPadding == TerminalFrame.Inset - 4 &&
            padding.All(value => Math.Abs(value - (TerminalFrame.Inset - 4.5f)) < 0.01),
            $"Map padding must be equal relative to the visible inner borders: {string.Join(", ", padding)}.");
        float excessWidth = host.Map.Grid.Columns * host.Map.Grid.PixelWidth - host.Map.Size.X;
        float excessHeight = host.Map.Grid.Rows * host.Map.Grid.PixelHeight - host.Map.Size.Y;
        Require(excessWidth >= 0 && excessWidth < host.Map.Grid.PixelWidth &&
            excessHeight >= 0 && excessHeight < host.Map.Grid.PixelHeight,
            "The map buffer must fill its frame and clip partial edge cells.");
        var sidebar = Descendants(host).OfType<CityPanel>().Single();
        var sidebarFrame = sidebar.GetParent<TerminalFrame>();
        Require(sidebarFrame is TerminalFrame && sidebarFrame.Name == "SidebarFrame",
            "The sidebar must retain its enclosing double-white frame.");
        Require(Math.Abs(sidebarFrame.GetGlobalRect().Position.X - frame.GetGlobalRect().End.X - TerminalFrame.Inset) < 0.01,
            "The map/sidebar gap must match the internal map padding.");
        var sidebarPadding = new[]
        {
            sidebar.Position.X - sidebarFrame.InnerBorderBounds.Position.X,
            sidebar.Position.Y - sidebarFrame.InnerBorderBounds.Position.Y,
            sidebarFrame.InnerBorderBounds.End.X - sidebar.Position.X - sidebar.Size.X,
            sidebarFrame.InnerBorderBounds.End.Y - sidebar.Position.Y - sidebar.Size.Y,
        };
        var railFrame = Descendants(host).OfType<ScrollContainer>()
            .Single(control => control.Name == "ConstructionSidebar").GetParent<TerminalFrame>();
        Require(sidebarFrame.VisibleContentPadding == railFrame.VisibleContentPadding &&
            sidebarPadding.All(value => Math.Abs(value - (Main.RailPadding - 0.5f)) < 0.01),
            $"Right sidebar padding must be equal and match the construction rail: {string.Join(", ", sidebarPadding)}.");
        Require(frame.Size.X <= frame.GetParent<Control>().Size.X + 0.01 &&
            frame.Size.Y <= frame.GetParent<Control>().Size.Y + 0.01,
            "The fitted map frame exceeds its available space.");
    }

    private static void VerifyDialogButtonBorders(Control dialog)
    {
        foreach (var button in Descendants(dialog).OfType<Button>())
            foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
            {
                Require(!button.Flat && button.GetThemeStylebox(state) is StyleBoxFlat style &&
                    style.BorderColor == Colors.White &&
                    new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }.All(side => style.GetBorderWidth(side) == 1),
                    $"Dialog button {button.Text} must have one white outline in its {state} state.");
            }
    }

    private static void VerifyTerminalJoins(Main host)
    {
        var map = host.Map.GetParent<TerminalFrame>();
        var rail = Descendants(host).OfType<ScrollContainer>().Single(n => n.Name == "ConstructionSidebar")
            .GetParent<TerminalFrame>();
        var sidebar = Descendants(host).OfType<TerminalFrame>().Single(n => n.Name == "SidebarFrame");
        Require(rail.JoinAdjacent && rail.JoinRight && rail.JoinTop && map.JoinLeft &&
            map.JoinRight && sidebar.JoinLeft && map.JoinTop && sidebar.JoinTop,
            $"Adjacent gameplay frames must share borders: rail={rail.GetGlobalRect()} joins={rail.JoinRight}/{rail.JoinTop}, " +
            $"map={map.GetGlobalRect()} joins={map.JoinLeft}/{map.JoinRight}/{map.JoinTop}, " +
            $"sidebar={sidebar.GetGlobalRect()} joins={sidebar.JoinLeft}/{sidebar.JoinTop}; headers=" +
            string.Join("; ", Descendants(host).OfType<TerminalFrame>()
                .Where(f => f.VisibleVerticalContentPadding is not null).Select(f => f.GetGlobalRect().ToString())));
        Require(Math.Abs(rail.GlobalPosition.X + rail.BorderBounds.End.X -
            map.GlobalPosition.X - map.BorderBounds.Position.X) < 0.01 &&
            Math.Abs(map.GlobalPosition.X + map.BorderBounds.End.X -
            sidebar.GlobalPosition.X - sidebar.BorderBounds.Position.X) < 0.01,
            "Each divider must have one shared double border, not two neighboring border pairs.");
        float top = map.GlobalPosition.Y + map.BorderBounds.Position.Y;
        Require(Math.Abs(rail.GlobalPosition.Y + rail.BorderBounds.Position.Y - top) < 0.01 &&
            Math.Abs(sidebar.GlobalPosition.Y + sidebar.BorderBounds.Position.Y - top) < 0.01,
            "The horizontal border at three-way joins must stay continuous.");
        Require(!rail.JoinLeft && !sidebar.JoinRight &&
            rail.HorizontalEndpoints(rail.JoinTop).Left == rail.InnerBorderBounds.Position.X &&
            sidebar.HorizontalEndpoints(sidebar.JoinTop).Right == sidebar.InnerBorderBounds.End.X,
            "Window-edge junctions must leave the outer vertical line separate from the horizontal branches.");
    }

    private static void VerifySidebarTables(Main host)
    {
        var panel = Descendants(host).OfType<CityPanel>().Single();
        Require(!Descendants(panel).OfType<Label>().Any(label => label.Text is "GUIDE" or "ZONES" or "MINIMAP"),
            "The sidebar still includes a removed section.");
        Require(!Descendants(panel).Any(node => node.Name == "ZonesTable"),
            "The Zones table was not removed.");
        Require(!Descendants(panel).Any(node => node.Name == "MINIMAPHeaderCell") &&
            panel.Minimap.GetIndex() == 2 &&
            Descendants(panel).OfType<Label>().Any(label => label.Name == "ZoomIndicator"),
            "The minimap must appear without a header and retain its zoom controls.");
        var zoomFrame = Descendants(panel).OfType<PanelContainer>().Single(control => control.Name == "MinimapZoomFrame");
        var cameraControls = Descendants(panel).OfType<HBoxContainer>().Single(control => control.Name == "MinimapCameraControls");
        Require(cameraControls.GetIndex() < zoomFrame.GetIndex(),
            "TILT and ROTATE must be above the minimap zoom controls.");
        var tiltButton = Descendants(cameraControls).OfType<Button>().Single(button => button.Text == "TILT");
        var rotateButton = Descendants(cameraControls).OfType<Button>().Single(button => button.Text == "ROTATE");
        Require(!Descendants(host.Map).OfType<Button>().Any(button => button.Text is "OVERHEAD" or "3D VIEW"),
            "The map must not show the removed overhead toggle.");
        foreach (var button in new[] { tiltButton, rotateButton })
            foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
                Require(button.GetThemeStylebox(state) is StyleBoxFlat style &&
                    style.BgColor == Colors.Black && style.BorderColor == Colors.White &&
                    new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }.All(side => style.GetBorderWidth(side) == 1),
                    "Each camera button must have its own single white border on black.");
        if (host.Map.PerspectiveEnabled && host.Map.Perspective is { } camera && host.Session.Prompt is null &&
            host.Session.Preview is null)
        {
            float azimuth = camera.Azimuth, elevation = camera.Elevation;
            rotateButton.EmitSignal(BaseButton.SignalName.Pressed);
            Require(camera.Azimuth == Mathf.PosMod(azimuth + 90, 360), "ROTATE must advance by 90 degrees.");
            for (int i = 0; i < 3; i++) rotateButton.EmitSignal(BaseButton.SignalName.Pressed);
            Require(camera.Azimuth == azimuth, "Four ROTATE clicks must return to the original heading.");
            tiltButton.EmitSignal(BaseButton.SignalName.Pressed);
            Require(camera.Elevation != elevation, "TILT must change camera elevation.");
            tiltButton.EmitSignal(BaseButton.SignalName.Pressed);
            Require(camera.Elevation != elevation, "TILT must provide three distinct elevations.");
            tiltButton.EmitSignal(BaseButton.SignalName.Pressed);
            Require(camera.Elevation == elevation, "Three TILT clicks must return to the original elevation.");
        }
        Require(Descendants(zoomFrame).OfType<Label>().Single(label => label.Name == "ZoomIndicator")
            .GetThemeFontSize("font_size") == 20, "Zoom-level text must retain its enlarged 20-point size.");
        Require(zoomFrame is not TerminalFrame &&
            zoomFrame.GetThemeStylebox("panel") is StyleBoxFlat zoomStyle &&
            zoomStyle.BgColor == Colors.Black && zoomStyle.BorderColor == Colors.White &&
            new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }
                .All(side => zoomStyle.GetBorderWidth(side) == 1 && zoomStyle.GetContentMargin(side) == 1),
            "The minimap zoom section must have one white border on black with even padding.");
        Require(Descendants(zoomFrame).OfType<Button>().Count() == 2 &&
            Descendants(zoomFrame).OfType<Button>().All(button => button.CustomMinimumSize == Vector2.Zero &&
                button.GetThemeFontSize("font_size") == 22 &&
                button.Alignment == HorizontalAlignment.Center &&
                Math.Abs(button.Size.Y - button.GetThemeFont("font").GetHeight(22) - 4) < 1 &&
                Math.Abs(button.Size.X - button.GetThemeFont("font").GetStringSize(button.Text, fontSize: 22).X - 4) < 1 &&
                new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" }.All(state =>
                    new[] { Side.Left, Side.Top, Side.Right, Side.Bottom }
                        .All(side => button.GetThemeStylebox(state).GetContentMargin(side) == 2))),
            "Zoom brackets must have tight equal padding on every side without forced extra space.");
        foreach (string title in new[] { "DEMAND", "CITY" })
        {
            var cell = Descendants(panel).OfType<PanelContainer>().Single(control => control.Name == title + "HeaderCell");
            Require(cell is TerminalFrame && cell.GetChild<Label>(0).Text == title &&
                cell.GetChild<Label>(0).GetThemeFontSize("font_size") == 22 &&
                cell.GetThemeStylebox("panel") is StyleBoxFlat { BgColor: var background } && background == Colors.Black,
                $"{title} does not have the shared double-white frame on a black background.");
        }
        Verify("CityTable", SidebarReport.CityRows(host.Session.Game));
        var city = Descendants(panel).OfType<GridContainer>().Single(control => control.Name == "CityTable");
        Require(city.GetGlobalRect().End.Y <= panel.GetGlobalRect().End.Y + 1 &&
            city.Size.X <= panel.Size.X && !panel.GetParent()!.GetChildren().OfType<ScrollContainer>().Any(),
            $"Sidebar must fit without scrolling: panel={panel.GetGlobalRect()}, city={city.GetGlobalRect()}, " +
            $"content minimum={panel.GetChild<Control>(0).GetCombinedMinimumSize()}, font={host.FontSize}.");
        foreach (var zone in Zones.Placeable)
        {
            var row = Descendants(panel).OfType<HBoxContainer>().Single(control => control.Name == $"Demand{Zones.Get(zone).Letter}Row");
            var demandLetter = row.GetChild<Label>(0);
            var demandBar = row.GetChild<ProgressBar>(1);
            Require(demandLetter.GetThemeFontSize("font_size") == 18 &&
                demandLetter.VerticalAlignment == VerticalAlignment.Center,
                "Demand letters must retain balanced, vertically centered 18-point text.");
            Require(Math.Abs(demandBar.Size.Y - demandLetter.GetThemeFontSize("font_size")) < 1 &&
                Math.Abs(demandBar.GetGlobalRect().GetCenter().Y - demandLetter.GetGlobalRect().GetCenter().Y) < 1,
                "Demand bars must match the lettering size and share its vertical center.");
            string details = CityReport.ZoneDetails(host.Session.Game, zone);
            foreach (var control in row.GetChildren().OfType<Control>().Prepend(row))
                Require(control.MouseFilter == Control.MouseFilterEnum.Stop && control.TooltipText.Contains(details),
                    $"{Zones.Get(zone).Letter} hover details are missing from its demand row.");
        }

        var frames = Descendants(host).OfType<TerminalFrame>().Where(frame => frame.IsVisibleInTree()).ToArray();
        for (int i = 0; i < frames.Length; i++)
            for (int j = i + 1; j < frames.Length; j++)
                if (!frames[i].IsAncestorOf(frames[j]) && !frames[j].IsAncestorOf(frames[i]))
                    Require(!frames[i].GetGlobalRect().Intersects(frames[j].GetGlobalRect()),
                        "Independent terminal frame borders overlap.");

        void Verify(string name, IReadOnlyList<SidebarCell[]> rows)
        {
            var table = Descendants(panel).OfType<GridContainer>().Single(control => control.Name == name);
            int expectedCells = (rows.Count * 2 + table.Columns - 1) / table.Columns * table.Columns;
            Require(table.Columns is 2 or 4 && table.GetChildCount() == expectedCells &&
                !Descendants(table).OfType<Label>().Any(label => label.Text is "METRIC" or "VALUE"),
                $"{name} has missing table cells.");
            Require(table.GetThemeConstant("h_separation") == 0 && table.GetThemeConstant("v_separation") == 0,
                "City table cells must meet in a continuous terminal grid.");
            for (int index = 0; index < table.GetChildCount(); index++)
            {
                var cell = table.GetChild<PanelContainer>(index);
                Require(cell.GetThemeStylebox("panel") is StyleBoxFlat style &&
                    style.BgColor == (index / table.Columns % 2 != 0 ? new Color("#101010") : Colors.Black) &&
                    style.BorderColor == Colors.White &&
                    style.BorderWidthLeft == (index % table.Columns == 0 ? 1 : 0) &&
                    style.BorderWidthTop == (index < table.Columns ? 1 : 0) &&
                    style.BorderWidthRight == 1 && style.BorderWidthBottom == 1 &&
                    style.CornerRadiusTopLeft == 0 && style.CornerRadiusTopRight == 0 &&
                    style.CornerRadiusBottomLeft == 0 && style.CornerRadiusBottomRight == 0,
                    "City table must use shared single-white edges and neutral terminal shading.");
            }
            for (int row = 0; row < rows.Count; row++)
                for (int column = 0; column < 2; column++)
                {
                    var label = table.GetChild<PanelContainer>(row * 2 + column).GetChild<Label>(0);
                    var expected = rows[row][column];
                    bool gaugeOnly = column == 1 && (rows[row][0].Text is "Fuel" or "Water" ||
                        rows[row][0].Text == "Grain" && host.Session.Game.GrainRequired);
                    if (gaugeOnly)
                    {
                        var gauge = label.GetChildren().OfType<ResourceGauge>().Single();
                        var supply = rows[row][0].Text == "Fuel" ? host.Session.Game.Services.Power : host.Session.Game.Services.Water;
                        double level = rows[row][0].Text == "Grain" ? SidebarReport.GrainLevel(host.Session.Game) :
                            SidebarReport.SupplyLevel(supply);
                        Require(Math.Abs(gauge.Level - level) < 1e-6 &&
                            gauge.MouseFilter == Control.MouseFilterEnum.Ignore &&
                            gauge.Size.X >= 8 && gauge.CustomMinimumSize.Y == 18 &&
                            ResourceGauge.BarHeight == 14 && gauge.GetRect().End.X <= label.Size.X -
                                label.GetThemeFont("font").GetStringSize(label.Text,
                                    fontSize: label.GetThemeFontSize("font_size")).X,
                            "Resource gauges must show raw supply levels across the available cell.");
                        Require(ResourceGauge.MinimumLevel == 0.5 && ResourceGauge.MaximumLevel == 1.5 &&
                            Math.Abs(gauge.MarkerPosition - Math.Clamp(gauge.Level - 0.5, 0, 1)) < 1e-6,
                            "Utility gauges must omit the 0-50% segment and remap the level marker to 50-150%.");
                        Require(label.Text.Length == 0 && label.TooltipText.Contains(expected.Text, StringComparison.Ordinal) &&
                            label.GetParent<Control>().TooltipText == label.TooltipText &&
                            Math.Abs(gauge.GetRect().End.X - label.Size.X) < 1,
                            "Resource cells must hide numbers in the cell and show them on hover.");
                    }
                    Require(label.GetThemeFontSize("font_size") >= 13,
                        "City table text must use the enlarged body font, not the old 8-13 range.");
                    if (table.Columns == 2)
                        Require(label.GetThemeFont("font").GetStringSize(label.Text,
                            fontSize: label.GetThemeFontSize("font_size")).X <= label.Size.X + 1,
                            $"City text is truncated: {label.Text}, width={label.Size.X}.");
                    Require(label.Text == (gaugeOnly ? "" : expected.Text) && label.GetThemeColor("font_color") ==
                        new Color(expected.Color.R / 255f, expected.Color.G / 255f, expected.Color.B / 255f),
                        $"{name} has incorrect values or colors.");
                }
        }
    }

    private static void OpenGameMenu(Main host)
    {
        host._UnhandledInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        Require(host.Session.Prompt?.Title == "GAME MENU", "Escape during gameplay must open the game menu.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
