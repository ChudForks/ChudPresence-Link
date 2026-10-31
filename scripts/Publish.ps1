[CmdletBinding()]
param([ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = [IO.Path]::GetFullPath((Join-Path $taskRoot 'dist'))
$destination = Join-Path $outputRoot "ChudPresence-Link-$Runtime"
$staging = Join-Path $outputRoot ('.publish-' + $Runtime + '-' + [Guid]::NewGuid().ToString('N'))
$stagingZip = "$staging.zip"
node (Join-Path $taskRoot 'scripts/generate-openapi.mjs') --check
if ($LASTEXITCODE -ne 0) { throw 'API definition check failed.' }
$targetPlatform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }

# Never publish over a previous build: removed assemblies/resources must not leak
# into either the portable ZIP or the installer payload.
function Remove-PublishOutput([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a path outside the release directory: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        if ((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to replace a linked release path: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $staging -Force | Out-Null
try {
    dotnet publish (Join-Path $taskRoot 'src/ChudPresence.Link.Windows/ChudPresence.Link.Windows.csproj') -c Release -r $Runtime --self-contained true -p:Platform=$targetPlatform -p:PlatformTarget=$targetPlatform -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=false -o $staging
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $requiredResources = @('ChudPresence.Link.exe', 'coreclr.dll', 'Microsoft.UI.Xaml.dll', 'Assets/Link.ico', 'App.xbf', 'LinkWindow.xbf', 'ServiceCard.xbf', 'SyncSetupDialog.xbf', 'Resources/Theme.xbf', 'ChudPresence.Link.pri')
    foreach ($resource in $requiredResources) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $resource) -PathType Leaf)) {
            throw "Publish is missing required runtime or UI resource '$resource'."
        }
    }
    Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination $staging
    New-Item -ItemType Directory -Path (Join-Path $staging 'docs'),(Join-Path $staging 'examples'),(Join-Path $staging 'scripts') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/API.md'),(Join-Path $taskRoot 'docs/CONNECTING.md'),(Join-Path $taskRoot 'docs/INSTALLING.md'),(Join-Path $taskRoot 'docs/openapi.json') -Destination (Join-Path $staging 'docs')
    Copy-Item -LiteralPath (Join-Path $taskRoot 'examples/link-client.mjs') -Destination (Join-Path $staging 'examples')
    Copy-Item -LiteralPath (Join-Path $taskRoot 'scripts/Allow-LocalNetwork.ps1') -Destination (Join-Path $staging 'scripts')
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $stagingZip
    Remove-PublishOutput $destination
    Move-Item -LiteralPath $staging -Destination $destination
    Move-Item -LiteralPath $stagingZip -Destination (Join-Path $outputRoot "ChudPresence-Link-$Runtime.zip") -Force
} finally {
    Remove-PublishOutput $staging
    Remove-PublishOutput $stagingZip
}
Write-Output "Built $destination"
