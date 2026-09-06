using EmotePreviewer.Core.Adapters.GtaToolkit;
using EmotePreviewer.Core.Anim;
using EmotePreviewer.Core.Catalog;
using EmotePreviewer.Core.Gta;
using EmotePreviewer.Core.Rage;

namespace EmotePreviewer.Core.Tests.Gta;

public sealed class PedNamingTests
{
    [Theory]
    [InlineData("uppr_003_r", "uppr", 3, true)]
    [InlineData("HEAD_000_U", "head", 0, false)]
    [InlineData("jbib_012_u", "jbib", 12, false)]
    public void ParsesComponentFileNames(string file, string slot, int number, bool race)
    {
        var parsed = PedNaming.ParseFile(file);
        Assert.NotNull(parsed);
        Assert.Equal(slot, parsed!.Slot);
        Assert.Equal(number, parsed.Number);
        Assert.Equal(race, parsed.Race);
    }

    [Theory]
    [InlineData("uppr_diff_000_a_uni")]
    [InlineData("nothing")]
    [InlineData("uppr_003")]
    [InlineData("hat_000_u")]
    public void RejectsOtherNames(string file) => Assert.Null(PedNaming.ParseFile(file));

    [Fact]
    public void StripsThePedPrefix()
    {
        var parsed = PedNaming.ParseFile("a_m_y_business_01_lowr_002_u", "a_m_y_business_01");
        Assert.Equal(("lowr", 2), (parsed!.Slot, parsed.Number));
    }

    [Theory]
    [InlineData("UPPR_DIFF_000_A_UNI", "uppr", 0)]
    [InlineData("head_diff_002_a_Lat", "head", 2)]
    public void ParsesDiffuseNames(string texture, string slot, int number)
    {
        Assert.Equal((slot, number), PedNaming.ParseDiffuse(texture));
    }

    [Fact]
    public void DrawableCandidatesResolveHashesBothWays()
    {
        var table = PedNaming.DrawableNameCandidates("s_m_y_cop_01");
        Assert.Equal("head_002_r", table[JenkinsHash.HashLower("head_002_r")]);
        Assert.Equal("jbib_000_u", table[JenkinsHash.HashLower("s_m_y_cop_01_jbib_000_u")]);
    }

    [Theory]
    [InlineData("a_c_rottweiler", "animal")]
    [InlineData("mp_m_freemode_01", "multiplayer")]
    [InlineData("s_m_y_cop_01", "service")]
    [InlineData("ig_bankman", "story")]
    [InlineData("csb_agent", "cutscene")]
    [InlineData("prop_bench_01a", null)]
    public void CategorisesByPrefix(string ped, string? category) => Assert.Equal(category, PedNaming.Category(ped));
}

public sealed class AnimalPedsTests
{
    [Theory]
    [InlineData("creatures@rottweiler@amb@world_dog_barking@idle_a", "a_c_rottweiler")]
    [InlineData("creatures@pug@move", "a_c_pug")]
    [InlineData("creatures@cat@amb@world_cat_sleeping_ledge@idle_a", "a_c_cat_01")]
    [InlineData("anim@mp_player_intcelebrationfemale@air_guitar", null)]
    public void MapsCreatureDictionaries(string dictionary, string? ped) => Assert.Equal(ped, AnimalPeds.ForDictionary(dictionary));

    [Fact]
    public void FallsBackToPedTypesThenToADog()
    {
        Assert.Equal("a_c_pug", AnimalPeds.Resolve("misssnowie@little_doggy_lying_down", new[] { "small_dogs" }));
        Assert.Equal("a_c_pug", AnimalPeds.Resolve("misssnowie@little_doggy_lying_down", Array.Empty<string>()));
        Assert.Equal("a_c_rottweiler", AnimalPeds.Resolve("amb@lo_res_idles@", Array.Empty<string>(), "world_dog_idle"));
        Assert.Equal("a_c_cat_01", AnimalPeds.Resolve("amb@lo_res_idles@", Array.Empty<string>(), "cat_sleep"));
    }

    [Fact]
    public void EntriesExposeTheirAnimalPed()
    {
        var animal = new EmoteEntry { Source = "s", Category = "AnimalEmotes", Command = "bdogbark", Label = "", Kind = EmoteKind.Animation, Dictionary = "creatures@rottweiler@amb@world_dog_barking@idle_a", Clip = "idle_a" };
        Assert.True(animal.IsAnimal);
        Assert.Equal("a_c_rottweiler", animal.AnimalPed);
        var human = new EmoteEntry { Source = "s", Category = "Emotes", Command = "wave", Label = "", Kind = EmoteKind.Animation, Dictionary = "anim@x", Clip = "wave" };
        Assert.False(human.IsAnimal);
        Assert.Null(human.AnimalPed);
        // rpemotes flags both sides of a human–animal pair; the human side keeps its human dictionary.
        var humanSide = new EmoteEntry { Source = "s", Category = "Shared", Command = "csdog3", Label = "", Kind = EmoteKind.Animation, Dictionary = "hooman@hugging_little_doggy", Clip = "a", AnimalFlag = true };
        Assert.False(humanSide.IsAnimal);
        var dogSide = new EmoteEntry { Source = "s", Category = "Shared", Command = "csdog4", Label = "", Kind = EmoteKind.Animation, Dictionary = "little_doggy@hugging_hooman", Clip = "a", AnimalFlag = true };
        Assert.True(dogSide.IsAnimal);
        Assert.Equal("a_c_pug", dogSide.AnimalPed);
    }
}

/// <summary>Ped listing, component selection and textures against the local GTA V install. Skipped without it.</summary>
public sealed class PedGameDataTests
{
    static GtaToolkitGameData OpenOrSkip()
    {
        var gta = GtaLocator.Detect();
        Skip.If(gta == null, "GTA V not installed");
        try { return GtaToolkitGameData.Open(gta!); }
        catch (GtaKeys.KeyMaterialMissingException ex) { throw new SkipException(ex.Message); }
    }

    [SkippableFact]
    public void ListsPedsOfBothStorageForms()
    {
        using var gd = OpenOrSkip();
        var peds = gd.ListPeds().ToDictionary(p => p.Name);
        Assert.True(peds.Count > 500, $"only {peds.Count} peds");
        Assert.Equal(PedStorage.Folder, peds["mp_m_freemode_01"].Storage);
        Assert.Equal(PedStorage.Folder, peds["a_c_rottweiler"].Storage);
        Assert.Equal(PedStorage.Component, peds["a_m_y_business_01"].Storage);
        Assert.Equal("animal", peds["a_c_pug"].Category);
        Assert.DoesNotContain(peds.Keys, k => k.StartsWith("prop_"));
    }

    [SkippableFact]
    public void ComponentPedDrawablesClassifyBySlot()
    {
        using var gd = OpenOrSkip();
        var drawables = gd.LoadPedDictionary("a_m_y_business_01");
        Assert.True(drawables.Count >= 6);
        var slots = new HashSet<string>();
        foreach (var (name, mesh) in drawables)
        {
            var parsed = PedNaming.ParseFile(name, "a_m_y_business_01");
            var slot = parsed?.Slot ?? mesh.SubMeshes.Select(s => s.Diffuse).Where(d => d != null).Select(d => PedNaming.ParseDiffuse(d!)?.slot).FirstOrDefault(s => s != null);
            Assert.NotNull(slot);
            slots.Add(slot!);
            Assert.True(mesh.IsSkinned);
        }
        Assert.Superset(new HashSet<string> { "head", "uppr", "lowr", "jbib" }, slots);
    }

    [SkippableFact]
    public void PropEmbedsItsDiffuseTextures()
    {
        using var gd = OpenOrSkip();
        var mesh = gd.LoadDrawable("p_amb_brolly_01");
        Assert.NotNull(mesh);
        Assert.Equal(3, mesh!.EmbeddedTextures.Count);
        Assert.All(mesh.SubMeshes, s => Assert.NotNull(s.Diffuse));
        Assert.All(mesh.SubMeshes, s => Assert.True(s.DiffuseEmbedded));
        var chrome = mesh.FindEmbedded("prop_chrome_dirty_02");
        Assert.NotNull(chrome);
        Assert.Equal(EmotePreviewer.Core.Textures.TextureFormat.Bc1, chrome!.Format);
        Assert.Equal((256, 256), (chrome.Width, chrome.Height));
        Assert.Equal(chrome.Data.Length + 128 + 2 * 8, chrome.ToDds().Length); // chain stored down to 4×4, padded to 1×1
    }

    [SkippableFact]
    public void HairHullsOfTheSecondaryPassAreHidden()
    {
        using var gd = OpenOrSkip();
        var hair = gd.LoadPedComponent("mp_f_freemode_01", "hair_001_u");
        Assert.NotNull(hair);
        Assert.Equal(2, hair!.SubMeshes.Count);
        Assert.All(hair.SubMeshes, s => Assert.Equal("ped_hair_spiked", s.ShaderName));
        Assert.All(hair.SubMeshes, s => Assert.True(s.Cutout));
        Assert.False(hair.SubMeshes[0].Hidden);
        Assert.True(hair.SubMeshes[1].Hidden);
        var body = gd.LoadPedComponent("mp_f_freemode_01", "uppr_000_r");
        Assert.All(body!.SubMeshes, s => Assert.False(s.Hidden));
        Assert.All(body.SubMeshes, s => Assert.False(s.Cutout));
    }

    [SkippableFact]
    public void PedTexturesResolveForBothStorageForms()
    {
        using var gd = OpenOrSkip();
        var head = gd.LoadPedTexture("mp_m_freemode_01", "head_diff_000_a_whi");
        Assert.NotNull(head);
        Assert.Equal((512, 512), (head!.Width, head.Height));
        Assert.True(head.IsCompressed);
        var component = gd.LoadPedDictionary("a_m_y_business_01").Select(d => d.mesh).First(m => m.SubMeshes.Any(s => s.Diffuse != null));
        var diffuse = component.SubMeshes.First(s => s.Diffuse != null).Diffuse!;
        Assert.NotNull(gd.LoadTexture("a_m_y_business_01", diffuse));
    }

    [SkippableFact]
    public void AnimalClipBakesOntoTheAnimalSkeleton()
    {
        using var gd = OpenOrSkip();
        var skel = gd.LoadSkeleton("a_c_rottweiler");
        Assert.NotNull(skel);
        var dict = gd.LoadClipDictionary("creatures@rottweiler@amb@world_dog_barking@idle_a");
        Skip.If(dict == null, "dictionary not in this game version");
        var clip = dict!.FindClip("idle_a");
        Skip.If(clip == null, "clip missing");
        var baked = ClipBaker.Bake(clip!, skel!);
        Assert.Equal(skel!.Bones.Count, baked.BoneCount);
        Assert.True(baked.AnimatedBones.Count >= 30, $"only {baked.AnimatedBones.Count} animated bones");
    }
}
