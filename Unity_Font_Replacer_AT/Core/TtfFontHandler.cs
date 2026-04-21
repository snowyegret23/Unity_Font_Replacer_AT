using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace UnityFontReplacer.Core;

public static class TtfFontHandler
{
    /// <summary>
    /// Unity Font 에셋에서 TTF 데이터를 읽는다.
    /// </summary>
    public static byte[]? ReadFontData(AssetTypeValueField baseField)
    {
        var fontData = ResolveFontDataField(baseField);
        if (fontData == null || fontData.IsDummy)
            return null;

        if (fontData.TemplateField.IsArray &&
            fontData.TemplateField.ValueType == AssetValueType.ByteArray &&
            fontData.Value != null)
        {
            return fontData.AsByteArray;
        }

        if (fontData.TemplateField.IsArray)
        {
            if (fontData.Children.Count == 0)
                return Array.Empty<byte>();

            var data = new byte[fontData.Children.Count];
            for (int i = 0; i < fontData.Children.Count; i++)
            {
                var child = fontData.Children[i];
                data[i] = child.TemplateField.ValueType switch
                {
                    AssetValueType.Int8 => unchecked((byte)child.AsSByte),
                    AssetValueType.UInt8 => child.AsByte,
                    _ => throw new InvalidOperationException(
                        $"Unsupported font data element type: {child.TemplateField.ValueType}"),
                };
            }

            return data;
        }

        return null;
    }

    /// <summary>
    /// Unity Font 에셋의 이름을 읽는다.
    /// </summary>
    public static string ReadFontName(AssetTypeValueField baseField)
    {
        return baseField["m_Name"].AsString;
    }

    /// <summary>
    /// Unity Font 에셋에 새 TTF 데이터를 쓴다.
    /// </summary>
    public static void ReplaceFontData(
        AssetsManager am,
        AssetsFileInstance inst,
        AssetFileInfo info,
        byte[] newTtfData)
    {
        var baseField = am.GetBaseField(inst, info);
        var fontData = ResolveFontDataField(baseField);
        if (fontData == null || fontData.IsDummy)
            throw new InvalidOperationException("Font asset does not expose m_FontData.");

        WriteFontData(fontData, newTtfData);

        // 수정사항 적용
        info.SetNewData(baseField);
    }

    /// <summary>
    /// TTF 파일에서 바이트 데이터를 로드한다.
    /// </summary>
    public static byte[] LoadTtfFile(string ttfPath)
    {
        var resolvedPath = ResolveTtfPath(ttfPath);
        if (resolvedPath == null)
            throw new FileNotFoundException($"TTF file not found: {ttfPath}");

        return File.ReadAllBytes(resolvedPath);
    }

    /// <summary>
    /// TTF/OTF 입력값을 실제 파일 경로로 해석한다.
    /// 직접 경로 -> 상대 경로(CWD/EXE) -> KR_ASSETS 하위 순으로 탐색한다.
    /// </summary>
    public static string? ResolveTtfPath(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;

        source = source.Trim().Trim('"');

        if (File.Exists(source))
            return Path.GetFullPath(source);

        if (Directory.Exists(source))
            return FindFirstFontInDirectory(source);

        var candidateNames = BuildCandidateNames(source);
        foreach (var baseDir in EnumerateSearchRoots())
        {
            foreach (var candidateName in candidateNames)
            {
                var candidatePath = Path.Combine(baseDir, candidateName);
                if (File.Exists(candidatePath))
                    return Path.GetFullPath(candidatePath);

                if (Directory.Exists(candidatePath))
                {
                    var nestedFont = FindFirstFontInDirectory(candidatePath);
                    if (nestedFont != null)
                        return nestedFont;
                }
            }
        }

        return null;
    }

    private static AssetTypeValueField? ResolveFontDataField(AssetTypeValueField baseField)
    {
        var fontData = baseField["m_FontData"];
        if (fontData.IsDummy)
            return null;

        if (fontData.TemplateField.IsArray)
            return fontData;

        var arrayField = fontData["Array"];
        return arrayField.IsDummy ? fontData : arrayField;
    }

    private static void WriteFontData(AssetTypeValueField fontDataField, byte[] newTtfData)
    {
        if (!fontDataField.TemplateField.IsArray)
            throw new InvalidOperationException("m_FontData is not an array field.");

        if (fontDataField.TemplateField.ValueType == AssetValueType.ByteArray)
        {
            if (fontDataField.Value == null)
                fontDataField.Value = new AssetTypeValue(AssetValueType.ByteArray, newTtfData);
            else
                fontDataField.AsByteArray = newTtfData;
            fontDataField.Children = [];
            return;
        }

        var elementTemplate = fontDataField.TemplateField.Children.Count > 1
            ? fontDataField.TemplateField.Children[1]
            : null;
        if (elementTemplate == null)
            throw new InvalidOperationException("m_FontData array element template is missing.");

        if (elementTemplate.ValueType != AssetValueType.Int8 &&
            elementTemplate.ValueType != AssetValueType.UInt8)
        {
            throw new InvalidOperationException(
                $"Unsupported m_FontData element type: {elementTemplate.ValueType}");
        }

        // Some Font assets store bytes as vector<Array<char>>. Swap in a compact byte-array field
        // so large TTF payloads do not require millions of per-byte child objects.
        var byteArrayTemplate = fontDataField.TemplateField.Clone();
        byteArrayTemplate.ValueType = AssetValueType.ByteArray;

        fontDataField.TemplateField = byteArrayTemplate;
        fontDataField.Value = new AssetTypeValue(AssetValueType.ByteArray, newTtfData);
        fontDataField.Children = [];
    }

    private static IEnumerable<string> BuildCandidateNames(string source)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                seen.Add(value);
        }

        Add(source);
        if (!source.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase))
            Add(source + ".ttf");
        if (!source.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            Add(source + ".otf");

        var fileName = Path.GetFileName(source);
        if (!string.Equals(fileName, source, StringComparison.Ordinal))
        {
            Add(fileName);
            if (!fileName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase))
                Add(fileName + ".ttf");
            if (!fileName.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
                Add(fileName + ".otf");
        }

        return seen;
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppDomain.CurrentDomain.BaseDirectory,
                     Path.Combine(Directory.GetCurrentDirectory(), "KR_ASSETS"),
                     Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KR_ASSETS"),
                 })
        {
            if (Directory.Exists(root) && seen.Add(root))
                yield return root;
        }
    }

    private static string? FindFirstFontInDirectory(string dir)
    {
        return Directory
            .EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path =>
                path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase));
    }
}
