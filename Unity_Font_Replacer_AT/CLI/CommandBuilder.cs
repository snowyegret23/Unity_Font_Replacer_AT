using System.CommandLine;

namespace UnityFontReplacer.CLI;

public static class CommandBuilder
{
    public static RootCommand Build()
    {
        // parse subcommand
        var parseCommand = new Command("parse", Strings.Get("cmd_parse"));
        var parseGamePathOption = CommandLineOptions.CreateGamePathOption();
        var parsePs5Option = CommandLineOptions.OptionalOption<bool>("--ps5-swizzle", "Detect PS5 swizzle state");
        var parseMaxWorkersOption = CommandLineOptions.OptionalOption("--max-workers", 1, "Max parallel scan workers");
        parseCommand.Add(parseGamePathOption);
        parseCommand.Add(parsePs5Option);
        parseCommand.Add(parseMaxWorkersOption);
        parseCommand.SetAction(async parseResult =>
        {
            await ParseCommand.ExecuteAsync(
                parseResult.GetRequiredValue(parseGamePathOption),
                parseResult.GetValue(parsePs5Option),
                parseResult.GetValue(parseMaxWorkersOption));
        });

        // list subcommand
        var listCommand = new Command("list", Strings.Get("cmd_list"));
        var listGamePathOption = CommandLineOptions.CreateGamePathOption();
        var listFileOption = CommandLineOptions.RequiredOption<string>("--file", "Font mapping JSON file");
        listCommand.Add(listGamePathOption);
        listCommand.Add(listFileOption);
        listCommand.SetAction(async parseResult =>
        {
            await ListCommand.ExecuteAsync(
                parseResult.GetRequiredValue(listGamePathOption),
                parseResult.GetRequiredValue(listFileOption));
        });

        // export subcommand
        var exportCommand = new Command("export", Strings.Get("cmd_export"));
        var exportGamePathOption = CommandLineOptions.CreateGamePathOption();
        exportCommand.Add(exportGamePathOption);
        exportCommand.SetAction(async parseResult =>
        {
            await ExportFontsCommand.ExecuteAsync(parseResult.GetRequiredValue(exportGamePathOption));
        });

        // makesdf subcommand
        var makeSdfCommand = new Command("makesdf", Strings.Get("cmd_makesdf"));
        var ttfOption = CommandLineOptions.RequiredOption<string>("--ttf", "TTF file path");
        var atlasSizeOption = CommandLineOptions.OptionalOption("--atlas-size", "4096,4096", "Atlas size (W,H)");
        var pointSizeOption = CommandLineOptions.OptionalOption("--point-size", 0, "Point size (0=auto)");
        var paddingOption = CommandLineOptions.OptionalOption("--padding", 7, "Atlas padding");
        var charsetOption = CommandLineOptions.OptionalOption("--charset", MakeSdfCommand.DefaultCharsetArgument, "Charset file or literal");
        var renderModeOption = CommandLineOptions.OptionalOption("--rendermode", "sdf", "sdf / raster");
        var filterModeOption = CommandLineOptions.OptionalOption("--filter-mode", "auto", "auto / point / bilinear / trilinear");
        makeSdfCommand.Add(ttfOption);
        makeSdfCommand.Add(atlasSizeOption);
        makeSdfCommand.Add(pointSizeOption);
        makeSdfCommand.Add(paddingOption);
        makeSdfCommand.Add(charsetOption);
        makeSdfCommand.Add(renderModeOption);
        makeSdfCommand.Add(filterModeOption);
        makeSdfCommand.SetAction(async parseResult =>
        {
            var atlasSize = parseResult.GetValue(atlasSizeOption) ?? "4096,4096";
            var charset = parseResult.GetValue(charsetOption) ?? MakeSdfCommand.DefaultCharsetArgument;
            var renderMode = parseResult.GetValue(renderModeOption) ?? "sdf";
            var filterMode = parseResult.GetValue(filterModeOption) ?? "bilinear";

            await MakeSdfCommand.ExecuteAsync(
                parseResult.GetRequiredValue(ttfOption),
                atlasSize,
                parseResult.GetValue(pointSizeOption),
                parseResult.GetValue(paddingOption),
                charset,
                renderMode,
                filterMode);
        });

        // batch subcommand
        var batchCommand = BatchCommand.Build();

        // diag subcommand (번들 내부 에셋 타입 진단)
        var diagCommand = new Command("diag", "Diagnose bundle contents");
        var diagFileOption = CommandLineOptions.RequiredOption<string>("--file", "Bundle file path");
        diagCommand.Add(diagFileOption);
        diagCommand.SetAction(parseResult =>
        {
            DiagBundle(parseResult.GetRequiredValue(diagFileOption));
        });

        // root
        var rootCommand = new RootCommand(Strings.Get("app_description"));
        rootCommand.Add(parseCommand);
        rootCommand.Add(listCommand);
        rootCommand.Add(batchCommand);
        rootCommand.Add(OneShotCommand.Build());
        rootCommand.Add(exportCommand);
        rootCommand.Add(makeSdfCommand);
        rootCommand.Add(diagCommand);

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
