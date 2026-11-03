using System.Collections;
using System.Reflection;
using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using static TermCity.GodotApp.SettlementSmoke;

namespace TermCity.GodotApp;

internal static class WorldScaleSmoke
{
    public static async Task Run(Main host, string? capture)
    {
        var budgetFailures = new List<string>();
        async Task Measure(Func<Task> check)
        {
            try { await check(); }
            catch (PerformanceBudgetExceededException error)
            {
                budgetFailures.Add(error.Message);
                GD.Print("TERMCITY_PERFORMANCE_BUDGET_EXCEEDED " + error.Message);
            }
        }
        var session = host.Session;
        var game = CityGame.New(session.Game.Config with
        {
            MapWidth = 640, MapHeight = 384, StartingMoney = 1_000_000, Rules = CityRules.Full,
        });
        var map = game.Map;
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
            {
                map.ClearCell(x, y);
                map.SetTerrain(x, y, map.Content.Terrains.Base);
            }
        game.Touch();
        var founded = game.FoundSettlement(new(256, 128, 128, 128));
        Require(founded.Success, founded.Message);
        var siege = game.Settlement!;
        int remaining = 1_000 - game.Stats.Population;
        var cottage = map.Content.Buildings.Get("Cottage");
        foreach (var plot in game.VacantPlots(ZoneType.Residential))
        {
            if (remaining <= 0) break;
            int people = Math.Min(remaining, cottage.Capacity);
            map.SetBuildingFootprint(cottage, plot);
            map.SetHousehold(plot.X, plot.Y,
                new Household((byte)Math.Min(4, people), (byte)Math.Max(0, people - 4), 0));
            remaining -= people;
        }
        Require(remaining == 0, "Realistic new plots must house 1,000 actual residents.");
        game.Touch();
        Replace(session, game);
        session.DismissGuide();
        game.Paused = false;
        game.Update(.1);
        Require(siege.TrollApproachExpected, "Trolls require a warning before their first wave.");
        Require(game.DebugRemoveGoblins().Success, "Clear the existing wave for the troll fixture.");
        Set(siege, nameof(siege.WaveNumber), 99);
        Set(siege, nameof(siege.NextWaveSeconds), 0d);
        for (int tick = 0; tick < 3 && siege.Enemies.Count == 0; tick++) game.Update(.1);
        game.Paused = true;
        Require(siege.Enemies.Count == 120 && siege.Enemies.Count(e => e.Definition.IsTroll) == 3,
            $"The maximum mixed swarm must include all three troll species: count={siege.Enemies.Count}, " +
            $"trolls={siege.Enemies.Count(e => e.Definition.IsTroll)}, wave={siege.WaveNumber}.");
        host.Map.SetPerspective(true);
        for (int row = 0; row < 6; row++)
        {
            var wallRun = game.BuildWallRun(new(32.5f, 40.5f + row * 12), new(220.5f, 45.5f + row * 12));
            Require(wallRun.Success, "The populated performance fixture needs real extended wall runs: " + wallRun.Message);
        }
        Require(game.BuildWallRun(new(32.5f, 40.5f), new(32.5f, 100.5f)).Success,
            "The performance fixture must include branching wall junctions.");
        GD.Print($"TERMCITY_WALL_LOAD_FIXTURE population={game.Stats.Population} sections={siege.Walls.Count} branches=6");
        session.ClosePrompt();
        session.CenterOn(new(320, 192));
        host.Map.Invalidate(true);
        await Frames(host);
        await Measure(() => SiegeAnimationSmoke.VerifyActiveSwarmBudget(host));
        await Measure(() => PerspectivePerformanceSmoke.Run(host));
        await RenderingQualitySmoke.Run(host, capture);
        var lineup = new[]
        {
            SiegeEnemyKind.Goblin, SiegeEnemyKind.Orc, SiegeEnemyKind.TreeTroll,
            SiegeEnemyKind.StoneTroll, SiegeEnemyKind.MudTroll,
        }.Select(kind => siege.Enemies.First(e => e.Kind == kind)).ToArray();
        var enemies = (IList)typeof(SettlementState).GetField("EnemyList",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(siege)!;
        enemies.Clear();
        int centerX = siege.Bounds.X + siege.Bounds.Width / 2;
        int wallY = siege.Bounds.Bottom;
        for (int i = 0; i < lineup.Length; i++)
        {
            var enemy = lineup[i];
            Set(enemy, nameof(enemy.X), centerX - 8.5 + i * 4);
            Set(enemy, nameof(enemy.Y), wallY + 4.5);
            typeof(SiegeEnemy).GetField("MotionInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(enemy, false);
            enemies.Add(enemy);
        }
        foreach (var (name, x) in new[] { ("Cottage", centerX - 8), ("Fire Watch", centerX + 12) })
        {
            var type = map.Content.Buildings.Get(name);
            var area = type.FootprintAt(x, wallY - 12);
            Require(area.Cells().All(p => map.BuildingAt(p.X, p.Y) is null), "Comparison plots must be genuinely free.");
            map.SetBuildingFootprint(type, area);
        }
        game.Touch();
        Set(siege, nameof(siege.Revision), siege.Revision + 1);
        session.SetZoom(0);
        session.CenterOn(new(centerX, wallY - 3));
        host.Map.Invalidate(true);
        var view = host.Map.Perspective!;
        await Frames(host);
        var home = view.BuildingVolumes(new(centerX - 8, wallY - 12));
        Require(home.Count > 0 && home.Max(b => b.End.Y) >= WorldScale.Feet(12),
            "Emitted cottage geometry must be taller than an adult, not a miniature.");
        int builds = view.GeometryBuilds;
        var picked = new HashSet<int>();
        int mostVisible = 0;
        float captureAzimuth = view.Azimuth;
        foreach (float rotation in new[] { 0f, 90f, 90f, 90f })
        {
            view.Rotate(rotation);
            await Frames(host);
            int visible = 0;
            foreach (var enemy in lineup)
            {
                var point = view.ScreenPoint(new((float)enemy.X,
                    (float)(enemy.BodyScale * 1.18), (float)enemy.Y));
                if (new Rect2(Vector2.Zero, host.Map.Size).HasPoint(point) &&
                    host.Map.TryPickSiegeActor(point, out bool friendly, out int id) && !friendly && id == enemy.Id)
                {
                    picked.Add(id);
                    visible++;
                    host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
                        { Position = point, ButtonIndex = MouseButton.Left, Pressed = true });
                    host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
                        { Position = point, ButtonIndex = MouseButton.Left, Pressed = false });
                    await Frames(host);
                    var status = Descendants(host).OfType<Label>().Single(label => label.Name == "Status");
                    Require(session.SelectedEnemy?.Id == id && status.Text.Contains(enemy.Definition.Name),
                        $"Clicking {enemy.Definition.Name} must show its name in the bottom-left status bar.");
                }
            }
            if (visible > mostVisible) { mostVisible = visible; captureAzimuth = view.Azimuth; }
        }
        Require(picked.Count == lineup.Length, "Every species must be frontmost-pickable from a visible orientation.");
        Require(mostVisible == lineup.Length, "The comparison capture must show all five species unobscured.");
        Require(view.GeometryBuilds == builds, "Actor picking and rotation must reuse buffered city geometry.");
        session.ClearSelection();
        view.Rotate(captureAzimuth - view.Azimuth);
        await Frames(host);
        await Capture(host, capture);
        foreach (int zoom in new[] { -1, 0, 1 })
        {
            session.SetZoom(zoom);
            await Frames(host);
            Require(view.EnemiesDrawn == 5, "All five comparison actors must survive zoom changes.");
        }
        host.Map.SetPerspective(false);
        await Frames(host);
        await Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".overhead.png"));
        GD.Print("TERMCITY_WORLD_SCALE_OK population=1000 species=5 trolls=3 picking=all selection-status=all rotations=4 zooms=3");
        if (budgetFailures.Count > 0) throw new PerformanceBudgetExceededException(string.Join(" ", budgetFailures));
    }

    private static void Set(object target, string property, object value) =>
        target.GetType().GetProperty(property)!.SetValue(target, value);

    private static void Replace(GameSession session, CityGame game) =>
        typeof(GameSession).GetMethod("ReplaceGame", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [game, true]);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
