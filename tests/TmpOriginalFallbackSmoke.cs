using AssetsTools.NET;
using AssetsTools.NET.Extra;
using UnityFontReplacer.Core;

internal static class TmpOriginalFallbackSmoke
{
    private const long FontId = -7, AtlasId = 1, MaterialId = -2, MonoId = 2, ShaderId = 4;

    internal static void Run()
    {
        var manager = new AssetsManager();
        using var input = Fixture();
        byte[] bytes = input.ToArray();
        var instance = new AssetsFileInstance(input, "/synthetic/font.assets");
        var info = instance.file.GetAssetInfo(FontId);
        var font = manager.GetBaseField(instance, info);
        var originalAtlas = manager.GetBaseField(instance, instance.file.GetAssetInfo(AtlasId)).WriteToByteArray();
        var preserve = TmpOriginalFallback.Capture(manager, instance, info, font);
        Check(instance.file.Metadata.AssetInfos.Count == 5, "Capture is read-only.");
        font["m_Name"].AsString = "Replacement";
        preserve.Install(font);
        info.SetNewData(font);
        var originalAtlasInfo = instance.file.GetAssetInfo(AtlasId);
        var replacementAtlas = manager.GetBaseField(instance, originalAtlasInfo);
        replacementAtlas["image data"].AsByteArray = [99, 98, 97, 96];
        originalAtlasInfo.SetNewData(replacementAtlas);
        var originalMaterialInfo = instance.file.GetAssetInfo(MaterialId);
        var replacementMaterial = manager.GetBaseField(instance, originalMaterialInfo);
        replacementMaterial["m_Name"].AsString = "Replacement Material";
        originalMaterialInfo.SetNewData(replacementMaterial);
        using var output = new MemoryStream();
        using (var writer = new AssetsFileWriter(output))
        {
            instance.file.Write(writer);
            bytes = output.ToArray();
        }

        var reopened = new AssetsFileInstance(new MemoryStream(bytes), "/synthetic/output.assets");
        var updated = Read(FontId);
        var cloneId = updated["m_FallbackFontAssetTable"]["Array"][0]["m_PathID"].AsLong;
        Check(cloneId == 3 && updated["m_AtlasTextures"]["Array"][0]["m_PathID"].AsLong == AtlasId, "No PathID collision.");
        var clone = Read(cloneId);
        Check(clone["m_Name"].AsString == "__UFR_OriginalFallback_Original" &&
              clone["m_GlyphTable"]["Array"][0]["m_Index"].AsInt == 73 &&
              clone["m_FallbackFontAssetTable"]["Array"].Children.Count == 0, "Original glyphs retained without fallback cycles.");
        var cloneAtlasId = clone["m_AtlasTextures"]["Array"][0]["m_PathID"].AsLong;
        var cloneMaterialId = clone["m_Material"]["m_PathID"].AsLong;
        Check(cloneAtlasId != AtlasId && cloneMaterialId != MaterialId &&
            Read(cloneAtlasId).WriteToByteArray().SequenceEqual(originalAtlas) &&
            !Read(AtlasId).WriteToByteArray().SequenceEqual(originalAtlas), "Texture bytes are retained exactly, independently of replacement.");
        var clonedMaterial = Read(cloneMaterialId);
        Check(clonedMaterial["m_SavedProperties"]["m_TexEnvs"]["Array"][0]["second"]["m_Texture"]["m_PathID"].AsLong == cloneAtlasId &&
            clonedMaterial["m_Shader"]["m_PathID"].AsLong == ShaderId &&
            clonedMaterial["m_Name"].AsString == "__UFR_OriginalFallback_Original Material",
            "Clone material points to the new atlas and preserved shader and retains the old properties.");
        Check(Read(MaterialId)["m_SavedProperties"]["m_TexEnvs"]["Array"][0]["second"]["m_Texture"]["m_PathID"].AsLong == AtlasId,
            "Only the cloned material receives a new atlas reference.");

        Reject(dynamic: true);
        Reject(streamed: true);
        Reject(externalShader: true);
        Reject(hasFallback: true);
        Reject(multi: true);

        AssetTypeValueField Read(long id) => manager.GetBaseField(reopened, reopened.file.GetAssetInfo(id));
        void Reject(bool dynamic = false, bool streamed = false, bool externalShader = false, bool hasFallback = false, bool multi = false)
        {
            using var invalid = Fixture(dynamic, streamed, externalShader, hasFallback, multi);
            var i = new AssetsFileInstance(invalid, "/synthetic/rejected.assets");
            var asset = i.file.GetAssetInfo(FontId);
            try { TmpOriginalFallback.Capture(manager, i, asset, manager.GetBaseField(i, asset)); }
            catch (InvalidDataException)
            {
                Check(i.file.Metadata.AssetInfos.Count == 5 && i.file.Metadata.AssetInfos.All(x => x.ReplacerType == ContentReplacerType.None),
                    "Unsupported inputs rejected without mutation.");
                return;
            }
            throw new Exception("Unsupported fallback fixture unexpectedly accepted.");
        }
    }

    private static MemoryStream Fixture(bool dynamic = false, bool streamed = false, bool externalShader = false, bool hasFallback = false, bool multi = false)
    {
        var font = Node("Base", "MonoBehaviour",
            String("m_Name"), Int("m_AtlasPopulationMode"), Int("m_AtlasTextureIndex"),
            Array("m_AtlasTextures", Pointer("data", "Texture2D")),
            Array("m_FallbackFontAssetTable", Pointer("data", "TMP_FontAsset")),
            Array("m_GlyphTable", Node("data", "Glyph", Int("m_AtlasIndex"), Int("m_Index"))),
            Pointer("m_Script", "MonoScript"), Pointer("m_Material", "Material"));
        var texture = Node("Base", "Texture2D",
            new AssetTypeTemplateField { Name = "image data", Type = "TypelessData", IsArray = true,
                Children = [Int("size"), Node("data", "UInt8")] },
            Node("m_StreamData", "StreamingInfo", Node("offset", "UInt64"), Int("size"), String("path")));
        var material = Node("Base", "Material", String("m_Name"), Pointer("m_Shader", "Shader"),
            Node("m_SavedProperties", "UnityPropertySheet",
                Array("m_TexEnvs", Node("data", "pair", String("first"),
                    Node("second", "UnityTexEnv", Pointer("m_Texture", "Texture2D"))))));
        var script = Node("Base", "MonoScript", String("m_ClassName"), String("m_Namespace"));
        var shader = Node("Base", "Shader", Int("m_Dummy"));

        var rows = new (long Id, AssetClassID Class, AssetTypeTemplateField Template, Action<AssetsFileWriter> Write)[]
        {
            (FontId, AssetClassID.MonoBehaviour, font, w =>
            {
                Str(w, "Original"); w.Write(dynamic ? 1 : 0); w.Write(0);
                w.Write(multi ? 2 : 1); Ptr(w, AtlasId); if (multi) Ptr(w, AtlasId);
                w.Write(hasFallback ? 1 : 0); if (hasFallback) Ptr(w, FontId);
                w.Write(1); w.Write(0); w.Write(73);
                Ptr(w, MonoId); Ptr(w, MaterialId);
            }),
            (AtlasId, AssetClassID.Texture2D, texture, w =>
            {
                w.Write(4); w.Write(new byte[] { 0, 10, 255, 128 });
                w.Write(streamed ? 32UL : 0UL); w.Write(streamed ? 4 : 0); Str(w, streamed ? "font.resS" : "");
            }),
            (MaterialId, AssetClassID.Material, material, w =>
            {
                Str(w, "Original Material"); Ptr(w, ShaderId, externalShader ? 1 : 0); w.Write(1);
                Str(w, "_MainTex"); Ptr(w, AtlasId);
            }),
            (MonoId, AssetClassID.MonoScript, script, w => { Str(w, "TMP_FontAsset"); Str(w, "TMPro"); }),
            (ShaderId, AssetClassID.Shader, shader, w => w.Write(42)),
        };
        var file = new AssetsFile
        {
            Header = new AssetsFileHeader { Version = 22 },
            Metadata = new AssetsFileMetadata
            {
                UnityVersion = "2021.3.0f1", TargetPlatform = 19, TypeTreeEnabled = true,
                TypeTreeTypes = rows.Select(r => TemplateFieldToTypeTree.Convert(r.Template, (int)r.Class)).ToList(),
                AssetInfos = new List<AssetFileInfo>(), Externals = [], ScriptTypes = [], RefTypes = [], UserInformation = "",
            },
        };
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            using var payload = new MemoryStream();
            using var raw = new AssetsFileWriter(payload);
            row.Write(raw);
            var info = new AssetFileInfo { PathId = row.Id, TypeId = (int)row.Class, TypeIdOrIndex = i };
            info.SetNewData(payload.ToArray());
            file.Metadata.AddAssetInfo(info);
        }

        using var data = new MemoryStream();
        using var writer = new AssetsFileWriter(data);
        file.Write(writer);
        return new MemoryStream(data.ToArray(), writable: false);
    }

    private static void Ptr(AssetsFileWriter writer, long pathId, int fileId = 0)
    {
        writer.Write(fileId);
        writer.Write(pathId);
    }
    private static void Str(AssetsFileWriter writer, string value)
    {
        writer.WriteCountStringInt32(value);
        writer.Align();
    }
    private static AssetTypeTemplateField Pointer(string name, string type) =>
        Node(name, $"PPtr<{type}>", Int("m_FileID"), Node("m_PathID", "SInt64"));
    private static AssetTypeTemplateField Array(string name, AssetTypeTemplateField data) =>
        Node(name, "vector", new AssetTypeTemplateField { Name = "Array", Type = "Array", IsArray = true,
            Children = [Int("size"), data] });
    private static AssetTypeTemplateField Int(string name) => Node(name, "int");
    private static AssetTypeTemplateField String(string name) => Node(name, "string",
        new AssetTypeTemplateField { Name = "Array", Type = "Array", IsArray = true, IsAligned = true,
            Children = [Int("size"), Node("data", "char")] });
    private static AssetTypeTemplateField Node(string name, string type, params AssetTypeTemplateField[] children) =>
        new() { Name = name, Type = type, Children = [.. children] };
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
