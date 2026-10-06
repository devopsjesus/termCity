using System.Globalization;
using TermCity.Core.Simulation;

namespace TermCity.GodotApp;

public sealed record PrototypeOptions(GameConfig Config, bool SmokeTest, string? CapturePath)
{
    public static PrototypeOptions Parse(string[] arguments)
    {
        var config = new GameConfig { Seed = Random.Shared.Next() };
        bool smokeTest = false;
        string? capturePath = null;
        for (int i = 0; i < arguments.Length; i++)
        {
            string argument = arguments[i];
            if (argument == "--smoke-test")
            {
                smokeTest = true;
                continue;
            }

            if (argument is not ("--seed" or "--size" or "--capture"))
            {
                throw new ArgumentException($"Unknown prototype option '{argument}'.");
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
                    config = config with { MapWidth = size.Width, MapHeight = size.Height };
                    break;
                case "--capture":
                    capturePath = value;
                    break;
                default:
                    throw new ArgumentException($"Unknown prototype option '{argument}'.");
            }
        }
        if (capturePath is not null && !smokeTest)
        {
            throw new ArgumentException("--capture requires --smoke-test.");
        }
        return new(config, smokeTest, capturePath);
    }
}
