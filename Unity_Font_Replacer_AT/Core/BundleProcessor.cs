using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace UnityFontReplacer.Core;

public static class BundleProcessor
{
    /// <summary>
    /// 번들 내 수정된 에셋 파일들을 적용하고 번들을 저장한다.
    /// </summary>
    public static void SaveBundle(
        BundleFileInstance bunInst,
        List<(int dirIndex, AssetsFileInstance fileInst)> modifiedFiles,
        string? outputPath = null,
        bool recompress = false)
    {
        SaveBundle(bunInst, modifiedFiles, outputPath, transaction: null, recompress: recompress);
    }

    internal static void SaveBundle(
        BundleFileInstance bunInst, List<(int dirIndex, AssetsFileInstance fileInst)> modifiedFiles,
        string? outputPath, ReplacementTransaction? transaction, bool recompress = false)
    {
        var dirInfos = bunInst.file.BlockAndDirInfo.DirectoryInfos;
        var manifests = modifiedFiles.ToDictionary(pair => dirInfos[pair.dirIndex].Name,
            pair => SavedAssetValidation.Capture(pair.fileInst.file), StringComparer.Ordinal);
        foreach (var (dirIndex, fileInst) in modifiedFiles)
            dirInfos[dirIndex].SetNewData(fileInst.file);

        var targetPath = outputPath ?? bunInst.path;
        var isInPlace = Path.GetFullPath(targetPath).Equals(Path.GetFullPath(bunInst.path),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        ReplacementTransaction.WriteValidated(targetPath,
            path => WriteBundle(bunInst, path, recompress), path =>
            {
                var manager = new AssetsManager();
                try
                {
                    var saved = LoadBundle(manager, path);
                    var entries = saved.file.BlockAndDirInfo.DirectoryInfos;
                    if (!entries.Select(entry => entry.Name).SequenceEqual(dirInfos.Select(entry => entry.Name)))
                        throw new InvalidDataException("Saved bundle directory does not match the source.");
                    for (int i = 0; i < entries.Count; i++)
                    {
                        if (manifests.TryGetValue(entries[i].Name, out var manifest))
                            SavedAssetValidation.Verify(manager.LoadAssetsFileFromBundle(saved, i).file, manifest);
                    }
                }
                finally { manager.UnloadAll(true); }
            }, isInPlace ? () => CloseBundleReaders(bunInst) : null, transaction);
    }

    internal static BundleFileInstance LoadBundle(AssetsManager manager, string path, bool unpackIfPacked = true) =>
        UnityFsV9.Load(manager, path, unpackIfPacked);

    // Kept until all callers use LoadBundle. It rejects versions newer than the
    // v9 adapter rather than passing unknown formats to AssetsTools.NET.
    internal static void EnsureSupportedBundle(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[12];
        int read = stream.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
        if (read == prefix.Length && prefix[..8].SequenceEqual("UnityFS\0"u8) &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(prefix[8..]) > 9)
            throw new NotSupportedException("UnityFS versions newer than 9 are not supported.");
    }

    private static void WriteBundle(BundleFileInstance bunInst, string path, bool recompress)
    {
        if (UnityFsV9.TryWrite(bunInst, path, recompress))
            return;

        using var writer = new AssetsFileWriter(path);

        if (recompress)
        {
            bunInst.file.Pack(writer, AssetBundleCompressionType.LZ4);
        }
        else
        {
            bunInst.file.Write(writer);
        }
    }

    /// <summary>
    /// 번들과 내부 에셋 파일의 모든 리더를 닫는다.
    /// in-place 저장 시 원본 파일 핸들 해제를 위해 필요.
    /// </summary>
    private static void CloseBundleReaders(BundleFileInstance bunInst)
    {
        // 내부 에셋 파일 리더 닫기
        foreach (var assetsInst in bunInst.loadedAssetsFiles)
        {
            try { assetsInst.file.Reader?.Close(); }
            catch { }
        }

        // 번들 데이터 리더 닫기
        try { bunInst.file.DataReader?.Close(); }
        catch { }

        // 번들 파일 리더 닫기
        try { bunInst.file.Reader?.Close(); }
        catch { }

        // BundleStream (원본 FileStream) 닫기
        try { bunInst.BundleStream?.Close(); }
        catch { }
    }
}
