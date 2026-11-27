using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace UnityFontReplacer.Core;

public class AssetsContext : IDisposable
{
    public AssetsManager Manager { get; }
    public string DataPath { get; }
    public string? ManagedPath { get; }

    private bool _classPackageLoaded;
    private bool _disposed;

    public AssetsContext(string dataPath, string? managedPath)
    {
        DataPath = dataPath;
        ManagedPath = managedPath;

        Manager = new AssetsManager();
        Manager.UseTemplateFieldCache = true;
        Manager.UseMonoTemplateFieldCache = true;
        Manager.UseQuickLookup = true;
    }

    public void LoadClassPackage(string? tpkPath = null)
    {
        tpkPath ??= FindClassPackage();
        tpkPath ??= ClassDataDownloader.EnsureClassData();

        Manager.LoadClassPackage(tpkPath);
        _classPackageLoaded = true;
    }

    public void LoadClassDatabase(string engineVersion)
    {
        if (!_classPackageLoaded)
            LoadClassPackage();

        Manager.LoadClassDatabaseFromPackage(engineVersion);
    }

    private static string? FindClassPackage()
    {
        var candidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "classdata.tpk"),
            Path.Combine(Directory.GetCurrentDirectory(), "classdata.tpk"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public void SetupMonoCecil()
    {
        if (ManagedPath != null && Directory.Exists(ManagedPath))
        {
            Manager.MonoTempGenerator = new MonoCecilTempGenerator(ManagedPath);
        }
    }

    public AssetsFileInstance LoadAssetsFile(string path)
    {
        return Manager.LoadAssetsFile(path, loadDeps: true);
    }

    public BundleFileInstance LoadBundleFile(string path)
    {
        BundleProcessor.EnsureSupportedBundle(path);
        return Manager.LoadBundleFile(path);
    }

    public AssetsFileInstance LoadAssetsFileFromBundle(BundleFileInstance bundle, int index = 0)
    {
        return Manager.LoadAssetsFileFromBundle(bundle, index);
    }

    internal static void ReleaseLoadedFiles(AssetsManager manager)
    {
        // UnloadAll also disposes the MonoCecil resolver. Keep it alive between files.
        manager.UnloadAllAssetsFiles(clearCache: true);
        manager.UnloadAllBundleFiles();
    }

    public string? DetectEngineVersion()
    {
        // globalgamemanagers에서 버전 추출 시도
        var ggmCandidates = new[]
        {
            Path.Combine(DataPath, "globalgamemanagers"),
            Path.Combine(DataPath, "globalgamemanagers.assets"),
            Path.Combine(DataPath, "data.unity3d"),
        };

        foreach (var ggmPath in ggmCandidates)
        {
            if (!File.Exists(ggmPath))
                continue;

            try
            {
                if (FontScanner.IsBundleFile(ggmPath))
                {
                    var bundle = Manager.LoadBundleFile(ggmPath);
                    var dirInfos = bundle.file.BlockAndDirInfo.DirectoryInfos;
                    for (int i = 0; i < dirInfos.Count; i++)
                    {
                        if (!dirInfos[i].IsSerialized)
                            continue;

                        var inst = Manager.LoadAssetsFileFromBundle(bundle, i);
                        var version = inst.file.Metadata.UnityVersion;
                        if (!string.IsNullOrEmpty(version))
                            return version;
                    }
                }
                else
                {
                    var inst = Manager.LoadAssetsFile(ggmPath, loadDeps: false);
                    var version = inst.file.Metadata.UnityVersion;
                    if (!string.IsNullOrEmpty(version))
                        return version;
                }
            }
            catch
            {
                // 다음 후보 시도
            }
            finally
            {
                ReleaseLoadedFiles(Manager);
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Manager.UnloadAll(true);
    }
}
