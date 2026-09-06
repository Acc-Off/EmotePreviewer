using EmotePreviewer.Core.Adapters.GtaToolkit;
using EmotePreviewer.Core.Catalog;
using EmotePreviewer.Core.Gta;
using EmotePreviewer.Core.Model;
using EmotePreviewer.Core.Rage;

namespace EmotePreviewer.Core.Tests.Anim;

public sealed class ClipSetTableTests
{
    static uint H(string s) => JenkinsHash.HashLower(s);

    static ClipSetTable Table()
    {
        var t = new ClipSetTable();
        t.Add(new ClipSetDef(H("move_m@gangster@var_a"), H("move_m@gangster@generic"), H("move_gangster"), new HashSet<uint> { H("walk"), H("idle") }));
        t.Add(new ClipSetDef(H("move_gangster"), H("move_m@gangster@a"), H("move_m@generic"), new HashSet<uint>()));
        t.Add(new ClipSetDef(H("move_m@generic"), H("move_m@generic"), 0, new HashSet<uint>()));
        t.Add(new ClipSetDef(H("loop_a"), H("dict_a"), H("loop_b"), new HashSet<uint>()));
        t.Add(new ClipSetDef(H("loop_b"), H("dict_b"), H("loop_a"), new HashSet<uint>()));
        t.Add(new ClipSetDef(H("no_dictionary"), 0, H("move_m@generic"), new HashSet<uint>()));
        return t;
    }

    [Fact]
    public void UsesTheSetsOwnDictionaryWhenItHasTheClip()
    {
        var dict = Table().ResolveDictionary("move_m@gangster@var_a", "walk", (d, c) => true, out var chain);
        Assert.Equal(H("move_m@gangster@generic"), dict);
        Assert.Single(chain);
    }

    [Fact]
    public void FollowsTheFallbackChainUntilADictionaryHasTheClip()
    {
        var generic = H("move_m@generic");
        var dict = Table().ResolveDictionary("move_m@gangster@var_a", "run", (d, c) => d == generic, out var chain);
        Assert.Equal(generic, dict);
        Assert.Equal(new[] { H("move_m@gangster@var_a"), H("move_gangster"), H("move_m@generic") }, chain.Select(s => s.Id));
    }

    [Fact]
    public void ReturnsNullForUnknownSetsAndClipsNobodyHas()
    {
        Assert.Null(Table().ResolveDictionary("nope", "walk", (d, c) => true, out var chain));
        Assert.Empty(chain);
        Assert.Null(Table().ResolveDictionary("move_m@gangster@var_a", "walk", (d, c) => false, out chain));
        Assert.Equal(3, chain.Count);
    }

    [Fact]
    public void StopsOnFallbackCyclesAndSkipsSetsWithoutADictionary()
    {
        Assert.Null(Table().ResolveDictionary("loop_a", "walk", (d, c) => false, out var chain));
        Assert.Equal(2, chain.Count);
        var dict = Table().ResolveDictionary("no_dictionary", "walk", (d, c) => true, out chain);
        Assert.Equal(H("move_m@generic"), dict);
    }

    [Fact]
    public void LaterTablesOverrideEarlierSets()
    {
        var t = Table();
        var later = new ClipSetTable();
        later.Add(new ClipSetDef(H("move_m@generic"), H("move_m@generic_v2"), 0, new HashSet<uint>()));
        t.Merge(later);
        Assert.Equal(H("move_m@generic_v2"), t.Find("move_m@generic")!.Dictionary);
        Assert.Equal(6, t.Count);
    }
}

/// <summary>clip_sets.ymt of the local GTA V install. Skipped without it.</summary>
public sealed class ClipSetGameDataTests
{
    static GtaToolkitGameData OpenOrSkip()
    {
        var gta = GtaLocator.Detect();
        Skip.If(gta == null, "GTA V not installed");
        try { return GtaToolkitGameData.Open(gta!); }
        catch (GtaKeys.KeyMaterialMissingException ex) { throw new SkipException(ex.Message); }
    }

    [SkippableFact]
    public void ParsesTheGamesClipSetTable()
    {
        using var gd = OpenOrSkip();
        Assert.NotEmpty(gd.ClipSetFilePaths);
        var table = gd.ClipSets;
        Assert.NotNull(table);
        Assert.True(table!.Count > 4000, $"only {table.Count} clip sets");
        var gangster = table.Find("move_m@gangster@var_a");
        Assert.NotNull(gangster);
        Assert.Equal("move_m@gangster@generic", gd.ClipDictionaryName(gangster!.Dictionary));
        Assert.Equal(JenkinsHash.HashLower("move_gangster"), gangster.Fallback);
        Assert.Contains(JenkinsHash.HashLower("walk"), gangster.Items);
    }

    [SkippableFact]
    public void ResolvesWalksThroughTheirOwnDictionaryOrAFallback()
    {
        using var gd = OpenOrSkip();
        var gangster = gd.ResolveClipSetClip("move_m@gangster@var_a", EmoteEntry.WalkClip);
        Assert.Equal(new ClipSetClip("move_m@gangster@generic", "walk", false), gangster);
        // move_m@alien only has run clips of its own; its walk comes from the generic set.
        var alien = gd.ResolveClipSetClip("move_m@alien", EmoteEntry.WalkClip);
        Assert.Equal(new ClipSetClip("move_m@generic", "walk", true), alien);
        Assert.Null(gd.ResolveClipSetClip("move_m@no_such_set", EmoteEntry.WalkClip));
        var dict = gd.LoadClipDictionary(gangster!.Dictionary);
        Assert.NotNull(dict?.FindClip(gangster.Clip));
    }
}
