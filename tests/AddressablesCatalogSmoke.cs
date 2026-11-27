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
        var sizes = new Dictionary<string, long> { [@"nested\FONT.BUNDLE"] = 23456789 };
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
        Reject(() => AddressablesCatalog.Patch(Catalog(first, second, remote: true), sizes, () => "StandaloneWindows64"));
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
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private static byte[] Catalog(byte[] first, byte[] second, bool remote = false)
    {
        byte[] entries = new byte[60];
        BinaryPrimitives.WriteInt32LittleEndian(entries, 2);
        BinaryPrimitives.WriteInt32LittleEndian(entries.AsSpan(20), 0);
        BinaryPrimitives.WriteInt32LittleEndian(entries.AsSpan(32), 1);
        BinaryPrimitives.WriteInt32LittleEndian(entries.AsSpan(48), first.Length);
        byte[] compact = [.. first, .. second];
        const string runtime = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}/";
        byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(new
        {
            m_EntryDataString = Convert.ToBase64String(entries),
            m_ExtraDataString = Convert.ToBase64String(compact),
            m_InternalIds = new[]
            {
                remote ? "https://example.test/font.bundle" : runtime + "font.bundle",
                runtime + "untouched.bundle",
            },
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return [.. Encoding.UTF8.Preamble.ToArray(), .. utf8];
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

    private static int JsonLength(byte[] record)
    {
        int classIndex = 2 + record[1];
        return BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(classIndex + 1 + record[classIndex]));
    }

    private static void Reject(Action operation)
    {
        try { operation(); }
        catch (InvalidDataException) { return; }
        throw new Exception("unsupported catalog was not rejected");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new Exception("Addressables regression: " + message);
    }
}
