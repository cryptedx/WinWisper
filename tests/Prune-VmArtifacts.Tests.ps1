$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '../eng/Prune-VmArtifacts.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('prune-test-' + [Guid]::NewGuid().ToString('N'))
$artifacts = Join-Path $root 'artifacts'
$temp = Join-Path $root 'temp'
$old = [DateTime]::UtcNow.AddDays(-30)

function New-TestFile {
    param([string]$RelativePath, [DateTime]$WriteTimeUtc = [DateTime]::UtcNow)
    $path = Join-Path $root $RelativePath
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    [IO.File]::WriteAllText($path, 'x')
    [IO.File]::SetLastWriteTimeUtc($path, $WriteTimeUtc)
    $path
}

function Assert-Exists([string]$RelativePath) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $RelativePath))) { throw "Expected to keep $RelativePath" }
}
function Assert-Missing([string]$RelativePath) {
    if (Test-Path -LiteralPath (Join-Path $root $RelativePath)) { throw "Expected to remove $RelativePath" }
}

try {
    foreach ($version in @('1.5.3', '1.6.0', '1.6.1', '1.10.0')) {
        New-TestFile "artifacts/installer/WinWispr-Setup-v$version.exe" | Out-Null
        New-TestFile "artifacts/installer/WinWispr-Setup-v$version.exe.sha256" | Out-Null
        New-TestFile "artifacts/portable/WinWispr-portable-v$version.zip" | Out-Null
        New-TestFile "artifacts/portable/WinWispr-portable-v$version.zip.sha256" | Out-Null
        New-TestFile "artifacts/sbom/SBOM-v$version.cdx.json" | Out-Null
        New-TestFile "artifacts/release-logs/v$version/timings.log" | Out-Null
    }
    New-TestFile 'artifacts/installer/WinWispr-Update-v1.5.3.exe' | Out-Null
    foreach ($version in @('2', '10')) {
        New-TestFile "artifacts/models/WinWispr-Models-v$version.exe" | Out-Null
        New-TestFile "artifacts/models/WinWispr-Models-v$version.exe.sha256" | Out-Null
    }
    New-TestFile 'temp/WinWispr-models-old/file' $old | Out-Null
    New-TestFile 'artifacts/installer/WinWispr-Setup.exe' | Out-Null
    New-TestFile 'artifacts/installer/WinWispr-Setup-v1.4.0.exe.sha256' | Out-Null
    New-TestFile 'artifacts/portable/WinWispr-portable-current.zip' | Out-Null
    New-TestFile "artifacts/portable/WinWispr-portable-tmp-$('a' * 32).zip" $old | Out-Null
    New-TestFile "artifacts/portable/WinWispr-portable-tmp-$('b' * 32).zip" | Out-Null
    New-TestFile 'artifacts/dev-check/bin/App.dll' $old | Out-Null
    New-TestFile 'artifacts/dev-check/obj/project.assets.json' $old | Out-Null
    New-TestFile 'artifacts/dev-check/logs/20260901T000000000-1/check-info.txt' $old | Out-Null
    New-TestFile 'artifacts/dev-check/logs/20260929T000000000-2/check-info.txt' | Out-Null
    New-TestFile 'artifacts/dev-check/review-recheck/notes.txt' $old | Out-Null
    New-TestFile 'artifacts/dev-check/tts-diagnostics/old.txt' $old | Out-Null
    New-TestFile 'artifacts/dev-check/tts-diagnostics/new.txt' | Out-Null
    New-TestFile 'artifacts/runtime-cache/win-x64/sherpa.dll' $old | Out-Null
    New-TestFile 'artifacts/model/model.tar.bz2' $old | Out-Null
    New-TestFile 'artifacts/translation-model/opus-mt-de-en/source.spm' $old | Out-Null
    New-TestFile 'artifacts/.vm-control/worker.lock' $old | Out-Null
    New-TestFile 'temp/WinWispr-publish-old/file' $old | Out-Null
    New-TestFile 'temp/WinWispr-installer-new/file' | Out-Null
    New-TestFile 'temp/Other-old/file' $old | Out-Null
    foreach ($directory in @('artifacts/dev-check/logs/20260901T000000000-1', 'artifacts/dev-check/review-recheck',
            'artifacts/dev-check/tts-diagnostics', 'temp/WinWispr-publish-old', 'temp/WinWispr-models-old', 'temp/Other-old')) {
        [IO.Directory]::SetLastWriteTimeUtc((Join-Path $root $directory), $old)
    }

    $preview = @(& $script -ArtifactsPath $artifacts -TempPath $temp -DryRun)
    if ('PRUNE=PASS' -notin $preview -or 'PRUNE_MODE=dry-run' -notin $preview) { throw "Dry run failed: $preview" }
    Assert-Exists 'artifacts/installer/WinWispr-Setup-v1.5.3.exe'
    Assert-Exists 'temp/WinWispr-publish-old'
    $previewCount = [int](($preview | Where-Object { $_ -like 'PRUNE_ITEMS=*' }) -replace '^PRUNE_ITEMS=', '')

    $output = @(& $script -ArtifactsPath $artifacts -TempPath $temp)
    if ('PRUNE=PASS' -notin $output) { throw "Prune failed: $output" }
    $count = [int](($output | Where-Object { $_ -like 'PRUNE_ITEMS=*' }) -replace '^PRUNE_ITEMS=', '')
    if ($count -ne $previewCount) { throw "The dry run listed $previewCount items, the prune removed $count." }

    # Version order is numeric: 1.10.0 is newer than 1.6.1.
    foreach ($kept in @('1.10.0', '1.6.1')) {
        Assert-Exists "artifacts/installer/WinWispr-Setup-v$kept.exe"
        Assert-Exists "artifacts/installer/WinWispr-Setup-v$kept.exe.sha256"
        Assert-Exists "artifacts/portable/WinWispr-portable-v$kept.zip"
        Assert-Exists "artifacts/sbom/SBOM-v$kept.cdx.json"
        Assert-Exists "artifacts/release-logs/v$kept"
    }
    foreach ($removed in @('1.6.0', '1.5.3')) {
        Assert-Missing "artifacts/installer/WinWispr-Setup-v$removed.exe"
        Assert-Missing "artifacts/installer/WinWispr-Setup-v$removed.exe.sha256"
        Assert-Missing "artifacts/portable/WinWispr-portable-v$removed.zip.sha256"
        Assert-Missing "artifacts/sbom/SBOM-v$removed.cdx.json"
        Assert-Missing "artifacts/release-logs/v$removed"
    }
    # Retention counts per kind, so the only Update installer stays.
    Assert-Exists 'artifacts/installer/WinWispr-Update-v1.5.3.exe'
    Assert-Exists 'artifacts/installer/WinWispr-Setup.exe'
    # Only the newest WinWispr Models installer stays; package versions sort numerically.
    Assert-Exists 'artifacts/models/WinWispr-Models-v10.exe'
    Assert-Exists 'artifacts/models/WinWispr-Models-v10.exe.sha256'
    Assert-Missing 'artifacts/models/WinWispr-Models-v2.exe'
    Assert-Missing 'artifacts/models/WinWispr-Models-v2.exe.sha256'
    Assert-Missing 'temp/WinWispr-models-old'
    Assert-Missing 'artifacts/installer/WinWispr-Setup-v1.4.0.exe.sha256'
    Assert-Exists 'artifacts/portable/WinWispr-portable-current.zip'
    Assert-Missing "artifacts/portable/WinWispr-portable-tmp-$('a' * 32).zip"
    Assert-Exists "artifacts/portable/WinWispr-portable-tmp-$('b' * 32).zip"
    Assert-Exists 'artifacts/dev-check/bin/App.dll'
    Assert-Exists 'artifacts/dev-check/obj/project.assets.json'
    Assert-Missing 'artifacts/dev-check/logs/20260901T000000000-1'
    Assert-Exists 'artifacts/dev-check/logs/20260929T000000000-2'
    Assert-Missing 'artifacts/dev-check/review-recheck'
    # One recent file keeps a whole scratch directory.
    Assert-Exists 'artifacts/dev-check/tts-diagnostics/old.txt'
    Assert-Exists 'artifacts/runtime-cache/win-x64/sherpa.dll'
    Assert-Exists 'artifacts/model/model.tar.bz2'
    Assert-Exists 'artifacts/translation-model/opus-mt-de-en/source.spm'
    Assert-Exists 'artifacts/.vm-control/worker.lock'
    Assert-Missing 'temp/WinWispr-publish-old'
    Assert-Exists 'temp/WinWispr-installer-new'
    Assert-Exists 'temp/Other-old'

    $again = @(& $script -ArtifactsPath $artifacts -TempPath $temp)
    if ('PRUNE_ITEMS=0' -notin $again) { throw "A second prune must remove nothing: $again" }
    Write-Output 'Prune VM artifacts: PASS'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
