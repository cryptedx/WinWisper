Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')

function Read-ZipEntryText {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchiveEntry]$Entry
    )

    $stream = $null
    $reader = $null
    try {
        $stream = $Entry.Open()
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.UTF8Encoding]::new($false, $true))
        return $reader.ReadToEnd()
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
        elseif ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Split-CommitMessageRecords {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Text
    )

    $records = @([regex]::Split($Text, [regex]::Escape([string][char]0x1e)))
    foreach ($record in $records) {
        if ([string]::IsNullOrWhiteSpace($record)) {
            throw 'Commit message record must not be empty or whitespace.'
        }
    }
    $records
}

function Read-ReleaseRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)]
        [string]$ArchivePath
    )

    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $requiredNames = @('previous-tag.txt', 'source-commit.txt', 'commits.rs')
        $allowedNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        $seenNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        $byName = [System.Collections.Generic.Dictionary[string, System.IO.Compression.ZipArchiveEntry]]::new([System.StringComparer]::Ordinal)
        foreach ($requiredName in $requiredNames) {
            [void]$allowedNames.Add($requiredName)
        }

        $entries = @($zip.Entries)
        if ($entries.Count -ne $requiredNames.Count) {
            throw "Release request must contain exactly $($requiredNames.Count) entries."
        }

        foreach ($entry in $entries) {
            if ([string]::IsNullOrEmpty($entry.Name) -or $entry.FullName -cne $entry.Name) {
                throw "Release request entry must be top-level: $($entry.FullName)"
            }
            if (-not $allowedNames.Contains($entry.FullName)) {
                throw "Unexpected release request entry: $($entry.FullName)"
            }
            if (-not $seenNames.Add($entry.FullName)) {
                throw "Duplicate release request entry: $($entry.FullName)"
            }
            $byName[$entry.FullName] = $entry
        }

        foreach ($requiredName in $requiredNames) {
            if (-not $byName.ContainsKey($requiredName)) {
                throw "Missing release request entry: $requiredName"
            }
        }

        $previousTag = (Read-ZipEntryText -Entry $byName['previous-tag.txt']).Trim()
        if ($previousTag -cnotmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
            throw "Invalid previous tag: $previousTag"
        }

        $sourceCommit = (Read-ZipEntryText -Entry $byName['source-commit.txt']).Trim()
        if ($sourceCommit -cnotmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') {
            throw "Invalid source commit: $sourceCommit"
        }

        $messages = @(Split-CommitMessageRecords -Text (Read-ZipEntryText -Entry $byName['commits.rs']))

        $currentVersion = $previousTag.Substring(1)
        $version = Get-NextVersion -CurrentVersion $currentVersion -Commit $messages

        [pscustomobject]@{
            PreviousTag = $previousTag
            CurrentVersion = $currentVersion
            SourceCommit = $sourceCommit
            CommitMessages = $messages
            Version = $version
        }
    }
    finally {
        if ($null -ne $zip) {
            $zip.Dispose()
        }
    }
}
