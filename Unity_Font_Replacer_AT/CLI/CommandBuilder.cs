using System.CommandLine;

namespace UnityFontReplacer.CLI;

public static class CommandBuilder
{
    public static RootCommand Build()
    {
        var gamePathOption = new Option<string>(
            aliases: ["--gamepath", "-g"],
            description: "Unity game path")
        { IsRequired = true };

        // parse subcommand
        var parseCommand = new Command("parse", Strings.Get("cmd_parse"));
        parseCommand.AddOption(gamePathOption);
        var parsePs5Option = new Option<bool>("--ps5-swizzle", "Detect PS5 swizzle state");
        var parseMaxWorkersOption = new Option<int>("--max-workers", () => 1, "Max parallel scan workers");
        parseCommand.AddOption(parsePs5Option);
        parseCommand.AddOption(parseMaxWorkersOption);
        parseCommand.SetHandler(async (gamePath, ps5, maxWorkers) =>
        {
            await ParseCommand.ExecuteAsync(gamePath, ps5, maxWorkers);
        }, gamePathOption, parsePs5Option, parseMaxWorkersOption);

        // list subcommand
        var listCommand = new Command("list", Strings.Get("cmd_list"));
        var listFileOption = new Option<string>("--file", "Font mapping JSON file") { IsRequired = true };
        listCommand.AddOption(gamePathOption);
        listCommand.AddOption(listFileOption);
        listCommand.SetHandler(async (gamePath, file) =>
        {
            await ListCommand.ExecuteAsync(gamePath, file);
        }, gamePathOption, listFileOption);

        // export subcommand
        var exportCommand = new Command("export", Strings.Get("cmd_export"));
        exportCommand.AddOption(gamePathOption);
        exportCommand.SetHandler(async (gamePath) =>
        {
            await ExportFontsCommand.ExecuteAsync(gamePath);
        }, gamePathOption);

        // makesdf subcommand
        var makeSdfCommand = new Command("makesdf", Strings.Get("cmd_makesdf"));
        var ttfOption = new Option<string>("--ttf", "TTF file path") { IsRequired = true };
        var atlasSizeOption = new Option<string>("--atlas-size", () => "4096,4096", "Atlas size (W,H)");
        var pointSizeOption = new Option<int>("--point-size", () => 0, "Point size (0=auto)");
        var paddingOption = new Option<int>("--padding", () => 7, "Atlas padding");
        var charsetOption = new Option<string>("--charset", () => MakeSdfCommand.DefaultCharsetArgument, "Charset file or literal");
        var renderModeOption = new Option<string>("--rendermode", () => "sdf", "sdf or raster");
        makeSdfCommand.AddOption(ttfOption);
        makeSdfCommand.AddOption(atlasSizeOption);
        makeSdfCommand.AddOption(pointSizeOption);
        makeSdfCommand.AddOption(paddingOption);
        makeSdfCommand.AddOption(charsetOption);
        makeSdfCommand.AddOption(renderModeOption);
        makeSdfCommand.SetHandler(async (ttf, atlasSize, pointSize, padding, charset, renderMode) =>
        {
            await MakeSdfCommand.ExecuteAsync(ttf, atlasSize, pointSize, padding, charset, renderMode);
        }, ttfOption, atlasSizeOption, pointSizeOption, paddingOption, charsetOption, renderModeOption);

        // batch subcommand
        var batchCommand = BatchCommand.Build(gamePathOption);

        // diag subcommand (번들 내부 에셋 타입 진단)
        var diagCommand = new Command("diag", "Diagnose bundle contents");
        var diagFileOption = new Option<string>("--file", "Bundle file path") { IsRequired = true };
        diagCommand.AddOption(diagFileOption);
        diagCommand.SetHandler(async (file) =>
        {
            await Task.CompletedTask;
            DiagBundle(file);
        }, diagFileOption);

        // root
        var rootCommand = new RootCommand(Strings.Get("app_description"));
        rootCommand.AddCommand(parseCommand);
        rootCommand.AddCommand(listCommand);
        rootCommand.AddCommand(batchCommand);
        rootCommand.AddCommand(exportCommand);
        rootCommand.AddCommand(makeSdfCommand);
        rootCommand.AddCommand(diagCommand);

        return rootCommand;
    }

    private static void DiagBundle(string filePath)
    {
        var am = new AssetsTools.NET.Extra.AssetsManager();
        try
        {
            Console.WriteLine($"File: {filePath}");

            if (UnityFontReplacer.Core.FontScanner.IsBundleFile(filePath))
            {
                var bundle = am.LoadBundleFile(filePath, unpackIfPacked: false);
                var dirs = bundle.file.BlockAndDirInfo.DirectoryInfos;
                Console.WriteLine($"Bundle entries: {dirs.Count}");

                for (int i = 0; i < dirs.Count; i++)
                {
                    var dir = dirs[i];
                    if (!dir.IsSerialized) { Console.WriteLine($"  [{i}] {dir.Name} (not serialized)"); continue; }
                    var inst = am.LoadAssetsFileFromBundle(bundle, i);
                    var meta = inst.file.Metadata;
                    Console.WriteLine($"  [{i}] {dir.Name}: {meta.AssetInfos.Count} assets, TypeTree={meta.TypeTreeEnabled}, Externals={meta.Externals.Count}");

                    var types = new Dictionary<int, int>();
                    foreach (var info in meta.AssetInfos)
                    {
                        types.TryGetValue(info.TypeId, out int c);
                        types[info.TypeId] = c + 1;
                    }
                    foreach (var kv in types.OrderBy(x => x.Key))
                        Console.WriteLine($"    TypeId {kv.Key} ({(AssetsTools.NET.Extra.AssetClassID)kv.Key}): {kv.Value}");

                    // Externals 출력
                    for (int e = 0; e < meta.Externals.Count; e++)
                        Console.WriteLine($"    External[{e}]: {meta.Externals[e].PathName}");

                    // Texture2D 상세
                    foreach (var info in meta.AssetInfos)
                    {
                        if (info.TypeId != 28) continue;
                        try
                        {
                            var bf = am.GetBaseField(inst, info);
                            var texFile = AssetsTools.NET.Texture.TextureFile.ReadTextureFile(bf);
                            // raw bytes 분석
                            var raw = texFile.pictureData ?? texFile.FillPictureData(inst);
                            int rawLen = raw?.Length ?? 0;
                            int firstNonzero = -1;
                            if (raw != null)
                                for (int ri = 0; ri < raw.Length; ri++)
                                    if (raw[ri] != 0) { firstNonzero = ri; break; }
                            Console.WriteLine($"    Tex #{info.PathId}: {texFile.m_Name} {texFile.m_Width}x{texFile.m_Height} fmt={texFile.m_TextureFormat} raw={rawLen} firstNZ@{firstNonzero}(row{firstNonzero / texFile.m_Width})");
                        }
                        catch (Exception ex) { Console.WriteLine($"    Tex #{info.PathId}: ERROR {ex.Message}"); }
                    }

                    // Material 상세
                    foreach (var info in meta.AssetInfos)
                    {
                        if (info.TypeId != 21) continue;
                        try
                        {
                            var bf = am.GetBaseField(inst, info);
                            var matName = bf["m_Name"].AsString;
                            var texEnvs = bf["m_SavedProperties"]["m_TexEnvs"]["Array"];
                            string mainTexInfo = "none";
                            if (!texEnvs.IsDummy)
                            {
                                foreach (var entry in texEnvs.Children)
                                {
                                    var key = entry["first"].AsString;
                                    if (key == "_MainTex")
                                    {
                                        var tex = entry["second"]["m_Texture"];
                                        mainTexInfo = $"FileId={tex["m_FileID"].AsInt} PathId={tex["m_PathID"].AsLong}";
                                        break;
                                    }
                                }
                            }
                            Console.WriteLine($"    Mat #{info.PathId}: {matName} -> _MainTex({mainTexInfo})");
                        }
                        catch (Exception ex) { Console.WriteLine($"    Mat #{info.PathId}: ERROR {ex.Message}"); }
                    }

                    // MonoBehaviour 상세
                    foreach (var info in meta.AssetInfos)
                    {
                        if (info.TypeId != 114) continue;
                        try
                        {
                            var origSize = info.ByteSize;
                            var bf = am.GetBaseField(inst, info);
                            var reserializedSize = bf.WriteToByteArray().Length;
                            var sizeDiff = origSize - reserializedSize;
                            var name = bf["m_Name"].AsString;
                            Console.WriteLine($"    MB #{info.PathId}: {name} (origSize={origSize} reserialized={reserializedSize} trailing={sizeDiff})");

                            // 아틀라스 참조
                            var atArr = bf["m_AtlasTextures"]["Array"];
                            if (!atArr.IsDummy)
                            {
                                for (int a = 0; a < atArr.Children.Count; a++)
                                {
                                    var fid = atArr[a]["m_FileID"].AsInt;
                                    var pid = atArr[a]["m_PathID"].AsLong;
                                    Console.WriteLine($"      Atlas[{a}]: FileId={fid}, PathId={pid}");
                                }
                            }

                            // 머티리얼 참조 - 여러 필드명 시도
                            foreach (var matName in new[] { "m_Material", "material", "m_material" })
                            {
                                var mat = bf[matName];
                                if (!mat.IsDummy)
                                {
                                    Console.WriteLine($"      Material ({matName}): FileId={mat["m_FileID"].AsInt}, PathId={mat["m_PathID"].AsLong}");
                                    break;
                                }
                            }

                            // 최상위 필드 이름 나열 (디버깅)
                            var fieldNames = bf.Children?.Select(c => c.FieldName).Take(30) ?? [];
                            Console.WriteLine($"      Fields: {string.Join(", ", fieldNames)}");
                        }
                        catch (Exception ex) { Console.WriteLine($"    MB #{info.PathId}: ERROR {ex.Message}"); }
                    }
                }
            }
            else
            {
                var inst = am.LoadAssetsFile(filePath, loadDeps: false);
                var meta = inst.file.Metadata;
                Console.WriteLine($"Assets: {meta.AssetInfos.Count}, TypeTree={meta.TypeTreeEnabled}");
                var types = new Dictionary<int, int>();
                foreach (var info in meta.AssetInfos)
                {
                    types.TryGetValue(info.TypeId, out int c);
                    types[info.TypeId] = c + 1;
                }
                foreach (var kv in types.OrderBy(x => x.Key))
                    Console.WriteLine($"  TypeId {kv.Key} ({(AssetsTools.NET.Extra.AssetClassID)kv.Key}): {kv.Value}");
            }
        }
        catch (Exception ex) { Console.WriteLine($"ERROR: {ex.Message}"); }
        finally { am.UnloadAll(true); }
    }
}
