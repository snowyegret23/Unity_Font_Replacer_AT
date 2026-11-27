using AssetsTools.NET;
using AssetsTools.NET.Extra;
using UnityFontReplacer.Core;
using UnityFontReplacer.Models;

static void Assert(bool condition, string reason)
{
    if (!condition)
        throw new Exception(reason);
}

static void Reject<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

static byte[] ReadAsset(AssetsFile file)
{
    var info = file.Metadata.AssetInfos.Single();
    file.Reader.Position = info.GetAbsoluteByteOffset(file);
    return file.Reader.ReadBytes((int)info.ByteSize);
}

static void MakeFixture(string path, byte[] original, bool font = false)
{
    int type = font ? (int)AssetClassID.Font : 49;
    var item = new AssetFileInfo { PathId = -7, TypeId = type, TypeIdOrIndex = 0, ScriptTypeIndex = ushort.MaxValue };
    var typeTree = new TypeTreeType { TypeId = type, ScriptTypeIndex = ushort.MaxValue, TypeHash = new Hash128(new byte[16]) };
    if (font)
    {
        var field = new AssetTypeTemplateField
        {
            Name = "Base", Type = "Font", Children =
            [
                new AssetTypeTemplateField
                {
                    Name = "m_FontData", Type = "TypelessData", IsArray = true, IsAligned = false, Children =
                    [
                        new AssetTypeTemplateField { Name = "size", Type = "int", Children = [] },
                        new AssetTypeTemplateField { Name = "data", Type = "UInt8", Children = [] },
                    ],
                },
            ],
        };
        typeTree = TemplateFieldToTypeTree.Convert(field, type);
        original = [.. BitConverter.GetBytes(original.Length), .. original];
    }
    item.SetNewData(original);
    var file = new AssetsFile
    {
        Header = new AssetsFileHeader { Version = 22 },
        Metadata = new AssetsFileMetadata
        {
            UnityVersion = "2021.3.0f1",
            TargetPlatform = 19,
            TypeTreeEnabled = font,
            TypeTreeTypes = [typeTree],
            AssetInfos = new List<AssetFileInfo> { item },
            Externals = [],
            ScriptTypes = [],
            RefTypes = [],
            UserInformation = "",
        },
    };
    using var writer = new AssetsFileWriter(path);
    file.Write(writer);
}

static void MakeBundle(string path, byte[] assets, bool duplicateCab = false)
{
    var header = new AssetBundleHeader
    {
        Signature = "UnityFS",
        Version = 8,
        GenerationVersion = "5.x.x",
        EngineVersion = "2021.3.0f1",
        FileStreamHeader = new AssetBundleFSHeader { Flags = AssetBundleFSHeaderFlags.HasDirectoryInfo },
    };
    int total = duplicateCab ? assets.Length * 2 : assets.Length;
    var table = new AssetBundleBlockAndDirInfo
    {
        BlockInfos = [new AssetBundleBlockInfo { DecompressedSize = (uint)total, CompressedSize = (uint)total }],
        DirectoryInfos = [new AssetBundleDirectoryInfo { Name = "CAB-test", Flags = 4, Offset = 0, DecompressedSize = assets.Length }],
    };
    if (duplicateCab)
        table.DirectoryInfos.Add(new AssetBundleDirectoryInfo
        { Name = "CAB-other", Flags = 4, Offset = assets.Length, DecompressedSize = assets.Length });
    using var writer = new AssetsFileWriter(path);
    header.Write(writer);
    writer.Align16();
    long infoStart = writer.Position;
    table.Write(writer);
    uint tableSize = (uint)(writer.Position - infoStart);
    writer.Write(assets);
    if (duplicateCab) writer.Write(assets);
    header.FileStreamHeader.TotalFileSize = writer.Position;
    header.FileStreamHeader.CompressedSize = tableSize;
    header.FileStreamHeader.DecompressedSize = tableSize;
    writer.Position = 0;
    header.Write(writer);
}

var flat = """{"k":{"File":"fonts.bundle","assets_name":"CAB-test","Type":"SDF","Path_ID":"-7","Replace_to":"Example","force_raster":true,"swizzle":0,"process_swizzle":"False"}}""";
foreach (string json in new[] { flat, "{\"game_path\":\"MyGame\",\"fonts\":" + flat + "}" })
{
    var entry = FontMapping.Parse(json).Fonts["k"];
    Assert(entry.PathId == -7 && entry.ForceRaster == "True" && entry.Swizzle == "False", "Mapping fields");
    Assert(FontReplacer.MatchesInnerFile(entry, @"bundle\CAB-test"), "Correct CAB");
    Assert(!FontReplacer.MatchesInnerFile(entry, "CAB-other"), "Same PathID in wrong CAB");
}
Reject<System.Text.Json.JsonException>(() => FontMapping.Parse("""{"fonts":[]}"""));

int active = 0, initialized = 0, disposed = 0, concurrent = 0;
var values = FontScanner.ScanInOrder(
    Enumerable.Range(0, 12).Select(i => i.ToString()).ToArray(), 2,
    () => { Interlocked.Increment(ref initialized); return new object(); },
    (path, _) =>
    {
        int count = Interlocked.Increment(ref active);
        if (count > 1) Interlocked.Exchange(ref concurrent, 1);
        Assert(count <= 2, "Worker limit enforced");
        Thread.Sleep(40);
        Interlocked.Decrement(ref active);
        return int.Parse(path);
    },
    _ => Interlocked.Increment(ref disposed));
Assert(values.SequenceEqual(Enumerable.Range(0, 12)), "Parallel scan order");
Assert(initialized == disposed && concurrent == 1, "Concurrent workers disposed");

string root = Path.Combine(Path.GetTempPath(), "ufr-parity-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string game = Path.Combine(root, "Game_Data");
    string outRoot = Path.Combine(root, "Output");
    string original = Path.Combine(game, "sub", "sample.assets");
    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
    string output = SaveStrategy.ResolveOutputPath(original, game, outRoot);
    Assert(output == Path.Combine(outRoot, "sub", "sample.assets"), "Output preserves subdirectories");
    Reject<IOException>(() => SaveStrategy.CheckOutputRoot(game, game));
    Reject<IOException>(() => SaveStrategy.CheckOutputRoot(game, Path.Combine(game, "output")));
    Reject<IOException>(() => SaveStrategy.ResolveOutputPath(Path.Combine(root, "outside"), game, outRoot));
    if (!OperatingSystem.IsWindows())
    {
        string alias = Path.Combine(root, "alias");
        Directory.CreateSymbolicLink(alias, game);
        Reject<IOException>(() => SaveStrategy.CheckOutputRoot(game, alias));
        Directory.CreateDirectory(outRoot);
        Directory.CreateSymbolicLink(Path.Combine(outRoot, "sub"), Path.GetDirectoryName(original)!);
        Reject<IOException>(() => SaveStrategy.ResolveOutputPath(original, game, outRoot));
        Directory.Delete(Path.Combine(outRoot, "sub"));
    }

    string v9 = Path.Combine(root, "v9.bundle");
    File.WriteAllBytes(v9, [.. "UnityFS\0"u8, 0, 0, 0, 9]);
    Reject<NotSupportedException>(() => BundleProcessor.EnsureSupportedBundle(v9));

    byte[] before = [1, 2, 3], after = [5, 6, 7, 8];
    MakeFixture(original, before);
    byte[] originalFile = File.ReadAllBytes(original);
    var am = new AssetsManager();
    var tx = new ReplacementTransaction();
    try
    {
        var inst = am.LoadAssetsFile(original, loadDeps: false);
        inst.file.Metadata.AssetInfos.Single().SetNewData(after);
        SaveStrategy.SaveAssetsFile(inst, output, tx);
        Assert(tx.HasPublished(output), "Transaction tracks staged output");
        Assert(File.ReadAllBytes(original).SequenceEqual(originalFile), "Output-only left original untouched");
        using (var stream = File.OpenRead(output))
        {
            var file = new AssetsFile();
            file.Read(stream);
            Assert(ReadAsset(file).SequenceEqual(after), "Saved asset bytes");
        }
        string priorOutput = Path.Combine(outRoot, "prior");
        File.WriteAllText(priorOutput, "original output");
        ReplacementTransaction.WriteValidated(priorOutput, path => File.WriteAllText(path, "new output"),
            _ => { }, null, tx);
        Reject<InvalidDataException>(() => ReplacementTransaction.WriteValidated(priorOutput,
            path => File.WriteAllText(path, "bad"), _ => throw new InvalidDataException(), null, tx));
        Assert(File.ReadAllText(priorOutput) == "new output", "Invalid staged bytes were not installed");
        tx.Rollback();
        Assert(!File.Exists(output) && File.ReadAllText(priorOutput) == "original output", "Full rollback");
    }
    finally { am.UnloadAll(true); }

    string bundlePath = Path.Combine(root, "fixture.bundle");
    string savedBundle = Path.Combine(root, "saved.bundle");
    MakeBundle(bundlePath, originalFile);
    BundleProcessor.EnsureSupportedBundle(bundlePath);
    try
    {
        var bundle = am.LoadBundleFile(bundlePath);
        var inner = am.LoadAssetsFileFromBundle(bundle, 0);
        inner.file.Metadata.AssetInfos.Single().SetNewData(after);
        BundleProcessor.SaveBundle(bundle, [(0, inner)], savedBundle);
        am.UnloadAll(true);
        var saved = am.LoadBundleFile(savedBundle);
        Assert(ReadAsset(am.LoadAssetsFileFromBundle(saved, 0).file).SequenceEqual(after), "Saved bundle bytes");
    }
    finally { am.UnloadAll(true); }

    string gameRoot = Path.Combine(root, "Game");
    string gameData = Path.Combine(gameRoot, "Synthetic_Data");
    Directory.CreateDirectory(gameData);
    string fontAssetPath = Path.Combine(root, "font.assets");
    MakeFixture(fontAssetPath, before, font: true);
    string fontBundlePath = Path.Combine(gameData, "fonts.bundle");
    MakeBundle(fontBundlePath, File.ReadAllBytes(fontAssetPath), duplicateCab: true);
    string fontBundleCopy = Path.Combine(gameData, "alternate", "fonts.bundle");
    Directory.CreateDirectory(Path.GetDirectoryName(fontBundleCopy)!);
    File.Copy(fontBundlePath, fontBundleCopy);
    byte[] unmodifiedBundle = File.ReadAllBytes(fontBundlePath);
    string replacement = Path.Combine(root, "replacement.ttf");
    File.WriteAllBytes(replacement, after);
    var mapping = new FontMapping { GamePath = gameRoot, Fonts = new()
    {
        ["replace"] = new() { File = "fonts.bundle", AssetsName = "CAB-test", PathId = -7,
            Type = FontType.TTF, ReplaceTo = replacement, Name = "Test" },
    } };
    using (var context = new AssetsContext(gameData, managedPath: null))
    {
        var replacer = new FontReplacer(context);
        Assert(replacer.ReplaceFromMapping(mapping, outRoot) == 2, "Both identical tuples should be replaced");
    }
    foreach (var path in new[] { fontBundlePath, fontBundleCopy })
    {
        Assert(File.ReadAllBytes(path).SequenceEqual(unmodifiedBundle), "Output-only original preserved");
        try
        {
            var saved = am.LoadBundleFile(SaveStrategy.ResolveOutputPath(path, gameData, outRoot));
            var patched = am.LoadAssetsFileFromBundle(saved, 0);
            var unpatched = am.LoadAssetsFileFromBundle(saved, 1);
            Assert(TtfFontHandler.ReadFontData(am.GetBaseField(patched, patched.file.Metadata.AssetInfos.Single()))!.SequenceEqual(after), "Matched CAB TTF changed");
            Assert(TtfFontHandler.ReadFontData(am.GetBaseField(unpatched, unpatched.file.Metadata.AssetInfos.Single()))!.SequenceEqual(before), "Wrong CAB TTF stayed intact");
        }
        finally { am.UnloadAll(true); }
    }
    mapping.Fonts["missing"] = new() { File = "fonts.bundle", AssetsName = "CAB-test", PathId = -5,
        Type = FontType.TTF, ReplaceTo = replacement };
    using (var context = new AssetsContext(gameData, managedPath: null))
        Reject<InvalidDataException>(() => new FontReplacer(context).ReplaceFromMapping(mapping));
    Assert(File.ReadAllBytes(fontBundlePath).SequenceEqual(unmodifiedBundle), "Rollback restored installed changes");
    Assert(File.ReadAllBytes(fontBundleCopy).SequenceEqual(unmodifiedBundle), "Rollback restored all matching files");

    Assert(!Directory.GetFiles(root, ".ufr-*", SearchOption.AllDirectories).Any(), "Temporary files cleaned");
}
finally { Directory.Delete(root, recursive: true); }

Console.WriteLine("AT parity smoke tests passed.");
