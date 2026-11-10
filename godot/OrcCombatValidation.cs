using System.Diagnostics;
using System.Reflection;
using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Persistence;
using TermCity.Core.Simulation;
using TermCity.Core.Util;

namespace TermCity.GodotApp;

public partial class OrcCombatValidation : Control
{
    private static void Set(object target, string property, object value) =>
        target.GetType().GetProperty(property)!.SetValue(target, value);

    public override async void _Ready()
    {
#if !DEBUG
        GD.PushError("Orc combat validation requires Debug.");
        GetTree().Quit(1);
        return;
#endif
        try
        {
            var host = GD.Load<PackedScene>("res://Main.tscn").Instantiate<Main>();
            AddChild(host); host.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            for (int i = 0; i < 600 && (host.Session is null || host.Map is null); i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (host.Session?.Game.Settlement is not { } siege)
                throw new InvalidOperationException("Load the 250-resident siege fixture with --load.");
            host.SetProcess(false);
            host.Session.Game.Paused = true;
            while (host.Session.BackgroundSimulationPending || host.Session.PendingSimulationSeconds > 0)
            {
                host.Session.UpdateResponsive(0);
                await Task.Delay(1);
            }
            var game = host.Session.Game;
            var map = host.Map ?? throw new InvalidOperationException("Map did not initialize.");
            var view = map.Perspective!;
            var advance = typeof(SettlementState).GetMethod("Advance", BindingFlags.Instance | BindingFlags.NonPublic)!;
            game.Paused = false;
            for (int tick = 0; tick < 25; tick++) advance.Invoke(siege, [game, .1d]);
            game.Paused = true;
            var orcs = siege.Enemies.Where(e => e.Kind == SiegeEnemyKind.Orc).ToArray();
            if (game.Stats.Population != 250 || orcs.Length < 3)
                throw new InvalidOperationException("Expected 250 residents and at least three wall attackers.");
            var targetWall = typeof(SettlementState).GetMethod("AttackableWall", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var contactPoint = typeof(SettlementState).GetMethod("WallAttackPoint", BindingFlags.Static | BindingFlags.NonPublic)!;
            foreach (var orc in orcs.Where(orc => orc.OrcAttacking))
            {
                if (targetWall.Invoke(siege, [orc, true]) is not WallComponent wall) continue;
                var contact = (System.Numerics.Vector2)contactPoint.Invoke(null, [wall, orc.X, orc.Y])!;
                double gap = System.Numerics.Vector2.Distance(new((float)orc.X, (float)orc.Y), contact);
                if (gap < OrcCombatAnimation.WallStandOff - .00001)
                    throw new InvalidOperationException($"Active orc attack still clips the wall: {gap}");
            }
            var target = orcs[orcs.Length / 2];
            float outside = Mathf.RadToDeg(MathF.Atan2((float)(target.X - siege.Bounds.X - siege.Bounds.Width / 2d),
                (float)(target.Y - siege.Bounds.Y - siege.Bounds.Height / 2d)));
            view.Rotate(outside + 45 - view.Azimuth);
            host.Session.PlaceCursor(new((int)target.X, (int)target.Y));
            host.Session.SetZoom(1);
            host.Map.RefreshCells();
            await SettlementSmoke.Frames(host);
            host.Session.CenterOn(host.Session.Cursor);
            host.Session.ClosePrompt();
            await SettlementSmoke.Frames(host);
            int geometry = view.GeometryBuilds;
            foreach (int frame in new[] { 33, 56, 71, 77, 79, 84, 91 })
            {
                foreach (var orc in orcs)
                {
                    Set(orc, nameof(SiegeEnemy.OrcAttacking), true);
                    Set(orc, nameof(SiegeEnemy.OrcAttackSeconds), frame / 30d);
                    typeof(SiegeEnemy).GetField("PreviousOrcAttackSeconds", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(orc, frame / 30d);
                }
                host.Session.CombatPresentation.Reset();
                host.Session.CombatPresentation.Advance(game, 0, running: false);
                view.Sync(host.Session, host.Map.Size);
                await SettlementSmoke.Frames(host);
                if (view.OrcsDrawn == 0 || view.OrcPartsDrawn < 251)
                    throw new InvalidOperationException("Main siege rendering did not use the approved orc rig.");
                if (frame == 79)
                {
                    var point = view.ScreenPoint(new((float)target.X, TerrainSprite.Elevation(game.Map,
                        (int)target.X, (int)target.Y) + .8f, (float)target.Y));
                    host.Session.SelectEnemy(target.Id);
                    view.Sync(host.Session, host.Map.Size);
                    await SettlementSmoke.Frames(host);
                    if (!new Rect2(Vector2.Zero, host.Map.Size).HasPoint(point))
                        throw new InvalidOperationException($"Orc is outside the actual viewport: {point} " +
                            $"camera={host.Session.CameraX},{host.Session.CameraY} " +
                            $"view={host.Session.ViewWidth},{host.Session.ViewHeight} target={target.X},{target.Y} size={host.Map.Size}");
                    if (!view.PickSiegeActor(point, out bool friendly, out int id) || friendly || id != target.Id)
                        throw new InvalidOperationException("Main-game approved orc picking failed.");
                    string? path = OS.GetEnvironment("TERMCITY_ORC_CAPTURE");
                    if (!string.IsNullOrEmpty(path))
                    {
                        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                        var result = GetViewport().GetTexture().GetImage().SavePng(path);
                        if (result != Error.Ok) throw new IOException($"Capture failed: {result}");
                    }
                    if (!float.IsFinite(point.X + point.Y)) throw new InvalidOperationException("Invalid actor projection.");
                }
            }
            foreach (float rotation in new[] { 90f, -180f, 90f })
            {
                view.Rotate(rotation);
                await SettlementSmoke.Frames(host);
            }
            if (view.GeometryBuilds != geometry) throw new InvalidOperationException("Actor selection/animation rebuilt terrain.");
            var liveEnemies = (List<SiegeEnemy>)typeof(SettlementState)
                .GetField("EnemyList", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(siege)!;
            var deaths = (List<SiegeDeath>)typeof(SettlementState)
                .GetField("_deaths", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(siege)!;
            var savedEnemies = liveEnemies.ToArray();
            liveEnemies.Clear();
            foreach (var cause in new[] { SiegeDeathCause.Arrow, SiegeDeathCause.Blade, SiegeDeathCause.Crushing })
            {
                deaths.Clear();
                var death = new SiegeDeath(new((float)target.X, (float)target.Y), 0, false, default,
                    SiegeEnemyKind.Orc, (float)target.BodyScale, cause, System.Numerics.Vector2.UnitY)
                    { Tier = target.Tier, AppearanceSeed = target.AppearanceSeed };
                Set(death, nameof(SiegeDeath.Seconds), 3.7d);
                deaths.Add(death);
                host.Session.CombatPresentation.Reset();
                host.Session.CombatPresentation.Advance(game, 0, running: false);
                view.Sync(host.Session, host.Map.Size);
                await SettlementSmoke.Frames(host);
                if (view.DeathsDrawn != 1 || view.OrcPartsDrawn != 251)
                    throw new InvalidOperationException("Original cause-specific orc death did not render.");
            }
            deaths.Clear(); liveEnemies.AddRange(savedEnemies);
            host.Session.CombatPresentation.Reset();
            host.Session.CombatPresentation.Advance(game, 0, running: false);
            view.Sync(host.Session, host.Map.Size);
            await SettlementSmoke.Frames(host);
            await SiegeAnimationSmoke.VerifyActiveSwarmBudget(host);
            GD.Print($"TERMCITY_ORC_COMBAT_OK population={game.Stats.Population} orcs={orcs.Length} parts={view.OrcPartsDrawn} " +
                "frames=33,56,71,77,79,84,91 rotation=true deaths=3 city-rebuilds=0");
            string? fixture = OS.GetEnvironment("TERMCITY_ORC_PLAY_SAVE");
            if (!string.IsNullOrEmpty(fixture))
            {
                game.Paused = false;
                SaveGameStore.Save(game, fixture, new(host.Session.CameraX, host.Session.CameraY, host.Session.ZoomLevel)
                    { CameraEdgePadding = true });
                game.Paused = true;
            }
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError($"TERMCITY_ORC_COMBAT_FAILED {error}");
            GetTree().Quit(1);
        }
    }

}
