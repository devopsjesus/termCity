using Godot;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

internal static class SettlementSmoke
{
    public static async Task Run(Main host, string? capturePath)
    {
        var session = host.Session;
        var game = session.Game;
        var sidebar = Descendants(host).OfType<CityPanel>().Single();
        var countdownSection = Descendants(sidebar).OfType<VBoxContainer>().Single(node => node.Name == "NextWaveSection");
        var countdown = Descendants(sidebar).OfType<ProgressBar>().Single(node => node.Name == "NextWaveProgress");
        Require(!countdownSection.Visible, "The siege countdown must remain hidden in cities without a bailey.");
        var probe = CityGame.New(game.Config);
        SettlementDebugSetup.Apply(probe);
        var proposed = probe.Settlement!.Bounds;
        var firstCorner = new Pos(proposed.X, proposed.Y);
        var lastCorner = new Pos(proposed.Right, proposed.Bottom);
        host.Map.SetPerspective(true);
        session.SetZoom(-2);
        session.CenterOn(new(proposed.X + proposed.Width / 2, proposed.Y + proposed.Height / 2));
        await Frames(host);
        var previewView = host.Map.Perspective!;
        session.BeginSettlementPlacement();
        ClickWorld(host, firstCorner);
        Require(game.Settlement is null && session.SettlementToolActive,
            "The first real mouse click must anchor the enclosure without purchasing it.");
        int previewBuilds = previewView.GeometryBuilds;
        var endpoint = previewView.ScreenPoint(new(lastCorner.X + 0.5f,
            TerrainSprite.Elevation(game.Map, lastCorner.X, lastCorner.Y), lastCorner.Y + 0.5f));
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion { Position = endpoint });
        await Frames(host);
        Require(session.Preview?.Area == proposed && session.FixedPlacementError is null &&
            previewView.GeometryBuilds == previewBuilds,
            "Mouse-follow wall previews must match the enclosure without rebuilding the city.");
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".enclosure.png"));
        ClickWorld(host, lastCorner);
        Require(game.Settlement?.Bounds == proposed && session.Preview is null,
            $"The second real mouse click must purchase the complete enclosure: expected={proposed}, " +
            $"actual={game.Settlement?.Bounds}, preview={session.Preview?.Area}, cursor={session.Cursor}, message={session.Message}.");
        session.RequestUndo();
        session.SelectPrompt(0);
        game = session.Game;
        Require(game.Settlement is null, "Paused undo must remove the entire enclosure and starter buildings.");
        SettlementDebugSetup.Apply(game, siege: true);
        session.ClosePrompt();
        game.Paused = true;
        var settlement = game.Settlement!;
        Require(game.Stats.Population == 100 && settlement.Goblins.Count > 0,
            "The siege starter must have exactly 100 settlers and an active first wave.");
        var center = new Pos(settlement.Bounds.X + settlement.Bounds.Width / 2,
            settlement.Bounds.Y + settlement.Bounds.Height / 2);
        host.Map.SetPerspective(true);
        session.SetZoom(-1);
        session.CenterOn(center);
        host.Map.Invalidate(true);
        var view = host.Map.Perspective!;
        await Frames(host);
        Require(view.WallsDrawn == settlement.Walls.Count && view.GoblinsDrawn == settlement.Goblins.Count,
            "The renderer must draw every wall component and goblin.");
        Require(!session.AreaOverlaysVisible && view.ZonePlotsDrawn == 0 && host.Map.AreasDrawn == 0,
            "New sessions must hide vacant-zone and service-range overlays.");
        session.SetAreaOverlays(true);
        view.Sync(session, host.Map.Size);
        await Frames(host);
        Require(view.ZonePlotsDrawn == 0 && game.VacantPlots(ZoneType.Residential).Count > 0,
            "Organic settlement sites must remain unzoned even when AREAS is enabled.");
        var marker = view.RenderedView.Cells().First(p => p.X > settlement.Bounds.X && p.X < settlement.Bounds.Right &&
            p.Y > settlement.Bounds.Y && p.Y < settlement.Bounds.Bottom && game.Map.TerrainAt(p.X, p.Y).Buildable &&
            game.Map.BuildingAt(p.X, p.Y) is null && !game.Map.HasRoad(p.X, p.Y));
        var oldZone = game.Map.ZoneAt(marker.X, marker.Y);
        game.Map.SetZone(marker.X, marker.Y, ZoneType.Commercial);
        game.Touch();
        view.Sync(session, host.Map.Size);
        Require(view.ZonePlotsDrawn > 0, "AREAS must show explicitly designated vacant commercial plots.");
        await Frames(host);
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".areas-on.png"));
        session.SetAreaOverlays(false);
        view.Sync(session, host.Map.Size);
        await Frames(host);
        Require(view.ZonePlotsDrawn == 0 && host.Map.PerspectiveRangeBounds.Count == 0,
            "Turning AREAS off must clear cached zone markers and range outlines.");
        Require(game.Map.ZoneAt(marker.X, marker.Y) == ZoneType.Commercial,
            "Hiding area overlays must preserve the zoning designation.");
        game.Map.SetZone(marker.X, marker.Y, oldZone);
        game.Touch();
        sidebar.Refresh();
        Require(countdownSection.Visible && Math.Abs(countdown.Value - settlement.NextWaveFraction) < 0.0011,
            "The next-wave bar must show the settlement's remaining siege countdown.");
        double fullCountdown = countdown.Value;
        int geometryBuilds = view.GeometryBuilds;
        foreach (var (zoom, rotation) in new[] { (-1, 0f), (0, 90f), (1, 90f), (-1, 180f) })
        {
            session.SetZoom(zoom);
            view.Rotate(rotation);
            session.CenterOn(center);
            host.Map.Invalidate(true);
            await Frames(host);
            var gate = settlement.Walls.Single(w => w.IsGate);
            var screen = view.ScreenPoint(view.WallTop(gate) - Vector3.Up * 0.1f);
            if (new Rect2(Vector2.Zero, host.Map.Size).HasPoint(screen))
                Require(host.Map.TryPickBuilding(screen, out _),
                    "Visible wall/gate geometry must be pickable after camera rotation and zoom.");
        }

        session.SetZoom(-1);
        session.CenterOn(center);
        await Frames(host);
        int beforeSelection = view.GeometryBuilds;
        var tower = settlement.Walls.First(w => w.IsTower);
        session.SelectCell(tower.Cell);
        view.Sync(session, host.Map.Size);
        Require(view.GeometryBuilds == beforeSelection, "Wall selection must not rebuild the city.");
        Require(view.SelectionArea == tower.SectionBounds && view.SelectionOutlineHeight > view.WallTop(tower).Y + 0.32f,
            "The tower selection square must sit above the usable top and crenellations, not on the ground.");
        session.PreviewWallDefense(WallDefenseKind.OilCauldron);
        session.MoveFixedPlacement(tower.Cell);
        Require(session.WallDefenseToolActive && session.FixedPlacementError is null,
            "The 100-soul starter must be able to preview its first unlocked wall defense.");
        view.Sync(session, host.Map.Size);
        Require(view.GeometryBuilds == beforeSelection, "A mounted-defense ghost must not rebuild the city.");
        Require(view.SelectionOutlineHeight > view.WallTop(tower).Y + 0.32f,
            "A valid wall-defense placement square must remain visible on top of the tower.");
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".tower-top-selection.png"));
        Require(session.ConfirmPreview().Success, session.Message);
        session.ClosePrompt();
        session.ClearSelection();
        view.Sync(session, host.Map.Size);
        int beforeCombat = view.GeometryBuilds;
        int mapVersion = game.MapVersion;
        game.Paused = false;
        game.Update(0.25);
        game.Paused = true;
        host.Map.Invalidate(false);
        await Frames(host);
        sidebar.Refresh();
        Require(countdown.Value < fullCountdown &&
            Math.Abs(countdown.Value - settlement.NextWaveFraction) < 0.0011,
            "The next-wave progress bar must visibly decrement as combat time advances.");
        double pausedCountdown = countdown.Value;
        session.Update(0.5);
        sidebar.Refresh();
        Require(countdown.Value == pausedCountdown, "Pausing must freeze the next-wave bar.");
        Require(game.MapVersion == mapVersion && view.GeometryBuilds == beforeCombat,
            "Combat-only updates must not rebuild terrain and city geometry.");
        Require(view.GoblinsDrawn > 0, "The active siege must remain visible.");
        await SiegeAnimationSmoke.VerifyWalking(host, capturePath);
        await Capture(host, capturePath);
        bool headless = DisplayServer.GetName() == "headless";
        var window = host.GetWindow();
        var size = headless ? window.ContentScaleSize : window.Size;
        foreach (int width in new[] { 960, 640 })
        {
            if (headless) window.ContentScaleSize = new(width, Main.MinimumWindowHeight);
            else window.Size = new(width, Main.MinimumWindowHeight);
            session.ShowGarrisonMenu();
            await Frames(host);
            Require(countdown.GetGlobalRect().End.Y <= sidebar.GetGlobalRect().End.Y + 1 &&
                countdown.GetGlobalRect().End.X <= sidebar.GetGlobalRect().End.X + 1,
                "The next-wave countdown must fit the sidebar in normal and narrow windows.");
            var dialog = Descendants(host).OfType<TerminalFrame>().Single(frame =>
                frame.Name == "CityDialog" && frame.IsVisibleInTree());
            Require(dialog.ClipContents && dialog.GetGlobalRect().End.X <= host.Size.X + 1 &&
                dialog.GetGlobalRect().End.Y <= host.Size.Y + 1,
                "Garrison must use a clipped terminal dialog fitting normal and narrow windows.");
            Require(session.Prompt!.Choices.Any(c => c.Label.Contains("Trebuchet", StringComparison.OrdinalIgnoreCase)),
                "Garrison must list the later siege weapons as well as starter defenses.");
            Require(Descendants(dialog).Any(node => node.Name == "ConstructionCatalog") &&
                Descendants(dialog).Any(node => node.Name == "ConstructionTypesBox") &&
                Descendants(dialog).Any(node => node.Name == "ConstructionStats") &&
                Descendants(dialog).Any(node => node.Name == "ConstructionAdd") &&
                Descendants(dialog).Any(node => node.Name == "ConstructionCancel"),
                "Garrison must reuse the same construction catalog, stats, previews and actions as other submenus.");
            int oil = session.Prompt.Choices.ToList().FindIndex(choice => choice.Label == WallDefenses.Get(WallDefenseKind.OilCauldron).Name);
            session.SelectPrompt(oil);
            await Frames(host);
            Require(session.Preview is null && session.Prompt is not null,
                "Selecting a Garrison catalog row must show its model and stats, not immediately enter placement.");
            var add = Descendants(dialog).OfType<Button>().Single(button => button.Name == "ConstructionAdd");
            Require(!add.Disabled && Descendants(dialog).OfType<RenderedModelPreview>().Any(preview =>
                preview.Name == "ConstructionModelPreview" && preview.PreviewKind == "WallDefense"),
                "Unlocked wall weapons must have canonical model previews and an enabled Add action.");
            GodotSmoke.VerifyDialogButtonBorders(dialog);
            Require(dialog.GlobalPosition.DistanceTo(host.Map.GetParent<Control>().GlobalPosition) < 1 &&
                !dialog.JoinAdjacent && Descendants(dialog).OfType<TerminalFrame>().All(frame => !frame.JoinAdjacent),
                "Garrison must anchor at the map and keep closed terminal borders, exactly like other construction catalogs.");
            var cancel = Descendants(dialog).OfType<Button>().Single(button => button.Name == "ConstructionCancel");
            Require(Math.Abs(add.Size.X - cancel.Size.X) < 1 &&
                !Descendants(Descendants(dialog).OfType<ScrollContainer>().Single(scroll =>
                    scroll.Name == "ConstructionCatalog")).Contains(add),
                "Garrison Add/Cancel must be equal-width fixed actions outside the scrolling catalog.");
            await GodotSmoke.VerifyModelPreviews(host, dialog);
            await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, $".garrison-{width}.png"));
            int gold = game.Money;
            add.EmitSignal(Button.SignalName.Pressed);
            Require(session.WallDefenseToolActive && session.Prompt is null && game.Money == gold,
                "Garrison Add must enter wall placement without purchasing a weapon before a wall is clicked.");
            session.CancelPreview();
            await Frames(host);
            int trebuchet = session.Prompt!.Choices.ToList().FindIndex(choice => choice.Label == "Trebuchet");
            session.SelectPrompt(trebuchet);
            await Frames(host);
            dialog = Descendants(host).OfType<TerminalFrame>().Single(frame =>
                frame.Name == "CityDialog" && frame.IsVisibleInTree());
            add = Descendants(dialog).OfType<Button>().Single(button => button.Name == "ConstructionAdd");
            Require(add.Disabled && add.TooltipText.Contains("1,000", StringComparison.Ordinal) && game.Money == gold,
                "Locked siege engines must use the same disabled Add gate and population tooltip as other catalogs.");
            session.ClosePrompt();
        }
        if (headless) window.ContentScaleSize = size;
        else window.Size = size;
        await Frames(host);
        var fuel = Descendants(host).OfType<Button>().Single(button => button.Name == "Fuel");
        fuel.EmitSignal(Button.SignalName.Pressed);
        await Frames(host);
        int burners = session.Prompt!.Choices.ToList().FindIndex(choice => choice.Label == "Charcoal Burners");
        session.SelectPrompt(burners);
        await Frames(host);
        var fuelDialog = Descendants(host).OfType<TerminalFrame>().Single(frame =>
            frame.Name == "CityDialog" && frame.IsVisibleInTree());
        Require(Descendants(fuelDialog).OfType<Label>().Any(label => label.Text.Contains("radius 6 cells", StringComparison.Ordinal)),
            "Fuel catalog pollution guides must show the same compact radius as the simulation.");
        await GodotSmoke.VerifyModelPreviews(host, fuelDialog);
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".fuel.png"));
        session.ClosePrompt();
        for (int day = 0; day < 14; day++) game.AdvanceDay();
        session.ClosePrompt();
        game.Paused = true;
        session.CenterOn(center);
        host.Map.Invalidate(true);
        await Frames(host);
        var paths = FootpathLayout.Strokes(game.Map);
        Require(paths.Count > 0 && view.FootpathTrianglesDrawn > 0,
            "Natural building-to-building paths must become visible inside the bailey.");
        Require(paths.All(path => !path.StartsAtRoad && !path.EndsAtRoad && path.Points.All(point =>
            point.X > settlement.Bounds.X && point.X < settlement.Bounds.Right &&
            point.Y > settlement.Bounds.Y && point.Y < settlement.Bounds.Bottom)),
            "Rendered bailey footpaths must stay within the walls and never target exterior roads.");
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".paths.png"));
        host.Map.SetPerspective(false);
        host.Map.Invalidate(true);
        await Frames(host);
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".overhead.png"));
        host.Map.SetPerspective(true);
        session.ClearSelection();
        session.CenterOn(center);
        GD.Print($"TERMCITY_SETTLEMENT_RENDER_OK walls={view.WallsDrawn} goblins={view.GoblinsDrawn} " +
            $"initial-builds={geometryBuilds} combat-city-rebuilds=0 nodes={view.SceneObjectCount}");
        await DebugSiegeSmoke.Run(host, capturePath);
        await SiegeAnimationSmoke.VerifyWallDamage(host, capturePath);
        await VerifyDevelopmentModels(host, capturePath);
        await WallUpgradeSmoke.Run(host, capturePath);
        await SelectionHighlightSmoke.Run(host, capturePath);
        await ConstructionKeyboardSmoke.Run(host, capturePath);
    }

    private static async Task VerifyDevelopmentModels(Main host, string? capturePath)
    {
        var session = host.Session;
        var game = session.Game;
        var map = game.Map;
        var bounds = game.Settlement!.Bounds;
        game.Paused = true;
        session.ClosePrompt();
        foreach (var area in map.BuildingFootprints.Values.ToArray())
            if (map.BuildingAt(area.X, area.Y)!.Zone != ZoneType.None)
            {
                map.SetBuilding(area.X, area.Y, null);
                foreach (var p in area.Cells()) map.SetZone(p.X, p.Y, ZoneType.None);
            }
        string[] names = ["Courtyard House", "Shanty Tower", "Timber Home", "Stone Home",
            "Blacksmith", "Woodsmith", "Armory", "Supply Shop", "Market", "Wall House"];
        var models = new List<(Pos Anchor, Vector3 Roof)>();
        for (int n = 0; n < names.Length; n++)
        {
            var type = map.Content.Buildings.Get(names[n]);
            var p = n == names.Length - 1 ? new Pos(bounds.X + 1, bounds.Y + 8) :
                new Pos(bounds.X + 4 + n % 3 * 5, bounds.Y + 4 + n / 3 * 4);
            var area = type.FootprintAt(p.X, p.Y);
            Require(area.Cells().All(cell => map.BuildingAt(cell.X, cell.Y) is null),
                "The development model fixture must not overwrite civic buildings.");
            map.SetBuildingFootprint(type, area);
            float height = type.Zone == ZoneType.Residential ? HouseAppearance.At(type, p.X, p.Y).Height : 0.4f;
            // The courtyard's back wing, unlike its open center, has a roof to pick.
            float z = type.Name == "Courtyard House" ? p.Y + 0.44f : p.Y + area.Height / 2f;
            float x = p.X + area.Width / 2f - (type.Name == "Wall House" ? 0.35f : 0);
            models.Add((p, new(x, TerrainSprite.Elevation(map, p.X, p.Y) + height, z)));
        }
        game.Touch();
        session.ClosePrompt();
        host.Map.SetPerspective(true);
        var view = host.Map.Perspective!;
        var pickedModels = new HashSet<Pos>();
        foreach (var (zoom, angle) in new[] { (-1, 0f), (0, 90f), (1, 180f), (-1, 270f) })
        {
            session.SetZoom(zoom);
            view.Rotate(angle);
            var focus = new Pos(bounds.X + 11, bounds.Y + 9);
            session.PlaceCursor(focus);
            host.Map.RefreshCells();
            session.CenterOn(focus);
            host.Map.Invalidate(true);
            await Frames(host);
            int checkedModels = 0;
            foreach (var model in models)
            {
                var screen = view.ScreenPoint(model.Roof);
                if (!new Rect2(Vector2.Zero, host.Map.Size).HasPoint(screen)) continue;
                Require(host.Map.TryPickBuilding(screen, out var picked),
                    $"Development model {map.BuildingAt(model.Anchor.X, model.Anchor.Y)!.Name} must have a mesh hit at {angle} degrees, zoom {zoom}.");
                if (picked != model.Anchor)
                {
                    Require(game.Settlement.Walls.Any(wall => wall.Cell == picked),
                        "A model obscured by a wall must pick that frontmost wall, not nearby land or another building.");
                    continue;
                }
                pickedModels.Add(model.Anchor);
                checkedModels++;
            }
            Require(checkedModels >= 3, "Each rotation and zoom must visibly exercise at least three development models.");
            await Capture(host, capturePath is null ? null :
                Path.ChangeExtension(capturePath, $".development-{angle:0}.png"));
        }
        Require(pickedModels.Count == models.Count, "Every new development model must be visibly picked in at least one camera orientation.");
        host.Map.SetPerspective(false);
        host.Map.Invalidate(true);
        await Frames(host);
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".development-overhead.png"));
        GD.Print("TERMCITY_DEVELOPMENT_MODELS_OK forms=10 rotations=4 zooms=3");
    }

    private static void ClickWorld(Main host, Pos cell)
    {
        host.Map.Perspective!.Sync(host.Session, host.Map.Size);
        var point = host.Map.Perspective.ScreenPoint(new(cell.X + 0.5f,
            TerrainSprite.Elevation(host.Session.Game.Map, cell.X, cell.Y), cell.Y + 0.5f));
        Require(new Rect2(Vector2.Zero, host.Map.Size).HasPoint(point),
            $"Enclosure corner {cell} must be on screen: point={point}, panel={host.Map.Size}, camera={host.Session.CameraX},{host.Session.CameraY}.");
        host.Map.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left, Pressed = true });
        host.Map.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left, Pressed = false });
    }

    internal static IEnumerable<Node> Descendants(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    internal static async Task Frames(Main host)
    {
        host.Map.QueueRedraw();
        for (int frame = 0; frame < 3; frame++)
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    internal static async Task Capture(Main host, string? path)
    {
        if (path is null) return;
        Require(DisplayServer.GetName() != "headless", "Captures require a graphical display.");
        for (int frame = 0; frame < 2; frame++)
        {
            host.Map.Perspective?.RequestRender();
            host.Map.QueueRedraw();
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            RenderingServer.ForceDraw();
        }
        using var image = host.GetViewport().GetTexture().GetImage();
        if (image.SavePng(path) != Error.Ok) throw new IOException("Settlement capture failed: " + path);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
