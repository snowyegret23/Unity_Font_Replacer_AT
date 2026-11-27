using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using UnityFontReplacer.Core;

internal static class AddressablesCatalogSmoke
{
    internal static void Run()
    {
        byte[] first = Record("font.bundle", 3653417969, 12345678);
        byte[] second = Record("untouched.bundle", 99, 88);
        byte[] original = Catalog(first, second);
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["FONT.BUNDLE"] = 23456789 };
        var (patched, matched) = AddressablesCatalog.Patch(original, sizes, () => "StandaloneWindows64");
        True(matched.Length == 1 && matched[0].Equals("font.bundle", StringComparison.OrdinalIgnoreCase), "patched record is reported");
        True(patched.Length == original.Length && patched.AsSpan().StartsWith(Encoding.UTF8.Preamble), "outer length and BOM");
        using var root = JsonDocument.Parse(patched.AsMemory(3));
        byte[] expanded = Convert.FromBase64String(root.RootElement.GetProperty("m_ExtraDataString").GetString()!);
        True(expanded.Length == first.Length + second.Length && expanded.AsSpan(first.Length).SequenceEqual(second),
            "unchanged following record at the same offset");
        string text = Encoding.Unicode.GetString(expanded, first.Length - (int)JsonLength(first), (int)JsonLength(first));
        using var request = JsonDocument.Parse(text);
        True(request.RootElement.GetProperty("m_Crc").GetInt64() == 0 &&
             request.RootElement.GetProperty("m_BundleSize").GetInt64() == 23456789 &&
             request.RootElement.GetProperty("m_Hash").GetString() == "same", "modified record fields");
        True(request.RootElement.GetProperty("other").GetProperty("m_Crc").GetInt64() == 567,
            "nested like-named field is untouched");
        var (same, unmatched) = AddressablesCatalog.Patch(original,
            new Dictionary<string, long> { ["missing.bundle"] = 42 },
            () => throw new Exception("Settings not needed for unrelated catalog"));
        True(same.AsSpan().SequenceEqual(original) && unmatched.Length == 0, "unrelated catalog unchanged");
        Reject(() => AddressablesCatalog.Patch(Catalog(Record("font.bundle", 10, 10, true), second), sizes,
            () => "StandaloneWindows64"));
        Reject(() => AddressablesCatalog.Patch(original, sizes, () => "Android"));
        Reject(() => AddressablesCatalog.Patch(Catalog(Record("font.bundle", 0, 1), second), sizes,
            () => "StandaloneWindows64"));

        string dir = Path.Combine(Path.GetTempPath(), "ufr-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            string data = Path.Combine(dir, "Game_Data");
            string aa = Path.Combine(data, "StreamingAssets", "aa");
            Directory.CreateDirectory(aa);
            string bundle = Path.Combine(aa, "font.bundle");
            string saved = Path.Combine(dir, "patched.bundle");
            File.WriteAllBytes(bundle, "UnityFS\0old"u8.ToArray());
            File.WriteAllBytes(saved, "UnityFS\0new size"u8.ToArray());
            string catalog = Path.Combine(aa, "catalog.json");
            File.WriteAllBytes(catalog, original);
            File.WriteAllText(Path.Combine(aa, "settings.json"), """{"m_buildTarget":"StandaloneWindows64"}""");
            var plan = AddressablesCatalog.PlanCatalogUpdates(data, new[] { (bundle, saved) });
            True(plan.Count == 1 && plan[0].CatalogPath == catalog, "plans exported bundle using real saved size");
            True(File.ReadAllBytes(catalog).AsSpan().SequenceEqual(original), "does not publish catalog itself");
            Reject(() => AddressablesCatalog.PlanCatalogUpdates(data, new[] { (Path.Combine(aa, "absent.bundle"), saved) }));
            Reject(() => AddressablesCatalog.PlanCatalogUpdates(data,
                new[] { (Path.Combine(aa, "other-profile", "font.bundle"), saved) }));
            File.WriteAllBytes(catalog, Catalog(first, second, remote: true));
            Reject(() => AddressablesCatalog.PlanCatalogUpdates(data, [(bundle, saved)]));

            string pathA = Path.Combine(aa, "profile-a", "font.bundle");
            string pathB = Path.Combine(aa, "profile-b", "font.bundle");
            foreach (string path in new[] { pathA, pathB })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(bundle, path);
            }
            string savedB = Path.Combine(dir, "patched-b.bundle");
            File.WriteAllBytes(savedB, "UnityFS\0a different saved size"u8.ToArray());
            const string runtime = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/";
            string[] profileIds = [runtime + "profile-a/font.bundle", runtime + @"profile-b\font.bundle"];
            byte[] duplicateName = Catalog(first, first, internalIds: profileIds);
            File.WriteAllBytes(catalog, duplicateName);
            (string, string)[] both = [(pathA, saved), (pathB, savedB)];
            var patchedBoth = AddressablesCatalog.PlanCatalogUpdates(data, both).Single();
            byte[] records = ReadExtra(patchedBoth.Bytes);
            CheckRecord(records.AsSpan(0, first.Length), new FileInfo(saved).Length);
            CheckRecord(records.AsSpan(first.Length), new FileInfo(savedB).Length);
            var patchedOne = AddressablesCatalog.PlanCatalogUpdates(data, [(pathA, saved)]).Single();
            True(ReadExtra(patchedOne.Bytes).AsSpan(first.Length).SequenceEqual(first),
                "same-basename record at an unmodified path is untouched");
            True(File.ReadAllBytes(catalog).AsSpan().SequenceEqual(duplicateName), "batch plan does not publish catalog");

            File.WriteAllBytes(catalog, Catalog(first, first, internalIds: profileIds, shared: true));
            Reject(() => AddressablesCatalog.PlanCatalogUpdates(data, both), "different sizes");
            Reject(() => AddressablesCatalog.PlanCatalogUpdates(data, [(pathA, saved)]), "unmodified paths");
            // Sharing a record is safe when all referenced paths are patched to the same size.
            var shared = AddressablesCatalog.PlanCatalogUpdates(data, [(pathA, saved), (pathB, saved)]).Single();
            CheckRecord(ReadExtra(shared.Bytes).AsSpan(0, first.Length), new FileInfo(saved).Length);

            File.WriteAllBytes(catalog, Catalog(first, first, internalIds:
                [profileIds[0], runtime + "profile-b/another.bundle"], shared: true));
            Reject(() => AddressablesCatalog.PlanCatalogUpdates(data, [(pathA, saved)]), "unmodified paths");
            File.WriteAllBytes(catalog, Catalog(first, first, internalIds:
                [profileIds[0], "https://example.test/a-different.bundle"], shared: true));
            Reject(() => AddressablesCatalog.PlanCatalogUpdates(data, [(pathA, saved)]), "unsupported internal ID");
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private static byte[] Catalog(byte[] first, byte[] second, bool remote = false, string[]? internalIds = null,
        bool shared = false)
    {
        byte[] entries = new byte[60];
        BinaryPrimitives.WriteInt32LittleEndian(entries, 2);
        BinaryPrimitives.WriteInt32LittleEndian(entries.AsSpan(20), 0);
        BinaryPrimitives.WriteInt32LittleEndian(entries.AsSpan(32), 1);
        BinaryPrimitives.WriteInt32LittleEndian(entries.AsSpan(48), shared ? 0 : first.Length);
        byte[] compact = [.. first, .. second];
        const string runtime = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/";
        byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(new
        {
            m_EntryDataString = Convert.ToBase64String(entries),
            m_ExtraDataString = Convert.ToBase64String(compact),
            m_InternalIds = internalIds ?? new[]
            {
                remote ? "https://example.test/font.bundle" : runtime + "font.bundle",
                runtime + "untouched.bundle",
            },
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return [.. Encoding.UTF8.Preamble.ToArray(), .. utf8];
    }

    private static byte[] ReadExtra(byte[] catalog)
    {
        using var root = JsonDocument.Parse(catalog.AsMemory(3));
        return Convert.FromBase64String(root.RootElement.GetProperty("m_ExtraDataString").GetString()!);
    }

    private static void CheckRecord(ReadOnlySpan<byte> record, long size)
    {
        int length = JsonLength(record);
        using var option = JsonDocument.Parse(Encoding.Unicode.GetString(record[^length..]));
        True(option.RootElement.GetProperty("m_Crc").GetInt64() == 0 &&
             option.RootElement.GetProperty("m_BundleSize").GetInt64() == size, "record has its own saved file size");
    }

    private static byte[] Record(string name, long crc, long size, bool webRequest = false)
    {
        byte[] assembly = "Unity.ResourceManager"u8.ToArray();
        byte[] className = "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleRequestOptions"u8.ToArray();
        byte[] payload = Encoding.Unicode.GetBytes(JsonSerializer.Serialize(new
        {
            other = new { m_Crc = 567 },
            m_Hash = "same",
            m_Crc = crc,
            m_BundleName = name,
            m_BundleSize = size,
            m_UseUWRForLocalBundles = webRequest,
        }));
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        return [7, (byte)assembly.Length, .. assembly, (byte)className.Length, .. className, .. length, .. payload];
    }

    private static int JsonLength(ReadOnlySpan<byte> record)
    {
        int classIndex = 2 + record[1];
        return BinaryPrimitives.ReadInt32LittleEndian(record[(classIndex + 1 + record[classIndex])..]);
    }

    private static void Reject(Action operation, string? expected = null)
    {
        try { operation(); }
        catch (InvalidDataException error) when (expected is null || error.Message.Contains(expected, StringComparison.Ordinal))
        { return; }
        throw new Exception("unsupported catalog was not rejected");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new Exception("Addressables regression: " + message);
    }
}
