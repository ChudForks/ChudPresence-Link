namespace ChudPresence.Link;

public sealed class PresenceCoordinator(IDiscordRpc rpc, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1);
    private string? deviceId;
    private string? leaseId;
    private DateTimeOffset expiresAt;
    private long sequence = -1;
    private PresenceIntent? intent;
    private string? sentPayload;
    private DateTimeOffset lastSent = DateTimeOffset.MinValue;
    private bool pending;
    private volatile PresenceStatus status = new(false, "Ready for a browser to connect.", null, null, null, false);
    public const int LeaseSeconds = 30;
    public PresenceStatus Status => status with { DiscordConnected = rpc.Connected, Published = status.Published && rpc.Connected };

    public async Task<LeaseView> AcquireAsync(string device, string? existingLease, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            await ExpireAsync(token);
            if (leaseId is not null && (deviceId != device || existingLease != leaseId))
                throw new LinkException(409, "publisher_busy", "Another browser owns desktop publishing. Release its lease or wait for it to expire.");
            var reconnecting = !rpc.Connected;
            await rpc.ConnectAsync(token);
            if (leaseId is null)
            {
                leaseId = Guid.NewGuid().ToString("N"); deviceId = device;
                intent = null; pending = false; sequence = -1; sentPayload = null;
            }
            else if (reconnecting)
            {
                // A fresh IPC connection has no activity, even when the lease and intent are unchanged.
                sentPayload = null;
                pending = true;
            }
            expiresAt = clock.GetUtcNow().AddSeconds(LeaseSeconds);
            Refresh("Desktop publishing reserved. Clear the browser's headless session before sending presence.");
            return new(leaseId, expiresAt);
        }
        finally { gate.Release(); }
    }

    public async Task<LeaseView> UpdateAsync(string device, PresenceRequest request, CancellationToken token)
    {
        PresencePayload.Build(request.Intent); // Validate before touching ownership or extending the lease.
        if (request.Sequence < 0) throw new LinkException(400, "invalid_sequence", "Sequence must be nonnegative and increase for each update.");
        await gate.WaitAsync(token);
        try
        {
            await ExpireAsync(token);
            RequireLease(device, request.LeaseId);
            if (request.Sequence <= sequence) throw new LinkException(409, "stale_sequence", "This update is older than an accepted update. Increment sequence.");
            sequence = request.Sequence; intent = request.Intent; pending = true;
            expiresAt = clock.GetUtcNow().AddSeconds(LeaseSeconds);
            Refresh("Browser activity accepted.");
            return new(leaseId!, expiresAt);
        }
        finally { gate.Release(); }
    }

    public async Task ReleaseAsync(string device, string? lease, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            await ExpireAsync(token);
            if (leaseId is null) return;
            RequireLease(device, lease);
            await ClearAsync(token);
        }
        finally { gate.Release(); }
    }

    public async Task RevokeAsync(string device, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { if (deviceId == device) await ClearAsync(token); }
        finally { gate.Release(); }
    }

    public async Task TickAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            await ExpireAsync(token);
            if (leaseId is null) return;
            var payload = PresencePayload.Build(intent)?.ToJsonString() ?? "null";
            if (!pending && rpc.Connected) return;
            if (payload == sentPayload && rpc.Connected)
            {
                pending = false;
                Refresh(intent is null ? "Desktop presence cleared; browser still linked." : "Publishing through Discord desktop.");
                return;
            }
            if (intent is not null && clock.GetUtcNow() - lastSent < TimeSpan.FromSeconds(5)) return;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                var remaining = expiresAt - clock.GetUtcNow();
                if (remaining <= TimeSpan.Zero) { await ClearAsync(token); return; }
                deadline.CancelAfter(remaining);
                await rpc.PublishAsync(intent, deadline.Token);
                sentPayload = payload; pending = false; lastSent = clock.GetUtcNow();
                Refresh(intent is null ? "Desktop presence cleared; browser still linked." : "Publishing through Discord desktop.");
            }
            catch (Exception e) when (e is LinkException or IOException or TimeoutException or OperationCanceledException or ObjectDisposedException)
            {
                token.ThrowIfCancellationRequested();
                pending = true; lastSent = clock.GetUtcNow();
                status = new(false, "Waiting for Discord desktop to reconnect.", deviceId, expiresAt, intent?.Details, false);
            }
        }
        finally { gate.Release(); }
    }

    private void RequireLease(string device, string? lease)
    {
        if (leaseId is null || deviceId != device || leaseId != lease)
            throw new LinkException(409, "lease_lost", "Acquire a new publishing lease before sending presence.");
    }
    private async Task ExpireAsync(CancellationToken token)
    { if (leaseId is not null && expiresAt <= clock.GetUtcNow()) await ClearAsync(token); }
    private async Task ClearAsync(CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            if (rpc.Connected) await rpc.PublishAsync(null, timeout.Token);
        }
        catch (Exception e) when (e is LinkException or IOException or TimeoutException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            // Closing the IPC connection also removes presence if a clear cannot be acknowledged.
            rpc.Disconnect(); leaseId = null; deviceId = null; intent = null; pending = false; sentPayload = null;
            status = new(false, "Ready for a browser to connect.", null, null, null, false);
        }
    }
    private void Refresh(string message) => status = new(rpc.Connected, message, deviceId, expiresAt,
        intent?.Details, intent is not null && !pending && sentPayload is not null);

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ClearAsync(timeout.Token);
            await rpc.DisposeAsync();
        }
        finally { gate.Release(); }
    }
}
