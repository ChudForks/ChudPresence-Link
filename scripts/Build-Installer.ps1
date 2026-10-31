[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$InnoCompiler
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot

if ($InnoCompiler) {
    $compiler = (Resolve-Path -LiteralPath $InnoCompiler).Path
} else {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $compiler = if ($command) { $command.Source } else { $null }
    if (-not $compiler) {
        $compiler = @(
            foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, "$env:LOCALAPPDATA\Programs")) {
                foreach ($edition in @('Inno Setup 7', 'Inno Setup 6')) {
                    if ($base) {
                        $candidate = Join-Path $base "$edition\ISCC.exe"
                        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $candidate }
                    }
                }
            }
        ) | Select-Object -First 1
    }
}
if (-not $compiler) {
    throw 'Install Inno Setup 6.3 or newer from https://jrsoftware.org/isdl.php, or pass -InnoCompiler with the path to ISCC.exe.'
}

& (Join-Path $PSScriptRoot 'Publish.ps1') -Runtime $Runtime

$payload = Join-Path $taskRoot "dist/ChudPresence-Link-$Runtime"
$version = ([xml](Get-Content -LiteralPath (Join-Path $taskRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'The installer requires a numeric application version.' }
$outputDirectory = Join-Path $taskRoot 'dist'
& $compiler "/DSourceDir=$payload" "/DOutputDir=$outputDirectory" "/DAppVersion=$version" "/DRuntime=$Runtime" (Join-Path $taskRoot 'installer/ChudPresence.Link.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed (exit $LASTEXITCODE)." }

$installer = Join-Path $outputDirectory "ChudPresence-Link-$version-$Runtime-Setup.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'The compiler did not produce the expected installer.' }
$releaseFiles = @($installer, (Join-Path $outputDirectory "ChudPresence-Link-$Runtime.zip"))
$hashes = foreach ($releaseFile in $releaseFiles) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $releaseFile -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path -Leaf $releaseFile)
}
$hashPath = Join-Path $outputDirectory "ChudPresence-Link-$version-$Runtime-SHA256SUMS.txt"
$hashes | Set-Content -LiteralPath $hashPath -Encoding ascii
Write-Output "Built installer: $installer"
Write-Output "Release hashes: $hashPath"
