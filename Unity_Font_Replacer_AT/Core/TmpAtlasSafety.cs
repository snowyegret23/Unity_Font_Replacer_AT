using AssetsTools.NET;
using AssetsTools.NET.Extra;
using UnityFontReplacer.Models;

namespace UnityFontReplacer.Core;

/// <summary>Read-only inventory of TMP atlas owners, evaluated before any replacement is installed.</summary>
internal static class TmpAtlasSafety
{
    internal sealed record AssetLocation(string OuterPath, string Cab);
    internal sealed record OwnerReference(
        AssetLocation OwnerFile, long OwnerPathId, int FileId, long AtlasPathId,
        string? ExternalCab, ReplacementPlan? Plan);
    internal sealed record ReplacementPlan(
        string Source, string? ForceRaster, string? Swizzle, string? ProcessSwizzle,
        int Padding, long RenderMode);
    private sealed record AtlasKey(AssetLocation File, long PathId);
    private sealed record ScriptReference(int FileId, long PathId, string? ExternalCab);

    internal static void Validate(AssetsContext context, IEnumerable<string> files, IReadOnlyCollection<FontEntry> targets)
    {
        var selected = targets.Where(e => e.Type == FontType.SDF && !string.IsNullOrWhiteSpace(e.ReplaceTo))
            .ToDictionary(e => (Name(e.File), Name(e.AssetsName), e.PathId));
        if (selected.Count == 0)
            return;

        var owners = new List<OwnerReference>();
        var locations = new HashSet<AssetLocation>();
        var scriptKinds = new Dictionary<(AssetLocation, long), bool>();
        var unclassified = new List<(AssetLocation File, long PathId, ScriptReference? Script)>();
        foreach (string path in files)
        {
            string outer = Canonical(path);
            string basename = Name(outer);
            try
            {
                if (FontScanner.IsBundleFile(path))
                {
                    var bundle = context.LoadBundleFile(path);
                    var directories = bundle.file.BlockAndDirInfo.DirectoryInfos;
                    for (int index = 0; index < directories.Count; index++)
                    {
                        if (!directories[index].IsSerialized)
                            continue;
                        var file = context.LoadAssetsFileFromBundle(bundle, index) ??
                            throw new InvalidDataException($"Cannot read serialized entry '{directories[index].Name}'.");
                        Inspect(file, outer, basename);
                    }
                }
                else if (AssetsFile.IsAssetsFile(path))
                    Inspect(context.Manager.LoadAssetsFile(path, loadDeps: false), outer, basename);
                else if (selected.Keys.Any(key => key.Item1 == basename))
                    throw new InvalidDataException("Selected font is not in a readable Unity asset file.");
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                throw new InvalidDataException($"Cannot verify TMP atlas ownership in '{path}': {error.Message}", error);
            }
            finally
            {
                AssetsContext.ReleaseLoadedFiles(context.Manager);
            }
        }
        var byCab = locations.GroupBy(loc => loc.Cab).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var unknown in unclassified)
        {
            var location = unknown.File;
            if (unknown.Script is not { } script)
                throw new InvalidDataException($"Cannot identify MonoScript for '{location.OuterPath}/{location.Cab}' #{unknown.PathId}.");
            if (script.FileId != 0)
                location = ResolveExternal(byCab, location, script.ExternalCab, "MonoScript");
            if (!scriptKinds.TryGetValue((location, script.PathId), out bool isFont) || isFont)
                throw new InvalidDataException($"Could not rule out an invisible TMP atlas owner in '{unknown.File.OuterPath}/{unknown.File.Cab}' #{unknown.PathId}.");
        }
        ValidateOwnership(locations, owners);

        void Inspect(AssetsFileInstance file, string outer, string basename)
        {
            string cab = Name(file.path);
            var location = new AssetLocation(outer, cab);
            if (!locations.Add(location))
                throw new InvalidDataException($"Duplicate serialized filename '{cab}' in one bundle.");
            var behaviours = file.file.GetAssetsOfType(AssetClassID.MonoBehaviour);
            // Class identity is necessary only for files containing behaviour owners
            // or a MonoScript that another file references. Cache identities, never full fields.
            foreach (var scriptInfo in file.file.GetAssetsOfType(AssetClassID.MonoScript))
            {
                try
                {
                    var scriptField = context.Manager.GetBaseField(file, scriptInfo);
                    var klass = scriptField["m_ClassName"];
                    var ns = scriptField["m_Namespace"];
                    if (!klass.IsDummy && !ns.IsDummy)
                        scriptKinds[(location, scriptInfo.PathId)] = (klass.AsString, ns.AsString) is
                            ("TMP_FontAsset", "TMPro") or ("FontAsset", "UnityEngine.TextCore.Text");
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // An unreadable script is only fatal if a potential owner uses it.
                }
            }

            foreach (var info in behaviours)
            {
                selected.TryGetValue((basename, cab, info.PathId), out FontEntry? entry);
                var pointer = ReadScriptReference(context.Manager, file, info);
                bool? script = pointer is { FileId: 0 } &&
                    scriptKinds.TryGetValue((location, pointer.PathId), out bool localKind) ? localKind : null;
                if (script is false)
                {
                    if (entry is not null)
                        throw new InvalidDataException($"Mapped Path ID {info.PathId} is not an official TMP FontAsset.");
                    continue;
                }

                AssetTypeValueField field;
                try { field = context.Manager.GetBaseField(file, info); }
                catch (Exception error) when (error is not OutOfMemoryException && script != true && entry is null)
                {
                    unclassified.Add((location, info.PathId, pointer));
                    continue;
                }
                if (!HasFontShape(field))
                {
                    if (script is true || entry is not null)
                        throw new InvalidDataException($"Could not establish schema for MonoBehaviour #{info.PathId} in '{cab}'.");
                    unclassified.Add((location, info.PathId, pointer));
                    continue;
                }
                var schema = TmpSchemaDetector.Inspect(field, file.file.Metadata.UnityVersion);
                var references = TmpFontHandler.ReadAtlasReferences(field);
                if (entry is not null && references.Count == 0)
                    throw new InvalidDataException($"Selected TMP FontAsset '{entry.Name}' has no serialized atlas.");
                var mode = field["m_AtlasRenderMode"];
                var plan = entry is null ? null : new ReplacementPlan(
                    entry.ReplaceTo.Trim(), entry.ForceRaster, entry.Swizzle, entry.ProcessSwizzle,
                    schema.AtlasPadding, mode.IsDummy ? 0 : mode.AsLong);
                foreach (var (fileId, pathId) in references)
                {
                    string? external = null;
                    if (fileId != 0)
                    {
                        if (fileId < 1 || fileId > file.file.Metadata.Externals.Count)
                            throw new InvalidDataException($"Invalid atlas external File ID {fileId} for TMP FontAsset #{info.PathId}.");
                        external = Name(file.file.Metadata.Externals[fileId - 1].PathName);
                    }
                    owners.Add(new OwnerReference(location, info.PathId, fileId, pathId, external, plan));
                }
            }
        }
    }

    internal static void ValidateOwnership(IEnumerable<AssetLocation> locations, IEnumerable<OwnerReference> owners)
    {
        var byCab = locations.GroupBy(loc => loc.Cab).ToDictionary(g => g.Key, g => g.ToArray());
        var references = new Dictionary<AtlasKey, List<OwnerReference>>();
        var atlasCount = new Dictionary<(AssetLocation, long), HashSet<AtlasKey>>();
        foreach (var owner in owners)
        {
            var atlasFile = owner.OwnerFile;
            if (owner.FileId != 0)
            {
                atlasFile = ResolveExternal(byCab, owner.OwnerFile, owner.ExternalCab, $"atlas for '{Describe(owner)}'");
            }
            var key = new AtlasKey(atlasFile, owner.AtlasPathId);
            if (!references.TryGetValue(key, out var group))
                references[key] = group = [];
            group.Add(owner);

            var ownerKey = (owner.OwnerFile, owner.OwnerPathId);
            if (owner.Plan is not null)
            {
                if (!atlasCount.TryGetValue(ownerKey, out var owned))
                    atlasCount[ownerKey] = owned = [];
                owned.Add(key);
                if (owned.Count > 1)
                    throw new InvalidDataException($"AT cannot replace a TMP FontAsset with multiple distinct atlases: '{Describe(owner)}'.");
            }
        }

        foreach (var (atlas, group) in references)
        {
            var selected = group.Where(owner => owner.Plan is not null).ToArray();
            if (selected.Length == 0)
                continue;
            var untouched = group.Where(owner => owner.Plan is null).Select(Describe).Distinct().ToArray();
            if (untouched.Length != 0)
                throw new InvalidDataException(
                    $"Shared TMP atlas '{atlas.File.Cab} #{atlas.PathId}' has unselected owners: {string.Join(", ", untouched)}.");
            if (selected.Select(owner => owner.Plan).Distinct().Count() != 1)
                throw new InvalidDataException(
                    $"Conflicting replacement plans for shared TMP atlas '{atlas.File.Cab} #{atlas.PathId}'.");
        }
    }

    private static string Describe(OwnerReference owner) => $"{owner.OwnerFile.OuterPath}/{owner.OwnerFile.Cab} #{owner.OwnerPathId}";
    private static string Name(string name) => Path.GetFileName(name.Replace('\\', '/')).ToUpperInvariant();
    private static string Canonical(string path) =>
        OperatingSystem.IsWindows() ? Path.GetFullPath(path).ToUpperInvariant() : Path.GetFullPath(path);

    private static bool HasFontShape(AssetTypeValueField field) =>
        !field["m_SpriteCharacterTable"].IsDummy || !field["m_SpriteGlyphTable"].IsDummy ||
        !field["spriteSheet"].IsDummy || !field["spriteInfoList"].IsDummy
            ? false
            : (!field["m_fontInfo"].IsDummy && !field["m_glyphInfoList"].IsDummy) ||
              (!field["m_FaceInfo"].IsDummy && (!field["m_GlyphTable"].IsDummy || !field["m_CharacterTable"].IsDummy));

    private static AssetLocation ResolveExternal(
        Dictionary<string, AssetLocation[]> byCab, AssetLocation source, string? external, string purpose)
    {
        if (external is null || !byCab.TryGetValue(external, out var candidates))
            throw new InvalidDataException($"Cannot resolve external CAB '{external}' ({purpose}).");
        var sameOuter = candidates.Where(location => location.OuterPath == source.OuterPath).ToArray();
        if (sameOuter.Length != 0)
            candidates = sameOuter;
        if (candidates.Length != 1)
            throw new InvalidDataException($"CAB '{external}' has ambiguous physical ownership ({purpose}).");
        return candidates[0];
    }

    private static ScriptReference? ReadScriptReference(AssetsManager manager, AssetsFileInstance file, AssetFileInfo info)
    {
        try
        {
            var head = manager.GetBaseField(file, info, AssetReadFlags.SkipMonoBehaviourFields);
            var pointer = head["m_Script"];
            if (pointer.IsDummy || pointer["m_FileID"].IsDummy || pointer["m_PathID"].IsDummy)
                return null;
            int fileId = pointer["m_FileID"].AsInt;
            long pathId = pointer["m_PathID"].AsLong;
            if (pathId == 0 || fileId < 0 || fileId > file.file.Metadata.Externals.Count)
                return null;
            return new ScriptReference(fileId, pathId,
                fileId == 0 ? null : Name(file.file.Metadata.Externals[fileId - 1].PathName));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
