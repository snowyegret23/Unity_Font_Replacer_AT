using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Extra.Decompressors.LZ4;
using LZ4ps;
using SevenZip.Compression.LZMA;

namespace UnityFontReplacer.Core;

/// <summary>
/// Native UnityFS v9 container adapter. The pinned AssetsTools.NET only understands
/// the old 10-byte block records, so expose a disk-backed, uncompressed v8 view
/// to its serialized-file APIs. On publish, write a genuinely v9 container.
/// </summary>
internal static class UnityFsV9
{
    private const uint InfoAtEnd = 0x80;
    private const uint DirectoryFlag = 0x40;
    private const uint Padding = 0x200;
    private const uint CompressionMask = 0x3f;
    private const int BufferSize = 128 * 1024;
    private const int MaxTableSize = 64 * 1024 * 1024;
    private const int MaxLzmaDictionarySize = 64 * 1024 * 1024;

    private sealed record SourceHeader(string GenerationVersion, string EngineVersion);
    private readonly record struct Block(uint UncompressedSize, uint CompressedSize, ushort Flags, long Offset);
    private static readonly ConditionalWeakTable<BundleFileInstance, SourceHeader> Converted = new();

    internal static BundleFileInstance Load(AssetsManager manager, string path, bool unpackIfPacked)
    {
        using var original = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[12];
        int read = original.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
        if (read != prefix.Length || !prefix[..8].SequenceEqual("UnityFS\0"u8))
            return manager.LoadBundleFile(path, unpackIfPacked);
        uint version = BinaryPrimitives.ReadUInt32BigEndian(prefix[8..]);
        if (version < 9)
            return manager.LoadBundleFile(path, unpackIfPacked);
        if (version != 9)
            throw new NotSupportedException($"UnityFS version {version} is not supported.");

        var normalized = OpenScratch();
        try
        {
            SourceHeader source = Normalize(original, normalized);
            normalized.Position = 0;
            // The temp stream is owned by the returned bundle. Use the actual path
            // for AssetsTools' dependency lookup and the outer file transaction.
            var result = manager.LoadBundleFile(normalized, path, unpackIfPacked: false);
            if (ReferenceEquals(result.BundleStream, normalized))
                Converted.Add(result, source);
            else
                normalized.Dispose(); // already cached in this manager
            return result;
        }
        catch
        {
            normalized.Dispose();
            throw;
        }
    }

    internal static bool TryWrite(BundleFileInstance bundle, string path, bool recompress)
    {
        if (!Converted.TryGetValue(bundle, out var source))
            return false;

        var directories = new List<AssetBundleDirectoryInfo>();
        using var payload = OpenScratch();
        var assetWriter = new AssetsFileWriter(payload);
        foreach (var entry in bundle.file.BlockAndDirInfo.DirectoryInfos)
        {
            if (entry.ReplacerType == ContentReplacerType.Remove)
                continue;

            long offset = assetWriter.Position;
            if (entry.ReplacerType == ContentReplacerType.AddOrModify)
                entry.Replacer.Write(assetWriter, finalWrite: true);
            else
            {
                var input = bundle.file.DataReader.BaseStream;
                input.Position = entry.Offset;
                CopyExact(input, payload, entry.DecompressedSize);
            }
            directories.Add(new AssetBundleDirectoryInfo
            {
                Name = entry.Name, Flags = entry.Flags, Offset = offset,
                DecompressedSize = assetWriter.Position - offset,
            });
        }
        assetWriter.Flush();

        int chunkSize = recompress ? BufferSize : 16 * 1024 * 1024;
        if (((payload.Length + chunkSize - 1) / chunkSize) * 18L + 24 > MaxTableSize)
            throw new IOException("UnityFS v9 output has too many blocks.");

        using var writer = new AssetsFileWriter(path);
        var header = BuildHeader(source, 9, 0, 0, DirectoryFlag | InfoAtEnd);
        header.Write(writer);
        AlignWriter(writer);
        long dataStart = writer.Position;
        payload.Position = 0;
        var blocks = new List<Block>();

        if (recompress)
        {
            byte[] raw = new byte[chunkSize];
            while (payload.Position < payload.Length)
            {
                int count = (int)Math.Min(raw.Length, payload.Length - payload.Position);
                payload.ReadExactly(raw.AsSpan(0, count));
                var compressed = LZ4Codec.Encode32HC(raw, 0, count);
                bool useCompressed = compressed.Length < count;
                long offset = writer.Position - dataStart;
                writer.Write(useCompressed ? compressed.AsSpan() : raw.AsSpan(0, count));
                blocks.Add(new Block((uint)count, (uint)(useCompressed ? compressed.Length : count),
                    useCompressed ? (ushort)3 : (ushort)0, offset));
                AlignWriter(writer);
            }
        }
        else
        {
            while (payload.Position < payload.Length)
            {
                uint count = (uint)Math.Min(chunkSize, payload.Length - payload.Position);
                long offset = writer.Position - dataStart;
                CopyExact(payload, writer.BaseStream, count);
                blocks.Add(new Block(count, count, 0, offset));
                AlignWriter(writer);
            }
        }
        long tableStart = writer.Position;
        WriteTable(writer, 9, blocks, directories);
        long totalSize = writer.Position;
        long tableSize = totalSize - tableStart;
        if (tableSize > MaxTableSize)
            throw new IOException("UnityFS v9 output table exceeds supported size.");
        writer.Position = 0;
        BuildHeader(source, 9, totalSize, (uint)tableSize, DirectoryFlag | InfoAtEnd).Write(writer);
        return true;
    }

    private static SourceHeader Normalize(Stream original, Stream destination)
    {
        var reader = new AssetsFileReader(original) { BigEndian = true };
        var source = new SourceHeader(ReadCString(reader, 1024), ReadCString(reader, 1024));
        long totalSize = reader.ReadInt64();
        uint tableCompressed = reader.ReadUInt32();
        uint tableUncompressed = reader.ReadUInt32();
        uint flags = reader.ReadUInt32();
        if (flags != (flags & (DirectoryFlag | InfoAtEnd | Padding | CompressionMask)) || (flags & DirectoryFlag) == 0)
            throw new NotSupportedException($"Unsupported UnityFS v9 container flags 0x{flags:X}.");
        if (totalSize != original.Length || tableCompressed > MaxTableSize || tableUncompressed > MaxTableSize)
            throw new InvalidDataException("UnityFS v9 bundle size or table size is out of bounds.");

        long headerEnd = Align16(reader.Position);
        long tableStart = (flags & InfoAtEnd) != 0 ? totalSize - tableCompressed : headerEnd;
        long dataStart = (flags & InfoAtEnd) != 0 ? headerEnd : tableStart + tableCompressed;
        if ((flags & Padding) != 0)
            dataStart = Align16(dataStart);
        long dataEnd = (flags & InfoAtEnd) != 0 ? tableStart : totalSize;
        if (tableStart < headerEnd || tableStart + tableCompressed > totalSize || dataStart > dataEnd)
            throw new InvalidDataException("UnityFS v9 table or payload range is out of bounds.");

        using var table = new MemoryStream((int)tableUncompressed);
        Decode(original, tableStart, tableCompressed, tableUncompressed, (ushort)(flags & CompressionMask), table);
        table.Position = 0;
        using var metadata = new AssetsFileReader(table) { BigEndian = true };
        table.Seek(16, SeekOrigin.Begin); // bundle data hash
        int count = metadata.ReadInt32();
        if (count < 0 || (long)count * 18 > table.Length - table.Position - 4)
            throw new InvalidDataException("Invalid UnityFS v9 block count.");
        var blocks = new List<Block>(count);
        long logicalSize = 0;
        for (int i = 0; i < count; i++)
        {
            uint uncompressed = metadata.ReadUInt32();
            uint compressed = metadata.ReadUInt32();
            ushort blockFlags = metadata.ReadUInt16();
            long offset = metadata.ReadInt64();
            if ((blockFlags & ~0x7F) != 0)
                throw new NotSupportedException($"Unsupported UnityFS v9 block flags 0x{blockFlags:X}.");
            if (offset < 0 || offset > dataEnd - dataStart || compressed > dataEnd - dataStart - offset)
                throw new InvalidDataException($"UnityFS v9 block {i} lies outside its payload.");
            blocks.Add(new Block(uncompressed, compressed, blockFlags, offset));
            logicalSize = checked(logicalSize + uncompressed);
        }
        int entries = metadata.ReadInt32();
        if (entries < 0 || (long)entries * 21 > table.Length - table.Position)
            throw new InvalidDataException("Invalid UnityFS v9 directory count.");
        var directories = new List<AssetBundleDirectoryInfo>(entries);
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < entries; i++)
        {
            long offset = metadata.ReadInt64();
            long length = metadata.ReadInt64();
            uint fileFlags = metadata.ReadUInt32();
            string name = ReadCString(metadata, 4096);
            if (offset < 0 || length < 0 || offset > logicalSize || length > logicalSize - offset || !names.Add(name))
                throw new InvalidDataException($"Invalid UnityFS v9 directory entry '{name}'.");
            directories.Add(new AssetBundleDirectoryInfo
            {
                Name = name, Flags = fileFlags, Offset = offset, DecompressedSize = length,
            });
        }

        var normalizedBlocks = blocks.Select(b => new Block(b.UncompressedSize, b.UncompressedSize, 0, 0)).ToArray();
        var writer = new AssetsFileWriter(destination);
        BuildHeader(source, 8, 0, 0, DirectoryFlag).Write(writer);
        AlignWriter(writer);
        long tableOffset = writer.Position;
        WriteTable(writer, 8, normalizedBlocks, directories);
        uint size = checked((uint)(writer.Position - tableOffset));
        foreach (var block in blocks)
            Decode(original, dataStart + block.Offset, block.CompressedSize,
                block.UncompressedSize, (ushort)(block.Flags & CompressionMask), destination);
        long total = writer.Position;
        writer.Position = 0;
        BuildHeader(source, 8, total, size, DirectoryFlag).Write(writer);
        writer.Flush();
        return source;
    }

    private static void Decode(Stream original, long offset, uint compressed, uint uncompressed, ushort mode, Stream dest)
    {
        using var segment = new SegmentStream(original, offset, compressed);
        long start = dest.Position;
        switch (mode)
        {
            case 0:
                if (compressed != uncompressed)
                    throw new InvalidDataException("Uncompressed UnityFS block sizes differ.");
                CopyExact(segment, dest, uncompressed);
                break;
            case 1:
                Span<byte> lzma = stackalloc byte[5];
                segment.ReadExactly(lzma);
                if (BinaryPrimitives.ReadUInt32LittleEndian(lzma[1..]) > MaxLzmaDictionarySize)
                    throw new NotSupportedException("UnityFS v9 LZMA dictionary exceeds the 64 MiB limit.");
                segment.Position = 0;
                SevenZipHelper.StreamDecompress(segment, dest, compressed, uncompressed);
                break;
            case 2 or 3:
                using (var decoder = new Lz4DecoderStream(segment))
                {
                    CopyExact(decoder, dest, uncompressed);
                    if (decoder.ReadByte() != -1)
                        throw new InvalidDataException("UnityFS v9 LZ4 block exceeds its declared size.");
                }
                break;
            default:
                throw new NotSupportedException($"Unsupported UnityFS v9 block compression {mode}.");
        }
        if (dest.Position - start != uncompressed)
            throw new InvalidDataException("UnityFS v9 decoded block size does not match its declaration.");
    }

    private static AssetBundleHeader BuildHeader(SourceHeader source, uint version, long size, uint tableSize, uint flags) =>
        new()
        {
            Signature = "UnityFS", Version = version,
            GenerationVersion = source.GenerationVersion, EngineVersion = source.EngineVersion,
            FileStreamHeader = new AssetBundleFSHeader
            {
                TotalFileSize = size, CompressedSize = tableSize, DecompressedSize = tableSize,
                Flags = (AssetBundleFSHeaderFlags)flags,
            },
        };

    private static void WriteTable(AssetsFileWriter writer, uint version, IReadOnlyList<Block> blocks,
        IReadOnlyList<AssetBundleDirectoryInfo> directories)
    {
        writer.BigEndian = true;
        writer.Write(0UL);
        writer.Write(0UL);
        writer.Write(blocks.Count);
        foreach (var block in blocks)
        {
            writer.Write(block.UncompressedSize);
            writer.Write(block.CompressedSize);
            writer.Write(block.Flags);
            if (version == 9)
                writer.Write(block.Offset);
        }
        writer.Write(directories.Count);
        foreach (var entry in directories)
        {
            writer.Write(entry.Offset);
            writer.Write(entry.DecompressedSize);
            writer.Write(entry.Flags);
            writer.WriteNullTerminated(entry.Name);
        }
    }

    private static string ReadCString(AssetsFileReader reader, int max)
    {
        Span<byte> bytes = stackalloc byte[max];
        for (int i = 0; i < max; i++)
        {
            byte datum = reader.ReadByte();
            if (datum == 0)
                return Encoding.UTF8.GetString(bytes[..i]);
            bytes[i] = datum;
        }
        throw new InvalidDataException("UnityFS v9 string exceeds supported length.");
    }

    private static long Align16(long value) => checked((value + 15) & ~15L);

    private static void AlignWriter(AssetsFileWriter writer)
    {
        while ((writer.Position & 15) != 0)
            writer.Write((byte)0);
    }

    private static FileStream OpenScratch() =>
        new(Path.Combine(Path.GetTempPath(), $"ufr-v9-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete,
            BufferSize, FileOptions.DeleteOnClose);

    private static void CopyExact(Stream input, Stream output, long count)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (count > 0)
            {
                int size = (int)Math.Min(buffer.Length, count);
                input.ReadExactly(buffer.AsSpan(0, size));
                output.Write(buffer, 0, size);
                count -= size;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
