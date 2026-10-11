using Godot;
using TermCity.Core.Simulation;

namespace TermCity.GodotApp;

public partial class CalendarIcon : Control
{
    public CalendarFeast Feast { get; set; }

    public CalendarIcon()
    {
        CustomMinimumSize = new Vector2(18, 18);
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        string[] pixels = Feast switch
        {
            CalendarFeast.Candlemas => ["...#...", "..###..", "...#...", "..###..", "..###..", "..###..", ".#####."],
            CalendarFeast.LadyDay => ["...#...", ".#.#.#.", "..###..", "#######", "..###..", ".#.#.#.", "...#..."],
            CalendarFeast.MayDay => ["...#...", "..###..", ".#.#.#.", "#..#..#", "...#...", "...#...", "..###.."],
            CalendarFeast.Middsommer => ["#..#..#", ".#####.", ".#...#.", "##...##", ".#...#.", ".#####.", "#..#..#"],
            CalendarFeast.Lammas => [".#.#.#.", "..###..", ".#.#.#.", "..###..", ".#.#.#.", "...#...", "...#..."],
            CalendarFeast.Michaelmas => ["...#...", "..###..", "..###..", ".#####.", "#..#..#", "...#...", "..###.."],
            CalendarFeast.AllHallows => [".#####.", "#######", "#.#.#.#", "#######", ".#.#.#.", "..###..", "..#.#.."],
            CalendarFeast.Christmas => ["...#...", "..###..", ".#####.", "..###..", ".#####.", "#######", "...#..."],
            _ => ["#.#.#.#", "#######", "#.....#", "#.#.#.#", "#.....#", "#.#.#.#", "#######"],
        };
        float pixelSize = Math.Max(1, Math.Min(2, MathF.Floor(Size.Y / 7)));
        var origin = ((Size - new Vector2(7, 7) * pixelSize) / 2).Floor();
        var color = Feast == CalendarFeast.None ? Colors.White : new Color("#ffe066");
        for (int y = 0; y < pixels.Length; y++)
            for (int x = 0; x < pixels[y].Length; x++)
                if (pixels[y][x] == '#')
                    DrawRect(new Rect2(origin + new Vector2(x, y) * pixelSize, new Vector2(pixelSize, pixelSize)), color);
    }
}
