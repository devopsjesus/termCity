using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;
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
            session.SetZoom(subject.Name == "building" ? 0 : 1);
            host.Map.RefreshCells();
            session.CenterOn(subject.Name == "building"
                ? new Pos(building.X + building.Width / 2, building.Y + building.Height / 2)
                : subject.Cell);
            session.ClearSelection();
            await Frames(host);
            if (subject.Name == "building")
            {
                var bark = PalisadeArtwork.MaterialTexture(PalisadeArtwork.BarkColor);
                Require(view.PixelMaterials.Any(material =>
                {
                    using var image = material.AlbedoTexture!.GetImage();
                    return image.GetWidth() == PalisadeArtwork.TextureSize &&
                        image.GetHeight() == PalisadeArtwork.TextureSize &&
                        image.GetData().AsSpan(0, bark.Length).SequenceEqual(bark);
                }), "Motte and Bailey logs must render the dark bark palette, not a white grayscale texture.");
            }
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
        await VerifyPalisade(host, building, capturePath);
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
        await VerifyMountedFrames(host, tower, capturePath);
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

    private static async Task VerifyPalisade(Main host, CellRect building, string? capturePath)
    {
        var session = host.Session;
        var game = session.Game;
        var view = host.Map.Perspective!;
        var anchor = new Pos(building.X, building.Y);
        var type = game.Map.BuildingAt(anchor.X, anchor.Y)!;
        int rotation = game.Map.BuildingRotations.GetValueOrDefault(game.Map.Index(anchor.X, anchor.Y));
        float width = Math.Max(0.55f, (rotation % 2 == 0 ? building.Width : building.Height) / type.ModelScale * 0.84f);
        float depth = Math.Max(0.55f, (rotation % 2 == 0 ? building.Height : building.Width) / type.ModelScale * 0.78f);
        var parts = PalisadeArtwork.Parts(width, depth, CellHash.Pick(anchor.X, anchor.Y, 1_000_003));
        float ground = building.Cells().Max(p => TerrainSprite.Elevation(game.Map, p.X, p.Y));
        var center = new Vector3(building.X + building.Width / 2f, ground, building.Y + building.Height / 2f);
        var picks = parts.Where(p => p.Color is PalisadeArtwork.CutColor or PalisadeArtwork.CutShadeColor)
            .Select(p => center + new Vector3(p.Center.X, p.Center.Y + p.Size.Y / 2, p.Center.Z)
                .Rotated(Vector3.Up, -rotation * Mathf.Pi / 2) * type.ModelScale).ToArray();
        float azimuth = view.Azimuth;
        foreach (int zoom in new[] { -1, 0, 1 })
            for (int turn = 0; turn < 4; turn++)
            {
                session.SetZoom(zoom);
                host.Map.RefreshCells();
                view.Rotate(90);
                session.CenterOn(new(building.X + building.Width / 2, building.Y + building.Height / 2));
                await Frames(host);
                Require(picks.Any(point => view.PickBuilding(view.ScreenPoint(point), out var picked) && picked == anchor),
                    "Rough-cut log tops and chipped palisades must remain clickable at every rotation and zoom.");
                int builds = view.GeometryBuilds;
                session.SelectCell(anchor);
                view.Sync(session, host.Map.Size);
                Require(view.HighlightedStructure == anchor && view.HighlightedBoxCount >= parts.Count &&
                    view.GeometryBuilds == builds, "Palisade selection must include every imperfect log without rebuilding the city.");
                session.ClearSelection();
                view.Sync(session, host.Map.Size);
                await Frames(host);
                if (zoom == 0)
                    await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, $".palisade-{turn}.png"));
            }
        view.Rotate(azimuth - view.Azimuth);
        GD.Print("TERMCITY_PALISADE_OK profiles=5 chinks=geometry picks=log-tops rotations=4 zooms=3 selection=cached");
    }

    private static async Task VerifyMountedFrames(Main host, WallComponent tower, string? capturePath)
    {
        var session = host.Session;
        var game = session.Game;
        var siege = game.Settlement!;
        var view = host.Map.Perspective!;
        var original = tower.Defense;
        double health = tower.Health;
        float azimuth = view.Azimuth;
        void Set(object target, string property, object value) =>
            target.GetType().GetProperty(property)!.SetValue(target, value);
        void Check(WallDefenseKind kind)
        {
            Require(view.SelectedObjectBounds is not null && view.SelectionVisualFootprint is not null,
                "Mounted selections must expose their rendered bounds and drawn footprint.");
            var bounds = view.SelectedObjectBounds!.Value;
            var frame = view.SelectionVisualFootprint!.Value;
            var definition = WallDefenses.Get(kind);
            Require(view.WeaponRangeVisible && view.WeaponRangeTriangles > 0 &&
                view.WeaponRangeOuter == definition.Range && view.WeaponRangeInner == definition.MinimumRange,
                $"{kind}: selection must shade its actual ground attack range and preserve minimum-range gaps.");
            Require(frame.Encloses(new Rect2(bounds.Position.X, bounds.Position.Z, bounds.Size.X, bounds.Size.Z)),
                $"{kind}: selection must enclose every mounted station, including opposite-face overhangs.");
            float support = TerrainSprite.Elevation(game.Map, tower.Cell.X, tower.Cell.Y) +
                WallDamageLayout.ActorWalkHeight(tower, game.Map) +
                (kind is WallDefenseKind.OilCauldron or WallDefenseKind.Rocks ? 0 : .05f);
            Require(Math.Abs(bounds.Position.Y - support) < .0001f &&
                view.SelectionOutlineHeight >= support && view.SelectionOutlineHeight < support + .1f,
                $"{kind}: the frame must follow the actual mounting surface, including damaged decks.");
            int boxes = kind switch
            {
                WallDefenseKind.OilCauldron or WallDefenseKind.Rocks =>
                    TowerDefenseArtwork.Parts(tower, kind, TowerDefenseArtwork.Rise(tower, game.Map)).Count,
                WallDefenseKind.Ballista => 4,
                WallDefenseKind.Trebuchet => 5,
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            Require(view.HighlightedBoxCount == boxes, "The selection tint must cover only the selected weapon model.");
            if (kind == WallDefenseKind.Ballista)
                Require(frame.Size.X > 1.15f && frame.Size.Y > 1.2f,
                    "The frame must expand beyond one cell for the ballista's crosspieces.");
        }
        foreach (var kind in Enum.GetValues<WallDefenseKind>().Where(kind => kind != WallDefenseKind.None))
        {
            Set(tower, nameof(WallComponent.Defense), kind);
            Set(siege, nameof(SettlementState.Revision), siege.Revision + 1);
            session.SelectCell(tower.Cell);
            foreach (int zoom in new[] { -1, 0, 1 })
                for (int turn = 0; turn < 4; turn++)
                {
                    session.SetZoom(zoom);
                    view.Rotate(90);
                    session.CenterOn(tower.Cell);
                    await Frames(host);
                    Check(kind);
                    int city = view.GeometryBuilds, walls = view.SiegeGeometryBuilds;
                    int guides = view.WeaponRangeMeshBuilds;
                    view.Sync(session, host.Map.Size);
                    Require(view.GeometryBuilds == city && view.SiegeGeometryBuilds == walls,
                        "Updating the selection frame must not rebuild city or fortification geometry.");
                    Require(view.WeaponRangeMeshBuilds == guides, "Unchanged range guides must reuse their ground mesh.");
                    if (zoom == 0 && turn == 0)
                        await Capture(host, capturePath is null ? null :
                            Path.ChangeExtension(capturePath, $".{kind}-footprint.png"));
                }
            Set(tower, nameof(WallComponent.Health), tower.MaxHealth * .6);
            Set(siege, nameof(SettlementState.Revision), siege.Revision + 1);
            await Frames(host);
            Check(kind);
            Set(tower, nameof(WallComponent.Health), health);
            Set(siege, nameof(SettlementState.Revision), siege.Revision + 1);
        }
        Set(tower, nameof(WallComponent.Defense), original);
        Set(siege, nameof(SettlementState.Revision), siege.Revision + 1);
        view.Rotate(azimuth - view.Azimuth);
        session.ClearSelection();
        await Frames(host);
        Require(!view.WeaponRangeVisible, "Clearing selection must remove the automatic weapon-range ground highlight.");
        GD.Print("TERMCITY_SELECTION_FOOTPRINT_OK weapons=4 rotations=4 zooms=3 damaged-decks=checked geometry=cached");
    }

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
