using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace UnityFontReplacer.Core;

/// <summary>Updates compact local Addressables catalogs without moving record offsets.</summary>
internal static class AddressablesCatalog
{
    private const int EntryBytes = 7 * sizeof(int);
    private const byte JsonObjectRecord = 7;
    private const string RuntimePrefix = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/";
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly UnicodeEncoding Utf16 = new(false, false, true);

    internal sealed record CatalogUpdate(string CatalogPath, byte[] Bytes);
    private sealed record Options(int Start, int Length, string Text, JsonElement Value, string[] InternalIds);
    private readonly record struct Slot(int Start, int Length);

    /// <summary>
    /// Prepare all writes before changing any catalog. The caller applies them in its
    /// existing replacement transaction, together with the bundle writes.
    /// </summary>
    internal static IReadOnlyList<CatalogUpdate> PlanCatalogUpdates(
        string dataPath, IEnumerable<(string SourcePath, string SavedPath)> savedBundles)
    {
        var bundleSizes = new Dictionary<string, long>(Names);
        foreach (var (source, saved) in savedBundles)
        {
            string relative = Path.GetRelativePath(dataPath, source).Replace('\\', '/');
            if (!relative.StartsWith("StreamingAssets/aa/", StringComparison.OrdinalIgnoreCase) ||
                !HasBundleSignature(saved))
                continue;

            string bundlePath = relative["StreamingAssets/aa/".Length..];
            if (!IsSimpleRelativePath(bundlePath) ||
                !bundleSizes.TryAdd(bundlePath, new FileInfo(saved).Length))
                throw new InvalidDataException($"Invalid or ambiguous modified Addressables path: {bundlePath}");
        }

        if (bundleSizes.Count == 0)
            return [];

        var updates = new List<CatalogUpdate>();
        var remaining = new HashSet<string>(bundleSizes.Keys, Names);
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (string path in Directory.EnumerateFiles(dataPath, "*", enumerationOptions)
                     .Where(path => Names.Equals(Path.GetFileName(path), "catalog.json"))
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            string? settings = Directory.EnumerateFiles(Path.GetDirectoryName(path)!)
                .FirstOrDefault(value => Names.Equals(Path.GetFileName(value), "settings.json"));
            if (settings is null)
                continue;

            byte[] original = File.ReadAllBytes(path);
            var (bytes, paths) = Patch(original, bundleSizes, () => ReadBuildTarget(settings));
            remaining.ExceptWith(paths);
            if (!original.AsSpan().SequenceEqual(bytes))
                updates.Add(new(path, bytes));
        }
        if (remaining.Count != 0)
            throw new InvalidDataException("No supported local Addressables catalog records for: " +
                                           string.Join(", ", remaining.OrderBy(name => name, Names)));
        return updates;
    }

    // Bundle keys are normalized paths relative to StreamingAssets/aa.
    internal static (byte[] Bytes, string[] Matched) Patch(
        byte[] original, IReadOnlyDictionary<string, long> bundleSizes, Func<string> getBuildTarget)
    {
        if (bundleSizes.Count == 0)
            return (original, []);

        var (extra, extraSlot, entries, ids) = ReadCatalog(original);
        var matchedPaths = new HashSet<string>(Names);
        var expected = new List<(Options Record, long Size)>();
        bool buildTargetChecked = false;
        foreach (Options record in ReadOptions(entries, extra, ids))
        {
            // Match the path the local provider loads, not m_BundleName or the basename.
            string?[] suffixes = record.InternalIds.Select(GetLocalBundlePath).ToArray();
            string? target = Array.Find(suffixes, path => path is not null && bundleSizes.ContainsKey(path));
            if (target is null)
                continue;
            long size = bundleSizes[target];
            if (ReadFlag(record.Value, "m_UseUnityWebRequestForLocalBundles") ||
                ReadFlag(record.Value, "m_UseUWRForLocalBundles"))
                throw new InvalidDataException("Addressables bundle uses UnityWebRequest, not the local file provider.");

            // Changing one options record changes it for every location that references it.
            foreach (string? path in suffixes)
            {
                if (path is null)
                    throw new InvalidDataException("One Addressables record is shared with an unsupported internal ID.");
                if (!bundleSizes.TryGetValue(path, out long pathSize))
                    throw new InvalidDataException("One Addressables record is shared by modified and unmodified paths.");
                if (size != pathSize)
                    throw new InvalidDataException("One Addressables record matches bundles of different sizes.");
                matchedPaths.Add(path);
            }

            if (!buildTargetChecked)
            {
                if (!getBuildTarget().Trim().StartsWith("Standalone", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Addressables updates require a local Standalone build target.");
                buildTargetChecked = true;
            }

            byte[] text = Utf8.GetBytes(record.Text);
            Slot crc = FindSlot(text, "m_Crc", JsonTokenType.Number);
            Slot bundleSize = FindSlot(text, "m_BundleSize", JsonTokenType.Number);
            // The Unity local LoadFromFile path does not use the catalog hash as
            // a content checksum. Match the existing Python mod workflow only
            // after proving that local-file contract.
            byte[] changed = ReplaceSlots(text,
                (crc, "0"u8.ToArray()), (bundleSize, Utf8.GetBytes(size.ToString(CultureInfo.InvariantCulture))));
            byte[] wide = Utf16.GetBytes(Utf8.GetString(changed));
            if (wide.Length > record.Length)
                throw new InvalidDataException("Updated Addressables options do not fit the existing record; preserve bundle compression.");
            wide.CopyTo(extra, record.Start);
            for (int pad = record.Start + wide.Length; pad < record.Start + record.Length; pad += 2)
            {
                extra[pad] = 0x20;
                extra[pad + 1] = 0;
            }
            expected.Add((record, size));
        }

        if (matchedPaths.Count == 0)
            return (original, []);
        byte[] base64 = Utf8.GetBytes(Convert.ToBase64String(extra));
        if (base64.Length != extraSlot.Length)
            throw new InvalidDataException("Unexpected Addressables extra data encoding or length.");
        byte[] patched = (byte[])original.Clone();
        base64.CopyTo(patched, extraSlot.Start);

        var (checkExtra, _, checkEntries, checkIds) = ReadCatalog(patched);
        var verified = ReadOptions(checkEntries, checkExtra, checkIds).ToDictionary(record => record.Start);
        foreach (var (record, size) in expected)
        {
            if (!verified.TryGetValue(record.Start, out var actual) || actual.Length != record.Length ||
                !actual.Value.GetProperty("m_Crc").TryGetInt64(out long crc) || crc != 0 ||
                !actual.Value.GetProperty("m_BundleSize").TryGetInt64(out long length) || length != size)
                throw new InvalidDataException("Addressables options verification failed.");
        }
        return (patched, matchedPaths.OrderBy(path => path, Names).ToArray());
    }

    private static string? GetLocalBundlePath(string raw)
    {
        string id = raw.Trim().Replace('\\', '/');
        if (!id.StartsWith(RuntimePrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        string suffix = id[RuntimePrefix.Length..];
        return IsSimpleRelativePath(suffix) ? suffix : null;
    }

    private static bool IsSimpleRelativePath(string path) =>
        path.IndexOfAny([':', '?', '#', '%', '{', '}']) < 0 &&
        !path.Split('/').Any(part => part.Length == 0 || part is "." or "..");

    private static bool ReadFlag(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out JsonElement result))
            return false;
        return result.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when result.TryGetInt32(out int number) && number is 0 or 1 => number == 1,
            JsonValueKind.String when result.GetString()?.Trim() is string flag =>
                flag.Equals("true", StringComparison.OrdinalIgnoreCase) || flag == "1" ? true :
                flag.Equals("false", StringComparison.OrdinalIgnoreCase) || flag == "0" ? false :
                throw new InvalidDataException($"Invalid Addressables option '{name}'."),
            _ => throw new InvalidDataException($"Invalid Addressables option '{name}'."),
        };
    }

    private static (byte[] Extra, Slot ExtraSlot, byte[] Entries, string[] Ids) ReadCatalog(byte[] bytes)
    {
        int bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        var plain = bytes.AsMemory(bom);
        using var document = JsonDocument.Parse(plain);
        var root = document.RootElement;
        var slot = FindSlot(plain.Span, "m_ExtraDataString", JsonTokenType.String);
        byte[] extra = Convert.FromBase64String(root.GetProperty("m_ExtraDataString").GetString()!);
        byte[] entries = Convert.FromBase64String(root.GetProperty("m_EntryDataString").GetString()!);
        string[] ids = root.TryGetProperty("m_InternalIds", out var element) && element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString()! : "").ToArray()
            : [];
        return (extra, new(slot.Start + bom, slot.Length), entries, ids);
    }

    private static IEnumerable<Options> ReadOptions(byte[] entries, byte[] extra, string[] ids)
    {
        int count = ReadInt(entries, 0);
        if (count < 0 || count > (entries.Length - sizeof(int)) / EntryBytes)
            throw new InvalidDataException("Invalid compact Addressables entry count.");
        var references = new Dictionary<int, HashSet<int>>();
        for (int index = 0; index < count; ++index)
        {
            int start = sizeof(int) + index * EntryBytes;
            int offset = ReadInt(entries, start + 4 * sizeof(int));
            if (offset < 0) continue;
            if (!references.TryGetValue(offset, out var set))
                references.Add(offset, set = []);
            set.Add(ReadInt(entries, start));
        }

        var occupiedUntil = 0;
        foreach (var (offset, indices) in references.OrderBy(pair => pair.Key))
        {
            if (offset < occupiedUntil || offset >= extra.Length)
                throw new InvalidDataException("Overlapping or out-of-range Addressables record.");
            if (extra[offset] != JsonObjectRecord) continue;
            int cursor = offset + 1;
            _ = ReadTinyString(extra, ref cursor); // Assembly name.
            string className = ReadTinyString(extra, ref cursor);
            int length = ReadInt(extra, cursor);
            cursor += sizeof(int);
            if (length < 0 || (length & 1) != 0 || length > extra.Length - cursor)
                throw new InvalidDataException("Invalid Addressables UTF-16 record size.");
            occupiedUntil = cursor + length;
            string text = Utf16.GetString(extra, cursor, length);
            using var doc = JsonDocument.Parse(text);
            var value = doc.RootElement;
            if (value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Addressables JSON record is not an object.");
            if (!className.EndsWith("AssetBundleRequestOptions", StringComparison.Ordinal) &&
                !(value.TryGetProperty("m_BundleName", out _) && value.TryGetProperty("m_Crc", out _)))
                continue;
            yield return new(cursor, length, text, value.Clone(),
                indices.Order().Select(index => index >= 0 && index < ids.Length ? ids[index] : "").ToArray());
        }
    }

    private static int ReadInt(byte[] bytes, int offset)
    {
        if (offset < 0 || offset > bytes.Length - sizeof(int))
            throw new InvalidDataException("Truncated compact Addressables record.");
        return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, sizeof(int)));
    }

    private static string ReadTinyString(byte[] data, ref int cursor)
    {
        if (cursor >= data.Length)
            throw new InvalidDataException("Truncated Addressables record name.");
        int length = data[cursor++];
        if (length > data.Length - cursor)
            throw new InvalidDataException("Truncated Addressables record name.");
        string name = Utf8.GetString(data, cursor, length);
        cursor += length;
        return name;
    }

    private static Slot FindSlot(ReadOnlySpan<byte> bytes, string name, JsonTokenType tokenType)
    {
        var reader = new Utf8JsonReader(bytes);
        Slot? result = null;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !reader.ValueTextEquals(name))
                continue;
            if (result.HasValue || !reader.Read() || reader.TokenType != tokenType)
                throw new InvalidDataException($"Invalid or duplicate Addressables field '{name}'.");
            int start = (int)reader.TokenStartIndex;
            int length = checked((int)(reader.BytesConsumed - start));
            if (tokenType == JsonTokenType.String)
            {
                if (reader.ValueIsEscaped)
                    throw new InvalidDataException($"Escaped compact Addressables field '{name}' is unsupported.");
                ++start;
                length -= 2;
            }
            result = new(start, length);
        }
        return result ?? throw new InvalidDataException($"Missing Addressables field '{name}'.");
    }

    private static byte[] ReplaceSlots(byte[] original, params (Slot Slot, byte[] Bytes)[] changes)
    {
        using var result = new MemoryStream();
        int cursor = 0;
        foreach (var (slot, replacement) in changes.OrderBy(x => x.Slot.Start))
        {
            result.Write(original, cursor, slot.Start - cursor);
            result.Write(replacement);
            cursor = slot.Start + slot.Length;
        }
        result.Write(original, cursor, original.Length - cursor);
        return result.ToArray();
    }

    private static string ReadBuildTarget(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        using var document = JsonDocument.Parse(bytes.AsMemory(bom));
        return ReadString(document.RootElement, "m_buildTarget") is { Length: > 0 } target ? target :
            ReadString(document.RootElement, "m_BuildTarget");
    }

    private static string ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()! : "";

    private static bool HasBundleSignature(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[8];
        return stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) == magic.Length &&
               (magic.SequenceEqual("UnityFS\0"u8) || magic.SequenceEqual("UnityRaw"u8) || magic.SequenceEqual("UnityWeb"u8));
    }
}
