using System.Buffers;
using System.Security.Cryptography;
using AssetsTools.NET;

namespace UnityFontReplacer.Core;

internal static class SavedAssetValidation
{
    internal sealed record ChangedAsset(long PathId, int ClassId, long Size, byte[] Hash);

    internal static List<ChangedAsset> Capture(AssetsFile file)
    {
        var result = new List<ChangedAsset>();
        foreach (var info in file.Metadata.AssetInfos)
        {
            if (info.ReplacerType == ContentReplacerType.None)
                continue;
            if (!info.IsReplacerPreviewable)
                throw new InvalidDataException($"Cannot verify pending data for Path ID {info.PathId}.");
            var stream = info.Replacer.GetPreviewStream();
            long position = stream.Position;
            try
            {
                stream.Position = 0;
                result.Add(new(info.PathId, info.TypeId, stream.Length, HashRange(stream, stream.Length)));
            }
            finally
            {
                stream.Position = position;
            }
        }
        return result;
    }

    internal static void Verify(AssetsFile file, IReadOnlyList<ChangedAsset> expected)
    {
        var actual = file.Metadata.AssetInfos.ToDictionary(info => info.PathId);
        var stream = file.Reader.BaseStream;
        foreach (var entry in expected)
        {
            if (!actual.TryGetValue(entry.PathId, out var found) ||
                found.TypeId != entry.ClassId || found.ByteSize != entry.Size)
                throw new InvalidDataException($"Saved metadata mismatch at Path ID {entry.PathId}.");
            stream.Position = found.GetAbsoluteByteOffset(file);
            if (!HashRange(stream, entry.Size).AsSpan().SequenceEqual(entry.Hash))
                throw new InvalidDataException($"Saved bytes mismatch at Path ID {entry.PathId}.");
        }
    }

    private static byte[] HashRange(Stream stream, long length)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            while (length > 0)
            {
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
                if (read == 0)
                    throw new EndOfStreamException("Saved asset data is truncated.");
                digest.AppendData(buffer, 0, read);
                length -= read;
            }
            return digest.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
