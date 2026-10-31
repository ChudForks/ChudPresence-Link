using ChudPresence.Link;

internal static class CoreRegressionChecks
{
    public static async Task RunAsync(string temporary, Action<bool, string> check, Action<Action, string> fails)
    {
        var clock = new TestClock();
        var rpc = new RecordingDiscord();
        await using (var coordinator = new PresenceCoordinator(rpc, clock))
        {
            var lease = await coordinator.AcquireAsync("browser", null, default);
            var intent = new PresenceIntent("Video", "A playing video");
            await coordinator.UpdateAsync("browser", new(lease.LeaseId, 0, intent), default);
            await coordinator.TickAsync(default);
            check(coordinator.Status.Published && rpc.Writes.Count == 1, "presence regression starts with a published activity");

            rpc.Disconnect();
            clock.Advance(TimeSpan.FromSeconds(6));
            var renewed = await coordinator.AcquireAsync("browser", lease.LeaseId, default);
            check(renewed.LeaseId == lease.LeaseId && !coordinator.Status.Published,
                "renewing after Discord disconnects waits for activity to be republished");
            await coordinator.TickAsync(default);
            check(coordinator.Status.Published && rpc.Writes.Count == 2 && rpc.Writes.Last() == intent,
                "a lease renewal that reconnects Discord restores the current activity");

            rpc.Disconnect();
            clock.Advance(TimeSpan.FromSeconds(6));
            await coordinator.AcquireAsync("browser", lease.LeaseId, default);
            await coordinator.UpdateAsync("browser", new(lease.LeaseId, 1, intent), default);
            await coordinator.TickAsync(default);
            check(coordinator.Status.Published && rpc.Writes.Count == 3,
                "an identical heartbeat cannot suppress activity on a renewed Discord connection");
        }

        const string origin = "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var store = new LinkStore(Path.Combine(temporary, "validation-regressions"));
        check(!LinkStore.IsExtensionOrigin(origin + "\n"), "extension origins must match the entire value");
        var code = store.CreatePairingCode("browser");
        fails(() => store.Pair(new(code.Code, "Browser", "browser"), origin + "\n"), "invalid_origin");
        var browser = store.Pair(new(code.Code, "Browser", "browser"), origin);
        fails(() => store.ReportServices(browser.DeviceId, [new("video\n", "Video", "title")]), "invalid_services");
        fails(() => store.Update(new() { ["video\n"] = new(0, Display: "title") }), "invalid_settings");
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-28T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class RecordingDiscord : IDiscordRpc
    {
        public bool Connected { get; private set; }
        public List<PresenceIntent?> Writes { get; } = new();
        public Task ConnectAsync(CancellationToken cancellationToken) { Connected = true; return Task.CompletedTask; }
        public Task PublishAsync(PresenceIntent? intent, CancellationToken cancellationToken)
        {
            Connected = true;
            Writes.Add(intent);
            return Task.CompletedTask;
        }
        public void Disconnect() => Connected = false;
        public ValueTask DisposeAsync() { Disconnect(); return ValueTask.CompletedTask; }
    }
}
