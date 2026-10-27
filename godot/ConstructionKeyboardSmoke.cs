using Godot;
using TermCity.Core.Buildings;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using static TermCity.GodotApp.SettlementSmoke;

namespace TermCity.GodotApp;

internal static class ConstructionKeyboardSmoke
{
    internal static async Task Run(Main host, string? capturePath)
    {
        var session = host.Session;
        var game = session.Game;
        game.Paused = true;
        session.ClosePrompt();
        session.ClearSelection();
        var rail = Descendants(host).OfType<ScrollContainer>().Single(node => node.Name == "ConstructionSidebar");
        var icons = Descendants(rail).OfType<Button>().ToArray();
        Button Add() => Descendants(host).OfType<Button>().Single(button => button.Name == "ConstructionAdd");
        Button[] Types() => Descendants(host).OfType<VBoxContainer>().Single(node => node.Name == "ConstructionTypes")
            .GetChildren().OfType<Button>().ToArray();
        void Press(Key key, bool shift = false, bool echo = false) => host._Input(new InputEventKey
        {
            Pressed = true, Keycode = key, PhysicalKeycode = key, ShiftPressed = shift, Echo = echo,
        });
        void Choice(string label)
        {
            var prompt = session.Prompt!;
            int index = prompt.Choices.ToList().FindIndex(choice => choice.Label == label);
            Require(index >= 0, $"Expected menu option {label}.");
            var shortcut = prompt.Shortcuts[index];
            Press((Key)shortcut.Letter, shortcut.Shift);
        }
        int checkedChoices = 0;
        foreach (var icon in icons)
        {
            icon.EmitSignal(Button.SignalName.Pressed);
            await Frames(host);
            var prompt = session.Prompt!;
            var buttons = Types();
            int money = game.Money;
            Require(buttons.Length == prompt.Choices.Count, "Every catalog row must have a shortcut.");
            for (int index = 0; index < buttons.Length; index++)
            {
                var shortcut = prompt.Shortcuts[index];
                Require(buttons[index] is MnemonicButton mnemonic &&
                    mnemonic.UnderlineIndex >= 0 && mnemonic.UnderlineIndex < mnemonic.Text.Length &&
                    char.ToUpperInvariant(mnemonic.Text[mnemonic.UnderlineIndex]) == shortcut.Letter,
                    "Every catalog shortcut must underline the actual keyboard letter.");
                Press((Key)shortcut.Letter, shortcut.Shift);
                Require(prompt.SelectedIndex == index && buttons[index].ButtonPressed &&
                    session.Preview is null && game.Money == money,
                    "A type hotkey must select its model/details without purchasing, including locked choices.");
                if (Add().Disabled)
                {
                    Press(Key.Enter);
                    Require(ReferenceEquals(session.Prompt, prompt) && session.Preview is null && game.Money == money,
                        "Enter must not bypass a locked, unaffordable or unavailable Add action.");
                }
                checkedChoices++;
            }
            if (icon.Name == "Water")
                await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".building-hotkeys.png"));
            Press(Key.Escape);
            await Frames(host);
        }

        var waterIcon = icons.Single(icon => icon.Name == "Water");
        waterIcon.EmitSignal(Button.SignalName.Pressed);
        await Frames(host);
        var waterTypes = Types();
        if (waterTypes.Length > 1)
        {
            waterTypes[1].GrabFocus();
            Require(session.Prompt!.SelectedIndex == 1 && waterTypes[1].ButtonPressed,
                "Keyboard focus must update the selected type, not leave Add targeting the previous row.");
        }
        Descendants(host).OfType<Button>().Single(button => button.Name == "ConstructionCancel").GrabFocus();
        Press(Key.Enter);
        Require(session.Prompt is null && session.Preview is null, "A focused Cancel must remain keyboard accessible.");
        waterIcon.EmitSignal(Button.SignalName.Pressed);
        await Frames(host);
        Choice("Town Well");
        int before = game.Money;
        Press(Key.Enter, echo: true);
        Require(session.Prompt is not null && session.Preview is null, "Held Enter must not repeatedly activate Add.");
        Press(Key.Enter);
        Require(session.Prompt is null && session.Preview?.Building?.Name == "Town Well" && game.Money == before,
            "Enter on a type row must activate Add and start ordinary sidebar placement without spending gold.");
        session.CancelPreview();
        session.ClosePrompt();

        var well = game.Map.Content.Buildings.Get("Town Well");
        async Task RightClick(Pos cell)
        {
            host.Map.SetPerspective(true);
            session.PlaceCursor(cell);
            session.SetZoom(1);
            host.Map.RefreshCells();
            session.CenterOn(cell);
            await Frames(host);
            var view = host.Map.Perspective!;
            var wall = game.Settlement!.Walls.FirstOrDefault(w => w.Cell == cell);
            Vector2 Point() => view.ScreenPoint(wall is null
                ? new(cell.X + 0.5f, TerrainSprite.Elevation(game.Map, cell.X, cell.Y), cell.Y + 0.5f)
                : view.WallTop(wall) - Vector3.Up * 0.2f);
            var point = Point();
            for (int angle = 0; angle < 4 && host.Map.TryPickBuilding(point, out var owner) && owner != cell; angle++)
            {
                view.Rotate(90);
                await Frames(host);
                point = Point();
            }
            host.Map.EmitSignal(Control.SignalName.GuiInput,
                new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Right, Position = point });
            await Frames(host);
        }
        Pos Spot(BuildingType type) => game.Settlement!.Bounds.Cells().OrderBy(cell =>
            Math.Abs(cell.X - game.Settlement.Bounds.X - game.Settlement.Bounds.Width / 2) +
            Math.Abs(cell.Y - game.Settlement.Bounds.Y - game.Settlement.Bounds.Height / 2)).First(cell =>
            game.Settlement.ContainsInterior(cell) && game.BuildingPlacementError(type, cell.X, cell.Y) is null);
        var target = Spot(well);
        await RightClick(target);
        Require(session.Prompt?.Title == "Area menu",
            $"An empty right-clicked map spot must open the area menu: target={target}, cursor={session.Cursor}, menu={session.Prompt?.Title}.");
        Choice("Services");
        await Frames(host);
        int cost = game.QuoteBuilding(well, well.FootprintAt(target.X, target.Y)).Cost;
        before = game.Money;
        Choice("Town Well");
        Require(game.Map.BuildingAt(target.X, target.Y) == well && game.Money == before - cost &&
            session.Prompt is null && session.Preview is null,
            "A fitting service selected through the right-click menu must be purchased at the original map spot.");

        var tower = game.Settlement!.Walls.First(wall => wall.IsTower && wall.Defense == WallDefenseKind.None);
        await RightClick(tower.Cell);
        // Aim at the actual model rather than terrain beneath the tower.
        if (session.Prompt?.Title != "Wall inspection")
        {
            session.ClosePrompt();
            var point = host.Map.Perspective!.ScreenPoint(host.Map.Perspective.WallTop(tower) - Vector3.Up * 0.2f);
            host.Map.EmitSignal(Control.SignalName.GuiInput,
                new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Right, Position = point });
            await Frames(host);
        }
        Require(session.Prompt?.Title == "Wall inspection", "Right-clicking a tower must inspect its actual geometry.");
        Choice("Garrison");
        await Frames(host);
        Choice("Dropped rocks");
        Require(!Add().Disabled, "The reached milestone must allow the selected wall weapon.");
        Press(Key.KpEnter);
        Require(tower.Defense == WallDefenseKind.Rocks && session.Prompt is null && session.Preview is null,
            "Keypad Enter must Add a valid defense immediately to the right-clicked tower.");

        var woodlot = game.Map.Content.Buildings.Get("Woodlot");
        var cramped = game.Settlement.Bounds.Cells().First(cell => game.Settlement.ContainsInterior(cell) &&
            game.Map.BuildingAt(cell.X, cell.Y) is null && game.BuildingPlacementError(well, cell.X, cell.Y) is null &&
            game.BuildingPlacementError(woodlot, cell.X, cell.Y) is not null);
        await RightClick(cramped);
        Require(session.Prompt?.Title == "Area menu", "The cramped test point must be an empty cell.");
        Choice("Services");
        await Frames(host);
        before = game.Money;
        Choice("Woodlot");
        Require(session.Preview?.Building == woodlot && session.BuildingPlacementError is not null &&
            game.Money == before && session.MessageKind == MessageKind.Error,
            "A building that does not fit must remain an unpaid movable preview with an explicit placement error.");
        session.CancelPreview();
        session.ClosePrompt();
        await RightClick(Spot(well));
        Press(Key.Escape);
        waterIcon.EmitSignal(Button.SignalName.Pressed);
        await Frames(host);
        Choice("Town Well");
        before = game.Money;
        Press(Key.Enter);
        Require(session.Preview?.Building == well && game.Money == before,
            "Cancelling a context menu must not leak its automatic placement into a later sidebar catalog.");
        session.CancelPreview();
        session.ClosePrompt();

        var window = host.GetWindow();
        bool headless = DisplayServer.GetName() == "headless";
        var originalSize = headless ? window.ContentScaleSize : window.Size;
        foreach (int width in new[] { 960, 640 })
        {
            if (headless) window.ContentScaleSize = new(width, Main.MinimumWindowHeight);
            else window.Size = new(width, Main.MinimumWindowHeight);
            waterIcon.EmitSignal(Button.SignalName.Pressed);
            await Frames(host);
            var dialog = Descendants(host).OfType<TerminalFrame>().Single(frame => frame.Name == "CityDialog");
            Require(dialog.ClipContents && dialog.GetGlobalRect().End.X <= host.Size.X + 1 &&
                dialog.GetGlobalRect().End.Y <= host.Size.Y + 1 && Types().All(button => button is MnemonicButton),
                "Underlined building catalogs must preserve terminal framing and fit narrow windows.");
            await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, $".hotkeys-{width}.png"));
            session.ClosePrompt();
        }
        if (headless) window.ContentScaleSize = originalSize;
        else window.Size = originalSize;
        await Frames(host);
        GD.Print($"TERMCITY_CONSTRUCTION_KEYBOARD_OK shortcuts={checkedChoices} underline=all enter=add keypad=add context=fitting-only stale-context=cleared widths=960,640");
    }

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
