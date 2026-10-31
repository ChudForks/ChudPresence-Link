using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChudPresence.Link;

public interface IDiscordRpc : IAsyncDisposable
{
    bool Connected { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
    Task PublishAsync(PresenceIntent? intent, CancellationToken cancellationToken);
    void Disconnect();
}

// No Discord OAuth credentials are needed: this talks to the signed-in desktop client.
public sealed class DiscordRpc(string applicationId = "1549066134706323548", string pipePrefix = "discord-ipc-") : IDiscordRpc
{
    private sealed class Connection(Stream stream) : IDisposable
    {
        public Stream Stream { get; } = stream;
        public CancellationTokenSource Stop { get; } = new();
        public SemaphoreSlim Writes { get; } = new(1);
        public ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> Pending { get; } = new();
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Alive = true;
        public void Dispose()
        {
            Alive = false;
            Stop.Cancel();
            Stream.Dispose();
            Ready.TrySetException(new IOException("Discord disconnected."));
            foreach (var request in Pending.Values) request.TrySetException(new IOException("Discord disconnected."));
        }
    }
    private Connection? connection;
    public bool Connected => connection?.Alive == true;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (Connected) return;
        Disconnect();
        foreach (var endpoint in Endpoints())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Stream? stream = null;
            Connection? candidate = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(200));
                if (OperatingSystem.IsWindows())
                {
                    var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
                    stream = pipe;
                    await pipe.ConnectAsync(timeout.Token);
                }
                else
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), timeout.Token); }
                    catch { socket.Dispose(); throw; }
                    stream = new NetworkStream(socket, ownsSocket: true);
                }
                candidate = new(stream);
                _ = ReadLoop(candidate);
                await WriteFrame(candidate, 0, JsonSerializer.SerializeToUtf8Bytes(new { v = 1, client_id = applicationId }), cancellationToken);
                await candidate.Ready.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                connection = candidate;
                return;
            }
            catch (Exception e) when (e is IOException or SocketException or TimeoutException or OperationCanceledException or ObjectDisposedException)
            {
                candidate?.Dispose();
                stream?.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        throw new LinkException(503, "discord_unavailable", "Open Discord on this computer and sign in to enable desktop publishing.");
    }

    private IEnumerable<string> Endpoints()
    {
        if (OperatingSystem.IsWindows()) return Enumerable.Range(0, 10).Select(i => pipePrefix + i);
        var directories = new[] { Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), Environment.GetEnvironmentVariable("TMPDIR"),
            Environment.GetEnvironmentVariable("TMP"), Environment.GetEnvironmentVariable("TEMP"), "/tmp" };
        return directories.Where(d => !string.IsNullOrEmpty(d)).Distinct()
            .SelectMany(d => Enumerable.Range(0, 10).Select(i => Path.Combine(d!, pipePrefix + i)))
            .Where(File.Exists);
    }

    public async Task PublishAsync(PresenceIntent? intent, CancellationToken cancellationToken)
    {
        await ConnectAsync(cancellationToken);
        var active = connection!;
        var nonce = Guid.NewGuid().ToString("N");
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        active.Pending[nonce] = reply;
        try
        {
            var payload = new JsonObject
            {
                ["cmd"] = "SET_ACTIVITY", ["nonce"] = nonce,
                ["args"] = new JsonObject { ["pid"] = Environment.ProcessId, ["activity"] = PresencePayload.Build(intent) }
            };
            await WriteFrame(active, 1, JsonSerializer.SerializeToUtf8Bytes(payload), cancellationToken);
            var response = await reply.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            if (response.TryGetProperty("evt", out var evt) && evt.GetString() == "ERROR")
                throw new LinkException(502, "discord_rejected", "Discord rejected this activity. Check its fields and Discord activity privacy settings.");
        }
        catch { Disconnect(); throw; }
        finally { active.Pending.TryRemove(nonce, out _); }
    }

    private static async Task ReadLoop(Connection active)
    {
        try
        {
            var header = new byte[8];
            while (!active.Stop.IsCancellationRequested)
            {
                await active.Stream.ReadExactlyAsync(header, active.Stop.Token);
                var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                if (length is < 0 or > 65_536) throw new IOException("Invalid Discord frame.");
                var bytes = new byte[length];
                await active.Stream.ReadExactlyAsync(bytes, active.Stop.Token);
                if (opcode == 2) throw new IOException("Discord closed its connection.");
                if (opcode == 3) { await WriteFrame(active, 4, bytes, active.Stop.Token); continue; }
                if (opcode != 1) continue;
                using var json = JsonDocument.Parse(bytes);
                var root = json.RootElement;
                if (root.TryGetProperty("evt", out var evt) && evt.GetString() == "READY") active.Ready.TrySetResult();
                if (root.TryGetProperty("nonce", out var nonce) && nonce.ValueKind == JsonValueKind.String &&
                    active.Pending.TryGetValue(nonce.GetString()!, out var pending)) pending.TrySetResult(root.Clone());
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or JsonException or InvalidOperationException)
        { active.Dispose(); }
    }

    private static async Task WriteFrame(Connection active, int opcode, byte[] bytes, CancellationToken token)
    {
        await active.Writes.WaitAsync(token);
        try
        {
            var frame = new byte[8 + bytes.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), bytes.Length);
            bytes.CopyTo(frame, 8);
            await active.Stream.WriteAsync(frame, token);
            await active.Stream.FlushAsync(token);
        }
        finally { active.Writes.Release(); }
    }

    public void Disconnect() { Interlocked.Exchange(ref connection, null)?.Dispose(); }
    public ValueTask DisposeAsync() { Disconnect(); return ValueTask.CompletedTask; }
}
