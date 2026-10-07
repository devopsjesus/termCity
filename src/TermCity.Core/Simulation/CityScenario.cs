using System.Text.Json;
using System.Text.Json.Serialization;

namespace TermCity.Core.Simulation;

/// <summary>
/// The pre-built historical cities. Their coastlines and hills were first drawn for modern cities, so each old name is
/// kept as a reading of a medieval stand-in: the peninsula is Constantinople, the sprawling bay is Naples, the harbour
/// with the burnable hills is Genoa, the cold lakeside freight town is Lubeck and the flood-prone river town is York.
/// </summary>
public enum CityScenario
{
    Random,
    Constantinople,
    Naples,
    Genoa,
    Lubeck,
    York,
}

/// <summary>Reads scenario names from old saves (from before the medieval setting) as their medieval stand-ins.</summary>
public sealed class CityScenarioConverter : JsonConverter<CityScenario>
{
    private static readonly Dictionary<string, CityScenario> Legacy = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SanFrancisco"] = CityScenario.Constantinople,
        ["LosAngeles"] = CityScenario.Naples,
        ["SanDiego"] = CityScenario.Genoa,
        ["Chicago"] = CityScenario.Lubeck,
        ["StLouis"] = CityScenario.York,
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
