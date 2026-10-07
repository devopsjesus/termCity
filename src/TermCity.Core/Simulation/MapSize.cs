using System.Globalization;

namespace TermCity.Core.Simulation;

/// <summary>Map dimensions in cells, with named presets for the <c>--size</c> option.</summary>
public readonly record struct MapSize(int Width, int Height)
{
    public CityScenario Scenario { get; init; }
    public bool Constantinople => Scenario == CityScenario.Constantinople;
    public const int MinWidth = 80;
    public const int MinHeight = 24;
    public const int MaxWidth = 640;
    public const int MaxHeight = 384;

    /// <summary>Random map sizes and large, prepopulated regional city scenarios.</summary>
    public static readonly IReadOnlyDictionary<string, MapSize> Presets = new Dictionary<string, MapSize>(StringComparer.OrdinalIgnoreCase)
    {
        ["small"] = new(160, 96),
        ["medium"] = new(320, 192),
        ["large"] = new(640, 384),
        ["CON"] = new(640, 384) { Scenario = CityScenario.Constantinople },
        ["NAP"] = new(640, 384) { Scenario = CityScenario.Naples },
        ["GEN"] = new(640, 384) { Scenario = CityScenario.Genoa },
        ["LUB"] = new(640, 384) { Scenario = CityScenario.Lubeck },
        ["YRK"] = new(640, 384) { Scenario = CityScenario.York },
    };

    public static string Describe() =>
        string.Join(", ", Presets.Select(p => $"{p.Key} ({p.Value.Width}x{p.Value.Height})")) +
        $", or WIDTHxHEIGHT between {MinWidth}x{MinHeight} and {MaxWidth}x{MaxHeight}";

    /// <summary>Parses a preset name (<c>large</c>) or dimensions (<c>640x384</c>).</summary>
    public static bool TryParse(string? text, out MapSize size, out string error)
    {
        size = default;
        error = string.Empty;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            error = "No size given. Use " + Describe() + ".";
            return false;
        }

        if (Presets.TryGetValue(text, out size))
        {
            return true;
        }

        string[] parts = text.Split(['x', 'X'], StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int width) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height))
        {
            error = $"'{text}' is not a valid size. Use " + Describe() + ".";
            return false;
        }

        if (width < MinWidth || height < MinHeight || width > MaxWidth || height > MaxHeight)
        {
            error = $"Size {width}x{height} is outside the supported range {MinWidth}x{MinHeight} to {MaxWidth}x{MaxHeight}.";
            return false;
        }

        size = new MapSize(width, height);
        return true;
    }
}
