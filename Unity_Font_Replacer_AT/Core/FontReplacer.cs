using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Spectre.Console;
using UnityFontReplacer.CLI;
using UnityFontReplacer.Models;
using UnityFontReplacer.SDF;

namespace UnityFontReplacer.Core;

public class FontReplacer
{
    private static readonly int[] BuiltinPaddingVariants = { 5, 7, 15 };
    private readonly AssetsContext _ctx;

    // 크로스 파일 텍스처 교체 보류: (논리 대상명, PathId) → PNG + atlas fallback material plan
    private readonly Dictionary<(string targetName, long pathId), DeferredTextureReplacement> _pendingCrossBundleTextures = new();

    // 크로스 파일 정확 material 패치 보류: (논리 대상명, PathId) → material plan
    private readonly Dictionary<(string targetName, long pathId), MaterialPatchPlan> _pendingCrossBundleMaterials = new();

    // CAB 이름 또는 standalone assets 파일명 → 실제 파일 경로
    private readonly Dictionary<string, string> _cabToBundlePath =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<(string ttfPath, int padding, bool rasterMode), string> _generatedSdfDirCache =
        new();

    private int[]? _defaultCharset;
    private string? _generatedSdfRoot;

    private sealed class DeferredTextureReplacement
    {
        public required string AtlasPngPath { get; init; }
        public required MaterialPatchPlan MaterialFallbackPlan { get; init; }
        public TextureFilterMode? FilterMode { get; init; }
    }

    private sealed class StandaloneSdfMirrorPatch
    {
        public required string FontName { get; init; }
        public required string AtlasPngPath { get; init; }
        public required MaterialPatchPlan MaterialPlan { get; init; }
        public required HashSet<string> TextureNameCandidates { get; init; }
        public TextureFilterMode? FilterMode { get; init; }
    }

    public FontReplacer(AssetsContext ctx)
    {
        _ctx = ctx;
    }

    public void RegisterCabMapping(string cabName, string bundlePath)
    {
        _cabToBundlePath[cabName] = bundlePath;
    }

    public void BuildCabMappings(List<string> assetFiles)
    {
        foreach (var filePath in assetFiles)
        {
            var baseName = Path.GetFileName(filePath);
            if (!string.IsNullOrWhiteSpace(baseName))
                _cabToBundlePath[baseName] = filePath;

            if (!FontScanner.IsBundleFile(filePath))
                continue;

            try
            {
                using var fs = File.OpenRead(filePath);
                var bundle = new AssetBundleFile();
                bundle.Read(new AssetsFileReader(fs));
                foreach (var dir in bundle.BlockAndDirInfo.DirectoryInfos)
                {
                    if (!string.IsNullOrEmpty(dir.Name))
                        _cabToBundlePath[dir.Name] = filePath;
                }
            }
            catch
            {
            }
        }
    }

    public int ReplaceFromMapping(FontMapping mapping, string? outputDir = null)
    {
        _pendingCrossBundleTextures.Clear();
        _pendingCrossBundleMaterials.Clear();

        try
        {
            int replacedCount = 0;
            var resolved = GamePathResolver.Resolve(mapping.GamePath);
            if (resolved == null)
                return 0;

            var byPhysicalFile = new Dictionary<string, List<FontEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in mapping.Fonts.Values.Where(e => !string.IsNullOrWhiteSpace(e.ReplaceTo)))
            {
                var filePath = FindAssetFile(resolved.AssetFiles, entry.File);
                if (filePath == null)
                {
                    AnsiConsole.MarkupLine($"[yellow]File not found: {Markup.Escape(entry.File)}[/]");
                    continue;
                }

                if (!byPhysicalFile.TryGetValue(filePath, out var bucket))
                {
                    bucket = [];
                    byPhysicalFile[filePath] = bucket;
                }

                bucket.Add(entry);
            }

            foreach (var (filePath, entries) in byPhysicalFile)
            {
                try
                {
                    var count = ReplaceResolvedFile(filePath, entries, resolved.DataPath, outputDir);
                    replacedCount += count;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error processing {Markup.Escape(Path.GetFileName(filePath))}: {Markup.Escape(ex.Message)}[/]");
                }
            }

            _ctx.Manager.UnloadAll(false);
            ReplaceStandaloneSdfMirrors(mapping, outputDir);
            _ctx.Manager.UnloadAll(false);

            if (_pendingCrossBundleTextures.Count > 0 || _pendingCrossBundleMaterials.Count > 0)
            {
                AnsiConsole.MarkupLine($"[cyan]Cross-file: {_pendingCrossBundleTextures.Count} textures, {_pendingCrossBundleMaterials.Count} materials[/]");
                replacedCount += ExecuteCrossBundleReplacements(outputDir, mapping.GamePath);
            }

            return replacedCount;
        }
        finally
        {
            CleanupGeneratedSdfAssets();
        }
    }

    private int ExecuteCrossBundleReplacements(string? outputDir, string gamePath)
    {
        var logicalTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _pendingCrossBundleTextures.Keys)
            logicalTargets.Add(kv.targetName);
        foreach (var kv in _pendingCrossBundleMaterials.Keys)
            logicalTargets.Add(kv.targetName);

        int count = 0;
        var resolved = GamePathResolver.Resolve(gamePath);
        var targetToWork = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var logicalTarget in logicalTargets)
        {
            if (!_cabToBundlePath.TryGetValue(logicalTarget, out var targetPath))
                continue;

            if (!targetToWork.TryGetValue(targetPath, out var workSet))
            {
                workSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                targetToWork[targetPath] = workSet;
            }

            workSet.Add(logicalTarget);
        }

        foreach (var (targetPath, workSet) in targetToWork)
        {
            if (FontScanner.IsBundleFile(targetPath))
            {
                count += ExecuteDeferredBundleReplacements(targetPath, workSet, resolved, outputDir);
            }
            else
            {
                count += ExecuteDeferredAssetsReplacements(targetPath, workSet, resolved, outputDir);
            }
        }

        return count;
    }

    private int ExecuteDeferredBundleReplacements(
        string bundlePath,
        HashSet<string> logicalTargets,
        ResolvedGamePath? resolved,
        string? outputDir)
    {
        var am = new AssetsManager();
        int count = 0;

        try
        {
            var bunInst = am.LoadBundleFile(bundlePath);
            var dirInfos = bunInst.file.BlockAndDirInfo.DirectoryInfos;
            var modifiedFiles = new List<(int dirIndex, AssetsFileInstance fileInst)>();

            for (int i = 0; i < dirInfos.Count; i++)
            {
                if (!dirInfos[i].IsSerialized)
                    continue;

                var inst = am.LoadAssetsFileFromBundle(bunInst, i);
                if (ApplyDeferredReplacements(am, inst, logicalTargets, ref count))
                    modifiedFiles.Add((i, inst));
            }

            if (modifiedFiles.Count > 0)
            {
                var savePath = outputDir != null && resolved != null
                    ? SaveStrategy.ResolveOutputPath(bundlePath, resolved.DataPath, outputDir)
                    : null;
                BundleProcessor.SaveBundle(bunInst, modifiedFiles, savePath);
                AnsiConsole.MarkupLine($"[green]Cross-file saved: {Path.GetFileName(bundlePath)}[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Cross-file error ({Path.GetFileName(bundlePath)}): {Markup.Escape(ex.Message)}[/]");
        }
        finally
        {
            am.UnloadAll(true);
        }

        return count;
    }

    private int ExecuteDeferredAssetsReplacements(
        string assetPath,
        HashSet<string> logicalTargets,
        ResolvedGamePath? resolved,
        string? outputDir)
    {
        var am = new AssetsManager();
        int count = 0;

        try
        {
            var inst = am.LoadAssetsFile(assetPath, loadDeps: true);
            if (ApplyDeferredReplacements(am, inst, logicalTargets, ref count))
            {
                var savePath = outputDir != null && resolved != null
                    ? SaveStrategy.ResolveOutputPath(assetPath, resolved.DataPath, outputDir)
                    : null;
                SaveStrategy.SaveAssetsFile(inst, savePath);
                AnsiConsole.MarkupLine($"[green]Cross-file saved: {Path.GetFileName(assetPath)}[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Cross-file error ({Path.GetFileName(assetPath)}): {Markup.Escape(ex.Message)}[/]");
        }
        finally
        {
            am.UnloadAll(true);
        }

        return count;
    }

    private bool ApplyDeferredReplacements(
        AssetsManager am,
        AssetsFileInstance inst,
        HashSet<string> logicalTargets,
        ref int count)
    {
        bool modified = false;
        var exactMaterialPathIds = _pendingCrossBundleMaterials
            .Where(kv => logicalTargets.Contains(kv.Key.targetName))
            .Select(kv => kv.Key.pathId)
            .ToHashSet();

        foreach (var kv in _pendingCrossBundleTextures)
        {
            if (!logicalTargets.Contains(kv.Key.targetName))
                continue;

            var texInfo = TextureHandler.FindTextureByPathId(inst, kv.Key.pathId);
            if (texInfo == null)
                continue;

            TextureHandler.ReplaceFromPng(am, inst, texInfo, kv.Value.AtlasPngPath, kv.Value.FilterMode);
            PatchAllMaterialsReferencingTexture(
                am,
                inst,
                kv.Key.pathId,
                kv.Value.MaterialFallbackPlan,
                exactMaterialPathIds);

            modified = true;
            count++;
            AnsiConsole.MarkupLine($"[green]Cross-file atlas: PathId={kv.Key.pathId}[/]");
        }

        foreach (var kv in _pendingCrossBundleMaterials)
        {
            if (!logicalTargets.Contains(kv.Key.targetName))
                continue;

            var matInfo = MaterialPatcher.FindMaterialByPathId(inst, kv.Key.pathId);
            if (matInfo == null)
                continue;

            MaterialPatcher.PatchForReplacement(am, inst, matInfo, kv.Value);
            modified = true;
        }

        return modified;
    }

    private int ReplaceResolvedFile(string filePath, List<FontEntry> entries, string dataPath, string? outputDir)
    {
        return FontScanner.IsBundleFile(filePath)
            ? ReplaceInBundle(filePath, entries, dataPath, outputDir)
            : ReplaceInAssetsFile(filePath, entries, dataPath, outputDir);
    }

    private void ReplaceStandaloneSdfMirrors(FontMapping mapping, string? outputDir)
    {
        var resolved = GamePathResolver.Resolve(mapping.GamePath);
        if (resolved == null)
            return;

        var dataUnityPath = FindAssetFile(resolved.AssetFiles, "data.unity3d");
        if (dataUnityPath == null)
            return;

        var patches = BuildStandaloneSdfMirrorPatches(mapping);
        if (patches.Count == 0)
            return;

        int count = FontScanner.IsBundleFile(dataUnityPath)
            ? ReplaceStandaloneSdfMirrorsInBundle(dataUnityPath, patches, resolved.DataPath, outputDir)
            : ReplaceStandaloneSdfMirrorsInAssetsFile(dataUnityPath, patches, resolved.DataPath, outputDir);
        if (count > 0)
            AnsiConsole.MarkupLine($"[green]Standalone SDF mirrors saved: {Path.GetFileName(dataUnityPath)}[/]");
    }

    private List<StandaloneSdfMirrorPatch> BuildStandaloneSdfMirrorPatches(FontMapping mapping)
    {
        var result = new List<StandaloneSdfMirrorPatch>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in mapping.Fonts.Values
                     .Where(e => e.Type == FontType.SDF && !string.IsNullOrWhiteSpace(e.ReplaceTo)))
        {
            var dedupeKey = $"{entry.Name}|{entry.ReplaceTo}|{entry.AtlasPadding}|{entry.ForceRaster}";
            if (!seen.Add(dedupeKey))
                continue;

            bool forceRaster = ParseBoolFlag(entry.ForceRaster);
            var sourceData = LoadSdfSourceData(entry.ReplaceTo, entry.AtlasPadding, forceRaster);
            if (sourceData == null || string.IsNullOrWhiteSpace(sourceData.AtlasPngPath))
                continue;

            result.Add(new StandaloneSdfMirrorPatch
            {
                FontName = entry.Name,
                AtlasPngPath = sourceData.AtlasPngPath!,
                MaterialPlan = CreateMaterialPlan(sourceData, entry.AtlasPadding, forceRaster, includeSourceMaterial: true),
                TextureNameCandidates = BuildStandaloneTextureNameCandidates(entry.Name),
                FilterMode = sourceData.TextureFilterMode,
            });
        }

        return result;
    }

    private int ReplaceStandaloneSdfMirrorsInAssetsFile(
        string filePath,
        List<StandaloneSdfMirrorPatch> patches,
        string dataPath,
        string? outputDir)
    {
        var inst = _ctx.LoadAssetsFile(filePath);
        int count = ReplaceStandaloneSdfMirrorsInAssetsInstance(inst, patches);

        if (count > 0)
        {
            var savePath = outputDir != null
                ? SaveStrategy.ResolveOutputPath(filePath, dataPath, outputDir)
                : null;
            SaveStrategy.SaveAssetsFile(inst, savePath);
        }

        return count;
    }

    private int ReplaceStandaloneSdfMirrorsInBundle(
        string filePath,
        List<StandaloneSdfMirrorPatch> patches,
        string dataPath,
        string? outputDir)
    {
        var bunInst = _ctx.LoadBundleFile(filePath);
        var dirInfos = bunInst.file.BlockAndDirInfo.DirectoryInfos;
        int totalCount = 0;
        var modifiedFiles = new List<(int dirIndex, AssetsFileInstance fileInst)>();

        for (int i = 0; i < dirInfos.Count; i++)
        {
            if (!dirInfos[i].IsSerialized)
                continue;

            try
            {
                var inst = _ctx.LoadAssetsFileFromBundle(bunInst, i);
                int count = ReplaceStandaloneSdfMirrorsInAssetsInstance(inst, patches);
                if (count > 0)
                {
                    modifiedFiles.Add((i, inst));
                    totalCount += count;
                }
            }
            catch
            {
            }
        }

        if (modifiedFiles.Count > 0)
        {
            var savePath = outputDir != null
                ? SaveStrategy.ResolveOutputPath(filePath, dataPath, outputDir)
                : null;
            BundleProcessor.SaveBundle(bunInst, modifiedFiles, savePath);
        }

        return totalCount;
    }

    private int ReplaceStandaloneSdfMirrorsInAssetsInstance(
        AssetsFileInstance inst,
        List<StandaloneSdfMirrorPatch> patches)
    {
        int count = 0;
        var matchedTextures = new List<(long pathId, string textureName, StandaloneSdfMirrorPatch patch)>();

        foreach (var texInfo in inst.file.GetAssetsOfType(AssetClassID.Texture2D))
        {
            string textureName = "";
            try
            {
                textureName = TryGetAssetName(inst, texInfo) ??
                              TryGetAssetNameWithClassDatabase(inst, texInfo) ??
                              "";
                if (string.IsNullOrWhiteSpace(textureName))
                    continue;

                var patch = FindStandaloneMirrorPatch(patches, textureName);
                if (patch == null)
                    continue;

                TextureHandler.ReplaceFromPng(_ctx.Manager, inst, texInfo, patch.AtlasPngPath, patch.FilterMode);
                matchedTextures.Add((texInfo.PathId, textureName, patch));
                count++;
                AnsiConsole.MarkupLine($"[green]Standalone atlas replaced: {Markup.Escape(textureName)}[/]");
            }
            catch (Exception ex)
            {
                var detail = string.IsNullOrWhiteSpace(textureName)
                    ? texInfo.PathId.ToString()
                    : $"{texInfo.PathId} / {textureName}";
                AnsiConsole.MarkupLine($"[red]Standalone atlas replace failed ({Markup.Escape(detail)}): {Markup.Escape(ex.Message)}[/]");
            }
        }

        foreach (var (pathId, textureName, patch) in matchedTextures)
        {
            int patchedMaterials = PatchAllMaterialsReferencingTexture(
                _ctx.Manager,
                inst,
                pathId,
                patch.MaterialPlan);
            if (patchedMaterials > 0)
            {
                count += patchedMaterials;
                AnsiConsole.MarkupLine($"[green]Standalone materials patched: {Markup.Escape(textureName)} ({patchedMaterials})[/]");
            }
        }

        return count;
    }

    private string? TryGetAssetNameWithClassDatabase(AssetsFileInstance inst, AssetFileInfo info)
    {
        try
        {
            var cldb = _ctx.Manager.ClassDatabase;
            if (cldb == null)
                return null;

            var name = AssetHelper.GetAssetNameFast(inst.file, cldb, info);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    private int ReplaceInAssetsFile(string filePath, List<FontEntry> entries, string dataPath, string? outputDir)
    {
        var inst = _ctx.LoadAssetsFile(filePath);
        int count = ReplaceInAssetsInstance(inst, entries);

        if (count > 0)
        {
            var savePath = outputDir != null
                ? SaveStrategy.ResolveOutputPath(filePath, dataPath, outputDir)
                : null;
            SaveStrategy.SaveAssetsFile(inst, savePath);
        }

        return count;
    }

    private int ReplaceInBundle(string filePath, List<FontEntry> entries, string dataPath, string? outputDir)
    {
        var bunInst = _ctx.LoadBundleFile(filePath);
        var dirInfos = bunInst.file.BlockAndDirInfo.DirectoryInfos;
        int totalCount = 0;
        var modifiedFiles = new List<(int dirIndex, AssetsFileInstance fileInst)>();

        for (int i = 0; i < dirInfos.Count; i++)
        {
            if (!dirInfos[i].IsSerialized)
                continue;

            try
            {
                var inst = _ctx.LoadAssetsFileFromBundle(bunInst, i);
                int count = ReplaceInAssetsInstance(inst, entries);
                if (count > 0)
                {
                    modifiedFiles.Add((i, inst));
                    totalCount += count;
                }
            }
            catch
            {
            }
        }

        if (modifiedFiles.Count > 0)
        {
            var savePath = outputDir != null
                ? SaveStrategy.ResolveOutputPath(filePath, dataPath, outputDir)
                : null;
            BundleProcessor.SaveBundle(bunInst, modifiedFiles, savePath);
        }

        return totalCount;
    }

    private int ReplaceInAssetsInstance(AssetsFileInstance inst, List<FontEntry> entries)
    {
        var entryByPathId = entries.ToDictionary(e => e.PathId);
        int count = 0;

        foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Font))
        {
            if (!entryByPathId.TryGetValue(info.PathId, out var entry) || entry.Type != FontType.TTF)
                continue;

            try
            {
                var ttfData = TtfFontHandler.LoadTtfFile(entry.ReplaceTo);
                TtfFontHandler.ReplaceFontData(_ctx.Manager, inst, info, ttfData);
                count++;
                AnsiConsole.MarkupLine($"[green]TTF replaced: {Markup.Escape(entry.Name)}[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]TTF replace failed ({Markup.Escape(entry.Name)}): {Markup.Escape(ex.Message)}[/]");
            }
        }

        var sdfEntries = entryByPathId.Values.Where(e => e.Type == FontType.SDF).ToList();
        if (sdfEntries.Count > 0)
            count += ReplaceTmpFonts(inst, sdfEntries);

        return count;
    }

    private int ReplaceTmpFonts(AssetsFileInstance inst, List<FontEntry> entries)
    {
        int count = 0;
        var entryByPathId = entries.ToDictionary(e => e.PathId);

        foreach (var info in inst.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
        {
            if (!entryByPathId.TryGetValue(info.PathId, out var entry))
                continue;

            try
            {
                var baseField = _ctx.Manager.GetBaseField(inst, info);
                var schemaInfo = TmpSchemaDetector.Inspect(baseField, inst.file.Metadata.UnityVersion);
                var targetAsset = TmpFontHandler.ReadFromField(baseField);
                if (targetAsset.AtlasTexturePathId == 0 && schemaInfo.AtlasPathId != 0)
                {
                    targetAsset.AtlasTextureFileId = schemaInfo.AtlasFileId;
                    targetAsset.AtlasTexturePathId = schemaInfo.AtlasPathId;
                }
                if (targetAsset.AtlasPadding <= 0 && schemaInfo.AtlasPadding > 0)
                    targetAsset.AtlasPadding = schemaInfo.AtlasPadding;

                bool forceRaster = ParseBoolFlag(entry.ForceRaster);
                var sourceData = LoadSdfSourceData(entry.ReplaceTo, targetAsset.AtlasPadding, forceRaster);
                if (sourceData == null)
                {
                    AnsiConsole.MarkupLine($"[yellow]SDF source not found: {Markup.Escape(entry.ReplaceTo)}[/]");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(sourceData.AtlasPngPath) || targetAsset.AtlasTexturePathId == 0)
                {
                    AnsiConsole.MarkupLine($"[yellow]SDF skipped (atlas missing): {Markup.Escape(entry.Name)}[/]");
                    continue;
                }

                var exactMaterialPlan = CreateMaterialPlan(sourceData, targetAsset.AtlasPadding, forceRaster, includeSourceMaterial: true);
                var atlasFallbackPlan = CreateMaterialPlan(sourceData, targetAsset.AtlasPadding, forceRaster, includeSourceMaterial: false);
                bool hasExactLocalMaterial = targetAsset.MaterialFileId == 0 &&
                                             targetAsset.MaterialPathId != 0 &&
                                             MaterialPatcher.FindMaterialByPathId(inst, targetAsset.MaterialPathId) != null;

                var textureLocalInfo = ResolveLocalTextureInfo(inst, targetAsset);
                var deferredTextureTarget = targetAsset.AtlasTextureFileId == 0
                    ? null
                    : ResolveExternalCabPath(inst, targetAsset.AtlasTextureFileId);
                if (textureLocalInfo == null && string.IsNullOrWhiteSpace(deferredTextureTarget))
                {
                    AnsiConsole.MarkupLine($"[yellow]SDF skipped (atlas not resolvable): {Markup.Escape(entry.Name)}[/]");
                    continue;
                }

                var targetSchema = schemaInfo.Version;
                TmpFontHandler.WriteToField(sourceData.FontAsset, baseField, targetSchema);
                info.SetNewData(baseField);

                bool atlasReplaced = false;
                bool exactMaterialPatched = false;

                if (textureLocalInfo != null)
                {
                    TextureHandler.ReplaceFromPng(_ctx.Manager, inst, textureLocalInfo, sourceData.AtlasPngPath!, sourceData.TextureFilterMode);
                    if (!hasExactLocalMaterial)
                    {
                        PatchAllMaterialsReferencingTexture(
                            _ctx.Manager,
                            inst,
                            targetAsset.AtlasTexturePathId,
                            atlasFallbackPlan);
                    }
                    atlasReplaced = true;
                }
                else if (!string.IsNullOrWhiteSpace(deferredTextureTarget))
                {
                    _pendingCrossBundleTextures[(deferredTextureTarget, targetAsset.AtlasTexturePathId)] =
                        new DeferredTextureReplacement
                        {
                            AtlasPngPath = sourceData.AtlasPngPath!,
                            MaterialFallbackPlan = atlasFallbackPlan,
                            FilterMode = sourceData.TextureFilterMode,
                        };
                    atlasReplaced = true;
                }

                if (targetAsset.MaterialPathId != 0)
                {
                    if (targetAsset.MaterialFileId == 0)
                    {
                        var matInfo = MaterialPatcher.FindMaterialByPathId(inst, targetAsset.MaterialPathId);
                        if (matInfo != null)
                        {
                            MaterialPatcher.PatchForReplacement(_ctx.Manager, inst, matInfo, exactMaterialPlan);
                            exactMaterialPatched = true;
                        }
                    }
                    else
                    {
                        var deferredMaterialTarget = ResolveExternalCabPath(inst, targetAsset.MaterialFileId);
                        if (!string.IsNullOrWhiteSpace(deferredMaterialTarget))
                        {
                            _pendingCrossBundleMaterials[(deferredMaterialTarget, targetAsset.MaterialPathId)] = exactMaterialPlan;
                            exactMaterialPatched = true;
                        }
                    }
                }

                if (!atlasReplaced)
                {
                    AnsiConsole.MarkupLine($"[yellow]SDF skipped (atlas not resolvable): {Markup.Escape(entry.Name)}[/]");
                    continue;
                }

                count++;
                var detail = exactMaterialPatched ? "+atlas+material" : "+atlas";
                AnsiConsole.MarkupLine($"[green]SDF replaced ({detail}): {Markup.Escape(entry.Name)}[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]SDF replace failed ({Markup.Escape(entry.Name)}): {Markup.Escape(ex.Message)}[/]");
            }
        }

        return count;
    }

    private static MaterialPatchPlan CreateMaterialPlan(
        SdfSourceData sourceData,
        int targetPadding,
        bool forceRaster,
        bool includeSourceMaterial)
    {
        return new MaterialPatchPlan
        {
            ReplacementPadding = sourceData.FontAsset.AtlasPadding,
            AtlasWidth = sourceData.FontAsset.AtlasWidth,
            AtlasHeight = sourceData.FontAsset.AtlasHeight,
            TargetPadding = targetPadding,
            ForceRaster = forceRaster,
            PreserveGameStyle = !forceRaster,
            SourceMaterial = includeSourceMaterial ? sourceData.Material : null,
        };
    }

    private static AssetFileInfo? ResolveLocalTextureInfo(AssetsFileInstance inst, TmpFontAsset targetAsset)
    {
        if (targetAsset.AtlasTextureFileId != 0)
            return null;

        return TextureHandler.FindTextureByPathId(inst, targetAsset.AtlasTexturePathId);
    }

    private SdfSourceData? LoadSdfSourceData(
        string sourcePath,
        int targetPaddingHint,
        bool preferRaster)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;

        if (sourcePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(sourcePath))
            return LoadFromJson(sourcePath);

        if (Directory.Exists(sourcePath))
            return LoadFromDirectory(sourcePath, Path.GetFileName(sourcePath), preferRaster);

        if (LooksLikeFontFileInput(sourcePath))
        {
            var generatedData = GenerateSdfSourceFromTtf(sourcePath, targetPaddingHint, preferRaster);
            if (generatedData != null)
                return generatedData;
        }

        if (File.Exists(sourcePath))
            return LoadFromDirectory(Path.GetDirectoryName(sourcePath)!, Path.GetFileNameWithoutExtension(sourcePath), preferRaster);

        if (TryGetBuiltinAlias(sourcePath, out var builtinAlias))
        {
            var paddingVariant = SelectBuiltinBulkPaddingVariant(builtinAlias, targetPaddingHint);
            foreach (var assetRoot in EnumerateKrAssetRoots(paddingVariant))
            {
                var builtinData = TryLoadNamedSdfSource(assetRoot, builtinAlias, preferRaster);
                if (builtinData != null)
                    return builtinData;
            }
        }

        foreach (var assetRoot in EnumerateKrAssetRoots(paddingVariant: null))
        {
            var namedData = TryLoadNamedSdfSource(assetRoot, sourcePath, preferRaster);
            if (namedData != null)
                return namedData;

            var subDir = Path.Combine(assetRoot, sourcePath);
            if (!Directory.Exists(subDir))
                continue;

            var dirData = LoadFromDirectory(subDir, Path.GetFileName(subDir), preferRaster);
            if (dirData != null)
                return dirData;
        }

        return null;
    }

    private SdfSourceData? GenerateSdfSourceFromTtf(
        string ttfSource,
        int targetPaddingHint,
        bool preferRaster)
    {
        var ttfPath = TtfFontHandler.ResolveTtfPath(ttfSource);
        if (ttfPath == null)
            return null;

        int padding = NormalizePadding(targetPaddingHint);
        var cacheKey = (ttfPath, padding, preferRaster);
        if (_generatedSdfDirCache.TryGetValue(cacheKey, out var cachedDir) && Directory.Exists(cachedDir))
            return LoadFromDirectory(cachedDir, Path.GetFileNameWithoutExtension(ttfPath), preferRaster);

        int[] unicodes;
        try
        {
            unicodes = _defaultCharset ??= MakeSdfCommand.LoadCharset(MakeSdfCommand.DefaultCharsetArgument);
        }
        catch (FileNotFoundException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return null;
        }

        if (unicodes.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Default charset is empty.[/]");
            return null;
        }

        var generatedRoot = EnsureGeneratedSdfRoot();
        var fontBaseName = Path.GetFileNameWithoutExtension(ttfPath);
        var modeSuffix = preferRaster ? "raster" : "sdf";
        var outputDir = Path.Combine(generatedRoot, $"{SanitizePathSegment(fontBaseName)}_{modeSuffix}_padding_{padding}");
        Directory.CreateDirectory(outputDir);

        byte[] ttfData;
        try
        {
            ttfData = File.ReadAllBytes(ttfPath);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to read TTF: {Markup.Escape(ex.Message)}[/]");
            return null;
        }

        var displayMode = preferRaster ? "Raster" : "SDF";
        var filterMode = preferRaster ? TextureFilterMode.Point : TextureFilterMode.Bilinear;
        AnsiConsole.MarkupLine($"[cyan]Generating {displayMode} from TTF: {Markup.Escape(Path.GetFileName(ttfPath))} (padding {padding})[/]");
        var result = SdfGenerator.Generate(
            ttfData,
            unicodes,
            atlasWidth: 4096,
            atlasHeight: 4096,
            padding: padding,
            pointSize: 0,
            rasterMode: preferRaster,
            filterMode: filterMode);

        try
        {
            SdfGenerator.SaveToFiles(result, outputDir, fontBaseName);
        }
        finally
        {
            result.AtlasImage.Dispose();
        }

        _generatedSdfDirCache[cacheKey] = outputDir;
        return LoadFromDirectory(outputDir, fontBaseName, preferRaster);
    }

    private static HashSet<string> BuildStandaloneTextureNameCandidates(string fontName)
    {
        var normalized = NormalizeFontName(fontName);
        var (_, nameCandidates) = BuildFontAssetNameCandidates(normalized, preferRaster: false);
        var textureNames = DedupePreserveOrder(
            nameCandidates
                .Select(name => $"{name} Atlas")
                .Append($"{fontName.Trim()} Atlas"));
        return new HashSet<string>(textureNames, StringComparer.OrdinalIgnoreCase);
    }

    private static StandaloneSdfMirrorPatch? FindStandaloneMirrorPatch(
        IEnumerable<StandaloneSdfMirrorPatch> patches,
        string textureName)
    {
        foreach (var patch in patches)
        {
            if (patch.TextureNameCandidates.Contains(textureName))
                return patch;
        }

        return null;
    }

    private static SdfSourceData? LoadFromDirectory(string dir, string? sourceNameHint, bool preferRaster)
    {
        if (!Directory.Exists(dir))
            return null;

        if (!string.IsNullOrWhiteSpace(sourceNameHint))
        {
            var namedData = TryLoadNamedSdfSource(dir, sourceNameHint, preferRaster);
            if (namedData != null)
                return namedData;
        }

        var jsonPath = Directory.GetFiles(dir, "* SDF.json")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (jsonPath != null)
            return LoadFromJson(jsonPath);

        jsonPath = Directory.GetFiles(dir, "*.json")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return jsonPath != null ? LoadFromJson(jsonPath) : null;
    }

    private static SdfSourceData? TryLoadNamedSdfSource(string assetRoot, string fontName, bool preferRaster)
    {
        if (!Directory.Exists(assetRoot))
            return null;

        var (_, nameCandidates) = BuildFontAssetNameCandidates(NormalizeFontName(fontName), preferRaster);
        foreach (var nameCandidate in nameCandidates)
        {
            var jsonPath = Path.Combine(assetRoot, $"{nameCandidate}.json");
            if (File.Exists(jsonPath))
                return LoadFromJson(jsonPath);
        }

        return null;
    }

    private static (List<string> fontNameCandidates, List<string> nameCandidates) BuildFontAssetNameCandidates(
        string normalized,
        bool preferRaster)
    {
        var rawName = normalized.Trim();
        var baseName = StripRenderSuffix(rawName);

        var nameCandidates = DedupePreserveOrder(preferRaster
            ? new[] { rawName, $"{baseName} Raster", $"{baseName} SDF" }
            : new[] { rawName, $"{baseName} SDF", $"{baseName} Raster" });
        var fontNameCandidates = DedupePreserveOrder(new[] { rawName, baseName }.Concat(nameCandidates));
        return (fontNameCandidates, nameCandidates);
    }

    private static string NormalizeFontName(string name)
    {
        var normalized = name.Trim();
        foreach (var ext in new[] { ".ttf", ".otf", ".json", ".png" })
        {
            if (normalized.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^ext.Length];
                break;
            }
        }

        foreach (var suffix in new[]
                 {
                     " SDF Atlas",
                     " Raster Atlas",
                     " Atlas",
                     " SDF Material",
                     " Raster Material",
                     " Material",
                     " SDF",
                     " Raster",
                 })
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }

        return normalized.Trim();
    }

    private static string StripRenderSuffix(string name)
    {
        if (name.EndsWith(" SDF", StringComparison.OrdinalIgnoreCase))
            return name[..^" SDF".Length];
        if (name.EndsWith(" Raster", StringComparison.OrdinalIgnoreCase))
            return name[..^" Raster".Length];
        return name;
    }

    private static List<string> DedupePreserveOrder(IEnumerable<string> values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var candidate = value?.Trim() ?? "";
            if (candidate.Length == 0 || !seen.Add(candidate))
                continue;
            result.Add(candidate);
        }

        return result;
    }

    private static int? SelectBuiltinBulkPaddingVariant(string normalized, int targetPaddingHint)
    {
        if (!TryGetBuiltinAlias(normalized, out _) || targetPaddingHint <= 0)
            return null;

        return BuiltinPaddingVariants
            .OrderBy(value => Math.Abs(value - targetPaddingHint))
            .ThenByDescending(value => value)
            .FirstOrDefault();
    }

    private static IEnumerable<string> EnumerateKrAssetRoots(int? paddingVariant)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseRoot in new[]
                 {
                     Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ASSETS"),
                     Path.Combine(Directory.GetCurrentDirectory(), "ASSETS"),
                 })
        {
            if (!Directory.Exists(baseRoot))
                continue;

            if (paddingVariant.HasValue)
            {
                var paddedRoot = Path.Combine(baseRoot, $"Padding_{paddingVariant.Value}");
                if (Directory.Exists(paddedRoot) && seen.Add(paddedRoot))
                    yield return paddedRoot;
            }

            if (seen.Add(baseRoot))
                yield return baseRoot;
        }
    }

    private static bool TryGetBuiltinAlias(string input, out string alias)
    {
        alias = "";
        var normalized = NormalizeFontName(Path.GetFileName(input.Trim()));
        if (normalized.Equals("Mulmaru", StringComparison.OrdinalIgnoreCase))
        {
            alias = "Mulmaru";
            return true;
        }

        if (normalized.Equals("NanumGothic", StringComparison.OrdinalIgnoreCase))
        {
            alias = "NanumGothic";
            return true;
        }

        return false;
    }

    private static bool LooksLikeFontFileInput(string sourcePath)
    {
        var trimmed = sourcePath.Trim().Trim('"');
        return trimmed.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);
    }

    private static int NormalizePadding(int padding)
    {
        return padding > 0 ? padding : 7;
    }

    private string EnsureGeneratedSdfRoot()
    {
        _generatedSdfRoot ??= Path.Combine(
            Path.GetTempPath(),
            "UnityFontReplacer_ListSdf",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_generatedSdfRoot);
        return _generatedSdfRoot;
    }

    private void CleanupGeneratedSdfAssets()
    {
        _generatedSdfDirCache.Clear();
        _defaultCharset = null;

        var generatedRoot = _generatedSdfRoot;
        _generatedSdfRoot = null;
        if (string.IsNullOrWhiteSpace(generatedRoot) || !Directory.Exists(generatedRoot))
            return;

        try
        {
            Directory.Delete(generatedRoot, recursive: true);
        }
        catch
        {
        }
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = value
            .Trim()
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray();
        var sanitized = new string(chars).Trim();
        return sanitized.Length == 0 ? "font" : sanitized;
    }

    private static SdfSourceData? LoadFromJson(string jsonPath)
    {
        var json = File.ReadAllText(jsonPath);
        var jsonOptions = new JsonSerializerOptions
        {
            Converters = { new FlexIntConverter(), new FlexFloatConverter() },
        };

        var fontAssetJson = JsonSerializer.Deserialize<TmpFontAssetJson>(json, jsonOptions);
        if (fontAssetJson == null)
            return null;

        var dir = Path.GetDirectoryName(jsonPath)!;
        var baseName = Path.GetFileNameWithoutExtension(jsonPath);

        string? atlasPng = null;
        var atlasCandidate = Path.Combine(dir, baseName + " Atlas.png");
        if (File.Exists(atlasCandidate))
            atlasPng = atlasCandidate;
        else
        {
            atlasCandidate = Path.Combine(dir, baseName + ".png");
            if (File.Exists(atlasCandidate))
                atlasPng = atlasCandidate;
        }

        string? materialPath = null;
        var materialCandidate = Path.Combine(dir, baseName + " Material.json");
        if (File.Exists(materialCandidate))
            materialPath = materialCandidate;

        TextureFilterMode? filterMode = null;
        if (TextureFilterModeParser.TryParse(fontAssetJson.texture_filter_mode, out var parsedFilterMode))
            filterMode = parsedFilterMode;

        return new SdfSourceData
        {
            FontAsset = ConvertFromJson(fontAssetJson),
            AtlasPngPath = atlasPng,
            Material = materialPath != null ? LoadMaterialSourceData(materialPath) : null,
            TextureFilterMode = filterMode,
        };
    }

    private static TmpFontAsset ConvertFromJson(TmpFontAssetJson json)
    {
        int newGlyphCount = json.m_GlyphTable?.Count ?? 0;
        int oldGlyphCount = json.m_glyphInfoList?.Count ?? 0;
        bool useNewSchema = newGlyphCount > 0 || oldGlyphCount == 0;
        if (oldGlyphCount > newGlyphCount)
            useNewSchema = false;

        var asset = new TmpFontAsset
        {
            Name = json.m_Name ?? "",
            Version = json.m_Version ?? "",
            SchemaVersion = useNewSchema ? TmpSchemaVersion.New : TmpSchemaVersion.Old,
            UsedGlyphRects = json.m_UsedGlyphRects?.Select(rect => new TmpGlyphRect
            {
                X = rect.m_X,
                Y = rect.m_Y,
                Width = rect.m_Width,
                Height = rect.m_Height,
            }).ToList(),
            FreeGlyphRects = json.m_FreeGlyphRects?.Select(rect => new TmpGlyphRect
            {
                X = rect.m_X,
                Y = rect.m_Y,
                Width = rect.m_Width,
                Height = rect.m_Height,
            }).ToList(),
            FontWeightTable = json.m_FontWeightTable?.Select(weight => new TmpFontWeightPair
            {
                RegularTypefaceFileId = weight.regularTypeface?.m_FileID ?? 0,
                RegularTypefacePathId = weight.regularTypeface?.m_PathID ?? 0,
                ItalicTypefaceFileId = weight.italicTypeface?.m_FileID ?? 0,
                ItalicTypefacePathId = weight.italicTypeface?.m_PathID ?? 0,
            }).ToList(),
        };

        if (useNewSchema)
        {
            asset.AtlasWidth = json.m_AtlasWidth != 0
                ? json.m_AtlasWidth
                : json.m_CreationSettings?.atlasWidth ??
                  json.m_FontAssetCreationSettings?.atlasWidth ??
                  0;
            asset.AtlasHeight = json.m_AtlasHeight != 0
                ? json.m_AtlasHeight
                : json.m_CreationSettings?.atlasHeight ??
                  json.m_FontAssetCreationSettings?.atlasHeight ??
                  0;
            asset.AtlasPadding = json.m_AtlasPadding != 0
                ? json.m_AtlasPadding
                : json.m_CreationSettings?.padding ??
                  json.m_FontAssetCreationSettings?.padding ??
                  0;
            asset.AtlasRenderMode = json.m_AtlasRenderMode != 0
                ? json.m_AtlasRenderMode
                : json.m_CreationSettings?.renderMode ??
                  json.m_FontAssetCreationSettings?.renderMode ??
                  0;

            if (json.m_FaceInfo != null)
            {
                asset.FaceInfo = new TmpFaceInfo
                {
                    FamilyName = json.m_FaceInfo.m_FamilyName ?? "",
                    StyleName = json.m_FaceInfo.m_StyleName ?? "",
                    PointSize = json.m_FaceInfo.m_PointSize,
                    Scale = json.m_FaceInfo.m_Scale,
                    UnitsPerEM = json.m_FaceInfo.m_UnitsPerEM,
                    LineHeight = json.m_FaceInfo.m_LineHeight,
                    AscentLine = json.m_FaceInfo.m_AscentLine,
                    CapLine = json.m_FaceInfo.m_CapLine,
                    MeanLine = json.m_FaceInfo.m_MeanLine,
                    Baseline = json.m_FaceInfo.m_Baseline,
                    DescentLine = json.m_FaceInfo.m_DescentLine,
                    SuperscriptOffset = json.m_FaceInfo.m_SuperscriptOffset,
                    SuperscriptSize = json.m_FaceInfo.m_SuperscriptSize,
                    SubscriptOffset = json.m_FaceInfo.m_SubscriptOffset,
                    SubscriptSize = json.m_FaceInfo.m_SubscriptSize,
                    UnderlineOffset = json.m_FaceInfo.m_UnderlineOffset,
                    UnderlineThickness = json.m_FaceInfo.m_UnderlineThickness,
                    StrikethroughOffset = json.m_FaceInfo.m_StrikethroughOffset,
                    StrikethroughThickness = json.m_FaceInfo.m_StrikethroughThickness,
                    TabWidth = json.m_FaceInfo.m_TabWidth,
                };
            }

            if (json.m_GlyphTable != null)
            {
                asset.GlyphTable = json.m_GlyphTable.Select(g => new TmpGlyphNew
                {
                    Index = g.m_Index,
                    MetricsWidth = g.m_Metrics?.m_Width ?? 0,
                    MetricsHeight = g.m_Metrics?.m_Height ?? 0,
                    HorizontalBearingX = g.m_Metrics?.m_HorizontalBearingX ?? 0,
                    HorizontalBearingY = g.m_Metrics?.m_HorizontalBearingY ?? 0,
                    HorizontalAdvance = g.m_Metrics?.m_HorizontalAdvance ?? 0,
                    RectX = g.m_GlyphRect?.m_X ?? 0,
                    RectY = g.m_GlyphRect?.m_Y ?? 0,
                    RectWidth = g.m_GlyphRect?.m_Width ?? 0,
                    RectHeight = g.m_GlyphRect?.m_Height ?? 0,
                    Scale = g.m_Scale,
                    AtlasIndex = g.m_AtlasIndex,
                }).ToList();
            }

            if (json.m_CharacterTable != null)
            {
                asset.CharacterTable = json.m_CharacterTable.Select(c => new TmpCharacterNew
                {
                    ElementType = c.m_ElementType,
                    Unicode = c.m_Unicode,
                    GlyphIndex = c.m_GlyphIndex,
                    Scale = c.m_Scale,
                }).ToList();
            }
        }
        else
        {
            if (json.m_fontInfo != null)
            {
                asset.FaceInfo = new TmpFaceInfo
                {
                    FamilyName = json.m_fontInfo.Name ?? "",
                    PointSize = json.m_fontInfo.PointSize,
                    Scale = json.m_fontInfo.Scale,
                    LineHeight = json.m_fontInfo.LineHeight,
                    AscentLine = json.m_fontInfo.Ascender,
                    CapLine = json.m_fontInfo.CapHeight,
                    Baseline = json.m_fontInfo.Baseline,
                    DescentLine = json.m_fontInfo.Descender,
                    SuperscriptOffset = json.m_fontInfo.SuperscriptOffset,
                    SubscriptOffset = json.m_fontInfo.SubscriptOffset,
                    UnderlineOffset = json.m_fontInfo.UnderlineOffset,
                    UnderlineThickness = json.m_fontInfo.underlineThickness,
                    StrikethroughOffset = json.m_fontInfo.strikethroughOffset,
                    TabWidth = json.m_fontInfo.TabWidth,
                    Padding = json.m_fontInfo.Padding,
                    AtlasWidth = json.m_fontInfo.AtlasWidth,
                    AtlasHeight = json.m_fontInfo.AtlasHeight,
                };
            }

            asset.AtlasWidth = asset.FaceInfo.AtlasWidth != 0
                ? asset.FaceInfo.AtlasWidth
                : json.m_CreationSettings?.atlasWidth ??
                  json.m_FontAssetCreationSettings?.atlasWidth ??
                  0;
            asset.AtlasHeight = asset.FaceInfo.AtlasHeight != 0
                ? asset.FaceInfo.AtlasHeight
                : json.m_CreationSettings?.atlasHeight ??
                  json.m_FontAssetCreationSettings?.atlasHeight ??
                  0;
            asset.AtlasPadding = asset.FaceInfo.Padding != 0
                ? asset.FaceInfo.Padding
                : json.m_CreationSettings?.padding ??
                  json.m_FontAssetCreationSettings?.padding ??
                  0;
            asset.AtlasRenderMode = json.m_CreationSettings?.renderMode ??
                                    json.m_FontAssetCreationSettings?.renderMode ??
                                    0;

            if (json.m_glyphInfoList != null)
            {
                asset.GlyphInfoList = json.m_glyphInfoList.Select(g => new TmpGlyphOld
                {
                    Id = g.id,
                    X = g.x,
                    Y = g.y,
                    Width = g.width,
                    Height = g.height,
                    XOffset = g.xOffset,
                    YOffset = g.yOffset,
                    XAdvance = g.xAdvance,
                    Scale = g.scale,
                }).ToList();
            }
        }

        return asset;
    }

    private static MaterialSourceData? LoadMaterialSourceData(string materialPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(materialPath));
        if (!document.RootElement.TryGetProperty("m_SavedProperties", out var savedProps))
            return null;

        var floats = new Dictionary<string, float>(StringComparer.Ordinal);
        if (savedProps.TryGetProperty("m_Floats", out var floatArray) &&
            floatArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in floatArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array)
                    continue;

                var values = item.EnumerateArray().ToArray();
                if (values.Length < 2 || values[0].ValueKind != JsonValueKind.String)
                    continue;

                if (TryReadJsonFloat(values[1], out var value))
                    floats[values[0].GetString() ?? ""] = value;
            }
        }

        var colors = new Dictionary<string, MaterialColorValue>(StringComparer.Ordinal);
        if (savedProps.TryGetProperty("m_Colors", out var colorArray) &&
            colorArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in colorArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array)
                    continue;

                var values = item.EnumerateArray().ToArray();
                if (values.Length < 2 || values[0].ValueKind != JsonValueKind.String)
                    continue;

                if (TryReadMaterialColor(values[1], out var color))
                    colors[values[0].GetString() ?? ""] = color;
            }
        }

        return new MaterialSourceData
        {
            FloatProperties = floats,
            ColorProperties = colors,
        };
    }

    private static bool TryReadJsonFloat(JsonElement element, out float value)
    {
        value = 0f;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                value = element.GetSingle();
                return true;
            case JsonValueKind.String:
                return float.TryParse(element.GetString(), out value);
            default:
                return false;
        }
    }

    private static bool TryReadMaterialColor(JsonElement element, out MaterialColorValue color)
    {
        color = new MaterialColorValue();
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (!TryReadJsonFloat(element.GetProperty("r"), out var r) ||
            !TryReadJsonFloat(element.GetProperty("g"), out var g) ||
            !TryReadJsonFloat(element.GetProperty("b"), out var b) ||
            !TryReadJsonFloat(element.GetProperty("a"), out var a))
        {
            return false;
        }

        color = new MaterialColorValue
        {
            R = r,
            G = g,
            B = b,
            A = a,
        };
        return true;
    }

    private static int PatchAllMaterialsReferencingTexture(
        AssetsManager am,
        AssetsFileInstance inst,
        long texturePathId,
        MaterialPatchPlan plan,
        HashSet<long>? skipMaterialPathIds = null)
    {
        int count = 0;

        foreach (var matInfo in inst.file.GetAssetsOfType(AssetClassID.Material))
        {
            try
            {
                if (skipMaterialPathIds != null && skipMaterialPathIds.Contains(matInfo.PathId))
                    continue;

                var bf = am.GetBaseField(inst, matInfo);
                var texEnvs = bf["m_SavedProperties"]["m_TexEnvs"]["Array"];
                if (texEnvs.IsDummy)
                    continue;

                bool referencesAtlas = false;
                foreach (var entry in texEnvs.Children)
                {
                    if (entry["first"].AsString != "_MainTex")
                        continue;

                    var texRef = entry["second"]["m_Texture"];
                    if (texRef["m_FileID"].AsInt == 0 && texRef["m_PathID"].AsLong == texturePathId)
                    {
                        referencesAtlas = true;
                    }
                    break;
                }

                if (!referencesAtlas)
                    continue;

                MaterialPatcher.PatchForReplacement(am, inst, matInfo, plan);
                count++;
            }
            catch
            {
            }
        }

        return count;
    }

    private static bool ParseBoolFlag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetAssetName(AssetsFileInstance inst, AssetFileInfo info)
    {
        var file = inst.file;
        if (!file.Metadata.TypeTreeEnabled)
            return null;

        ushort scriptId = info.GetScriptIndex(file);
        var ttType = file.Metadata.FindTypeTreeTypeByID(info.TypeId, scriptId);
        if (ttType == null || ttType.Nodes.Count == 0)
            return null;

        var reader = file.Reader;
        if (ttType.Nodes.Count > 1 &&
            ttType.Nodes[1].GetNameString(ttType.StringBufferBytes) == "m_Name")
        {
            reader.Position = info.GetAbsoluteByteOffset(file);
            return reader.ReadCountStringInt32();
        }

        var typeName = ttType.Nodes[0].GetTypeString(ttType.StringBufferBytes);
        if (typeName == "GameObject")
        {
            reader.Position = info.GetAbsoluteByteOffset(file);
            int size = reader.ReadInt32();
            int componentSize = file.Header.Version > 0x10 ? 0x0c : 0x10;
            reader.Position += size * componentSize;
            reader.Position += 0x04;
            return reader.ReadCountStringInt32();
        }

        if (typeName == "MonoBehaviour")
        {
            reader.Position = info.GetAbsoluteByteOffset(file);
            reader.Position += 0x1c;
            var name = reader.ReadCountStringInt32();
            return string.IsNullOrEmpty(name) ? null : name;
        }

        return null;
    }

    private static string? ResolveExternalCabPath(AssetsFileInstance inst, int fileId)
    {
        if (fileId <= 0)
            return null;

        var externals = inst.file.Metadata.Externals;
        int idx = fileId - 1;
        if (idx < 0 || idx >= externals.Count)
            return null;

        var pathName = externals[idx].PathName;
        if (string.IsNullOrWhiteSpace(pathName))
            return null;

        if (pathName.Contains('/'))
        {
            var parts = pathName.Split('/');
            return parts[^1];
        }

        return pathName;
    }

    private static string? FindAssetFile(List<string> assetFiles, string fileName)
    {
        var match = assetFiles.FirstOrDefault(f =>
            Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (match != null)
            return match;

        var parts = fileName.Split('/');
        var lastPart = parts[0];
        return assetFiles.FirstOrDefault(f =>
            Path.GetFileName(f).Equals(lastPart, StringComparison.OrdinalIgnoreCase));
    }
}
