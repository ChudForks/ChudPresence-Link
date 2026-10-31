using System.Net;
using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChudPresence.Link;

public static class LinkServer
{
    public const int DefaultPort = 17879;
    public static WebApplication Create(LinkStore store, PresenceCoordinator presence, int port = DefaultPort, TimeSpan? eventHeartbeatInterval = null)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var heartbeatInterval = eventHeartbeatInterval ?? TimeSpan.FromSeconds(120);
        if (heartbeatInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(eventHeartbeatInterval));
        var eventStreams = new Dictionary<string, CancellationTokenSource>();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [], EnvironmentName = Environments.Production, ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.RespectRequiredConstructorParameters = true;
            options.SerializerOptions.MaxDepth = 16;
        });
        builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 32_768;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            k.Limits.MaxConcurrentConnections = 100;
            k.ListenAnyIP(port);
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                // Routing accepts casing changes and a trailing slash. All variants must
                // share the pairing budget rather than the ordinary request budget.
                var pairing = string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/v1/pair", StringComparison.OrdinalIgnoreCase);
                return RateLimitPartition.GetFixedWindowLimiter($"{context.Connection.RemoteIpAddress}|{pairing}", _ => new()
                {
                    PermitLimit = pairing ? 5 : 240, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
            options.OnRejected = async (context, token) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(new { error = "rate_limited", message = "Try again in one minute." }, token);
            };
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var origin = Origin(context);
            var remote = context.Connection.RemoteIpAddress;
            if (remote is null || !LocalNetwork.IsLocal(remote) || !LocalNetwork.IsOurHost(context.Request.Host.Host))
            { await Error(context, 403, "local_network_only", "ChudPresence Link accepts local-network connections addressed to this computer only."); return; }
            if (origin is not null)
            {
                if (!LinkStore.IsExtensionOrigin(origin))
                { await Error(context, 403, "origin_denied", "Only paired browser extensions may access this API from a browser."); return; }
                context.Response.Headers.AccessControlAllowOrigin = origin;
                context.Response.Headers.Vary = "Origin";
                context.Response.Headers.AccessControlAllowMethods = "GET, POST, PATCH, PUT, DELETE, OPTIONS";
                context.Response.Headers.AccessControlAllowHeaders = "Authorization, Content-Type";
                context.Response.Headers.AccessControlExposeHeaders = "Retry-After";
                context.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
                context.Response.Headers.AccessControlMaxAge = "600";
            }
            if (context.Request.Method == "OPTIONS") { context.Response.StatusCode = 204; return; }
            try { await next(context); }
            catch (LinkException e) { await Error(context, e.Status, e.Code, e.Message); }
            catch (BadHttpRequestException e) { await Error(context, e.StatusCode, "invalid_request", "Send a valid JSON request matching API version 1."); }
            catch (JsonException) { await Error(context, 400, "invalid_json", "Send a valid JSON request matching API version 1."); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception)
            { await Error(context, 500, "server_error", "The request could not be completed. Check that Link can write its local data folder."); }
        });
        app.UseRateLimiter();

        app.MapGet("/v1/info", () => new
        {
            name = "ChudPresence Link", version = typeof(LinkServer).Assembly.GetName().Version!.ToString(3), protocolVersion = 1, serverId = store.ServerId,
            capabilities = new[] { "service-discovery", "service-sync-opt-in", "service-display-sync", "service-settings-sync", "selective-settings-sync", "settings-change-events", "desktop-presence-lease" },
            displayChoices = new[] { "app", "creator", "title" },
            settingFields = ServiceValues.AllFields,
            presenceLeaseSeconds = PresenceCoordinator.LeaseSeconds
        });
        app.MapPost("/v1/pair", (PairRequest request, HttpContext context) => store.Pair(request, Origin(context)));
        app.MapGet("/v1/services", (HttpContext context) => store.Discover(Authenticate(context, store).Id));
        app.MapPut("/v1/services", (ServicesRequest request, HttpContext context) =>
            store.ReportServices(Authenticate(context, store).Id, request.Services));
        app.MapPut("/v1/services/{serviceId}/sync", (string serviceId, ServiceSyncRequest request, HttpContext context) =>
            store.SetServiceSync(serviceId, request, Authenticate(context, store).Id));
        app.MapGet("/v1/settings", (HttpContext context) => store.Settings(Authenticate(context, store).Id));
        app.MapPatch("/v1/settings", (SettingsRequest request, HttpContext context) =>
        { return store.Update(request.Changes, Authenticate(context, store).Id); });
        app.MapGet("/v1/events", async (HttpContext context) =>
        {
            var device = Authenticate(context, store);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            lock (eventStreams)
            {
                if (eventStreams.Remove(device.Id, out var previous)) previous.Cancel();
                eventStreams.Add(device.Id, cancellation);
            }
            try { await StreamEvents(context, store, device.Id, heartbeatInterval, cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (IOException) { } // A client can disconnect while a frame is being written.
            finally
            {
                lock (eventStreams)
                    if (eventStreams.TryGetValue(device.Id, out var current) && ReferenceEquals(current, cancellation))
                        eventStreams.Remove(device.Id);
            }
        });
        app.MapGet("/v1/status", (HttpContext context) =>
        { Authenticate(context, store); return presence.Status; });
        app.MapDelete("/v1/device", async (HttpContext context) =>
        {
            var device = Authenticate(context, store);
            store.Revoke(device.Id);
            await presence.RevokeAsync(device.Id, context.RequestAborted);
            return Results.NoContent();
        });
        app.MapPost("/v1/presence/lease", async (LeaseRequest request, HttpContext context) =>
        {
            var device = Publisher(context, store);
            return await presence.AcquireAsync(device.Id, request.LeaseId, context.RequestAborted);
        });
        app.MapPut("/v1/presence", async (PresenceRequest request, HttpContext context) =>
        {
            var device = Publisher(context, store);
            return await presence.UpdateAsync(device.Id, request, context.RequestAborted);
        });
        app.MapDelete("/v1/presence/lease/{leaseId}", async (string leaseId, HttpContext context) =>
        {
            var device = Publisher(context, store);
            await presence.ReleaseAsync(device.Id, leaseId, context.RequestAborted);
            return Results.NoContent();
        });
        return app;
    }

    private static async Task StreamEvents(HttpContext context, LinkStore store, string deviceId,
        TimeSpan heartbeatInterval, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "text/event-stream";
        long? lastRevision = null;
        while (true)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(heartbeatInterval);
            try
            {
                var revision = await store.WaitForRevisionAsync(deviceId, lastRevision, wait.Token);
                if (revision is null)
                {
                    await context.Response.WriteAsync("event: revoked\ndata: {}\n\n", cancellationToken);
                    await context.Response.Body.FlushAsync(cancellationToken);
                    return;
                }
                lastRevision = revision;
                var id = revision.Value.ToString(CultureInfo.InvariantCulture);
                var data = JsonSerializer.Serialize(new { serverId = store.ServerId, revision = revision.Value });
                await context.Response.WriteAsync($"id: {id}\nevent: revision\ndata: {data}\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && wait.IsCancellationRequested)
            {
                await context.Response.WriteAsync(": heartbeat\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
    }

    private static Device Authenticate(HttpContext context, LinkStore store)
    {
        var auth = context.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.Ordinal)) throw new LinkException(401, "unauthorized", "Use the token returned by pairing.");
        return store.Authenticate(auth[7..], Origin(context));
    }
    private static Device Publisher(HttpContext context, LinkStore store)
    {
        var device = Authenticate(context, store);
        if (device.Kind != "browser" || !IPAddress.IsLoopback(context.Connection.RemoteIpAddress!))
            throw new LinkException(403, "desktop_browser_only", "Desktop publishing is available only to a paired browser on this computer.");
        return device;
    }
    private static string? Origin(HttpContext context) => context.Request.Headers.ContainsKey("Origin") ? context.Request.Headers.Origin.ToString() : null;
    private static Task Error(HttpContext context, int status, string error, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error, message });
    }
}
