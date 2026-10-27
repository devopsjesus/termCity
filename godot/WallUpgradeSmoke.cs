using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using static TermCity.GodotApp.SettlementSmoke;

namespace TermCity.GodotApp;

internal static class WallUpgradeSmoke
{
    internal static async Task Run(Main host, string? capturePath)
    {
        var session = host.Session;
        var game = session.Game;
        Require(session.DebugClearGoblins().Success && session.DebugRepairBuildings().Success,
            "Upgrade artwork requires an intact, quiet enclosure.");
        var siege = game.Settlement!;
        host.Map.SetPerspective(true);
        var view = host.Map.Perspective!;
        typeof(CityGame).GetProperty(nameof(CityGame.HighestPopulation))!.SetValue(game, 1_000);
        Require(session.UpgradeWalls().Success, session.Message);
        Require(siege.Material == WallMaterial.Wood && siege.UpgradeTargetMaterial == WallMaterial.Stone,
            "Buying stone must start construction, not instantly replace the wooden walls.");
        var first = siege.WallSections.First();
        session.PlaceCursor(first.Cell);
        session.SetZoom(1);
        host.Map.RefreshCells();
        session.CenterOn(first.Cell);
        view.Rotate(225 - view.Azimuth);
        game.Paused = false;
        game.Update(0.1);
        game.Paused = true;
        await Frames(host);
        int geometry = view.GeometryBuilds, wallBuilds = view.SiegeGeometryBuilds;
        void AdvanceWork(double seconds) =>
            typeof(SettlementState).GetMethod("AdvanceUpgrades", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.Invoke(siege, [game, seconds]);
        game.Paused = false;
        AdvanceWork(0.95);
        game.Paused = true;
        await Frames(host);
        Require(siege.ProgressFor(first) > 0 && siege.ProgressFor(first) < 1 &&
            WallUpgradeArtwork.Blocks(siege, first).Any() && view.UpgradeCrewDrawn &&
            view.SiegeGeometryBuilds > wallBuilds && view.GeometryBuilds == geometry,
            $"Stone courses and masonry workers must appear over wood without rebuilding the city: progress={siege.ProgressFor(first)}, " +
            $"blocks={WallUpgradeArtwork.Blocks(siege, first).Count()}, crew={view.UpgradeCrewDrawn}, " +
            $"wall-builds={wallBuilds}->{view.SiegeGeometryBuilds}, city-builds={geometry}->{view.GeometryBuilds}.");
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".stone-upgrade-a.png"));
        double frozen = siege.OverallUpgradeProgress;
        game.Update(1);
        Require(siege.OverallUpgradeProgress == frozen, "Pausing must freeze masonry construction.");
        game.Paused = false;
        AdvanceWork(1);
        game.Paused = true;
        await Frames(host);
        Require(siege.ProgressFor(first) > 0.5 && siege.Material == WallMaterial.Wood,
            "Higher masonry courses must be placed before a section changes material.");
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".stone-upgrade-b.png"));
        host.Map.SetPerspective(false);
        await Frames(host);
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".stone-upgrade-overhead.png"));
        host.Map.SetPerspective(true);
        game.Paused = false;
        AdvanceWork(siege.WallSections.Count() * SettlementState.UpgradeSecondsPerSection);
        game.Paused = true;
        await Frames(host);
        Require(siege.Material == WallMaterial.Stone && siege.UpgradeTargetMaterial is null &&
            siege.Walls.All(w => w.MaxHealth == 450 * w.SectionLength),
            "Completed shared wall sections must have stone strength exactly once.");
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".stone-upgrade-finished.png"));
        GD.Print("TERMCITY_WALL_UPGRADE_OK purchase=gradual stone-courses=visible workers=masonry pause=frozen shared-health=once city-rebuilds=0");
    }

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
