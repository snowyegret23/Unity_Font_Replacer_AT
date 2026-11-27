using System.Buffers.Binary;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using LZ4ps;
using SevenZip.Compression.LZMA;
using UnityFontReplacer.Core;

internal static class V9Checks
{
    internal static void Run(string root, byte[] serializedAsset, byte[] expectedUpdatedObject)
    {
        // The single directory crosses two logical blocks whose physical order
        // is reversed. That catches linear reads as well as wrong offset bases.
        foreach (bool infoAtEnd in new[] { false, true })
        foreach (int tablePacking in new[] { 0, 1, 2 })
        {
            string input = Path.Combine(root, $"v9-{infoAtEnd}-{tablePacking}.bundle");
            MakeFixture(input, serializedAsset, infoAtEnd, tablePacking);
            byte[] original = File.ReadAllBytes(input);
            foreach (bool recompress in new[] { false, true })
            {
                string output = Path.Combine(root, $"saved-v9-{infoAtEnd}-{tablePacking}-{recompress}.bundle");
                var manager = new AssetsManager();
                try
                {
                    var loaded = BundleProcessor.LoadBundle(manager, input);
                    Check(loaded.path == Path.GetFullPath(input), "V9 normalization did not preserve original path.");
                    var asset = manager.LoadAssetsFileFromBundle(loaded, 0);
                    asset.file.Metadata.AssetInfos.Single().SetNewData(expectedUpdatedObject);
                    BundleProcessor.SaveBundle(loaded, [(0, asset)], output, recompress);
                    manager.UnloadAll(true);
                    var rawOutput = File.ReadAllBytes(output);
                    Check(rawOutput.AsSpan(0, 8).SequenceEqual("UnityFS\0"u8)
                        && BinaryPrimitives.ReadUInt32BigEndian(rawOutput.AsSpan(8, 4)) == 9, "Output was not UnityFS v9.");
                    var saved = BundleProcessor.LoadBundle(manager, output);
                    var savedAsset = manager.LoadAssetsFileFromBundle(saved, 0).file;
                    var info = savedAsset.Metadata.AssetInfos.Single();
                    savedAsset.Reader.Position = info.GetAbsoluteByteOffset(savedAsset);
                    Check(savedAsset.Reader.ReadBytes((int)info.ByteSize).SequenceEqual(expectedUpdatedObject), "Incorrect v9 saved object bytes.");
                    Check(File.ReadAllBytes(input).SequenceEqual(original), "V9 original was changed in output-only mode.");
                }
                finally { manager.UnloadAll(true); }
            }
        }
        Console.WriteLine("AT UnityFS v9 raw/LZ4/LZMA metadata and mixed-block roundtrips passed.");
    }

    private static void Check(bool result, string failure)
    {
        if (!result)
            throw new InvalidDataException(failure);
    }

    private static void MakeFixture(string path, byte[] serialized, bool infoAtEnd, int tablePacking)
    {
        int split = serialized.Length / 2;
        byte[] a = serialized[..split], b = serialized[split..];
        byte[] packedA = LZ4Codec.Encode32HC(a, 0, a.Length);
        byte[] packedB = SevenZipHelper.Compress(b);
        int offsetA = (packedB.Length + 15) & ~15;

        using var table = new MemoryStream();
        var metadata = new AssetsFileWriter(table) { BigEndian = true };
        metadata.Write(new byte[16]);
        metadata.Write(2);
        metadata.Write((uint)a.Length);
        metadata.Write((uint)packedA.Length);
        metadata.Write((ushort)3);
        metadata.Write((long)offsetA);
        metadata.Write((uint)b.Length);
        metadata.Write((uint)packedB.Length);
        metadata.Write((ushort)1);
        metadata.Write(0L);
        metadata.Write(1);
        metadata.Write(0L);
        metadata.Write((long)serialized.Length);
        metadata.Write(4U);
        metadata.WriteNullTerminated("CAB-v9");
        byte[] rawTable = table.ToArray();
        byte[] tableBytes = tablePacking switch
        {
            1 => SevenZipHelper.Compress(rawTable),
            2 => LZ4Codec.Encode32HC(rawTable, 0, rawTable.Length),
            _ => rawTable,
        };

        using var writer = new AssetsFileWriter(path);
        var header = new AssetBundleHeader
        {
            Signature = "UnityFS", Version = 9, GenerationVersion = "5.x.x", EngineVersion = "6000.0.1f1",
            FileStreamHeader = new AssetBundleFSHeader
            {
                Flags = (AssetBundleFSHeaderFlags)(0x240 | (infoAtEnd ? 0x80 : 0) | tablePacking),
                CompressedSize = (uint)tableBytes.Length, DecompressedSize = (uint)rawTable.Length,
            },
        };
        header.Write(writer);
        writer.Align16();
        if (!infoAtEnd)
        {
            writer.Write(tableBytes);
            writer.Align16();
        }
        writer.Write(packedB);
        writer.Align16();
        writer.Write(packedA);
        if (infoAtEnd)
            writer.Write(tableBytes);
        header.FileStreamHeader.TotalFileSize = writer.Position;
        writer.Position = 0;
        header.Write(writer);
    }
}
