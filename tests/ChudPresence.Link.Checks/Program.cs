using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ChudPresence.Link;

var temporary = Path.Combine(Path.GetTempPath(), "chudpresence-link-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
const string browserOrigin = "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++; Console.WriteLine("PASS " + name);
}
void Fails(Action action, string code)
{
    try { action(); throw new Exception("Expected " + code); }
    catch (LinkException e) { Check(e.Code == code, code); }
}
async Task FailsAsync(Func<Task> action, string code)
{
    try { await action(); throw new Exception("Expected " + code); }
    catch (LinkException e) { Check(e.Code == code, code); }
}
async Task<(string? Event, string? Id, string? Data)> ReadEvent(StreamReader reader)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    string? name = null, id = null, data = null;
    while (await reader.ReadLineAsync(timeout.Token) is { } line)
    {
        if (line.Length == 0)
        {
            if (name is not null) return (name, id, data);
            continue;
        }
        if (line.StartsWith("event: ", StringComparison.Ordinal)) name = line[7..];
        if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..];
        if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
    }
    throw new EndOfStreamException("Event stream closed before an event arrived.");
}
void CheckRevision((string? Event, string? Id, string? Data) frame, LinkStore expectedStore, long revision, string name)
{
    using var json = JsonDocument.Parse(frame.Data ?? "null");
    Check(frame.Event == "revision" && frame.Id == revision.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
        json.RootElement.GetProperty("serverId").GetString() == expectedStore.ServerId &&
        json.RootElement.GetProperty("revision").GetInt64() == revision, name);
}
async Task<bool> StreamClosed(StreamReader reader)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (await reader.ReadLineAsync(timeout.Token) is { } line)
        if (line.Length > 0 && !line.StartsWith(':')) return false;
    return true;
}

try
{
    await CoreRegressionChecks.RunAsync(temporary, Check, Fails);
    SettingsChecks.Run(temporary, Check, Fails);
    await SettingsChecks.RunHttpAsync(temporary, Check);
    await ServerChecks.RunHttpAsync(temporary, Check);
    var clock = new ManualClock();
    var store = new LinkStore(Path.Combine(temporary, "store"), clock);
    var pairing = store.CreatePairingCode("browser");
    Fails(() => store.Pair(new("000000000", "Browser", "browser"), browserOrigin), "invalid_code");
    Fails(() => store.Pair(new(pairing.Code, "Browser", "browser"), "https://example.com"), "invalid_origin");
    var browser = store.Pair(new(pairing.Code, "Browser", "browser"), browserOrigin);
    Check(store.Authenticate(browser.Token, browserOrigin).Id == browser.DeviceId, "pairing token authenticates");
    Fails(() => store.Pair(new(pairing.Code, "Second", "browser"), browserOrigin), "invalid_code");
    Fails(() => store.Authenticate(browser.Token, null), "unauthorized");
    Check(!File.ReadAllText(Path.Combine(temporary, "store", "link-state.json")).Contains(browser.Token), "only token hash is persisted");
    var mobileCode = store.CreatePairingCode("mobile");
    clock.Advance(TimeSpan.FromMinutes(6));
    Fails(() => store.Pair(new(mobileCode.Code, "Phone", "mobile"), null), "invalid_code");

    Check(store.Discover().Services.Length == 0 && store.Settings().Services.Count == 0, "new server contains no predefined services");
    Fails(() => store.Update(new() { ["youtube"] = new("creator", 0) }), "sync_not_enabled");
    ClientService[] browserServices = [new("youtube", "YouTube", "creator"), new("youtube-music", "YouTube Music", "title"),
        new("twitch", "Twitch", "title"), new("nebula-radio", "Nebula Radio", "creator"), new("browser-only", "Browser only", "app"),
        new("limited", "Limited", "creator", ["app", "creator"]), new("disjoint", "Disjoint", "creator", ["creator"])];
    store.ReportServices(browser.DeviceId, browserServices);
    Check(store.Discover(browser.DeviceId).Services.All(s => !s.Shared), "one client never creates a shared service");
    Fails(() => store.SetServiceSync("youtube", new(true, 0, browser.DeviceId)), "service_not_shared");
    var phoneCodeForStore = store.CreatePairingCode("mobile");
    var phoneForStore = store.Pair(new(phoneCodeForStore.Code, "Phone", "mobile"), null);
    ClientService[] phoneServices = [new("youtube", "YouTube on phone", "app"), new("youtube-music", "YouTube Music", "app"),
        new("twitch", "Twitch", "app"), new("nebula-radio", "Nebula Radio", "title"), new("phone-only", "Phone only", "title"),
        new("limited", "Limited", "title", ["app", "title"]), new("disjoint", "Disjoint", "title", ["title"])];
    var offers = store.ReportServices(phoneForStore.DeviceId, phoneServices);
    Check(offers.Services.Single(s => s.Id == "nebula-radio").Shared && offers.Services.All(s => !s.SyncEnabled), "arbitrary matching services are offered without automatic sync");
    Check(!offers.Services.Any(s => s.Id == "browser-only"), "client discovery contains only its own installed services");
    Check(offers.Services.Single(s => s.Id == "limited").DisplayChoices.SequenceEqual(["app"]), "discovery intersects reported display choices");
    Fails(() => store.SetServiceSync("disjoint", new(true, 0, browser.DeviceId)), "service_not_shared");
    Fails(() => store.SetServiceSync("limited", new(true, 0, browser.DeviceId)), "unsupported_display");
    store.SetServiceSync("limited", new(true, 0, InitialDisplay: "app"));
    Check(store.Settings().Services["limited"].Display == "app", "explicit common display can initialize sync when reported local choices differ");
    Fails(() => store.SetServiceSync("youtube", new(true, 0)), "source_required");
    store.SetServiceSync("youtube", new(true, 0, browser.DeviceId));
    store.SetServiceSync("youtube-music", new(true, 0, phoneForStore.DeviceId));
    store.SetServiceSync("twitch", new(true, 0, phoneForStore.DeviceId));
    store.SetServiceSync("nebula-radio", new(true, 0, InitialDisplay: "title"));
    Check(store.Settings(browser.DeviceId).Services["youtube"].Display == "creator" &&
        store.Settings(phoneForStore.DeviceId).Services["youtube-music"].Display == "app", "sync starts with the selected browser or phone choice");
    Fails(() => store.SetServiceSync("youtube", new(false, 0)), "sync_conflict");
    Fails(() => store.ReportServices(browser.DeviceId, [new("duplicate", "A", "app"), new("duplicate", "B", "app")]), "invalid_services");
    Check(store.Discover(browser.DeviceId).Services.Any(s => s.Id == "youtube"), "invalid inventory leaves previous report intact");
    var musicVersion = store.Settings().Services["youtube-music"].Version;
    var initial = store.Update(new() { ["youtube-music"] = new("title", musicVersion) });
    Check(initial.Services["youtube"].Display == "creator" && initial.Services["youtube-music"].Display == "title", "shared service preferences update independently");
    var twitchVersion = initial.Services["twitch"].Version;
    var conflict = store.Update(new() { ["youtube"] = new("app", 0), ["twitch"] = new("creator", twitchVersion) });
    Check(conflict.Conflicts.SequenceEqual(["youtube"]) && conflict.Services["youtube"].Display == "creator" && conflict.Services.ContainsKey("twitch"), "stale change cannot overwrite; unrelated change survives");
    var current = store.Update(new() { ["youtube"] = new("title", initial.Services["youtube"].Version) });
    Check(current.Services["youtube"].Display == "title", "versioned edit succeeds");
    var retry = store.Update(new() { ["youtube"] = new("title", initial.Services["youtube"].Version) });
    Check(retry.Conflicts.Length == 0 && retry.Revision == current.Revision, "lost-response retry is idempotent");
    Fails(() => store.Update(new() { ["hulu"] = new("app", 0), ["bad/id"] = new("creator", 0) }), "invalid_settings");
    Check(!store.Settings().Services.ContainsKey("hulu"), "invalid batch makes no partial writes");
    Fails(() => store.Update(new() { ["hulu"] = new("season_episode", 0) }), "invalid_settings");
    var beforeRejectedBatch = store.Settings().Services["youtube"];
    Fails(() => store.Update(new() { ["youtube"] = new("app", beforeRejectedBatch.Version), ["browser-only"] = new("app", 0) }), "sync_not_enabled");
    Check(store.Settings().Services["youtube"] == beforeRejectedBatch, "a nonshared service rejects the whole batch before any preference changes");
    Fails(() => store.Update(new() { ["limited"] = new("creator", store.Settings().Services["limited"].Version) }), "unsupported_display");
    var nebula = store.Discover().Services.Single(s => s.Id == "nebula-radio");
    store.SetServiceSync("nebula-radio", new(false, nebula.SyncVersion), phoneForStore.DeviceId);
    Check(!store.Settings().Services.ContainsKey("nebula-radio"), "disabling a shared service removes it from sync snapshots");
    Fails(() => store.Update(new() { ["nebula-radio"] = new("creator", 0) }), "sync_not_enabled");
    var restored = new LinkStore(Path.Combine(temporary, "store"));
    Check(restored.ServerId == store.ServerId && restored.Settings().Revision == store.Settings().Revision &&
        restored.Discover().Services.Single(s => s.Id == "youtube").SyncEnabled &&
        restored.Authenticate(browser.Token, browserOrigin).Id == browser.DeviceId, "identity, inventories, opt-ins, preferences, and versions survive restart");
    store.ReportServices(phoneForStore.DeviceId, phoneServices.Select(s => s.Id == "youtube" ? s with { Display = "app", DisplayChoices = ["app"] } : s).ToArray());
    Check(!store.Discover().Services.Single(s => s.Id == "youtube").SyncEnabled && !store.Settings(browser.DeviceId).Services.ContainsKey("youtube"),
        "narrowed client capabilities suspend an incompatible saved preference");
    store.ReportServices(phoneForStore.DeviceId, phoneServices);
    Check(store.Settings(browser.DeviceId).Services["youtube"] == beforeRejectedBatch, "inventory updates never overwrite the saved shared preference");
    store.ReportServices(phoneForStore.DeviceId, []);
    Check(store.Discover().Services.All(s => !s.Shared) && store.Settings().Services.Count == 0, "removing phone services immediately suspends their sync");
    Fails(() => store.Update(new() { ["youtube"] = new("creator", 0) }), "sync_not_enabled");
    store.ReportServices(phoneForStore.DeviceId, phoneServices);
    Check(store.Discover().Services.Single(s => s.Id == "youtube").SyncEnabled, "previously selected sync resumes when its service returns");
    var otherBrowserCode = store.CreatePairingCode("browser");
    var otherBrowser = store.Pair(new(otherBrowserCode.Code, "Other browser", "browser"), browserOrigin);
    store.ReportServices(otherBrowser.DeviceId, [new("browser-only", "Browser only", "app")]);
    Check(!store.Discover(otherBrowser.DeviceId).Services.Single().Shared, "two browsers alone do not create a browser-phone match");
    Check(store.Settings(otherBrowser.DeviceId).Services.Count == 0, "a client cannot read unrelated shared settings");
    Fails(() => store.SetServiceSync("youtube", new(false, store.Discover().Services.Single(s => s.Id == "youtube").SyncVersion), otherBrowser.DeviceId), "service_not_reported");
    store.Revoke(phoneForStore.DeviceId);
    Check(store.Discover().Services.All(s => !s.Shared) && store.Settings().Services.Count == 0, "revocation removes the phone inventory and sync eligibility");
    store.Revoke(browser.DeviceId);
    Fails(() => store.Authenticate(browser.Token, browserOrigin), "unauthorized");
    var legacyPath = Path.Combine(temporary, "legacy"); Directory.CreateDirectory(legacyPath);
    File.WriteAllText(Path.Combine(legacyPath, "link-state.json"), "{\"serverId\":\"legacy\",\"revision\":1,\"services\":{\"youtube\":{\"display\":\"creator\",\"version\":1}},\"devices\":{}}");
    var legacy = new LinkStore(legacyPath);
    Check(legacy.ServerId == "legacy" && legacy.Discover().Services.Length == 0 && legacy.Settings().Services.Count == 0,
        "old saved preferences do not imply reported services or consent to sync");

    var watchStore = new LinkStore(Path.Combine(temporary, "watch"));
    var watchCode = watchStore.CreatePairingCode("mobile");
    var watchDevice = watchStore.Pair(new(watchCode.Code, "Watch phone", "mobile"), null);
    var watchedRevision = (await watchStore.WaitForRevisionAsync(watchDevice.DeviceId, null))!.Value;
    Check(watchedRevision == 0, "revision watch returns an initial snapshot immediately");
    for (var i = 0; i < 20; i++)
    {
        var previousRevision = watchedRevision;
        var waiting = Task.Run(async () => await watchStore.WaitForRevisionAsync(watchDevice.DeviceId, previousRevision));
        watchStore.ReportServices(watchDevice.DeviceId, [new("youtube", "YouTube", "app")]);
        watchedRevision = (await waiting.WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
        Check(watchedRevision == previousRevision + 1, "revision watch never loses a concurrent commit " + i);
    }
    using (var stopWatch = new CancellationTokenSource())
    {
        var waiting = watchStore.WaitForRevisionAsync(watchDevice.DeviceId, watchedRevision, stopWatch.Token).AsTask();
        Check(!waiting.IsCompleted, "revision watch waits without holding the store lock");
        stopWatch.Cancel();
        var canceled = false;
        try { await waiting; } catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "revision watch honors cancellation");
    }
    var beforeCoalescing = watchedRevision;
    watchStore.ReportServices(watchDevice.DeviceId, [new("youtube", "YouTube", "app")]);
    watchStore.ReportServices(watchDevice.DeviceId, [new("youtube", "YouTube", "title")]);
    watchedRevision = (await watchStore.WaitForRevisionAsync(watchDevice.DeviceId, beforeCoalescing))!.Value;
    Check(watchedRevision == beforeCoalescing + 2, "revision watch coalesces changes into the latest snapshot");
    var peerCode = watchStore.CreatePairingCode("mobile");
    var peerDevice = watchStore.Pair(new(peerCode.Code, "Peer phone", "mobile"), null);
    var revokedWatch = watchStore.WaitForRevisionAsync(watchDevice.DeviceId, watchedRevision).AsTask();
    var peerWatch = watchStore.WaitForRevisionAsync(peerDevice.DeviceId, watchedRevision).AsTask();
    watchStore.Revoke(watchDevice.DeviceId);
    Check(await revokedWatch is null && await peerWatch == watchedRevision + 1,
        "revocation wakes all watchers and revalidates only the removed device");

    foreach (var address in new[] { "127.0.0.1", "::1", "::ffff:127.0.0.1", "10.2.3.4", "172.16.3.4", "192.168.1.4", "fd00::1", "fe80::1" })
        Check(LocalNetwork.IsLocal(IPAddress.Parse(address)), "allows local " + address);
    foreach (var address in new[] { "8.8.8.8", "172.15.0.1", "172.32.0.1", "0.0.0.0", "2001:4860:4860::8888" })
        Check(!LocalNetwork.IsLocal(IPAddress.Parse(address)), "rejects external " + address);
    Check(!LocalNetwork.IsOurHost("attacker.example"), "rejects DNS rebinding hostnames");

    var fake = new FakeDiscord();
    await using (var coordinator = new PresenceCoordinator(fake, clock))
    {
        var lease = await coordinator.AcquireAsync("browser1", null, default);
        Check(fake.Writes.Count == 0, "acquiring lease does not publish before headless cleanup");
        await FailsAsync(() => coordinator.AcquireAsync("browser2", null, default), "publisher_busy");
        await FailsAsync(() => coordinator.AcquireAsync("browser1", null, default), "publisher_busy");
        var intent = new PresenceIntent("YouTube", "A video", State: "Asmongold TV", StatusDisplayType: "state");
        await coordinator.UpdateAsync("browser1", new(lease.LeaseId, 0, intent), default);
        await coordinator.TickAsync(default);
        Check(fake.Writes.Last() == intent && coordinator.Status.Published, "accepted presence publishes through desktop");
        await FailsAsync(() => coordinator.UpdateAsync("browser1", new(lease.LeaseId, 0, null), default), "stale_sequence");
        await coordinator.UpdateAsync("browser1", new(lease.LeaseId, 1, intent), default);
        await coordinator.TickAsync(default);
        Check(fake.Writes.Count == 1 && coordinator.Status.Published, "heartbeat retains published status without duplicate write");
        clock.Advance(TimeSpan.FromSeconds(10)); fake.Disconnect();
        await coordinator.TickAsync(default);
        Check(fake.Writes.Count == 2 && coordinator.Status.Published, "Discord reconnect republishes current intent");
        await coordinator.UpdateAsync("browser1", new(lease.LeaseId, 2, intent with { Details = "Another video" }), default);
        fake.DropNextWrite = true; clock.Advance(TimeSpan.FromSeconds(6));
        await coordinator.TickAsync(default);
        Check(!coordinator.Status.Published, "IPC disposal during a write becomes recoverable disconnected status");
        clock.Advance(TimeSpan.FromSeconds(6)); await coordinator.TickAsync(default);
        Check(coordinator.Status.Published && fake.Writes.Last()?.Details == "Another video", "next tick recovers from a disposed IPC stream");
        clock.Advance(TimeSpan.FromSeconds(31));
        await coordinator.TickAsync(default);
        Check(fake.Writes.Last() is null && coordinator.Status.DeviceId is null && !fake.Connected, "expired lease clears presence and closes IPC");
        await FailsAsync(() => coordinator.UpdateAsync("browser1", new(lease.LeaseId, 2, intent), default), "lease_lost");
        var next = await coordinator.AcquireAsync("browser2", null, default);
        await coordinator.UpdateAsync("browser2", new(next.LeaseId, 0, intent), default);
        clock.Advance(TimeSpan.FromSeconds(6)); await coordinator.TickAsync(default);
        await coordinator.RevokeAsync("browser2", default);
        Check(fake.Writes.Last() is null && coordinator.Status.DeviceId is null, "device revocation clears its presence immediately");
        fake.Unavailable = true;
        await FailsAsync(() => coordinator.AcquireAsync("browser3", null, default), "discord_unavailable");
    }
    var payload = PresencePayload.Build(new("YouTube", "Video", "streaming", "Asmongold TV", "state", Timestamps: new(1000, 1050)))!;
    Check(payload["type"]!.GetValue<int>() == 3 && payload["status_display_type"]!.GetValue<int>() == 1 &&
        payload["timestamps"]!["start"]!.GetValue<long>() == 1000, "RPC mapping preserves compact creator and Unix-second timers");
    Fails(() => PresencePayload.Build(new("YouTube", "Video", DetailsUrl: "http://example.com")), "invalid_presence");
    Fails(() => PresencePayload.Build(new("YouTube", new string('界', 50))), "invalid_presence");

    var apiStore = new LinkStore(Path.Combine(temporary, "api"));
    var apiFake = new FakeDiscord();
    await using (var apiPresence = new PresenceCoordinator(apiFake))
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        await using var app = LinkServer.Create(apiStore, apiPresence, port, TimeSpan.FromMilliseconds(200));
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var info = await http.GetFromJsonAsync<JsonElement>("/v1/info");
        Check(info.GetProperty("protocolVersion").GetInt32() == 1 && info.GetProperty("version").GetString() == typeof(LinkServer).Assembly.GetName().Version!.ToString(3) &&
            info.GetProperty("capabilities").EnumerateArray().Any(c => c.GetString() == "settings-change-events") &&
            info.GetProperty("capabilities").EnumerateArray().Any(c => c.GetString() == "selective-settings-sync") &&
            info.GetProperty("settingFields").EnumerateArray().Select(c => c.GetString()).SequenceEqual(ServiceValues.AllFields),
            "API advertises protocol, events, and selective service settings");
        Check((await http.GetAsync("/v1/settings")).StatusCode == HttpStatusCode.Unauthorized, "API protects settings");
        using (var invalidEvents = new HttpRequestMessage(HttpMethod.Get, "/v1/events"))
        {
            invalidEvents.Headers.Authorization = new("Bearer", new string('0', 64));
            using var denied = await http.SendAsync(invalidEvents, HttpCompletionOption.ResponseHeadersRead);
            Check(denied.StatusCode == HttpStatusCode.Unauthorized, "event stream rejects invalid bearer tokens");
        }
        http.DefaultRequestHeaders.Add("Origin", "https://example.com");
        Check((await http.GetAsync("/v1/info")).StatusCode == HttpStatusCode.Forbidden, "API denies website origins");
        http.DefaultRequestHeaders.Remove("Origin"); http.DefaultRequestHeaders.Add("Origin", browserOrigin);
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/v1/settings");
        preflight.Headers.Add("Access-Control-Request-Private-Network", "true");
        var cors = await http.SendAsync(preflight);
        Check(cors.StatusCode == HttpStatusCode.NoContent && cors.Headers.GetValues("Access-Control-Allow-Origin").Single() == browserOrigin &&
            cors.Headers.GetValues("Access-Control-Allow-Private-Network").Single() == "true", "extension CORS and private-network preflight");
        var code = apiStore.CreatePairingCode("browser");
        var pairedResponse = await http.PostAsJsonAsync("/v1/pair", new PairRequest(code.Code, "Integration browser", "browser"));
        pairedResponse.EnsureSuccessStatusCode();
        var paired = (await pairedResponse.Content.ReadFromJsonAsync<PairResponse>())!;
        http.DefaultRequestHeaders.Authorization = new("Bearer", paired.Token);
        var reported = await http.PutAsJsonAsync("/v1/services", new ServicesRequest([new("youtube", "YouTube", "app"), new("browser-only", "Browser only", "app")]));
        reported.EnsureSuccessStatusCode();
        Check((await reported.Content.ReadFromJsonAsync<ServicesSnapshot>())!.Services.All(s => !s.Shared), "HTTP inventory report has no invented matches");
        using var mobile = new HttpClient { BaseAddress = http.BaseAddress };
        var phoneCode = apiStore.CreatePairingCode("mobile");
        var phoneResponse = await mobile.PostAsJsonAsync("/v1/pair", new PairRequest(phoneCode.Code, "Phone", "mobile"));
        phoneResponse.EnsureSuccessStatusCode();
        var phone = (await phoneResponse.Content.ReadFromJsonAsync<PairResponse>())!;
        mobile.DefaultRequestHeaders.Authorization = new("Bearer", phone.Token);
        using var phoneEventRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/events");
        using var phoneEventResponse = await mobile.SendAsync(phoneEventRequest, HttpCompletionOption.ResponseHeadersRead);
        phoneEventResponse.EnsureSuccessStatusCode();
        Check(phoneEventResponse.Content.Headers.ContentType?.MediaType == "text/event-stream", "event endpoint streams SSE");
        using var phoneEvents = new StreamReader(await phoneEventResponse.Content.ReadAsStreamAsync());
        CheckRevision(await ReadEvent(phoneEvents), apiStore, apiStore.Settings().Revision, "stream begins with current server identity and revision");
        using (var heartbeatTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            Check(await phoneEvents.ReadLineAsync(heartbeatTimeout.Token) == ": heartbeat" &&
                await phoneEvents.ReadLineAsync(heartbeatTimeout.Token) == "", "idle event streams send a heartbeat comment");
        }
        var mobileReport = await mobile.PutAsJsonAsync("/v1/services", new ServicesRequest([new("youtube", "YouTube on phone", "title"), new("phone-only", "Phone only", "title")]));
        mobileReport.EnsureSuccessStatusCode();
        CheckRevision(await ReadEvent(phoneEvents), apiStore, apiStore.Settings().Revision, "inventory changes notify the event stream");
        var shared = (await http.GetFromJsonAsync<ServicesSnapshot>("/v1/services"))!;
        Check(shared.Services.Single(s => s.Id == "youtube").Shared && !shared.Services.Any(s => s.Id == "phone-only"), "HTTP discovery offers the actual installed overlap");
        Check((await http.GetFromJsonAsync<SettingsSnapshot>("/v1/settings"))!.Services.Count == 0, "matching reports never opt users into sync");
        var enabled = await http.PutAsJsonAsync("/v1/services/youtube/sync", new ServiceSyncRequest(true, 0, paired.DeviceId));
        enabled.EnsureSuccessStatusCode();
        CheckRevision(await ReadEvent(phoneEvents), apiStore, apiStore.Settings().Revision, "sync opt-in notifies the event stream");
        Check((await enabled.Content.ReadFromJsonAsync<ServicesSnapshot>())!.Services.Single(s => s.Id == "youtube").SyncEnabled,
            "HTTP opt-in enables only the selected shared service");
        var enabledSettings = (await http.GetFromJsonAsync<SettingsSnapshot>("/v1/settings"))!;
        apiStore.Update(new() { ["youtube"] = new("title", enabledSettings.Services["youtube"].Version) });
        CheckRevision(await ReadEvent(phoneEvents), apiStore, apiStore.Settings().Revision, "native store edits notify the event stream");
        enabledSettings = (await http.GetFromJsonAsync<SettingsSnapshot>("/v1/settings"))!;
        using var update = new HttpRequestMessage(HttpMethod.Patch, "/v1/settings")
        { Content = JsonContent.Create(new SettingsRequest(new() { ["youtube"] = new("creator", enabledSettings.Services["youtube"].Version) })) };
        var saved = await http.SendAsync(update); saved.EnsureSuccessStatusCode();
        Check((await saved.Content.ReadFromJsonAsync<SettingsResult>())!.Services["youtube"].Display == "creator", "real HTTP pairing and settings edit");
        CheckRevision(await ReadEvent(phoneEvents), apiStore, apiStore.Settings().Revision, "HTTP settings edits notify the event stream");
        using var browserEventRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/events");
        using var browserEventResponse = await http.SendAsync(browserEventRequest, HttpCompletionOption.ResponseHeadersRead);
        browserEventResponse.EnsureSuccessStatusCode();
        using var browserEvents = new StreamReader(await browserEventResponse.Content.ReadAsStreamAsync());
        CheckRevision(await ReadEvent(browserEvents), apiStore, apiStore.Settings().Revision, "another device receives its own initial event");
        using var replacementRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/events");
        using var replacementResponse = await mobile.SendAsync(replacementRequest, HttpCompletionOption.ResponseHeadersRead);
        replacementResponse.EnsureSuccessStatusCode();
        using var replacementEvents = new StreamReader(await replacementResponse.Content.ReadAsStreamAsync());
        CheckRevision(await ReadEvent(replacementEvents), apiStore, apiStore.Settings().Revision, "replacement stream starts at the current revision");
        Check(await StreamClosed(phoneEvents), "a new stream closes only the same device's previous stream");
        apiStore.ReportServices(paired.DeviceId, [new("youtube", "YouTube", "app"), new("browser-only", "Browser only", "app")]);
        CheckRevision(await ReadEvent(replacementEvents), apiStore, apiStore.Settings().Revision, "replacement stream receives subsequent events");
        CheckRevision(await ReadEvent(browserEvents), apiStore, apiStore.Settings().Revision, "replacing the phone stream leaves the browser stream active");
        replacementResponse.Dispose();
        using var resumedRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/events");
        using var resumedResponse = await mobile.SendAsync(resumedRequest, HttpCompletionOption.ResponseHeadersRead);
        resumedResponse.EnsureSuccessStatusCode();
        using var resumedEvents = new StreamReader(await resumedResponse.Content.ReadAsStreamAsync());
        CheckRevision(await ReadEvent(resumedEvents), apiStore, apiStore.Settings().Revision, "a disconnected client can immediately reconnect");
        var reservation = await http.PostAsJsonAsync("/v1/presence/lease", new LeaseRequest()); reservation.EnsureSuccessStatusCode();
        var ownership = (await reservation.Content.ReadFromJsonAsync<LeaseView>())!;
        var published = await http.PutAsJsonAsync("/v1/presence", new PresenceRequest(ownership.LeaseId, 0,
            new("YouTube", "Watching a video", State: "Asmongold TV", StatusDisplayType: "state")));
        published.EnsureSuccessStatusCode(); await apiPresence.TickAsync(default);
        Check(apiFake.Writes.Last()?.State == "Asmongold TV", "real HTTP browser handoff reaches RPC transport");
        var cleared = await http.DeleteAsync("/v1/presence/lease/" + ownership.LeaseId);
        Check(cleared.StatusCode == HttpStatusCode.NoContent && apiFake.Writes.Last() is null, "HTTP release acknowledges desktop clear");
        var invalidJson = await http.PutAsync("/v1/presence", new StringContent("{broken", Encoding.UTF8, "application/json"));
        Check(invalidJson.StatusCode == HttpStatusCode.BadRequest, "malformed JSON gets bounded client error");
        http.DefaultRequestHeaders.Remove("Origin"); http.DefaultRequestHeaders.Authorization = null;
        http.DefaultRequestHeaders.Authorization = new("Bearer", phone.Token);
        Check((await http.GetFromJsonAsync<SettingsSnapshot>("/v1/settings"))!.Services["youtube"].Display == "creator", "mobile reads same enabled shared settings");
        Check((await http.PostAsJsonAsync("/v1/presence/lease", new LeaseRequest())).StatusCode == HttpStatusCode.Forbidden, "mobile cannot take desktop publishing");
        Check((await http.DeleteAsync("/v1/device")).StatusCode == HttpStatusCode.NoContent &&
            (await http.GetAsync("/v1/settings")).StatusCode == HttpStatusCode.Unauthorized, "unpair immediately revokes API access");
        var revokedEvent = await ReadEvent(resumedEvents);
        Check(revokedEvent.Event == "revoked" && revokedEvent.Data == "{}" && await StreamClosed(resumedEvents),
            "revocation promptly notifies and closes the removed device's stream");
        CheckRevision(await ReadEvent(browserEvents), apiStore, apiStore.Settings().Revision, "other device's stream observes revoked inventory");
        browserEventResponse.Dispose();
        http.DefaultRequestHeaders.Authorization = null;
        for (var i = 0; i < 4; i++) await http.PostAsJsonAsync("/v1/pair", new PairRequest("invalid", "Phone", "mobile"));
        Check((await http.PostAsJsonAsync("/v1/pair", new PairRequest("invalid", "Phone", "mobile"))).StatusCode == HttpStatusCode.TooManyRequests,
            "pairing guesses are rate limited");
        http.DefaultRequestHeaders.Host = "attacker.example";
        Check((await http.GetAsync("/v1/info")).StatusCode == HttpStatusCode.Forbidden, "HTTP rejects foreign Host headers");
        await app.StopAsync();
    }

    if (OperatingSystem.IsWindows())
    {
        var prefix = "chudpresence-check-" + Guid.NewGuid().ToString("N") + "-";
        using var pipe = new NamedPipeServerStream(prefix + "0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        async Task<(int Opcode, JsonElement Payload)> Read()
        {
            var header = new byte[8]; await pipe.ReadExactlyAsync(header, timeout.Token);
            var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4))]; await pipe.ReadExactlyAsync(body, timeout.Token);
            return (BinaryPrimitives.ReadInt32LittleEndian(header), JsonDocument.Parse(body).RootElement.Clone());
        }
        async Task Write(int opcode, object payload)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(payload); var frame = new byte[8 + body.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, opcode); BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length); body.CopyTo(frame, 8);
            // Fragment both header and body to exercise byte-stream framing.
            await pipe.WriteAsync(frame.AsMemory(0, 3), timeout.Token); await pipe.FlushAsync(timeout.Token);
            await pipe.WriteAsync(frame.AsMemory(3), timeout.Token); await pipe.FlushAsync(timeout.Token);
        }
        async Task RunPeerAsync()
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            var handshake = await Read();
            Check(handshake.Opcode == 0 && handshake.Payload.GetProperty("client_id").GetString() == "1549066134706323548", "real named-pipe Discord handshake");
            await Write(1, new { evt = "READY", data = new { v = 1 } });
            var frame = await Read();
            Check(frame.Opcode == 1 && frame.Payload.GetProperty("cmd").GetString() == "SET_ACTIVITY" &&
                frame.Payload.GetProperty("args").GetProperty("activity").GetProperty("state").GetString() == "Asmongold TV", "real framed SET_ACTIVITY payload");
            // Keep SET_ACTIVITY pending until its concurrent ping is answered. Sending the ping
            // after READY allowed it to finish before PublishAsync even began on faster machines.
            await Write(3, new { ping = true });
            var pong = await Read();
            Check(pong.Opcode == 4 && pong.Payload.GetProperty("ping").GetBoolean(),
                "Discord ping is answered while requests are active");
            await Write(1, new { nonce = frame.Payload.GetProperty("nonce").GetString(), evt = (string?)null, data = new { } });
        }
        var peer = RunPeerAsync();
        await using var rpc = new DiscordRpc(pipePrefix: prefix);
        await rpc.ConnectAsync(timeout.Token);
        await Task.WhenAll(peer,
            rpc.PublishAsync(new("YouTube", "Video", State: "Asmongold TV", StatusDisplayType: "state"), timeout.Token));
    }
    Console.WriteLine($"\nAll {checks} checks passed.");
}
finally { Directory.Delete(temporary, recursive: true); }

sealed class ManualClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.Parse("2026-10-26T12:00:00Z");
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan by) => now += by;
}
sealed class FakeDiscord : IDiscordRpc
{
    public bool Connected { get; private set; }
    public bool Unavailable { get; set; }
    public bool DropNextWrite { get; set; }
    public List<PresenceIntent?> Writes { get; } = new();
    public Task ConnectAsync(CancellationToken token)
    {
        if (Unavailable) throw new LinkException(503, "discord_unavailable", "Unavailable");
        Connected = true; return Task.CompletedTask;
    }
    public async Task PublishAsync(PresenceIntent? intent, CancellationToken token)
    {
        await ConnectAsync(token);
        if (DropNextWrite) { DropNextWrite = false; Disconnect(); throw new ObjectDisposedException("test pipe"); }
        Writes.Add(intent);
    }
    public void Disconnect() => Connected = false;
    public ValueTask DisposeAsync() { Disconnect(); return ValueTask.CompletedTask; }
}
