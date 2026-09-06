using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EmotePreviewer.App.Services;

namespace EmotePreviewer.App.Tests;

public sealed class ApiTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task StatusReportsVersionAndMissingGtaWithoutGameData()
    {
        await using var host = await TestHost.StartAsync();
        var status = await host.Client.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.Equal(Web.AppVersion.Value, status.GetProperty("version").GetString());
        // Game data is not started in tests, so the initial state is the default one.
        Assert.Equal("missingGta", status.GetProperty("gta").GetString());
        Assert.Equal(0, status.GetProperty("catalogEntries").GetInt32());
    }

    [Fact]
    public async Task UnknownApiPathReturnsErrorEnvelope()
    {
        await using var host = await TestHost.StartAsync();
        var response = await host.Client.GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NOT_FOUND", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ForeignHostHeaderIsRejected()
    {
        await using var host = await TestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Host = "evil.example";
        var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("BAD_HOST", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task CatalogIsBuiltFromConfiguredResourcesWithStableIds()
    {
        await using var host = await TestHost.StartAsync(prepareDataDirectory: dir =>
        {
            var resource = TestHost.WriteRpEmotesResource(dir);
            var settings = new AppSettings { Resources = { new ResourceSetting { Id = "fake", Path = resource } } };
            File.WriteAllText(Path.Combine(dir, SettingsStore.FileName), JsonSerializer.Serialize(settings, Json));
        });

        var catalog = await host.Client.GetFromJsonAsync<JsonElement>("/api/catalog");
        var entries = catalog.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(11, entries.Count);
        Assert.False(catalog.GetProperty("previewResolved").GetBoolean());
        var expression = entries.Single(e => e.GetProperty("command").GetString() == "angry");
        Assert.Equal("kind", expression.GetProperty("previewReason").GetString());

        var wave = entries.Single(e => e.GetProperty("command").GetString() == "wave");
        Assert.Equal("fake/Emotes/wave", wave.GetProperty("id").GetString());
        Assert.Equal("fake", wave.GetProperty("source").GetString());
        Assert.Equal("animation", wave.GetProperty("kind").GetString());
        Assert.Equal("friends@frj@ig_1", wave.GetProperty("dictionary").GetString());
        Assert.True(wave.GetProperty("loop").GetBoolean());
        Assert.False(wave.GetProperty("previewable").GetBoolean());
        Assert.Equal("not-indexed", wave.GetProperty("previewReason").GetString());

        var guitar = entries.Single(e => e.GetProperty("command").GetString() == "guitar");
        var prop = guitar.GetProperty("props").EnumerateArray().Single();
        Assert.Equal("prop_acc_guitar_01", prop.GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.Null, prop.GetProperty("available").ValueKind); // unknown until the game data is indexed
        Assert.Equal(24818, prop.GetProperty("bone").GetInt32());
        Assert.Equal(6, prop.GetProperty("placement").GetArrayLength());

        // Walks are tried as "<clip set>/walk", so they are resolved like animations once the game data is indexed.
        var walk = entries.Single(e => e.GetProperty("command").GetString() == "alien");
        Assert.Equal("walk", walk.GetProperty("kind").GetString());
        Assert.Equal("move_m@alien", walk.GetProperty("dictionary").GetString());
        Assert.Equal("walk", walk.GetProperty("clip").GetString());
        Assert.Equal("not-indexed", walk.GetProperty("previewReason").GetString());

        var source = catalog.GetProperty("sources").EnumerateArray().Single();
        Assert.Equal("rpemotes", source.GetProperty("kind").GetString());
        Assert.Equal(11, source.GetProperty("entries").GetInt32());

        var status = await host.Client.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.Equal(11, status.GetProperty("catalogEntries").GetInt32());
        Assert.Equal(JsonValueKind.Null, wave.GetProperty("partner").ValueKind);
        Assert.Equal(JsonValueKind.Null, wave.GetProperty("partnerCommand").ValueKind);
    }

    [Fact]
    public async Task SharedEmotesExposeTheirPartnerAndPlacement()
    {
        await using var host = await TestHost.StartAsync(prepareDataDirectory: dir =>
        {
            var resource = TestHost.WriteRpEmotesResource(dir);
            var settings = new AppSettings { Resources = { new ResourceSetting { Id = "fake", Path = resource } } };
            File.WriteAllText(Path.Combine(dir, SettingsStore.FileName), JsonSerializer.Serialize(settings, Json));
        });
        var catalog = await host.Client.GetFromJsonAsync<JsonElement>("/api/catalog");
        var entries = catalog.GetProperty("entries").EnumerateArray().ToDictionary(e => e.GetProperty("command").GetString()!);

        // Offsets: the main ped's own offset places the partner in front of it, turned around to face it.
        var hug = entries["hug"];
        Assert.Equal("hug2", hug.GetProperty("partnerCommand").GetString());
        var partner = hug.GetProperty("partner");
        Assert.Equal("fake/Shared/hug2", partner.GetProperty("id").GetString());
        Assert.Equal("kisses_guy_b", partner.GetProperty("clip").GetString());
        Assert.Equal("not-indexed", partner.GetProperty("previewReason").GetString());
        var placement = partner.GetProperty("placement");
        Assert.Equal("offset", placement.GetProperty("kind").GetString());
        Assert.Equal(new[] { 0f, 1.05f, 0f }, placement.GetProperty("position").EnumerateArray().Select(v => v.GetSingle()).ToArray());
        Assert.Equal(180f, placement.GetProperty("heading").GetSingle());
        Assert.True(placement.GetProperty("explicit").GetBoolean());
        // Seen from hug2 the side offset keeps its sign: -Rz(180) * (-0.05, 1.18, 0) = (-0.05, 1.18, 0).
        var hug2Placement = entries["hug2"].GetProperty("partner").GetProperty("placement");
        Assert.Equal(new[] { -0.05f, 1.18f, 0f }, hug2Placement.GetProperty("position").EnumerateArray().Select(v => v.GetSingle()).ToArray());

        // Attachments: whichever side carries Attachto hangs on the other ped's bone.
        var carry = entries["carry"].GetProperty("partner").GetProperty("placement");
        Assert.Equal("attach", carry.GetProperty("kind").GetString());
        Assert.Equal("partner", carry.GetProperty("attached").GetString());
        Assert.Equal(40269, carry.GetProperty("bone").GetInt32());
        Assert.Equal(6, carry.GetProperty("offset").GetArrayLength());
        var carry2 = entries["carry2"].GetProperty("partner").GetProperty("placement");
        Assert.Equal("main", carry2.GetProperty("attached").GetString());
        Assert.True(entries["carry2"].GetProperty("partner").GetProperty("loop").GetBoolean() == false); // carry is MOVING, not LOOP

        // No placement on either side: the default, flagged as such.
        var punch = entries["punch"].GetProperty("partner").GetProperty("placement");
        Assert.Equal("offset", punch.GetProperty("kind").GetString());
        Assert.False(punch.GetProperty("explicit").GetBoolean());
        Assert.Equal(new[] { 0f, 1f, 0f }, punch.GetProperty("position").EnumerateArray().Select(v => v.GetSingle()).ToArray());

        // Unresolved partner: the command stays, the partner object is null, the delay is passed through.
        var lonely = entries["lonely"];
        Assert.Equal("nobody", lonely.GetProperty("partnerCommand").GetString());
        Assert.Equal(JsonValueKind.Null, lonely.GetProperty("partner").ValueKind);
        Assert.Equal(250, lonely.GetProperty("startDelayMs").GetInt32());
    }

    [Fact]
    public async Task PartnerPedSettingIsNormalised()
    {
        await using var host = await TestHost.StartAsync();
        var initial = await host.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("partnerPed").ValueKind);
        Assert.True(initial.GetProperty("viewer").GetProperty("showPartner").GetBoolean());

        var response = await host.Client.PutAsJsonAsync("/api/settings", new { ped = "mp_m_freemode_01", partnerPed = "MP_F_Freemode_01", viewer = new { showPartner = false } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mp_f_freemode_01", saved.GetProperty("partnerPed").GetString());
        Assert.False(saved.GetProperty("viewer").GetProperty("showPartner").GetBoolean());

        // The same ped as the main one means "same as main" and is stored as null; junk names are dropped.
        response = await host.Client.PutAsJsonAsync("/api/settings", new { ped = "mp_m_freemode_01", partnerPed = "mp_m_freemode_01" });
        Assert.Equal(JsonValueKind.Null, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("partnerPed").ValueKind);
        response = await host.Client.PutAsJsonAsync("/api/settings", new { ped = "mp_m_freemode_01", partnerPed = "../x" });
        Assert.Equal(JsonValueKind.Null, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("partnerPed").ValueKind);
    }

    [Fact]
    public async Task SettingsRoundTripAndRebuildCatalog()
    {
        await using var host = await TestHost.StartAsync();
        var initial = await host.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal(0, initial.GetProperty("resources").GetArrayLength());
        Assert.Equal("mp_m_freemode_01", initial.GetProperty("ped").GetString());

        var resource = TestHost.WriteRpEmotesResource(host.DataDirectory);
        var response = await host.Client.PutAsJsonAsync("/api/settings", new
        {
            gtaFolder = (string?)null,
            keysFolder = (string?)null,
            resources = new[] { new { id = "fake", path = resource, origin = "folder" } },
            ped = "mp_m_freemode_01",
            viewer = new { showHelperBones = true, rootMotion = false, theme = "dark", language = "en" },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var saved = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(Path.Combine(host.DataDirectory, SettingsStore.FileName)), Json)!;
        Assert.Single(saved.Resources);
        Assert.Equal("dark", saved.Viewer.Theme);
        Assert.True(saved.Viewer.ShowHelperBones);

        var catalog = await host.Client.GetFromJsonAsync<JsonElement>("/api/catalog");
        Assert.Equal(11, catalog.GetProperty("entries").GetArrayLength());

        var bad = await host.Client.PutAsJsonAsync("/api/settings", new { gtaFolder = Path.Combine(host.DataDirectory, "nowhere"), resources = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var error = await bad.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_GTA_FOLDER", error.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ClipEndpointsFailCleanlyWithoutGameData()
    {
        await using var host = await TestHost.StartAsync(prepareDataDirectory: dir =>
        {
            var resource = TestHost.WriteRpEmotesResource(dir);
            var settings = new AppSettings { Resources = { new ResourceSetting { Id = "fake", Path = resource } } };
            File.WriteAllText(Path.Combine(dir, SettingsStore.FileName), JsonSerializer.Serialize(settings, Json));
        });

        var skeleton = await host.Client.GetAsync("/api/skeleton");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, skeleton.StatusCode);

        var clip = await host.Client.GetAsync("/api/emotes/" + Uri.EscapeDataString("fake/Emotes/wave") + "/clip.bin");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, clip.StatusCode);
        var error = await clip.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("GTA_NOT_READY", error.GetProperty("error").GetProperty("code").GetString());

        var walk = await host.Client.GetAsync("/api/emotes/" + Uri.EscapeDataString("fake/Walks/alien") + "/clip");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, walk.StatusCode);

        var missing = await host.Client.GetAsync("/api/emotes/nope/clip");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var expression = await host.Client.GetAsync("/api/emotes/" + Uri.EscapeDataString("fake/Expressions/angry") + "/clip");
        Assert.Equal(HttpStatusCode.Conflict, expression.StatusCode);
        error = await expression.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NOT_PREVIEWABLE", error.GetProperty("error").GetProperty("code").GetString());

        var prop = await host.Client.GetAsync("/api/props/prop_acc_guitar_01.bin");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, prop.StatusCode);
        var ped = await host.Client.GetAsync("/api/ped/mp_m_freemode_01/uppr");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ped.StatusCode);
        var peds = await host.Client.GetAsync("/api/peds");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, peds.StatusCode);
        var animalSkeleton = await host.Client.GetAsync("/api/skeleton?ped=a_c_rottweiler");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, animalSkeleton.StatusCode);
        var badPed = await host.Client.GetAsync("/api/skeleton?ped=" + Uri.EscapeDataString("../x"));
        Assert.Equal(HttpStatusCode.BadRequest, badPed.StatusCode);
        var texture = await host.Client.GetAsync("/api/textures/ped/mp_m_freemode_01/head_diff_000_a_whi.dds");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, texture.StatusCode);
        var badScope = await host.Client.GetAsync("/api/textures/nope/x/y.dds");
        Assert.Equal(HttpStatusCode.BadRequest, badScope.StatusCode);
        var badModel = await host.Client.GetAsync("/api/props/" + Uri.EscapeDataString("..\evil"));
        Assert.Equal(HttpStatusCode.BadRequest, badModel.StatusCode);
    }

    [Fact]
    public async Task EventStreamSendsInitialStatusAndCatalog()
    {
        await using var host = await TestHost.StartAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await host.Client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        var events = new List<string>();
        while (events.Count < 2)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line == null) break;
            if (line.StartsWith("event: ", StringComparison.Ordinal)) events.Add(line[7..]);
        }
        Assert.Equal(new[] { "status", "catalog" }, events);
    }

    [Fact]
    public async Task DiagnosticsAndNoticesAreServed()
    {
        await using var host = await TestHost.StartAsync();
        var diagnostics = await host.Client.GetFromJsonAsync<JsonElement>("/api/diagnostics");
        Assert.Equal(host.DataDirectory, diagnostics.GetProperty("dataDirectory").GetString());
        Assert.Equal(4, diagnostics.GetProperty("missingKeyFiles").GetArrayLength());
        Assert.StartsWith("http://127.0.0.1:", diagnostics.GetProperty("url").GetString());

        var notices = await host.Client.GetAsync("/api/notices");
        Assert.Equal(HttpStatusCode.OK, notices.StatusCode);
    }
}
