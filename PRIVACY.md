# ChudPresence Link Privacy Policy

Effective date: November 11, 2026

Publisher: ChudForks

Application: ChudPresence Link for Windows

This policy explains how ChudPresence Link ("Link") accesses, uses, stores, and shares information. It covers Link distributed through Microsoft Store, an installer, a portable package, or this project's source code. Browser extensions, mobile clients, Discord, Microsoft Store, and GitHub have their own privacy practices; this policy describes their interaction with Link, rather than their independent operation.

## How Link works

Link runs a local HTTP server on your Windows PC. Compatible browser and mobile clients can pair with it, report their service settings, and synchronize settings you select. A paired browser on the same PC can also send activity to the signed-in Discord desktop client.

Link does not require a ChudPresence account, operate a publisher-hosted cloud service, or automatically upload your Link data to ChudForks. The application includes no advertising, advertising identifiers, analytics telemetry, tracking cookies, or automatic crash-report uploads. ChudForks does not receive, sell, rent, or use your local Link data for targeted advertising through the application.

## Information Link accesses and why

- **Pairing and device information.** Link receives a client-supplied device name and device type (browser or mobile), generates server and device identifiers, and records pairing and last-seen times. For browser clients, it also records the extension origin used when pairing. It uses this information to identify paired clients, restrict access, show connection information, and let you remove devices.
- **Pairing credentials.** Link generates temporary pairing codes and random device access tokens. It returns the original access token to the paired client and stores a SHA-256 hash of that token on the PC to authenticate later requests. The client is responsible for storing its original token. These are Link credentials, separate from Discord credentials.
- **Client service inventories and settings.** Paired clients report service identifiers and names, supported display choices, current supported setting values, and inventory report times. The settings can include the Discord display choice, service enabled, show while paused, artwork, playback progress, and action buttons. Link stores these reports, your sync selections, shared preference values, and revision numbers to discover matching services, apply selected changes, and resolve conflicting updates. An inventory describes services reported by a compatible client; Link does not scan your PC for a general list of installed applications.
- **Browser activity.** When a compatible paired browser uses desktop publishing, Link receives the activity name and type, details such as a media title, state such as a creator or artist, display choice, optional playback timestamps, artwork URLs and descriptive text, and optional action labels and links. It uses this information to publish the requested Discord activity. Titles, creator text, and URLs may reveal what you are watching or listening to and can contain personal information. Link does not independently read browser history, tabs, page contents, or phone media; it processes the information clients submit.
- **Local connection information.** Link reads the PC's local network addresses to show connection URLs. It processes incoming IP addresses, request host and origin information, authentication headers, and request data to serve the API, enforce local-network and browser-origin restrictions, and limit requests. IP addresses are not saved in Link's application state file, and Link does not use them to determine your geographic location.
- **Local preferences and Windows integration.** Link reads Windows appearance settings and saves your chosen appearance. If you enable Start with Windows, it saves startup configuration through Windows. For an unpackaged installation, the current user's startup registry entry contains the executable path, port, and data-directory path. If you press a Copy button, Link writes the selected connection address or pairing code to the Windows clipboard. Windows clipboard history, clipboard sync, and other applications with clipboard access may retain or access that copy.
- **Discord connection replies.** Link reads local Discord protocol replies to establish the connection and check whether activity requests succeeded. It does not request your Discord password, account token, or OAuth credentials, and does not use the connection to retrieve your messages, contacts, or friends list.

Link does not request access to your camera, microphone, contacts, payment information, or Windows location service. It does not use activity data to build advertising profiles or make automated decisions about you.

## What is shared and with whom

**Paired clients.** Link returns shared setting values to eligible paired clients for the settings selected for synchronization. Service discovery also exposes matching service information, participating device identifiers, names and types, inventory report times, supported settings, and the participants' reported setting values. These inventory values can include settings you have not selected for synchronization. Pairing and inventory reporting allow this discovery even when settings sync is off; turning sync off stops shared settings updates, but does not revoke access or erase reported inventories.

Any authenticated paired client can request publishing status, including the current publisher's device identifier, lease expiry, connection and publishing status, and the activity's details text, which may contain the current media title. Pair only clients you trust. Phone media is not forwarded to desktop Discord by Link, and the settings-sync API does not distribute the full browser activity payload.

**Other local-network devices.** The server-information endpoint is accessible without pairing to permitted local-network peers. It reveals the application name and version, protocol capabilities, and generated server identifier. Authentication is required for the settings, inventories, and publishing-status endpoints.

**Discord and people who can see your activity.** A paired browser on the same PC can acquire a publishing lease and submit activity. Link sends that activity, its public Discord application identifier, and the Link process identifier to the signed-in Discord desktop client over a local named pipe. Discord can then process the activity through its service and display it to others according to Discord's features and your activity/privacy settings. Local transmission to Discord does not mean that the activity stays on your PC. Link's settings-sync selection does not itself enable or disable Discord publishing.

Link passes artwork and action URLs to Discord; it does not itself download that artwork or open those links. Discord or a viewer's software may contact the referenced services when resolving artwork or following links. Those services may receive the URL and ordinary connection information from the software making the request. Review the [Discord Privacy Policy](https://discord.com/privacy) and the policies of services referenced in your activity.

**Microsoft, GitHub, and companion applications.** Microsoft can process Store installation, update, licensing, and diagnostic information under the [Microsoft Privacy Statement](https://www.microsoft.com/en-us/privacy/privacystatement). Visiting this policy on GitHub, downloading releases, or submitting an issue involves GitHub under the [GitHub General Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement). Browser and mobile clients can retain their own settings, tokens, and activity and may communicate with other services independently of Link. Link does not control those services' retention, international processing, or disclosures.

**Support you choose to provide.** If you submit a support or privacy question, ChudForks can see the information you provide, including your GitHub account name and issue content. GitHub issues are public. Link does not automatically attach your state file, credentials, activity, or logs to a support request.

## Where information is stored and how it is protected

The default data directory is `%LOCALAPPDATA%\ChudPresence Link` under your Windows account. `link-state.json` stores the server identifier, paired-device records and token hashes, client inventories, sync selections, shared preferences, and revisions. `appearance.txt` stores the appearance choice. The `--data-dir` launch option changes this directory. Windows may redirect app-data storage to a package-specific location for a Microsoft Store/MSIX installation.

Link saves its state as a local JSON file using temporary-file replacement. It does not encrypt that file or the appearance file itself; access depends on Windows permissions and any device or disk encryption you enable. People or software with access to your Windows account, data directory, or backups may be able to read or change it. Link does not create a cloud backup; any backup or synchronization software you configure can make additional copies.

Pairing codes are single-use, are valid for up to five minutes, and are held in memory. Device tokens are generated from cryptographically random bytes, and only their hashes are stored by Link. API access uses those tokens, browser tokens are bound to their pairing origin, and activity publishing is restricted to paired browser connections from the same PC. The server applies local-address restrictions and request limits.

**Local API traffic uses HTTP without TLS encryption.** Pairing codes, original device tokens, settings, metadata, and browser activity sent through that API are not encrypted by Link in transit. Someone able to observe or alter the connection may read the traffic or misuse credentials. Token hashing does not encrypt the connection. Use trusted private networks and devices, and do not expose Link through port forwarding, public proxies, or Internet tunnels. No security measure guarantees complete protection.

Link does not maintain an application analytics or activity-history log. Startup failures may be shown locally or written to standard error. Windows and third-party software may separately record diagnostic information under their own settings and policies.

## Retention and deletion

Saved pairings, inventories, preferences, sync selections, and appearance remain until changed or deleted; Link does not apply an automatic age-based deletion schedule. Offline devices retain their last reported inventories. Removing a service or turning sync off can leave saved preference values and sync selections for future use.

Removing a device revokes its token and removes its device record and reported inventory from Link. It also clears any desktop publishing lease owned by that device. Shared service preferences may remain. Revocation does not erase copies already held by that client or other recipients.

Pairing codes, original credentials while being processed, current activity, publishing leases, and request-processing information are handled in memory rather than saved as activity history. Publishing leases normally expire after 30 seconds without renewal. Link attempts to clear the desktop activity when a lease is released or expires, its owner is removed, or you exit Link, and closes its Discord connection. Clearing current activity does not delete information already processed by Discord, observed by another person, or retained elsewhere.

To erase Link's saved data, exit Link and delete its actual data directory, including `link-state.json` and `appearance.txt`. Start with Windows can be disabled in Link or Windows startup settings. EXE/portable uninstall does not delete the saved data directory. For Store/MSIX installations, Windows reset/uninstall behavior governs package-specific storage; a custom external data directory or separate backups may remain. Removing local data requires pairing clients again and does not delete data stored by Discord, companion clients, clipboard services, or backups.

## Your choices and access

- Review and remove paired devices in Link. Cancel a pairing code to stop its further use, or allow it to expire. Remove an existing device if its token may have been exposed.
- Choose which supported settings to synchronize, change the selection, or turn sync off for a service. Reported inventory and publishing-status access continues until the client is removed or Link stops.
- Control what the browser submits through that client's publishing and activity settings. Remove the paired browser or exit Link to stop its publishing through Link. Use Discord's activity/privacy settings to control visibility through Discord. Hiding artwork, playback progress, or action buttons does not necessarily hide the activity title or every Discord timer.
- Use **Exit Link** or the tray menu's **Exit** to stop the local server and publishing. Closing or minimizing the window keeps Link running in the tray. Start with Windows is optional and initially off.
- Inspect your devices and shared settings in the app. To access or copy the full persisted Link record, exit Link and open or copy the JSON file in your data directory. It contains device and credential-related information; keep it private. Delete local data as described above to reset Link.

ChudForks cannot remotely access, correct, export, or erase the Link files on your PC through this application. Depending on applicable law, you may have rights to access, correction, deletion, portability, restriction, objection, withdrawal of consent, or a complaint to a data-protection authority. Local-data controls above let you manage the information Link holds on your PC. For privacy questions about information you have directly provided to ChudForks, use the contact below. Requests about data held independently by Discord, Microsoft, GitHub, or a companion client must be directed to that provider.

## Children

Link does not request an age or date of birth and has no account-registration system. It processes client-supplied information regardless of the user's age. Use of Discord and companion applications remains subject to those services' applicable age requirements. A parent or guardian can stop Link, remove pairings, and delete the local data using the controls above.

## Changes and contact

The current policy is published in this repository's [PRIVACY.md](https://github.com/ChudForks/ChudPresence-Link/blob/main/PRIVACY.md). Changes to the policy will be reflected in its effective date. Check the policy when updating Link or enabling new integrations.

For privacy questions, contact ChudForks through the [ChudPresence Link issue tracker](https://github.com/ChudForks/ChudPresence-Link/issues). Because issues are public, include only the minimum non-sensitive information needed to explain your question. Do not post pairing codes, device tokens, state files, personal device names, private activity, or other confidential information.
