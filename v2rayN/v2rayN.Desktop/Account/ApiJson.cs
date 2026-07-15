using System.Text.Json;
using System.Text.Json.Serialization;

namespace v2rayN.Desktop.Account;

/// <summary>
/// Shared System.Text.Json options for the Departament backend DTOs. Port of V2rayNG auth/ApiGson.kt.
///
/// The backend legitimately omits or nulls string fields — a Telegram-only user has no `email`, a
/// fresh account has no `currency`, etc. The null-tolerant string converter maps any JSON null (or a
/// bool/number token) to a non-null string so a partial payload never throws. camelCase naming +
/// case-insensitive matching mirror Gson's field mapping; nulls are omitted on write like Gson.
/// </summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // Case-SENSITIVE (Gson parity): distinct alternates like `tariffId` vs `tariffID` must not
            // collide, and the backend sends exact camelCase keys the CamelCase policy already matches.
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        options.Converters.Add(new NullTolerantStringConverter());
        return options;
    }
}

/// <summary>
/// Maps any JSON null (or a bool/number scalar) to a non-null string, mirroring ApiGson's null-tolerant
/// String adapter so every DTO string is non-null regardless of what the backend sends.
/// </summary>
public sealed class NullTolerantStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return string.Empty;
            case JsonTokenType.String:
                return reader.GetString() ?? string.Empty;
            case JsonTokenType.True:
                return "true";
            case JsonTokenType.False:
                return "false";
            case JsonTokenType.Number:
                // Preserve the raw numeric text so an id/amount typed as a JSON number still maps.
                return reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
            default:
                reader.Skip();
                return string.Empty;
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
