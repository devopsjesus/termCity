using Godot;
using System.Diagnostics;
using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using TermCity.Core.Util;
using TermCity.Core.World;

namespace TermCity.GodotApp;

internal static class PrototypeSmoke
{
    public static void Run(Main host)
    {
        var session = host.Session;
        var map = session.Game.Map;
        Require(host.Map.Grid.Columns >= 8 && host.Map.Grid.Rows >= 6,
            "Smoke viewport must be large enough to exercise map input.");
        Require(host.Map.Size.X <= host.Size.X && host.Map.Size.Y <= host.Size.Y,
            "Map layout extends beyond the window.");
        Require(host.Size == new Vector2(960, 640), "Window resize did not reach the scene.");
        Require(host.Map.Grid.Columns == (int)(host.Map.Size.X / TerminalGrid.CellWidth) &&
                host.Map.Grid.Rows == (int)(host.Map.Size.Y / TerminalGrid.CellHeight),
            "Window resize did not reach the cell grid.");
        Require(Godot.FileAccess.FileExists("res://Assets/DejaVu-LICENSE.txt"),
            "The bundled font license must also be distributed with exported players.");
        GD.Print($"Godot viewport: {host.Map.Grid.Columns}x{host.Map.Grid.Rows} cells");
        var before = session.Cursor;
        host._UnhandledInput(new InputEventKey { Keycode = Key.Right, Pressed = true });
        Require(session.Cursor.X == before.X + 1, "Keyboard movement did not reach the session.");

        var vacant = Enumerable.Range(0, map.Width * map.Height)
            .Select(i => new Pos(i % map.Width, i / map.Width))
            .First(p => session.Game.CanBuildOn(p.X, p.Y));
        session.PlaceCursor(vacant);
        host._UnhandledInput(new InputEventKey { Keycode = Key.B, Pressed = true });
        Require(session.Preview is not null, "Road preview was not created.");
        int zoned = session.Game.Stats.Residential.Zoned;
        host._UnhandledInput(new InputEventKey { Keycode = Key.R, Pressed = true });
        Require(session.Game.Stats.Residential.Zoned == zoned, "Preview allowed a background action.");
        host._UnhandledInput(new InputEventKey { Keycode = Key.Enter, Pressed = true });
        Require(session.Preview is null && map.HasRoad(vacant.X, vacant.Y), "Road confirmation failed.");

        vacant = Enumerable.Range(0, map.Width * map.Height)
            .Select(i => new Pos(i % map.Width, i / map.Width))
            .First(p => session.Game.CanBuildOn(p.X, p.Y));
        session.PlaceCursor(vacant);
        host._UnhandledInput(new InputEventKey { Keycode = Key.R, Pressed = true });
        Require(map.ZoneAt(vacant.X, vacant.Y) == ZoneType.Residential, "Zoning failed.");
        var loaded = SaveGameStore.Deserialize(SaveGameStore.Serialize(session.Game));
        Require(loaded.Stats == session.Game.Stats, "Save serialization changed city statistics.");

        host._UnhandledInput(new InputEventKey { Keycode = Key.Key3, Pressed = true });
        double days = session.Game.ElapsedDays;
        for (int i = 0; i < 8; i++)
        {
            session.Update(0.25);
        }
        Require(session.Game.ElapsedDays > days, "Simulation did not advance.");
        session.Game.Paused = true;
        Require(session.Autosave(), "Autosave failed.");
        for (int slot = 1; slot <= GameSession.AutosaveSlots; slot++)
        {
            string path = session.AutosavePath(slot);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        var watch = Stopwatch.StartNew();
        for (int zoom = GameSession.MinZoom; zoom <= GameSession.MaxZoom; zoom++)
        {
            session.SetZoom(zoom);
            session.ScrollCamera(17, 9);
            host.Map.Grid.Fill(session);
            Require(host.Map.Grid.Columns > 0 && host.Map.Grid.Rows > 0, "Viewport grid was empty.");
        }
        GD.Print($"Godot visible-cell rebuilds at all zooms: {watch.Elapsed.TotalMilliseconds:F1} ms");
        session.SetZoom(0);
        session.CenterOn(new Pos(map.Width / 2, map.Height / 2));
        host.Map.Invalidate(true);

        session.ShowPrompt("Smoke prompt", "Dismiss this prompt.", [new("Close", session.ClosePrompt)]);
        host._UnhandledInput(new InputEventKey { Keycode = Key.Enter, Pressed = true });
        Require(session.Prompt is null, "Prompt dismissal failed.");
        host._Notification(checked((int)Node.NotificationWMCloseRequest));
        Require(session.Prompt is not null, "Window close did not guard unsaved progress.");
        int homes = session.Game.Stats.Residential.Zoned;
        host._UnhandledInput(new InputEventKey { Keycode = Key.R, Pressed = true });
        Require(session.Game.Stats.Residential.Zoned == homes, "Quit prompt allowed a background action.");
        host._UnhandledInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        Require(session.Prompt is null, "Quit prompt cancellation failed.");
        host.Map.RefreshCells();

        var pointer = new Vector2(5 * TerminalGrid.CellWidth, 5 * TerminalGrid.CellHeight);
        var expected = session.ScreenToMap(5, 5);
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = pointer,
        });
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion
        {
            Position = pointer + new Vector2(2 * TerminalGrid.CellWidth, 0),
        });
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = false,
        });
        Require(session.Selection?.Width == 3 && session.Selection.Value.Contains(expected),
            "Mouse drag selection did not reach the session.");
        session.ClearSelection();
        host.Map.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton
        {
            ButtonIndex = MouseButton.WheelDown,
            Pressed = true,
            CtrlPressed = true,
            Position = pointer,
        });
        Require(session.ZoomLevel == -1, "Mouse wheel zoom failed.");
        session.SetZoom(0);
        var center = new Pos(map.Width / 2, map.Height / 2);
        session.PlaceCursor(center);
        session.CenterOn(center);
        host.Map.RefreshCells();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
