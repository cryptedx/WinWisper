# Local inputs only. This file never downloads or modifies the supplied voices.
function Copy-TtsVoices {
    param([Parameter(Mandatory)][string]$InputRoot, [Parameter(Mandatory)][string]$Destination, [string]$ManifestRoot)
    if (-not (Test-Path -LiteralPath $InputRoot -PathType Container)) {
        throw "Verified offline voices are required: $InputRoot"
    }
    if ((Get-Item -LiteralPath $InputRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked voice roots are not allowed.' }
    if ($ManifestRoot) {
        $expected = @(Get-ChildItem -LiteralPath $ManifestRoot -Filter 'vits-piper-*.json')
        $actual = @(Get-ChildItem -LiteralPath $InputRoot -Directory)
        if ($expected.Count -ne $actual.Count -or $expected.Count -ne 2) { throw 'Pinned release voices are required.' }
        foreach ($manifest in $expected) {
            $inputManifest = Join-Path (Join-Path $InputRoot $manifest.BaseName) 'voice.json'
            if ((Get-ArtifactSha256 -LiteralPath $inputManifest) -cne (Get-ArtifactSha256 -LiteralPath $manifest.FullName)) {
                throw 'Release voice manifest differs from the pinned manifest.'
            }
        }
    }
    $languages = @()
    foreach ($directory in Get-ChildItem -LiteralPath $InputRoot -Directory) {
        if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked voices are not allowed.' }
        if ($directory.Name -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Invalid voice directory name.' }
        $manifestPath = Join-Path $directory.FullName 'voice.json'
        $manifestFile = Get-Item -LiteralPath $manifestPath
        if (($manifestFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $manifestFile.Length -gt 2MB) { throw 'Invalid voice manifest file.' }
        $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
        $voice = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
        if ($voice.Language -notin @('de-DE', 'en-US', 'en-GB') -or [string]::IsNullOrWhiteSpace($voice.Id) -or
            [string]::IsNullOrWhiteSpace($voice.License) -or @($voice.Files).Count -eq 0 -or @($voice.Files).Count -gt 4096) {
            throw 'Voice identity, language, license, and file hashes are required.'
        }
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $target = Join-Path $Destination $directory.Name
        foreach ($file in $voice.Files) {
            $relative = [string]$file.Path
            if ($relative -match '[\\:]' -or @($relative -split '/' | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) {
                throw 'Unsafe voice path.'
            }
            $path = $directory.FullName
            foreach ($part in $relative -split '/') {
                $path = Join-Path $path $part
                if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked voice files are not allowed.' }
            }
            if (-not $names.Add($relative) -or $file.Bytes -le 0 -or $file.Bytes -gt 512MB -or
                $file.Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or (Get-Item -LiteralPath $path).Length -ne $file.Bytes -or
                (Get-ArtifactSha256 -LiteralPath $path) -ine $file.Sha256) { throw 'Voice file integrity failed.' }
            $output = Join-Path $target $relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null
            Copy-Item -LiteralPath $path -Destination $output
            if ((Get-ArtifactSha256 -LiteralPath $output) -ine $file.Sha256) { throw 'Copied voice integrity failed.' }
        }
        if (-not $names.Contains($voice.Model) -or -not $names.Contains($voice.Tokens) -or -not $names.Contains($voice.License) -or
            -not ($names | Where-Object { $_.StartsWith($voice.DataDirectory + '/', [StringComparison]::Ordinal) })) {
            throw 'Model, tokens, license, and phonemizer data must be listed.'
        }
        [IO.File]::WriteAllBytes((Join-Path $target 'voice.json'), $manifestBytes)
        $languages += $voice.Language
    }
    if ('de-DE' -notin $languages -or -not ($languages | Where-Object { $_ -like 'en-*' })) {
        throw 'Both German and English voices must be bundled.'
    }
}
