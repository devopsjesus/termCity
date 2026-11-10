using System.Collections;
using System.Reflection;
using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using static TermCity.GodotApp.SettlementSmoke;

namespace TermCity.GodotApp;

internal static class ArcherBowSmoke
{
    internal static async Task Run(Main host, CityGame game, string? capture)
    {
        var state = game.Settlement!;
        var unit = state.Defenders.First(d => d.Kind == DefenderKind.Archer);
        var units = (IList)typeof(SettlementState).GetField("DefenderList", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
        units.Clear();
        units.Add(unit);
        var target = state.Enemies.First();
        var tower = state.Walls.First(w => w.IsTower && w.Cell.Y == state.Bounds.Y && w.Cell.X > state.Bounds.X + 4);
        Set(unit, nameof(unit.X), (double)tower.Center.X);
        Set(unit, nameof(unit.Y), (double)tower.Center.Y);
        Set(unit, nameof(unit.PreviousX), unit.X);
        Set(unit, nameof(unit.PreviousY), unit.Y);
        Set(unit, nameof(unit.Elevation), WallDamageLayout.ActorWalkHeight(tower, game.Map));
        Set(unit, nameof(unit.PreviousElevation), unit.Elevation);
        Set(unit, nameof(unit.TargetEnemyId), target.Id);
        Set(target, nameof(target.X), unit.X);
        Set(target, nameof(target.Y), unit.Y - 7);
        typeof(SettlementState).GetField("Accumulator", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(state, 0d);
        Set(state, nameof(state.Revision), state.Revision + 1);
        var view = host.Map.Perspective!;
        host.Session.ClearDefenderSelection();
        host.Session.CenterOn(tower.Cell);
        host.Map.RefreshCells();
        await Frames(host);
        int geometry = view.GeometryBuilds, walls = view.SiegeGeometryBuilds;
        var camera = (Camera3D)typeof(PerspectiveCity).GetField("_camera", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        var cameraTransform = camera.Transform;
        float cameraSize = camera.Size;
        try
        {
            foreach (var (phase, elapsed) in new[]
            {
                ("raise", .05), ("draw", .18), ("anchor", .29),
                ("release", .32), ("lower", .75), ("nock-start", .96), ("nock", 1.1), ("ready", 1.5),
            })
            {
                Set(unit, nameof(unit.AttackCooldown), Math.Max(0, CombatProfiles.Archer.Interval - elapsed));
                Set(state, nameof(state.Revision), state.Revision + 1);
                host.Session.AdvancePresentation(0, false);
                view.Sync(host.Session, host.Map.Size);
                Require(view.ArcherArmSegmentsDrawn == 4 && view.ArcherStringSegmentsDrawn == 2,
                    "The archer needs two articulated arms and an actual two-segment bowstring.");
                var pose = ArcherArtwork.Pose(elapsed, elapsed < CombatProfiles.Archer.Interval, view.SiegeAnimationEnabled);
                Require(view.NockedArrowsDrawn == (pose.ArrowVisible ? 1 : 0),
                    $"The held arrow must follow actual draw/release/nocking state: {phase}.");
                Require(view.GeometryBuilds == geometry && view.SiegeGeometryBuilds == walls,
                    "Bow movement must update actor batches without rebuilding city or fortifications.");
                for (int turn = 0; turn < 2; turn++)
                {
                    var at = view.DefenderPosition(unit, state.InterpolationFraction);
                    var focal = at + Vector3.Up * 1.05f;
                    camera.Position = focal + new Vector3(turn == 0 ? 70 : -70, 50, -75);
                    camera.LookAt(focal, Vector3.Up);
                    camera.Size = turn == 0 ? 3.2f : 4;
                    camera.MakeCurrent();
                    camera.ForceUpdateTransform();
                    view.RequestRender();
                    await Frames(host);
                    view.RequestRender();
                    if (DisplayServer.GetName() != "headless")
                    {
                        var rendered = host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                        RenderingServer.ForceDraw(false);
                        await rendered;
                    }
                    var chest = view.ScreenPoint(at + Vector3.Up * 1.15f);
                    Require(view.PickSiegeActor(chest, out bool friendly, out int id) && friendly && id == unit.Id,
                        "The articulated archer must retain frontmost model picking.");
                    if (capture is null) continue;
                    using var image = view.CaptureDisplayImage();
                    var error = image.SavePng(Path.ChangeExtension(capture, $".bow-{phase}-{turn}.png"));
                    if (error != Error.Ok) throw new IOException($"Bow capture failed: {error}");
                }
            }
        }
        finally
        {
            camera.Transform = cameraTransform;
            camera.Size = cameraSize;
            camera.ForceUpdateTransform();
            view.RequestRender();
        }
        GD.Print("TERMCITY_ARCHER_BOW_OK arms=articulated anchor=cheek string=taut release=committed " +
            "nocking=visible rotations=2 zooms=2 picking=retained geometry=cached");
    }

    private static void Set(object target, string property, object value) => target.GetType().GetProperty(property)!.SetValue(target, value);
}
