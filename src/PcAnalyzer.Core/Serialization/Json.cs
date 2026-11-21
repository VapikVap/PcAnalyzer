using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcAnalyzer.Core.Serialization;

public static class Json
{
    /// <summary>Compact options for storage: nulls are omitted (null = "not available").</summary>
    public static readonly JsonSerializerOptions Compact = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static readonly JsonSerializerOptions Pretty = new(Compact) { WriteIndented = true };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Compact);
    public static T? Deserialize<T>(string? json) => string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize<T>(json, Compact);
}
