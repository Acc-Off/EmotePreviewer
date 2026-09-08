using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EmotePreviewer.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EmotePreviewer.App.Tests;

/// <summary>Resource management against a fake GitHub (an in-memory zip served by a stub HttpMessageHandler).</summary>
public sealed class ResourceApiTests
{
    /// <summary>Serves <c>owner/name/archive/refs/heads/branch.zip</c> as a zip with the GitHub top-level folder layout.</summary>
    sealed class FakeGitHub : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var path = request.RequestUri!.AbsolutePath; // /owner/name/archive/refs/heads/branch.zip
            var parts = path.Trim('/').Split('/');
            if (parts.Length != 6 || parts[1] != "fake-emotes" || parts[5] != "main.zip")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var zip = BuildZip("fake-emotes-main");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"abc123\"");
            return Task.FromResult(response);
        }

        static byte[] BuildZip(string top)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                Add(zip, $"{top}/types.lua", "AnimFlag = { MOVING = 51, LOOP = 1, STUCK = 50 }\n");
                Add(zip, $"{top}/client/AnimationList.lua", """
                    RP = {}
                    RP.Emotes = { ["wave"] = { "friends@frj@ig_1", "wave_a", "Wave" }, ["nod"] = { "gestures@m@standing@casual", "gesture_nod_yes_hard", "Nod" } }
                    """);
                Add(zip, $"{top}/README.md", "fake\n");
            }
            return ms.ToArray();
        }

        static void Add(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name);
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write(content);
        }
    }

    static async Task<JsonElement> WaitForJobAsync(HttpClient client, string id, string state, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            var dto = await client.GetFromJsonAsync<JsonElement>("/api/resources");
            var item = dto.GetProperty("resources").EnumerateArray().FirstOrDefault(r => r.GetProperty("id").GetString() == id);
            if (item.ValueKind == JsonValueKind.Object)
            {
                var job = item.GetProperty("job");
                if (job.ValueKind == JsonValueKind.Object && job.GetProperty("state").GetString() == state) return dto;
                if (state == "done" && job.ValueKind == JsonValueKind.Null && item.GetProperty("exists").GetBoolean()) return dto;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"resource {id} did not reach {state}");
    }

    [Fact]
    public async Task GitHubResourceIsDownloadedExtractedAndCatalogued()
    {
        var github = new FakeGitHub();
        await using var host = await TestHost.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<HttpMessageHandler>(github));

        var templates = await host.Client.GetFromJsonAsync<JsonElement>("/api/resources");
        Assert.Equal(3, templates.GetProperty("templates").GetArrayLength());
        Assert.All(templates.GetProperty("templates").EnumerateArray(), t => Assert.False(t.GetProperty("installed").GetBoolean()));

        var response = await host.Client.PostAsJsonAsync("/api/resources", new { origin = "github", repository = "someone/fake-emotes", @ref = "main" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        Assert.Equal("fake-emotes", id);

        var dto = await WaitForJobAsync(host.Client, id!, "done");
        var item = dto.GetProperty("resources").EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
        Assert.Equal("github", item.GetProperty("origin").GetString());
        Assert.Equal("rpemotes", item.GetProperty("kind").GetString());
        Assert.Equal(2, item.GetProperty("entries").GetInt32());
        Assert.Equal("someone/fake-emotes", item.GetProperty("source").GetProperty("repository").GetString());
        Assert.Equal(1, github.Requests);

        var folder = Path.Combine(host.DataDirectory, "resources", "fake-emotes");
        Assert.True(File.Exists(Path.Combine(folder, "types.lua")));
        Assert.True(File.Exists(Path.Combine(folder, ResourceManager.SourceFileName)));
        Assert.False(Directory.Exists(Path.Combine(host.DataDirectory, "resources", "fake-emotes.extract.tmp")));

        var settings = await host.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        var saved = settings.GetProperty("resources").EnumerateArray().Single();
        Assert.Equal("github", saved.GetProperty("origin").GetString());
        Assert.Equal("main", saved.GetProperty("ref").GetString());

        var catalog = await host.Client.GetFromJsonAsync<JsonElement>("/api/catalog");
        Assert.Equal(2, catalog.GetProperty("entries").GetArrayLength());
        Assert.Equal("fake-emotes/Emotes/nod", catalog.GetProperty("entries")[0].GetProperty("id").GetString());

        // Refresh downloads again; the folder is replaced in place.
        var refresh = await host.Client.PostAsync($"/api/resources/{id}/refresh", null);
        Assert.Equal(HttpStatusCode.Accepted, refresh.StatusCode);
        await WaitForJobAsync(host.Client, id!, "done");
        Assert.Equal(2, github.Requests);

        // Delete removes the settings entry and the files.
        var delete = await host.Client.DeleteAsync($"/api/resources/{id}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.False(Directory.Exists(folder));
        Assert.Equal(0, (await host.Client.GetFromJsonAsync<JsonElement>("/api/catalog")).GetProperty("entries").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync($"/api/resources/{id}")).StatusCode);
    }

    [Fact]
    public async Task UnknownRepositoryReportsAnErrorJob()
    {
        await using var host = await TestHost.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<HttpMessageHandler>(new FakeGitHub()));
        var response = await host.Client.PostAsJsonAsync("/api/resources", new { origin = "github", repository = "someone/nothing-here" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var dto = await WaitForJobAsync(host.Client, "nothing-here", "error");
        var item = dto.GetProperty("resources").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "nothing-here");
        Assert.Contains("not found", item.GetProperty("job").GetProperty("message").GetString());
        Assert.Equal(0, (await host.Client.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("resources").GetArrayLength());

        var bad = await host.Client.PostAsJsonAsync("/api/resources", new { origin = "github", repository = "not-a-repo" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task FolderResourcesAreAddedDetectedAndRejected()
    {
        await using var host = await TestHost.StartAsync();
        var folder = TestHost.WriteRpEmotesResource(host.DataDirectory, "My Emotes");

        var added = await host.Client.PostAsJsonAsync("/api/resources", new { origin = "folder", path = folder });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.Equal("my-emotes", (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString());

        var duplicate = await host.Client.PostAsJsonAsync("/api/resources", new { origin = "folder", path = folder });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var junk = Path.Combine(host.DataDirectory, "junk");
        Directory.CreateDirectory(junk);
        var rejected = await host.Client.PostAsJsonAsync("/api/resources", new { origin = "folder", path = junk });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("UNKNOWN_RESOURCE_LAYOUT", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());

        var detect = await host.Client.GetFromJsonAsync<JsonElement>($"/api/resources/detect?path={Uri.EscapeDataString(folder)}");
        Assert.Equal("rpemotes", detect.GetProperty("kind").GetString());

        Assert.Equal(11, (await host.Client.GetFromJsonAsync<JsonElement>("/api/catalog")).GetProperty("entries").GetArrayLength());

        // Disabling keeps the resource configured but drops it from the catalog; enabling brings it back without re-adding.
        var disable = await host.Client.PostAsJsonAsync("/api/resources/my-emotes/enabled", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.Equal(0, (await host.Client.GetFromJsonAsync<JsonElement>("/api/catalog")).GetProperty("entries").GetArrayLength());
        var listed = (await host.Client.GetFromJsonAsync<JsonElement>("/api/resources")).GetProperty("resources").EnumerateArray().Single();
        Assert.False(listed.GetProperty("enabled").GetBoolean());
        Assert.Equal("rpemotes", listed.GetProperty("kind").GetString());
        var saved = await host.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.False(saved.GetProperty("resources")[0].GetProperty("enabled").GetBoolean());

        var enable = await host.Client.PostAsJsonAsync("/api/resources/my-emotes/enabled", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.Equal(11, (await host.Client.GetFromJsonAsync<JsonElement>("/api/catalog")).GetProperty("entries").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsJsonAsync("/api/resources/nope/enabled", new { enabled = true })).StatusCode);
    }
    [Fact]
    public async Task RescanPicksUpFilesEditedInPlace()
    {
        await using var host = await TestHost.StartAsync(o => o.ConfigureServices = s => s.AddSingleton<HttpMessageHandler>(new FakeGitHub()));
        var folder = TestHost.WriteRpEmotesResource(host.DataDirectory, "edited");
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/api/resources", new { origin = "folder", path = folder })).StatusCode);
        var before = await host.Client.GetFromJsonAsync<JsonElement>("/api/catalog");
        int count = before.GetProperty("entries").GetArrayLength();

        // a converter writes a new .ycd and registers it: the running app must see both after a rescan
        var lua = Path.Combine(folder, "client", "AnimationList.lua");
        File.AppendAllText(lua, Environment.NewLine + "RP.Emotes[\"mine\"] = { \"my_dict\", \"my_dict_clip\", \"Mine\" }" + Environment.NewLine);
        Directory.CreateDirectory(Path.Combine(folder, "stream"));
        File.WriteAllBytes(Path.Combine(folder, "stream", "my_dict.ycd"), new byte[] { 1, 2, 3 });

        var response = await host.Client.PostAsync("/api/resources/edited/rescan", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(folder, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("folder").GetString());
        var after = await host.Client.GetFromJsonAsync<JsonElement>("/api/catalog");
        Assert.Equal(count + 1, after.GetProperty("entries").GetArrayLength());
        Assert.True(after.GetProperty("revision").GetInt32() > before.GetProperty("revision").GetInt32());
        var mine = after.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("command").GetString() == "mine");
        Assert.True(mine.GetProperty("custom").GetBoolean());

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsync("/api/resources/nope/rescan", null)).StatusCode);
        // GitHub resources can be rescanned too (no download involved)
        var github = await host.Client.PostAsJsonAsync("/api/resources", new { origin = "github", repository = "someone/fake-emotes", @ref = "main" });
        var id = (await github.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        await WaitForJobAsync(host.Client, id!, "done");
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsync($"/api/resources/{id}/rescan", null)).StatusCode);
    }

    [Fact]
    public async Task FolderWatcherRescansAfterAFileChange()
    {
        await using var host = await TestHost.StartAsync();
        var folder = TestHost.WriteRpEmotesResource(host.DataDirectory, "watched");
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/api/resources", new { origin = "folder", path = folder })).StatusCode);
        var watcher = host.App.Services.GetRequiredService<FolderWatcher>();
        Assert.Equal(new[] { "watched" }, watcher.Watched);
        int count = (await host.Client.GetFromJsonAsync<JsonElement>("/api/catalog")).GetProperty("entries").GetArrayLength();

        var rescanned = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Rescanned += id => rescanned.TrySetResult(id);
        File.AppendAllText(Path.Combine(folder, "client", "AnimationList.lua"), Environment.NewLine + "RP.Emotes[\"later\"] = { \"later_dict\", \"later_clip\", \"Later\" }" + Environment.NewLine);
        var id = await rescanned.Task.WaitAsync(FolderWatcher.Debounce + TimeSpan.FromSeconds(10));
        Assert.Equal("watched", id);
        Assert.Equal(count + 1, (await host.Client.GetFromJsonAsync<JsonElement>("/api/catalog")).GetProperty("entries").GetArrayLength());

        // switching the watch off drops the watcher; a disabled resource is not watched either
        var settings = await host.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        var patched = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settings.GetRawText())!;
        patched["watchFolders"] = JsonSerializer.SerializeToElement(false);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PutAsJsonAsync("/api/settings", patched)).StatusCode);
        Assert.Empty(watcher.Watched);
    }
}
