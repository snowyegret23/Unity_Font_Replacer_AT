using Spectre.Console;
using UnityFontReplacer.Core;
using UnityFontReplacer.Models;

namespace UnityFontReplacer.CLI;

public static class ListCommand
{
    public static async Task ExecuteAsync(string? gamePath, string jsonFile, string? outputDir = null, bool freezeDynamic = false, bool keepOriginalFallback = false)
    {
        await Task.CompletedTask;

        if (!File.Exists(jsonFile))
        {
            AnsiConsole.MarkupLine($"[red]JSON file not found: {Markup.Escape(jsonFile)}[/]");
            return;
        }

        var json = await File.ReadAllTextAsync(jsonFile, System.Text.Encoding.UTF8);
        var mapping = FontMapping.Parse(json);

        // gamePath 인자가 있으면 매핑의 game_path를 덮어쓰기
        if (!string.IsNullOrWhiteSpace(gamePath))
            mapping.GamePath = gamePath;

        if (string.IsNullOrWhiteSpace(mapping.GamePath))
            throw new ArgumentException("Specify --gamepath when the mapping does not include game_path.");
        var resolved = GamePathResolver.Resolve(mapping.GamePath);
        if (resolved == null)
        {
            AnsiConsole.MarkupLine($"[red]{Strings.Get("err_gamepath_not_found", mapping.GamePath)}[/]");
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

        // 교체 대상 수 확인
        var replaceCount = mapping.Fonts.Values.Count(e => !string.IsNullOrWhiteSpace(e.ReplaceTo));
        AnsiConsole.MarkupLine($"Replacement targets: [green]{replaceCount}[/]");

        if (replaceCount == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No fonts to replace (all Replace_to fields are empty)[/]");
            return;
        }

        using var ctx = new AssetsContext(resolved.DataPath, resolved.ManagedPath);

        // 버전 로드
        var version = !string.IsNullOrWhiteSpace(mapping.EngineVersion)
            ? mapping.EngineVersion
            : ctx.DetectEngineVersion();

        if (version != null)
        {
            ctx.LoadClassDatabase(version);
            ctx.SetupMonoCecil();
        }

        var replacer = new FontReplacer(ctx);
        replacer.BuildCabMappings(resolved.AssetFiles);
        var replaced = replacer.ReplaceFromMapping(mapping, outputDir, freezeDynamic, keepOriginalFallback);

        AnsiConsole.MarkupLine($"[green]Replacement complete: {replaced} font(s) replaced[/]");
    }
}
