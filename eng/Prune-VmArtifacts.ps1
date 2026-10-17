[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArtifactsPath,
    [string]$TempPath = [IO.Path]::GetTempPath(),
    [ValidateRange(1, 100)]
    [int]$KeepVersions = 2,
    [ValidateRange(1, 3650)]
    [int]$MaxAgeDays = 14,
    [switch]$DryRun
)

# Keeps the VM's build output bounded. Every worker run calls this before it
# builds; `./eng/deploy-windows-vm.sh --prune [--dry-run]` runs it on its own.
# It removes only what it recognizes: versioned packages beyond the newest
# $KeepVersions per kind (with their .sha256 sidecars), WinWispr Models
# installers older than the newest model package version, orphaned sidecars,
# stale temporary packages, development logs and scratch older than
# $MaxAgeDays, and leftover WinWispr temporary directories. The build caches
# (dev-check\bin, dev-check\obj, runtime-cache), the bundled model, the voices,
# the translation model, unversioned current packages, and the control
# directory stay.

$ErrorActionPreference = 'Stop'

$artifacts = [IO.Path]::GetFullPath($ArtifactsPath)
$now = [DateTime]::UtcNow
$script:freedBytes = [long]0
$script:removedCount = 0
$versionPattern = '(?<version>(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*))'

function Test-ReparseItem {
    param([Parameter(Mandatory)]$Item)
    (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
}

# Windows PowerShell's recursive delete can follow a junction into its target,
# so a tree with any reparse point is left alone.
function Test-PlainTree {
    param([Parameter(Mandatory)]$Item)
    if (Test-ReparseItem -Item $Item) { return $false }
    if (-not $Item.PSIsContainer) { return $true }
    foreach ($child in @(Get-ChildItem -LiteralPath $Item.FullName -Recurse -Force -ErrorAction SilentlyContinue)) {
        if (Test-ReparseItem -Item $child) { return $false }
    }
    $true
}

function Get-TreeSize {
    param([Parameter(Mandatory)]$Item)
    if (-not $Item.PSIsContainer) { return [long]$Item.Length }
    $sum = (Get-ChildItem -LiteralPath $Item.FullName -Recurse -File -Force -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sum) { [long]0 } else { [long]$sum }
}

# The newest write anywhere in the tree, so a directory still in use counts as young.
function Get-TreeLastWriteUtc {
    param([Parameter(Mandatory)]$Item)
    $newest = $Item.LastWriteTimeUtc
    if ($Item.PSIsContainer) {
        foreach ($child in @(Get-ChildItem -LiteralPath $Item.FullName -Recurse -Force -ErrorAction SilentlyContinue)) {
            if ($child.LastWriteTimeUtc -gt $newest) { $newest = $child.LastWriteTimeUtc }
        }
    }
    $newest
}

function Remove-PruneItem {
    param(
        [Parameter(Mandatory)]$Item,
        [Parameter(Mandatory)] [string]$Reason
    )
    if (-not (Test-PlainTree -Item $Item)) {
        Write-Output "PRUNE_SKIP=$($Item.FullName) (contains a reparse point)"
        return
    }
    $size = Get-TreeSize -Item $Item
    $verb = if ($DryRun) { 'PRUNE_WOULD_REMOVE' } else { 'PRUNE_REMOVED' }
    Write-Output ('{0}={1} ({2:N1} MB, {3})' -f $verb, $Item.FullName, ($size / 1MB), $Reason)
    if (-not $DryRun) {
        Remove-Item -LiteralPath $Item.FullName -Recurse -Force
    }
    $script:freedBytes += $size
    $script:removedCount++
}

function Get-ChildItemsSafe {
    param([Parameter(Mandatory)] [string]$Path, [switch]$Directory, [switch]$File)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return @() }
    $item = Get-Item -LiteralPath $Path -Force
    if (Test-ReparseItem -Item $item) { return @() }
    @(Get-ChildItem -LiteralPath $Path -Force -Directory:$Directory -File:$File -ErrorAction SilentlyContinue)
}

# Keeps the newest $KeepVersions versions per kind. $Pattern must capture
# `kind` and `version`; an item's .sha256 sidecar goes with it.
function Remove-OldVersions {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Pattern,
        [switch]$Directory
    )
    $matched = foreach ($item in @(Get-ChildItemsSafe -Path $Path -Directory:$Directory -File:(-not $Directory))) {
        $match = [regex]::Match($item.Name, $Pattern)
        if ($match.Success) {
            [pscustomobject]@{ Item = $item; Kind = $match.Groups['kind'].Value; Version = [version]$match.Groups['version'].Value }
        }
    }
    foreach ($group in @($matched | Group-Object -Property Kind)) {
        $old = @($group.Group | Sort-Object -Property Version -Descending | Select-Object -Skip $KeepVersions)
        foreach ($entry in $old) {
            Remove-PruneItem -Item $entry.Item -Reason "older than the newest $KeepVersions"
            $sidecar = "$($entry.Item.FullName).sha256"
            if (Test-Path -LiteralPath $sidecar -PathType Leaf) {
                Remove-PruneItem -Item (Get-Item -LiteralPath $sidecar -Force) -Reason 'sidecar of a removed package'
            }
        }
    }
}

function Remove-OrphanedSidecars {
    param([Parameter(Mandatory)] [string]$Path)
    foreach ($item in @(Get-ChildItemsSafe -Path $Path -File | Where-Object { $_.Name -like '*.sha256' })) {
        $target = $item.FullName.Substring(0, $item.FullName.Length - '.sha256'.Length)
        if (-not (Test-Path -LiteralPath $target)) {
            Remove-PruneItem -Item $item -Reason 'sidecar without its package'
        }
    }
}

function Remove-Aged {
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]]$Items,
        [Parameter(Mandatory)] [TimeSpan]$MaxAge,
        [Parameter(Mandatory)] [string]$Reason
    )
    foreach ($item in $Items) {
        if (($now - (Get-TreeLastWriteUtc -Item $item)) -gt $MaxAge) {
            Remove-PruneItem -Item $item -Reason $Reason
        }
    }
}

if (-not (Test-Path -LiteralPath $artifacts -PathType Container)) {
    Write-Output "PRUNE_ARTIFACTS=MISSING $artifacts"
}
else {
    $installer = Join-Path $artifacts 'installer'
    $portable = Join-Path $artifacts 'portable'
    $devCheck = Join-Path $artifacts 'dev-check'
    $age = [TimeSpan]::FromDays($MaxAgeDays)
    $staleTemporary = [TimeSpan]::FromDays(1)

    Remove-OldVersions -Path $installer -Pattern "^WinWispr-(?<kind>Setup|Update)-v$versionPattern\.exe$"
    Remove-OldVersions -Path $portable -Pattern "^WinWispr-(?<kind>portable|update)-v$versionPattern\.zip$"
    Remove-OldVersions -Path (Join-Path $artifacts 'sbom') -Pattern "^(?<kind>SBOM)-v$versionPattern\.cdx\.json$"
    Remove-OldVersions -Path (Join-Path $artifacts 'release-logs') -Pattern "^(?<kind>)v$versionPattern$" -Directory
    Remove-Aged -Items @(Get-ChildItemsSafe -Path $portable -File | Where-Object { $_.Name -match '^WinWispr-(portable|update)-tmp-[0-9a-f]{32}\.zip(\.sha256)?$' }) `
        -MaxAge $staleTemporary -Reason 'stale temporary package'
    # Deployments reuse the newest WinWispr Models installer; older package versions are not installed again.
    $models = Join-Path $artifacts 'models'
    $modelPackages = @(Get-ChildItemsSafe -Path $models -File |
        Where-Object { $_.Name -match '^WinWispr-Models-v[1-9][0-9]*\.exe$' } |
        Sort-Object -Property { [int]($_.BaseName.Substring('WinWispr-Models-v'.Length)) } -Descending)
    foreach ($old in @($modelPackages | Select-Object -Skip 1)) {
        Remove-PruneItem -Item $old -Reason 'older than the newest model package'
        $sidecar = "$($old.FullName).sha256"
        if (Test-Path -LiteralPath $sidecar -PathType Leaf) {
            Remove-PruneItem -Item (Get-Item -LiteralPath $sidecar -Force) -Reason 'sidecar of a removed package'
        }
    }
    foreach ($path in @($installer, $portable, $models)) {
        Remove-OrphanedSidecars -Path $path
    }
    Remove-Aged -Items @(Get-ChildItemsSafe -Path (Join-Path $devCheck 'logs') -Directory) `
        -MaxAge $age -Reason "development log older than $MaxAgeDays days"
    Remove-Aged -Items @(Get-ChildItemsSafe -Path $devCheck | Where-Object { $_.Name -notin @('bin', 'obj', 'logs') }) `
        -MaxAge $age -Reason "development scratch older than $MaxAgeDays days"
}

Remove-Aged -Items @(Get-ChildItemsSafe -Path $TempPath -Directory | Where-Object { $_.Name -match '^WinWispr-(publish|installer|sherpa|models)-' }) `
    -MaxAge ([TimeSpan]::FromDays(1)) -Reason 'leftover temporary directory'

Write-Output ('PRUNE_FREED_MB={0:N0}' -f ($script:freedBytes / 1MB))
Write-Output "PRUNE_ITEMS=$($script:removedCount)"
Write-Output "PRUNE_MODE=$(if ($DryRun) { 'dry-run' } else { 'remove' })"
Write-Output 'PRUNE=PASS'
