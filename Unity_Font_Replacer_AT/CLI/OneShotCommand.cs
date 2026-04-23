using System.CommandLine;
using Spectre.Console;
using UnityFontReplacer.Core;
using UnityFontReplacer.Models;
using UnityFontReplacer.SDF;

namespace UnityFontReplacer.CLI;

public static class OneShotCommand
{
    private const int DefaultPadding = 7;
    private const string DefaultAtlasSize = "4096,4096";
    private const string DefaultFilterMode = "auto";

    public static Command Build()
    {
        var oneShotCommand = new Command("oneshot", Strings.Get("cmd_oneshot"));
        var gamePathOption = CommandLineOptions.CreateGamePathOption();

        var fontOption = CommandLineOptions.RequiredOption<string>(
            "--font",
            "TTF/OTF file path or resolvable font name",
            "-f");

        var ps5Option = CommandLineOptions.OptionalOption<bool>("--ps5-swizzle", "Handle PS5 texture swizzle");
        var outputOption = CommandLineOptions.OptionalOption<string?>("--output-only", "Write modified files to this directory instead of in-place");
        var sdfOnlyOption = CommandLineOptions.OptionalOption<bool>("--sdfonly", "Replace SDF fonts only");
        var ttfOnlyOption = CommandLineOptions.OptionalOption<bool>("--ttfonly", "Replace TTF fonts only");
        var forceRasterOption = CommandLineOptions.OptionalOption<bool>("--force-raster", "Generate raster atlases for SDF replacements", "--raster");
        var sdfOption = CommandLineOptions.OptionalOption<bool>("--sdf", "Generate SDF atlases for SDF replacements");
        var atlasSizeOption = CommandLineOptions.OptionalOption("--atlas-size", DefaultAtlasSize, "Atlas size (W,H)");
        var pointSizeOption = CommandLineOptions.OptionalOption("--point-size", 0, "Point size (0=auto)");
        var charsetOption = CommandLineOptions.OptionalOption("--charset", MakeSdfCommand.DefaultCharsetArgument, "Charset file or literal");
        var filterModeOption = CommandLineOptions.OptionalOption("--filter-mode", DefaultFilterMode, "auto / point / bilinear / trilinear");

        oneShotCommand.Add(gamePathOption);
        oneShotCommand.Add(fontOption);
        oneShotCommand.Add(ps5Option);
        oneShotCommand.Add(outputOption);
        oneShotCommand.Add(sdfOnlyOption);
        oneShotCommand.Add(ttfOnlyOption);
        oneShotCommand.Add(forceRasterOption);
        oneShotCommand.Add(sdfOption);
        oneShotCommand.Add(atlasSizeOption);
        oneShotCommand.Add(pointSizeOption);
        oneShotCommand.Add(charsetOption);
        oneShotCommand.Add(filterModeOption);

        oneShotCommand.SetAction(async parseResult =>
        {
            await ExecuteAsync(
                parseResult.GetRequiredValue(gamePathOption),
                parseResult.GetRequiredValue(fontOption),
                parseResult.GetValue(ps5Option),
                parseResult.GetValue(outputOption),
                parseResult.GetValue(sdfOnlyOption),
                parseResult.GetValue(ttfOnlyOption),
                parseResult.GetValue(forceRasterOption),
                parseResult.GetValue(sdfOption),
                parseResult.GetValue(atlasSizeOption) ?? DefaultAtlasSize,
                parseResult.GetValue(pointSizeOption),
                parseResult.GetValue(charsetOption) ?? MakeSdfCommand.DefaultCharsetArgument,
                parseResult.GetValue(filterModeOption) ?? DefaultFilterMode);
        });

        return oneShotCommand;
    }

    public static async Task ExecuteAsync(
        string gamePath,
        string fontName,
        bool ps5Swizzle,
        string? outputDir,
        bool sdfOnly,
        bool ttfOnly,
        bool forceRaster,
        bool sdf,
        string atlasSize,
        int pointSize,
        string charset,
        string filterMode)
    {
        await Task.CompletedTask;

        if (forceRaster && sdf)
        {
            AnsiConsole.MarkupLine("[red]--force-raster/--raster and --sdf cannot be used together.[/]");
            return;
        }

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

        var ttfPath = TtfFontHandler.ResolveTtfPath(fontName);
        if (ttfPath == null)
        {
            AnsiConsole.MarkupLine($"[red]TTF not found: {Markup.Escape(fontName)}[/]");
            return;
        }

        byte[] ttfData;
        try
        {
            ttfData = File.ReadAllBytes(ttfPath);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to read TTF: {Markup.Escape(ex.Message)}[/]");
            return;
        }

        int[] unicodes = [];
        if (!ttfOnly)
        {
            try
            {
                unicodes = MakeSdfCommand.LoadCharset(charset);
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
        }

        var normalizedFilterMode = (filterMode ?? "").Trim().ToLowerInvariant();
        TextureFilterMode resolvedFilterMode;
        if (normalizedFilterMode is "" or "auto")
        {
            resolvedFilterMode = forceRaster
                ? TextureFilterMode.Point
                : TextureFilterMode.Bilinear;
        }
        else if (!TextureFilterModeParser.TryParse(filterMode, out resolvedFilterMode))
        {
            AnsiConsole.MarkupLine($"[red]Invalid filter mode: {Markup.Escape(filterMode ?? "")} (auto / point / bilinear / trilinear)[/]");
            return;
        }

        var (atlasWidth, atlasHeight) = MakeSdfCommand.ParseAtlasSize(atlasSize);
        bool rasterMode = forceRaster;
        var displayMode = rasterMode ? "Raster" : "SDF";

        using var ctx = new AssetsContext(resolved.DataPath, resolved.ManagedPath);

        var version = ctx.DetectUnityVersion();
        if (version != null)
        {
            ctx.LoadClassDatabase(version);
            ctx.SetupMonoCecil();
        }

        var scanner = new FontScanner(ctx);
        var scanResult = scanner.ScanAll(resolved.AssetFiles, ps5Swizzle);

        var mapping = FontMapping.FromScanResult(scanResult, resolved.GamePath);
        mapping.UnityVersion = version ?? "";

        string? tempRoot = null;

        try
        {
            Dictionary<int, string> generatedSdfDirs = new();
            if (!ttfOnly)
            {
                var paddings = mapping.Fonts.Values
                    .Where(entry => entry.Type == FontType.SDF)
                    .Select(entry => NormalizePadding(entry.AtlasPadding))
                    .Distinct()
                    .OrderBy(value => value)
                    .ToList();

                if (paddings.Count > 0)
                {
                    tempRoot = Path.Combine(
                        Path.GetTempPath(),
                        "UnityFontReplacer_Oneshot",
                        Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempRoot);

                    var fontBaseName = Path.GetFileNameWithoutExtension(ttfPath);
                    foreach (var padding in paddings)
                    {
                        var paddingDir = Path.Combine(tempRoot, $"padding_{padding}");
                        Directory.CreateDirectory(paddingDir);

                        AnsiConsole.MarkupLine($"[cyan]Generating {displayMode}: padding {padding}[/]");
                        var result = SdfGenerator.Generate(
                            ttfData,
                            unicodes,
                            atlasWidth: atlasWidth,
                            atlasHeight: atlasHeight,
                            padding: padding,
                            pointSize: pointSize,
                            rasterMode: rasterMode,
                            filterMode: resolvedFilterMode);

                        try
                        {
                            SdfGenerator.SaveToFiles(result, paddingDir, fontBaseName);
                        }
                        finally
                        {
                            result.AtlasImage.Dispose();
                        }

                        generatedSdfDirs[padding] = paddingDir;
                    }
                }
            }

            int assignCount = 0;
            foreach (var entry in mapping.Fonts.Values)
            {
                if (sdfOnly && entry.Type == FontType.TTF)
                    continue;
                if (ttfOnly && entry.Type == FontType.SDF)
                    continue;

                if (entry.Type == FontType.TTF)
                {
                    entry.ReplaceTo = ttfPath;
                    assignCount++;
                    continue;
                }

                var padding = NormalizePadding(entry.AtlasPadding);
                if (!generatedSdfDirs.TryGetValue(padding, out var sdfDir))
                    continue;

                entry.ReplaceTo = sdfDir;
                assignCount++;
            }

            AnsiConsole.MarkupLine($"Replacement targets: [green]{assignCount}[/]");

            if (assignCount == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No fonts to replace[/]");
                return;
            }

            var replacer = new FontReplacer(ctx);
            replacer.BuildCabMappings(resolved.AssetFiles);

            var replaced = replacer.ReplaceFromMapping(mapping, outputDir);
            AnsiConsole.MarkupLine($"[green]Oneshot complete: {replaced} font(s) replaced[/]");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch
                {
                }
            }
        }
    }

    private static int NormalizePadding(int padding)
    {
        return padding > 0 ? padding : DefaultPadding;
    }
}
