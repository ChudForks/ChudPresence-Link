using System.Text.Json.Serialization;

namespace ChudPresence.Link;

public sealed record Device(string Id, string Name, string Kind, string TokenHash, string? Origin,
    DateTimeOffset PairedAt, DateTimeOffset LastSeen);
public sealed record DeviceView(string Id, string Name, string Kind, DateTimeOffset PairedAt, DateTimeOffset LastSeen);
[method: JsonConstructor]
public sealed record ServicePreference(string? Display, long Version, bool? Enabled = null, bool? ShowWhilePaused = null,
    bool? Artwork = null, bool? PlaybackProgress = null, bool? ActionButtons = null)
    : ServiceValues(Display, Enabled, ShowWhilePaused, Artwork, PlaybackProgress, ActionButtons)
{
    public ServicePreference(ServiceValues values, long version) : this(values.Display, version, values.Enabled,
        values.ShowWhilePaused, values.Artwork, values.PlaybackProgress, values.ActionButtons) { }
}
[method: JsonConstructor]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreferenceChange(long BaseVersion, string? Display = null, bool? Enabled = null, bool? ShowWhilePaused = null,
    bool? Artwork = null, bool? PlaybackProgress = null, bool? ActionButtons = null)
    : ServiceValues(Display, Enabled, ShowWhilePaused, Artwork, PlaybackProgress, ActionButtons)
{
    public PreferenceChange(string display, long baseVersion) : this(baseVersion, display) { }
}
public sealed record SettingsSnapshot(long Revision, Dictionary<string, ServicePreference> Services);
public sealed record SettingsResult(long Revision, Dictionary<string, ServicePreference> Services, string[] Conflicts);
public sealed record PairRequest(string Code, string Name, string Kind);
public sealed record PairResponse(string ServerId, string DeviceId, string Token, int ProtocolVersion = 1);
public sealed record SettingsRequest(Dictionary<string, PreferenceChange> Changes);
public sealed record ClientService(string Id, string Name, string? Display = null, string[]? DisplayChoices = null,
    bool? Enabled = null, bool? ShowWhilePaused = null, bool? Artwork = null, bool? PlaybackProgress = null, bool? ActionButtons = null)
    : ServiceValues(Display, Enabled, ShowWhilePaused, Artwork, PlaybackProgress, ActionButtons);
public sealed record ServicesRequest(ClientService[] Services);
public sealed record ServiceInventory(DateTimeOffset ReportedAt, ClientService[] Services);
public sealed record SyncPolicy(bool Enabled, long Version, string[]? Fields = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ServiceSyncRequest(bool Enabled, long BaseVersion, string? SourceDeviceId = null, string? InitialDisplay = null,
    string[]? Fields = null, ServiceValues? InitialSettings = null);
public sealed record ServiceParticipant(string DeviceId, string Name, string Kind, string? Display, DateTimeOffset ReportedAt,
    bool? Enabled = null, bool? ShowWhilePaused = null, bool? Artwork = null, bool? PlaybackProgress = null, bool? ActionButtons = null)
    : ServiceValues(Display, Enabled, ShowWhilePaused, Artwork, PlaybackProgress, ActionButtons);
public sealed record SharedService(string Id, string Name, bool Shared, bool SyncEnabled, long SyncVersion,
    string[] DisplayChoices, ServiceParticipant[] Participants, string[] SupportedFields, string[] SyncFields, bool SyncRequested);
public sealed record ServicesSnapshot(long Revision, SharedService[] Services);
public sealed record PairingCode(string Code, string Kind, DateTimeOffset ExpiresAt);
public sealed record PresenceButton(string Label, string Url);
public sealed record PresenceTimestamps(long? Start = null, long? End = null);
public sealed record PresenceAssets(string? LargeImage = null, string? LargeText = null,
    string? LargeUrl = null, string? SmallImage = null, string? SmallText = null);
public sealed record PresenceIntent(string Name, string Details, string Type = "watching", string? State = null,
    string StatusDisplayType = "name", string? DetailsUrl = null, string? StateUrl = null,
    PresenceTimestamps? Timestamps = null, PresenceAssets? Assets = null, PresenceButton[]? Buttons = null);
public sealed record PresenceRequest(string LeaseId, long Sequence, PresenceIntent? Intent);
public sealed record LeaseRequest(string? LeaseId = null);
public sealed record LeaseView(string LeaseId, DateTimeOffset ExpiresAt, int HeartbeatSeconds = 10, int FallbackGraceSeconds = 5);
public sealed record PresenceStatus(bool DiscordConnected, string Message, string? DeviceId,
    DateTimeOffset? ExpiresAt, string? Details, bool Published);

public sealed class LinkException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
