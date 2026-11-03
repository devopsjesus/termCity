using System.Reflection;
using Godot;
using TermCity.Core.Persistence;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using static TermCity.GodotApp.SettlementSmoke;
using NVector2 = System.Numerics.Vector2;

namespace TermCity.GodotApp;

internal static class WallWaterSmoke
{
    internal static async Task Run(Main host, string? capture)
    {
        var game = CityGame.New(new GameConfig { MapWidth = 128, MapHeight = 128, Seed = 42, StartingMoney = 2_000_000 });
        for (int y = 0; y < game.Map.Height; y++)
            for (int x = 0; x < game.Map.Width; x++)
            {
                game.Map.ClearCell(x, y);
                game.Map.SetTerrain(x, y, x is >= 68 and <= 73
                    ? game.Map.Content.Terrains.Get("Water") : game.Map.Content.Terrains.Base);
            }
        game.Touch();
        var founded = game.FoundSettlement(new(40, 40, 64, 64));
        Require(founded.Success, founded.Message);
        game.Paused = true;
        var state = game.Settlement!;
        Require(state.Walls.Where(w => w.IsTower).All(w => WorldScale.HasDryTowerFootprint(game.Map, w.Center)),
            "Founding towers must have entirely dry footprints; water crossings stay ordinary grated walls.");
        foreach (var area in game.Map.BuildingFootprints.Values)
            Require(area.Cells().All(p => game.Map.TerrainAt(p.X, p.Y).Buildable && state.AcceptsArrivals(game, p)),
                "A river-crossing bailey must place its starters on the dry, gate-reachable bank.");
        string saved = SaveGameStore.Serialize(game);
        Require(saved == SaveGameStore.Serialize(SaveGameStore.Deserialize(saved)),
            "Water-wall terrain and fortifications must round-trip without rewriting logical water.");
        var session = new GameSession(game);
        session.SetViewport(80, 48);
        session.SetZoom(0);
        var view = new PerspectiveCity { WaterMotionEnabled = false };
        host.AddChild(view);
        var size = new Vector2(960, 900);
        try
        {
            var wall = state.Walls.First(w => w.Cell.Y == state.Bounds.Bottom && w.Cell.X == 70);
            Require(!wall.IsTower, "The river-crossing span must remain a wall rather than generate a tower.");
            GD.Print($"TERMCITY_WATER_WALL_GEOMETRY water={WallWaterLayout.Surface(game.Map, wall.Center):F3} " +
                $"ground={WallDamageLayout.GroundHeight(wall, game.Map):F3} " +
                $"body-min={WallDamageLayout.Fragments(wall).Where(part => part.Kind == WallFragmentKind.Body).Min(part => part.Center.Y - part.Size.Y / 2):F3} " +
                $"grate-top={WallDamageLayout.Fragments(wall).Where(part => part.Kind == WallFragmentKind.Grate).Max(part => part.Center.Y + part.Size.Y / 2):F3}");
            var crossing = new Vector3(70.5f, 2.4f, 103.5f);
            foreach (int turn in new[] { 0, 1 })
            {
                session.CenterOn(wall.Cell);
                view.Sync(session, size);
                view.Rotate(turn == 0 ? 0 : 90);
                view.SetCameraOffset(session, new Vector2(crossing.X, crossing.Z) -
                    new Vector2(session.CameraX + session.ViewWidth * session.Stride / 2f,
                        session.CameraY + session.ViewHeight * session.Stride / 2f));
                view.Sync(session, size);
                var camera = (Camera3D)typeof(PerspectiveCity).GetField("_camera",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
                float azimuth = Mathf.DegToRad(view.Azimuth);
                camera.Position = crossing + new Vector3(Mathf.Sin(azimuth) * .85f, .52f,
                    Mathf.Cos(azimuth) * .85f) * 100;
                camera.LookAt(crossing, Vector3.Up);
                camera.Size = turn == 0 ? 10 : 14;
                camera.MakeCurrent();
                camera.ForceUpdateTransform();
                view.RequestRender();
                await Frames(host);
                Require(WallDamageLayout.Fragments(wall).Any(part => part.Kind == WallFragmentKind.Grate),
                    "The rendered river span must have real grated collision/picking geometry.");
                var grate = WallDamageLayout.Fragments(wall).First(part =>
                    part.Kind == WallFragmentKind.Grate && part.Size.Y > .2f);
                var normal = new NVector2(-wall.Tangent.Y, wall.Tangent.X);
                var p = wall.Center + wall.Tangent * grate.Center.X + normal * grate.Center.Z;
                var point = new Vector3(p.X, WallDamageLayout.GroundHeight(wall, game.Map) +
                    grate.Center.Y + grate.Size.Y / 2 - .05f, p.Y);
                bool picked = view.PickBuilding(view.ScreenPoint(point), out _);
                Require(picked, "A visible iron grate must remain pickable as fortification geometry.");
                int geometry = view.GeometryBuilds, walls = view.SiegeGeometryBuilds;
                session.PlaceCursor(wall.Cell);
                session.RememberWallPick(wall.Id);
                session.SelectCell(wall.Cell);
                view.Sync(session, size);
                Require(view.GeometryBuilds == geometry && view.SiegeGeometryBuilds == walls,
                    "Selecting water-wall grates must retain cached city/fortification geometry.");
                if (capture is not null)
                {
                    view.RequestRender();
                    var rendered = host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    RenderingServer.ForceDraw(false);
                    await rendered;
                    using var image = view.CaptureDisplayImage();
                    var error = image.SavePng(Path.ChangeExtension(capture, $".water-wall-{turn}.png"));
                    if (error != Error.Ok) throw new IOException($"Water-wall capture failed: {error}");
                }
            }
            var home = game.Map.Content.Buildings.Get("Cottage");
            var plot = game.VacantPlots(TermCity.Core.World.ZoneType.Residential).First();
            game.Map.SetBuildingFootprint(home, plot);
            game.Map.SetHousehold(plot.X, plot.Y, new(3, 2, 0));
            game.Touch();
            for (int day = 0; day <= SettlementFootbridge.ConstructionDays; day++)
            {
                game.AdvanceDay();
                if (day is not (1 or SettlementFootbridge.ConstructionDays)) continue;
                var bridge = game.Map.Footbridges.Single();
                var center = new Vector3((bridge.Start.X + bridge.End.X) / 2f + .5f,
                    bridge.DeckHeight(game.Map), (bridge.Start.Y + bridge.End.Y) / 2f + .5f);
                session.CenterOn(new((int)center.X, (int)center.Z));
                view.Sync(session, size);
                view.SetCameraOffset(session, new Vector2(center.X, center.Z) -
                    new Vector2(session.CameraX + session.ViewWidth * session.Stride / 2f,
                        session.CameraY + session.ViewHeight * session.Stride / 2f));
                view.Sync(session, size);
                var camera = (Camera3D)typeof(PerspectiveCity).GetField("_camera",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
                camera.Position = center + new Vector3(-60, 65, 80);
                camera.LookAt(center, Vector3.Up);
                camera.Size = 12;
                camera.MakeCurrent();
                camera.ForceUpdateTransform();
                view.RequestRender();
                await Frames(host);
                Require(view.CivilianFootbridgesDrawn == 1, "The real path-built bridge must render with its construction progress.");
                GD.Print($"TERMCITY_FOOTBRIDGE_GEOMETRY progress={bridge.Progress} deck={bridge.DeckHeight(game.Map):F3} " +
                    $"rendered-water={view.RenderedGroundHeight(game.Map, center.X, center.Z):F3}");
                if (bridge.Complete)
                {
                    Require(state.AcceptsArrivals(game, new(55, 60)), "Completed footbridges must open the opposite bank for growth.");
                    Require(view.GroundCell(view.ScreenPoint(center)) == new Pos((int)center.X, (int)center.Z),
                        "A bridge deck must be picked at the actual elevated surface.");
                }
                if (capture is not null)
                {
                    view.RequestRender();
                    var rendered = host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    RenderingServer.ForceDraw(false);
                    await rendered;
                    using var image = view.CaptureDisplayImage();
                    var error = image.SavePng(Path.ChangeExtension(capture, bridge.Complete ? ".footbridge-complete.png" : ".footbridge-building.png"));
                    if (error != Error.Ok) throw new IOException($"Footbridge capture failed: {error}");
                }
            }
            if (capture is not null)
            {
                var original = host.Session.Game;
                var replace = typeof(GameSession).GetMethod("ReplaceGame", BindingFlags.Instance | BindingFlags.NonPublic)!;
                try
                {
                    replace.Invoke(host.Session, [game, true]);
                    host.Session.ClosePrompt();
                    host.Session.ClearSelection();
                    host.Session.SetZoom(0);
                    var crossingBridge = game.Map.Footbridges.Single();
                    host.Session.CenterOn(new((crossingBridge.Start.X + crossingBridge.End.X) / 2,
                        (crossingBridge.Start.Y + crossingBridge.End.Y) / 2));
                    host.Map.SetPerspective(false);
                    host.Map.RefreshCells();
                    await Frames(host);
                    await Capture(host, Path.ChangeExtension(capture, ".footbridge-overhead.png"));
                }
                finally
                {
                    replace.Invoke(host.Session, [original, true]);
                    host.Session.ClosePrompt();
                    host.Map.SetPerspective(true);
                    host.Map.Invalidate(true);
                    await Frames(host);
                }
            }
            GD.Print("TERMCITY_CIVILIAN_FOOTBRIDGE_OK progress=daily-path-work builder=visible crossing=completed-only " +
                "banks=connected terrain=water picking=deck");
            GD.Print("TERMCITY_WATER_WALL_OK founding=64x64 starters=dry+reachable grate=submerged " +
                "water=unchanged travel=not-bridge save=roundtrip rotations=2 zooms=2 picking=grate selection=cached");
        }
        finally
        {
            host.RemoveChild(view);
            view.QueueFree();
            await Frames(host);
        }
    }
}
