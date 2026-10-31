namespace ChudPresence.Link.Windows;

// Keeps the native dialog's selection rules separate from its control state.
internal sealed class SyncSelection
{
    private readonly HashSet<string> selectedFields;

    public SyncSelection(SharedService service, ServicePreference? preference)
    {
        Service = service;
        selectedFields = service.SupportedFields
            .Where(key => service.SyncVersion == 0 || service.SyncFields.Contains(key)).ToHashSet();
        AllSettings = service.SyncVersion == 0 || service.SupportedFields.Order().SequenceEqual(service.SyncFields.Order());
        var sources = new List<SyncSource>();
        if (preference is not null) sources.Add(new(null, preference, "Current shared settings"));
        sources.AddRange(service.Participants.Select(participant => new SyncSource(participant.DeviceId, participant,
            participant.Name + (participant.Kind == "mobile" ? " · Phone" : " · Browser"), participant.ReportedAt)));
        sources.AddRange(service.DisplayChoices.Select(choice => new SyncSource(null, new ServiceValues(choice),
            "Discord bar only · " + DisplayName(choice))));
        Sources = sources;
        SourceIndex = sources.Count > 0 ? 0 : -1;
    }

    public SharedService Service { get; }
    public bool AllSettings { get; private set; }
    public IReadOnlyCollection<string> SelectedFields => selectedFields;
    public IReadOnlyList<SyncSource> Sources { get; }
    public int SourceIndex { get; set; }

    public void SetAllSettings(bool value)
    {
        AllSettings = value;
        if (!value) return;
        selectedFields.Clear();
        selectedFields.UnionWith(Service.SupportedFields);
    }

    public void SetField(string key, bool value)
    {
        if (AllSettings || !Service.SupportedFields.Contains(key)) return;
        if (value) selectedFields.Add(key);
        else selectedFields.Remove(key);
    }

    public ServiceSyncRequest? Build()
    {
        var keys = ServiceValues.AllFields.Where(selectedFields.Contains).ToArray();
        if (keys.Length == 0 || SourceIndex < 0 || SourceIndex >= Sources.Count) return null;
        var chosen = Sources[SourceIndex];
        if (keys.Except(chosen.Values.Fields).Any() ||
            (keys.Contains("display") && !Service.DisplayChoices.Contains(chosen.Values.Display))) return null;
        return new(true, Service.SyncVersion, chosen.DeviceId, Fields: keys,
            InitialSettings: chosen.DeviceId is null ? chosen.Values.Select(keys) : null);
    }

    public static string DisplayName(string value) => value switch
    {
        "app" => "App name", "creator" => "Creator / artist", "title" => "Video / song title", _ => value
    };
}

internal sealed record SyncSource(string? DeviceId, ServiceValues Values, string Title, DateTimeOffset? ReportedAt = null);
