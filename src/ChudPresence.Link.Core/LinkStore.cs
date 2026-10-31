using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChudPresence.Link;

public sealed partial class LinkStore
{
    private sealed record State(string ServerId, long Revision,
        Dictionary<string, ServicePreference> Services, Dictionary<string, Device> Devices)
    {
        public Dictionary<string, ServiceInventory> Inventories { get; init; } = new();
        public Dictionary<string, SyncPolicy> SyncPolicies { get; init; } = new();
    }
    private readonly object gate = new();
    private readonly string path;
    private readonly TimeProvider clock;
    private State state;
    private PairingCode? pairing;
    private TaskCompletionSource<bool> revisionChanged = NewRevisionSignal();
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public LinkStore(string directory, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "link-state.json");
        // A damaged state must not silently revoke devices or reset conflict versions.
        state = File.Exists(path)
            ? JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("Link's saved state is empty.")
            : new(Guid.NewGuid().ToString("N"), 0, new(), new());
        if (!File.Exists(path)) Save(state);
    }

    public string ServerId { get { lock (gate) return state.ServerId; } }

    // Compare and register under the same lock so a commit cannot fall between them.
    // A null result means the device was revoked while the caller was waiting.
    public async ValueTask<long?> WaitForRevisionAsync(string deviceId, long? afterRevision, CancellationToken cancellationToken = default)
    {
        Task signal;
        lock (gate)
        {
            if (!state.Devices.ContainsKey(deviceId)) return null;
            if (afterRevision is null || afterRevision != state.Revision) return state.Revision;
            signal = revisionChanged.Task;
        }
        await signal.WaitAsync(cancellationToken);
        lock (gate) return state.Devices.ContainsKey(deviceId) ? state.Revision : null;
    }

    public PairingCode? CurrentCode { get { lock (gate) return pairing?.ExpiresAt > clock.GetUtcNow() ? pairing : null; } }
    public PairingCode CreatePairingCode(string kind)
    {
        ValidateKind(kind);
        lock (gate) return pairing = new(RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8"),
            kind, clock.GetUtcNow().AddMinutes(5));
    }

    public void CancelPairing() { lock (gate) pairing = null; }

    public PairResponse Pair(PairRequest request, string? origin)
    {
        ValidateKind(request.Kind);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80 || request.Name.Any(char.IsControl))
            throw new LinkException(400, "invalid_name", "Use a device name of 1–80 characters.");
        if (request.Kind == "browser" ? !IsExtensionOrigin(origin) : !string.IsNullOrEmpty(origin))
            throw new LinkException(403, "invalid_origin", "Browser clients must use their extension Origin; mobile clients must omit Origin.");
        lock (gate)
        {
            if (pairing is null || pairing.ExpiresAt <= clock.GetUtcNow() || pairing.Kind != request.Kind ||
                request.Code is null || !FixedEquals(pairing.Code, request.Code))
                throw new LinkException(401, "invalid_code", "The pairing code is incorrect, expired, or for another device type.");
            if (state.Devices.Count >= 32) throw new LinkException(409, "device_limit", "Remove a paired device before adding another.");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var id = Guid.NewGuid().ToString("N");
            var now = clock.GetUtcNow();
            var devices = new Dictionary<string, Device>(state.Devices)
            { [id] = new(id, request.Name.Trim(), request.Kind, Hash(token), origin, now, now) };
            Commit(state with { Devices = devices });
            pairing = null;
            return new(state.ServerId, id, token);
        }
    }

    public Device Authenticate(string token, string? origin)
    {
        if (token.Length != 64) throw new LinkException(401, "unauthorized", "Pair this device with ChudPresence Link.");
        var hash = Hash(token);
        lock (gate)
        {
            var device = state.Devices.Values.FirstOrDefault(d => FixedEquals(d.TokenHash, hash));
            if (device is null || !string.Equals(device.Origin, origin, StringComparison.Ordinal))
                throw new LinkException(401, "unauthorized", "Pair this device with ChudPresence Link.");
            device = device with { LastSeen = clock.GetUtcNow() };
            state.Devices[device.Id] = device;
            return device;
        }
    }

    public DeviceView[] Devices()
    {
        lock (gate) return state.Devices.Values.OrderBy(d => d.PairedAt)
            .Select(d => new DeviceView(d.Id, d.Name, d.Kind, d.PairedAt, d.LastSeen)).ToArray();
    }

    public void Revoke(string id)
    {
        lock (gate)
        {
            var devices = new Dictionary<string, Device>(state.Devices);
            devices.Remove(id);
            var inventories = new Dictionary<string, ServiceInventory>(state.Inventories);
            inventories.Remove(id);
            Commit(state with { Devices = devices, Inventories = inventories, Revision = state.Revision + 1 });
        }
    }

    public SettingsSnapshot Settings(string? deviceId = null)
    {
        lock (gate)
        {
            var eligible = DiscoverInternal(deviceId).Where(s => s.SyncEnabled).ToDictionary(s => s.Id);
            return new(state.Revision, state.Services.Where(p => eligible.ContainsKey(p.Key))
                .ToDictionary(p => p.Key, p => new ServicePreference(p.Value.Select(eligible[p.Key].SyncFields), p.Value.Version)));
        }
    }

    public SettingsResult Update(Dictionary<string, PreferenceChange>? changes, string? deviceId = null)
    {
        if (changes is null || changes.Count > 128) throw new LinkException(400, "invalid_settings", "Send at most 128 service preferences.");
        foreach (var (service, change) in changes)
        {
            if (service.Length > 64 || !ServiceId().IsMatch(service) || change is null ||
                change.Display is not (null or "app" or "creator" or "title") || change.Fields.Length == 0 || change.BaseVersion < 0)
                throw new LinkException(400, "invalid_settings", "Each service needs a lowercase hyphenated ID, at least one valid setting, and a nonnegative baseVersion.");
        }
        lock (gate)
        {
            var eligible = DiscoverInternal(deviceId).Where(s => s.SyncEnabled).ToDictionary(s => s.Id);
            foreach (var (id, change) in changes)
            {
                if (!eligible.TryGetValue(id, out var service))
                    throw new LinkException(409, "sync_not_enabled", "Report this service, find a browser/phone match, and enable its sync first.");
                if (change.Fields.Except(service.SyncFields).Any())
                    throw new LinkException(409, "field_not_synced", "Only change settings selected for sync. Refresh service discovery first.");
                if (change.Display is not null && !service.DisplayChoices.Contains(change.Display))
                    throw new LinkException(400, "unsupported_display", "Choose a display value supported by every device sharing this service.");
            }
            var services = new Dictionary<string, ServicePreference>(state.Services);
            var revision = state.Revision;
            var conflicts = new List<string>();
            foreach (var (service, change) in changes)
            {
                services.TryGetValue(service, out var previous);
                if (previous is not null && change.Fields.All(field => Equals(previous.Value(field), change.Value(field)))) continue;
                if ((previous?.Version ?? 0) != change.BaseVersion) { conflicts.Add(service); continue; }
                services[service] = new((previous ?? new ServiceValues()).Merge(change), ++revision);
            }
            if (services.Count > 256) throw new LinkException(409, "service_limit", "At most 256 services can be linked.");
            if (revision != state.Revision) Commit(state with { Revision = revision, Services = services });
            var visible = Settings(deviceId);
            return new(visible.Revision, visible.Services, conflicts.ToArray());
        }
    }

    public ServicesSnapshot Discover(string? deviceId = null)
    {
        lock (gate) return new(state.Revision, DiscoverInternal(deviceId));
    }

    public ServicesSnapshot ReportServices(string deviceId, ClientService[]? services)
    {
        if (services is null || services.Length > 128)
            throw new LinkException(400, "invalid_services", "Report at most 128 installed services.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var normalized = services.Select(service =>
        {
            if (service is null || string.IsNullOrEmpty(service.Id) || service.Id.Length > 64 ||
                !ServiceId().IsMatch(service.Id) || !ids.Add(service.Id) || string.IsNullOrWhiteSpace(service.Name) ||
                service.Name.Length > 80 || service.Name.Any(char.IsControl))
                throw new LinkException(400, "invalid_services", "Services need unique lowercase hyphenated IDs and names of 1–80 characters.");
            var choices = service.DisplayChoices ?? (service.Display is null ? [] : ["app", "creator", "title"]);
            if (choices.Length > 3 || choices.Distinct().Count() != choices.Length || service.Fields.Length == 0 ||
                choices.Any(choice => choice is not ("app" or "creator" or "title")) ||
                (service.Display is null ? choices.Length != 0 : !choices.Contains(service.Display)))
                throw new LinkException(400, "invalid_services", "Report current supported settings. A display needs app/creator/title choices; omit both when display is unsupported.");
            return service with { Name = service.Name.Trim(), DisplayChoices = choices.Order().ToArray() };
        }).OrderBy(s => s.Id).ToArray();
        lock (gate)
        {
            if (!state.Devices.ContainsKey(deviceId)) throw new LinkException(401, "unauthorized", "Pair this device before reporting services.");
            var inventories = new Dictionary<string, ServiceInventory>(state.Inventories)
            { [deviceId] = new(clock.GetUtcNow(), normalized) };
            if (inventories.Values.SelectMany(i => i.Services).Select(s => s.Id).Distinct().Count() > 256)
                throw new LinkException(409, "service_limit", "At most 256 distinct services can be reported across paired devices.");
            Commit(state with { Inventories = inventories, Revision = state.Revision + 1 });
            return Discover(deviceId);
        }
    }

    public ServicesSnapshot SetServiceSync(string id, ServiceSyncRequest request, string? deviceId = null)
    {
        if (request.BaseVersion < 0) throw new LinkException(400, "invalid_sync", "Use the syncVersion returned by service discovery.");
        var fields = request.Fields ?? ["display"];
        if (fields.Length is < 1 or > 6 || fields.Distinct().Count() != fields.Length || fields.Except(ServiceValues.AllFields).Any())
            throw new LinkException(400, "invalid_sync", "Choose one or more distinct supported settings to sync.");
        if (!request.Enabled && (request.SourceDeviceId is not null || request.InitialDisplay is not null || request.InitialSettings is not null || request.Fields is not null))
            throw new LinkException(400, "invalid_sync", "Disabling sync needs only enabled and baseVersion.");
        lock (gate)
        {
            var service = DiscoverInternal(deviceId).FirstOrDefault(s => s.Id == id)
                ?? throw new LinkException(404, "service_not_reported", "Report this installed service before configuring sync.");
            if (service.SyncVersion != request.BaseVersion)
                throw new LinkException(409, "sync_conflict", "The sync selection changed. Refresh service discovery before trying again.");
            var preferences = new Dictionary<string, ServicePreference>(state.Services);
            var revision = state.Revision;
            if (request.Enabled)
            {
                if (!service.Shared || (fields.Contains("display") && service.DisplayChoices.Length == 0))
                    throw new LinkException(409, "service_not_shared", "Sync needs a browser and phone reporting compatible settings for this service.");
                if (fields.Except(service.SupportedFields).Any())
                    throw new LinkException(400, "unsupported_field", "Select only settings reported by every participant.");
                var sourceCount = (request.SourceDeviceId is null ? 0 : 1) + (request.InitialDisplay is null ? 0 : 1) + (request.InitialSettings is null ? 0 : 1);
                if (sourceCount > 1)
                    throw new LinkException(400, "invalid_sync", "Choose one source device, initial display, or initial settings object.");
                ServiceValues? source = request.InitialSettings ?? (request.InitialDisplay is null ? null : new ServiceValues(request.InitialDisplay))
                    ?? service.Participants.FirstOrDefault(p => p.DeviceId == request.SourceDeviceId);
                if (source is null)
                    throw new LinkException(400, "source_required", "Choose a participating source device or initial values to start sync.");
                if (fields.Except(source.Fields).Any())
                    throw new LinkException(400, "invalid_sync", "The source must supply a value for every selected setting.");
                if (fields.Contains("display") && !service.DisplayChoices.Contains(source.Display))
                    throw new LinkException(400, "unsupported_display", "The selected device's setting is not supported by every participant.");
                // A new selection also invalidates edits queued under an earlier field selection.
                preferences[id] = new(source.Select(fields), ++revision);
            }
            var policies = new Dictionary<string, SyncPolicy>(state.SyncPolicies)
            { [id] = new(request.Enabled, ++revision, request.Enabled ? ServiceValues.AllFields.Intersect(fields).ToArray() : service.SyncFields) };
            Commit(state with { Services = preferences, SyncPolicies = policies, Revision = revision });
            return Discover(deviceId);
        }
    }

    private SharedService[] DiscoverInternal(string? deviceId)
    {
        HashSet<string>? ownIds = deviceId is null ? null : state.Inventories.TryGetValue(deviceId, out var own)
            ? own.Services.Select(s => s.Id).ToHashSet() : new();
        return state.Inventories.Where(i => state.Devices.ContainsKey(i.Key))
            .SelectMany(i => i.Value.Services.Select(s => new { Service = s, Device = state.Devices[i.Key], i.Value.ReportedAt }))
            .Where(p => ownIds is null || ownIds.Contains(p.Service.Id)).GroupBy(p => p.Service.Id)
            .Select(group =>
            {
                var participants = group.OrderBy(p => p.Device.Kind).ThenBy(p => p.Device.Id).ToArray();
                var shared = participants.Any(p => p.Device.Kind == "browser") && participants.Any(p => p.Device.Kind == "mobile");
                var choices = participants.Select(p => p.Service.DisplayChoices!).Aggregate((left, right) => left.Intersect(right).ToArray());
                var supported = ServiceValues.AllFields.Where(field => participants.All(p => p.Service.Value(field) is not null) &&
                    (field != "display" || choices.Length > 0)).ToArray();
                state.SyncPolicies.TryGetValue(group.Key, out var policy);
                var fields = policy?.Fields ?? ["display"];
                var enabled = shared && policy?.Enabled == true && !fields.Except(supported).Any() &&
                    state.Services.TryGetValue(group.Key, out var preference) && !fields.Except(preference.Fields).Any() &&
                    (!fields.Contains("display") || choices.Contains(preference.Display));
                return new SharedService(group.Key, participants[0].Service.Name, shared, enabled, policy?.Version ?? 0,
                    choices, participants.Select(p => new ServiceParticipant(p.Device.Id, p.Device.Name, p.Device.Kind, p.Service.Display, p.ReportedAt,
                        p.Service.Enabled, p.Service.ShowWhilePaused, p.Service.Artwork, p.Service.PlaybackProgress, p.Service.ActionButtons)).ToArray(),
                    supported, fields, policy?.Enabled == true);
            }).OrderBy(s => s.Name).ThenBy(s => s.Id).ToArray();
    }

    private void Commit(State next)
    {
        var changed = next.Revision != state.Revision;
        Save(next);
        state = next;
        if (changed)
        {
            var previous = revisionChanged;
            revisionChanged = NewRevisionSignal();
            previous.TrySetResult(true);
        }
    }
    private static TaskCompletionSource<bool> NewRevisionSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Save(State next)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, next, Json);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateKind(string kind)
    {
        if (kind is not ("browser" or "mobile")) throw new LinkException(400, "invalid_kind", "Choose browser or mobile.");
    }
    public static bool IsExtensionOrigin(string? origin) => origin is not null && ExtensionOrigin().IsMatch(origin);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static bool FixedEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    [GeneratedRegex("\\A[a-z][a-z0-9]*(?:-[a-z0-9]+)*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceId();
    [GeneratedRegex("\\A(?:chrome-extension://[a-p]{32}|moz-extension://[a-fA-F0-9-]{36})\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionOrigin();
}
