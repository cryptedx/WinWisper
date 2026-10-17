$ErrorActionPreference = 'Stop'
. (Join-Path (Join-Path $PSScriptRoot '..') 'eng\ReleaseVersion.ps1')

function Assert-Equal {
    param($Expected, $Actual, [string]$Name)
    if ($Expected -cne $Actual) { throw "$Name`: expected '$Expected', got '$Actual'." }
}
function Assert-True {
    param($Value, [string]$Name)
    if (-not $Value) { throw "$Name`: expected true." }
}
function Assert-False {
    param($Value, [string]$Name)
    if ($Value) { throw "$Name`: expected false." }
}
function Assert-Throws {
    param([scriptblock]$Script, [string]$Name)
    try { & $Script } catch { return }
    throw "$Name`: expected an exception."
}

Assert-True (Test-SemVer '1.2.3') 'strict SemVer'
Assert-False (Test-SemVer 'v1.2.3') 'v-prefixed SemVer'
Assert-False (Test-SemVer '1.2.3-beta.1') 'prerelease SemVer'
Assert-False (Test-SemVer '01.2.3') 'leading-zero SemVer'
Assert-Equal 'patch' (Get-CommitBump -Subject 'fix: correct tray icon') 'fix patch'
Assert-Equal 'minor' (Get-CommitBump -Subject 'FeAt(ui): add settings shortcut') 'case-insensitive feat minor'
Assert-Equal 'major' (Get-CommitBump -Subject 'feat!: remove legacy setting') 'bang major'
Assert-Equal 'major' (Get-CommitBump -Subject 'refactor: change config' -Body "Summary.`n`nBREAKING CHANGE: old config is no longer read") 'breaking change major'
Assert-Equal 'major' (Get-CommitBump -Subject 'refactor: change config' -Body "Summary.`n`nBREAKING-CHANGE: old config is no longer read") 'breaking-change major'
Assert-Equal 'major' (Get-CommitBump -Subject 'refactor: change config' -Body 'BREAKING CHANGE: footer only') 'footer-only breaking change'
Assert-Equal 'major' (Get-CommitBump -Subject 'refactor: change config' -Body "Reviewed-by: reviewer`nBREAKING-CHANGE: footer block") 'breaking trailer after footer line'
Assert-Equal 'patch' (Get-CommitBump -Subject 'fix: ordinary change' -Body "Summary.`n`nreview text`nBREAKING CHANGE: not a footer") 'breaking text after footer prose'
Assert-Equal 'none' (Get-CommitBump -Subject 'docs: update guide') 'docs no release'
Assert-Equal 'none' (Get-CommitBump -Subject 'test: add coverage') 'test no release'
Assert-Equal 'none' (Get-CommitBump -Subject 'chore: refresh lock') 'chore no release'
Assert-Equal 'none' (Get-CommitBump -Subject 'refactor: simplify helper') 'refactor no release'
Assert-Equal 'none' (Get-CommitBump -Subject 'CI: run the fast tests on the runner') 'ci no release'
Assert-Equal 'none' (Get-CommitBump -Subject "Merge pull request 'Forgejo issues' (#6) from claude/forgejo-issues into main") 'forgejo merge no release'
Assert-Equal 'none' (Get-CommitBump -Subject "Merge branch 'main' into feature") 'git merge no release'
Assert-Throws { Get-CommitBump -Subject 'Merged the helper' } 'merge-like prose is not a merge'
Assert-Throws { Get-CommitBump -Subject 'update things' } 'unknown subject'
Assert-Throws { Get-CommitBump -Subject 'fix:   ' } 'empty description'
Assert-Equal 'none' (Get-CommitBump -Subject 'refactor: mention BREAKING CHANGE: in prose' -Body 'No footer here.') 'breaking text outside footer'
Assert-Equal '1.2.4' (Get-NextVersion -CurrentVersion '1.2.3' -Commit @('fix: patch')) 'patch increment'
Assert-Equal '1.3.0' (Get-NextVersion -CurrentVersion '1.2.3' -Commit @('feat: feature')) 'minor increment'
Assert-Equal '2.0.0' (Get-NextVersion -CurrentVersion '1.2.3' -Commit @('fix!: breaking fix')) 'major increment'
Assert-Equal '1.3.0' (Get-NextVersion -CurrentVersion '1.2.3' -Commit @('fix: patch', 'feat: feature')) 'highest bump'
Assert-Throws { Get-NextVersion -CurrentVersion 'v1.2.3' -Commit @('fix: patch') } 'malformed current version'
Assert-Throws { Get-NextVersion -CurrentVersion '1.2.3' -Commit @('docs: guide') } 'no release commits'

Write-Output 'ReleaseVersion tests passed.'
