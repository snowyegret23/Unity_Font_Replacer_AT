namespace UnityFontReplacer.Swizzle;

public static class Ps5SwizzleConstants
{
    public static readonly IReadOnlyDictionary<int, int> BcBlockBytes = new Dictionary<int, int>
    {
        [10] = 8, [12] = 16, [24] = 16, [25] = 16, [26] = 8, [27] = 16,
    };

    public static bool IsBcFormat(int textureFormat) => BcBlockBytes.ContainsKey(textureFormat);

    public static int BytesPerElement(int textureFormat) => textureFormat switch
    {
        1 or 63 => 1, // Alpha8, R8
        2 or 7 or 9 or 62 => 2, // ARGB4444, RGB565, R16, RG16
        3 => 3, // RGB24
        4 or 5 => 4, // RGBA32, ARGB32
        _ => throw new NotSupportedException($"PS5 uncompressed texture format {textureFormat} is not supported in AT."),
    };

    public static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
}
