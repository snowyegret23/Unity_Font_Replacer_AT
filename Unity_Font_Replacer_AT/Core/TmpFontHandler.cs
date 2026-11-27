using AssetsTools.NET;
using AssetsTools.NET.Extra;
using UnityFontReplacer.Models;

namespace UnityFontReplacer.Core;

/// <summary>
/// TMP_FontAsset MonoBehaviour의 AssetTypeValueField 트리와 POCO 모델 간 양방향 변환.
/// </summary>
public static class TmpFontHandler
{
    private static readonly string[] CreationSettingsKeys =
    [
        "m_CreationSettings",
        "m_FontAssetCreationSettings",
    ];

    private static readonly string[] DirtyFlagKeys =
    [
        "m_IsFontAssetLookupTablesDirty",
        "IsFontAssetLookupTablesDirty",
    ];

    private static readonly string[] GlyphIndexListKeys =
    [
        "m_GlyphIndexList",
        "m_GlyphIndexes",
    ];

    public static TmpFontAsset ReadFromField(AssetTypeValueField baseField)
    {
        var schema = TmpSchemaDetector.Detect(baseField);
        var asset = new TmpFontAsset
        {
            Name = SafeString(baseField, "m_Name"),
            Version = SafeString(baseField, "m_Version"),
            SchemaVersion = schema,
        };

        if (schema == TmpSchemaVersion.New)
            ReadNewSchema(baseField, asset);
        else
            ReadOldSchema(baseField, asset);

        return asset;
    }

    public static void WriteToField(
        TmpFontAsset source,
        AssetTypeValueField baseField,
        TmpSchemaVersion targetSchema)
    {
        var atlasRef = ReadAtlasRef(baseField);
        var materialRef = ReadMaterialRef(baseField);
        bool hasNewFields = HasNewSchemaFields(baseField);
        bool hasOldFields = HasOldSchemaFields(baseField);

        if (hasNewFields || targetSchema == TmpSchemaVersion.New)
            WriteNewSchema(source, baseField);
        if (hasOldFields || targetSchema == TmpSchemaVersion.Old)
            WriteOldSchema(source, baseField);

        SyncAtlasReferences(baseField, atlasRef.FileId, atlasRef.PathId);
        SyncMaterialReference(baseField, materialRef.FileId, materialRef.PathId);
        SyncGlyphRects(source, baseField);
        SyncFontWeightTable(source, baseField);
        SyncCreationSettings(source, baseField);
        SyncGlyphIndexLists(source, baseField);
        SetDirtyFlags(baseField);

        if (!string.IsNullOrWhiteSpace(source.Version))
            SafeSetString(baseField, "m_Version", source.Version);
    }

    /// <summary>
    /// Convert a Dynamic/DynamicOS FontAsset to Static for a baked atlas. Call
    /// before serializing any replacement fields. The default is fail-closed.
    /// </summary>
    public static bool PrepareStaticPopulation(AssetTypeValueField target, bool allowFreeze)
    {
        var mode = target["m_AtlasPopulationMode"];
        var dynamicOs = target["InternalDynamicOS"];
        bool dynamic = false;
        if (!mode.IsDummy)
            dynamic |= ReadInteger(mode) != 0;
        if (!dynamicOs.IsDummy)
        {
            if (dynamicOs.Value?.ValueType == AssetValueType.Bool)
                dynamic |= dynamicOs.AsBool;
            else
                dynamic |= ReadInteger(dynamicOs) != 0;
        }
        if (!dynamic)
            return false;
        if (!allowFreeze)
            throw new InvalidDataException(
                "Dynamic TMP FontAsset replacement requires explicit --freeze-dynamic because the replacement is a baked static atlas.");

        var sourceFont = target["m_SourceFontFile"];
        if (!sourceFont.IsDummy)
        {
            // Validate both fields before mutation to avoid leaving half a PPtr.
            ReadInteger(sourceFont["m_FileID"]);
            ReadInteger(sourceFont["m_PathID"]);
        }

        if (!mode.IsDummy)
            SetIntegerZero(mode);
        if (!dynamicOs.IsDummy)
        {
            if (dynamicOs.Value?.ValueType == AssetValueType.Bool)
                dynamicOs.AsBool = false;
            else
                SetIntegerZero(dynamicOs);
        }
        if (!sourceFont.IsDummy)
        {
            SetIntegerZero(sourceFont["m_FileID"]);
            SetIntegerZero(sourceFont["m_PathID"]);
        }
        return true;
    }

    private static long ReadInteger(AssetTypeValueField value)
    {
        try
        {
            return value.Value?.ValueType switch
            {
                AssetValueType.Int8 or AssetValueType.UInt8 or AssetValueType.Int16 or AssetValueType.UInt16 or
                AssetValueType.Int32 or AssetValueType.UInt32 or AssetValueType.Int64 or AssetValueType.UInt64
                    => value.AsLong,
                _ => throw new InvalidDataException($"Expected a serialized TMP integer: {value.FieldName}."),
            };
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException($"Invalid serialized TMP integer: {value.FieldName}.", ex);
        }
    }

    private static void SetIntegerZero(AssetTypeValueField field)
    {
        switch (field.Value.ValueType)
        {
            case AssetValueType.Int8: field.AsSByte = 0; break;
            case AssetValueType.UInt8: field.AsByte = 0; break;
            case AssetValueType.Int16: field.AsShort = 0; break;
            case AssetValueType.UInt16: field.AsUShort = 0; break;
            case AssetValueType.Int32: field.AsInt = 0; break;
            case AssetValueType.UInt32: field.AsUInt = 0; break;
            case AssetValueType.Int64: field.AsLong = 0; break;
            case AssetValueType.UInt64: field.AsULong = 0; break;
        }
    }

    internal static IReadOnlyList<(int FileId, long PathId)> ReadAtlasReferences(AssetTypeValueField target)
    {
        var references = new List<(int FileId, long PathId)>();
        var array = GetArrayField(target, "m_AtlasTextures");
        if (array is not null)
        {
            foreach (var element in array.Children)
                AddReference(element);
        }
        AddReference(target["m_AtlasTexture"]);
        AddReference(target["atlas"]);
        return references;

        void AddReference(AssetTypeValueField reference)
        {
            if (reference.IsDummy)
                return;
            var fileId = reference["m_FileID"];
            var pathId = reference["m_PathID"];
            if (fileId.IsDummy && pathId.IsDummy)
                return;
            var key = (checked((int)ReadInteger(fileId)), ReadInteger(pathId));
            if (key.Item2 != 0 && !references.Contains(key))
                references.Add(key);
        }
    }

    private static bool HasNewSchemaFields(AssetTypeValueField bf)
    {
        return !bf["m_FaceInfo"].IsDummy ||
               !bf["m_GlyphTable"].IsDummy ||
               !bf["m_CharacterTable"].IsDummy ||
               !bf["m_AtlasWidth"].IsDummy ||
               !bf["m_AtlasHeight"].IsDummy ||
               !bf["m_AtlasPadding"].IsDummy;
    }

    private static bool HasOldSchemaFields(AssetTypeValueField bf)
    {
        return !bf["m_fontInfo"].IsDummy || !bf["m_glyphInfoList"].IsDummy;
    }

    private static void ReadNewSchema(AssetTypeValueField bf, TmpFontAsset asset)
    {
        var fi = bf["m_FaceInfo"];
        if (!fi.IsDummy)
            asset.FaceInfo = ReadFaceInfoNew(fi);

        var gt = GetArrayField(bf, "m_GlyphTable");
        if (gt != null)
        {
            asset.GlyphTable = new List<TmpGlyphNew>(gt.Children.Count);
            foreach (var g in gt.Children)
                asset.GlyphTable.Add(ReadGlyphNew(g));
        }

        var ct = GetArrayField(bf, "m_CharacterTable");
        if (ct != null)
        {
            asset.CharacterTable = new List<TmpCharacterNew>(ct.Children.Count);
            foreach (var c in ct.Children)
                asset.CharacterTable.Add(ReadCharacterNew(c));
        }

        var atlasRef = ReadAtlasRef(bf);
        asset.AtlasTextureFileId = atlasRef.FileId;
        asset.AtlasTexturePathId = atlasRef.PathId;

        var mat = bf["m_Material"];
        if (mat.IsDummy) mat = bf["material"];
        if (!mat.IsDummy)
        {
            asset.MaterialFileId = mat["m_FileID"].AsInt;
            asset.MaterialPathId = mat["m_PathID"].AsLong;
        }

        ReadAtlasParamsNew(bf, asset);
    }

    private static void ReadOldSchema(AssetTypeValueField bf, TmpFontAsset asset)
    {
        var fi = bf["m_fontInfo"];
        if (!fi.IsDummy)
            asset.FaceInfo = ReadFaceInfoOld(fi);

        var gl = GetArrayField(bf, "m_glyphInfoList");
        if (gl != null)
        {
            asset.GlyphInfoList = new List<TmpGlyphOld>(gl.Children.Count);
            foreach (var g in gl.Children)
                asset.GlyphInfoList.Add(ReadGlyphOld(g));
        }

        var atlasRef = ReadAtlasRef(bf);
        asset.AtlasTextureFileId = atlasRef.FileId;
        asset.AtlasTexturePathId = atlasRef.PathId;

        var materialRef = ReadMaterialRef(bf);
        asset.MaterialFileId = materialRef.FileId;
        asset.MaterialPathId = materialRef.PathId;

        if (asset.FaceInfo != null)
        {
            asset.AtlasWidth = asset.FaceInfo.AtlasWidth;
            asset.AtlasHeight = asset.FaceInfo.AtlasHeight;
            asset.AtlasPadding = asset.FaceInfo.Padding;
        }
    }

    private static void WriteNewSchema(TmpFontAsset source, AssetTypeValueField bf)
    {
        var faceInfo = CloneFaceInfo(source.FaceInfo);

        var fi = bf["m_FaceInfo"];
        if (!fi.IsDummy)
            WriteFaceInfoNew(faceInfo, fi);

        WriteGlyphTableNew(GetNewGlyphs(source), bf);
        WriteCharacterTableNew(GetNewCharacters(source), bf);
        WriteAtlasParamsNew(source, bf);
    }

    private static void WriteOldSchema(TmpFontAsset source, AssetTypeValueField bf)
    {
        var fi = bf["m_fontInfo"];
        if (!fi.IsDummy)
            WriteFaceInfoOld(BuildOldFaceInfo(source), fi);

        WriteGlyphInfoListOld(GetOldGlyphs(source), bf);
    }

    private static TmpFaceInfo ReadFaceInfoNew(AssetTypeValueField fi)
    {
        return new TmpFaceInfo
        {
            FamilyName = SafeString(fi, "m_FamilyName"),
            StyleName = SafeString(fi, "m_StyleName"),
            PointSize = SafeInt(fi, "m_PointSize"),
            Scale = SafeFloat(fi, "m_Scale", 1.0f),
            UnitsPerEM = SafeInt(fi, "m_UnitsPerEM"),
            LineHeight = SafeFloat(fi, "m_LineHeight"),
            AscentLine = SafeFloat(fi, "m_AscentLine"),
            CapLine = SafeFloat(fi, "m_CapLine"),
            MeanLine = SafeFloat(fi, "m_MeanLine"),
            Baseline = SafeFloat(fi, "m_Baseline"),
            DescentLine = SafeFloat(fi, "m_DescentLine"),
            SuperscriptOffset = SafeFloat(fi, "m_SuperscriptOffset"),
            SuperscriptSize = SafeFloat(fi, "m_SuperscriptSize", 0.5f),
            SubscriptOffset = SafeFloat(fi, "m_SubscriptOffset"),
            SubscriptSize = SafeFloat(fi, "m_SubscriptSize", 0.5f),
            UnderlineOffset = SafeFloat(fi, "m_UnderlineOffset"),
            UnderlineThickness = SafeFloat(fi, "m_UnderlineThickness"),
            StrikethroughOffset = SafeFloat(fi, "m_StrikethroughOffset"),
            StrikethroughThickness = SafeFloat(fi, "m_StrikethroughThickness"),
            TabWidth = SafeFloat(fi, "m_TabWidth"),
        };
    }

    private static TmpFaceInfo ReadFaceInfoOld(AssetTypeValueField fi)
    {
        return new TmpFaceInfo
        {
            FamilyName = SafeString(fi, "Name"),
            PointSize = SafeInt(fi, "PointSize"),
            Scale = SafeFloat(fi, "Scale", 1.0f),
            LineHeight = SafeFloat(fi, "LineHeight"),
            AscentLine = SafeFloat(fi, "Ascender"),
            Baseline = SafeFloat(fi, "Baseline"),
            DescentLine = SafeFloat(fi, "Descender"),
            SuperscriptOffset = SafeFloat(fi, "SuperscriptOffset"),
            SubscriptOffset = SafeFloat(fi, "SubscriptOffset"),
            UnderlineOffset = SafeFloat(fi, "UnderlineOffset"),
            UnderlineThickness = SafeFloat(fi, "underlineThickness"),
            StrikethroughOffset = SafeFloat(fi, "strikethroughOffset"),
            TabWidth = SafeFloat(fi, "TabWidth"),
            Padding = SafeInt(fi, "Padding"),
            AtlasWidth = SafeInt(fi, "AtlasWidth"),
            AtlasHeight = SafeInt(fi, "AtlasHeight"),
        };
    }

    private static void WriteFaceInfoNew(TmpFaceInfo info, AssetTypeValueField fi)
    {
        SafeSetString(fi, "m_FamilyName", info.FamilyName);
        SafeSetString(fi, "m_StyleName", info.StyleName);
        SafeSetInt(fi, "m_PointSize", info.PointSize);
        SafeSetFloat(fi, "m_Scale", info.Scale);
        SafeSetInt(fi, "m_UnitsPerEM", info.UnitsPerEM);
        SafeSetFloat(fi, "m_LineHeight", info.LineHeight);
        SafeSetFloat(fi, "m_AscentLine", info.AscentLine);
        SafeSetFloat(fi, "m_CapLine", info.CapLine);
        SafeSetFloat(fi, "m_MeanLine", info.MeanLine);
        SafeSetFloat(fi, "m_Baseline", info.Baseline);
        SafeSetFloat(fi, "m_DescentLine", info.DescentLine);
        SafeSetFloat(fi, "m_SuperscriptOffset", info.SuperscriptOffset);
        SafeSetFloat(fi, "m_SuperscriptSize", info.SuperscriptSize);
        SafeSetFloat(fi, "m_SubscriptOffset", info.SubscriptOffset);
        SafeSetFloat(fi, "m_SubscriptSize", info.SubscriptSize);
        SafeSetFloat(fi, "m_UnderlineOffset", info.UnderlineOffset);
        SafeSetFloat(fi, "m_UnderlineThickness", info.UnderlineThickness);
        SafeSetFloat(fi, "m_StrikethroughOffset", info.StrikethroughOffset);
        SafeSetFloat(fi, "m_StrikethroughThickness", info.StrikethroughThickness);
        SafeSetFloat(fi, "m_TabWidth", info.TabWidth);
    }

    private static void WriteFaceInfoOld(TmpFaceInfo info, AssetTypeValueField fi)
    {
        SafeSetString(fi, "Name", info.FamilyName);
        SafeSetInt(fi, "PointSize", info.PointSize);
        SafeSetFloat(fi, "Scale", info.Scale);
        SafeSetFloat(fi, "LineHeight", info.LineHeight);
        SafeSetFloat(fi, "Ascender", info.AscentLine);
        SafeSetFloat(fi, "Baseline", info.Baseline);
        SafeSetFloat(fi, "Descender", info.DescentLine);
        SafeSetFloat(fi, "SuperscriptOffset", info.SuperscriptOffset);
        SafeSetFloat(fi, "SubscriptOffset", info.SubscriptOffset);
        SafeSetFloat(fi, "UnderlineOffset", info.UnderlineOffset);
        SafeSetFloat(fi, "underlineThickness", info.UnderlineThickness);
        SafeSetFloat(fi, "strikethroughOffset", info.StrikethroughOffset);
        SafeSetFloat(fi, "TabWidth", info.TabWidth);
        SafeSetInt(fi, "Padding", info.Padding);
        SafeSetInt(fi, "AtlasWidth", info.AtlasWidth);
        SafeSetInt(fi, "AtlasHeight", info.AtlasHeight);
    }

    private static TmpGlyphNew ReadGlyphNew(AssetTypeValueField g)
    {
        var metrics = g["m_Metrics"];
        var rect = g["m_GlyphRect"];
        return new TmpGlyphNew
        {
            Index = g["m_Index"].AsInt,
            MetricsWidth = SafeFloat(metrics, "m_Width"),
            MetricsHeight = SafeFloat(metrics, "m_Height"),
            HorizontalBearingX = SafeFloat(metrics, "m_HorizontalBearingX"),
            HorizontalBearingY = SafeFloat(metrics, "m_HorizontalBearingY"),
            HorizontalAdvance = SafeFloat(metrics, "m_HorizontalAdvance"),
            RectX = SafeInt(rect, "m_X"),
            RectY = SafeInt(rect, "m_Y"),
            RectWidth = SafeInt(rect, "m_Width"),
            RectHeight = SafeInt(rect, "m_Height"),
            Scale = SafeFloat(g, "m_Scale", 1.0f),
            AtlasIndex = SafeInt(g, "m_AtlasIndex"),
        };
    }

    private static TmpCharacterNew ReadCharacterNew(AssetTypeValueField c)
    {
        return new TmpCharacterNew
        {
            ElementType = SafeInt(c, "m_ElementType", 1),
            Unicode = SafeInt(c, "m_Unicode"),
            GlyphIndex = SafeInt(c, "m_GlyphIndex"),
            Scale = SafeFloat(c, "m_Scale", 1.0f),
        };
    }

    private static TmpGlyphOld ReadGlyphOld(AssetTypeValueField g)
    {
        return new TmpGlyphOld
        {
            Id = SafeInt(g, "id"),
            X = SafeFloat(g, "x"),
            Y = SafeFloat(g, "y"),
            Width = SafeFloat(g, "width"),
            Height = SafeFloat(g, "height"),
            XOffset = SafeFloat(g, "xOffset"),
            YOffset = SafeFloat(g, "yOffset"),
            XAdvance = SafeFloat(g, "xAdvance"),
            Scale = SafeFloat(g, "scale", 1.0f),
        };
    }

    private static void WriteGlyphTableNew(List<TmpGlyphNew> glyphs, AssetTypeValueField bf)
    {
        var arr = GetArrayField(bf, "m_GlyphTable");
        if (arr == null) return;

        arr.Children.Clear();
        foreach (var glyph in glyphs)
        {
            var elem = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
            elem["m_Index"].AsInt = glyph.Index;
            elem["m_Metrics"]["m_Width"].AsFloat = glyph.MetricsWidth;
            elem["m_Metrics"]["m_Height"].AsFloat = glyph.MetricsHeight;
            elem["m_Metrics"]["m_HorizontalBearingX"].AsFloat = glyph.HorizontalBearingX;
            elem["m_Metrics"]["m_HorizontalBearingY"].AsFloat = glyph.HorizontalBearingY;
            elem["m_Metrics"]["m_HorizontalAdvance"].AsFloat = glyph.HorizontalAdvance;
            elem["m_GlyphRect"]["m_X"].AsInt = glyph.RectX;
            elem["m_GlyphRect"]["m_Y"].AsInt = glyph.RectY;
            elem["m_GlyphRect"]["m_Width"].AsInt = glyph.RectWidth;
            elem["m_GlyphRect"]["m_Height"].AsInt = glyph.RectHeight;
            elem["m_Scale"].AsFloat = glyph.Scale;
            SafeSetInt(elem, "m_AtlasIndex", glyph.AtlasIndex);
            arr.Children.Add(elem);
        }
    }

    private static void WriteCharacterTableNew(List<TmpCharacterNew> chars, AssetTypeValueField bf)
    {
        var arr = GetArrayField(bf, "m_CharacterTable");
        if (arr == null) return;

        arr.Children.Clear();
        foreach (var ch in chars)
        {
            var elem = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
            SafeSetInt(elem, "m_ElementType", ch.ElementType);
            elem["m_Unicode"].AsInt = ch.Unicode;
            elem["m_GlyphIndex"].AsInt = ch.GlyphIndex;
            elem["m_Scale"].AsFloat = ch.Scale;
            arr.Children.Add(elem);
        }
    }

    private static void WriteGlyphInfoListOld(List<TmpGlyphOld> glyphs, AssetTypeValueField bf)
    {
        var arr = GetArrayField(bf, "m_glyphInfoList");
        if (arr == null) return;

        arr.Children.Clear();
        foreach (var g in glyphs)
        {
            var elem = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
            elem["id"].AsInt = g.Id;
            elem["x"].AsFloat = g.X;
            elem["y"].AsFloat = g.Y;
            elem["width"].AsFloat = g.Width;
            elem["height"].AsFloat = g.Height;
            elem["xOffset"].AsFloat = g.XOffset;
            elem["yOffset"].AsFloat = g.YOffset;
            elem["xAdvance"].AsFloat = g.XAdvance;
            elem["scale"].AsFloat = g.Scale;
            arr.Children.Add(elem);
        }
    }

    private static void ReadAtlasParamsNew(AssetTypeValueField bf, TmpFontAsset asset)
    {
        asset.AtlasWidth = SafeInt(bf, "m_AtlasWidth");
        asset.AtlasHeight = SafeInt(bf, "m_AtlasHeight");
        asset.AtlasPadding = SafeInt(bf, "m_AtlasPadding");
        asset.AtlasRenderMode = SafeInt(bf, "m_AtlasRenderMode");
    }

    private static void WriteAtlasParamsNew(TmpFontAsset source, AssetTypeValueField bf)
    {
        SafeSetInt(bf, "m_AtlasWidth", source.AtlasWidth);
        SafeSetInt(bf, "m_AtlasHeight", source.AtlasHeight);
        SafeSetInt(bf, "m_AtlasPadding", source.AtlasPadding);
        SafeSetInt(bf, "m_AtlasRenderMode", source.AtlasRenderMode);
    }

    private static void SyncAtlasReferences(
        AssetTypeValueField bf,
        int fileId,
        long pathId)
    {
        var array = GetArrayField(bf, "m_AtlasTextures");
        if (array != null && array.Children.Count > 0)
        {
            var first = array[0];
            first["m_FileID"].AsInt = fileId;
            first["m_PathID"].AsLong = pathId;
        }

        foreach (var key in new[] { "m_AtlasTexture", "atlas" })
        {
            var reference = bf[key];
            if (!reference.IsDummy && !reference["m_FileID"].IsDummy && !reference["m_PathID"].IsDummy)
            {
                reference["m_FileID"].AsInt = fileId;
                reference["m_PathID"].AsLong = pathId;
            }
        }
    }

    private static void SyncMaterialReference(
        AssetTypeValueField bf,
        int fileId,
        long pathId)
    {
        foreach (var fieldName in new[] { "m_Material", "material", "m_material" })
        {
            var field = bf[fieldName];
            if (field.IsDummy)
                continue;

            var fieldFileId = field["m_FileID"];
            var fieldPathId = field["m_PathID"];
            if (fieldFileId.IsDummy || fieldPathId.IsDummy)
                continue;

            fieldFileId.AsInt = fileId;
            fieldPathId.AsLong = pathId;
        }
    }

    private static void SyncGlyphRects(TmpFontAsset source, AssetTypeValueField bf)
    {
        WriteGlyphRectArray(bf, "m_UsedGlyphRects", source.UsedGlyphRects);
        WriteGlyphRectArray(bf, "m_FreeGlyphRects", source.FreeGlyphRects);
    }

    private static void SyncFontWeightTable(TmpFontAsset source, AssetTypeValueField bf)
    {
        var weights = source.FontWeightTable;
        if (weights == null)
            return;

        var arr = GetArrayField(bf, "m_FontWeightTable");
        if (arr == null)
            return;

        arr.Children.Clear();
        foreach (var weight in weights)
        {
            var elem = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
            elem["regularTypeface"]["m_FileID"].AsInt = weight.RegularTypefaceFileId;
            elem["regularTypeface"]["m_PathID"].AsLong = weight.RegularTypefacePathId;
            elem["italicTypeface"]["m_FileID"].AsInt = weight.ItalicTypefaceFileId;
            elem["italicTypeface"]["m_PathID"].AsLong = weight.ItalicTypefacePathId;
            arr.Children.Add(elem);
        }
    }

    private static void SyncCreationSettings(TmpFontAsset source, AssetTypeValueField bf)
    {
        foreach (var key in CreationSettingsKeys)
        {
            var settings = bf[key];
            if (settings.IsDummy)
                continue;

            foreach (var child in settings.Children)
            {
                var normalized = child.FieldName.Replace("_", "").ToLowerInvariant();
                if (normalized.Contains("atlaswidth"))
                {
                    SafeSetFieldInt(child, source.AtlasWidth);
                }
                else if (normalized.Contains("atlasheight"))
                {
                    SafeSetFieldInt(child, source.AtlasHeight);
                }
                else if (normalized == "padding")
                {
                    SafeSetFieldInt(child, source.AtlasPadding);
                }
                else if (normalized == "pointsize")
                {
                    SafeSetFieldInt(child, source.FaceInfo.PointSize);
                }
                else if (normalized.Contains("rendermode"))
                {
                    SafeSetFieldInt(child, source.AtlasRenderMode);
                }
            }
        }
    }

    private static void SyncGlyphIndexLists(TmpFontAsset source, AssetTypeValueField bf)
    {
        var glyphIndexes = GetNewGlyphs(source)
            .Select(g => g.Index)
            .ToList();

        foreach (var key in GlyphIndexListKeys)
            WriteIntArray(bf, key, glyphIndexes);
    }

    private static void SetDirtyFlags(AssetTypeValueField bf)
    {
        foreach (var key in DirtyFlagKeys)
        {
            var field = bf[key];
            if (!field.IsDummy)
                field.AsBool = true;
        }
    }

    private static void WriteIntArray(
        AssetTypeValueField parent,
        string name,
        List<int> values)
    {
        var arr = GetArrayField(parent, name);
        if (arr == null)
            return;

        arr.Children.Clear();
        foreach (var value in values)
        {
            var elem = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
            elem.AsInt = value;
            arr.Children.Add(elem);
        }
    }

    private static void WriteGlyphRectArray(
        AssetTypeValueField parent,
        string name,
        List<TmpGlyphRect>? values)
    {
        if (values == null)
            return;

        var arr = GetArrayField(parent, name);
        if (arr == null)
            return;

        arr.Children.Clear();
        foreach (var value in values)
        {
            var elem = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
            elem["m_X"].AsInt = value.X;
            elem["m_Y"].AsInt = value.Y;
            elem["m_Width"].AsInt = value.Width;
            elem["m_Height"].AsInt = value.Height;
            arr.Children.Add(elem);
        }
    }

    private static (int FileId, long PathId) ReadAtlasRef(AssetTypeValueField bf) =>
        ReadAtlasReferences(bf).FirstOrDefault();

    private static (int FileId, long PathId) ReadMaterialRef(AssetTypeValueField bf)
    {
        foreach (var fieldName in new[] { "m_Material", "material", "m_material" })
        {
            var field = bf[fieldName];
            if (field.IsDummy)
                continue;

            var fileId = field["m_FileID"];
            var pathId = field["m_PathID"];
            if (fileId.IsDummy || pathId.IsDummy)
                continue;

            return (fileId.AsInt, pathId.AsLong);
        }

        return (0, 0);
    }

    private static AssetTypeValueField? GetArrayField(AssetTypeValueField parent, string name)
    {
        var field = parent[name];
        if (field.IsDummy)
            return null;

        var array = field["Array"];
        return array.IsDummy ? null : array;
    }

    private static List<TmpGlyphNew> GetNewGlyphs(TmpFontAsset source)
    {
        if (source.SchemaVersion == TmpSchemaVersion.New)
            return source.GlyphTable?.ToList() ?? [];

        var result = new List<TmpGlyphNew>();
        foreach (var old in source.GlyphInfoList ?? [])
        {
            var (glyph, _) = TmpGlyphConverter.OldToNew(old, source.AtlasHeight);
            result.Add(glyph);
        }
        return result;
    }

    private static List<TmpCharacterNew> GetNewCharacters(TmpFontAsset source)
    {
        if (source.SchemaVersion == TmpSchemaVersion.New)
            return source.CharacterTable?.ToList() ?? [];

        var result = new List<TmpCharacterNew>();
        foreach (var old in source.GlyphInfoList ?? [])
        {
            var (_, character) = TmpGlyphConverter.OldToNew(old, source.AtlasHeight);
            result.Add(character);
        }
        return result;
    }

    private static List<TmpGlyphOld> GetOldGlyphs(TmpFontAsset source)
    {
        if (source.SchemaVersion == TmpSchemaVersion.Old)
            return source.GlyphInfoList?.ToList() ?? [];

        var glyphMap = (source.GlyphTable ?? [])
            .ToDictionary(g => g.Index);
        var result = new List<TmpGlyphOld>();
        foreach (var ch in source.CharacterTable ?? [])
        {
            if (glyphMap.TryGetValue(ch.GlyphIndex, out var glyph))
                result.Add(TmpGlyphConverter.NewToOld(glyph, ch, source.AtlasHeight));
        }
        return result;
    }

    private static TmpFaceInfo BuildOldFaceInfo(TmpFontAsset source)
    {
        var info = CloneFaceInfo(source.FaceInfo);
        info.Padding = source.AtlasPadding;
        info.AtlasWidth = source.AtlasWidth;
        info.AtlasHeight = source.AtlasHeight;
        return info;
    }

    private static TmpFaceInfo CloneFaceInfo(TmpFaceInfo source)
    {
        return new TmpFaceInfo
        {
            FamilyName = source.FamilyName,
            StyleName = source.StyleName,
            PointSize = source.PointSize,
            Scale = source.Scale,
            UnitsPerEM = source.UnitsPerEM,
            LineHeight = source.LineHeight,
            AscentLine = source.AscentLine,
            CapLine = source.CapLine,
            MeanLine = source.MeanLine,
            Baseline = source.Baseline,
            DescentLine = source.DescentLine,
            SuperscriptOffset = source.SuperscriptOffset,
            SuperscriptSize = source.SuperscriptSize,
            SubscriptOffset = source.SubscriptOffset,
            SubscriptSize = source.SubscriptSize,
            UnderlineOffset = source.UnderlineOffset,
            UnderlineThickness = source.UnderlineThickness,
            StrikethroughOffset = source.StrikethroughOffset,
            StrikethroughThickness = source.StrikethroughThickness,
            TabWidth = source.TabWidth,
            Padding = source.Padding,
            AtlasWidth = source.AtlasWidth,
            AtlasHeight = source.AtlasHeight,
        };
    }

    private static string SafeString(
        AssetTypeValueField parent,
        string name,
        string fallback = "")
    {
        var f = parent[name];
        return f.IsDummy ? fallback : f.AsString;
    }

    private static int SafeInt(
        AssetTypeValueField parent,
        string name,
        int fallback = 0)
    {
        var f = parent[name];
        return f.IsDummy ? fallback : f.AsInt;
    }

    private static float SafeFloat(
        AssetTypeValueField parent,
        string name,
        float fallback = 0f)
    {
        var f = parent[name];
        return f.IsDummy ? fallback : f.AsFloat;
    }

    private static void SafeSetString(AssetTypeValueField parent, string name, string value)
    {
        var f = parent[name];
        if (!f.IsDummy)
            f.AsString = value;
    }

    private static void SafeSetInt(AssetTypeValueField parent, string name, int value)
    {
        var f = parent[name];
        if (!f.IsDummy)
            f.AsInt = value;
    }

    private static void SafeSetFloat(AssetTypeValueField parent, string name, float value)
    {
        var f = parent[name];
        if (!f.IsDummy)
            f.AsFloat = value;
    }

    private static void SafeSetFieldInt(AssetTypeValueField field, int value)
    {
        if (!field.IsDummy)
            field.AsInt = value;
    }
}
