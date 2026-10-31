using System.Text.Json.Serialization;

namespace ChudPresence.Link;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record ServiceValues(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Display = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Enabled = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ShowWhilePaused = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Artwork = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PlaybackProgress = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ActionButtons = null)
{
    public static readonly string[] AllFields = ["display", "enabled", "showWhilePaused", "artwork", "playbackProgress", "actionButtons"];

    // An omitted value is unsupported in an inventory and unchanged in a patch.
    [JsonIgnore]
    public string[] Fields => AllFields.Where(key => Value(key) is not null).ToArray();

    public object? Value(string field) => field switch
    {
        "display" => Display, "enabled" => Enabled, "showWhilePaused" => ShowWhilePaused,
        "artwork" => Artwork, "playbackProgress" => PlaybackProgress, "actionButtons" => ActionButtons,
        _ => null
    };

    public ServiceValues Select(IEnumerable<string> fields)
    {
        var selected = fields.ToHashSet();
        return new(selected.Contains("display") ? Display : null, selected.Contains("enabled") ? Enabled : null,
            selected.Contains("showWhilePaused") ? ShowWhilePaused : null, selected.Contains("artwork") ? Artwork : null,
            selected.Contains("playbackProgress") ? PlaybackProgress : null, selected.Contains("actionButtons") ? ActionButtons : null);
    }

    public ServiceValues Merge(ServiceValues changes) => new(changes.Display ?? Display, changes.Enabled ?? Enabled,
        changes.ShowWhilePaused ?? ShowWhilePaused, changes.Artwork ?? Artwork,
        changes.PlaybackProgress ?? PlaybackProgress, changes.ActionButtons ?? ActionButtons);
}
