using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityFontReplacer.Models;

public class FontMapping
{
    [JsonPropertyName("game_path")]
    public string GamePath { get; set; } = "";

    [JsonPropertyName("unity_version")]
    public string EngineVersion { get; set; } = "";

    [JsonPropertyName("fonts")]
    public Dictionary<string, FontEntry> Fonts { get; set; } = new();

    public static FontMapping Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Font mapping must be a JSON object.");
        if (root.TryGetProperty("fonts", out var fonts))
        {
            if (fonts.ValueKind != JsonValueKind.Object)
                throw new JsonException("'fonts' must be a JSON object.");
            return root.Deserialize<FontMapping>()!;
        }
        return new FontMapping { Fonts = root.Deserialize<Dictionary<string, FontEntry>>()! };
    }

    public static FontMapping FromScanResult(ScanResult result, string gamePath)
    {
        var mapping = new FontMapping
        {
            GamePath = gamePath,
            EngineVersion = result.EngineVersion ?? "",
        };

        foreach (var entry in result.Entries)
        {
            var key = $"{entry.File}|{entry.AssetsName}|{entry.Name}|{entry.Type}|{entry.PathId}";
            mapping.Fonts[key] = entry;
        }

        return mapping;
    }
}
