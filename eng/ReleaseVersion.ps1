Set-StrictMode -Version Latest

function Test-SemVer {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)]
        [AllowEmptyString()]
        [string]$Version
    )

    return $Version -cmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
}

function Get-CommitBump {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Subject,
        [string]$Body
    )

    if ([string]::IsNullOrWhiteSpace($Subject)) {
        throw 'Commit subject must not be empty.'
    }

    # A merge brings in commits that the range lists and classifies on their own. Forgejo and Git name merges
    # "Merge pull request ..." or "Merge branch ...", which carry no commit type.
    if ($Subject -cmatch '^Merge (?:pull request|branch|remote-tracking branch) ') {
        return 'none'
    }

    $Body = if ($null -eq $Body) { '' } else { $Body }
    $bodySections = @([regex]::Split($Body.Trim(), '\r?\n\r?\n'))
    $footerLines = @($bodySections[-1].Trim() -split '\r?\n')
    $invalidFooterLines = @($footerLines | Where-Object {
        $_ -notmatch '^\s*[A-Za-z][A-Za-z0-9-]*(?:[ \t]+[A-Za-z][A-Za-z0-9-]*)?[ \t]*:[ \t]*\S.*$'
    })
    if ($footerLines.Count -gt 0 -and $invalidFooterLines.Count -eq 0) {
        if ($footerLines | Where-Object {
            $_ -match '(?i)^\s*BREAKING(?: CHANGE|-CHANGE)(?:-TRAILER)?\s*:\s*\S'
        }) {
            return 'major'
        }
    }

    $match = [regex]::Match($Subject, '(?i)^(?<type>[a-z]+)(?:\([^\r\n()]+\))?(?<bang>!)?:\s+\S.*$')
    if (-not $match.Success) {
        throw "Unknown commit subject: $Subject"
    }

    if ($match.Groups['bang'].Success) {
        return 'major'
    }

    switch ($match.Groups['type'].Value.ToLowerInvariant()) {
        'fix' { return 'patch' }
        'feat' { return 'minor' }
        'docs' { return 'none' }
        'test' { return 'none' }
        'chore' { return 'none' }
        'refactor' { return 'none' }
        'ci' { return 'none' }
        default { throw "Unknown commit type in subject: $Subject" }
    }
}

function Get-NextVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$CurrentVersion,
        [Parameter(Mandatory)]
        [Alias('Commits', 'CommitSubjects')]
        [string[]]$Commit
    )

    if (-not (Test-SemVer $CurrentVersion)) {
        throw "Invalid strict SemVer: $CurrentVersion"
    }

    $bump = 'none'
    foreach ($message in $Commit) {
        $lines = @($message -split "`r?`n", 2)
        $subject = $lines[0]
        $body = if ($lines.Count -gt 1) { $lines[1] } else { '' }
        $candidate = Get-CommitBump -Subject $subject -Body $body
        if ($candidate -eq 'major' -or ($candidate -eq 'minor' -and $bump -notin @('major')) -or ($candidate -eq 'patch' -and $bump -eq 'none')) {
            $bump = $candidate
        }
    }

    if ($bump -eq 'none') {
        throw 'No release-worthy commits were found.'
    }

    $parts = $CurrentVersion.Split('.') | ForEach-Object { [long]$_ }
    switch ($bump) {
        'major' { $parts[0]++; $parts[1] = 0; $parts[2] = 0 }
        'minor' { $parts[1]++; $parts[2] = 0 }
        'patch' { $parts[2]++ }
    }
    if ($parts | Where-Object { $_ -gt [uint32]::MaxValue }) {
        throw "Version component overflow while incrementing $CurrentVersion."
    }
    return ($parts -join '.')
}
