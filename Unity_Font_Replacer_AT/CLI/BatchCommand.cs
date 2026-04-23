using System.CommandLine;
using Spectre.Console;
using UnityFontReplacer.Core;
using UnityFontReplacer.Models;

namespace UnityFontReplacer.CLI;

public static class BatchCommand
{
    private sealed class BuiltinPreset
    {
        public required string Alias { get; init; }
        public bool ForceRaster { get; init; }
    }

    private sealed class BatchFontSpec
    {
        public required string SdfSource { get; init; }
        public string? TtfPath { get; init; }
        public bool ForceRaster { get; init; }
    }

    public static Command Build()
    {
        var batchCommand = new Command("batch", "Batch replace all fonts using a built-in preset or custom font source");
        var gamePathOption = CommandLineOptions.CreateGamePathOption();

        var fontOption = CommandLineOptions.RequiredOption<string>(
            "--font",
            "Builtin font name, TTF/OTF path/name, or path to font directory",
            "-f");

        var ps5Option = CommandLineOptions.OptionalOption<bool>("--ps5-swizzle", "Handle PS5 texture swizzle");
        var outputOption = CommandLineOptions.OptionalOption<string?>("--output-only", "Write modified files to this directory instead of in-place");
        var sdfOnlyOption = CommandLineOptions.OptionalOption<bool>("--sdfonly", "Replace SDF fonts only");
        var ttfOnlyOption = CommandLineOptions.OptionalOption<bool>("--ttfonly", "Replace TTF fonts only");
        var forceRasterOption = CommandLineOptions.OptionalOption<bool>("--force-raster", "Force raster behavior for SDF replacements", "--raster");

        batchCommand.Add(gamePathOption);
        batchCommand.Add(fontOption);
        batchCommand.Add(ps5Option);
        batchCommand.Add(outputOption);
        batchCommand.Add(sdfOnlyOption);
        batchCommand.Add(ttfOnlyOption);
        batchCommand.Add(forceRasterOption);

        batchCommand.SetAction(async parseResult =>
        {
            await ExecuteAsync(
                parseResult.GetRequiredValue(gamePathOption),
                parseResult.GetRequiredValue(fontOption),
                parseResult.GetValue(ps5Option),
                parseResult.GetValue(outputOption),
                parseResult.GetValue(sdfOnlyOption),
                parseResult.GetValue(ttfOnlyOption),
                parseResult.GetValue(forceRasterOption));
        });

        return batchCommand;
    }

    public static async Task ExecuteAsync(
        string gamePath, string fontName, bool ps5Swizzle,
        string? outputDir, bool sdfOnly, bool ttfOnly, bool forceRaster)
    {
        await Task.CompletedTask;

        var resolved = GamePathResolver.Resolve(gamePath);
        if (resolved == null)
        {
            AnsiConsole.MarkupLine($"[red]{Strings.Get("err_gamepath_not_found", gamePath)}[/]");
            return;
        }
        try
        {
            Il2CppManagedGenerator.EnsureManagedFolder(resolved);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return;
        }

        var fontSpec = ResolveBatchFontSpec(fontName, forceRaster);
        if (fontSpec == null)
        {
            AnsiConsole.MarkupLine($"[red]Font not found: {Markup.Escape(fontName)}[/]");
            return;
        }

        using var ctx = new AssetsContext(resolved.DataPath, resolved.ManagedPath);

        var version = ctx.DetectUnityVersion();
        if (version != null)
        {
            ctx.LoadClassDatabase(version);
            ctx.SetupMonoCecil();
        }

        // 1. 스캔
        var scanner = new FontScanner(ctx);
        var scanResult = scanner.ScanAll(resolved.AssetFiles, ps5Swizzle);

        // 2. 매핑 생성 - 모든 폰트를 해당 폰트로 교체
        var mapping = FontMapping.FromScanResult(scanResult, resolved.GamePath);
        mapping.UnityVersion = version ?? "";

        int assignCount = 0;
        foreach (var entry in mapping.Fonts.Values)
        {
            if (sdfOnly && entry.Type == FontType.TTF) continue;
            if (ttfOnly && entry.Type == FontType.SDF) continue;

            if (entry.Type == FontType.SDF)
            {
                entry.ReplaceTo = fontSpec.SdfSource;
                entry.ForceRaster = fontSpec.ForceRaster ? "true" : null;
                assignCount++;
                continue;
            }

            if (fontSpec.TtfPath == null)
                continue;

            entry.ReplaceTo = fontSpec.TtfPath;
            assignCount++;
        }

        AnsiConsole.MarkupLine($"Replacement targets: [green]{assignCount}[/]");

        if (assignCount == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No fonts to replace[/]");
            return;
        }

        // 3. CAB 매핑 수집 (크로스 번들 텍스처 교체용)
        var replacer = new FontReplacer(ctx);
        replacer.BuildCabMappings(resolved.AssetFiles);

        // 4. 교체 실행
        var replaced = replacer.ReplaceFromMapping(mapping, outputDir);

        AnsiConsole.MarkupLine($"[green]Batch complete: {replaced} font(s) replaced[/]");
    }

    private static BatchFontSpec? ResolveBatchFontSpec(string fontName, bool forceRaster)
    {
        if (TryGetBuiltinPreset(fontName, out var preset))
        {
            var ttfPath = ResolveBuiltinTtfPath(preset.Alias);
            if (ttfPath == null)
                return null;

            return new BatchFontSpec
            {
                SdfSource = ttfPath,
                TtfPath = ttfPath,
                ForceRaster = forceRaster || preset.ForceRaster,
            };
        }

        if (Directory.Exists(fontName))
        {
            var ttfPath = Directory
                .EnumerateFiles(fontName, "*.*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path =>
                    path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase));
            return new BatchFontSpec
            {
                SdfSource = ttfPath ?? fontName,
                TtfPath = ttfPath,
                ForceRaster = forceRaster,
            };
        }

        if (File.Exists(fontName))
        {
            if (fontName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                fontName.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            {
                return new BatchFontSpec
                {
                    SdfSource = fontName,
                    TtfPath = fontName,
                    ForceRaster = forceRaster,
                };
            }

            if (fontName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(fontName)!;
                var ttfPath = Directory
                    .EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(path =>
                        path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase));
                return new BatchFontSpec
                {
                    SdfSource = fontName,
                    TtfPath = ttfPath,
                    ForceRaster = forceRaster,
                };
            }
        }

        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(exeDir, "ASSETS", fontName),
            Path.Combine(exeDir, fontName),
            Path.Combine(Directory.GetCurrentDirectory(), "ASSETS", fontName),
            Path.Combine(Directory.GetCurrentDirectory(), fontName),
        };

        foreach (var dir in candidates)
        {
            if (!Directory.Exists(dir))
                continue;

            var jsonPath = Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (jsonPath == null)
                continue;

            var ttfPath = Directory
                .EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path =>
                    path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase));

            return new BatchFontSpec
            {
                SdfSource = ttfPath ?? dir,
                TtfPath = ttfPath,
                ForceRaster = forceRaster,
            };
        }

        var resolvedTtfPath = TtfFontHandler.ResolveTtfPath(fontName);
        if (resolvedTtfPath != null)
        {
            return new BatchFontSpec
            {
                SdfSource = resolvedTtfPath,
                TtfPath = resolvedTtfPath,
                ForceRaster = forceRaster,
            };
        }

        return null;
    }

    private static bool TryGetBuiltinPreset(string input, out BuiltinPreset preset)
    {
        preset = null!;
        var normalized = input.Trim();
        if (normalized.Equals("mulmaru", StringComparison.OrdinalIgnoreCase))
        {
            preset = new BuiltinPreset
            {
                Alias = "Mulmaru",
                ForceRaster = true,
            };
            return true;
        }

        if (normalized.Equals("nanumgothic", StringComparison.OrdinalIgnoreCase))
        {
            preset = new BuiltinPreset
            {
                Alias = "NanumGothic",
                ForceRaster = false,
            };
            return true;
        }

        return false;
    }

    private static string? ResolveBuiltinTtfPath(string alias)
    {
        foreach (var root in EnumerateKrAssetsRoots())
        {
            foreach (var ext in new[] { ".ttf", ".otf" })
            {
                var path = Path.Combine(root, alias + ext);
                if (File.Exists(path))
                    return path;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateKrAssetsRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[]
                 {
                     Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ASSETS"),
                     Path.Combine(Directory.GetCurrentDirectory(), "ASSETS"),
                 })
        {
            if (Directory.Exists(root) && seen.Add(root))
                yield return root;
        }
    }
}
