using Godot;
using TermCity.Core.Persistence;
using TermCity.Core.Session;
using TermCity.Core.Simulation;
using static TermCity.GodotApp.SettlementSmoke;

namespace TermCity.GodotApp;

internal static class DebugSiegeSmoke
{
    internal static async Task Run(Main host, string? capturePath)
    {
        var session = host.Session;
        var game = session.Game;
        Require(session.DebugSiegeMode, "The debug-control fixture must explicitly enable Debug Siege mode.");
        var strip = Descendants(host.Map).OfType<TerminalFrame>().Single(node => node.Name == "DebugSiegeControls");
        var size = Descendants(strip).OfType<LineEdit>().Single(node => node.Name == "DebugSwarmSize");
        var wallWidth = Descendants(strip).OfType<LineEdit>().Single(node => node.Name == "DebugWallWidth");
        var wallHeight = Descendants(strip).OfType<LineEdit>().Single(node => node.Name == "DebugWallHeight");
        Button FindButton(string name) => Descendants(strip).OfType<Button>().Single(node => node.Name == name);
        var start = FindButton("DebugStartWave");
        var clear = FindButton("DebugClearGoblins");
        var repair = FindButton("DebugRepairBuildings");
        var restart = FindButton("DebugRestartGame");
        game.Paused = true;
        size.Text = "";
        size.GrabFocus();
        await Frames(host);
        var speed = game.Speed;
        var cursor = session.Cursor;
        Input.ParseInputEvent(new InputEventKey { Pressed = true, Keycode = Key.Key3, PhysicalKeycode = Key.Key3, Unicode = '3' });
        await Frames(host);
        Input.ParseInputEvent(new InputEventKey { Pressed = false, Keycode = Key.Key3, PhysicalKeycode = Key.Key3 });
        Require(size.Text == "3" && game.Speed == speed && session.Cursor == cursor && session.Prompt is null,
            "Typing swarm digits must edit the input without changing speed, cursor or menus.");
        foreach (string invalid in new[] { "", "0", "121", "abc" })
        {
            size.Text = invalid;
            string saved = SaveGameStore.Serialize(game);
            start.EmitSignal(Godot.Button.SignalName.Pressed);
            Require(session.MessageKind == MessageKind.Error && SaveGameStore.Serialize(game) == saved,
                "Invalid swarm inputs must report an error without changing the siege.");
        }
        int mapVersion = game.MapVersion, gold = game.Money, builds = host.Map.Perspective!.GeometryBuilds;
        int horns = host.BattleHornPlayCount;
        size.Text = "17";
        game.Speed = GameSpeed.Fast;
        start.EmitSignal(Godot.Button.SignalName.Pressed);
        await Frames(host);
        Require(game.Settlement!.Goblins.Count == 17 && host.Map.Perspective.GoblinsDrawn == 17 &&
            game.MapVersion == mapVersion && host.Map.Perspective.GeometryBuilds == builds && game.Money == gold,
            "Start wave must use the exact requested size without spending gold or rebuilding city geometry.");
        Require(game.Speed == GameSpeed.Slow && host.BattleHornPlayCount == horns + 1,
            "Starting a debug wave must switch to 1x and play one battle horn.");
        clear.EmitSignal(Godot.Button.SignalName.Pressed);
        await Frames(host);
        Require(game.Settlement.Goblins.Count == 0 && game.Settlement.Projectiles.Count == 0 &&
            game.Settlement.NextWaveFraction == 1 && host.Map.Perspective.GoblinsDrawn == 0,
            "Remove goblins must clear every attacker/projectile and refresh the countdown and renderer.");
        await SiegeAnimationSmoke.VerifyRepairCrew(host, capturePath);
        size.Text = SettlementState.MaximumGoblinSwarm.ToString();
        size.EmitSignal(LineEdit.SignalName.TextSubmitted, size.Text);
        await Frames(host);
        Require(game.Settlement.Goblins.Count == SettlementState.MaximumGoblinSwarm &&
            host.Map.Perspective.GoblinsDrawn == SettlementState.MaximumGoblinSwarm,
            "Submitting the size field must support the full saved-game swarm limit.");
        await SiegeAnimationSmoke.VerifyActiveSwarmBudget(host);
        Require(game.WallRepairCost > 0, "The played siege fixture must contain actual wall damage.");
        repair.EmitSignal(Godot.Button.SignalName.Pressed);
        Require(game.Settlement.Walls.All(wall => wall.Health == wall.MaxHealth) && game.Money == gold,
            "Repair all must restore every damaged structure for free.");
        await SiegeAnimationSmoke.VerifyWallAttack(host, capturePath);
        Require(host.BattleHornPlayCount == horns + 2, "Clearing and repairing must not replay wave-start horns.");
        wallHeight.Text = "20";
        foreach (string invalid in new[] { "", "0", "161", "abc" })
        {
            wallWidth.Text = invalid;
            string saved = SaveGameStore.Serialize(game);
            restart.EmitSignal(Godot.Button.SignalName.Pressed);
            Require(session.MessageKind == MessageKind.Error && session.Prompt is null &&
                SaveGameStore.Serialize(game) == saved,
                "Invalid wall dimensions must report an error before offering to discard the current game.");
        }
        wallWidth.Text = "24";
        bool headless = DisplayServer.GetName() == "headless";
        var window = host.GetWindow();
        var originalSize = headless ? window.ContentScaleSize : window.Size;
        foreach (int width in new[] { 960, 640 })
        {
            if (headless) window.ContentScaleSize = new(width, Main.MinimumWindowHeight);
            else window.Size = new(width, Main.MinimumWindowHeight);
            await Frames(host);
            var map = host.Map.GetGlobalRect();
            var bounds = strip.GetGlobalRect();
            Require(bounds.Position.X >= map.Position.X && bounds.Position.Y >= map.Position.Y &&
                bounds.End.X <= map.End.X + 1 && bounds.End.Y <= map.End.Y + 1,
                "Map-edge debug controls must wrap and remain inside normal and narrow map panels.");
            Require(!strip.JoinAdjacent && strip.GetThemeStylebox("panel").GetContentMargin(Side.Left) == TerminalFrame.Inset &&
                size.GetThemeStylebox("normal") is StyleBoxFlat { BgColor: var color } && color == Colors.Black,
                "Debug controls must reuse closed TerminalFrame borders, shared insets and black square input styling.");
            await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, $".debug-controls-{width}.png"));
        }
        if (headless) window.ContentScaleSize = originalSize;
        else window.Size = originalSize;
        await Frames(host);
        restart.EmitSignal(Godot.Button.SignalName.Pressed);
        Require(session.Prompt?.Title == "Restart debug siege" && start.Disabled && !size.Editable &&
            !wallWidth.Editable && !wallHeight.Editable,
            "Restart must use the standard progress guard and disable the underlying debug inputs.");
        session.SelectPrompt(2);
        Require(ReferenceEquals(game, session.Game), "Cancelling restart must preserve the current game.");
        restart.EmitSignal(Godot.Button.SignalName.Pressed);
        session.SelectPrompt(1);
        Require(!ReferenceEquals(game, session.Game) && session.Game.Config.Seed == game.Config.Seed &&
            session.Game.Stats.Population == 100 && session.Game.Settlement is { WaveNumber: 1 } fresh &&
            fresh.Bounds.Width == 24 && fresh.Bounds.Height == 20 &&
            fresh.Goblins.Count == 24 && fresh.Walls.All(wall => wall.Health == wall.MaxHealth) &&
            session.DebugSiegeMode && session.UndoCount == 0 && session.Game.Speed == GameSpeed.Slow &&
            host.BattleHornPlayCount == horns + 3,
            "Restart must apply the selected wall rectangle and start the same-seed siege at 1x with one horn.");
        session.Game.Paused = true;
        await Frames(host);
        await Capture(host, capturePath is null ? null : Path.ChangeExtension(capturePath, ".resized-walls.png"));
        await VerifyHornSoundSettings(host);
        GD.Print("TERMCITY_DEBUG_SIEGE_CONTROLS_OK sizes=1-120 walls=24x20 start=exact clear=all repair=free restart=same-seed horns=once speed=1x widths=960,640");
    }

    private static async Task VerifyHornSoundSettings(Main host)
    {
        var session = host.Session;
        bool initialSound = host.SoundEnabled;
        double initialVolume = host.SoundVolume;
        int horns = host.BattleHornPlayCount;
        var player = host.GetNode<AudioStreamPlayer>("BattleHorn");
        Require(player.Stream is AudioStreamWav { Stereo: false, MixRate: BattleHorn.SampleRate } stream &&
            stream.Data.Length == BattleHorn.Render().Length,
            "The wave cue must use the original synthesized horn PCM.");
        GodotSmoke.OpenGameMenu(host);
        await Frames(host);
        var mute = Descendants(host).OfType<Button>().Single(button => button.Name == "SoundMute");
        if (host.SoundEnabled) mute.EmitSignal(Godot.Button.SignalName.Pressed);
        Require(!player.Playing, "Muting sound must immediately stop any active battle horn.");
        session.ClosePrompt();
        session.DebugStartWave(1);
        Require(host.BattleHornPlayCount == horns && session.Game.Speed == GameSpeed.Slow,
            "Muted waves must still switch to 1x without playing a horn.");
        GodotSmoke.OpenGameMenu(host);
        await Frames(host);
        mute = Descendants(host).OfType<Button>().Single(button => button.Name == "SoundMute");
        mute.EmitSignal(Godot.Button.SignalName.Pressed);
        var volume = Descendants(host).OfType<HSlider>().Single(slider => slider.Name == "SoundVolume");
        volume.Value = 0;
        session.ClosePrompt();
        session.DebugStartWave(1);
        Require(host.BattleHornPlayCount == horns && !player.Playing,
            "Zero sound volume must suppress and stop wave horns.");
        GodotSmoke.OpenGameMenu(host);
        await Frames(host);
        volume = Descendants(host).OfType<HSlider>().Single(slider => slider.Name == "SoundVolume");
        volume.Value = initialVolume;
        if (!initialSound)
            Descendants(host).OfType<Button>().Single(button => button.Name == "SoundMute")
                .EmitSignal(Godot.Button.SignalName.Pressed);
        session.ClosePrompt();
        session.DebugStartWave(1);
        Require(host.BattleHornPlayCount == horns + (initialSound && initialVolume > 0 ? 1 : 0),
            "Restored sound settings must control the next wave horn.");
        GD.Print("TERMCITY_BATTLE_HORN_OK pcm=original mute=honored volume=honored speed=1x");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
