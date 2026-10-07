using System.Text.Json;
using System.Text.Json.Serialization;

namespace TermCity.Core.Simulation;

/// <summary>The pre-built cities.</summary>
public enum CityScenario
{
    Random,
    SanFrancisco,
    LosAngeles,
    SanDiego,
    Chicago,
    StLouis,
}

/// <summary>Reads the medieval stand-in names that saves from the medieval setting's first versions used.</summary>
public sealed class CityScenarioConverter : JsonConverter<CityScenario>
{
    private static readonly Dictionary<string, CityScenario> Legacy = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Constantinople"] = CityScenario.SanFrancisco,
        ["Naples"] = CityScenario.LosAngeles,
        ["Genoa"] = CityScenario.SanDiego,
        ["Lubeck"] = CityScenario.Chicago,
        ["York"] = CityScenario.StLouis,
    };

    public override CityScenario Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return (CityScenario)reader.GetInt32();
        }

        string text = reader.GetString() ?? string.Empty;
        if (Legacy.TryGetValue(text, out var legacy))
        {
            return legacy;
        }

        return Enum.TryParse<CityScenario>(text, ignoreCase: true, out var scenario)
            ? scenario
            : throw new JsonException($"'{text}' is not a known city scenario.");
    }

    public override void Write(Utf8JsonWriter writer, CityScenario value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
