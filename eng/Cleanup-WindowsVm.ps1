[CmdletBinding()]
param(
    [string]$LegacyRoot = (Join-Path $env:USERPROFILE 'WinWispr')
)

$ErrorActionPreference = 'Stop'

function Test-ReparseItem {
    param([Parameter(Mandatory)]$Item)
    (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
}

function Assert-NoReparsePath {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Name
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath)) {
        throw "${Name} is missing: $fullPath"
    }
    $root = [IO.Path]::GetPathRoot($fullPath)
    $current = $root
    foreach ($segment in @($fullPath.Substring($root.Length) -split '[\\/]' | Where-Object { $_ })) {
        $current = Join-Path $current $segment
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (Test-ReparseItem -Item $item) {
                throw "${Name} contains a reparse point: $current"
            }
        }
    }
    $fullPath
}

function Assert-NoReparseTree {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Name
    )

    $root = Assert-NoReparsePath -Path $Path -Name $Name
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "${Name} is not a directory: $root"
    }
    $pending = New-Object 'System.Collections.Generic.List[string]'
    $pending.Add($root)
    for ($index = 0; $index -lt $pending.Count; $index++) {
        foreach ($child in @(Get-ChildItem -LiteralPath $pending[$index] -Force)) {
            if (Test-ReparseItem -Item $child) {
                throw "${Name} contains a reparse point: $($child.FullName)"
            }
            if ($child.PSIsContainer) {
                $pending.Add($child.FullName)
            }
        }
    }
}

function Remove-SafeTree {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Name
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return 'MISSING'
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (Test-ReparseItem -Item $item) {
        throw "Refusing to remove ${Name} because it is a reparse point: $Path"
    }
    if ($item.PSIsContainer) {
        Assert-NoReparseTree -Path $Path -Name $Name
    }
    Remove-Item -LiteralPath $Path -Recurse -Force
    'REMOVED'
}

$expectedRoot = [IO.Path]::GetFullPath((Join-Path $env:USERPROFILE 'WinWispr')).TrimEnd('\')
$root = [IO.Path]::GetFullPath($LegacyRoot).TrimEnd('\')
if ($root -cne $expectedRoot) {
    throw "Legacy cleanup is restricted to $expectedRoot"
}

$root = Assert-NoReparsePath -Path $root -Name 'Legacy workspace'
Assert-NoReparseTree -Path $root -Name 'Legacy workspace'

$testDataPath = Join-Path $root 'test-data'
$testDataExisted = Test-Path -LiteralPath $testDataPath
$installPath = Join-Path $env:LOCALAPPDATA 'Programs\WinWispr'
$appDataPath = Join-Path $env:LOCALAPPDATA 'WinWispr'
$installExisted = Test-Path -LiteralPath $installPath
$appDataExisted = Test-Path -LiteralPath $appDataPath

foreach ($name in @('source', 'incoming', 'artifacts')) {
    $status = Remove-SafeTree -Path (Join-Path $root $name) -Name "Legacy $name"
    Write-Output ("LEGACY_{0}={1}" -f $name.ToUpperInvariant(), $status)
}

if ($testDataExisted -and -not (Test-Path -LiteralPath $testDataPath)) {
    throw 'Legacy cleanup removed test-data unexpectedly.'
}
if ($installExisted -and -not (Test-Path -LiteralPath $installPath)) {
    throw 'Legacy cleanup removed the installed application unexpectedly.'
}
if ($appDataExisted -and -not (Test-Path -LiteralPath $appDataPath)) {
    throw 'Legacy cleanup removed WinWispr app data unexpectedly.'
}

Write-Output 'TEST_DATA=PRESERVED'
Write-Output 'APP_INSTALL=PRESERVED'
Write-Output 'APP_DATA=PRESERVED'
Write-Output 'LEGACY_CLEANUP=PASS'
