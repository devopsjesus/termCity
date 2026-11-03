using System.Diagnostics;
using Godot;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.World;

namespace TermCity.GodotApp;

public partial class DepthComparison
{
    private async Task RunTerrainSmoke()
    {
        var untouched = TerrainStudyFixture.Create(0, TerrainStudyTreatment.Current).Game;
        string original = SaveGameStore.Serialize(untouched);
        float water = TerrainSprite.Flow(untouched.Map).At(32, 26).Level;
        for (int subject = 0; subject < TerrainSubjects.Length; subject++)
        {
            SetSubject(subject);
            for (int treatment = 0; treatment < TerrainTreatments.Length; treatment++)
            {
                SetTreatmentIndex(treatment);
                await Frames();
                if (subject == 2)
                    foreach (var owner in new[] { new TermCity.Core.Util.Pos(17, 17), new TermCity.Core.Util.Pos(15, 16) })
                        Require(_renderer.NaturalVolumes(owner).Any(box =>
                        {
                            var pixel = _renderer.ScreenPoint(new(box.GetCenter().X, box.End.Y, box.GetCenter().Z));
                            return new Rect2(Vector2.Zero, _picture.Size).HasPoint(pixel) &&
                                _renderer.PickNaturalFeature(pixel, out var selected) && selected == owner;
                        }), "Visible tree crowns and rock facets must pick their actual feature cell.");
                var map = _session.Game.Map;
                var candidate = (TerrainStudyTreatment)treatment;
                Require(map.PreviewElevationProfile == TerrainStudyFixture.Profile(candidate),
                    "Terrain treatments must use the corresponding private-map elevation profile.");
                Require(_renderer.DepthTreatment == DepthStudyArtwork.DefaultTreatment &&
                    _renderer.TerrainTreatment == candidate && _renderer.StablePixelMaterials,
                    "Terrain studies must retain the improved buildings, palisades and stable pixel filtering.");
                if (TerrainStudyFixture.LitInfrastructure(candidate))
                    Require(_renderer.TerrainStudyNormalsCorrect, "All lit ground, road and bank triangles need correct normals.");
                if (map.TerrainAt(32, 26).Name == "Water")
                    Require(Math.Abs(TerrainSprite.Flow(map).At(32, 26).Level - water) < .000001f,
                        "Changing hill heights must not raise rivers or sea levels.");
                float high = TerrainSprite.Elevation(map, 24, 22);
                Require(Math.Abs(high - TerrainSprite.MaximumHillFor(map)) < .001f,
                    "The transect must contain the full fourth terrace level.");
                int pickingCases = 0;
                foreach (int zoom in new[] { -1, 0 })
                {
                    _session.SetZoom(zoom);
                    _session.CenterOn(new(29, subject == 1 ? 30 : 27));
                    foreach (float tilt in new[] { 20f, 35f, 50f })
                        foreach (float rotation in new[] { 0f, 90f, 180f, 270f })
                        {
                            _renderer.SetDepthStudyCamera(PerspectiveCity.InitialAzimuth + rotation, tilt);
                            Sync();
                            var pixel = _renderer.ScreenPoint(new(24.5f, high, 22.5f));
                            if (new Rect2(Vector2.Zero, _picture.Size).HasPoint(pixel))
                            {
                                Require(_renderer.GroundPoint(pixel).DistanceTo(new(24.5f, high, 22.5f)) < .03f,
                                    $"Terrain picking must follow real heights: scene={subject}, treatment={candidate}, zoom={zoom}, tilt={tilt}, rotation={rotation}.");
                                pickingCases++;
                            }
                        }
                }
                Require(pickingCases >= 12, "Each terrain candidate must exercise visible picking across camera angles.");
                _elevation = 90;
                _renderer.SetDepthStudyCamera(_azimuth, _elevation);
                Sync();
                var focus = new TermCity.Core.Util.Pos(29, subject == 1 ? 30 : 27);
                var overhead = _renderer.ScreenPoint(new(focus.X + .5f,
                    TerrainSprite.Elevation(map, focus.X, focus.Y), focus.Y + .5f));
                Require(new Rect2(Vector2.Zero, _picture.Size).HasPoint(overhead) &&
                    _renderer.GroundCell(overhead) == focus, "Visible overhead picking must retain real terrace heights.");
                ResetCamera();
                _session.CenterOn(new(29, subject == 1 ? 30 : 27));
                Sync();
                await Frames();
                if (subject == 1)
                {
                    Require(_renderer.BridgeSegmentsDrawn > 0 && _renderer.CivilianFootbridgesDrawn == 1,
                        "The village fixture must render both road and civilian bridges at each height.");
                    Require(HasPickableModel(), "The reference buildings must remain clickable.");
                    if (candidate == TerrainStudyTreatment.Balanced && DisplayServer.GetName() != "headless")
                    {
                        await VerifyGroundShadows();
                        await VerifyRoadShadows();
                    }
                    int builds = _renderer.GeometryBuilds, models = _renderer.BuildingsDrawn;
                    _session.PlaceCursor(new(45, 42));
                    _session.PreviewBuilding(map.Content.Buildings.Get("Cottage"));
                    Sync();
                    Require(_renderer.BuildingsDrawn > models, "Terrain candidates must retain real placement ghosts.");
                    Require(!_session.Game.CanPlaceBuilding(map.Content.Buildings.Get("Cottage"), -1, -1),
                        "Changing elevation must not permit off-map placement.");
                    _session.CancelPreview();
                    _session.ClosePrompt();
                    _session.ClearSelection();
                    Sync();
                    Require(_renderer.GeometryBuilds == builds, "Placement indicators must not rebuild terrain geometry.");
                }
                if (subject is 1 or 3)
                    foreach (var area in map.BuildingFootprints.Values)
                    {
                        var owner = new TermCity.Core.Util.Pos(area.X, area.Y);
                        Require(_renderer.BuildingVolumes(owner).Any(volume =>
                        {
                            var pixel = _renderer.ScreenPoint(new(volume.GetCenter().X, volume.End.Y, volume.GetCenter().Z));
                            return new Rect2(Vector2.Zero, _picture.Size).HasPoint(pixel) &&
                                _renderer.PickBuilding(pixel, out var picked) && picked == owner;
                        }), "Every reference building must retain a visible, pickable surface at the selected terrain height.");
                    }
                int geometry = _renderer.GeometryBuilds;
                var reference = new Vector3(24.5f, high, 22.5f);
                var beforePan = _renderer.ScreenPoint(reference);
                var pointer = _picture.Size / 2;
                _picture.EmitSignal(Control.SignalName.GuiInput,
                    new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = true, Position = pointer });
                _picture.EmitSignal(Control.SignalName.GuiInput,
                    new InputEventMouseMotion { Position = pointer + new Vector2(.5f, .25f), ButtonMask = MouseButtonMask.Middle });
                var middlePan = _renderer.ScreenPoint(reference);
                _picture.EmitSignal(Control.SignalName.GuiInput,
                    new InputEventMouseMotion { Position = pointer + new Vector2(1, .5f), ButtonMask = MouseButtonMask.Middle });
                var afterPan = _renderer.ScreenPoint(reference);
                _picture.EmitSignal(Control.SignalName.GuiInput,
                    new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = false });
                Require((middlePan - beforePan).DistanceTo(new(.5f, .25f)) < .05f &&
                    (afterPan - middlePan).DistanceTo(new(.5f, .25f)) < .05f,
                    "Slow terrain drags must follow fractional screen-space motion without snapping to cells or heights.");
                ResetCamera();
                var frames = new List<double>();
                double pick = 0;
                for (int sample = 0; sample < 14; sample++)
                {
                    long start = Stopwatch.GetTimestamp();
                    Rotate(1);
                    long picking = Stopwatch.GetTimestamp();
                    _renderer.GroundCell(_picture.Size / 2);
                    pick = Math.Max(pick, Stopwatch.GetElapsedTime(picking).TotalMilliseconds);
                    await Frames(1);
                    if (sample >= 2) frames.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
                Require(_renderer.GeometryBuilds == geometry, "Buffered terrain rotations must not rebuild map geometry.");
                frames.Sort();
                double p90 = frames[(int)(frames.Count * .9)];
                GD.Print($"TERMCITY_TERRAIN_SAMPLE subject={subject} treatment={candidate} " +
                    $"frame-p90={p90:0.0}ms pick={pick:0.0}ms nodes={_renderer.SceneObjectCount}");
                if (pick >= 50 || DisplayServer.GetName() != "headless" && p90 >= 60 || _renderer.SceneObjectCount >= 512)
                    throw new PerformanceBudgetExceededException(
                        $"Terrain candidate {candidate}: frame {p90:0.0}/60ms, pick {pick:0.0}/50ms, nodes {_renderer.SceneObjectCount}/512.");
                ResetCamera();
            }
            if (DisplayServer.GetName() != "headless")
            {
                SetSubject(subject);
                _session.SetZoom(subject == 3 ? -2 : -1);
                _session.CenterOn(new(29, subject == 1 ? 30 : 27));
                await CaptureTreatments();
                _session.SetZoom(subject == 3 ? -1 : 0);
                _session.CenterOn(new(29, subject == 1 ? 30 : 27));
                await CaptureTreatments("close");
            }
        }
        Require(untouched.Map.PreviewElevationProfile == TerrainElevationProfile.Current &&
            SaveGameStore.Serialize(untouched) == original,
            "Terrain candidates and rendering must not mutate another map or its saved state.");
        GetWindow().Size = new(640, 780);
        await Frames();
        Require(_controls.All(button => button.GetGlobalRect().End.X <= Size.X + 1 &&
            button.GetGlobalRect().End.Y <= Size.Y + 1), "Terrain comparison controls must fit a narrow window.");
        if (DisplayServer.GetName() != "headless")
        {
            var drawn = ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            _renderer.RequestRender();
            RenderingServer.ForceDraw();
            await drawn;
            using var image = GetViewport().GetTexture().GetImage();
            Require(image.SavePng(Path.ChangeExtension(CapturePrefix, null) + "-narrow.png") == Error.Ok,
                "The narrow terrain comparison must save.");
        }
        GD.Print("TERMCITY_TERRAIN_COMPARISON_SMOKE_OK default=3x-new-random profiles=isolated water=unchanged");
        GetTree().Quit();
    }

    private async Task VerifyRoadShadows()
    {
        using var shadowed = await RenderImage();
        _renderer.SetDepthStudyShadows(false);
        using var unshadowed = await RenderImage();
        _renderer.SetDepthStudyShadows(true);
        float ground = TerrainSprite.Elevation(_session.Game.Map, 20, 25);
        var tree = new Aabb(new(18.5f, ground, 23.5f), new(4, 8, 4));
        var points = Enumerable.Range(0, 8).Select(i => _renderer.ScreenPoint(tree.GetEndpoint(i))).ToArray();
        var treeBounds = new Rect2(points[0], Vector2.Zero);
        foreach (var point in points) treeBounds = treeBounds.Expand(point);
        int samples = 0;
        for (int y = 4; y < _picture.Size.Y; y += 4)
            for (int x = 4; x < _picture.Size.X; x += 4)
            {
                var pixel = new Vector2(x, y);
                if (treeBounds.HasPoint(pixel) || _renderer.PickBuilding(pixel, out _)) continue;
                var hit = _renderer.GroundPoint(pixel);
                if (!_renderer.RoadSurfaceAt(new(hit.X, hit.Z)) ||
                    _renderer.BridgeSurfaceAt(new(hit.X, hit.Z))) continue;
                int px = (int)(x * shadowed.GetWidth() / _picture.Size.X);
                int py = (int)(y * shadowed.GetHeight() / _picture.Size.Y);
                var a = shadowed.GetPixel(px, py);
                var b = unshadowed.GetPixel(px, py);
                if (b.R + b.G + b.B - a.R - a.G - a.B > .04f) samples++;
            }
        Require(samples >= 2, $"Lit roads must actually receive cast shadows; found {samples} darkened road samples.");
        GD.Print($"TERMCITY_TERRAIN_ROAD_SHADOWS_OK samples={samples}");
    }
}
