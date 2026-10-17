$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path (Join-Path $PSScriptRoot '..') 'eng\ReleaseRequest.ps1')

function Assert-Equal {
    param($Expected, $Actual, [string]$Name)
    if ($Expected -is [int]) {
        if ([int]$Actual -ne $Expected) { throw "$Name`: expected '$Expected', got '$Actual'." }
        return
    }
    if ($Expected -cne $Actual) { throw "$Name`: expected '$Expected', got '$Actual'." }
}

function Assert-Throws {
    param([scriptblock]$Script, [string]$Name)
    try {
        & $Script
    }
    catch {
        return
    }
    throw "$Name`: expected an exception."
}

function Assert-ThrowsLike {
    param([scriptblock]$Script, [string]$Pattern, [string]$Name)
    try {
        & $Script
    }
    catch {
        if ($_.Exception.Message -notlike $Pattern) {
            throw "$Name`: expected exception like '$Pattern', got '$($_.Exception.Message)'."
        }
        return
    }
    throw "$Name`: expected an exception."
}

function New-ReleaseRequestArchive {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][object[]]$Entries
    )

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Force
    }

    $archive = [System.IO.Compression.ZipFile]::Open($Path, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entrySpec in $Entries) {
            $entry = $archive.CreateEntry($entrySpec.Name)
            $writer = [System.IO.StreamWriter]::new($entry.Open(), [System.Text.UTF8Encoding]::new($false))
            try {
                $writer.Write($entrySpec.Content)
            }
            finally {
                $writer.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ReleaseRequest.Tests." + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null

try {
    $separator = [string][char]0x1e
    $validArchive = Join-Path $tempRoot 'valid.zip'
    New-ReleaseRequestArchive -Path $validArchive -Entries @(
        @{ Name = 'previous-tag.txt'; Content = 'v1.2.3' },
        @{ Name = 'source-commit.txt'; Content = '0123456789abcdef0123456789abcdef01234567' },
        @{ Name = 'commits.rs'; Content = "fix: correct tray icon${separator}feat: add release request parser" }
    )

    $request = Read-ReleaseRequest -ArchivePath $validArchive
    Assert-Equal 'v1.2.3' $request.PreviousTag 'previous tag'
    Assert-Equal '1.2.3' $request.CurrentVersion 'current version'
    Assert-Equal '0123456789abcdef0123456789abcdef01234567' $request.SourceCommit 'source commit'
    Assert-Equal '1.3.0' $request.Version 'calculated version'
    Assert-Equal 2 $request.CommitMessages.Count 'commit count'

    $messageWithWhitespace = "feat: preserve release framing`n`n body keeps leading whitespace`ntrailing space "
    $preserveArchive = Join-Path $tempRoot 'preserve-record.zip'
    New-ReleaseRequestArchive -Path $preserveArchive -Entries @(
        @{ Name = 'previous-tag.txt'; Content = 'v1.2.3' },
        @{ Name = 'source-commit.txt'; Content = '0123456789abcdef0123456789abcdef01234567' },
        @{ Name = 'commits.rs'; Content = $messageWithWhitespace }
    )
    $preserved = Read-ReleaseRequest -ArchivePath $preserveArchive
    Assert-Equal 1 $preserved.CommitMessages.Count 'preserved commit count'
    Assert-Equal $messageWithWhitespace $preserved.CommitMessages[0] 'commit record preserves multiline whitespace'

    foreach ($badFrame in @(
        @{ Name = 'leading-rs'; Content = "${separator}feat: release evidence" },
        @{ Name = 'trailing-rs'; Content = "feat: release evidence${separator}" },
        @{ Name = 'double-rs'; Content = "feat: release evidence${separator}${separator}fix: reject bad frame" },
        @{ Name = 'whitespace-record'; Content = "feat: release evidence${separator} `t `r`n ${separator}fix: reject bad frame" }
    )) {
        $badFrameArchive = Join-Path $tempRoot ($badFrame.Name + '.zip')
        New-ReleaseRequestArchive -Path $badFrameArchive -Entries @(
            @{ Name = 'previous-tag.txt'; Content = 'v1.2.3' },
            @{ Name = 'source-commit.txt'; Content = '0123456789abcdef0123456789abcdef01234567' },
            @{ Name = 'commits.rs'; Content = $badFrame.Content }
        )
        Assert-ThrowsLike { Read-ReleaseRequest -ArchivePath $badFrameArchive } '*Commit message record*' "bad commit RS framing $($badFrame.Name)"
    }

    $extraEntryArchive = Join-Path $tempRoot 'extra-entry.zip'
    New-ReleaseRequestArchive -Path $extraEntryArchive -Entries @(
        @{ Name = 'previous-tag.txt'; Content = 'v1.2.3' },
        @{ Name = 'source-commit.txt'; Content = '0123456789abcdef0123456789abcdef01234567' },
        @{ Name = 'commits.rs'; Content = 'fix: correct tray icon' },
        @{ Name = 'notes.txt'; Content = 'nope' }
    )
    Assert-Throws { Read-ReleaseRequest -ArchivePath $extraEntryArchive } 'extra ZIP entry'

    $duplicateEntryArchive = Join-Path $tempRoot 'duplicate-entry.zip'
    New-ReleaseRequestArchive -Path $duplicateEntryArchive -Entries @(
        @{ Name = 'previous-tag.txt'; Content = 'v1.2.3' },
        @{ Name = 'previous-tag.txt'; Content = 'v1.2.4' },
        @{ Name = 'source-commit.txt'; Content = '0123456789abcdef0123456789abcdef01234567' }
    )
    Assert-ThrowsLike { Read-ReleaseRequest -ArchivePath $duplicateEntryArchive } 'Duplicate release request entry: previous-tag.txt' 'duplicate ZIP entry'

    $wrongCaseTagArchive = Join-Path $tempRoot 'wrong-case-tag.zip'
    New-ReleaseRequestArchive -Path $wrongCaseTagArchive -Entries @(
        @{ Name = 'Previous-Tag.txt'; Content = 'v1.2.3' },
        @{ Name = 'source-commit.txt'; Content = '0123456789abcdef0123456789abcdef01234567' },
        @{ Name = 'commits.rs'; Content = 'fix: correct tray icon' }
    )
    Assert-ThrowsLike { Read-ReleaseRequest -ArchivePath $wrongCaseTagArchive } 'Unexpected release request entry: Previous-Tag.txt' 'wrong-case previous tag entry'

    $wrongCaseCommitsArchive = Join-Path $tempRoot 'wrong-case-commits.zip'
    New-ReleaseRequestArchive -Path $wrongCaseCommitsArchive -Entries @(
        @{ Name = 'previous-tag.txt'; Content = 'v1.2.3' },
        @{ Name = 'source-commit.txt'; Content = '0123456789abcdef0123456789abcdef01234567' },
        @{ Name = 'COMMITS.RS'; Content = 'fix: correct tray icon' }
    )
    Assert-ThrowsLike { Read-ReleaseRequest -ArchivePath $wrongCaseCommitsArchive } 'Unexpected release request entry: COMMITS.RS' 'wrong-case commits entry'

    $nestedPathArchive = Join-Path $tempRoot 'nested-path.zip'
    New-ReleaseRequestArchive -Path $nestedPathArchive -Entries @(
        @{ Name = 'request/previous-tag.txt'; Content = 'v1.2.3' },
        @{ Name = 'source-commit.txt'; Content = '0123456789abcdef0123456789abcdef01234567' },
        @{ Name = 'commits.rs'; Content = 'fix: correct tray icon' }
    )
    Assert-Throws { Read-ReleaseRequest -ArchivePath $nestedPathArchive } 'nested ZIP path'

    $badTagArchive = Join-Path $tempRoot 'bad-tag.zip'
    New-ReleaseRequestArchive -Path $badTagArchive -Entries @(
        @{ Name = 'previous-tag.txt'; Content = '1.2.3' },
        @{ Name = 'source-commit.txt'; Content = '0123456789abcdef0123456789abcdef01234567' },
        @{ Name = 'commits.rs'; Content = 'fix: correct tray icon' }
    )
    Assert-Throws { Read-ReleaseRequest -ArchivePath $badTagArchive } 'malformed tag'

    $badCommitArchive = Join-Path $tempRoot 'bad-source-commit.zip'
    New-ReleaseRequestArchive -Path $badCommitArchive -Entries @(
        @{ Name = 'previous-tag.txt'; Content = 'v1.2.3' },
        @{ Name = 'source-commit.txt'; Content = '01234567' },
        @{ Name = 'commits.rs'; Content = 'fix: correct tray icon' }
    )
    Assert-Throws { Read-ReleaseRequest -ArchivePath $badCommitArchive } 'malformed source commit'

    $emptyRangeArchive = Join-Path $tempRoot 'empty-range.zip'
    New-ReleaseRequestArchive -Path $emptyRangeArchive -Entries @(
        @{ Name = 'previous-tag.txt'; Content = 'v1.2.3' },
        @{ Name = 'source-commit.txt'; Content = '0123456789abcdef0123456789abcdef01234567' },
        @{ Name = 'commits.rs'; Content = "${separator}${separator}" }
    )
    Assert-Throws { Read-ReleaseRequest -ArchivePath $emptyRangeArchive } 'empty commit range'

    $unknownSubjectArchive = Join-Path $tempRoot 'unknown-subject.zip'
    New-ReleaseRequestArchive -Path $unknownSubjectArchive -Entries @(
        @{ Name = 'previous-tag.txt'; Content = 'v1.2.3' },
        @{ Name = 'source-commit.txt'; Content = '0123456789abcdef0123456789abcdef01234567' },
        @{ Name = 'commits.rs'; Content = 'ship it' }
    )
    Assert-Throws { Read-ReleaseRequest -ArchivePath $unknownSubjectArchive } 'unknown commit subject'

    $noReleaseArchive = Join-Path $tempRoot 'no-release.zip'
    New-ReleaseRequestArchive -Path $noReleaseArchive -Entries @(
        @{ Name = 'previous-tag.txt'; Content = 'v1.2.3' },
        @{ Name = 'source-commit.txt'; Content = '0123456789abcdef0123456789abcdef01234567' },
        @{ Name = 'commits.rs'; Content = 'docs: update guide' }
    )
    Assert-Throws { Read-ReleaseRequest -ArchivePath $noReleaseArchive } 'no release-worthy commits'

    Write-Output 'ReleaseRequest tests passed.'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
