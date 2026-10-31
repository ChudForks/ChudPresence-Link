[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Installer
)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Installer checks require Windows.' }
$installerPath = (Resolve-Path -LiteralPath $Installer).Path
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) { throw 'Pass the path to the installer executable.' }
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{EFD0F081-4EB4-4D49-AC6C-42CB813CA467}_is1'
$runKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$startupName = 'ChudPresence Link'

function Get-LinkInstallation {
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
        try {
            $key = $baseKey.OpenSubKey($uninstallKey)
            if ($key) {
                try {
                    [pscustomobject]@{ Location = $key.GetValue('InstallLocation'); Version = $key.GetValue('DisplayVersion') }
                } finally { $key.Dispose() }
            }
        } finally { $baseKey.Dispose() }
    }
}
if (@(Get-LinkInstallation).Count -ne 0) {
    throw 'A ChudPresence Link installation already exists for this account. Run these checks in a test account to avoid modifying it.'
}

$taskRoot = Split-Path -Parent $PSScriptRoot
$testId = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $taskRoot "artifacts/installer-smoke/$testId"
$installDirectory = Join-Path $testRoot 'installed app'
$dataDirectory = Join-Path $testRoot 'data'
$groupName = "ChudPresence Link Installer Smoke $testId"
$shortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) "$groupName/ChudPresence Link.lnk"
$installedExe = Join-Path $installDirectory 'ChudPresence.Link.exe'
$uninstaller = Join-Path $installDirectory 'unins000.exe'
$statePath = Join-Path $dataDirectory 'link-state.json'
$sentinel = '{"installerSmoke":"' + $testId + '"}'
$installerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($installerPath)
$expectedVersion = [Version]::new($installerVersion.FileMajorPart, $installerVersion.FileMinorPart,
    $installerVersion.FileBuildPart, $installerVersion.FilePrivatePart)
$checkCount = 0
New-Item -ItemType Directory -Path $testRoot, $dataDirectory -Force | Out-Null

function Assert-InstallerCheck([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAILED: $Name. Logs: $testRoot" }
    $script:checkCount++
    Write-Output "PASS $Name"
}
function Invoke-InstallerProcess([string]$Path, [string[]]$Arguments, [string]$Stage) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "$Stage failed (exit $($process.ExitCode)). Logs: $testRoot" }
}
function Install-TestCopy([string]$Stage) {
    Invoke-InstallerProcess $installerPath @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/TASKS=',
        "/DIR=`"$installDirectory`"", "/GROUP=`"$groupName`"", "/LOG=`"$(Join-Path $testRoot "$Stage.log")`""
    ) $Stage
}
function Test-IsolatedRegistration {
    $registrations = @(Get-LinkInstallation)
    return $registrations.Count -gt 0 -and @($registrations | Where-Object {
        $_.Location -and [IO.Path]::GetFullPath($_.Location).TrimEnd('\') -eq $installDirectory
    }).Count -eq $registrations.Count
}
function Uninstall-TestCopy([string]$Stage) {
    if (-not (Test-IsolatedRegistration)) { throw "Refusing to uninstall an unexpected registration. Logs: $testRoot" }
    Invoke-InstallerProcess $uninstaller @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $testRoot "$Stage.log")`""
    ) $Stage
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ((Test-Path -LiteralPath $installDirectory) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
}
function Assert-Uninstalled {
    Assert-InstallerCheck (-not (Test-Path -LiteralPath $installDirectory)) 'uninstall removes installed files and directory'
    Assert-InstallerCheck (-not (Test-Path -LiteralPath $shortcut)) 'uninstall removes the Start menu shortcut'
    Assert-InstallerCheck (@(Get-LinkInstallation).Count -eq 0) 'uninstall removes its registration'
    Assert-InstallerCheck ((Get-Content -LiteralPath $statePath -Raw) -ceq $sentinel) 'uninstall preserves saved user data'
}

$startupKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKey)
$hadStartup = $false
$savedStartup = $null
$savedKind = [Microsoft.Win32.RegistryValueKind]::String
if ($startupKey) {
    try {
        $hadStartup = $startupKey.GetValueNames() -contains $startupName
        if ($hadStartup) {
            $savedStartup = $startupKey.GetValue($startupName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            $savedKind = $startupKey.GetValueKind($startupName)
        }
    } finally { $startupKey.Dispose() }
}
try {
    Install-TestCopy 'install'
    Assert-InstallerCheck (Test-Path -LiteralPath $installedExe -PathType Leaf) 'silent install creates the application'
    Assert-InstallerCheck ([Version]([Diagnostics.FileVersionInfo]::GetVersionInfo($installedExe).FileVersion) -eq $expectedVersion) 'installed executable matches the installer version'
    Assert-InstallerCheck (Test-Path -LiteralPath $shortcut -PathType Leaf) 'silent install creates the selected Start menu shortcut'
    Assert-InstallerCheck (Test-IsolatedRegistration) 'install registers only the isolated directory'

    [IO.File]::WriteAllText($statePath, $sentinel)
    $document = Join-Path $installDirectory 'docs/API.md'
    $documentHash = (Get-FileHash -LiteralPath $document -Algorithm SHA256).Hash
    [IO.File]::WriteAllText($document, 'Installer smoke test: replace this on upgrade.')
    Install-TestCopy 'upgrade'
    Assert-InstallerCheck ((Get-FileHash -LiteralPath $document -Algorithm SHA256).Hash -eq $documentHash) 'upgrade replaces application files in the same directory'
    Assert-InstallerCheck ((Get-Content -LiteralPath $statePath -Raw) -ceq $sentinel) 'upgrade preserves saved user data'
    Assert-InstallerCheck ((Test-IsolatedRegistration) -and (Test-Path -LiteralPath $shortcut -PathType Leaf)) 'upgrade preserves registration and shortcut'

    $startupKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($runKey)
    try { $startupKey.SetValue($startupName, "`"$installedExe`" --minimized --data-dir `"$dataDirectory`"") }
    finally { $startupKey.Dispose() }
    Uninstall-TestCopy 'uninstall'
    Assert-Uninstalled
    $startupKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKey)
    try { Assert-InstallerCheck ($startupKey.GetValueNames() -notcontains $startupName) 'uninstall removes startup belonging to the installed copy' }
    finally { $startupKey.Dispose() }

    Install-TestCopy 'reinstall'
    $portableCommand = '"' + (Join-Path $testRoot 'portable/ChudPresence.Link.exe') + '" --minimized'
    $startupKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($runKey)
    try { $startupKey.SetValue($startupName, $portableCommand) }
    finally { $startupKey.Dispose() }
    Uninstall-TestCopy 'uninstall-portable-startup'
    Assert-Uninstalled
    $startupKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKey)
    try { Assert-InstallerCheck ($startupKey.GetValue($startupName) -ceq $portableCommand) 'uninstall preserves startup belonging to a different portable copy' }
    finally { $startupKey.Dispose() }
    Write-Output "All $checkCount installer checks passed. Logs and retained state: $testRoot"
} finally {
    try {
        if ((Test-IsolatedRegistration) -and (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { Uninstall-TestCopy 'cleanup' }
    } catch { Write-Warning "Could not clean up the isolated test installation: $_" }
    $startupKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($runKey)
    try {
        if ($hadStartup) { $startupKey.SetValue($startupName, $savedStartup, $savedKind) }
        else { $startupKey.DeleteValue($startupName, $false) }
    } finally { $startupKey.Dispose() }
}
