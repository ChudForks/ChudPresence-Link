# Connect the browser extension or mobile app

Link 0.4.0 supports six per-service settings: Discord bar display, service enabled, show while paused, artwork, playback progress, and action buttons. The user can sync all supported settings or a selected subset for each service. This guide describes the adapters to add to those apps later; this change only updates the Link repository.

Use [API.md](API.md) for the complete protocol and [openapi.json](openapi.json) for schemas. [link-client.mjs](../examples/link-client.mjs) is a dependency-free reference HTTP client, not an adapter already installed in either app.

## 1. Connect and pair

1. Run Link on Windows. Use its **Extension Connection** address for an extension on that PC, or **Phone Connection** for the phone on the same private network.
2. Read `GET /v1/info`. Require `protocolVersion: 1`; check for `service-settings-sync` and `selective-settings-sync` before using the new fields. An older Link server supports display-only sync.
3. Click **Pair browser** or **Pair phone** in Link. Submit the code to `POST /v1/pair` with the matching kind and a device name.
4. Privately save the endpoint, `serverId`, `deviceId`, and token. Verify `/v1/info` against the pinned identity before sending the token on a new connection. Send `Authorization: Bearer <token>` on protected requests.

Browser requests must run in trusted extension code with permission for the Link HTTP endpoint. Let the browser supply its extension Origin. Android requests omit Origin and need narrowly scoped cleartext HTTP/network permissions for the private LAN endpoint. Keep tokens out of page scripts, URLs, logs, and exported settings.

## 2. Report actual local capabilities and values

Send a full inventory using `PUT /v1/services` after pairing, when services are installed/removed, and when reported local settings change. Build each entry from the client's existing settings store:

```json
{
  "services": [{
    "id": "youtube",
    "name": "YouTube",
    "display": "creator",
    "displayChoices": ["app", "creator", "title"],
    "enabled": true,
    "showWhilePaused": false,
    "artwork": true,
    "playbackProgress": true,
    "actionButtons": false
  }]
}
```

Omit any setting the app cannot both read and apply for that service. A boolean set to `false` still advertises support. Do not fill in missing settings with defaults just to match this example. Keep disabled services in the inventory with `enabled: false`; settings sync must continue while their activities are disabled.

Agree on stable service IDs across adapters. For example, map Android's `youtube_music` to `youtube-music` on the wire. Link discovers IDs from clients; it does not have a built-in catalog.

Map the display values as follows:

| Link | Browser | Mobile |
|---|---|---|
| `app` | `app` | `app_name` |
| `creator` | `artist` | `artist` |
| `title` | `track` | `track` |

If the mobile display is `season_episode`, leave it local and omit `display` and `displayChoices` from that report. The same service can still advertise and sync its supported boolean settings. Re-report if the user later chooses a mappable display.

## 3. Let the user choose what to sync

Read `GET /v1/services`. Offer services with `shared: true` and a nonempty `supportedFields`. A match requires a browser and a phone with the same service ID. Reports never automatically enable sync.

For **All supported settings**, send the service's current `supportedFields` as `fields`. For **Selected settings**, send a nonempty subset. The desktop Link window offers the same choice under **Enable sync** and **Settings**. Choose which device's last reported settings should initialize the selected fields:

```js
// `client` is a paired LinkClient from examples/link-client.mjs.
const discovery = await client.services();
const service = discovery.services.find(item => item.id === 'youtube');
await client.setServiceSync(service.id, {
  enabled: true,
  baseVersion: service.syncVersion,
  sourceDeviceId: chosenDeviceId,
  fields: service.supportedFields, // Or ['enabled', 'artwork'].
});
const settings = await client.settings();
```

The source must be a participant in that service and supply compatible values for every selected field. Alternatively send `initialSettings` with explicit values for the selected fields; see [API.md](API.md). Send exactly one initialization option.

Omitting `fields` means legacy display-only sync. Existing opt-ins stay display-only after upgrading. **All supported settings** selects the fields available now; later capabilities require the user to update their selection.

`syncEnabled` controls whether settings are shared. The shared setting `enabled` controls whether the service publishes an activity. These have separate meanings: a service can be disabled while its settings keep syncing.

To stop sharing settings, send `{ enabled: false, baseVersion: service.syncVersion }` to the sync endpoint. Keep the client's current local values. A saved opt-in can also pause if a device loses support or the browser/phone overlap disappears; `syncRequested: true` and `syncEnabled: false` indicate that state.

## 4. Apply only selected fields and publish local edits

Only services with active sync appear in `GET /v1/settings`. Each contains a service `version` and only its selected settings. A boolean-only selection can return:

```json
{ "revision": 12, "services": { "youtube": { "enabled": false, "artwork": true, "version": 12 } } }
```

This changes service enablement and artwork; it leaves the client's Discord bar, paused behavior, progress, and buttons as they are. Check for field presence rather than truthiness:

```js
// Adapter pseudocode: use the app's existing persistence and change listeners.
for (const field of service.syncFields) {
  if (Object.hasOwn(remote, field)) {
    await writeLocalPreference(service.id, field, remote[field], { origin: 'link' });
  }
}
// Recompute the current activity after successfully applying the changes.
```

Remote changes must not enter the local dirty queue. Save the baseline/version only after local application succeeds. On explicit user edits, queue only changed fields currently selected for sync, preserving their original baseline version:

```js
await client.changeSettings({
  youtube: { baseVersion: baseline.version, artwork: false, showWhilePaused: true },
});
```

Use `syncVersion` for changing the sync selection and the settings `version` for preference patches. A preference version covers the whole service. Stale edits return the authoritative snapshot and a `conflicts` array; apply that result without resubmitting the conflict under a new version. Nonconflicting services in the batch still succeed.

Preserve edits made while a request is in flight. Serialize sync passes, retain dirty changes while offline, and discard writes for fields deselected or services whose sync stopped. When a selection is changed or reenabled, fetch a fresh baseline before writing. Do not reset local values for fields absent from a snapshot.

Connect an authenticated fetch-based SSE reader to `GET /v1/events` before the initial inventory/settings sync. Refresh discovery and settings on each revision hint. Standard browser `EventSource` cannot attach the required bearer header; do not put the token in the event URL. Reconnect with backoff and allow idle reads longer than the 120-second heartbeat. Do not report inventory on every event: only report when the inventory or its values actually changed, since each report creates another revision.

## 5. Apply the visibility settings in each publisher

Link shares preferences. The browser/mobile adapter applies them to its existing formatter and reevaluates the current activity immediately after a settings change, even when playback has not changed:

| Setting | Required client behavior |
|---|---|
| `enabled: false` | Clear this service's existing activity and stop publishing it; continue reporting and syncing its settings. |
| `showWhilePaused: false` | Clear the activity on pause. |
| `showWhilePaused: true` | Retain activity while paused. Keep its media text and permitted artwork/buttons; omit running media playback timers. The green app/activity elapsed timer may continue. |
| `artwork: false` | Omit cover art/video thumbnails and their related image text/links from the generated presence. |
| `playbackProgress: false` | Omit media playback progress (elapsed/remaining playback time). Discord falls back to its green app/activity elapsed timer on both the browser extension and mobile app; this does not hide all timers. |
| `actionButtons: false` | Omit watch/play/channel buttons from the generated presence. |

The green elapsed timer measures app/activity duration rather than media position and may keep running while paused when `showWhilePaused: true`. For Link's desktop transport, omit media-derived playback timestamps when progress is off; an app/activity `timestamps.start` may remain without `timestamps.end` for the elapsed timer.

When a preference is true, use the client's existing formatter and available media metadata. When playback resumes with `playbackProgress: true`, rebuild media playback timestamps from playback position. Remote preferences must also affect any fallback publisher the client already uses.

For a browser owning a Link desktop lease, send `intent: null` when the activity should be cleared. Keep increasing sequences and heartbeating while the lease is owned; a visibility change does not require ending settings sync. The presence endpoint receives an already formatted intent, so it cannot infer service IDs or playback state from the activity name and does not apply these preferences automatically.

The phone keeps its existing mobile publisher and applies the selected preferences there. It does not acquire a desktop lease or transfer phone media to Link. For browser desktop handoff, keep the single-publisher ownership and 35-second fallback procedure in [API.md](API.md).

## Integration acceptance checks

- Report a browser and phone with the same ID and different starting values. Choose the source and verify that only selected settings converge.
- Select all settings, toggle each one from Link and each client, and verify the active activity updates without waiting for a media change.
- Select only artwork/buttons and verify the other local preferences remain different on each device.
- Sync `enabled: false` and verify settings can still be changed and synced while the activity is hidden.
- On both the browser extension and mobile app, turn playback progress off and verify that Discord switches from media playback progress to its green app/activity elapsed timer.
- Pause and resume with both paused-visibility choices. Verify that paused activities never show running media playback timers; the green app/activity elapsed timer may continue when the activity stays visible, including with playback progress off.
- Test explicit `false`, boolean-only snapshots, offline edits, stale conflicts, selection changes, reconnection, and removed devices/capabilities.
- Connect an older display-only client and verify that new preferences stay local until every participant reports support and the user selects them.
