using System.Text.Json;
using System.Text.Json.Serialization;
using RiskReward.Core;

namespace RiskReward.Infrastructure;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new HistoricalPricePointConverter() }
    };
}

public sealed class HistoricalPricePointConverter : JsonConverter<HistoricalPricePoint>
{
    public override HistoricalPricePoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("A history point must be a two-value array.");
        if (!reader.Read() || reader.TokenType != JsonTokenType.String || !reader.TryGetDateTimeOffset(out var timestamp))
            throw new JsonException("A history point requires an ISO-8601 timestamp.");
        if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetDecimal(out var price))
            throw new JsonException("A history point requires a numeric price.");
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
            throw new JsonException("A history point must contain exactly two values.");
        return new HistoricalPricePoint(timestamp, price);
    }

    public override void Write(Utf8JsonWriter writer, HistoricalPricePoint value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteStringValue(value.Timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
        writer.WriteNumberValue(value.Price);
        writer.WriteEndArray();
    }
}
