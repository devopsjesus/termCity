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
    public bool DebugStarter { get; init; }
    public bool DebugSettlement { get; init; }
    public bool DebugSiege { get; init; }
    public bool SettlementSmokeTest { get; init; }
    public bool BuildingSmokeTest { get; init; }
    public bool MilestoneSmokeTest { get; init; }
    public bool WoodlotSmokeTest { get; init; }
    public bool PlacementSmokeTest { get; init; }
    public bool PerspectiveSmokeTest { get; init; }
    public bool PerspectivePerformanceTest { get; init; }
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
          --debug-starter   Debug only: well, woodlot, 4x10 homes beside the spur; start unpaused
          --debug-settlement Debug only: pre-drawn bailey; camera focused inside; start unpaused
          --debug-siege     Debug only: 100-settler bailey, goblin wave and map-edge siege controls
          -h, --help        Show this help
          --smoke-test     Run engine integration checks and exit
          --building-smoke-test Verify building sprites at every zoom and exit
          --milestone-smoke-test Verify reached-only unlocks and narrow milestone dialogs
          --woodlot-smoke-test Verify fenced firewood yard, picking and model previews
          --placement-smoke-test Verify map controls and clicked placement while building
          --perspective-smoke-test Verify tilted 3D camera, rotation and picking
          --perspective-performance-test Check zoom, pan and selection performance
          --settlement-smoke-test Verify enclosure tools, wall picking, garrison and combat rendering
          --capture <path> Save a PNG during a graphical smoke run
        """;

    public static GodotOptions Parse(string[] arguments)
    {
        var config = new GameConfig { Seed = Random.Shared.Next() };
        bool smokeTest = false;
        bool buildingSmokeTest = false;
        bool milestoneSmokeTest = false;
        bool woodlotSmokeTest = false;
        bool placementSmokeTest = false;
        bool perspectiveSmokeTest = false;
        bool perspectivePerformanceTest = false;
        string? capturePath = null;
        bool load = false, help = false, dump = false, reducedMotion = false;
        bool debugStarter = false;
        bool debugSettlement = false, debugSiege = false, settlementSmokeTest = false;
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
            if (argument == "--debug-starter")
            {
                debugStarter = true;
                continue;
            }
            if (argument is "--debug-settlement" or "--debug-siege")
            {
                debugSettlement |= argument == "--debug-settlement";
                debugSiege |= argument == "--debug-siege";
                continue;
            }
            if (argument == "--settlement-smoke-test")
            {
                smokeTest = true;
                settlementSmokeTest = true;
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
            if (argument == "--building-smoke-test")
            {
                smokeTest = true;
                buildingSmokeTest = true;
                continue;
            }
            if (argument == "--woodlot-smoke-test")
            {
                smokeTest = true;
                buildingSmokeTest = true;
                woodlotSmokeTest = true;
                continue;
            }
            if (argument == "--placement-smoke-test")
            {
                smokeTest = true;
                buildingSmokeTest = true;
                placementSmokeTest = true;
                continue;
            }
            if (argument == "--milestone-smoke-test")
            {
                smokeTest = true;
                buildingSmokeTest = true;
                milestoneSmokeTest = true;
                continue;
            }
            if (argument == "--perspective-smoke-test")
            {
                smokeTest = true;
                buildingSmokeTest = true;
                perspectiveSmokeTest = true;
                continue;
            }
            if (argument == "--perspective-performance-test")
            {
                smokeTest = true;
                buildingSmokeTest = true;
                perspectivePerformanceTest = true;
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
        if (milestoneSmokeTest && (load || config.Scenario != CityScenario.Random ||
            woodlotSmokeTest || perspectiveSmokeTest || perspectivePerformanceTest))
            throw new ArgumentException("--milestone-smoke-test requires a new random city and cannot be combined with other model smoke modes.");
        if (settlementSmokeTest && (load || config.Scenario != CityScenario.Random ||
            buildingSmokeTest || perspectivePerformanceTest))
            throw new ArgumentException("--settlement-smoke-test requires a new random city and cannot be combined with other smoke modes.");
        if ((debugStarter || debugSettlement || debugSiege) && !help)
        {
            if ((debugStarter ? 1 : 0) + (debugSettlement ? 1 : 0) + (debugSiege ? 1 : 0) > 1)
                throw new ArgumentException("Choose only one Debug starter configuration.");
            if (!CityGame.DebugToolsAvailable)
                throw new ArgumentException("Debug starter configurations require a Debug build.");
            if (load || config.Scenario != CityScenario.Random)
                throw new ArgumentException("Debug starter configurations require a new random city, not a saved city or populated scenario.");
            if (smokeTest || dump)
                throw new ArgumentException("Debug starter configurations cannot be combined with smoke tests or --dump-map.");
        }
        return new(config, smokeTest, capturePath)
        {
            Load = load, LoadPath = loadPath, Help = help, DumpMap = dump, FramesPerSecond = fps, ReducedMotion = reducedMotion,
            BuildingSmokeTest = buildingSmokeTest,
            MilestoneSmokeTest = milestoneSmokeTest,
            WoodlotSmokeTest = woodlotSmokeTest,
            PlacementSmokeTest = placementSmokeTest,
            PerspectiveSmokeTest = perspectiveSmokeTest,
            PerspectivePerformanceTest = perspectivePerformanceTest,
            DebugStarter = debugStarter,
            DebugSettlement = debugSettlement, DebugSiege = debugSiege, SettlementSmokeTest = settlementSmokeTest,
        };
    }
}
