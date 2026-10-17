$ErrorActionPreference = 'Stop'
. (Join-Path (Join-Path $PSScriptRoot '..') 'eng\ReleaseVersion.ps1')
. (Join-Path (Join-Path $PSScriptRoot '..') 'eng\DevelopmentVersion.ps1')

function Assert-Equal {
    param($Expected, $Actual, [string]$Name)
    if ($Expected -cne $Actual) { throw "$Name`: expected '$Expected', got '$Actual'." }
}

function Assert-Throws {
    param([scriptblock]$Script, [string]$Name)
    try { & $Script } catch { return }
    throw "$Name`: expected an exception."
}

function Invoke-TestGit {
    param(
        [Parameter(Mandatory)][string]$Workspace,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $output = @(& git.exe -c safe.directory=* -C $Workspace @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Test git command failed with exit code ${exitCode}: git $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
    }
    $output
}

function New-TestCommit {
    param(
        [Parameter(Mandatory)][string]$Workspace,
        [Parameter(Mandatory)][string]$Message
    )

    $file = Join-Path $Workspace 'history.txt'
    Add-Content -LiteralPath $file -Value $Message -Encoding utf8
    Invoke-TestGit -Workspace $Workspace -Arguments @('add', 'history.txt') | Out-Null
    Invoke-TestGit -Workspace $Workspace -Arguments @('commit', '-m', $Message) | Out-Null
    $headOutput = @(Invoke-TestGit -Workspace $Workspace -Arguments @('rev-parse', 'HEAD'))
    ([string]$headOutput[0]).Trim()
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('DevelopmentVersion.Tests.' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null

try {
    Invoke-TestGit -Workspace $tempRoot -Arguments @('init', '--initial-branch', 'main') | Out-Null
    Invoke-TestGit -Workspace $tempRoot -Arguments @('config', 'user.name', 'WinWispr tests') | Out-Null
    Invoke-TestGit -Workspace $tempRoot -Arguments @('config', 'user.email', 'tests@example.invalid') | Out-Null

    $baseCommit = New-TestCommit -Workspace $tempRoot -Message 'chore: create test repository'
    Invoke-TestGit -Workspace $tempRoot -Arguments @('tag', '-a', 'v1.5.3', $baseCommit, '-m', 'v1.5.3') | Out-Null
    $intermediateCommit = New-TestCommit -Workspace $tempRoot -Message 'chore: add nearer lower tag'
    Invoke-TestGit -Workspace $tempRoot -Arguments @('tag', '-a', 'v1.4.0', $intermediateCommit, '-m', 'v1.4.0') | Out-Null
    $head = New-TestCommit -Workspace $tempRoot -Message 'feat: add development version coverage'
    Invoke-TestGit -Workspace $tempRoot -Arguments @('tag', 'v9.9.9', $head) | Out-Null

    Assert-Equal 'v1.5.3' (Get-HighestReachableReleaseTag -Workspace $tempRoot -Head $head) 'highest annotated reachable tag'
    Assert-Equal '1.6.0' (Get-DevelopmentVersion -Workspace $tempRoot -Head $head) 'development version from commit history'

    $headTag = 'v1.6.0'
    Invoke-TestGit -Workspace $tempRoot -Arguments @('tag', '-a', $headTag, $head, '-m', $headTag) | Out-Null
    Assert-Equal '1.6.0' (Get-DevelopmentVersion -Workspace $tempRoot -Head $head) 'version at exact release tag'

    $refactorHead = New-TestCommit -Workspace $tempRoot -Message 'refactor: restructure without a version bump'
    Assert-Equal '1.6.0' (Get-DevelopmentVersion -Workspace $tempRoot -Head $refactorHead) 'refactor-only commits keep the release version'
    $fixHead = New-TestCommit -Workspace $tempRoot -Message 'fix: repair after the refactor'
    Assert-Equal '1.6.1' (Get-DevelopmentVersion -Workspace $tempRoot -Head $fixHead) 'a fix after a refactor still bumps the patch version'

    Assert-Throws {
        Get-HighestReachableReleaseTag -Workspace $tempRoot -Head ('0' * 40)
    } 'missing reachable release tag'

    Write-Output 'DevelopmentVersion tests passed.'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
