using System.Numerics;

namespace UnityFontReplacer.Swizzle;

/// <summary>Python UFR's base-level PS5 tile permutation for uncompressed elements.</summary>
public static class Ps5Swizzle
{
    private static (int X, int Y) MicroTile(int bytesPerElement) => bytesPerElement switch
    {
        2 or 3 => (4, 3),
        4 => (3, 2),
        _ => (5, 4),
    };

    public static bool SupportsDimensions(int width, int height, int bytesPerElement)
    {
        var (x, y) = MicroTile(bytesPerElement);
        return bytesPerElement > 0 && Ps5SwizzleConstants.IsPowerOfTwo(width)
            && Ps5SwizzleConstants.IsPowerOfTwo(height) && width >= (1 << x) && height >= (1 << y);
    }

    public static (long maskX, long maskY) ComputeSwizzleMasks(int width, int height, int bytesPerElement)
    {
        if (!SupportsDimensions(width, height, bytesPerElement))
            throw new ArgumentException($"Unsupported PS5 texture shape: {width}x{height}, {bytesPerElement} bytes per element.");
        var (microX, microY) = MicroTile(bytesPerElement);
        int moreX = BitOperations.Log2((uint)width) - microX;
        int moreY = BitOperations.Log2((uint)height) - microY;
        int offset = 0;
        long x = 0, y = 0;
        void Append(ref long mask, int bits)
        {
            mask |= ((1L << bits) - 1) << offset;
            offset += bits;
        }
        Append(ref y, microY);
        Append(ref x, microX);
        if (moreY > 0) { Append(ref y, 1); --moreY; }
        if (moreX > 0) { Append(ref x, 1); --moreX; }
        Append(ref y, moreY);
        Append(ref x, moreX);
        return (x, y);
    }

    private static int[] DepositTable(long mask, int size)
    {
        var table = new int[size];
        for (int value = 1; value < size; value++)
        {
            int deposited = 0;
            long bits = mask;
            for (int remaining = value; remaining != 0; remaining >>= 1, bits &= bits - 1)
                if ((remaining & 1) != 0)
                    deposited |= checked((int)(bits & -bits));
            table[value] = deposited;
        }
        return table;
    }

    private static byte[] Transform(byte[] data, int width, int height, int bytesPerElement, bool encode)
    {
        if (width <= 0 || height <= 0 || bytesPerElement <= 0)
            throw new ArgumentException("PS5 texture dimensions and bytes per element must be positive.");
        int size = checked(width * height * bytesPerElement);
        if (data.Length < size)
            throw new ArgumentException($"PS5 texture needs at least {size} bytes; got {data.Length}.", nameof(data));
        // Match Python: unsupported tile shapes are kept linear, and only mip0 is returned.
        if (!SupportsDimensions(width, height, bytesPerElement))
            return data.AsSpan(0, size).ToArray();

        var output = new byte[size];
        var (maskX, maskY) = ComputeSwizzleMasks(width, height, bytesPerElement);
        var xTable = DepositTable(maskX, width);
        var yTable = DepositTable(maskY, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int linear = (y * width + x) * bytesPerElement;
                int swizzled = (xTable[x] | yTable[y]) * bytesPerElement;
                data.AsSpan(encode ? linear : swizzled, bytesPerElement)
                    .CopyTo(output.AsSpan(encode ? swizzled : linear, bytesPerElement));
            }
        }
        return output;
    }

    public static byte[] UnswizzleUncompressed(byte[] data, int width, int height, int bytesPerElement) =>
        Transform(data, width, height, bytesPerElement, encode: false);

    public static byte[] SwizzleUncompressed(byte[] data, int width, int height, int bytesPerElement) =>
        Transform(data, width, height, bytesPerElement, encode: true);

    /// <summary>Encode top-origin PNG alpha as the Python tool's rotated PS5 Alpha8 payload.</summary>
    public static byte[] EncodeAlpha8Atlas(byte[] topOrigin, int width, int height)
    {
        CheckAlpha8Atlas(topOrigin, width, height);
        var rotated = new byte[topOrigin.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                rotated[x * height + height - 1 - y] = topOrigin[y * width + x];
        return SwizzleUncompressed(rotated, height, width, 1);
    }

    /// <summary>Decode an explicitly marked PS5 Alpha8 atlas using its logical metadata size.</summary>
    public static byte[] DecodeAlpha8Atlas(byte[] encoded, int width, int height)
    {
        CheckAlpha8Atlas(encoded, width, height);
        var rotated = UnswizzleUncompressed(encoded, height, width, 1);
        var linear = new byte[rotated.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                linear[y * width + x] = rotated[x * height + height - 1 - y];
        return linear;
    }

    private static void CheckAlpha8Atlas(byte[] data, int width, int height)
    {
        if (!SupportsDimensions(width, height, 1) || !SupportsDimensions(height, width, 1))
            throw new NotSupportedException("PS5 Alpha8 requires power-of-two dimensions supported in both orientations.");
        if (data.Length != checked(width * height))
            throw new ArgumentException("PS5 Alpha8 atlas requires exactly one unpadded mip level.", nameof(data));
    }

    // The Python branch has separate BC/decoding/layout inference. A generic tile permutation
    // must not be presented as support for writing those layouts.
    public static byte[] UnswizzleBcBlocks(byte[] data, int width, int height, int blockBytes) =>
        throw new NotSupportedException("PS5 BC layout selection is not supported in AT.");

    public static byte[] SwizzleBcBlocks(byte[] data, int width, int height, int blockBytes) =>
        throw new NotSupportedException("PS5 BC layout selection is not supported in AT.");

    public static byte[] Unswizzle(byte[] data, int width, int height, int textureFormat) =>
        UnswizzleUncompressed(data, width, height, Ps5SwizzleConstants.BytesPerElement(textureFormat));

    public static byte[] Swizzle(byte[] data, int width, int height, int textureFormat) =>
        SwizzleUncompressed(data, width, height, Ps5SwizzleConstants.BytesPerElement(textureFormat));
}
