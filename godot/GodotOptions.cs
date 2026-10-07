using System.Globalization;
using TermCity.Core.Simulation;

namespace TermCity.GodotApp;

public sealed record GodotOptions(GameConfig Config, bool SmokeTest, string? CapturePath)
{
    public bool Load { get; init; }
    public string? LoadPath { get; init; }
    public bool Help { get; init; }
    public bool DumpMap { get; init; }
    public bool ReducedMotion { get; init; }
    public int FramesPerSecond { get; init; } = 30;

    public const string Usage = """
        TermCity Godot - a desktop city builder
        User options follow Godot's -- separator:
          --seed <n>        Signed 32-bit map seed (default: random)
          --size <size>     small, medium, large, SF, LA, SD, CHI, STL, or WIDTHxHEIGHT
          --load [file]     Load a city (default: Godot quick-save)
          --fps <n>         Maximum rendered frames per second, 5-60 (default: 30)
          --dump-map        Print the map and exit
          --reduced-motion  Turn all visual effects off (the V key cannot turn them back on)
          -h, --help        Show this help
          --smoke-test     Run engine integration checks and exit
          --capture <path> Save a PNG during a graphical smoke run
        """;

    public static GodotOptions Parse(string[] arguments)
    {
        var config = new GameConfig { Seed = Random.Shared.Next() };
        bool smokeTest = false;
        string? capturePath = null;
        bool load = false, help = false, dump = false, reducedMotion = false;
        string? loadPath = null;
        int fps = 30;
        for (int i = 0; i < arguments.Length; i++)
        {
            string argument = arguments[i];
            if (argument is "-h" or "--help")
            {
                help = true;
                continue;
            }
            if (argument == "--dump-map")
            {
                dump = true;
                continue;
            }
            if (argument == "--reduced-motion")
            {
                reducedMotion = true;
                continue;
            }
            if (argument == "--load")
            {
                load = true;
                if (i + 1 < arguments.Length && !arguments[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    loadPath = arguments[++i];
                }
                continue;
            }
            if (argument == "--smoke-test")
            {
                smokeTest = true;
                continue;
            }

            if (argument is not ("--seed" or "--size" or "--capture" or "--fps"))
            {
                throw new ArgumentException($"Unknown Godot option '{argument}'.");
            }

            if (++i >= arguments.Length)
            {
                throw new ArgumentException($"Missing value for {argument}.");
            }

            string value = arguments[i];
            switch (argument)
            {
                case "--seed":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
                    {
                        throw new ArgumentException("--seed must be a signed 32-bit integer.");
                    }
                    config = config with { Seed = seed };
                    break;
                case "--size":
                    if (!MapSize.TryParse(value, out var size, out string error))
                    {
                        throw new ArgumentException(error);
                    }
                    config = config with { MapWidth = size.Width, MapHeight = size.Height, Scenario = size.Scenario };
                    break;
                case "--capture":
                    capturePath = value;
                    break;
                case "--fps":
                    if (!int.TryParse(value, out fps) || fps is < 5 or > 60)
                    {
                        throw new ArgumentException("--fps must be from 5 to 60.");
                    }
                    break;
                default:
                    throw new ArgumentException($"Unknown Godot option '{argument}'.");
            }
        }
        if (capturePath is not null && !smokeTest)
        {
            throw new ArgumentException("--capture requires --smoke-test.");
        }
        return new(config, smokeTest, capturePath)
        {
            Load = load, LoadPath = loadPath, Help = help, DumpMap = dump, FramesPerSecond = fps, ReducedMotion = reducedMotion,
        };
    }
}
