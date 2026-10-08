using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inventory.Contracts
{
    /// <summary>Gemeinsame JSON-Einstellungen, damit Server und Runner dasselbe Format lesen und schreiben.</summary>
    public static class JobJson
    {
        private static readonly JsonSerializerOptions Options = Create(indented: false);
        private static readonly JsonSerializerOptions IndentedOptions = Create(indented: true);

        public static string Serialize<T>(T value, bool indented = false) =>
            JsonSerializer.Serialize(value, indented ? IndentedOptions : Options);

        public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

        private static JsonSerializerOptions Create(bool indented)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = indented,
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
    }
}
