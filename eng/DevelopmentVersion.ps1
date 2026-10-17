Set-StrictMode -Version Latest

function Invoke-RepositoryGit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Workspace,
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $gitArguments = @('-c', 'safe.directory=*', '-C', $Workspace) + $Arguments
    $output = @(& git.exe @gitArguments 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $details = if ($output.Count -gt 0) { "`n$($output -join [Environment]::NewLine)" } else { '' }
        throw "Git command failed with exit code ${exitCode}: git $($Arguments -join ' ')$details"
    }
    $output
}

function Test-RepositoryAncestor {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Workspace,
        [Parameter(Mandatory)]
        [string]$Ancestor,
        [Parameter(Mandatory)]
        [string]$Head
    )

    $gitArguments = @('-c', 'safe.directory=*', '-C', $Workspace,
        'merge-base', '--is-ancestor', "$Ancestor^{}", $Head)
    & git.exe @gitArguments > $null 2>&1
    return ($LASTEXITCODE -eq 0)
}

function Compare-StrictSemVerTag {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Left,
        [Parameter(Mandatory)]
        [string]$Right
    )

    $leftParts = $Left.Substring(1).Split('.') | ForEach-Object { [long]$_ }
    $rightParts = $Right.Substring(1).Split('.') | ForEach-Object { [long]$_ }
    for ($index = 0; $index -lt 3; $index++) {
        if ($leftParts[$index] -gt $rightParts[$index]) { return 1 }
        if ($leftParts[$index] -lt $rightParts[$index]) { return -1 }
    }
    return 0
}

function Get-HighestReachableReleaseTag {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Workspace,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9a-fA-F]{40}$')]
        [string]$Head
    )

    $best = $null
    $refs = @(Invoke-RepositoryGit -Workspace $Workspace -Arguments @(
        'for-each-ref', 'refs/tags', '--format=%(refname:short) %(objecttype)'))
    foreach ($ref in $refs) {
        $parts = $ref -split '\s+', 2
        if ($parts.Count -ne 2) { continue }

        $tag = $parts[0]
        if ($parts[1] -cne 'tag' -or $tag -cnotmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
            continue
        }
        if (-not (Test-RepositoryAncestor -Workspace $Workspace -Ancestor $tag -Head $Head)) {
            continue
        }
        if ($null -eq $best -or (Compare-StrictSemVerTag -Left $tag -Right $best) -gt 0) {
            $best = $tag
        }
    }

    if ($null -eq $best) {
        throw 'Could not determine the highest reachable annotated strict SemVer release tag.'
    }
    $best
}

function Get-DevelopmentVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Workspace,
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9a-fA-F]{40}$')]
        [string]$Head
    )

    $previousTag = Get-HighestReachableReleaseTag -Workspace $Workspace -Head $Head
    $commitIds = @(foreach ($line in @(Invoke-RepositoryGit -Workspace $Workspace -Arguments @(
        'rev-list', '--reverse', "$previousTag..$Head"))) {
        $normalized = ([string]$line).Trim()
        if ($normalized -ne '') { $normalized }
    })

    if ($commitIds.Count -eq 0) {
        return $previousTag.Substring(1)
    }

    $messages = foreach ($commitId in $commitIds) {
        $messageLines = @(Invoke-RepositoryGit -Workspace $Workspace -Arguments @(
            'show', '-s', '--format=%B', $commitId))
        $messageLines -join [Environment]::NewLine
    }
    # Refactor, chore, docs and test commits leave the product version unchanged; a development
    # build right after a release must use that release's version instead of failing.
    $releaseWorthy = @($messages | Where-Object {
        $lines = @($_ -split "`r?`n", 2)
        $body = if ($lines.Count -gt 1) { $lines[1] } else { '' }
        (Get-CommitBump -Subject $lines[0] -Body $body) -ne 'none'
    })
    if ($releaseWorthy.Count -eq 0) {
        return $previousTag.Substring(1)
    }
    Get-NextVersion -CurrentVersion $previousTag.Substring(1) -Commit $messages
}
