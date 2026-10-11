using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoadTester.Core.Serialization;

/// <summary>JSON settings shared by everything that persists scenarios and results.</summary>
public static class LoadTesterJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("JSON içeriği boş.");
}
