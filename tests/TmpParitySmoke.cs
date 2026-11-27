using AssetsTools.NET;
using UnityFontReplacer.Core;

internal static class TmpParitySmoke
{
    internal static void Run()
    {
        AssetTypeValueField Dynamic(bool flag, int mode) => Branch("font",
            Int("m_AtlasPopulationMode", mode),
            Bool("InternalDynamicOS", flag),
            Branch("m_SourceFontFile", Int("m_FileID", 2), Long("m_PathID", -71)));

        var legacy = Branch("legacy", Branch("atlas", Int("m_FileID", 0), Long("m_PathID", -8)));
        Check(!TmpFontHandler.PrepareStaticPopulation(legacy, allowFreeze: false), "Legacy TMP is static.");
        var dynamic = Dynamic(false, 1);
        Reject(() => TmpFontHandler.PrepareStaticPopulation(dynamic, allowFreeze: false), "Dynamic TMP must opt in.");
        Check(dynamic["m_AtlasPopulationMode"].AsInt == 1, "Rejected freeze must not mutate mode.");
        Check(dynamic["m_SourceFontFile"]["m_PathID"].AsLong == -71, "Rejected freeze must not mutate reference.");
        Check(TmpFontHandler.PrepareStaticPopulation(dynamic, allowFreeze: true), "Dynamic TMP froze.");
        Check(dynamic["m_AtlasPopulationMode"].AsInt == 0 &&
              dynamic["m_SourceFontFile"]["m_FileID"].AsInt == 0 &&
              dynamic["m_SourceFontFile"]["m_PathID"].AsLong == 0, "Freeze clears mode and source PPtr.");
        var dynamicOs = Dynamic(true, 0);
        Check(TmpFontHandler.PrepareStaticPopulation(dynamicOs, allowFreeze: true) &&
              !dynamicOs["InternalDynamicOS"].AsBool, "DynamicOS is frozen even with a zero population mode.");
        var malformed = Branch("font", Int("m_AtlasPopulationMode", 1),
            Branch("m_SourceFontFile", Int("m_FileID", 2)));
        Reject(() => TmpFontHandler.PrepareStaticPopulation(malformed, allowFreeze: true), "Incomplete source PPtr rejected.");
        Check(malformed["m_AtlasPopulationMode"].AsInt == 1 &&
              malformed["m_SourceFontFile"]["m_FileID"].AsInt == 2, "Malformed freeze cannot partially mutate.");

        var aliases = Branch("font",
            Branch("m_AtlasTextures", Branch("Array",
                Branch("data", Int("m_FileID", 0), Long("m_PathID", 0)),
                Branch("data", Int("m_FileID", 2), Long("m_PathID", -8)))),
            Branch("m_AtlasTexture", Int("m_FileID", 2), Long("m_PathID", -8)),
            Branch("atlas", Int("m_FileID", 0), Long("m_PathID", 23)));
        Check(TmpFontHandler.ReadAtlasReferences(aliases).SequenceEqual(new (int, long)[] { (2, -8), (0, 23) }),
            "Atlas parser skips nulls and deduplicates aliases.");
        Check(TmpFontHandler.ReadFromField(legacy).AtlasTexturePathId == -8, "Legacy signed atlas ID retained.");

        var a = new TmpAtlasSafety.AssetLocation("/path/a.bundle", "CAB-A");
        var b = new TmpAtlasSafety.AssetLocation("/path/b.bundle", "CAB-B");
        var c = new TmpAtlasSafety.AssetLocation("/other/b.bundle", "CAB-B");
        var plan = new TmpAtlasSafety.ReplacementPlan("/tmp/font.json", null, null, null, 7, 4165);
        var local = new TmpAtlasSafety.OwnerReference(b, 1, 0, -9, null, plan);
        var external = new TmpAtlasSafety.OwnerReference(a, 2, 1, -9, "CAB-B", plan);

        TmpAtlasSafety.ValidateOwnership([a, b], [local, external]);
        Reject(() => TmpAtlasSafety.ValidateOwnership([a, b], [local, external with { Plan = null }]),
            "Mixing selected and untouched atlas owners rejected.");
        Reject(() => TmpAtlasSafety.ValidateOwnership([a, b],
            [local, external with { Plan = plan with { Padding = 3 } }]), "Non-identical atlas plans rejected.");
        Reject(() => TmpAtlasSafety.ValidateOwnership([a, b], [local, local with { AtlasPathId = 71 }]),
            "Multiple distinct active atlases rejected.");
        Reject(() => TmpAtlasSafety.ValidateOwnership([a, b, c], [external]), "Ambiguous external CAB rejected.");
        TmpAtlasSafety.ValidateOwnership([b, c], [local, local with { OwnerFile = c, Plan = null }]);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception(message);
    }

    private static AssetTypeValueField Int(string name, int value) => Field(name, new AssetTypeValue(value));
    private static AssetTypeValueField Long(string name, long value) => Field(name, new AssetTypeValue(value));
    private static AssetTypeValueField Bool(string name, bool value) => Field(name, new AssetTypeValue(value));
    private static AssetTypeValueField Branch(string name, params AssetTypeValueField[] children) =>
        Field(name, null, children);
    private static AssetTypeValueField Field(
        string name, AssetTypeValue? value, params AssetTypeValueField[] children) =>
        new()
        {
            TemplateField = new AssetTypeTemplateField { Name = name },
            Value = value,
            Children = [.. children],
        };
}
