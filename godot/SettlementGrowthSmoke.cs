using System.Diagnostics;
using System.Reflection;
using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
using static TermCity.GodotApp.SettlementSmoke;
using NVector2 = System.Numerics.Vector2;

namespace TermCity.GodotApp;

internal static class SettlementGrowthSmoke
{
    internal static async Task Run(Main host, string? capture)
    {
        var session = host.Session;
        var original = session.Game;
        var window = host.GetWindow();
        var windowSize = window.Size;
        var game = CityGame.New(new GameConfig
        {
            MapWidth = 160, MapHeight = 128, Seed = 42, StartingMoney = 2_000_000, Rules = CityRules.Full,
            FireIgnitionPerBuildingWeek = 0, RaidChancePerWeek = 0, FloodChancePerWeek = 0, OutbreakChancePerWeek = 0,
        });
        for (int y = 0; y < game.Map.Height; y++)
            for (int x = 0; x < game.Map.Width; x++)
            {
                game.Map.ClearCell(x, y);
                game.Map.SetTerrain(x, y, game.Map.Content.Terrains.Base);
            }
        game.Touch();
        Require(game.FoundSettlement(new(40, 40, 40, 40)).Success, "Growth validation needs a founding bailey.");
        void Build(NVector2 a, NVector2 b)
        {
            var result = game.BuildWallRun(a, b);
            Require(result.Success, result.Message);
        }
        Build(new(79.5f, 40.5f), new(110.5f, 40.5f));
        Build(new(110.5f, 40.5f), new(110.5f, 79.5f));
        Build(new(110.5f, 79.5f), new(79.5f, 79.5f));
        var cell = new Pos(92, 52);
        var state = game.Settlement!;
        game.Paused = true;
        _ = game.Indicators;
        _ = state.EligibleArea(game);
        Replace(session, game);
        try
        {
            session.ClosePrompt();
            session.ClearSelection();
            host.Map.SetPerspective(true);
            session.SetZoom(-1);
            session.SelectCell(cell);
            session.CenterOn(cell);
            await Frames(host);
            var watch = Stopwatch.StartNew();
            var diagnostic = GrowthDiagnostics.ForSettlementCell(game, cell);
            watch.Stop();
            double cold = watch.Elapsed.TotalMilliseconds;
            Require(diagnostic.Status == GrowthStatus.Ready, "A gated extension must show a usable home site.");
            watch.Restart();
            for (int n = 0; n < 100; n++) _ = CellInspector.Summary(game, cell);
            watch.Stop();
            double cached = watch.Elapsed.TotalMilliseconds / 100;
            Require(cached < 50, $"Cached growth inspection took {cached:F1}ms; budget is 50ms.");
            foreach (int width in new[] { 960, 640 })
            {
                if (DisplayServer.GetName() != "headless") window.Size = new(width, Math.Max(780, windowSize.Y));
                await Frames(host);
                var status = Descendants(host).OfType<Label>().Single(label => label.Name == "Status");
                Require(status.TooltipText.Contains("Eligible home site") && status.TooltipText.Contains("No zoning"),
                    "Empty unzoned extension land must expose its full growth explanation in the existing status tooltip.");
                await Capture(host, capture is null ? null : Path.ChangeExtension(capture, $".growth-inspection-{width}.png"));
                session.ShowReport(4);
                await Frames(host);
                Require(ReportContent.Build(game)[4].Groups.Any(group =>
                    group.Rows.Any(row => row.Any(text => text.Text.Contains("Eligible sites inside")))),
                    "Growth reports must use access-aware vacancies and explain site selection.");
                await Capture(host, capture is null ? null : Path.ChangeExtension(capture, $".growth-report-{width}.png"));
                session.ClosePrompt();
            }
            foreach (var p in state.Bounds.Cells())
                if (game.Map.BuildingAt(p.X, p.Y) is null) game.Map.SetPathWear(p.X, p.Y, 3);
            game.Touch();
            int population = game.Stats.Population;
            for (int n = 0; n < 4 && game.Stats.Population == population; n++) game.AdvanceWeek();
            Require(game.Stats.Population > population && game.Map.BuildingFootprints.Values.Any(area =>
                area.X > state.Bounds.Right && game.Map.HouseholdAt(area.X, area.Y).Total > 0),
                "Normal immigration must put real households into the physically eligible extension.");
            if (DisplayServer.GetName() != "headless") window.Size = windowSize;
            await Frames(host);
            var home = game.Map.BuildingFootprints.Values.First(area =>
                area.X > state.Bounds.Right && game.Map.HouseholdAt(area.X, area.Y).Total > 0);
            session.SelectCell(new(home.X, home.Y));
            session.CenterOn(new(home.X, home.Y));
            host.Map.RefreshCells();
            await Frames(host);
            var view = host.Map.Perspective!;
            var target = new Vector2(home.X + home.Width / 2f, home.Y + home.Height / 2f);
            view.SetCameraOffset(session, target - new Vector2(session.CameraX + session.ViewWidth * session.Stride / 2f,
                session.CameraY + session.ViewHeight * session.Stride / 2f));
            view.Sync(session, host.Map.Size);
            await Frames(host);
            Require(view.BuildingVolumes(new(home.X, home.Y)).Count > 0,
                "The populated extension capture must contain the actual new home geometry.");
            await Capture(host, capture is null ? null : Path.ChangeExtension(capture, ".growth-extension-populated.png"));
            GD.Print($"TERMCITY_EXTENSION_GROWTH_OK gates=boundary-transit households=organic zoning=none " +
                $"inspection={cold:F1}ms cached={cached:F3}ms widths=960,640");
        }
        finally
        {
            if (DisplayServer.GetName() != "headless") window.Size = windowSize;
            Replace(session, original);
            session.ClosePrompt();
            host.Map.Invalidate(true);
            await Frames(host);
        }
    }

    private static void Replace(GameSession session, CityGame game) =>
        typeof(GameSession).GetMethod("ReplaceGame", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, [game, true]);
}
