using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace UnityFontReplacer.Core;

/// <summary>Preserves a narrowly supported original static TMP asset as a local fallback.</summary>
internal sealed class TmpOriginalFallback(
    AssetsFile file, long fallbackId, AssetFileInfo[] additions, string fallbackField)
{
    private bool _installed;

    internal static TmpOriginalFallback Capture(
        AssetsManager manager, AssetsFileInstance instance, AssetFileInfo fontInfo, AssetTypeValueField target)
    {
        var file = instance.file;
        if (instance.parentBundle != null || file.Header.Version < 17 || !file.Metadata.TypeTreeEnabled ||
            file.Header.Endianness || fontInfo.TypeId != (int)AssetClassID.MonoBehaviour)
            throw Unsupported("requires a standalone little-endian modern serialized asset file with embedded TypeTrees");

        // Read again before any edits; never retain a mutable alias of the replacement fields.
        var original = ReadExact(manager, instance, fontInfo);
        if (target.WriteToByteArray().AsSpan().SequenceEqual(original.WriteToByteArray()) is false)
            throw Unsupported("target TMP fields have already changed");
        string fallbackField = ValidateFont(original);

        var scriptInfo = Resolve(file, original["m_Script"], AssetClassID.MonoScript);
        var script = manager.GetBaseField(instance, scriptInfo);
        if (script["m_ClassName"].IsDummy || script["m_Namespace"].IsDummy ||
            script["m_ClassName"].AsString != "TMP_FontAsset" || script["m_Namespace"].AsString != "TMPro")
            throw Unsupported("requires an official local TMPro.TMP_FontAsset MonoScript");

        var atlases = original["m_AtlasTextures"]["Array"];
        var atlasInfo = Resolve(file, atlases[0], AssetClassID.Texture2D);
        var atlas = ReadExact(manager, instance, atlasInfo);
        ValidateAtlas(atlas);
        var originalPointers = new HashSet<long> { fontInfo.PathId, atlasInfo.PathId };
        var materialPointers = new[] { "m_Material", "material", "m_material" }
            .Select(name => original[name]).Where(p => !p.IsDummy && ReadPointer(p).Path != 0).ToArray();
        if (materialPointers.Length == 0)
            throw Unsupported("requires a local TMP Material");
        var materialInfo = Resolve(file, materialPointers[0], AssetClassID.Material);
        originalPointers.Add(materialInfo.PathId);
        if (materialPointers.Any(p => ReadPointer(p) != (0, materialInfo.PathId)))
            throw Unsupported("conflicting Material aliases");
        var material = ReadExact(manager, instance, materialInfo);
        var shaderInfo = Resolve(file, material["m_Shader"], AssetClassID.Shader);

        var properties = material["m_SavedProperties"]["m_TexEnvs"]["Array"];
        if (properties.IsDummy)
            throw Unsupported("Material does not store texture properties");
        var mainTextures = properties.Children.Where(e => !e["first"].IsDummy &&
                e["first"].AsString == "_MainTex")
            .Select(e => e["second"]["m_Texture"]).ToArray();
        if (mainTextures.Length != 1 || ReadPointer(mainTextures[0]) != (0, atlasInfo.PathId))
            throw Unsupported("Material must use the original atlas as its one _MainTex property");

        var atlasPointers = new List<AssetTypeValueField> { atlases[0] };
        foreach (string alias in new[] { "m_AtlasTexture", "atlas" })
        {
            var pointer = original[alias];
            if (pointer.IsDummy || ReadPointer(pointer) == (0, 0))
                continue;
            if (ReadPointer(pointer) != (0, atlasInfo.PathId))
                throw Unsupported("conflicting atlas aliases");
            atlasPointers.Add(pointer);
        }

        // The fallback may share the unchanged local script and shader, but must not depend
        // on any external file or retain another route to one of the assets we will overwrite.
        ValidatePointers(original, p => atlasPointers.Contains(p) || materialPointers.Contains(p) ||
            (ReferenceEquals(p, original["m_Script"]) && ReadPointer(p) == (0, scriptInfo.PathId)));
        ValidatePointers(atlas, _ => false);
        ValidatePointers(material, p => ReferenceEquals(p, mainTextures[0]) ||
            (ReferenceEquals(p, material["m_Shader"]) && ReadPointer(p) == (0, shaderInfo.PathId)));
        ValidateShaderChain(shaderInfo, new HashSet<long>());

        var used = file.Metadata.AssetInfos.Select(info => info.PathId).ToHashSet();
        long next = 1;
        long Allocate()
        {
            while (!used.Add(next))
                next = checked(next + 1);
            return next++;
        }
        long fallbackId = Allocate();
        long atlasId = Allocate();
        long materialId = Allocate();

        foreach (var pointer in atlasPointers)
            pointer["m_PathID"].AsLong = atlasId;
        foreach (var pointer in materialPointers)
            pointer["m_PathID"].AsLong = materialId;
        mainTextures[0]["m_PathID"].AsLong = atlasId;
        TagName(original);
        TagName(material);
        return new TmpOriginalFallback(file, fallbackId,
            [CloneInfo(fontInfo, fallbackId, original.WriteToByteArray()),
             CloneInfo(atlasInfo, atlasId, ReadRaw(instance, atlasInfo)),
             CloneInfo(materialInfo, materialId, material.WriteToByteArray())], fallbackField);

        void ValidateShaderChain(AssetFileInfo info, HashSet<long> visited)
        {
            if (!visited.Add(info.PathId))
                return;
            var shader = ReadExact(manager, instance, info);
            ValidatePointers(shader, p =>
            {
                var (id, path) = ReadPointer(p);
                if (id != 0 || originalPointers.Contains(path))
                    return false;
                var dep = file.GetAssetInfo(path);
                if (dep?.TypeId != (int)AssetClassID.Shader)
                    return false;
                ValidateShaderChain(dep, visited);
                return true;
            });
        }
    }

    internal void Install(AssetTypeValueField target)
    {
        if (_installed || additions.Any(info => file.GetAssetInfo(info.PathId) != null))
            throw Unsupported("new Path IDs are already in use");
        var fallbackArray = target[fallbackField]["Array"];
        if (fallbackArray.IsDummy || fallbackArray.Children.Count != 0)
            throw Unsupported("replacement no longer has an empty fallback table");
        var pointer = ValueBuilder.DefaultValueFieldFromArrayTemplate(fallbackArray);
        if (ReadPointer(pointer) != (0, 0))
            throw Unsupported("could not construct an empty fallback PPtr");
        pointer["m_PathID"].AsLong = fallbackId;

        fallbackArray.Children.Add(pointer);
        foreach (var info in additions)
            file.Metadata.AddAssetInfo(info);
        _installed = true;
    }

    private static string ValidateFont(AssetTypeValueField font)
    {
        var mode = font["m_AtlasPopulationMode"];
        var multi = font["m_IsMultiAtlasTexturesEnabled"];
        if (mode.IsDummy || mode.AsInt != 0 ||
            (!multi.IsDummy && multi.AsBool) ||
            TmpFontHandler.PrepareStaticPopulation(font, allowFreeze: false))
            throw Unsupported("dynamic or multi-atlas fonts are not supported");
        var index = font["m_AtlasTextureIndex"];
        if (!index.IsDummy && index.AsInt != 0)
            throw Unsupported("expected atlas index zero");
        var atlases = font["m_AtlasTextures"]["Array"];
        if (atlases.IsDummy || atlases.Children.Count != 1)
            throw Unsupported("requires exactly one atlas entry");
        var glyphs = font["m_GlyphTable"]["Array"];
        if (glyphs.IsDummy || glyphs.Children.Any(g =>
            !g["m_AtlasIndex"].IsDummy && g["m_AtlasIndex"].AsInt != 0))
            throw Unsupported("glyphs must all use atlas index zero");
        var options = new[] { "m_FallbackFontAssetTable", "fallbackFontAssets", "m_FallbackFontAssets" }
            .Where(name => !font[name].IsDummy).ToArray();
        if (options.Length == 0 || options.Any(name =>
            font[name]["Array"].IsDummy || font[name]["Array"].Children.Count != 0))
            throw Unsupported("requires an empty TMP fallback chain");
        if (!options.Contains("m_FallbackFontAssetTable"))
            throw Unsupported("requires the modern serialized fallback table");
        return "m_FallbackFontAssetTable";
    }

    private static void ValidateAtlas(AssetTypeValueField atlas)
    {
        var stream = atlas["m_StreamData"];
        if (!stream.IsDummy && (stream["offset"].IsDummy || stream["size"].IsDummy ||
            stream["path"].IsDummy || stream["offset"].AsULong != 0 ||
            stream["size"].AsULong != 0 || !string.IsNullOrEmpty(stream["path"].AsString)))
            throw Unsupported("streamed atlas data is not supported");

        var pixels = atlas["image data"];
        if (pixels.IsDummy)
            throw Unsupported("atlas has no inline image data");
        bool hasData = pixels.Value?.ValueType == AssetValueType.ByteArray
            ? pixels.AsByteArray.Length > 0
            : pixels.TemplateField.IsArray && pixels.Children.Count > 0 ||
                !pixels["Array"].IsDummy && pixels["Array"].Children.Count > 0;
        if (!hasData)
            throw Unsupported("atlas has no inline image data");
    }

    private static void TagName(AssetTypeValueField field)
    {
        var name = field["m_Name"];
        if (name.IsDummy || name.Value?.ValueType != AssetValueType.String)
            throw Unsupported("expected a named TMP or Material object");
        name.AsString = "__UFR_OriginalFallback_" + name.AsString;
    }

    private static AssetFileInfo Resolve(AssetsFile file, AssetTypeValueField pointer, AssetClassID type)
    {
        var (id, path) = ReadPointer(pointer);
        var info = id == 0 && path != 0 ? file.GetAssetInfo(path) : null;
        return info?.TypeId == (int)type ? info : throw Unsupported($"requires a local {type} dependency");
    }

    private static (int File, long Path) ReadPointer(AssetTypeValueField pointer)
    {
        if (pointer.IsDummy || pointer["m_FileID"].IsDummy || pointer["m_PathID"].IsDummy ||
            pointer["m_PathID"].Value?.ValueType != AssetValueType.Int64)
            throw Unsupported("encountered an unsupported PPtr");
        return (pointer["m_FileID"].AsInt, pointer["m_PathID"].AsLong);
    }

    private static void ValidatePointers(AssetTypeValueField field, Func<AssetTypeValueField, bool> allow)
    {
        if (field.Value?.ValueType == AssetValueType.ManagedReferencesRegistry &&
            field.AsManagedReferencesRegistry.references.Count != 0)
            throw Unsupported("managed reference dependencies are not supported");
        if (field.TypeName?.StartsWith("PPtr<", StringComparison.Ordinal) == true ||
            !field["m_FileID"].IsDummy || !field["m_PathID"].IsDummy)
        {
            if (ReadPointer(field) != (0, 0) && !allow(field))
                throw Unsupported($"unsupported or external dependency at {field.FieldName}");
            return;
        }
        foreach (var child in field.Children)
            ValidatePointers(child, allow);
    }

    private static AssetTypeValueField ReadExact(AssetsManager manager, AssetsFileInstance instance, AssetFileInfo info)
    {
        var field = manager.GetBaseField(instance, info);
        if (!field.WriteToByteArray().AsSpan().SequenceEqual(ReadRaw(instance, info)))
            throw Unsupported($"cannot preserve original bytes for Path ID {info.PathId}");
        return field;
    }

    private static byte[] ReadRaw(AssetsFileInstance instance, AssetFileInfo info)
    {
        if (info.ReplacerType != ContentReplacerType.None)
            throw Unsupported($"original Path ID {info.PathId} has already been changed");
        lock (instance.LockReader)
        {
            var reader = instance.file.Reader;
            long previous = reader.Position;
            try
            {
                reader.Position = info.GetAbsoluteByteOffset(instance.file);
                var bytes = reader.ReadBytes(checked((int)info.ByteSize));
                if (bytes.LongLength != info.ByteSize)
                    throw Unsupported($"original Path ID {info.PathId} is truncated");
                return bytes;
            }
            finally { reader.Position = previous; }
        }
    }

    private static AssetFileInfo CloneInfo(AssetFileInfo source, long pathId, byte[] bytes)
    {
        var clone = new AssetFileInfo
        {
            PathId = pathId, ByteSize = checked((uint)bytes.Length),
            TypeId = source.TypeId, TypeIdOrIndex = source.TypeIdOrIndex,
            OldTypeId = source.OldTypeId, ScriptTypeIndex = source.ScriptTypeIndex, Stripped = source.Stripped,
        };
        clone.SetNewData(bytes);
        return clone;
    }

    private static InvalidDataException Unsupported(string detail) => new($"Cannot retain original TMP fallback: {detail}.");
}
