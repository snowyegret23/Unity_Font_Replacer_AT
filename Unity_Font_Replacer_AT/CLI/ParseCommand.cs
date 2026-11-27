using System.Text.Json;
using Spectre.Console;
using UnityFontReplacer.Core;
using UnityFontReplacer.Models;

namespace UnityFontReplacer.CLI;

public static class ParseCommand
{
    public static async Task ExecuteAsync(string gamePath, bool ps5Swizzle, int maxWorkers)
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

        AnsiConsole.MarkupLine($"[green]{Strings.Get("scan_start")}[/]");

        using var ctx = new AssetsContext(resolved.DataPath, resolved.ManagedPath);

        var scanner = new FontScanner(ctx);
        var entries = scanner.ScanAll(resolved.AssetFiles, ps5Swizzle, maxWorkers);

        AnsiConsole.MarkupLine(Strings.Get("scan_complete", entries.TtfCount, entries.TmpCount));

        var mapping = FontMapping.FromScanResult(entries, resolved.GamePath);

        var jsonPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            Path.GetFileName(resolved.GamePath) + ".json");

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        var json = JsonSerializer.Serialize(mapping, options);
        await File.WriteAllTextAsync(jsonPath, json, System.Text.Encoding.UTF8);

        AnsiConsole.MarkupLine(Strings.Get("json_saved", jsonPath));
    }
}
