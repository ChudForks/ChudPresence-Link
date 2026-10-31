using ChudPresence.Link;
using ChudPresence.Link.Windows;

internal static class SettingsUiChecks
{
    public static void Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var browser = new ServiceParticipant("browser", "My browser", "browser", "creator", now, true, false, true, true, true);
        var phone = new ServiceParticipant("phone", "My phone", "mobile", "title", now, false, true, false, true, false);
        var service = new SharedService("youtube", "YouTube", true, false, 0, ["app", "creator", "title"],
            [browser, phone], ServiceValues.AllFields, ["display"], false);

        var selection = new SyncSelection(service, null);
        check(selection.AllSettings && selection.Build()?.Fields?.SequenceEqual(ServiceValues.AllFields) == true &&
            selection.Build()?.SourceDeviceId == "browser",
            "new setup selects all currently supported settings and a device source");
        selection.SetAllSettings(false);
        foreach (var key in ServiceValues.AllFields) selection.SetField(key, key is "artwork" or "actionButtons");
        check(selection.Build()?.Fields?.SequenceEqual(["artwork", "actionButtons"]) == true,
            "setup can share a subset without the Discord bar");
        selection.SetField("artwork", false);
        selection.SetField("actionButtons", false);
        check(selection.Build() is null, "setup cannot save an empty selection");
        selection.SetAllSettings(true);
        check(selection.Build()?.Fields?.Length == 6, "switching back to all restores every supported field");
        selection.SetField("artwork", false);
        check(selection.Build()?.Fields?.Length == 6, "all-settings mode prevents accidentally deselecting individual fields");

        var subsetService = service with { SyncEnabled = true, SyncRequested = true, SyncVersion = 5, SyncFields = ["artwork"] };
        var reenable = new SyncSelection(subsetService with { SyncEnabled = false, SyncRequested = false }, null);
        check(!reenable.AllSettings && reenable.Build()?.Fields?.SequenceEqual(["artwork"]) == true,
            "reenabling sync starts with the previous field selection");

        var edit = new SyncSelection(subsetService, new((string?)null, 4, Artwork: false));
        check(edit.Build()?.InitialSettings?.Artwork == false && edit.Build()?.Fields?.SequenceEqual(["artwork"]) == true,
            "editing a selection keeps the current shared value, including false");
        edit.SetAllSettings(true);
        check(edit.Build() is null, "adding settings requires a source with all starting values");
        edit.SourceIndex = edit.Sources.Select((source, index) => (source, index))
            .Single(item => item.source.DeviceId == "browser").index;
        check(edit.Build()?.SourceDeviceId == "browser" && edit.Build()?.InitialSettings is null,
            "choosing a device supplies newly selected settings without conflicting explicit values");

        var legacyService = service with
        {
            SupportedFields = ["display"],
            Participants = [browser with { Enabled = null, ShowWhilePaused = null, Artwork = null, PlaybackProgress = null, ActionButtons = null }, phone]
        };
        var legacy = new SyncSelection(legacyService, null);
        legacy.SetAllSettings(false);
        legacy.SetField("artwork", true);
        check(!legacy.SelectedFields.Contains("artwork") && legacy.Build()?.Fields?.SequenceEqual(["display"]) == true,
            "older clients cannot select unsupported preferences");

        var artworkOnly = new SyncSelection(service with { SupportedFields = ["artwork"], DisplayChoices = [] }, null);
        check(artworkOnly.Build()?.Fields?.SequenceEqual(["artwork"]) == true,
            "boolean settings remain available without a common Discord bar choice");

        var mismatchedDisplay = new SyncSelection(service with { DisplayChoices = ["app"] }, null);
        check(mismatchedDisplay.Build() is null, "a source with an unsupported display choice cannot be saved");
    }
}
