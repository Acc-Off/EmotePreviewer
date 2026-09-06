using System.Text;
using EmotePreviewer.Core.Catalog;

namespace EmotePreviewer.Core.Tests.Catalog;

/// <summary>Multi-source catalog building on synthetic resources (no real emote data needed).</summary>
public sealed class CatalogBuilderTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "emotepreviewer-core-tests", Guid.NewGuid().ToString("N"));

    public CatalogBuilderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    string WriteRpEmotes(string name, string animationList, params string[] ycdNames)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dir, "client"));
        Directory.CreateDirectory(Path.Combine(dir, "stream"));
        File.WriteAllText(Path.Combine(dir, "types.lua"), "AnimFlag = { MOVING = 51, LOOP = 1, STUCK = 50 }\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(dir, "client", "AnimationList.lua"), animationList, Encoding.UTF8);
        foreach (var y in ycdNames) File.WriteAllBytes(Path.Combine(dir, "stream", y + ".ycd"), new byte[] { 1, 2, 3 });
        return dir;
    }

    string WriteScully(string name, string emotesLua)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dir, "shared", "data", "emotes"));
        File.WriteAllText(Path.Combine(dir, "shared", "data", "emotes", "general_emotes.lua"), emotesLua, Encoding.UTF8);
        return dir;
    }

    [Fact]
    public void DetectsResourceKinds()
    {
        var rp = WriteRpEmotes("rp", "RP = {}\n");
        var sc = WriteScully("sc", "return { type = 'general_emotes', options = {} }\n");
        Assert.Equal(ResourceKind.RpEmotes, ResourceSource.DetectKind(rp));
        Assert.Equal(ResourceKind.Scully, ResourceSource.DetectKind(sc));
        Assert.Equal(ResourceKind.Unknown, ResourceSource.DetectKind(_root));
        Assert.Equal(ResourceKind.Unknown, ResourceSource.DetectKind(Path.Combine(_root, "missing")));

        // A GitHub zip unpacks into <name>-<branch>/; ResolveRoot descends into a single recognised sub-folder.
        var wrapper = Path.Combine(_root, "wrapper");
        Directory.CreateDirectory(wrapper);
        Directory.Move(rp, Path.Combine(wrapper, "rp-main"));
        Assert.Equal(Path.Combine(wrapper, "rp-main"), ResourceSource.ResolveRoot(wrapper));
    }

    [Fact]
    public void MakeIdProducesUrlSafeNames()
    {
        Assert.Equal("rpemotes-reborn", ResourceSource.MakeId("RPEmotes-Reborn"));
        Assert.Equal("scully_emotemenu", ResourceSource.MakeId("scully_emotemenu"));
        Assert.Equal("my-emotes-v2", ResourceSource.MakeId(" my emotes/v2 "));
        Assert.Equal("resource", ResourceSource.MakeId("///"));
    }

    [Fact]
    public void EntriesGetStableIdsAndSourceIds()
    {
        var rp = WriteRpEmotes("rp", """
            RP = {}
            RP.Emotes = {
                ["wave"] = { "friends@frj@ig_1", "wave_a", "Wave" },
                ["sit"] = { "Scenario", "PROP_HUMAN_SEAT_BENCH", "Sit" },
            }
            RP.Walks = { ["alien"] = { "move_m@alien", "Alien" } }
            """);
        var sc = WriteScully("sc", """
            return { type = 'general_emotes', options = {
                { Label = 'Wave', Command = 'wave', Dictionary = 'friends@frj@ig_1', Animation = 'wave_a', Options = { Flags = { Loop = true } } },
                { Label = 'Wave again', Command = 'wave', Dictionary = 'friends@frj@ig_1', Animation = 'wave_b' },
            } }
            """);
        var catalog = CatalogBuilder.Build(new[] { new ResourceSource("rp-id", rp), new ResourceSource("sc-id", sc) });

        Assert.Empty(catalog.Warnings);
        Assert.Equal(5, catalog.Entries.Count);
        // rpemotes tables are Lua hash tables (unordered), so the loader sorts categories and commands; scully lists keep file order.
        Assert.Equal(new[] { "rp-id/Emotes/sit", "rp-id/Emotes/wave", "rp-id/Walks/alien", "sc-id/general_emotes/wave", "sc-id/general_emotes/wave#2" },
            catalog.Entries.Select(e => e.Id).ToArray());
        Assert.All(catalog.Entries.Take(3), e => Assert.Equal("rp-id", e.Source));
        Assert.Equal(EmoteKind.Scenario, catalog.FindById("rp-id/Emotes/sit")!.Kind);
        Assert.False(catalog.FindById("rp-id/Emotes/sit")!.HasClip);
        var alien = catalog.FindById("rp-id/Walks/alien")!;
        Assert.Equal(("move_m@alien", "walk"), (alien.Dictionary, alien.Clip));
        Assert.True(catalog.FindById("sc-id/general_emotes/wave")!.Loop);
        Assert.Equal("wave_b", catalog.FindById("sc-id/general_emotes/wave#2")!.Clip);
        Assert.Null(catalog.FindById("nope"));
    }

    [Fact]
    public void ShippedYcdFollowsSourceOrderAndUnknownLayoutBecomesWarning()
    {
        var first = WriteRpEmotes("first", """
            RP = {}
            RP.Emotes = { ["a"] = { "custom@dict", "clip_a", "A" } }
            """, "custom@dict");
        var second = WriteRpEmotes("second", """
            RP = {}
            RP.Emotes = { ["b"] = { "custom@dict", "clip_b", "B" }, ["c"] = { "other@dict", "clip_c", "C" } }
            """, "custom@dict", "other@dict");
        var junk = Path.Combine(_root, "junk");
        Directory.CreateDirectory(junk);

        var catalog = CatalogBuilder.Build(new[] { new ResourceSource("first", first), new ResourceSource("second", second), new ResourceSource("junk", junk) });

        Assert.Single(catalog.Warnings);
        Assert.Contains("junk", catalog.Warnings[0]);
        Assert.Equal(3, catalog.Entries.Count);
        Assert.Equal(Path.Combine(first, "stream", "custom@dict.ycd"), catalog.CustomYcds["custom@dict"]);
        Assert.Equal(Path.Combine(second, "stream", "other@dict.ycd"), catalog.CustomYcds["OTHER@DICT"]);
        Assert.All(catalog.Entries, e => Assert.True(e.IsCustom));
        Assert.Equal(Path.Combine(first, "stream", "custom@dict.ycd"), catalog.FindById("second/Emotes/b")!.CustomYcdPath);
    }

    [Fact]
    public void BuildFromDataRootPicksRecognisedFoldersOnly()
    {
        WriteRpEmotes("Zeta", "RP = {}\nRP.Emotes = { ['z'] = { 'd', 'c', 'Z' } }\n");
        WriteScully("alpha", "return { type = 'x', options = { { Label = 'A', Command = 'a', Dictionary = 'd', Animation = 'c' } } }\n");
        Directory.CreateDirectory(Path.Combine(_root, "not-a-resource"));

        var catalog = CatalogBuilder.BuildFromDataRoot(_root);
        Assert.Equal(new[] { "alpha", "zeta" }, catalog.Sources.Select(s => s.Id).ToArray());
        Assert.Equal(2, catalog.Entries.Count);
        Assert.Empty(CatalogBuilder.BuildFromDataRoot(Path.Combine(_root, "missing")).Entries);
    }
}
