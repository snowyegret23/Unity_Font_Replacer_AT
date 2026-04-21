using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UnityFontReplacer.Models;

namespace UnityFontReplacer.SDF;

/// <summary>
/// TTF → TMP SDF 에셋 생성 파이프라인.
/// 출력: SDF JSON + Atlas PNG + Material JSON
/// </summary>
public static class SdfGenerator
{
    public record SdfResult(
        TmpFontAsset FontAsset,
        Image<Rgba32> AtlasImage,
        Dictionary<string, float> MaterialProperties);

    /// <summary>
    /// TTF 바이트에서 SDF 에셋을 생성한다.
    /// </summary>
    public static SdfResult Generate(
        byte[] ttfData,
        int[] unicodes,
        int atlasWidth = 4096, int atlasHeight = 4096,
        int padding = 7,
        int pointSize = 0,
        bool rasterMode = false)
    {
        using var renderer = new GlyphRenderer(ttfData);

        // 1. 포인트 크기 결정
        int resolvedSize = PointSizeSearch.Find(renderer, unicodes, atlasWidth, atlasHeight, padding, pointSize);

        // 2. 글리프 메트릭 수집 + 패킹
        var glyphMetrics = new List<(int unicode, GlyphMetrics metrics)>();
        var packRects = new List<ShelfPacker.GlyphRect>();

        foreach (var unicode in unicodes)
        {
            var m = renderer.MeasureGlyph(unicode, resolvedSize);
            glyphMetrics.Add((unicode, m));
            int w = Math.Max(1, m.Width) + padding * 2;
            int h = Math.Max(1, m.Height) + padding * 2;
            packRects.Add(new ShelfPacker.GlyphRect(unicode, w, h));
        }

        var placements = ShelfPacker.Pack(packRects, atlasWidth, atlasHeight);
        if (placements == null)
            throw new InvalidOperationException($"Failed to pack glyphs at point size {resolvedSize}");

        var placementMap = placements.ToDictionary(p => p.Id);

        // 3. 아틀라스 이미지 생성 + 글리프 렌더링/SDF 변환
        var atlas = new Image<Rgba32>(atlasWidth, atlasHeight);
        var glyphTable = new List<TmpGlyphNew>();
        var characterTable = new List<TmpCharacterNew>();

        var globalMetrics = renderer.GetGlobalMetrics(resolvedSize);

        foreach (var (unicode, metrics) in glyphMetrics)
        {
            if (!placementMap.TryGetValue(unicode, out var placement))
                continue;

            // 글리프 비트맵 렌더링
            var bitmap = renderer.RenderGlyphBitmap(unicode, resolvedSize, padding, out int offX, out int offY);
            int bmpH = bitmap.GetLength(0);
            int bmpW = bitmap.GetLength(1);

            // SDF 또는 래스터 변환
            byte[,] processed;
            if (!rasterMode)
            {
                processed = EdtCalculator.ComputeSdf(bitmap, Math.Max(1, padding));
            }
            else
            {
                processed = bitmap;
            }

            // 아틀라스에 복사 (alpha 채널)
            int destW = Math.Min(bmpW, placement.Width);
            int destH = Math.Min(bmpH, placement.Height);

            atlas.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < destH && (placement.Y + y) < atlasHeight; y++)
                {
                    var row = accessor.GetRowSpan(placement.Y + y);
                    for (int x = 0; x < destW && (placement.X + x) < atlasWidth; x++)
                    {
                        byte val = processed[y, x];
                        row[placement.X + x] = new Rgba32(0, 0, 0, val);
                    }
                }
            });

            // 글리프 메트릭(패딩 제외한 실제 글리프 영역)
            int glyphW = Math.Max(0, metrics.Width);
            int glyphH = Math.Max(0, metrics.Height);
            int glyphX = placement.X + padding;
            // TMP: bottom-origin Y
            int glyphY = atlasHeight - (placement.Y + padding) - glyphH;

            glyphTable.Add(new TmpGlyphNew
            {
                Index = unicode,
                MetricsWidth = glyphW,
                MetricsHeight = glyphH,
                HorizontalBearingX = offX,
                HorizontalBearingY = globalMetrics.Ascender - offY,
                HorizontalAdvance = metrics.HorizontalAdvance,
                RectX = glyphX,
                RectY = glyphY,
                RectWidth = glyphW,
                RectHeight = glyphH,
                Scale = 1.0f,
                AtlasIndex = 0,
            });

            characterTable.Add(new TmpCharacterNew
            {
                ElementType = 1,
                Unicode = unicode,
                GlyphIndex = unicode,
                Scale = 1.0f,
            });
        }

        // 4. TmpFontAsset 조립
        var fontAsset = new TmpFontAsset
        {
            Name = renderer.FamilyName + " SDF",
            SchemaVersion = TmpSchemaVersion.New,
            FaceInfo = new TmpFaceInfo
            {
                FamilyName = renderer.FamilyName,
                PointSize = resolvedSize,
                Scale = 1.0f,
                UnitsPerEM = globalMetrics.UnitsPerEm,
                LineHeight = globalMetrics.LineHeight,
                AscentLine = globalMetrics.Ascender,
                Baseline = 0,
                DescentLine = globalMetrics.Descender,
                SuperscriptOffset = globalMetrics.Ascender * 0.5f,
                SubscriptOffset = globalMetrics.Descender * 0.5f,
                UnderlineOffset = globalMetrics.Descender * 0.5f,
                UnderlineThickness = resolvedSize * 0.05f,
                StrikethroughOffset = globalMetrics.Ascender * 0.3f,
                TabWidth = globalMetrics.Ascender,
            },
            GlyphTable = glyphTable,
            CharacterTable = characterTable,
            AtlasWidth = atlasWidth,
            AtlasHeight = atlasHeight,
            AtlasPadding = padding,
            AtlasRenderMode = rasterMode ? 4 : 4118,
        };

        // 5. 머티리얼 속성
        var materialProps = new Dictionary<string, float>
        {
            ["_GradientScale"] = rasterMode ? 1.0f : padding + 1.0f,
            ["_TextureWidth"] = atlasWidth,
            ["_TextureHeight"] = atlasHeight,
        };

        return new SdfResult(fontAsset, atlas, materialProps);
    }

    /// <summary>
    /// SDF 결과를 파일로 저장한다.
    /// </summary>
    public static void SaveToFiles(SdfResult result, string outputDir, string fontName)
    {
        Directory.CreateDirectory(outputDir);

        // JSON (SDF 데이터)
        var jsonPath = Path.Combine(outputDir, fontName + " SDF.json");
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var sdfJson = SerializeFontAsset(result.FontAsset);
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(sdfJson, jsonOptions));

        // PNG (아틀라스)
        var pngPath = Path.Combine(outputDir, fontName + " SDF Atlas.png");
        result.AtlasImage.SaveAsPng(pngPath);

        // Material JSON
        var matPath = Path.Combine(outputDir, fontName + " SDF Material.json");
        var matJson = new Dictionary<string, object>
        {
            ["m_SavedProperties"] = new Dictionary<string, object>
            {
                ["m_Floats"] = result.MaterialProperties.Select(kv =>
                    new object[] { kv.Key, kv.Value }).ToArray()
            }
        };
        File.WriteAllText(matPath, JsonSerializer.Serialize(matJson, jsonOptions));
    }

    private static TmpFontAssetJson SerializeFontAsset(TmpFontAsset asset)
    {
        return new TmpFontAssetJson
        {
            m_FaceInfo = new TmpFaceInfoJson
            {
                m_FamilyName = asset.FaceInfo.FamilyName,
                m_StyleName = asset.FaceInfo.StyleName,
                m_PointSize = asset.FaceInfo.PointSize,
                m_Scale = asset.FaceInfo.Scale,
                m_UnitsPerEM = asset.FaceInfo.UnitsPerEM,
                m_LineHeight = asset.FaceInfo.LineHeight,
                m_AscentLine = asset.FaceInfo.AscentLine,
                m_CapLine = asset.FaceInfo.CapLine,
                m_MeanLine = asset.FaceInfo.MeanLine,
                m_Baseline = asset.FaceInfo.Baseline,
                m_DescentLine = asset.FaceInfo.DescentLine,
                m_SuperscriptOffset = asset.FaceInfo.SuperscriptOffset,
                m_SubscriptOffset = asset.FaceInfo.SubscriptOffset,
                m_UnderlineOffset = asset.FaceInfo.UnderlineOffset,
                m_UnderlineThickness = asset.FaceInfo.UnderlineThickness,
                m_StrikethroughOffset = asset.FaceInfo.StrikethroughOffset,
                m_TabWidth = asset.FaceInfo.TabWidth,
            },
            m_GlyphTable = asset.GlyphTable?.Select(g => new TmpGlyphJson
            {
                m_Index = g.Index,
                m_Metrics = new TmpMetricsJson
                {
                    m_Width = g.MetricsWidth,
                    m_Height = g.MetricsHeight,
                    m_HorizontalBearingX = g.HorizontalBearingX,
                    m_HorizontalBearingY = g.HorizontalBearingY,
                    m_HorizontalAdvance = g.HorizontalAdvance,
                },
                m_GlyphRect = new TmpGlyphRectJson
                {
                    m_X = g.RectX,
                    m_Y = g.RectY,
                    m_Width = g.RectWidth,
                    m_Height = g.RectHeight,
                },
                m_Scale = g.Scale,
                m_AtlasIndex = g.AtlasIndex,
            }).ToList(),
            m_CharacterTable = asset.CharacterTable?.Select(c => new TmpCharacterJson
            {
                m_ElementType = c.ElementType,
                m_Unicode = c.Unicode,
                m_GlyphIndex = c.GlyphIndex,
                m_Scale = c.Scale,
            }).ToList(),
            m_AtlasWidth = asset.AtlasWidth,
            m_AtlasHeight = asset.AtlasHeight,
            m_AtlasPadding = asset.AtlasPadding,
            m_AtlasRenderMode = asset.AtlasRenderMode,
        };
    }
}
