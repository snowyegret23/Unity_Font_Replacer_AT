using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace UnityFontReplacer.Core;

public static class SaveStrategy
{
    /// <summary>
    /// 수정된 .assets 파일을 저장한다.
    /// outputPath가 null이면 원본을 덮어쓴다 (임시파일 → rename).
    /// </summary>
    public static void SaveAssetsFile(AssetsFileInstance inst, string? outputPath = null)
    {
        SaveAssetsFile(inst, outputPath, transaction: null);
    }

    internal static void SaveAssetsFile(
        AssetsFileInstance inst, string? outputPath, ReplacementTransaction? transaction)
    {
        var targetPath = outputPath ?? inst.path;
        var isInPlace = Path.GetFullPath(targetPath).Equals(Path.GetFullPath(inst.path),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var manifest = SavedAssetValidation.Capture(inst.file);
        ReplacementTransaction.WriteValidated(targetPath, path =>
        {
            using var writer = new AssetsFileWriter(path);
            inst.file.Write(writer);
        }, path =>
        {
            using var stream = File.OpenRead(path);
            var saved = new AssetsFile();
            saved.Read(stream);
            SavedAssetValidation.Verify(saved, manifest);
        }, isInPlace ? () => CloseAssetsReaders(inst) : null, transaction);
    }

    /// <summary>
    /// --output-only 경로를 해석하여 대상 파일 경로를 생성한다.
    /// 원본 파일 경로의 상대 구조를 출력 디렉토리에 재현한다.
    /// </summary>
    public static string ResolveOutputPath(string originalPath, string dataPath, string outputDir)
    {
        var relativePath = Path.GetRelativePath(dataPath, originalPath);
        if (IsOutside(relativePath))
            throw new IOException($"Asset is outside the game data directory: {originalPath}");

        string output = Path.GetFullPath(Path.Combine(outputDir, relativePath));
        string actualRoot = ResolveDirectory(outputDir);
        string actualParent = ResolveDirectory(Path.GetDirectoryName(output)!);
        if (IsOutside(Path.GetRelativePath(actualRoot, actualParent)) ||
            !IsOutside(Path.GetRelativePath(ResolveDirectory(dataPath), actualParent)) ||
            new FileInfo(output).LinkTarget is not null)
            throw new IOException($"Output path aliases game data or leaves the output directory: {output}");
        return output;
    }

    internal static void CheckOutputRoot(string gameData, string outputDir)
    {
        if (!IsOutside(Path.GetRelativePath(ResolveDirectory(gameData), ResolveDirectory(outputDir))))
            throw new IOException("Output-only directory must be outside the game data directory.");
    }

    private static bool IsOutside(string relative) =>
        Path.IsPathRooted(relative) || relative == ".." ||
        relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
        relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

    private static string ResolveDirectory(string path)
    {
        var info = new DirectoryInfo(Path.GetFullPath(path));
        if (info.Parent is null)
            return info.FullName;
        var child = new DirectoryInfo(Path.Combine(ResolveDirectory(info.Parent.FullName), info.Name));
        return child.LinkTarget is null ? child.FullName :
            child.ResolveLinkTarget(returnFinalTarget: true)?.FullName ??
            throw new IOException($"Cannot resolve output path link: {child.FullName}");
    }

    private static void CloseAssetsReaders(AssetsFileInstance inst)
    {
        try { inst.file.Reader?.Close(); }
        catch { }

        try { inst.AssetsStream?.Close(); }
        catch { }
    }
}
