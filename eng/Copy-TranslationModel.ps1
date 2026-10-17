# Local inputs only. This file never downloads or modifies the supplied translation model.
# Copies the ten Opus-MT files that eng/translation-model-pins.json pins from InputRoot (artifacts\translation-model,
# laid out as opus-mt-de-en\... and opus-mt-en-de\...) to Destination (the package's TranslationData folder),
# after checking each size and SHA-256, and checks every copy again.
function Copy-TranslationModel {
    param(
        [Parameter(Mandatory)][string]$InputRoot,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$PinsPath
    )
    if (-not (Test-Path -LiteralPath $InputRoot -PathType Container)) {
        throw "The verified translation model is required: $InputRoot"
    }
    if ((Get-Item -LiteralPath $InputRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'A linked translation model root is not allowed.'
    }
    $pins = Get-Content -Raw -LiteralPath $PinsPath | ConvertFrom-Json
    $count = 0
    foreach ($direction in $pins.directions.PSObject.Properties) {
        $folder = [string]$direction.Value.folder
        if ($folder -notmatch '^opus-mt-[a-z]{2}-[a-z]{2}$') { throw "Invalid translation model folder: $folder" }
        foreach ($file in $direction.Value.files) {
            $relative = "$folder/$([string]$file.path)"
            if ($relative -match '[\\:]' -or @($relative -split '/' | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) {
                throw "Unsafe translation model path: $relative"
            }
            if ([long]$file.bytes -le 0 -or [string]$file.sha256 -notmatch '^[a-f0-9]{64}$') {
                throw "Invalid translation model pin: $relative"
            }
            $path = $InputRoot
            foreach ($part in $relative -split '/') {
                $path = Join-Path $path $part
                if (-not (Test-Path -LiteralPath $path)) { throw "Translation model file is missing: $relative" }
                if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    throw "Linked translation model files are not allowed: $relative"
                }
            }
            if ((Get-Item -LiteralPath $path).Length -ne [long]$file.bytes -or
                (Get-ArtifactSha256 -LiteralPath $path) -cne [string]$file.sha256) {
                throw "Translation model file integrity failed: $relative"
            }
            $output = Join-Path $Destination ($relative.Replace('/', '\'))
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null
            Copy-Item -LiteralPath $path -Destination $output
            if ((Get-ArtifactSha256 -LiteralPath $output) -cne [string]$file.sha256) {
                throw "Copied translation model file integrity failed: $relative"
            }
            $count++
        }
    }
    if ($count -ne 10) {
        throw "The translation model must have ten files, the pins list $count."
    }
}
