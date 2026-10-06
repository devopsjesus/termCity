using System.Text;
using TermCity.App;
using TermCity.Core.Rendering;
using TermCity.Core.Session;
using TermCity.Core.Simulation;

return Cli.Run(args);

internal static class Cli
{
    private const string Usage = """
        TermCity - a terminal city builder

        Usage: termcity [options]

          --seed <n>        Generate the map from this seed (default: random)
          --load [file]     Load a saved game (default: the quick-save file)
          --size <size>     Map size: small (160x96, default), medium (320x192), large (640x384),
                            or WIDTHxHEIGHT (80x24 up to 640x384)
          --fps <n>         Redraws per second while scrolling, 5 to 60 (default: 30); lower it if the screen lags behind
          --dump-map        Print the generated map as text and exit
          -h, --help        Show this help
        """;

    public static int Run(string[] args)
    {
        int? seed = null;
        var size = new MapSize(new GameConfig().MapWidth, new GameConfig().MapHeight);
        string? loadPath = null;
        bool load = false, dump = false;
        int fps = GameApp.DefaultFramesPerSecond;

        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--seed": seed = int.Parse(Next(args, ref i)); break;
                    case "--size":
                        if (!MapSize.TryParse(Next(args, ref i), out size, out string sizeError))
                        {
                            throw new ArgumentException(sizeError);
                        }

                        break;
                    case "--fps":
                        fps = int.Parse(Next(args, ref i));
                        if (fps is < GameApp.MinFramesPerSecond or > GameApp.MaxFramesPerSecond)
                        {
                            throw new ArgumentException($"--fps must be from {GameApp.MinFramesPerSecond} to {GameApp.MaxFramesPerSecond}.");
                        }

                        break;
                    case "--dump-map": dump = true; break;
                    case "--load":
                        load = true;
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        {
                            loadPath = args[++i];
                        }

                        break;
                    case "-h" or "--help" or "/?":
                        Console.WriteLine(Usage);
                        return 0;
                    default:
                        throw new ArgumentException($"Unknown option '{args[i]}'.");
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var config = new GameConfig { Seed = seed ?? Random.Shared.Next(), MapWidth = size.Width, MapHeight = size.Height };

        GameSession session;
        if (load)
        {
            session = new GameSession(CityGame.New(config));
            if (!session.LoadFrom(loadPath ?? session.SavePath))
            {
                Console.Error.WriteLine(session.Message);
                return 1;
            }
        }
        else
        {
            session = new GameSession(CityGame.New(config), showGuide: !dump);
        }

        if (dump)
        {
            Console.OutputEncoding = Encoding.UTF8;
            DumpMap(session.Game);
            return 0;
        }

        if (Console.IsOutputRedirected || Console.IsInputRedirected)
        {
            Console.Error.WriteLine("TermCity needs an interactive terminal.");
            return 1;
        }

        new GameApp(session, fps).Run();
        return 0;
    }

    private static string Next(string[] args, ref int i) =>
        i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Option '{args[i]}' needs a value.");

    private static void DumpMap(CityGame game)
    {
        Console.WriteLine($"Seed {game.Config.Seed}, {game.Map.Width}x{game.Map.Height}");
        var sb = new StringBuilder();
        for (int y = 0; y < game.Map.Height; y++)
        {
            for (int x = 0; x < game.Map.Width; x++)
            {
                sb.Append(CellRenderer.Render(game, x, y).Glyph);
            }

            sb.AppendLine();
        }

        Console.Write(sb);
    }
}
