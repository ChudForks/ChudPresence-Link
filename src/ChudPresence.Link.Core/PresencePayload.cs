using System.Text;
using System.Text.Json.Nodes;

namespace ChudPresence.Link;

public static class PresencePayload
{
    public static JsonObject? Build(PresenceIntent? intent)
    {
        if (intent is null) return null;
        Text(intent.Name, true); Text(intent.Details, true); Text(intent.State);
        if (intent.Type is not ("playing" or "streaming" or "listening" or "watching" or "competing") ||
            intent.StatusDisplayType is not ("name" or "state" or "details")) Invalid();
        var activity = new JsonObject
        {
            ["name"] = intent.Name, ["details"] = intent.Details,
            // SET_ACTIVITY does not support Streaming; live streams use Watching.
            ["type"] = intent.Type switch { "playing" => 0, "listening" => 2, "competing" => 5, _ => 3 },
            ["status_display_type"] = intent.StatusDisplayType switch { "state" => 1, "details" => 2, _ => 0 }
        };
        if (!string.IsNullOrEmpty(intent.State)) activity["state"] = intent.State;
        AddUrl(activity, "details_url", intent.DetailsUrl); AddUrl(activity, "state_url", intent.StateUrl);
        if (intent.Timestamps is { } time)
        {
            const long maxSeconds = 8_640_000_000_000;
            if (time.Start is < 0 or > maxSeconds || time.End is < 0 or > maxSeconds ||
                time.Start is not null && time.End is not null && time.End < time.Start) Invalid();
            var timestamps = new JsonObject();
            if (time.Start is not null) timestamps["start"] = time.Start.Value;
            if (time.End is not null) timestamps["end"] = time.End.Value;
            if (timestamps.Count > 0) activity["timestamps"] = timestamps;
        }
        if (intent.Assets is { } art)
        {
            Text(art.LargeText); Text(art.SmallText);
            var assets = new JsonObject();
            AddUrl(assets, "large_image", art.LargeImage); AddUrl(assets, "small_image", art.SmallImage);
            AddUrl(assets, "large_url", art.LargeUrl);
            if (!string.IsNullOrEmpty(art.LargeText)) assets["large_text"] = art.LargeText;
            if (!string.IsNullOrEmpty(art.SmallText)) assets["small_text"] = art.SmallText;
            if (assets.Count > 0) activity["assets"] = assets;
        }
        if (intent.Buttons is { Length: > 0 } buttons)
        {
            if (buttons.Length > 2) Invalid();
            var array = new JsonArray();
            foreach (var button in buttons)
            {
                if (button is null) Invalid();
                Text(button!.Label, true, 32); Url(button.Url, true);
                array.Add(new JsonObject { ["label"] = button.Label, ["url"] = button.Url });
            }
            activity["buttons"] = array;
        }
        return activity;
    }

    private static void Text(string? text, bool required = false, int maxBytes = 128)
    {
        if (required && string.IsNullOrWhiteSpace(text) || text is not null &&
            (Encoding.UTF8.GetByteCount(text) > maxBytes || text.Any(char.IsControl))) Invalid();
    }
    private static void AddUrl(JsonObject target, string key, string? url)
    {
        Url(url);
        if (!string.IsNullOrEmpty(url)) target[key] = url;
    }
    private static void Url(string? url, bool required = false)
    {
        if (string.IsNullOrEmpty(url)) { if (required) Invalid(); return; }
        if (url.Length > 256 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) Invalid();
    }
    private static void Invalid() => throw new LinkException(400, "invalid_presence",
        "Use valid presence types, text up to 128 UTF-8 bytes, HTTPS links up to 256 characters, and at most two buttons.");
}
