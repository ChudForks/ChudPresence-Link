param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Message,
    [string] $WorktreePath
)

$ErrorActionPreference = 'Stop'
$repo = (git -C $PSScriptRoot rev-parse --show-toplevel)
if ($LASTEXITCODE -ne 0) { throw 'Cannot find the development repository.' }
$branch = (git -C $repo branch --show-current)
if ($LASTEXITCODE -ne 0 -or $branch -ne 'dev') { throw 'Run this script from the dev checkout.' }
if (-not $WorktreePath) { $WorktreePath = "$repo-publish" }
if (-not (Test-Path -LiteralPath $WorktreePath -PathType Container)) { throw "Publishing worktree not found: $WorktreePath" }
$publishRoot = (git -C $WorktreePath rev-parse --show-toplevel)
if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($publishRoot) -ne [IO.Path]::GetFullPath($WorktreePath)) { throw 'The publishing path must be a Git worktree root.' }
$commonDev = (git -C $repo rev-parse --path-format=absolute --git-common-dir)
$commonPublish = (git -C $WorktreePath rev-parse --path-format=absolute --git-common-dir)
if ($commonDev -ne $commonPublish) { throw 'The publishing worktree belongs to a different repository.' }
$publishBranch = (git -C $WorktreePath branch --show-current)
if ($publishBranch -ne 'main') { throw 'The publishing worktree must be on main.' }
$status = @(git -C $WorktreePath status --porcelain=v1 --untracked-files=all --ignored)
if ($LASTEXITCODE -ne 0 -or $status.Count -gt 0) { throw 'The publishing worktree must be completely clean, including ignored files.' }
git -C $WorktreePath merge-base --is-ancestor dev main
if ($LASTEXITCODE -eq 0) { throw 'Development history is an ancestor of main; refusing to publish.' }
if ($LASTEXITCODE -ne 1) { throw 'Could not check branch ancestry.' }
$common = @(git -C $WorktreePath merge-base --all dev main)
if ($LASTEXITCODE -eq 0 -and $common.Count -gt 0) { throw 'Development and publishing histories are connected; refusing to publish.' }
if ($LASTEXITCODE -ne 1) { throw 'Could not verify independent histories.' }
git -C $WorktreePath fetch origin refs/heads/main:refs/remotes/origin/main
if ($LASTEXITCODE -ne 0) { throw 'Could not fetch origin/main; nothing was published.' }
git -C $WorktreePath merge-base --is-ancestor refs/remotes/origin/main main
if ($LASTEXITCODE -ne 0) { throw 'Local main is behind or diverged from origin/main. Reconcile it before publishing.' }
# A two-tree update applies additions, modifications, and deletions without checking out dev.
git -C $WorktreePath read-tree -m -u HEAD refs/heads/dev
if ($LASTEXITCODE -ne 0) { throw 'Could not apply the dev snapshot to the publishing worktree.' }
git -C $WorktreePath diff --cached --quiet --exit-code
$diffExit = $LASTEXITCODE
if ($diffExit -eq 1) {
    # Explicit environment values also override inherited Git author/committer settings.
    $identityKeys = @('GIT_AUTHOR_NAME', 'GIT_AUTHOR_EMAIL', 'GIT_COMMITTER_NAME', 'GIT_COMMITTER_EMAIL')
    $oldIdentity = @{}
    foreach ($key in $identityKeys) { $oldIdentity[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
    try {
        $env:GIT_AUTHOR_NAME = 'ChudForks'
        $env:GIT_COMMITTER_NAME = 'ChudForks'
        $env:GIT_AUTHOR_EMAIL = '286146499+ChudForks@users.noreply.github.com'
        $env:GIT_COMMITTER_EMAIL = '286146499+ChudForks@users.noreply.github.com'
        git -C $WorktreePath commit -m $Message
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the milestone commit. Inspect the publishing worktree before retrying.' }
    } finally {
        foreach ($key in $identityKeys) { [Environment]::SetEnvironmentVariable($key, $oldIdentity[$key], 'Process') }
    }
} elseif ($diffExit -ne 0) { throw 'Could not inspect the milestone snapshot.' }
git -C $WorktreePath push origin refs/heads/main:refs/heads/main
if ($LASTEXITCODE -ne 0) { throw 'Push failed. The local milestone commit remains on main; inspect and retry without force-pushing.' }
Write-Host 'Milestone main is up to date on origin. The dev worktree was not modified.'
