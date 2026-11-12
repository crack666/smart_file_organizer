using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartFileOrganizer.Cli.Contract;

/// <summary>
/// Serialisation rules of the machine contract: <c>snake_case</c> properties, enums as <c>snake_case</c>
/// strings, timestamps in ISO 8601 UTC, non-ASCII characters (paths) left readable.
/// Every command result is serialised through here; never call <see cref="JsonSerializer"/> with other options.
/// </summary>
public static class CliJson
{
    /// <summary>Version of the document shapes. Bump when a field is removed or changes meaning;
    /// adding a field does not require a bump.</summary>
    public const int SchemaVersion = 1;

    public static readonly JsonSerializerOptions Document = Create(indented: true);
    public static readonly JsonSerializerOptions Line = Create(indented: false);

    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? Document : Line);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        options.Converters.Add(new UtcDateTimeConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>The database stores UTC without a kind marker; write every timestamp with a trailing <c>Z</c>.</summary>
    private sealed class UtcDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDateTime().ToUniversalTime();

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            var utc = value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            };
            writer.WriteStringValue(utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>
/// The single document a command writes to stdout with <c>--json</c>.
/// <c>result</c> is the command-specific payload (may be present on failure, for example the paused job);
/// <c>error</c> is null on success.
/// </summary>
public sealed record CliEnvelope(
    int SchemaVersion,
    string Command,
    bool Ok,
    int ExitCode,
    object? Result,
    CliError? Error);

public sealed record CliError(string Code, string Message);
