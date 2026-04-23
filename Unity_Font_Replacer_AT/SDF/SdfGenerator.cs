using System.Runtime.InteropServices;
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
    private static readonly (int Width, int Height)[] ReplacementAtlasSizes =
    [
        (4096, 4096),
        (8192, 8192),
    ];

    public record SdfResult(
        TmpFontAsset FontAsset,
        Image<Rgba32> AtlasImage,
        Dictionary<string, float> MaterialProperties,
        TextureFilterMode FilterMode);

    /// <summary>
    /// TTF 바이트에서 SDF 에셋을 생성한다.
    /// </summary>
    public static SdfResult Generate(
        byte[] ttfData,
        int[] unicodes,
        int atlasWidth = 4096, int atlasHeight = 4096,
        int padding = 7,
        int pointSize = 0,
        bool rasterMode = false,
        TextureFilterMode filterMode = TextureFilterMode.Bilinear)
    {
        using var renderer = new GlyphRenderer(ttfData, rasterMode);

        // 1. 포인트 크기 결정
        int requestedSize = PointSizeSearch.Find(renderer, unicodes, atlasWidth, atlasHeight, padding, pointSize);

        // 2. 글리프 메트릭 수집 + 패킹
        var (resolvedSize, glyphMetrics, placements) = BuildExactLayout(
            renderer,
            unicodes,
            requestedSize,
            atlasWidth,
            atlasHeight,
            padding,
            rasterMode);

        var placementMap = placements.ToDictionary(p => p.Id);
        var usedGlyphRects = placements
            .Select(p => new TmpGlyphRect
            {
                X = p.X,
                Y = p.Y,
                Width = p.Width,
                Height = p.Height,
            })
            .ToList();

        // 3. 아틀라스 이미지 생성 + 글리프 렌더링/SDF 변환
        var atlasPixels = new Rgba32[atlasWidth * atlasHeight];
        var glyphTable = new List<TmpGlyphNew>();
        var characterTable = new List<TmpCharacterNew>();

        var globalMetrics = renderer.GetGlobalMetrics(resolvedSize);

        foreach (var (unicode, metrics) in glyphMetrics)
        {
            if (!placementMap.TryGetValue(unicode, out var placement))
                continue;

            byte[,] processed;
            if (rasterMode)
            {
                processed = renderer.RenderGlyphBitmap(unicode, resolvedSize, padding, out _, out _);
            }
            else
            {
                var sdfBitmap = renderer.RenderGlyphSdfBitmap(unicode, resolvedSize, padding, out _, out _);
                if (sdfBitmap != null)
                {
                    processed = sdfBitmap;
                }
                else
                {
                    var bitmap = renderer.RenderGlyphBitmap(unicode, resolvedSize, padding, out _, out _);
                    processed = EdtCalculator.ComputeSdf(bitmap, padding);
                }
            }

            int bmpH = processed.GetLength(0);
            int bmpW = processed.GetLength(1);
            int destW = Math.Min(bmpW, placement.Width);
            int destH = Math.Min(bmpH, placement.Height);

            for (int y = 0; y < destH; y++)
            {
                int atlasRow = placement.Y + y;
                if (atlasRow >= atlasHeight)
                    break;

                int rowOffset = atlasRow * atlasWidth;
                for (int x = 0; x < destW; x++)
                {
                    int atlasX = placement.X + x;
                    if (atlasX >= atlasWidth)
                        break;

                    atlasPixels[rowOffset + atlasX] = new Rgba32(0, 0, 0, processed[y, x]);
                }
            }

            int glyphW = Math.Max(0, metrics.Width);
            int glyphH = Math.Max(0, metrics.Height);
            int glyphX = rasterMode ? placement.X + padding : placement.X;
            int glyphY = rasterMode
                ? atlasHeight - (placement.Y + padding) - glyphH
                : atlasHeight - placement.Y - glyphH;

            glyphTable.Add(new TmpGlyphNew
            {
                Index = unicode,
                MetricsWidth = glyphW,
                MetricsHeight = glyphH,
                HorizontalBearingX = metrics.HorizontalBearingX,
                HorizontalBearingY = metrics.HorizontalBearingY,
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
            UsedGlyphRects = usedGlyphRects,
            FreeGlyphRects = [],
            FontWeightTable = [],
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

        var atlas = Image.LoadPixelData<Rgba32>(
            MemoryMarshal.AsBytes(atlasPixels.AsSpan()),
            atlasWidth,
            atlasHeight);
        return new SdfResult(fontAsset, atlas, materialProps, filterMode);
    }

    public static SdfResult GenerateForReplacement(
        byte[] ttfData,
        int[] unicodes,
        int padding,
        int pointSizeHint,
        bool rasterMode,
        TextureFilterMode filterMode)
    {
        var (atlasWidth, atlasHeight, requestedPointSize) = PlanReplacementGeneration(
            ttfData,
            unicodes,
            padding,
            pointSizeHint,
            rasterMode);

        return Generate(
            ttfData,
            unicodes,
            atlasWidth,
            atlasHeight,
            padding,
            requestedPointSize,
            rasterMode,
            filterMode);
    }

    private static (int AtlasWidth, int AtlasHeight, int RequestedPointSize) PlanReplacementGeneration(
        byte[] ttfData,
        int[] unicodes,
        int padding,
        int pointSizeHint,
        bool rasterMode)
    {
        using var renderer = new GlyphRenderer(ttfData, rasterMode);

        int requestedPointSize = NormalizePointSize(pointSizeHint);
        if (requestedPointSize > 0)
        {
            foreach (var (width, height) in ReplacementAtlasSizes)
            {
                if (PointSizeSearch.CanPack(renderer, unicodes, requestedPointSize, width, height, padding))
                    return (width, height, requestedPointSize);
            }
        }

        foreach (var (width, height) in ReplacementAtlasSizes)
        {
            int autoPointSize = PointSizeSearch.Find(renderer, unicodes, width, height, padding, requestedSize: 0);
            if (PointSizeSearch.CanPack(renderer, unicodes, autoPointSize, width, height, padding))
                return (width, height, 0);
        }

        throw new InvalidOperationException("Failed to find a usable atlas/point-size combination");
    }

    private static int NormalizePointSize(int pointSizeHint)
    {
        if (pointSizeHint <= 0)
            return 0;

        return Math.Clamp(pointSizeHint, 8, 512);
    }

    private static (int ResolvedPointSize, List<(int unicode, GlyphMetrics metrics)> GlyphMetrics, List<ShelfPacker.Placement> Placements) BuildExactLayout(
        GlyphRenderer renderer,
        int[] unicodes,
        int requestedPointSize,
        int atlasWidth,
        int atlasHeight,
        int padding,
        bool rasterMode)
    {
        for (int pointSize = requestedPointSize; pointSize >= 8; pointSize--)
        {
            var glyphMetrics = new List<(int unicode, GlyphMetrics metrics)>(unicodes.Length);
            var packRects = new List<ShelfPacker.GlyphRect>(unicodes.Length);

            foreach (var unicode in unicodes)
            {
                var metrics = renderer.MeasureGlyph(unicode, pointSize, padding);
                glyphMetrics.Add((unicode, metrics));

                int w = Math.Max(1, metrics.Width);
                int h = Math.Max(1, metrics.Height);
                if (rasterMode)
                {
                    w += padding * 2;
                    h += padding * 2;
                }

                packRects.Add(new ShelfPacker.GlyphRect(unicode, w, h));
            }

            var placements = ShelfPacker.Pack(packRects, atlasWidth, atlasHeight);
            if (placements != null)
                return (pointSize, glyphMetrics, placements);
        }

        throw new InvalidOperationException($"Failed to pack glyphs at point size {requestedPointSize}");
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
        var sdfJson = SerializeFontAsset(result.FontAsset, result.FilterMode);
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

    private static TmpFontAssetJson SerializeFontAsset(TmpFontAsset asset, TextureFilterMode filterMode)
    {
        return new TmpFontAssetJson
        {
            texture_filter_mode = filterMode.ToString(),
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
            m_AtlasTextures =
            [
                new TmpPPtrJson
                {
                    m_FileID = 0,
                    m_PathID = 0,
                },
            ],
            m_UsedGlyphRects = asset.UsedGlyphRects?.Select(rect => new TmpGlyphRectJson
            {
                m_X = rect.X,
                m_Y = rect.Y,
                m_Width = rect.Width,
                m_Height = rect.Height,
            }).ToList(),
            m_FreeGlyphRects = asset.FreeGlyphRects?.Select(rect => new TmpGlyphRectJson
            {
                m_X = rect.X,
                m_Y = rect.Y,
                m_Width = rect.Width,
                m_Height = rect.Height,
            }).ToList(),
            m_FontWeightTable = asset.FontWeightTable?.Select(weight => new TmpFontWeightPairJson
            {
                regularTypeface = new TmpPPtrJson
                {
                    m_FileID = weight.RegularTypefaceFileId,
                    m_PathID = weight.RegularTypefacePathId,
                },
                italicTypeface = new TmpPPtrJson
                {
                    m_FileID = weight.ItalicTypefaceFileId,
                    m_PathID = weight.ItalicTypefacePathId,
                },
            }).ToList(),
            m_AtlasWidth = asset.AtlasWidth,
            m_AtlasHeight = asset.AtlasHeight,
            m_AtlasPadding = asset.AtlasPadding,
            m_AtlasRenderMode = asset.AtlasRenderMode,
        };
    }
}
