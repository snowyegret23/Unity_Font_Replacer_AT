namespace UnityFontReplacer.SDF;

/// <summary>
/// Felzenszwalb/Huttenlocher O(n) 유클리드 거리 변환.
/// scipy.ndimage.distance_transform_edt의 C# 구현.
/// </summary>
public static class EdtCalculator
{
    private const float InfiniteDistance = 1_000_000f;
    private const float EdgeThreshold = 1f / 255f;

    /// <summary>
    /// 2D 유클리드 거리 변환. 입력: binary mask (true=seed). 출력: 각 픽셀에서 가장 가까운 seed 픽셀까지의 거리.
    /// </summary>
    public static float[,] DistanceTransform(bool[,] mask)
    {
        int h = mask.GetLength(0);
        int w = mask.GetLength(1);
        var result = new float[h, w];

        // 초기화: object=0, background=INF
        float inf = (float)(w + h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                result[y, x] = mask[y, x] ? 0f : inf * inf;

        // 행 방향 1D 변환
        var rowBuf = new float[Math.Max(w, h)];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                rowBuf[x] = result[y, x];

            Edt1D(rowBuf, w);

            for (int x = 0; x < w; x++)
                result[y, x] = rowBuf[x];
        }

        // 열 방향 1D 변환
        var colBuf = new float[h];
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
                colBuf[y] = result[y, x];

            Edt1D(colBuf, h);

            for (int y = 0; y < h; y++)
                result[y, x] = colBuf[y];
        }

        // 제곱근
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                result[y, x] = MathF.Sqrt(result[y, x]);

        return result;
    }

    /// <summary>
    /// SDF를 계산한다. 입력: alpha 이미지(0-255). spread: SDF 확산 거리(보통 padding).
    /// 출력: SDF 이미지(0-255). edge=128, inside>128, outside&lt;128.
    /// 
    /// 기존 threshold 기반 binary EDT 대신, anti-aliased alpha와 gradient를 함께 이용해
    /// subpixel edge 위치를 추정하는 alpha-aware 거리장을 생성한다.
    /// </summary>
    public static byte[,] ComputeSdf(byte[,] alpha, int spread)
    {
        int h = alpha.GetLength(0);
        int w = alpha.GetLength(1);

        var coverage = new float[h, w];
        bool hasEdgePixels = false;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float value = alpha[y, x] / 255f;
                coverage[y, x] = value;
                if (value > EdgeThreshold && value < (1f - EdgeThreshold))
                    hasEdgePixels = true;
            }
        }

        if (!hasEdgePixels)
            return ComputeBinarySdf(alpha, spread);

        var gradientX = new float[h, w];
        var gradientY = new float[h, w];
        ComputeCoverageGradient(coverage, gradientX, gradientY);

        var edgeVectorX = new float[h, w];
        var edgeVectorY = new float[h, w];
        var distSq = new float[h, w];

        bool seeded = InitializeEdgeSeeds(coverage, gradientX, gradientY, edgeVectorX, edgeVectorY, distSq);
        if (!seeded)
            return ComputeBinarySdf(alpha, spread);

        PropagateDistanceField(edgeVectorX, edgeVectorY, distSq);

        float spreadF = Math.Max(1f, spread);
        var result = new byte[h, w];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float distance = MathF.Sqrt(distSq[y, x]);
                float signedDistance = coverage[y, x] >= 0.5f ? distance : -distance;
                float normalized = 0.5f + signedDistance / (2f * spreadF);
                normalized = Math.Clamp(normalized, 0f, 1f);
                result[y, x] = (byte)Math.Clamp(MathF.Round(normalized * 255f), 0f, 255f);
            }
        }

        return result;
    }

    private static byte[,] ComputeBinarySdf(byte[,] alpha, int spread)
    {
        int h = alpha.GetLength(0);
        int w = alpha.GetLength(1);

        var inside = new bool[h, w];
        var outside = new bool[h, w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                inside[y, x] = alpha[y, x] > 127;
                outside[y, x] = !inside[y, x];
            }
        }

        var distToOutside = DistanceTransform(outside);
        var distToInside = DistanceTransform(inside);

        float spreadF = Math.Max(1f, spread);
        var result = new byte[h, w];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float signedDist = distToOutside[y, x] - distToInside[y, x];
                float normalized = 0.5f + signedDist / (2f * spreadF);
                normalized = Math.Clamp(normalized, 0f, 1f);
                result[y, x] = (byte)Math.Clamp(MathF.Round(normalized * 255f), 0f, 255f);
            }
        }

        return result;
    }

    private static void ComputeCoverageGradient(float[,] coverage, float[,] gradientX, float[,] gradientY)
    {
        int h = coverage.GetLength(0);
        int w = coverage.GetLength(1);

        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - 1);
            int y1 = Math.Min(h - 1, y + 1);

            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - 1);
                int x1 = Math.Min(w - 1, x + 1);

                float gx =
                    (coverage[y0, x1] - coverage[y0, x0]) +
                    2f * (coverage[y, x1] - coverage[y, x0]) +
                    (coverage[y1, x1] - coverage[y1, x0]);

                float gy =
                    (coverage[y1, x0] - coverage[y0, x0]) +
                    2f * (coverage[y1, x] - coverage[y0, x]) +
                    (coverage[y1, x1] - coverage[y0, x1]);

                gradientX[y, x] = gx;
                gradientY[y, x] = gy;
            }
        }
    }

    private static bool InitializeEdgeSeeds(
        float[,] coverage,
        float[,] gradientX,
        float[,] gradientY,
        float[,] edgeVectorX,
        float[,] edgeVectorY,
        float[,] distSq)
    {
        int h = coverage.GetLength(0);
        int w = coverage.GetLength(1);
        bool seeded = false;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float cov = coverage[y, x];
                edgeVectorX[y, x] = 0f;
                edgeVectorY[y, x] = 0f;
                distSq[y, x] = InfiniteDistance;

                if (cov <= EdgeThreshold || cov >= (1f - EdgeThreshold))
                    continue;

                float gx = gradientX[y, x];
                float gy = gradientY[y, x];
                float gradientLength = MathF.Sqrt((gx * gx) + (gy * gy));

                float offsetX = 0f;
                float offsetY = 0f;

                if (gradientLength > 1e-5f)
                {
                    float scale = Math.Clamp((0.5f - cov) / gradientLength, -1f, 1f);
                    offsetX = gx * scale;
                    offsetY = gy * scale;
                }
                else
                {
                    float fallback = Math.Clamp(0.5f - cov, -0.5f, 0.5f);
                    offsetX = fallback;
                }

                edgeVectorX[y, x] = offsetX;
                edgeVectorY[y, x] = offsetY;
                distSq[y, x] = (offsetX * offsetX) + (offsetY * offsetY);
                seeded = true;
            }
        }

        return seeded;
    }

    private static void PropagateDistanceField(float[,] edgeVectorX, float[,] edgeVectorY, float[,] distSq)
    {
        int h = distSq.GetLength(0);
        int w = distSq.GetLength(1);

        for (int pass = 0; pass < 2; pass++)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    UpdateFromNeighbor(x, y, x - 1, y, edgeVectorX, edgeVectorY, distSq);
                    UpdateFromNeighbor(x, y, x, y - 1, edgeVectorX, edgeVectorY, distSq);
                    UpdateFromNeighbor(x, y, x - 1, y - 1, edgeVectorX, edgeVectorY, distSq);
                    UpdateFromNeighbor(x, y, x + 1, y - 1, edgeVectorX, edgeVectorY, distSq);
                }
            }

            for (int y = h - 1; y >= 0; y--)
            {
                for (int x = w - 1; x >= 0; x--)
                {
                    UpdateFromNeighbor(x, y, x + 1, y, edgeVectorX, edgeVectorY, distSq);
                    UpdateFromNeighbor(x, y, x, y + 1, edgeVectorX, edgeVectorY, distSq);
                    UpdateFromNeighbor(x, y, x + 1, y + 1, edgeVectorX, edgeVectorY, distSq);
                    UpdateFromNeighbor(x, y, x - 1, y + 1, edgeVectorX, edgeVectorY, distSq);
                }
            }
        }
    }

    private static void UpdateFromNeighbor(
        int x,
        int y,
        int nx,
        int ny,
        float[,] edgeVectorX,
        float[,] edgeVectorY,
        float[,] distSq)
    {
        int h = distSq.GetLength(0);
        int w = distSq.GetLength(1);

        if ((uint)nx >= (uint)w || (uint)ny >= (uint)h)
            return;

        float neighborDistSq = distSq[ny, nx];
        if (neighborDistSq >= InfiniteDistance)
            return;

        float candidateX = edgeVectorX[ny, nx] + (nx - x);
        float candidateY = edgeVectorY[ny, nx] + (ny - y);
        float candidateDistSq = (candidateX * candidateX) + (candidateY * candidateY);

        if (candidateDistSq + 1e-6f >= distSq[y, x])
            return;

        edgeVectorX[y, x] = candidateX;
        edgeVectorY[y, x] = candidateY;
        distSq[y, x] = candidateDistSq;
    }

    public static byte[,] ResampleBilinear(byte[,] source, int targetWidth, int targetHeight)
    {
        int sourceHeight = source.GetLength(0);
        int sourceWidth = source.GetLength(1);

        if (sourceWidth == targetWidth && sourceHeight == targetHeight)
            return (byte[,])source.Clone();

        var result = new byte[targetHeight, targetWidth];
        float scaleX = (float)sourceWidth / targetWidth;
        float scaleY = (float)sourceHeight / targetHeight;

        for (int y = 0; y < targetHeight; y++)
        {
            float sourceY = ((y + 0.5f) * scaleY) - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(sourceY), 0, sourceHeight - 1);
            int y1 = Math.Clamp(y0 + 1, 0, sourceHeight - 1);
            float ty = sourceY - y0;

            for (int x = 0; x < targetWidth; x++)
            {
                float sourceX = ((x + 0.5f) * scaleX) - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(sourceX), 0, sourceWidth - 1);
                int x1 = Math.Clamp(x0 + 1, 0, sourceWidth - 1);
                float tx = sourceX - x0;

                float top = Lerp(source[y0, x0], source[y0, x1], tx);
                float bottom = Lerp(source[y1, x0], source[y1, x1], tx);
                result[y, x] = (byte)Math.Clamp(MathF.Round(Lerp(top, bottom, ty)), 0, 255);
            }
        }

        return result;
    }

    /// <summary>
    /// 1D 제곱 유클리드 거리 변환 (Felzenszwalb/Huttenlocher).
    /// f[i]를 in-place로 변환: f[q] = min_p (f[p] + (q-p)^2)
    /// </summary>
    private static void Edt1D(float[] f, int n)
    {
        if (n <= 0) return;

        var d = new float[n];    // 결과
        var v = new int[n];      // 포물선 꼭짓점 인덱스
        var z = new float[n + 1]; // 포물선 교차점

        int k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;

        for (int q = 1; q < n; q++)
        {
            // 새 포물선과 기존 포물선의 교차점 계산
            float s;
            while (true)
            {
                int vk = v[k];
                s = ((f[q] + (float)q * q) - (f[vk] + (float)vk * vk)) / (2f * q - 2f * vk);

                if (s > z[k])
                    break;

                k--;
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }

        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q)
                k++;

            int vk = v[k];
            d[q] = (float)(q - vk) * (q - vk) + f[vk];
        }

        Array.Copy(d, f, n);
    }

    private static float Lerp(float a, float b, float t)
    {
        return a + ((b - a) * t);
    }
}
