# ChudPresence Link

A native WinUI 3 Windows tray app that links ChudPresence clients on a local network and publishes browser presence through the Discord desktop client. Its Fluent interface follows the Windows theme and accent color, with Mica on supported versions of Windows 11.

The browser extension and mobile app can integrate through the versioned HTTP API documented in [docs/API.md](docs/API.md). Follow [docs/CONNECTING.md](docs/CONNECTING.md) to connect either app. A dependency-free JavaScript example client is provided in [examples/link-client.mjs](examples/link-client.mjs). This repository updates Link only; the existing browser and mobile apps need the documented adapters to use these features.

## Current scope

- Share the Discord bar display choice (**app name**, **creator / artist**, or **video / song title**) and five service preferences: **service enabled**, **show while paused**, **artwork**, **playback progress**, and **action buttons**.
- Each paired client reports its installed services and supported display choices. Link has no built-in service catalog: it finds IDs shared by a browser and phone and offers sync for those services.
- Sync is off until selected for each shared service. Choose **All supported settings** or **Selected settings**, then choose a browser/phone's last reported values to start with. Use **Settings** to change the selection later, and edit each shared value in the service card or a participating client. Unselected settings stay local. **Turn off** stops settings sync; **Service enabled** controls the service's activity separately.
- A setting is offered only when every participant reports support for it. Older clients and existing selections remain display-only. Choosing all includes the settings currently supported by all participants; later additions require a new selection.
- Connected clients can receive revision events for settings, inventory, and sync selection changes, then fetch the latest shared state.
- For example, YouTube set to **Creator / artist** lets both clients display **Asmongold TV** when that is the creator of the current video. Link syncs the choice, not the creator's text or the media playing on the phone.
- Optionally accept a browser's presence intent and publish it through Discord's local IPC connection. A 30-second lease, 10-second heartbeat, sequence checks, and explicit clearing handle ownership and disconnects.
- Pair browser/mobile clients using single-use, eight-digit codes that expire after five minutes. Remove devices from the desktop window to revoke their tokens.

On both the browser extension and mobile app, turning **Playback progress** off switches Discord from media elapsed/remaining time to its green app/activity elapsed timer. It does not hide all timers. The green timer may continue while paused if **Show while paused** keeps the activity visible.

## Install or run on Windows

Run `ChudPresence-Link-0.4.1-win-x64-Setup.exe` for a normal installation. Setup installs for your Windows account without administrator rights, adds a Start menu shortcut, and offers an optional desktop shortcut. It includes .NET and the Windows App SDK. Update by running a newer installer; use **Installed apps** in Windows Settings to uninstall. Exit Link from its window or tray menu before updating or uninstalling.

Installed and portable copies use the same settings location by default. Pairings, preferences, and appearance survive upgrades and uninstall. **Start with Windows** remains opt-in inside the app; uninstall removes that entry when it points to the installed copy. See [docs/INSTALLING.md](docs/INSTALLING.md) for build, silent setup, and validation instructions.

### Build from source

Build a portable package with the .NET 10 SDK and Windows SDK 10.0.26100 (or newer). Windows App SDK and SDK build tools restore through NuGet:

```powershell
dotnet build src/ChudPresence.Link.Windows -c Release
dotnet run --project tests/ChudPresence.Link.Checks -c Release
dotnet run --project tests/ChudPresence.Link.Windows.Checks -c Release
node --test tests/link-client.test.mjs
./scripts/Publish.ps1
# Requires Inno Setup 6.3+ (https://jrsoftware.org/isdl.php):
./scripts/Build-Installer.ps1
```

The installer is written to `dist/ChudPresence-Link-0.4.1-win-x64-Setup.exe`. For a portable run, use `dist/ChudPresence-Link-win-x64/ChudPresence.Link.exe` or extract `dist/ChudPresence-Link-win-x64.zip`. Both packages include .NET and the Windows App SDK; end users do not need to install either runtime. Extract the entire portable ZIP before running it, and keep its DLLs, assets, and resource files beside the executable. WinUI is published as a folder rather than a single executable. Releases are currently unsigned. Use `-Runtime win-arm64` with either build script for Windows on ARM. Publishing replaces that runtime's generated output folder and ZIP with a fresh package.

The window shows `http://127.0.0.1:17879` for the extension on this PC and the computer's local IPv4 addresses for a phone on the same network. A client first reads `GET /v1/info`, then pairs with a code created using **Pair browser** or **Pair phone**. There is no multicast discovery in this version.

Closing or minimizing the window keeps Link in the system tray. Use **Exit Link** or the tray's **Exit** item to stop the server and clear its desktop activity. **Start with Windows** is opt-in and applies to the current Windows account.

Arguments:

```powershell
./ChudPresence.Link.exe --minimized
./ChudPresence.Link.exe --port 17880
./ChudPresence.Link.exe --data-dir 'D:\Link-test-data'
```

Only one copy runs per Windows login session. If phone connections are blocked by Windows Firewall, manually run `scripts/Allow-LocalNetwork.ps1` in an elevated PowerShell. It creates a TCP rule restricted to the local subnet on Private network profiles. The app does not change firewall settings itself. Use an alternate port consistently in the app, clients, and firewall rule when needed.

## Data and networking

State is saved atomically in `%LOCALAPPDATA%\ChudPresence Link\link-state.json`: server identity, client service inventories, per-service sync selections, versioned choices, paired device metadata, and SHA-256 token hashes. Pairing codes, current media, and publishing leases remain in memory. Link never requests Discord passwords, user tokens, or OAuth credentials. It uses the signed-in desktop Discord client with the existing public browser application ID `1549066134706323548`.

Installing/removing a service updates the overlap when that client submits its next full inventory. Removing a device removes its inventory. Settings sync pauses when there is no longer a browser/phone match or a selected setting is unsupported by a participant. Offline devices retain their last reported inventory; discovery includes report timestamps. An existing opt-in is remembered if a service later returns. Preferences saved by the first development build do not create an inventory or automatically enable sync.

The API uses HTTP on the trusted local network. Pairing tokens travel over that network, so this version is intended for a trusted private LAN. It accepts loopback, RFC 1918 IPv4, link-local, and IPv6 ULA peers; public peers and forwarded-client headers are not accepted. DNS hostnames other than `localhost` and ordinary website origins are rejected. Browser tokens are bound to the extension origin used when pairing. Presence publishing additionally requires a loopback browser connection. There is no remote hosting, UPnP, account system, or Internet relay.

Desktop Discord must be open and signed in to acquire a publishing lease. Discord privacy settings and the desktop client's support determine what is ultimately visible. Protocol checks use a fake Discord peer; actual visibility from another Discord account still needs to be verified after extension integration. Phone presence continues to belong to the mobile app.

## Structure

- `src/ChudPresence.Link.Core`: settings, persistence, pairing, presence validation, ownership, and the Discord transport interface.
- `src/ChudPresence.Link.Server`: the versioned HTTP API, network restrictions, CORS, and request limits.
- `src/ChudPresence.Link.Windows`: WinUI 3 XAML window and dialogs, shared Fluent theme resources, native notification-area integration, optional Windows startup, and the server/publisher host.
- `tests/ChudPresence.Link.Checks`: executable behavioral checks, real HTTP integration checks, and a fake Discord named-pipe peer.
- `installer`: per-user Windows setup, upgrade, shortcut, and uninstall configuration.
- `scripts/Build-Installer.ps1`: builds both portable and installer releases plus SHA-256 checksums.

The core and API target `net10.0`; the Windows UI targets `net10.0-windows10.0.26100.0` with Windows App SDK 2.6.4. Windows 10 version 1809 is the minimum OS, with native backdrop effects available on supported Windows 11 versions. Core sync and ownership logic remain independent of the UI framework. Linux packaging, UI, deployment, and testing are outside this release.

Discord's [IPC and SET_ACTIVITY documentation](https://docs.discord.com/developers/topics/rpc) describes the local transport. This app's icon is copied from the existing ChudPresence browser artwork.
