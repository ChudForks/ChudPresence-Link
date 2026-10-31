using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using ChudPresence.Link;

internal static class ServerChecks
{
    public static async Task RunHttpAsync(string temporary, Action<bool, string> check)
    {
        var store = new LinkStore(Path.Combine(temporary, "server-http"));
        await using var presence = new PresenceCoordinator(new UnusedDiscord());
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var app = LinkServer.Create(store, presence, port);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        http.DefaultRequestHeaders.Add("Origin", "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        // Minimal API routing treats these as the same endpoint. They must also
        // consume the same five-request budget, including failed pairing attempts.
        foreach (var path in new[] { "/v1/pair", "/v1/PAIR", "/v1/pair/", "/V1/Pair", "/V1/PAIR/" })
        {
            using var response = await http.PostAsJsonAsync(path, new PairRequest("00000000", "Browser", "browser"));
            check(response.StatusCode == HttpStatusCode.Unauthorized, "pairing route variant consumes the shared budget: " + path);
        }
        foreach (var path in new[] { "/v1/pair", "/v1/PAIR", "/v1/pair/" })
        {
            using var response = await http.PostAsJsonAsync(path, new PairRequest("00000000", "Browser", "browser"));
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            check(response.StatusCode == HttpStatusCode.TooManyRequests && error.GetProperty("error").GetString() == "rate_limited" &&
                response.Headers.RetryAfter?.Delta == TimeSpan.FromSeconds(60), "pairing limit covers every route variant: " + path);
            check(response.Headers.GetValues("Access-Control-Expose-Headers").Contains("Retry-After"),
                "extension clients can read the pairing retry delay: " + path);
        }
        using var info = await http.GetAsync("/v1/info");
        check(info.IsSuccessStatusCode, "exhausting pairing guesses leaves ordinary discovery available");
        var discovery = await info.Content.ReadFromJsonAsync<JsonElement>();
        check(discovery.GetProperty("version").GetString() == typeof(LinkServer).Assembly.GetName().Version!.ToString(3),
            "API advertises the built application version");
        await app.StopAsync();
    }

    private sealed class UnusedDiscord : IDiscordRpc
    {
        public bool Connected => false;
        public Task ConnectAsync(CancellationToken token) => throw new InvalidOperationException();
        public Task PublishAsync(PresenceIntent? intent, CancellationToken token) => throw new InvalidOperationException();
        public void Disconnect() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
