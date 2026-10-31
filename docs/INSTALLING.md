# Windows installation and releases

## Installing

Use the `ChudPresence-Link-<version>-win-x64-Setup.exe` release on an Intel/AMD Windows PC, or the `win-arm64` installer on Windows on ARM. Windows 10 version 1809 or later is required. The installer includes the .NET and Windows App SDK runtimes and works offline.

Setup installs for the current account in `%LOCALAPPDATA%\Programs\ChudPresence Link`, with a Start menu shortcut and an optional desktop shortcut. Administrator rights are not required. Link keeps its **Start with Windows** option inside the app and does not enable it during setup. Phone connections may still require the manual firewall rule described in the README.

Run a newer installer to upgrade the existing installation. Choose **Exit Link** from the app or its tray menu first: closing its window keeps the process running. Setup and uninstall check Link's single-instance mutex and ask you to exit before replacing files. Uninstall through **Installed apps** in Windows Settings.

Pairings, preferences, and appearance are stored in `%LOCALAPPDATA%\ChudPresence Link`, separately from program files. Upgrades and uninstall retain them. Uninstall removes Link's startup registration only when it points to the installed executable; a registration pointing to a portable copy is preserved. Portable and installed copies share the default data directory and only one can run per login session. If a portable copy previously registered startup, open the installed app and switch **Start with Windows** off and on to move that registration.

Releases are unsigned. Code signing is a separate release step and is not configured in this repository.

## Building

Install the .NET 10 SDK, Windows SDK 10.0.26100 or newer, Node.js, and [Inno Setup 6.3 or newer](https://jrsoftware.org/isdl.php). The build locates `ISCC.exe` on PATH or in the normal Inno Setup 6/7 installation directories. A custom compiler location can be supplied explicitly:

```powershell
./scripts/Build-Installer.ps1
./scripts/Build-Installer.ps1 -Runtime win-arm64
./scripts/Build-Installer.ps1 -InnoCompiler 'C:\Tools\Inno Setup 6\ISCC.exe'
```

Each command checks the API definition, publishes into a clean staging folder, verifies runtime and XAML resources, builds the portable ZIP, and compiles setup from that same payload. The app, API, and installer share the `Version` property in `Directory.Build.props`. Output in `dist`:

- `ChudPresence-Link-<version>-<runtime>-Setup.exe`
- `ChudPresence-Link-<runtime>.zip` and its extracted folder
- `ChudPresence-Link-<version>-<runtime>-SHA256SUMS.txt`

For portable testing alone, run `./scripts/Publish.ps1`. Generated runtime folders are replaced on each successful publish. Setup's application ID must stay stable across releases so Windows recognizes upgrades.

### GitHub Releases

Each milestone pushed to `main` runs the Windows release workflow. It runs the project checks, builds the x64 and ARM64 installers and portable ZIPs, and publishes them with SHA-256 checksum files to GitHub Releases. The workflow can also be run manually from the Actions tab on `main`. It does not publish development branches.

The app version comes from `Directory.Build.props`. Each snapshot gets a distinct release tag in the form `v<version>-build.<full-commit-sha>`, so another milestone can be released without overwriting an earlier one even if the version has not changed. Increase the app version for an actual version change. If a release for the same snapshot already exists, inspect it instead of overwriting it. A build for an older commit stops if a newer milestone reaches `main` before it publishes. Releases are unsigned; cross-building ARM64 on the x64 runner does not test ARM64 execution.

## Validation

```powershell
dotnet run --project tests/ChudPresence.Link.Checks -c Release
dotnet run --project tests/ChudPresence.Link.Windows.Checks -c Release
node --test tests/link-client.test.mjs
./scripts/Test-Installer.ps1 -Installer './dist/ChudPresence-Link-0.4.1-win-x64-Setup.exe'
```

The installer smoke test uses an isolated installation directory and Start menu group, checks upgrades and uninstall, and preserves logs under `artifacts/installer-smoke`. It refuses to replace an existing installed copy. Native launch and tray behavior should also be checked on the target Windows architecture. Building an ARM64 package on x64 does not verify ARM64 execution.

## Unattended setup

```powershell
./ChudPresence-Link-0.4.1-win-x64-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-
```

Add `/TASKS=desktopicon` to create the optional desktop shortcut. Silent setup does not launch Link. Exit the app first; a running instance causes unattended setup or uninstall to fail without modifying its files. The installed `unins000.exe` supports `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART` for unattended removal.
