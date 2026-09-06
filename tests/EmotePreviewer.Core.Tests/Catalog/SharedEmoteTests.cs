using System.Text;
using EmotePreviewer.Core.Catalog;

namespace EmotePreviewer.Core.Tests.Catalog;

/// <summary>Partner commands and placements of shared emotes in both resource layouts.</summary>
public sealed class SharedEmoteTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "emotepreviewer-core-tests", Guid.NewGuid().ToString("N"));

    public SharedEmoteTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    string WriteRpEmotes(string animationList)
    {
        var dir = Path.Combine(_root, "rp");
        Directory.CreateDirectory(Path.Combine(dir, "client"));
        File.WriteAllText(Path.Combine(dir, "types.lua"), "AnimFlag = { MOVING = 51, LOOP = 1, STUCK = 50 }\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(dir, "client", "AnimationList.lua"), animationList, Encoding.UTF8);
        return dir;
    }

    string WriteScully(string emotesLua)
    {
        var dir = Path.Combine(_root, "sc");
        Directory.CreateDirectory(Path.Combine(dir, "shared", "data", "emotes"));
        File.WriteAllText(Path.Combine(dir, "shared", "data", "emotes", "synchronized_emotes.lua"), emotesLua, Encoding.UTF8);
        return dir;
    }

    [Fact]
    public void RpEmotesSharedEntriesCarryPartnerAndPlacement()
    {
        var rp = WriteRpEmotes("""
            RP = {}
            RP.Emotes = { ["wave"] = { "friends@frj@ig_1", "wave_a", "Wave" } }
            RP.Shared = {
                ["hug"] = { "mp_ped_interaction", "kisses_guy_a", "Hug", "hug2", AnimationOptions = { EmoteDuration = 5000, SyncOffsetFront = 1.05 } },
                ["hug2"] = { "mp_ped_interaction", "kisses_guy_b", "Hug 2", "hug", AnimationOptions = { EmoteDuration = 5000, SyncOffsetSide = -0.05, SyncOffsetFront = 1.18 } },
                ["carry"] = { "missfinale_c2mcs_1", "fin_c2_mcs_1_camman", "Carry", "carry2", AnimationOptions = { onFootFlag = AnimFlag.MOVING } },
                ["carry2"] = { "nm", "firemans_carry", "Be carried", "carry", AnimationOptions = { onFootFlag = AnimFlag.LOOP, Attachto = true, bone = 40269, pos = vector3(-0.14, 0.15, 0.14), rot = vector3(0.0, -59.0, -4.5) } },
                ["punch"] = { "melee@unarmed@streamed_variations", "plyr_takedown_front_slap", "Punch", "punched" },
                ["punched"] = { "melee@unarmed@streamed_variations", "victim_takedown_front_slap", "Punched", "punch" },
                ["cprs"] = { "mini@cpr@char_a@cpr_str", "cpr_pumpchest", "CPR", "cprs2", AnimationOptions = { onFootFlag = AnimFlag.LOOP, StartDelay = 250 } },
                ["cprs2"] = { "mini@cpr@char_b@cpr_str", "cpr_pumpchest", "CPR 2", "cprs", AnimationOptions = { onFootFlag = AnimFlag.LOOP, Attachto = true, bone = 0, xPos = 0.35, yPos = 0.8, zRot = 270.0 } },
                ["lonely"] = { "some@dict", "clip", "Lonely", "nobody" },
                ["mirror"] = { "some@dict", "clip", "Mirror" },
            }
            """);
        var catalog = CatalogBuilder.Build(new[] { new ResourceSource("rp", rp) });
        Assert.Empty(catalog.Warnings);

        Assert.False(catalog.FindById("rp/Emotes/wave")!.IsShared);

        var hug = catalog.FindById("rp/Shared/hug")!;
        Assert.Equal("hug2", hug.PartnerCommand);
        Assert.Equal("rp/Shared/hug2", hug.PartnerId);
        Assert.Equal(PlacementKind.Offset, hug.Placement!.Kind);
        Assert.Equal((0f, 1.05f, 0f, 180f), (hug.Placement.Side, hug.Placement.Front, hug.Placement.Height, hug.Placement.Heading));
        Assert.Equal(-0.05f, catalog.FindById("rp/Shared/hug2")!.Placement!.Side);
        Assert.Equal("rp/Shared/hug", catalog.FindById("rp/Shared/hug2")!.PartnerId);

        var carry2 = catalog.FindById("rp/Shared/carry2")!;
        Assert.Equal(PlacementKind.Attach, carry2.Placement!.Kind);
        Assert.Equal(40269, carry2.Placement.Bone);
        Assert.Equal(new[] { -0.14f, 0.15f, 0.14f, 0f, -59f, -4.5f }, carry2.Placement.Placement);
        Assert.True(carry2.Loop);
        Assert.Null(catalog.FindById("rp/Shared/carry")!.Placement);

        // No placement on either side: the viewer falls back to the default (facing each other 1 m apart).
        var punch = catalog.FindById("rp/Shared/punch")!;
        Assert.Equal("rp/Shared/punched", punch.PartnerId);
        Assert.Null(punch.Placement);

        Assert.Equal(250, catalog.FindById("rp/Shared/cprs")!.StartDelayMs);
        var cprs2 = catalog.FindById("rp/Shared/cprs2")!;
        Assert.Equal(0, cprs2.Placement!.Bone);
        Assert.Equal(new[] { 0.35f, 0.8f, 0f, 0f, 0f, 270f }, cprs2.Placement.Placement);

        // An unknown partner keeps the command but resolves to no id; no fourth element means "same clip on both".
        var lonely = catalog.FindById("rp/Shared/lonely")!;
        Assert.Equal("nobody", lonely.PartnerCommand);
        Assert.Null(lonely.PartnerId);
        var mirror = catalog.FindById("rp/Shared/mirror")!;
        Assert.Equal("mirror", mirror.PartnerCommand);
        Assert.Equal(mirror.Id, mirror.PartnerId);
    }

    [Fact]
    public void ScullySharedEntriesCarryPartnerAndPlacement()
    {
        var sc = WriteScully("""
            return { type = 'synchronized_emotes', options = {
                { Label = 'Bro', Command = 'sbro', Animation = 'hugs_guy_a', Dictionary = 'mp_ped_interaction', Options = { Shared = { FrontOffset = 1.14, OtherEmote = 'sbro2' } }, Synchronized = true },
                { Label = 'Bro 2', Command = 'sbro2', Animation = 'hugs_guy_b', Dictionary = 'mp_ped_interaction', Options = { Shared = { FrontOffset = 1.14, OtherEmote = 'sbro' } }, Synchronized = true },
                { Label = 'Carried', Command = 'scarried', Animation = 'firemans_carry', Dictionary = 'nm', Options = { Flags = { Loop = true }, Shared = { Attach = true, Bone = 40269, OtherEmote = 'scarry', Placement = { vec3(-0.14, 0.15, 0.14), vec3(0.0, -59.0, -4.5) } } }, Synchronized = true },
                { Label = 'Get CPR', Command = 'scprs2', Animation = 'cpr_pumpchest', Dictionary = 'mini@cpr@char_b@cpr_str', Options = { Delay = 250, Shared = { Attach = true, OtherEmote = 'scprs', Placement = { vec3(0.35, 0.8, 0.0), vec3(0.0, 0.0, 270.0) } } }, Synchronized = true },
                { Label = 'Dance', Command = 'sdance', Animation = 'd', Dictionary = 'dict', Options = { Shared = { OtherEmote = 'sdance' } }, Synchronized = true },
            } }
            """);
        var catalog = CatalogBuilder.Build(new[] { new ResourceSource("sc", sc) });
        Assert.Empty(catalog.Warnings);

        var bro = catalog.FindById("sc/synchronized_emotes/sbro")!;
        Assert.Equal("sc/synchronized_emotes/sbro2", bro.PartnerId);
        Assert.Equal(PlacementKind.Offset, bro.Placement!.Kind);
        Assert.Equal((0f, 1.14f), (bro.Placement.Side, bro.Placement.Front));

        var carried = catalog.FindById("sc/synchronized_emotes/scarried")!;
        Assert.Equal("scarry", carried.PartnerCommand);
        Assert.Null(carried.PartnerId); // scarry is not in this file
        Assert.Equal(PlacementKind.Attach, carried.Placement!.Kind);
        Assert.Equal(40269, carried.Placement.Bone);
        Assert.Equal(new[] { -0.14f, 0.15f, 0.14f, 0f, -59f, -4.5f }, carried.Placement.Placement);

        var cpr = catalog.FindById("sc/synchronized_emotes/scprs2")!;
        Assert.Equal(-1, cpr.Placement!.Bone); // no Bone = the entity origin
        Assert.Equal(250, cpr.StartDelayMs);

        var dance = catalog.FindById("sc/synchronized_emotes/sdance")!;
        Assert.Equal(dance.Id, dance.PartnerId);
        Assert.Null(dance.Placement);
    }
}
