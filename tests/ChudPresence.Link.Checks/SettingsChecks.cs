using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using ChudPresence.Link;

internal static class SettingsChecks
{
    public static async Task RunHttpAsync(string temporary, Action<bool, string> check)
    {
        var store = new LinkStore(Path.Combine(temporary, "settings-http"));
        var browserCode = store.CreatePairingCode("browser");
        var browser = store.Pair(new(browserCode.Code, "Browser", "browser"), "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var phoneCode = store.CreatePairingCode("mobile");
        var phone = store.Pair(new(phoneCode.Code, "Phone", "mobile"), null);
        await using var presence = new PresenceCoordinator(new UnusedDiscord());
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        await using var app = LinkServer.Create(store, presence, port);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", browser.Token);
        http.DefaultRequestHeaders.Add("Origin", "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        using var mobile = new HttpClient { BaseAddress = http.BaseAddress };
        mobile.DefaultRequestHeaders.Authorization = new("Bearer", phone.Token);
        var service = new ClientService("video", "Video", "title", Enabled: false, ShowWhilePaused: true,
            Artwork: false, PlaybackProgress: true, ActionButtons: true);
        using var report = await http.PutAsJsonAsync("/v1/services", new ServicesRequest([service]));
        report.EnsureSuccessStatusCode();
        using var phoneReport = await mobile.PutAsJsonAsync("/v1/services", new ServicesRequest([service]));
        phoneReport.EnsureSuccessStatusCode();
        var discovery = (await phoneReport.Content.ReadFromJsonAsync<ServicesSnapshot>())!.Services.Single();
        check(discovery.SupportedFields.SequenceEqual(ServiceValues.AllFields) && discovery.Participants.All(p => p.Enabled == false && p.Artwork == false),
            "HTTP inventory negotiates all six fields and preserves false reported values");
        using var enable = await http.PutAsJsonAsync("/v1/services/video/sync", new ServiceSyncRequest(true, 0, phone.DeviceId, Fields: ServiceValues.AllFields));
        enable.EnsureSuccessStatusCode();
        var snapshot = (await mobile.GetFromJsonAsync<SettingsSnapshot>("/v1/settings"))!;
        using var patch = await http.PatchAsJsonAsync("/v1/settings", new { changes = new { video = new { baseVersion = snapshot.Services["video"].Version, artwork = true, actionButtons = false } } });
        patch.EnsureSuccessStatusCode();
        var saved = (await patch.Content.ReadFromJsonAsync<SettingsResult>())!.Services["video"];
        check(saved.Artwork == true && saved.ActionButtons == false && saved.ShowWhilePaused == true && saved.Enabled == false && saved.Display == "title",
            "HTTP boolean-only patches preserve omitted fields without requiring display");

        async Task Reject(string json, string name)
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await http.PatchAsync("/v1/settings", content);
            check(response.StatusCode == HttpStatusCode.BadRequest && store.Settings().Services["video"] == saved, name);
        }
        await Reject("{\"changes\":{\"video\":{\"artwork\":false}}}", "HTTP rejects preference patches without baseVersion");
        await Reject($"{{\"changes\":{{\"video\":{{\"baseVersion\":{saved.Version},\"artwork\":\"false\"}}}}}}", "HTTP rejects nonboolean setting values");
        await Reject($"{{\"changes\":{{\"video\":{{\"baseVersion\":{saved.Version},\"artwork\":null}}}}}}", "HTTP rejects a patch containing no setting values");
        await Reject($"{{\"changes\":{{\"video\":{{\"baseVersion\":{saved.Version},\"artwork\":false,\"password\":\"secret\"}}}}}}", "HTTP rejects unknown preference fields before applying valid ones");
        var selected = store.Discover().Services.Single();
        using var subset = await mobile.PutAsJsonAsync("/v1/services/video/sync", new
        {
            enabled = true, baseVersion = selected.SyncVersion, fields = new[] { "artwork", "showWhilePaused" },
            initialSettings = new { artwork = false, showWhilePaused = true }
        });
        subset.EnsureSuccessStatusCode();
        var filtered = await http.GetFromJsonAsync<JsonElement>("/v1/settings");
        var filteredService = filtered.GetProperty("services").GetProperty("video");
        check(!filteredService.TryGetProperty("display", out _) && !filteredService.TryGetProperty("enabled", out _) &&
            !filteredService.GetProperty("artwork").GetBoolean() && filteredService.GetProperty("showWhilePaused").GetBoolean(),
            "HTTP explicit initial settings and snapshots include only selected fields");
        using var unselected = await http.PatchAsJsonAsync("/v1/settings", new { changes = new { video = new { baseVersion = filteredService.GetProperty("version").GetInt64(), enabled = true } } });
        var error = await unselected.Content.ReadFromJsonAsync<JsonElement>();
        check(unselected.StatusCode == HttpStatusCode.Conflict && error.GetProperty("error").GetString() == "field_not_synced",
            "HTTP rejects deselected fields with a specific conflict code");
        using var noDisplay = await mobile.PutAsJsonAsync("/v1/services", new { services = new[] { new { id = "video", name = "Video", artwork = true, showWhilePaused = false } } });
        noDisplay.EnsureSuccessStatusCode();
        var noDisplayService = (await noDisplay.Content.ReadFromJsonAsync<ServicesSnapshot>())!.Services.Single();
        check(noDisplayService.SyncEnabled && !noDisplayService.SupportedFields.Contains("display") && noDisplayService.DisplayChoices.Length == 0,
            "HTTP supports boolean-only client inventories without a display value");
        await app.StopAsync();
    }

    public static void Run(string temporary, Action<bool, string> check, Action<Action, string> fails)
    {
        var directory = Path.Combine(temporary, "selective-settings");
        var store = new LinkStore(directory);
        var browserCode = store.CreatePairingCode("browser");
        var browser = store.Pair(new(browserCode.Code, "Browser", "browser"), "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var phoneCode = store.CreatePairingCode("mobile");
        var phone = store.Pair(new(phoneCode.Code, "Phone", "mobile"), null);
        var browserService = new ClientService("video", "Video", "creator", Enabled: true, ShowWhilePaused: false,
            Artwork: true, PlaybackProgress: true, ActionButtons: true);
        var phoneService = browserService with { Display = "title", Enabled = false, ShowWhilePaused = true, Artwork = false };
        store.ReportServices(browser.DeviceId, [browserService]);
        store.ReportServices(phone.DeviceId, [phoneService]);
        SharedService Service() => store.Discover().Services.Single();
        ServicePreference Preference() => store.Settings().Services["video"];
        void Select(string[]? fields, string? source = null) => store.SetServiceSync("video", new(true, Service().SyncVersion, source ?? browser.DeviceId, Fields: fields));

        check(Service().SupportedFields.SequenceEqual(ServiceValues.AllFields) && !Service().SyncEnabled,
            "all six reported settings are offered without opting in");
        Select(null);
        var legacyVersion = Preference().Version;
        check(Service().SyncFields.SequenceEqual(["display"]) && Preference().Fields.SequenceEqual(["display"]),
            "omitting a field selection preserves display-only behavior");
        var legacyJson = JsonSerializer.Serialize(Preference(), LinkStore.Json);
        check(!legacyJson.Contains("enabled") && !legacyJson.Contains("artwork"), "unselected boolean settings are absent from JSON snapshots");
        Select(ServiceValues.AllFields);
        var all = Preference();
        check(all.Enabled == true && all.ShowWhilePaused == false && all.Artwork == true && all.PlaybackProgress == true && all.ActionButtons == true &&
            all.Display == "creator" && all.Version > legacyVersion, "all settings initialize from the selected device, including false values");
        var result = store.Update(new() { ["video"] = new(all.Version, Enabled: false) }, phone.DeviceId);
        var disabled = result.Services["video"];
        check(disabled.Enabled == false && disabled.Display == all.Display && disabled.Artwork == all.Artwork && disabled.ShowWhilePaused == all.ShowWhilePaused &&
            disabled.PlaybackProgress == all.PlaybackProgress && disabled.ActionButtons == all.ActionButtons && Service().SyncEnabled,
            "disabling a service leaves settings synchronization active and preserves omitted settings");
        var retry = store.Update(new() { ["video"] = new(all.Version, Enabled: false) });
        check(retry.Conflicts.Length == 0 && retry.Revision == result.Revision, "boolean retries are idempotent with their original version");
        var stale = store.Update(new() { ["video"] = new(all.Version, Artwork: false, ShowWhilePaused: true) });
        check(stale.Conflicts.SequenceEqual(["video"]) && Preference() == disabled, "stale multi-setting edits cannot partially overwrite a service");
        var edited = store.Update(new() { ["video"] = new(disabled.Version, ShowWhilePaused: true, Artwork: false, PlaybackProgress: false, ActionButtons: false) }).Services["video"];
        check(edited.ShowWhilePaused == true && edited.Artwork == false && edited.PlaybackProgress == false && edited.ActionButtons == false && edited.Enabled == false,
            "paused visibility, artwork, progress, and action buttons accept independent boolean changes");
        fails(() => store.Update(new() { ["video"] = new(edited.Version) }), "invalid_settings");
        fails(() => Select([]), "invalid_sync");
        fails(() => Select(["artwork", "artwork"]), "invalid_sync");
        fails(() => Select(["password"]), "invalid_sync");
        fails(() => store.SetServiceSync("video", new(true, Service().SyncVersion, browser.DeviceId, InitialSettings: new(Artwork: true), Fields: ["artwork"])), "invalid_sync");
        fails(() => store.SetServiceSync("video", new(true, Service().SyncVersion, Fields: ["artwork"], InitialDisplay: "title")), "invalid_sync");

        Select(["enabled", "artwork"], phone.DeviceId);
        var subset = Preference();
        check(subset.Fields.SequenceEqual(["enabled", "artwork"]) && subset.Enabled == false && subset.Artwork == false,
            "a boolean-only selection initializes without syncing the Discord bar");
        var subsetJson = JsonSerializer.Serialize(subset, LinkStore.Json);
        check(!subsetJson.Contains("display") && !subsetJson.Contains("showWhilePaused") && subsetJson.Contains("false"),
            "snapshots omit every unselected setting and preserve explicit false");
        fails(() => store.Update(new() { ["video"] = new(subset.Version, Enabled: true, ActionButtons: true) }), "field_not_synced");
        check(Preference() == subset, "an unselected field rejects the whole patch before any writes");
        fails(() => store.Update(new() { ["video"] = new(subset.Version, Artwork: true), ["unreported"] = new(0, Artwork: false) }), "sync_not_enabled");
        check(Preference() == subset, "invalid service batches leave the selected booleans untouched");

        store.ReportServices(phone.DeviceId, [phoneService with { Artwork = null }]);
        check(Service().SyncRequested && !Service().SyncEnabled && store.Settings().Services.Count == 0 && !Service().SupportedFields.Contains("artwork"),
            "a participant losing a selected capability pauses sync without forgetting consent");
        fails(() => Select(["artwork"]), "unsupported_field");
        store.ReportServices(phone.DeviceId, [phoneService]);
        check(Service().SyncEnabled && Preference() == subset, "restoring support resumes the exact saved selection and values");
        var restored = new LinkStore(directory);
        check(restored.Discover().Services.Single().SyncFields.SequenceEqual(["enabled", "artwork"]) && restored.Settings().Services["video"] == subset,
            "selected fields and boolean values survive a restart");

        store.SetServiceSync("video", new(false, Service().SyncVersion));
        check(!Service().SyncRequested && !Service().SyncEnabled && store.Settings().Services.Count == 0, "turning off sync hides all selected settings");
        Select(["enabled", "artwork"]);
        var fresh = Preference();
        var oldQueue = store.Update(new() { ["video"] = new(subset.Version, Enabled: false) });
        check(fresh.Version > subset.Version && oldQueue.Conflicts.SequenceEqual(["video"]) && Preference() == fresh,
            "reenabling sync invalidates edits queued under the previous selection");
        store.SetServiceSync("video", new(true, Service().SyncVersion, Fields: ["showWhilePaused", "actionButtons"],
            InitialSettings: new(ShowWhilePaused: false, ActionButtons: false)));
        check(Preference().ShowWhilePaused == false && Preference().ActionButtons == false && Preference().Display is null,
            "explicit initial settings support boolean-only selections");

        store.ReportServices(browser.DeviceId, [browserService with { Display = "creator", DisplayChoices = ["creator"] }]);
        store.ReportServices(phone.DeviceId, [phoneService with { Display = "title", DisplayChoices = ["title"] }]);
        check(Service().DisplayChoices.Length == 0 && Service().SyncEnabled, "boolean sync continues when devices have no common Discord bar choice");
        fails(() => Select(null), "service_not_shared");
        Select(["artwork"]);
        check(Preference().Artwork == true && Preference().Fields.SequenceEqual(["artwork"]), "boolean-only sync can be configured without a common display");
        store.ReportServices(phone.DeviceId, [phoneService with { Display = null, DisplayChoices = null }]);
        check(Service().SyncEnabled && !Service().SupportedFields.Contains("display") && Preference().Artwork == true,
            "a client can omit an unmappable local display while syncing its supported booleans");
        fails(() => store.ReportServices(phone.DeviceId, [new("video", "Video")]), "invalid_services");

        store.ReportServices(browser.DeviceId, [browserService]);
        store.ReportServices(phone.DeviceId, [phoneService with { Enabled = null, ShowWhilePaused = null, Artwork = null, PlaybackProgress = null, ActionButtons = null }]);
        check(Service().SupportedFields.SequenceEqual(["display"]) && !Service().SyncEnabled, "legacy clients expose only display support");
        Select(null);
        var savedVersion = Preference().Version;
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "link-state.json")))!;
        saved["syncPolicies"]!["video"]!.AsObject().Remove("fields");
        File.WriteAllText(Path.Combine(directory, "link-state.json"), saved.ToJsonString());
        var migrated = new LinkStore(directory);
        check(migrated.Discover().Services.Single().SyncFields.SequenceEqual(["display"]) && migrated.Settings().Services["video"].Version == savedVersion,
            "preexisting opt-ins load as display-only without resetting conflict versions");
    }

    private sealed class UnusedDiscord : IDiscordRpc
    {
        public bool Connected => false;
        public Task ConnectAsync(CancellationToken token) => throw new InvalidOperationException("Settings sync must not connect to Discord.");
        public Task PublishAsync(PresenceIntent? intent, CancellationToken token) => throw new InvalidOperationException("Settings sync must not publish presence.");
        public void Disconnect() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
