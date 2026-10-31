# ChudPresence Link API v1

Base URL: `http://127.0.0.1:17879` on the PC, or `http://<PC-LAN-IP>:17879` on the phone. The server's port can be changed with `--port`. All timestamps in responses are UTC ISO 8601. JSON field names use camel case. The server accepts request bodies up to 32 KiB and no more than 240 ordinary requests per minute per source IP. Pairing has a separate five-request-per-minute limit.

See [CONNECTING.md](CONNECTING.md) for the browser/mobile adapter guide, [openapi.json](openapi.json) for a machine-readable definition, and [../examples/link-client.mjs](../examples/link-client.mjs) for an example client. These integrations are documented here; the other apps are unchanged.

## Discovery and pairing

`GET /v1/info` needs no token. It advertises a stable `serverId`, protocol version, capabilities, and supported display choices:

```json
{
  "name": "ChudPresence Link",
  "version": "0.4.0",
  "protocolVersion": 1,
  "serverId": "<stable ID>",
  "capabilities": ["service-discovery", "service-sync-opt-in", "service-display-sync", "service-settings-sync", "selective-settings-sync", "settings-change-events", "desktop-presence-lease"],
  "displayChoices": ["app", "creator", "title"],
  "settingFields": ["display", "enabled", "showWhilePaused", "artwork", "playbackProgress", "actionButtons"],
  "presenceLeaseSeconds": 30
}
```

The browser can probe this URL on loopback. The phone uses the local IP shown in the desktop window; there is no mDNS discovery. Pin the `serverId` returned during pairing and verify it on subsequent connections. An unexpected identity requires pairing again; never upload a token to an unexpected server.

Create a code in the native desktop window. Send `POST /v1/pair`:

```json
{ "code": "01234567", "name": "Jon's browser", "kind": "browser" }
```

```json
{ "serverId": "<stable ID>", "deviceId": "<device ID>", "token": "<64 hex characters>", "protocolVersion": 1 }
```

For a phone use `kind: "mobile"`, a phone name, and a **Pair phone** code. Codes are bound to device kind, single use, and valid for five minutes. Names are limited to 80 characters; at most 32 devices can be paired. Only the native desktop interface can create codes or remove other devices.

For browser clients, the request must come from the extension's `chrome-extension://<32-character ID>` or `moz-extension://<UUID>` Origin. The browser supplies this header; do not set it manually in extension code. The token is bound to that exact Origin. Mobile clients must omit Origin. Every protected request sends:

```http
Authorization: Bearer <pairing token>
Content-Type: application/json
```

`DELETE /v1/device` unpairs the calling device and clears any presence it owns. Returns `204`. Local revocation in the desktop window has the same effect. The server stores only token hashes; clients must store their returned token privately and keep it out of URLs, logs, exported settings, and page scripts.

## Report services, discover overlap, offer sync

Link has no predefined service list. After pairing, each client sends a full inventory of the services it actually has installed/supported using `PUT /v1/services`:

```json
{
  "services": [
    { "id": "youtube", "name": "YouTube", "display": "creator", "displayChoices": ["app", "creator", "title"],
      "enabled": true, "showWhilePaused": false, "artwork": true, "playbackProgress": true, "actionButtons": true },
    { "id": "my-radio", "name": "My Radio", "display": "title" }
  ]
}
```

`display` is the current local preference, used to offer an initial sync source. When supplied, it must belong to `displayChoices`, which defaults to all three choices if omitted. If the local display is unsupported or cannot be mapped, omit both fields (or send an empty `displayChoices`) and report supported booleans only. Each service needs a unique stable lowercase hyphenated ID (up to 64 characters), a name of 1–80 characters, and at least one supported setting. Send at most 128 services per client; up to 256 distinct services across all devices are retained. An empty array removes the caller's inventory. Each PUT replaces only the authenticated caller's inventory, so submit a full list after install/uninstall changes and serialize report requests. Reports never change shared preferences or enable sync.

The five boolean fields are optional. Include a current `true` or `false` only if this client can both read and apply that preference for this service; omission means unsupported. Do not invent defaults to advertise capabilities. A disabled service remains installed: keep reporting it with `enabled: false` so its preference can still sync. Never remove a service from inventory just because the user disabled its activity.

`GET /v1/services` and the PUT response return the caller's reported services, with their browser/phone overlap:

```json
{
  "revision": 4,
  "services": [
    {
      "id": "youtube",
      "name": "YouTube",
      "shared": true,
      "syncEnabled": false,
      "syncVersion": 0,
      "syncRequested": false,
      "supportedFields": ["display", "enabled", "showWhilePaused", "artwork", "playbackProgress", "actionButtons"],
      "syncFields": ["display"],
      "displayChoices": ["app", "creator", "title"],
      "participants": [
        { "deviceId": "browser-id", "name": "My browser", "kind": "browser", "display": "creator", "enabled": true, "showWhilePaused": false, "artwork": true, "playbackProgress": true, "actionButtons": true, "reportedAt": "2026-10-26T12:00:00Z" },
        { "deviceId": "phone-id", "name": "My phone", "kind": "mobile", "display": "title", "enabled": false, "showWhilePaused": true, "artwork": false, "playbackProgress": true, "actionButtons": false, "reportedAt": "2026-10-26T12:01:00Z" }
      ]
    }
  ]
}
```

`shared` requires at least one browser and one mobile device reporting the same ID. Two browsers alone do not qualify. Display names do not determine identity; the client adapters must agree on IDs. The ID examples here are illustrative, not a server catalog. Normalize differing platform IDs in the later integrations (for example, the existing mobile `youtube_music` can be advertised as `youtube-music`). Unknown IDs work without changing Link.

`displayChoices` in discovery is the intersection across every reporting participant. `supportedFields` is the intersection of reported settings, with `display` included only when there is a common display choice. Offer sync for `shared: true` entries with at least one supported field. Boolean-only sync can work even when no display choices match. A client sees only its own reported service IDs; peer details are limited to participants in those services. Names and last reported values help explain the offer. Inventory is retained while a client is offline, so `reportedAt` is a last-known snapshot, not proof it is online. Report current local settings again after they change if they should be available as future initial sources.

`syncFields` is the saved selection (defaults to `["display"]` before the first opt-in). `syncRequested` remembers the user's opt-in; `syncEnabled` means that selection is currently compatible and active. `syncRequested: true` with `syncEnabled: false` indicates paused sync. Neither flag is the service's `enabled` preference.

The user can enable sync in either client's integration UI or the native Link window. Send `PUT /v1/services/<serviceId>/sync` after the user selects the service and starting source:

```json
{ "enabled": true, "baseVersion": 0, "sourceDeviceId": "phone-id",
  "fields": ["display", "enabled", "showWhilePaused", "artwork", "playbackProgress", "actionButtons"] }
```

This initializes every selected setting from the source's last report. For **all settings**, copy `supportedFields` into `fields`. For a subset, send only the chosen keys, for example `["enabled", "artwork"]`. At least one field is required, every field must be supported by all participants, and duplicate or unknown fields are rejected. All means all currently supported settings; new capabilities are not automatically added later. Omitting `fields` preserves legacy display-only sync.

Instead of a source device, use `initialSettings` with a value for every selected field, for example `{ "enabled": true, "baseVersion": 0, "fields": ["artwork", "showWhilePaused"], "initialSettings": { "artwork": false, "showWhilePaused": true } }`. Unselected initial values are ignored. Legacy display-only callers can still use `initialDisplay: "creator"`. Send exactly one of `sourceDeviceId`, `initialDisplay`, or `initialSettings`. Selected values must be supported by every participant.

To change the selection, send another enabled request using the latest `syncVersion` and chosen starting source. **Current shared settings** in the desktop dialog retains existing shared values; to add a field it does not yet contain, select a device that supplies its starting value. Every enabled configuration assigns a new preference version, so fetch settings before editing again. Disabling uses only `{ "enabled": false, "baseVersion": <syncVersion> }` and leaves each client's current local settings intact. The response has the same shape as discovery; refresh discovery and settings when notified.

`baseVersion` is the service's `syncVersion`, separate from its preference version. A stale selection returns `409 sync_conflict`; refresh discovery instead of silently reapplying it. An enabled selection covers all paired devices reporting that ID, including a later compatible participant. It is not an independent link between each possible device pair. Only a client reporting the service (or the native desktop interface) may change its selection.

Revoking a device removes its inventory. If a service disappears from a client, it ceases to be a participant after the next report. Sync pauses for the whole service when no browser/phone overlap remains, a selected field loses support, or a selected display choice becomes incompatible. Paused services are absent from the settings API. The opt-in and field selection remain remembered: sync resumes if compatibility returns, or can be reconfigured using the latest `syncVersion`. Old saved preferences alone never imply an inventory or opt-in.

## Shared service settings

The service's **preferences** are synchronized. Neither media sessions nor the actual creator/title text are synchronized by the settings API. Fields mean:

| Field | Type | Meaning |
|---|---|---|
| `display` | `app`, `creator`, `title` | Text shown in Discord's compact activity bar. |
| `enabled` | boolean | Enable or disable this service's activity. This does not disable settings sync. |
| `showWhilePaused` | boolean | Keep activity visible when playback pauses. |
| `artwork` | boolean | Show cover art or a video thumbnail. |
| `playbackProgress` | boolean | Show media elapsed/remaining time when enabled; when disabled, Discord falls back to its green app/activity elapsed timer. |
| `actionButtons` | boolean | Show watch, play, or view-channel buttons. |

`true` enables/shows the named feature and `false` disables/hides that feature. Turning playback progress off does not hide all Discord timers: the green timer measures app/activity duration rather than media position and may continue while paused when `showWhilePaused: true`. This behavior applies to both the browser extension and mobile app. Each client maps these preferences to its own formatter and local storage. Only fields in `syncFields` are shared; absent fields stay local, with no default substituted. The existing display adapters are:

| `display` | Meaning | Browser preference | Mobile preference |
|---|---|---|---|
| `app` | Application / service name | `app` | `app_name` |
| `creator` | Creator, artist, streamer, or series | `artist` | `artist` |
| `title` | Video, song, stream, or episode title | `track` | `track` |

Clients retain their current title/app fallback when creator metadata is unavailable. Mobile's `season_episode` choice stays local; omit `display` and `displayChoices` while it is selected and report the other supported settings. Do not silently convert it into a shared choice.

`GET /v1/settings`:

```json
{
  "revision": 7,
  "services": {
    "youtube": { "display": "creator", "enabled": true, "showWhilePaused": true,
      "artwork": false, "playbackProgress": true, "actionButtons": false, "version": 6 },
    "youtube-music": { "artwork": true, "actionButtons": true, "version": 7 }
  }
}
```

Only the caller's reported services with active settings sync appear. Only selected fields and `version` are included per service; `display` is absent if it was not selected. Before reporting services or enabling sync, the response is empty. Enabling sync creates preferences from the selected starting source, so first read that service's nonzero preference version before editing. The desktop shows discovered browser/phone matches with an **Enable sync** action; it starts with no service rows.

`PATCH /v1/settings` writes only locally changed, selected fields:

```json
{
  "changes": {
    "youtube": { "artwork": true, "showWhilePaused": false, "baseVersion": 6 }
  }
}
```

The response is a complete settings snapshot plus `conflicts`:

```json
{
  "revision": 8,
  "services": {
    "youtube": { "display": "creator", "enabled": true, "showWhilePaused": false,
      "artwork": true, "playbackProgress": true, "actionButtons": false, "version": 8 },
    "youtube-music": { "artwork": true, "actionButtons": true, "version": 7 }
  },
  "conflicts": []
}
```

Each changed service receives a new, increasing version from the server. Omitted fields remain unchanged; send at least one setting and the required `baseVersion`. Explicit `false` is a value, never an omission. Null is treated as omitted; do not use it to reset a preference. A stale `baseVersion` cannot overwrite a different value: that whole service is listed in `conflicts`, and its current settings are returned. Concurrent edits to different fields of the same service can therefore conflict. Other valid, nonconflicting services in the same request are applied. Retrying already-applied values is idempotent. Invalid input, unknown fields, or a write outside `syncFields` rejects the entire batch; at most 128 services can be changed per request. `revision` is an overall counter; use each service's `version` for edits.

Client sync procedure:

1. After pairing, connect to the event stream before the initial sync, then report the client's full installed service inventory with current local choices. Fetch service discovery, offer eligible browser/phone matches, and let the user select which to sync and which starting setting to use. Never auto-enable on a match. Fetch settings and adopt preferences only for enabled services.
2. Persist a per-service baseline, selected fields, and version alongside the pairing. Mark explicit local user edits dirty only for selected fields, with the version on which the edit was based. Retain that dirty change while offline.
3. On a revision event, fetch discovery and settings. Push dirty changes only for currently enabled services with their saved base versions, then apply the returned snapshot. A conflict adopts the server choice and can show a small conflict notice. Do not automatically resubmit a conflict with a new version. If a service becomes disabled/unshared, discard its queued shared writes, retain the current local choice, and leave it independent until sync is enabled again. Report inventory again only when local inventory or reported choices change, not for every revision event: each inventory report itself increments the revision.
4. When `syncFields` changes, discard dirty writes for deselected fields and adopt the new baseline for newly selected fields. Never write an absent snapshot field into local preferences. Applying a remote value must not create a dirty edit. If a user edits while a request is in flight, preserve that newer local edit; do not overwrite it when the earlier response arrives. Serialize sync passes and report updated local settings after applying them successfully.
5. Persist the new baseline only after applying its values successfully. A `401` means the device must pair again. Retain local preferences when Link is unavailable.

The server's edit-time preference wins on a stale conflict. It does not rely on device clocks. Writes to unreported, unshared, or sync-disabled services return `409 sync_not_enabled`; writes to unselected fields return `409 field_not_synced`; display values outside the common choices return `400 unsupported_display`. A client cannot access another service merely by knowing its ID. Settings sync works independently of Discord or the publishing lease. Only the six listed preferences are shared; credentials, account tokens, priority rules, and other local settings are outside this contract. See [CONNECTING.md](CONNECTING.md) for applying visibility preferences to each publisher.

## Change events

When `/v1/info` advertises `settings-change-events`, an authenticated `GET /v1/events` returns a `text/event-stream`. Its first event arrives immediately, even when nothing has changed:

```text
id: 7
event: revision
data: {"serverId":"<stable ID>","revision":7}

```

The stream sends another revision after a durable settings edit, inventory report, sync selection, or device revocation, including edits made in the desktop window. Revisions are global hints rather than a list of changes; multiple updates can be coalesced. The event contains no settings or service data. Fetch `/v1/services` and `/v1/settings` to obtain the caller's filtered state. Connecting before the initial fetch and refreshing on each event closes the gap between the initial snapshot and later changes. A reconnect always starts with the latest revision; individual missed events are not replayed and `Last-Event-ID` is not required.

Link sends `: heartbeat` comments every 120 seconds when idle. Use a read timeout longer than that interval and reconnect with backoff after a network failure. Each paired device may have one active stream; opening another closes the previous stream for that same device without affecting other devices. A revoked device receives `event: revoked` with `data: {}` and the stream closes; a new request with its token returns `401`. Stop sync and pair again while retaining local preferences. If the event's `serverId` differs from the pinned identity, close the connection and require pairing again.

## Desktop presence ownership

Only a paired `browser` client connecting over loopback may publish. A paired browser on another machine can sync settings but cannot publish through this desktop. Mobile devices only sync settings. The fixed public Discord application ID is the browser application's `1549066134706323548`; the client cannot select arbitrary application IDs.

### Acquire before handing over

`POST /v1/presence/lease` with `{}` first verifies that local desktop Discord is reachable. It returns `503 discord_unavailable` without reserving ownership if Discord cannot be connected, or `409 publisher_busy` if another lease already owns publishing.

```json
{ "leaseId": "<opaque lease ID>", "expiresAt": "2026-10-26T12:00:30Z", "heartbeatSeconds": 10, "fallbackGraceSeconds": 5 }
```

Acquiring a lease does **not** publish an activity. After acquiring it, the browser must successfully clear its existing headless session and cancel its pending headless updates/renewals **before** sending a desktop presence. If headless cleanup fails, release the lease and retain that transport's recovery behavior. Do not let both publishers run concurrently.

The owner can renew a reservation with `POST /v1/presence/lease` and `{ "leaseId": "<current ID>" }`; this verifies Discord again. An unqualified `{}` does not recover an unknown existing lease, even for the same device. If the acquisition response is lost, wait for expiry or recover using the known lease ID.

### Publish and heartbeat

`PUT /v1/presence`:

```json
{
  "leaseId": "<lease ID>",
  "sequence": 0,
  "intent": {
    "name": "YouTube",
    "type": "watching",
    "details": "The current video title",
    "state": "Asmongold TV",
    "statusDisplayType": "state",
    "detailsUrl": "https://www.youtube.com/watch?v=abcdefghijk",
    "timestamps": { "start": 1793016000, "end": 1793016600 },
    "assets": {
      "largeImage": "https://i.ytimg.com/vi/abcdefghijk/hqdefault.jpg",
      "largeText": "The current video title"
    },
    "buttons": [{ "label": "Watch on YouTube", "url": "https://www.youtube.com/watch?v=abcdefghijk" }]
  }
}
```

The response is a renewed lease with 30 seconds of ownership. `sequence` starts at zero and increases with every update and heartbeat; delayed or repeated sequences return `409 stale_sequence` and do not extend ownership. Send the latest full intent every 10 seconds, even when it has not changed. A heartbeat does not need to change content. Send `intent: null` to clear the current presence while retaining the link and lease.

This endpoint acknowledges **acceptance**, not display on Discord. A background publisher coalesces changes, sends ordinary updates at most once every five seconds, and clears immediately on the next one-second tick. It retries after Discord reconnects while the lease remains alive. `GET /v1/status` returns `discordConnected`, `message`, `deviceId`, `expiresAt`, `details`, and `published` so the browser can distinguish accepted from successfully published content. Status is available to any paired client and can include the browser's current title.

Presence fields:

- `name` and `details`: required, nonempty, up to 128 UTF-8 bytes. `state`: optional, up to 128 UTF-8 bytes. No control characters.
- `type`: `playing`, `listening`, `watching`, `competing`; `streaming` is accepted and maps to `watching` because Discord's `SET_ACTIVITY` does not support the streaming type.
- `statusDisplayType`: `name`, `state`, or `details`. The browser's existing presence formatter remains responsible for choosing the correct field for its layout. Link preserves that choice.
- `timestamps.start` / `end`: optional Unix seconds, nonnegative, with end not before start. While paused or with `playbackProgress: false`, omit media-derived playback timestamps; an app/activity start timestamp may remain without an end timestamp for the green elapsed timer. Do not send Android SDK millisecond values here.
- Optional HTTPS links: `detailsUrl`, `stateUrl`; `assets.largeImage`, `largeUrl`, `smallImage`; each up to 256 characters and without embedded credentials. `largeText` / `smallText` are optional, up to 128 UTF-8 bytes. The server does not fetch these links.
- Up to two `buttons`, each with a nonempty `label` up to 32 UTF-8 bytes and a valid HTTPS `url`.
- Unknown top-level intent fields are ignored. Existing browser `source` and `streamUrl` fields therefore need not be sent. Private package code and page data do not belong in this API.

### Release and fallback

`DELETE /v1/presence/lease/<lease ID>` clears the activity, closes the Discord IPC connection, and returns `204`. It is safe to resume headless publishing once this succeeds. The owner must release on a normal extension disconnect. Releasing an already-expired lease is harmless; trying to release another live lease is rejected.

If Link cannot be contacted, stop sending desktop requests. Wait **35 seconds after the most recent in-flight desktop request finishes or times out** before resuming headless publishing: the 30-second lease plus the five-second cleanup grace. Waiting only until an older acknowledged expiry is unsafe when a newer heartbeat reached the server but its response was lost. Use a monotonic client timer, bounded request timeouts, and cancel requests before starting this wait. A confirmed `lease_lost` means the lease is already cleared; reacquire or fall back. Link never restores leases or media after restarting.

On server lease expiry, device revocation, app exit, or lease release, Link clears presence and disposes its connection. Discord can still take time to reflect the clear remotely. The browser should keep one serialized publisher state machine responsible for handoff, reconnects, and fallback. Settings sync must not start a second publisher.

## Browser and Android integration notes

The browser needs the relevant HTTP host permission (loopback for desktop publishing). Keep requests in trusted extension code. The API responds to extension preflights with an exact `Access-Control-Allow-Origin`, allowed methods/headers, and `Access-Control-Allow-Private-Network: true`. Browser-specific local-network permission prompts or policies still apply; this server does not bypass them. Ordinary HTTPS website origins and `Origin: null` are denied. Firefox's installation-specific origin requires pairing that installation.

Android needs explicit permission to use HTTP with the chosen private LAN endpoint under its network-security policy. Restrict accepted addresses to the intended local network and verify the pinned server identity before sending a token. Store the pairing token privately (for example using the app's existing Keystore-backed storage). Do not route the existing mobile presence publisher to this server: the initial mobile integration is settings sync only. Pairing and sync should happen only while the relevant app process/background work is active; the server cannot wake the phone app.

## Errors

Errors produced by Link use `{ "error": "code", "message": "explanation" }`. Malformed JSON / binding failures return `400`, unsupported content types may return `415`, and oversized bodies return `413`.

| HTTP | Common codes | Client behavior |
|---|---|---|
| 400 | `invalid_services`, `invalid_sync`, `source_required`, `unsupported_display`, `unsupported_field`, `invalid_settings`, `invalid_presence`, `invalid_sequence`, `invalid_name`, `invalid_kind` | Fix the request; do not retry unchanged. |
| 401 | `invalid_code`, `unauthorized` | Check the code or pair again. |
| 403 | `origin_denied`, `invalid_origin`, `local_network_only`, `desktop_browser_only` | Fix origin, address, or client capability. |
| 404 | `service_not_reported` | Report the service before changing its sync selection. |
| 409 | `sync_not_enabled`, `field_not_synced`, `service_not_shared`, `sync_conflict`, `publisher_busy`, `lease_lost`, `stale_sequence`, `device_limit`, `service_limit` | Refresh discovery and the field selection, or resolve ownership, sequence, or capacity. Settings conflicts instead return 200 with a conflicts list. |
| 429 | `rate_limited` | Observe `Retry-After: 60`. |
| 500 | `server_error` | Retain local preferences and retry later. |
| 503 | `discord_unavailable` | Keep the current publisher; retry acquiring after desktop Discord is available. |
