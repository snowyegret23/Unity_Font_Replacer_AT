using Spectre.Console;
using UnityFontReplacer.Models;
using UnityFontReplacer.SDF;

namespace UnityFontReplacer.CLI;

public static class MakeSdfCommand
{
    public const string DefaultCharsetArgument = "./CharList_3911.txt";

    public static async Task ExecuteAsync(
        string ttf, string atlasSize, int pointSize,
        int padding, string charset, string renderMode, string filterMode)
    {
        await Task.CompletedTask;

        // TTF 파일 로드
        var ttfPath = ResolveTtfPath(ttf);
        if (ttfPath == null)
        {
            AnsiConsole.MarkupLine($"[red]TTF not found: {Markup.Escape(ttf)}[/]");
            return;
        }

        var ttfData = File.ReadAllBytes(ttfPath);
        var fontName = Path.GetFileNameWithoutExtension(ttfPath);

        // 아틀라스 크기 파싱
        var (aw, ah) = ParseAtlasSize(atlasSize);

        // 캐릭터셋 로드
        int[] unicodes;
        try
        {
            unicodes = LoadCharset(charset);
        }
        catch (FileNotFoundException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return;
        }

        if (unicodes.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Charset is empty.[/]");
            return;
        }

        var normalizedRenderMode = (renderMode ?? "").Trim().ToLowerInvariant();
        bool rasterMode = normalizedRenderMode == "raster";
        bool sdfMode = normalizedRenderMode is "" or "sdf";
        if (!rasterMode && !sdfMode)
        {
            AnsiConsole.MarkupLine($"[red]Invalid render mode: {Markup.Escape(renderMode ?? "")} (sdf / raster)[/]");
            return;
        }

        var normalizedFilterMode = (filterMode ?? "").Trim().ToLowerInvariant();
        TextureFilterMode resolvedFilterMode;
        if (normalizedFilterMode is "" or "auto")
        {
            resolvedFilterMode = rasterMode
                ? TextureFilterMode.Point
                : TextureFilterMode.Bilinear;
        }
        else if (!TextureFilterModeParser.TryParse(filterMode, out resolvedFilterMode))
        {
            AnsiConsole.MarkupLine($"[red]Invalid filter mode: {Markup.Escape(filterMode ?? "")} (auto / point / bilinear / trilinear)[/]");
            return;
        }

        var displayMode = rasterMode ? "Raster" : "SDF";

        AnsiConsole.MarkupLine($"Font: [green]{Markup.Escape(fontName)}[/]");
        AnsiConsole.MarkupLine($"Atlas: [green]{aw}x{ah}[/], Padding: [green]{padding}[/]");
        AnsiConsole.MarkupLine($"Characters: [green]{unicodes.Length}[/], Mode: [green]{displayMode}[/], Filter: [green]{resolvedFilterMode}[/]");

        try
        {
            var result = SdfGenerator.Generate(ttfData, unicodes, aw, ah, padding, pointSize, rasterMode, resolvedFilterMode);

            var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "ASSETS");
            Directory.CreateDirectory(outputDir);
            SdfGenerator.SaveToFiles(result, outputDir, fontName);

            result.AtlasImage.Dispose();

            AnsiConsole.MarkupLine($"[green]SDF generation complete![/]");
            AnsiConsole.MarkupLine($"  Point size: [cyan]{result.FontAsset.FaceInfo.PointSize}[/]");
            AnsiConsole.MarkupLine($"  Glyphs: [cyan]{result.FontAsset.GlyphCount}[/]");
            AnsiConsole.MarkupLine($"  Output: [cyan]{Markup.Escape(outputDir)}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]SDF generation failed: {Markup.Escape(ex.Message)}[/]");
        }
    }

    private static string? ResolveTtfPath(string ttf)
    {
        if (File.Exists(ttf)) return ttf;

        var inCwd = Path.Combine(Directory.GetCurrentDirectory(), ttf);
        if (File.Exists(inCwd)) return inCwd;

        var inExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ttf);
        if (File.Exists(inExe)) return inExe;

        return null;
    }

    public static (int width, int height) ParseAtlasSize(string s)
    {
        var parts = s.Split(',');
        if (parts.Length == 2 &&
            int.TryParse(parts[0].Trim(), out int w) &&
            int.TryParse(parts[1].Trim(), out int h))
            return (w, h);

        return (4096, 4096);
    }

    public static int[] LoadCharset(string? charset)
    {
        var raw = string.IsNullOrWhiteSpace(charset)
            ? DefaultCharsetArgument
            : charset.Trim();

        foreach (var candidate in EnumerateCharsetCandidates(raw))
        {
            if (!File.Exists(candidate))
                continue;

            var text = File.ReadAllText(candidate);
            return TextToUnicodes(text);
        }

        bool looksLikePath =
            raw.Contains(Path.DirectorySeparatorChar) ||
            raw.Contains(Path.AltDirectorySeparatorChar) ||
            raw.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

        if (looksLikePath)
            throw new FileNotFoundException($"Charset file not found: {raw}");

        return TextToUnicodes(raw);
    }

    private static IEnumerable<string> EnumerateCharsetCandidates(string raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static IEnumerable<string> Expand(string root, string value)
        {
            yield return Path.Combine(root, value);

            var fileName = Path.GetFileName(value);
            if (!string.IsNullOrWhiteSpace(fileName) &&
                !fileName.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.Combine(root, fileName);
            }
        }

        if (Path.IsPathRooted(raw))
        {
            if (seen.Add(raw))
                yield return raw;
            yield break;
        }

        if (seen.Add(raw))
            yield return raw;

        foreach (var candidate in Expand(Directory.GetCurrentDirectory(), raw))
        {
            if (seen.Add(candidate))
                yield return candidate;
        }

        foreach (var candidate in Expand(AppDomain.CurrentDomain.BaseDirectory, raw))
        {
            if (seen.Add(candidate))
                yield return candidate;
        }
    }

    private static int[] TextToUnicodes(string text)
    {
        var set = new HashSet<int>();
        for (int i = 0; i < text.Length; i++)
        {
            int cp;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            else
            {
                cp = text[i];
            }

            // Python 원본은 텍스트 파일을 universal-newline 모드로 읽으므로
            // CR(\r)은 문자셋에 포함되지 않는다.
            if (cp == '\r')
                continue;

            // NUL과 서로게이트 제외
            if (cp > 0 && (cp < 0xD800 || cp > 0xDFFF))
                set.Add(cp);
        }

        return set.Order().ToArray();
    }
}
