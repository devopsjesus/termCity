namespace TermCity.GodotApp;

public enum PausePart
{
    Ground, Table, Television, Mat, Chair, Ball, Head, Torso, UpperArm, LowerArm,
    Hand, Thigh, Shin, Foot, Caption, Paddle, Screen,
}

public readonly record struct PauseGlyph(string Text, double X, double Y, PausePart Part,
    double Rotation = 0, double Scale = 1);

public static class PauseRig
{
    public static IReadOnlyList<PauseGlyph> FrameAt(double seconds)
    {
        var activity = PauseAnimation.ActivityAt(seconds);
        double time = seconds % PauseAnimation.CycleSeconds;
        double beat = time % PauseAnimation.BeatSeconds / PauseAnimation.BeatSeconds;
        double wave = Math.Sin(beat * Math.Tau);
        double camera = PauseAnimation.CharacterPositionAt(seconds) - 15;
        var glyphs = new List<PauseGlyph>();
        World(-100, 10.3, [new string('_', 200)], PausePart.Ground);
        World(41, 7.3, ["--------------+", "|             |", "|             |"], PausePart.Table);
        World(55, 3.3, ["|", "|", "|", "|"], PausePart.Table);
        World(49, 6.3, ["|"], PausePart.Table);
        World(0, 3.3, ["+---------+", "|         |", "|         |", "+---------+", "    |      ", "-----------"], PausePart.Television);
        World(-35, 9.9, ["==========="], PausePart.Mat);

        double headY = 5.7;
        bool walking = activity is PauseActivity.WalkingToGame or PauseActivity.WalkingToMeditate or PauseActivity.WalkingToTable;
        bool gaming = activity == PauseActivity.VideoGame;
        bool meditating = activity == PauseActivity.Meditating;
        string head = meditating ? "(-_-)" : activity == PauseActivity.LookingRight ? "(>_>)" :
            activity == PauseActivity.LookingLeft || gaming ? "(<_<)" :
            walking ? activity == PauseActivity.WalkingToTable ? "(o>)" : "(<o)" :
            PauseAnimation.EyesClosedAt(seconds) ? "(-_-)" : "(o_o)";
        if (walking) headY += Math.Sin(beat * Math.Tau * 2) * 0.06;
        if (meditating) headY -= wave * 0.12;
        Add(head, 15, headY, PausePart.Head);
        Bone(15, headY + 0.5, 15, 8.2, PausePart.Torso);
        string caption;
        if (activity == PauseActivity.PingPong)
        {
            double travel = 1 - Math.Abs(beat * 2 - 1);
            Add("*", 40 + 14 * travel - camera, 6.3 - Math.Sin(travel * Math.PI) * 0.6, PausePart.Ball);
            double stroke = Math.Max(0, 1 - Math.Min(beat, 1 - beat) / 0.125);
            Arm(false, 16.5, 7.2 - stroke * 0.7, 18, 7.6 - stroke * 1.3, "]");
            Arm(true, 14, 7.6, 13.8, 8.2, "o");
            caption = "JUST PASSING THE TIME";
        }
        else if (gaming)
        {
            bool frustrated = time - 10 >= 4;
            Add("*", 2 + beat * 7 - camera, 4.3, PausePart.Screen);
            double handY = frustrated ? 5.2 + wave * 0.35 : 7.5 + wave * 0.12;
            Arm(true, 13.5, frustrated ? 6.5 : 7.4, 12.8, handY, frustrated ? "o" : ".");
            Arm(false, 15.7, 7.4, 13.1, 7.6 + wave * 0.08, ".");
            caption = frustrated ? "REALLY?!" : "ONE MORE LEVEL";
        }
        else
        {
            double swing = walking ? wave * 0.35 : 0;
            Arm(true, 13.9, 7.5 + swing, meditating ? 12.8 : 13.8, meditating ? 8.6 : 8.2 + swing, "o");
            Arm(false, 16.1, 7.5 - swing, meditating ? 17.2 : 16.2, meditating ? 8.6 : 8.2 - swing, "o");
            caption = meditating ? "BREATHE IN ... BREATHE OUT" : walking ? "NO HURRY..." :
                activity is PauseActivity.LookingRight or PauseActivity.LookingLeft ? "..." :
                PauseAnimation.EyesClosedAt(seconds) ? "..." : "STILL HERE?";
        }

        if (meditating)
        {
            Leg(true, 12.8, 8.9, 16.1, 9.5);
            Leg(false, 17.2, 8.9, 13.9, 9.5);
        }
        else if (gaming)
        {
            Leg(true, 13.9, 8.7, 13.5, 9.7);
            Leg(false, 16.1, 8.7, 16.5, 9.7);
            Add("_", 15, 8.5, PausePart.Chair);
        }
        else
        {
            double stride = walking ? wave * 0.6 : 0;
            Leg(true, 14.4, 8.9, 13.6 + stride, 9.7 - (walking ? Math.Max(0, wave) * 0.2 : 0));
            Leg(false, 15.6, 8.9, 16.4 - stride, 9.7 - (walking ? Math.Max(0, -wave) * 0.2 : 0));
        }

        double zoom = activity == PauseActivity.Blinking ? 1 :
            activity == PauseActivity.ZoomingIn ? (time - 32) / 2 :
            activity == PauseActivity.ZoomingOut ? 1 - (time - 46) / 2 : 0;
        double smooth = zoom * zoom * (3 - 2 * zoom);
        double scale = 1 + 5 * smooth;
        if (zoom > 0)
            for (int index = 0; index < glyphs.Count; index++)
            {
                var glyph = glyphs[index];
                glyphs[index] = glyph with
                {
                    X = 15 + 5 * smooth + (glyph.X - 15) * scale,
                    Y = headY + (glyph.Y - headY) * scale,
                    Scale = glyph.Scale * scale,
                };
            }
        Add(caption, PauseAnimation.Width / 2d, 12, PausePart.Caption);
        return glyphs;

        void Add(string text, double x, double y, PausePart part, double rotation = 0, double size = 1) =>
            glyphs.Add(new(text, x, y, part, rotation, size));
        void World(double x, double y, string[] lines, PausePart part)
        {
            for (int row = 0; row < lines.Length; row++)
                Add(lines[row], x - camera + (lines[row].Length - 1) / 2d, y + row, part);
        }
        void Bone(double x1, double y1, double x2, double y2, PausePart part)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double length = Math.Sqrt(dx * dx / 4 + dy * dy);
            Add("|", (x1 + x2) / 2, (y1 + y2) / 2, part,
                Math.Atan2(dy * 2, dx) - Math.PI / 2, length);
        }
        void Arm(bool left, double elbowX, double elbowY, double handX, double handY, string hand)
        {
            Bone(left ? 14.5 : 15.5, 6.7, elbowX, elbowY, PausePart.UpperArm);
            Bone(elbowX, elbowY, handX, handY, PausePart.LowerArm);
            Add(hand, handX, handY, hand == "]" ? PausePart.Paddle : PausePart.Hand);
        }
        void Leg(bool left, double kneeX, double kneeY, double footX, double footY)
        {
            Bone(left ? 14.7 : 15.3, 8.2, kneeX, kneeY, PausePart.Thigh);
            Bone(kneeX, kneeY, footX, footY, PausePart.Shin);
            Add("_", footX, footY, PausePart.Foot);
        }
    }
}
