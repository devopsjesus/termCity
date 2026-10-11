namespace TermCity.GodotApp;

public enum PauseActivity
{
    PingPong,
    WalkingToGame,
    VideoGame,
    WalkingToMeditate,
    Meditating,
    LookingRight,
    LookingLeft,
    FacingViewer,
    ZoomingIn,
    Blinking,
    ZoomingOut,
    WalkingToTable,
}

public static class PauseAnimation
{
    public const int Width = 41, Height = 13;
    public const int FramesPerSecond = 8;
    public const double BeatSeconds = 2;
    private const double TurnSeconds = 0.5;
    public const double MeditationStarts = 10 * BeatSeconds;
    public const double CycleSeconds = 28 * BeatSeconds;

    public static PauseActivity ActivityAt(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        return (seconds % CycleSeconds) switch
        {
            < 3 * BeatSeconds => PauseActivity.PingPong,
            < 5 * BeatSeconds => PauseActivity.WalkingToGame,
            < 8 * BeatSeconds => PauseActivity.VideoGame,
            < 10 * BeatSeconds => PauseActivity.WalkingToMeditate,
            < 13 * BeatSeconds => PauseActivity.Meditating,
            < 14 * BeatSeconds => PauseActivity.LookingRight,
            < 15 * BeatSeconds => PauseActivity.LookingLeft,
            < 16 * BeatSeconds => PauseActivity.FacingViewer,
            < 17 * BeatSeconds => PauseActivity.ZoomingIn,
            < 23 * BeatSeconds => PauseActivity.Blinking,
            < 24 * BeatSeconds => PauseActivity.ZoomingOut,
            _ => PauseActivity.WalkingToTable,
        };
    }

    internal static int CharacterXAt(double seconds) => (int)Math.Round(CharacterPositionAt(seconds));

    internal static double CharacterPositionAt(double seconds)
    {
        var activity = ActivityAt(seconds);
        double time = seconds % CycleSeconds;
        return activity switch
        {
            PauseActivity.PingPong => 37,
            PauseActivity.WalkingToGame => Walk(37, 14, 6, 4),
            PauseActivity.VideoGame => 14,
            PauseActivity.WalkingToMeditate => Walk(14, -30, 16, 4),
            PauseActivity.WalkingToTable => Walk(-30, 37, 48, 8),
            _ => -30,
        };

        double Walk(int from, int to, double start, double duration) =>
            from + (to - from) * Math.Clamp(
                (time - start - TurnSeconds) / (duration - TurnSeconds), 0, 1);
    }

    internal static bool EyesClosedAt(double seconds)
    {
        if (ActivityAt(seconds) != PauseActivity.Blinking) return false;
        return (seconds % CycleSeconds - 34) switch
        {
            >= 4 and < 4.25 => true,
            >= 6 and < 6.25 => true,
            >= 8 and < 8.25 => true,
            >= 10 and < 10.125 => true,
            >= 10.375 and < 10.5 => true,
            _ => false,
        };
    }

    public static string FrameAt(double seconds)
    {
        var activity = ActivityAt(seconds);
        double time = seconds % CycleSeconds;
        double beat = time % BeatSeconds / BeatSeconds;
        int character = CharacterXAt(seconds);
        int camera = character - 15;
        var canvas = Enumerable.Range(0, Height).Select(_ => new string(' ', Width).ToCharArray()).ToArray();
        bool closeup = activity is PauseActivity.ZoomingIn or PauseActivity.Blinking or PauseActivity.ZoomingOut;
        double zoom = activity == PauseActivity.Blinking ? 1 :
            activity == PauseActivity.ZoomingIn ? (time - 32) / BeatSeconds :
            activity == PauseActivity.ZoomingOut ? 1 - (time - 46) / BeatSeconds : 0;
        if (!closeup || zoom == 0)
        {
            Write(0, 10, ["_________________________________________"]);
            World(41, 7, ["--------------+", "|             |", "|             |"]);
            World(55, 3, ["|", "|", "|", "|"]);
            World(49, 6, ["|"]);
            World(0, 3, ["+---------+", "|         |", "|         |", "+---------+", "    |      ", "-----------"]);
            World(-35, 9, ["==========="]);
        }
        switch (activity)
        {
            case PauseActivity.PingPong:
                bool striking = beat >= 0.875 || beat < 0.125;
                World(character - 3, 6, striking ? [" o\\]", " |  ", "/ \\ "] : [" o  ", " |\\]", "/ \\ "]);
                int ball = (int)Math.Round(12 * (1 - Math.Abs(beat * 2 - 1)));
                Put(42 + ball - camera, ball is >= 5 and <= 8 ? 5 : 6, '*');
                Caption("JUST PASSING THE TIME");
                break;
            case PauseActivity.VideoGame:
                Put(2 + (int)(beat * 7) - camera, 4, '*');
                bool frustrated = (time - 10) % 6 >= 4;
                World(character - 2, 5, frustrated
                    ? beat < 0.5 ? ["o o  ", " \\|\\ ", " _|_ ", "/   \\"] : ["  o  ", "o_|\\ ", " _|_ ", "/   \\"]
                    : beat < 0.25 ? ["  o  ", "._|\\ ", " _|_ ", "/   \\"] : ["  o  ", "--|\\ ", " _|_ ", "/   \\"]);
                Caption(frustrated ? "REALLY?!" : "ONE MORE LEVEL");
                break;
            case PauseActivity.WalkingToGame:
            case PauseActivity.WalkingToMeditate:
            case PauseActivity.WalkingToTable:
                bool left = activity != PauseActivity.WalkingToTable;
                double walkingStart = activity == PauseActivity.WalkingToGame ? 6 :
                    activity == PauseActivity.WalkingToMeditate ? 16 : 48;
                World(character - 2, 6, [left ? " <o  " : "  o> ", " /|\\ ",
                    time - walkingStart < TurnSeconds || beat < 0.5 ? " / \\ " : "  /| "]);
                Caption("NO HURRY...");
                break;
            case PauseActivity.Meditating:
                World(character - 5, 4, ["   _____   ", "  ( -_- )  ", "   /| |\\   ", "  / | | \\  ", " (_/___\\_) "]);
                World(character - 3, beat < 0.5 ? 3 : 2, [" .     . "]);
                Caption("BREATHE IN ... BREATHE OUT");
                break;
            case PauseActivity.LookingRight:
            case PauseActivity.LookingLeft:
            case PauseActivity.FacingViewer:
                World(character - 2, 5, [activity == PauseActivity.LookingRight ? "  ( >) " :
                    activity == PauseActivity.LookingLeft ? "  (< ) " : "  (o o)", "   |   ", "  /|\\  ", "  / \\  "]);
                Caption(activity == PauseActivity.FacingViewer ? "... OH, HELLO." : "...");
                break;
            case PauseActivity.ZoomingIn:
            case PauseActivity.Blinking:
            case PauseActivity.ZoomingOut:
                bool blink = EyesClosedAt(seconds);
                if (zoom == 0)
                    World(character - 2, 5, ["  (o o)", "   |   ", "  /|\\  ", "  / \\  "]);
                else
                    Face(zoom, blink);
                Caption(blink ? "..." : "STILL HERE?");
                break;
        }
        return string.Join('\n', canvas.Select(row => new string(row)));

        void Put(int x, int y, char value)
        {
            if (x >= 0 && x < Width && y >= 0 && y < Height) canvas[y][x] = value;
        }
        void Write(int x, int y, string[] lines)
        {
            for (int row = 0; row < lines.Length; row++)
                for (int column = 0; column < lines[row].Length; column++)
                    Put(x + column, y + row, lines[row][column]);
        }
        void World(int x, int y, string[] lines) => Write(x - camera, y, lines);
        void Caption(string text) => Write((Width - text.Length) / 2, Height - 1, [text]);
        void Face(double amount, bool blink)
        {
            double smooth = amount * amount * (3 - 2 * amount);
            int width = 5 + (int)Math.Round(30 * smooth);
            int height = 3 + (int)Math.Round(6 * smooth);
            int center = 17 + (int)Math.Round(3 * smooth);
            int left = center - width / 2, top = (Height - 1 - height) / 2;
            Write(left, top, ["." + new string('-', width - 2) + "."]);
            for (int row = 1; row < height - 1; row++)
            {
                Put(left, top + row, '|');
                Put(left + width - 1, top + row, '|');
            }
            Write(left, top + height - 1, ["\\" + new string('_', width - 2) + "/"]);
            string eye = blink ? "-----" : smooth > 0.6 ? "( o )" : "o";
            int eyeRow = top + Math.Max(1, height / 3);
            Write(left + width / 3 - eye.Length / 2, eyeRow, [eye]);
            Write(left + width * 2 / 3 - eye.Length / 2, eyeRow, [eye]);
            if (height >= 5)
            {
                Put(center, top + height / 2, '^');
                Write(center - 1, top + height - 2, ["---"]);
            }
        }
    }
}
