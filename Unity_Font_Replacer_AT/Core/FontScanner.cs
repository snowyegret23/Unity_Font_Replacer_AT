using AssetsTools.NET;
using AssetsTools.NET.Extra;
using UnityFontReplacer.CLI;
using UnityFontReplacer.Models;

namespace UnityFontReplacer.Core;

public class FontScanner
{
    private readonly AssetsContext _ctx;

    // 공유 리소스
    private string? _tpkPath;
    private string? _engineVersion;

    public FontScanner(AssetsContext ctx)
    {
        _ctx = ctx;
    }

    public ScanResult ScanAll(List<string> assetFiles, bool detectPs5Swizzle = false, int maxWorkers = 1)
    {
        if (detectPs5Swizzle)
            throw new NotSupportedException("PS5 swizzle detection is not implemented in AT. Use the Python edition.");
        if (maxWorkers < 1)
            throw new ArgumentOutOfRangeException(nameof(maxWorkers), "At least one scan worker is required.");

        var result = new ScanResult { EngineVersion = _ctx.DetectEngineVersion() };
        _engineVersion = result.EngineVersion;
        if (assetFiles.Count == 0)
            return result;
        _tpkPath = ClassDataDownloader.EnsureClassData();

        using var status = new StatusBar(Math.Min(maxWorkers, assetFiles.Count));
        status.SetTotal(assetFiles.Count);
        status.Log(Strings.Get("scan_start"));
        var results = ScanInOrder<AssetsManager, List<FontEntry>>(
            assetFiles, maxWorkers, CreateWorkerManager,
            (path, manager) =>
            {
                var fileName = Path.GetFileName(path);
                try
                {
                    if (ShouldSkipFile(path, fileName))
                        return [];
                    var entries = ScanFileIsolated(path, fileName, manager);
                    foreach (var entry in entries)
                        status.Log($"{entry.Type}: {entry.Name} ({TruncMiddle(entry.File, 50)} #{entry.PathId})");
                    return entries;
                }
                catch (Exception ex)
                {
                    status.Log($"[WARN] {fileName}: {ex.Message}");
                    return [];
                }
                finally { status.Increment(fileName); }
            },
            manager => manager.UnloadAll(true));
        foreach (var entries in results)
            result.Entries.AddRange(entries);
        status.Clear();
        Console.WriteLine(Strings.Get("scan_complete", result.TtfCount, result.TmpCount));
        return result;
    }

    // Keep result order independent of which worker finishes first. Each worker owns its own
    // MonoCecil resolver; AssetsTools managers/readers are not shared between workers.
    internal static TResult[] ScanInOrder<TWorker, TResult>(
        IReadOnlyList<string> paths, int workers, Func<TWorker> create,
        Func<string, TWorker, TResult> scan, Action<TWorker> close)
    {
        var results = new TResult[paths.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = workers };
        Parallel.For(0, paths.Count, options, create, (index, _, worker) =>
        {
            results[index] = scan(paths[index], worker);
            return worker;
        }, close);
        return results;
    }

    private AssetsManager CreateWorkerManager()
    {
        var manager = new AssetsManager { UseTemplateFieldCache = true, UseQuickLookup = true };
        if (_tpkPath != null && _engineVersion != null)
        {
            manager.LoadClassPackage(_tpkPath);
            manager.LoadClassDatabaseFromPackage(_engineVersion);
        }
        if (_ctx.ManagedPath is { } managed && Directory.Exists(managed))
            manager.MonoTempGenerator = new MonoCecilTempGenerator(managed);
        return manager;
    }

    private static List<FontEntry> ScanFileIsolated(string filePath, string displayName, AssetsManager manager)
    {
        var entries = new List<FontEntry>();
        try
        {
            if (IsBundleFile(filePath))
            {
                BundleProcessor.EnsureSupportedBundle(filePath);
                ScanBundle(manager, filePath, displayName, entries);
            }
            else
                ScanAssetsFile(manager, filePath, displayName, entries);
        }
        finally { AssetsContext.ReleaseLoadedFiles(manager); }

        return entries;
    }

    private static void ScanBundle(AssetsManager am, string filePath, string displayName, List<FontEntry> entries)
    {
        // unpackIfPacked: false → LZ4는 스트리밍 읽기, 메모리에 전체를 풀지 않음
        var bundle = am.LoadBundleFile(filePath, unpackIfPacked: false);
        var dirInfos = bundle.file.BlockAndDirInfo.DirectoryInfos;

        // 번들 내 모든 직렬화 파일을 먼저 로드 (MonoScript 크로스 참조 해석용)
        var instances = new List<(int idx, AssetsFileInstance inst)>();
        for (int i = 0; i < dirInfos.Count; i++)
        {
            if (!dirInfos[i].IsSerialized) continue;
            try
            {
                var inst = am.LoadAssetsFileFromBundle(bundle, i);
                instances.Add((i, inst));
            }
            catch { }
        }

        foreach (var (idx, inst) in instances)
        {
            try { ScanInstance(am, inst, $"{displayName}/{dirInfos[idx].Name}", entries); }
            catch { }
        }
    }

    private static void ScanAssetsFile(AssetsManager am, string filePath, string displayName, List<FontEntry> entries)
    {
        try
        {
            var inst = am.LoadAssetsFile(filePath, loadDeps: false);
            ScanInstance(am, inst, displayName, entries);
        }
        catch { }
    }

    private static void ScanInstance(AssetsManager am, AssetsFileInstance inst, string displayName, List<FontEntry> entries, bool verbose = false)
    {
        var file = inst.file;
        var assetsName = Path.GetFileName(inst.path);

        var fontInfos = file.GetAssetsOfType(AssetClassID.Font);
        var mbInfos = file.GetAssetsOfType(AssetClassID.MonoBehaviour);

        if (verbose)
            Console.Error.WriteLine($"  [DIAG] {displayName}: Font={fontInfos.Count}, MB={mbInfos.Count}");

        if (fontInfos.Count == 0 && mbInfos.Count == 0) return;

        foreach (var info in fontInfos)
        {
            try
            {
                var name = AssetHelper.GetAssetNameFast(inst.file, am.ClassDatabase, info);
                if (string.IsNullOrEmpty(name))
                {
                    var bf = am.GetBaseField(inst, info);
                    name = bf["m_Name"].AsString;
                }

                entries.Add(new FontEntry
                {
                    File = displayName,
                    AssetsName = assetsName,
                    PathId = info.PathId,
                    Type = FontType.TTF,
                    Name = name ?? $"Font_{info.PathId}",
                });
            }
            catch (Exception ex) { if (verbose) Console.Error.WriteLine($"  [DIAG] Font read error: {ex.Message}"); }
        }

        foreach (var info in mbInfos)
        {
            try { ScanMonoBehaviour(am, inst, info, displayName, assetsName, entries, verbose); }
            catch (Exception ex) { if (verbose) Console.Error.WriteLine($"  [DIAG] MB scan error #{info.PathId}: {ex.Message}"); }
        }
    }

    private static void ScanMonoBehaviour(
        AssetsManager am, AssetsFileInstance inst, AssetFileInfo info,
        string displayName, string assetsName, List<FontEntry> entries, bool verbose = false)
    {
        // 1차: MonoScript 클래스명으로 판별
        bool isTmp = false;
        try
        {
            var bf = am.GetBaseField(inst, info, AssetReadFlags.SkipMonoBehaviourFields);
            var scriptPPtr = bf["m_Script"];
            if (verbose)
                Console.Error.WriteLine($"  [DIAG] MB #{info.PathId}: m_Script FileId={scriptPPtr["m_FileID"].AsInt}, PathId={scriptPPtr["m_PathID"].AsLong}");

            var scriptExt = am.GetExtAsset(inst, scriptPPtr);
            if (scriptExt.baseField != null)
            {
                var className = scriptExt.baseField["m_ClassName"].AsString;
                if (verbose) Console.Error.WriteLine($"  [DIAG]   -> className={className}");
                if (className == "TMP_FontAsset")
                    isTmp = true;
                else
                    return;
            }
            else
            {
                if (verbose) Console.Error.WriteLine($"  [DIAG]   -> MonoScript not resolved (external?)");
            }
        }
        catch (Exception ex)
        {
            if (verbose) Console.Error.WriteLine($"  [DIAG]   -> MonoScript resolve error: {ex.Message}");
        }

        AssetTypeValueField fullField;
        try
        {
            fullField = am.GetBaseField(inst, info);
        }
        catch (Exception ex)
        {
            if (verbose) Console.Error.WriteLine($"  [DIAG]   -> GetBaseField failed: {ex.Message}");
            return;
        }

        var schemaInfo = TmpSchemaDetector.Inspect(
            fullField,
            inst.file.Metadata.UnityVersion);

        if (!isTmp && !schemaInfo.IsTmp)
        {
            if (verbose) Console.Error.WriteLine("  [DIAG]   -> field inspect says not TMP");
            return;
        }

        if (schemaInfo.GlyphCount <= 0)
        {
            if (verbose) Console.Error.WriteLine("  [DIAG]   -> glyph count is 0, skipped");
            return;
        }

        if (schemaInfo.AtlasFileId != 0 && schemaInfo.AtlasPathId == 0)
        {
            if (verbose) Console.Error.WriteLine("  [DIAG]   -> external atlas stub only, skipped");
            return;
        }

        var schema = schemaInfo.Version;
        if (schema == TmpSchemaVersion.Unknown)
            schema = TmpSchemaVersion.New;

        string fontName = fullField["m_Name"].IsDummy
            ? $"TMP_{info.PathId}"
            : fullField["m_Name"].AsString;

        if (verbose)
        {
            Console.Error.WriteLine(
                $"  [DIAG]   -> TMP confirmed, name={fontName}, schema={schema}, glyphs={schemaInfo.GlyphCount}, atlas={schemaInfo.AtlasFileId}:{schemaInfo.AtlasPathId}");
        }

        var targetAsset = TmpFontHandler.ReadFromField(fullField);

        entries.Add(new FontEntry
        {
            File = displayName,
            AssetsName = assetsName,
            PathId = info.PathId,
            Type = FontType.SDF,
            Name = fontName,
            Schema = schema,
            GlyphCount = schemaInfo.GlyphCount,
            AtlasPadding = schemaInfo.AtlasPadding,
            AtlasPathId = schemaInfo.AtlasPathId,
            PointSize = targetAsset.FaceInfo?.PointSize ?? 0,
        });
    }

    /// <summary>
    /// .assets 파일은 헤더만 읽어서 Font/MonoBehaviour TypeId 존재 여부를 경량 확인.
    /// 번들은 풀어야 하므로 스킵 불가 → false 반환.
    /// </summary>
    private static bool ShouldSkipFile(string filePath, string fileName)
    {
        // 번들은 여기서 스킵 판단 불가 (ScanBundle 내에서 TypeId 체크)
        if (IsBundleFile(filePath))
            return false;

        // .assets 파일: 헤더만 읽어서 TypeId 확인
        try
        {
            var file = new AssetsFile();
            using var reader = new AssetsFileReader(filePath);
            file.Read(reader);

            foreach (var info in file.Metadata.AssetInfos)
            {
                if (info.TypeId == (int)AssetClassID.Font ||
                    info.TypeId == (int)AssetClassID.MonoBehaviour)
                    return false; // 폰트 가능성 있음 → 스캔
            }
            return true; // Font/MB 없음 → 스킵
        }
        catch
        {
            return false; // 읽기 실패 → 안전하게 스캔
        }
    }

    public static bool IsBundleFile(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            if (fs.Length < 8) return false;
            Span<byte> header = stackalloc byte[8];
            fs.ReadExactly(header);
            return header.StartsWith("UnityFS"u8) ||
                   header.StartsWith("UnityWeb"u8[..7]) ||
                   header.StartsWith("UnityRaw"u8[..7]);
        }
        catch { return false; }
    }

    private static string TruncMiddle(string s, int maxLen)
    {
        if (s.Length <= maxLen) return s;
        int half = (maxLen - 2) / 2;
        return s[..half] + ".." + s[^half..];
    }
}
