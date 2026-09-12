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

    /// <summary>The legacy rpemotes layout: no types.lua. dpemotes additionally keeps its list under Client/ (capital C).</summary>
    string WriteLegacyList(string name, string clientFolder, string animationList)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dir, clientFolder));
        File.WriteAllText(Path.Combine(dir, clientFolder, "AnimationList.lua"), animationList, Encoding.UTF8);
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
        var legacy = WriteLegacyList("legacy", "client", "-- banner\nRP = {}\n");
        var dp = WriteLegacyList("dp", "Client", "DP = {}\n");
        Assert.Equal(ResourceKind.RpEmotes, ResourceSource.DetectKind(rp));
        Assert.Equal(ResourceKind.Scully, ResourceSource.DetectKind(sc));
        Assert.Equal(ResourceKind.RpEmotes, ResourceSource.DetectKind(legacy));
        Assert.Equal(ResourceKind.DpEmotes, ResourceSource.DetectKind(dp));
        Assert.Equal("dpemotes", ResourceSource.KindName(ResourceKind.DpEmotes));
        Assert.Equal(ResourceKind.Unknown, ResourceSource.DetectKind(_root));
        Assert.Equal(ResourceKind.Unknown, ResourceSource.DetectKind(Path.Combine(_root, "missing")));

        // A GitHub zip unpacks into <name>-<branch>/; ResolveRoot descends into a single recognised sub-folder.
        var wrapper = Path.Combine(_root, "wrapper");
        Directory.CreateDirectory(wrapper);
        Directory.Move(rp, Path.Combine(wrapper, "rp-main"));
        Assert.Equal(Path.Combine(wrapper, "rp-main"), ResourceSource.ResolveRoot(wrapper));
    }

    [Fact]
    public void AddOnEmotesFromAnimationListCustomJoinTheCatalog()
    {
        var rp = WriteRpEmotes("rp", """
            RP = {}
            RP.Dances = { ["builtin"] = { "dict@builtin", "clip", "Built in" } }
            RP.Emotes = {}
            """, "bnr_dance_short");
        // Same shape as the file rpemotes-reborn ships: a local table plus the merge function EmoteMenu.lua calls.
        File.WriteAllText(Path.Combine(rp, "client", "AnimationListCustom.lua"), """
            local CustomDP = {}
            CustomDP.Dances = {
                ["bnrdance"] = { "bnr_dance_short", "bnr_dance_short_clip", "BNR Dance Short", AnimationOptions = { EmoteLoop = true } },
            }
            CustomDP.Emotes = {}
            CustomDP.NotACategory = { ["x"] = { "d", "c", "X" } }
            function LoadAddonEmotes()
                assert(CustomDP ~= nil, 'Addon emotes can only be loaded once')
                for arrayName, array in pairs(CustomDP) do
                    if RP[arrayName] then
                        for emoteName, emoteData in pairs(array) do
                            RP[arrayName][emoteName] = emoteData
                        end
                    end
                end
                CustomDP = nil
            end
            """, Encoding.UTF8);

        var catalog = CatalogBuilder.Build(new[] { new ResourceSource("rp", rp) });

        Assert.Equal(2, catalog.Entries.Count);
        var dance = catalog.FindById("rp/Dances/bnrdance");
        Assert.NotNull(dance);
        Assert.Equal("bnr_dance_short", dance!.Dictionary);
        Assert.Equal("bnr_dance_short_clip", dance.Clip);
        Assert.True(dance.Loop);
        Assert.True(dance.IsCustom);
        Assert.Equal(Path.Combine(rp, "stream", "bnr_dance_short.ycd"), dance.CustomYcdPath);
    }

    [Fact]
    public void LegacyRpEmotesLoadsWithoutTypesLuaAndWithConfigReferences()
    {
        // Shape of Daudeuf/rpemotes: boolean options, PtfxInfo read from the resource config, add-on list merged inline.
        var legacy = WriteLegacyList("legacy", "client", """
            RP = {}
            RP.Expressions = { ["Angry"] = { "mood_angry_1" }, ["Grumpy2"] = { "mood_drivefast_1", "Grumpy 2" } }
            RP.AnimalEmotes = {
                ["bdogpee"] = { "creatures@rottweiler@amb@world_dog_barking@idle_a", "idle_a", "Pee (big dog)", AnimationOptions = {
                    EmoteLoop = true,
                    PtfxAsset = "scr_amb_chop", PtfxName = "ent_anim_dog_peeing",
                    PtfxInfo = Config.Languages[Config.MenuLanguage]['pee'],
                } },
            }
            RP.Emotes = { ["atm"] = { "Scenario", "PROP_HUMAN_ATM", "ATM" } }
            """);
        File.WriteAllText(Path.Combine(legacy, "client", "AnimationListCustom.lua"), """
            local CustomDP = {}
            CustomDP.Emotes = { ["custom"] = { "d", "c", "Custom", AnimationOptions = { EmoteMoving = true } } }
            for arrayName, array in pairs(CustomDP) do
                if RP[arrayName] then
                    for emoteName, emoteData in pairs(array) do RP[arrayName][emoteName] = emoteData end
                end
            end
            CustomDP = nil
            """, Encoding.UTF8);

        var catalog = CatalogBuilder.Build(new[] { new ResourceSource("legacy", legacy) });
        Assert.Empty(catalog.Warnings);
        Assert.Equal(5, catalog.Entries.Count);
        Assert.Equal("mood_angry_1", catalog.FindById("legacy/Expressions/Angry")!.Name);
        Assert.Equal("Grumpy 2", catalog.FindById("legacy/Expressions/Grumpy2")!.Label);
        var pee = catalog.FindById("legacy/AnimalEmotes/bdogpee")!;
        Assert.True(pee.Loop);
        Assert.True(pee.IsAnimal);
        Assert.Equal(EmoteKind.Scenario, catalog.FindById("legacy/Emotes/atm")!.Kind);
        Assert.Equal(51, catalog.FindById("legacy/Emotes/custom")!.AnimFlag);
    }

    [Fact]
    public void DpEmotesLoadsFromTheDpGlobalWithItsExpressionShape()
    {
        var dp = WriteLegacyList("dp", "Client", """
            DP = {}
            DP.Expressions = { ["Angry"] = { "Expression", "mood_angry_1" } }
            DP.Walks = { ["Alien"] = { "move_m@alien" } }
            DP.Shared = {
                ["hug"] = { "mp_ped_interaction", "kisses_guy_a", "Hug", "hug2", AnimationOptions = { EmoteMoving = false, EmoteDuration = 5000, SyncOffsetFront = 1.05 } },
                ["hug2"] = { "mp_ped_interaction", "kisses_guy_b", "Hug 2", "hug", AnimationOptions = { EmoteMoving = false, EmoteDuration = 5000 } },
            }
            DP.PropEmotes = {
                ["umbrella"] = { "amb@world_human_drinking@coffee@male@base", "base", "Umbrella", AnimationOptions = {
                    Prop = "p_amb_brolly_01", PropBone = 57005, PropPlacement = { 0.15, 0.005, 0.0, 87.0, -20.0, 180.0 }, EmoteLoop = true, EmoteMoving = true } },
            }
            """);

        var catalog = CatalogBuilder.Build(new[] { new ResourceSource("dp", dp) });
        Assert.Empty(catalog.Warnings);
        Assert.Equal(5, catalog.Entries.Count);
        var angry = catalog.FindById("dp/Expressions/Angry")!;
        Assert.Equal(EmoteKind.Expression, angry.Kind);
        Assert.Equal("mood_angry_1", angry.Name);
        Assert.Equal("Angry", angry.Label);
        var walk = catalog.FindById("dp/Walks/Alien")!;
        Assert.Equal(EmoteKind.Walk, walk.Kind);
        Assert.Equal("Alien", walk.Label);
        var hug = catalog.FindById("dp/Shared/hug")!;
        Assert.Equal("hug2", hug.PartnerCommand);
        Assert.Equal("dp/Shared/hug2", hug.PartnerId);
        Assert.Equal(PlacementKind.Offset, hug.Placement!.Kind);
        Assert.Equal(1.05f, hug.Placement.Front, 3);
        var umbrella = catalog.FindById("dp/PropEmotes/umbrella")!;
        Assert.Equal(51, umbrella.AnimFlag);
        Assert.Equal("p_amb_brolly_01", Assert.Single(umbrella.Props).Model);
    }

    [Fact]
    public void AnimationFlagsFollowTheMenus()
    {
        var rp = WriteRpEmotes("rp", """
            RP = {}
            RP.Emotes = {
                ["once"] = { "d", "c", "Once" },
                ["loop"] = { "d", "c", "Loop", AnimationOptions = { onFootFlag = AnimFlag.LOOP } },
                ["moving"] = { "d", "c", "Moving", AnimationOptions = { onFootFlag = AnimFlag.MOVING } },
                ["stuck"] = { "d", "c", "Stuck", AnimationOptions = { onFootFlag = AnimFlag.STUCK } },
                ["explicit"] = { "d", "c", "Explicit", AnimationOptions = { onFootFlag = AnimFlag.LOOP, Flag = 35 } },
                ["legacymove"] = { "d", "c", "Legacy", AnimationOptions = { EmoteMoving = true, EmoteLoop = true } },
                ["legacyloop"] = { "d", "c", "Legacy", AnimationOptions = { EmoteLoop = true } },
                ["legacystuck"] = { "d", "c", "Legacy", AnimationOptions = { EmoteStuck = true } },
            }
            """);
        var sc = WriteScully("sc", """
            return { type = 'general_emotes', options = {
                { Label = 'Once', Command = 'once', Dictionary = 'd', Animation = 'c' },
                { Label = 'Loop', Command = 'loop', Dictionary = 'd', Animation = 'c', Options = { Flags = { Loop = true } } },
                { Label = 'Move', Command = 'move', Dictionary = 'd', Animation = 'c', Options = { Flags = { Move = true, Loop = true } } },
                { Label = 'Stuck', Command = 'stuck', Dictionary = 'd', Animation = 'c', Options = { Flags = { Stuck = true, Move = true } } },
            } }
            """);
        var catalog = CatalogBuilder.Build(new[] { new ResourceSource("rp", rp), new ResourceSource("sc", sc) });
        Assert.Empty(catalog.Warnings);

        (int flag, bool loop, bool secondary, bool upper) Of(string id)
        {
            var e = catalog.FindById(id)!;
            return (e.AnimFlag, e.Loop, e.Move, e.UpperBody);
        }
        // rpemotes: Flag overrides onFootFlag; the legacy booleans convert like EmoteMenu.lua (Moving before Loop before Stuck).
        Assert.Equal((0, false, false, false), Of("rp/Emotes/once"));
        Assert.Equal((1, true, false, false), Of("rp/Emotes/loop"));
        Assert.Equal((51, true, true, true), Of("rp/Emotes/moving"));
        Assert.Equal((50, false, true, true), Of("rp/Emotes/stuck"));
        Assert.Equal((35, true, true, false), Of("rp/Emotes/explicit"));
        Assert.Equal((51, true, true, true), Of("rp/Emotes/legacymove"));
        Assert.Equal((1, true, false, false), Of("rp/Emotes/legacyloop"));
        Assert.Equal((50, false, true, true), Of("rp/Emotes/legacystuck"));
        // scully: Stuck and 50 or Move and 51 or Loop and 1 or 0.
        Assert.Equal((0, false, false, false), Of("sc/general_emotes/once"));
        Assert.Equal((1, true, false, false), Of("sc/general_emotes/loop"));
        Assert.Equal((51, true, true, true), Of("sc/general_emotes/move"));
        Assert.Equal((50, false, true, true), Of("sc/general_emotes/stuck"));
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
