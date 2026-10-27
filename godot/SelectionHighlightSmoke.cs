using Godot;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using static TermCity.GodotApp.SettlementSmoke;

namespace TermCity.GodotApp;

internal static class SelectionHighlightSmoke
{
    internal static async Task Run(Main host, string? capturePath)
    {
        var session = host.Session;
        var game = session.Game;
        var siege = game.Settlement!;
        game.Paused = true;
        host.Map.SetPerspective(true);
        var view = host.Map.Perspective!;
        var building = game.Map.BuildingFootprints.Values.First(area =>
            game.Map.BuildingAt(area.X, area.Y)!.Name == "Motte and Bailey");
        var subjects = new[]
        {
            (Name: "building", Cell: new Pos(building.X, building.Y), Anchor: new Pos(building.X, building.Y)),
            (Name: "tower", Cell: siege.Walls.First(w => w.IsTower).Cell, Anchor: siege.Walls.First(w => w.IsTower).Cell),
            (Name: "wall", Cell: siege.WallSections.First(w => w.SectionLength == 3).SectionBounds.Cells().Last(),
                Anchor: siege.WallSections.First(w => w.SectionLength == 3).Cell),
        };
        foreach (var subject in subjects)
        {
            session.ClearSelection();
            session.PlaceCursor(subject.Cell);
            session.SetZoom(1);
            host.Map.RefreshCells();
            session.CenterOn(subject.Cell);
            session.ClearSelection();
            await Frames(host);
            await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, $".{subject.Name}-unselected.png"));
            int cityBuilds = view.GeometryBuilds, wallBuilds = view.SiegeGeometryBuilds;
            session.SelectCell(subject.Cell);
            view.Sync(session, host.Map.Size);
            await Frames(host);
            Require(view.HighlightedStructure == subject.Anchor && view.HighlightedBoxCount > 0 &&
                view.SelectionArea is not null && float.IsFinite(view.SelectionOutlineHeight),
                "Selecting a structure must tint its actual model while retaining the yellow selection outline.");
            Require(view.GeometryBuilds == cityBuilds && view.SiegeGeometryBuilds == wallBuilds,
                "Selecting a structure must not rebuild the city or fortifications.");
            await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, $".{subject.Name}-selected.png"));
            int highlights = view.SelectionHighlightBuilds;
            view.Sync(session, host.Map.Size);
            Require(view.SelectionHighlightBuilds == highlights, "Unchanged selections must reuse their highlight meshes.");
            session.ClearSelection();
            view.Sync(session, host.Map.Size);
            Require(view.HighlightedStructure is null && view.HighlightedBoxCount == 0 &&
                view.HighlightedRoofTriangleCount == 0, "Clearing selection must remove every structure tint.");
        }
        var tower = siege.Walls.First(w => w.IsTower);
        session.SelectCell(tower.Cell);
        foreach (int zoom in new[] { -1, 0, 1 })
        {
            session.SetZoom(zoom);
            host.Map.RefreshCells();
            view.Rotate(90);
            session.CenterOn(tower.Cell);
            await Frames(host);
            Require(view.HighlightedStructure == tower.Cell && view.HighlightedBoxCount > 0,
                "Structure highlights must remain attached at different zooms and camera rotations.");
        }
        session.PreviewWallDefense(WallDefenseKind.Rocks);
        view.Sync(session, host.Map.Size);
        Require(view.HighlightedStructure is null, "Placement ghosts must not inherit the previous selection tint.");
        session.CancelPreview();
        session.ClosePrompt();
        host.Map.SetPerspective(false);
        session.SelectCell(new(building.X, building.Y));
        session.CenterOn(session.Cursor);
        await Frames(host);
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".building-selected-overhead.png"));
        host.Map.SetPerspective(true);
        session.ClearSelection();
        await Frames(host);
        GD.Print("TERMCITY_SELECTION_HIGHLIGHT_OK targets=building,tower,shared-wall outline=retained clear=removed rotation=3 zoom=3 city-rebuilds=0");
    }

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
