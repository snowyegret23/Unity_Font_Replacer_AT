using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityFontReplacer.Models;

public enum FontType
{
    TTF,
    SDF,
}

public enum TmpSchemaVersion
{
    Unknown,
    Old,
    New,
    Hybrid,
}

public class FontEntry
{
    [JsonPropertyName("File")]
    public string File { get; set; } = "";

    [JsonPropertyName("assets_name")]
    public string AssetsName { get; set; } = "";

    [JsonPropertyName("Path_ID")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long PathId { get; set; }

    [JsonPropertyName("Type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FontType Type { get; set; }

    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("Replace_to")]
    public string ReplaceTo { get; set; } = "";

    [JsonPropertyName("schema")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TmpSchemaVersion Schema { get; set; }

    [JsonPropertyName("glyph_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int GlyphCount { get; set; }

    [JsonPropertyName("atlas_path_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long AtlasPathId { get; set; }

    [JsonPropertyName("atlas_padding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int AtlasPadding { get; set; }

    [JsonPropertyName("point_size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int PointSize { get; set; } = 0;

    [JsonPropertyName("force_raster")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonConverter(typeof(FlexibleFlagConverter))]
    public string? ForceRaster { get; set; }

    [JsonPropertyName("swizzle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonConverter(typeof(FlexibleFlagConverter))]
    public string? Swizzle { get; set; }

    [JsonPropertyName("process_swizzle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonConverter(typeof(FlexibleFlagConverter))]
    public string? ProcessSwizzle { get; set; }
}


internal sealed class FlexibleFlagConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, System.Type type, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.True => "True",
            JsonTokenType.False => "False",
            JsonTokenType.Number when reader.TryGetInt32(out var value) && (value == 0 || value == 1)
                => value == 1 ? "True" : "False",
            _ => throw new JsonException("Expected a boolean, 0/1, or a string."),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
