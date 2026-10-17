param(
    [Parameter(Mandatory)]
    [string]$OutputPath,
    [string]$RuntimeHashPath,
    [switch]$AllowMissingRuntime
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$RuntimeHashPath = if ([string]::IsNullOrWhiteSpace($RuntimeHashPath)) {
    Join-Path $repositoryRoot 'src\WinWispr\Runtime\win-x64\runtime.sha256'
} else {
    [IO.Path]::GetFullPath($RuntimeHashPath)
}
$fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
$blockedOutputSegments = '^(secrets?|models?|audio|transcripts?)$'
foreach ($segment in ($fullOutputPath -split '[\\/]')) {
    if ($segment -match $blockedOutputSegments) {
        throw "SBOM output path must not be below secrets, models, audio, or transcripts: $OutputPath"
    }
}

function Convert-ToRelativePath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($repositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return $fullPath.Replace('\', '/')
    }

    return $fullPath.Substring($repositoryRoot.Length).TrimStart('\', '/').Replace('\', '/')
}

function Convert-Base64Sha512ToHex {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    $bytes = [Convert]::FromBase64String($Value)
    if ($bytes.Length -ne 64) {
        throw 'NuGet contentHash is not a SHA-512 value.'
    }

    ($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
}

function Get-LockPackages {
    param(
        [Parameter(Mandatory)]
        [string]$LockPath,
        [Parameter(Mandatory)]
        [string]$Scope
    )

    $lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
    $packages = @{}
    foreach ($targetProperty in ($lock.dependencies.PSObject.Properties | Sort-Object Name)) {
        foreach ($packageProperty in ($targetProperty.Value.PSObject.Properties | Sort-Object Name)) {
            $entry = $packageProperty.Value
            $name = [string]$packageProperty.Name
            $typeProperty = $entry.PSObject.Properties['type']
            $type = if ($null -eq $typeProperty) { '' } else { [string]$typeProperty.Value }
            if ($type -eq 'Project') {
                continue
            }

            $versionProperty = $entry.PSObject.Properties['resolved']
            $contentHashProperty = $entry.PSObject.Properties['contentHash']
            $version = if ($null -eq $versionProperty) { '' } else { [string]$versionProperty.Value }
            $contentHash = if ($null -eq $contentHashProperty) { '' } else { [string]$contentHashProperty.Value }
            if ([string]::IsNullOrWhiteSpace($version) -or [string]::IsNullOrWhiteSpace($contentHash)) {
                continue
            }

            $key = "$Scope|$($name.ToLowerInvariant())|$version|$contentHash"
            if (-not $packages.ContainsKey($key)) {
                $packages[$key] = [ordered]@{
                    Scope = $Scope
                    Name = $name
                    Version = $version
                    PackageType = $type
                    ContentHash = $contentHash
                    Targets = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
                }
            }
            elseif ($packages[$key]['PackageType'] -ne 'Direct' -and $type -eq 'Direct') {
                $packages[$key]['PackageType'] = 'Direct'
            }

            $packages[$key]['Targets'].Add([string]$targetProperty.Name) | Out-Null
        }
    }

    $packages.Values | Sort-Object `
        @{ Expression = { $_['Scope'] } }, `
        @{ Expression = { $_['Name'].ToLowerInvariant() } }, `
        @{ Expression = { $_['Version'] } }
}

function New-NuGetComponent {
    param(
        [Parameter(Mandatory)]
        $Package
    )

    $contentHashBase64 = $Package['ContentHash']
    $contentHashHex = Convert-Base64Sha512ToHex -Value $contentHashBase64
    $escapedName = [Uri]::EscapeDataString($Package['Name'].ToLowerInvariant())
    $escapedVersion = [Uri]::EscapeDataString($Package['Version'])
    $purl = "pkg:nuget/${escapedName}@${escapedVersion}?winwispr_scope=$($Package['Scope'])"
    [ordered]@{
        'bom-ref' = $purl
        type = 'library'
        name = $Package['Name']
        version = $Package['Version']
        purl = $purl
        hashes = @(
            [ordered]@{
                alg = 'SHA-512'
                content = $contentHashHex
            }
        )
        properties = @(
            [ordered]@{ name = 'winwispr:scope'; value = $Package['Scope'] },
            [ordered]@{ name = 'nuget:dependency-type'; value = $Package['PackageType'] },
            [ordered]@{ name = 'nuget:target-frameworks'; value = (($Package['Targets'] | Sort-Object) -join ';') },
            [ordered]@{ name = 'nuget:content-hash-base64'; value = $contentHashBase64 }
        )
    }
}

function Read-ModelSpec {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    # ModelManifest.cs is the single source of the archive URL, size and hash that the installer verifies.
    $manifestPath = Join-Path $repositoryRoot 'src\WinWispr\Speech\ModelManifest.cs'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw
    $block = [regex]::Match($manifest, "ModelSpec $Name\s*=\s*new\((.*?)\);\r?\n", [Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $block.Success) {
        throw "Could not find the $Name model in $manifestPath"
    }

    $text = $block.Groups[1].Value
    if ($text -notmatch 'Uri:\s*new Uri\("([^"]+)"\)' -or
        $text -notmatch 'ArchiveBytes:\s*([0-9_]+)' -or
        $text -notmatch 'ArchiveSha256:\s*"([a-fA-F0-9]{64})"') {
        throw "Could not read model provenance from $manifestPath"
    }

    [ordered]@{
        Uri = [regex]::Match($text, 'Uri:\s*new Uri\("([^"]+)"\)').Groups[1].Value
        ArchiveBytes = [regex]::Match($text, 'ArchiveBytes:\s*([0-9_]+)').Groups[1].Value.Replace('_', '')
        ArchiveSha256 = [regex]::Match($text, 'ArchiveSha256:\s*"([a-fA-F0-9]{64})"').Groups[1].Value.ToLowerInvariant()
    }
}

function New-ModelComponent {
    $spec = Read-ModelSpec -Name 'Parakeet'
    [ordered]@{
        'bom-ref' = 'model:parakeet-tdt-0.6b-v3-int8'
        type = 'machine-learning-model'
        name = 'Parakeet TDT 0.6B V3 INT8'
        version = 'sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8'
        hashes = @(
            [ordered]@{
                alg = 'SHA-256'
                content = $spec['ArchiveSha256']
            }
        )
        externalReferences = @(
            [ordered]@{
                type = 'distribution'
                url = $spec['Uri']
            }
        )
        properties = @(
            [ordered]@{ name = 'winwispr:scope'; value = 'app' },
            [ordered]@{ name = 'model:archive-bytes'; value = $spec['ArchiveBytes'] }
        )
    }
}

function Read-TranslationFiles {
    param([Parameter(Mandatory)] $Pins)

    # eng/translation-model-pins.json is the single source of the ten files that the package build copies into
    # TranslationData after checking each size and SHA-256. The URL names where each file was obtained.
    foreach ($direction in ($Pins.directions.PSObject.Properties | Sort-Object Name)) {
        $export = $direction.Value.export
        foreach ($file in $direction.Value.files) {
            if ([string]$file.sha256 -notmatch '^[a-f0-9]{64}$' -or [long]$file.bytes -le 0) {
                throw "Invalid translation model pin: $($direction.Value.folder)/$($file.path)"
            }
            [ordered]@{
                Uri = "https://huggingface.co/$($export.repo)/resolve/$($export.commit)/$($file.path)"
                InstalledName = "$($direction.Value.folder)/$($file.path)"
                Bytes = [string]$file.bytes
                Sha256 = [string]$file.sha256
            }
        }
    }
}

function New-TranslationModelComponent {
    # The translation model: ten upstream files from pinned Hugging Face commits, shipped in Setup and Portable in
    # TranslationData. There is no archive, so every file is a nested component with its own hash and source URL.
    $pinsPath = Join-Path $repositoryRoot 'eng\translation-model-pins.json'
    $pins = Get-Content -LiteralPath $pinsPath -Raw | ConvertFrom-Json
    $files = @(Read-TranslationFiles -Pins $pins)
    if ($files.Count -ne 10) {
        throw "The translation model must have ten files, $pinsPath lists $($files.Count)."
    }

    $totalBytes = [long]0
    foreach ($file in $files) {
        $totalBytes += [long]$file['Bytes']
    }

    $properties = [Collections.Generic.List[object]]::new()
    [void]$properties.Add([ordered]@{ name = 'winwispr:scope'; value = 'app' })
    [void]$properties.Add([ordered]@{ name = 'winwispr:distribution'; value = 'bundled' })
    [void]$properties.Add([ordered]@{ name = 'winwispr:package-path'; value = 'TranslationData' })
    [void]$properties.Add([ordered]@{ name = 'model:files'; value = [string]$files.Count })
    [void]$properties.Add([ordered]@{ name = 'model:total-bytes'; value = [string]$totalBytes })
    $references = [Collections.Generic.List[object]]::new()
    [void]$references.Add([ordered]@{ type = 'website'; url = 'https://github.com/Helsinki-NLP/Opus-MT' })
    foreach ($direction in ($pins.directions.PSObject.Properties | Sort-Object Name)) {
        $export = $direction.Value.export
        $original = $direction.Value.original
        [void]$properties.Add([ordered]@{ name = "model:export:$($direction.Name)"; value = "$($export.repo)@$($export.commit)" })
        [void]$properties.Add([ordered]@{ name = "model:original:$($direction.Name)"; value = "$($original.repo)@$($original.commit)" })
        [void]$references.Add([ordered]@{ type = 'vcs'; url = "https://huggingface.co/$($export.repo)/tree/$($export.commit)" })
    }

    $fileComponents = [Collections.Generic.List[object]]::new()
    foreach ($file in $files) {
        [void]$fileComponents.Add([ordered]@{
            'bom-ref' = "model:opus-mt-int8-v1:$($file['InstalledName'])"
            type = 'file'
            name = $file['InstalledName']
            hashes = @(
                [ordered]@{
                    alg = 'SHA-256'
                    content = $file['Sha256']
                }
            )
            externalReferences = @(
                [ordered]@{
                    type = 'distribution'
                    url = $file['Uri']
                }
            )
            properties = @(
                [ordered]@{ name = 'model:bytes'; value = $file['Bytes'] }
            )
        })
    }

    [ordered]@{
        'bom-ref' = 'model:opus-mt-int8-v1'
        type = 'machine-learning-model'
        name = 'Opus-MT de-en and en-de INT8'
        version = [string]$pins.name
        licenses = @(
            [ordered]@{ license = [ordered]@{ id = 'CC-BY-4.0' } }
        )
        externalReferences = @($references)
        properties = @($properties)
        components = @($fileComponents)
    }
}

function Get-RuntimeComponents {
    param(
        [Parameter(Mandatory)]
        [string]$RuntimeHashPath,
        [switch]$AllowMissingRuntime
    )

    $fullRuntimeHashPath = [IO.Path]::GetFullPath($RuntimeHashPath)
    if (-not (Test-Path -LiteralPath $fullRuntimeHashPath)) {
        if ($AllowMissingRuntime) {
            return @()
        }

        throw "Native runtime hash file is required: $RuntimeHashPath"
    }

    foreach ($line in (Get-Content -LiteralPath $fullRuntimeHashPath)) {
        $trimmed = $line.Trim()
        if ($trimmed -notmatch '^(\S+)\s+([a-fA-F0-9]{64})$') {
            throw "Invalid runtime.sha256 line: $line"
        }

        $name = $Matches[1]
        $hash = $Matches[2].ToLowerInvariant()
        [ordered]@{
            'bom-ref' = "runtime:win-x64/$([Uri]::EscapeDataString($name))"
            type = 'file'
            name = $name
            hashes = @(
                [ordered]@{
                    alg = 'SHA-256'
                    content = $hash
                }
            )
            properties = @(
                [ordered]@{ name = 'winwispr:scope'; value = 'app' },
                [ordered]@{ name = 'winwispr:native-runtime-provenance'; value = (Convert-ToRelativePath -Path $fullRuntimeHashPath) }
            )
        }
    }
}

$lockFiles = @(
    [ordered]@{ Scope = 'app'; Path = (Join-Path $repositoryRoot 'src\WinWispr\packages.lock.json') },
    [ordered]@{ Scope = 'test'; Path = (Join-Path $repositoryRoot 'tests\WinWispr.Tests\packages.lock.json') }
)

$components = [Collections.Generic.List[object]]::new()
foreach ($lockFile in $lockFiles) {
    foreach ($package in (Get-LockPackages -LockPath $lockFile['Path'] -Scope $lockFile['Scope'])) {
        [void]$components.Add((New-NuGetComponent -Package $package))
    }
}
[void]$components.Add((New-ModelComponent))
[void]$components.Add((New-TranslationModelComponent))
foreach ($component in (Get-RuntimeComponents -RuntimeHashPath $RuntimeHashPath -AllowMissingRuntime:$AllowMissingRuntime)) {
    [void]$components.Add($component)
}
$runtimeManifest = Join-Path (Split-Path -Parent $RuntimeHashPath) 'runtime.dependencies.json'
if (Test-Path -LiteralPath $runtimeManifest) {
    $runtime = Get-Content -Raw -LiteralPath $runtimeManifest | ConvertFrom-Json
    if ($runtime.PSObject.Properties['ttsEnabled'] -and $runtime.ttsEnabled) {
        foreach ($dependency in @(
            @{ Name = 'espeak-ng'; Commit = $runtime.espeakSourceCommit; Hash = $runtime.espeakSourceSha256; License = $runtime.espeakLicense },
            @{ Name = 'piper-phonemize'; Commit = $runtime.piperPhonemizeSourceCommit; Hash = $runtime.piperPhonemizeSourceSha256; License = $runtime.piperPhonemizeLicense }
        )) {
            [void]$components.Add([ordered]@{
                'bom-ref' = "native-source:$($dependency.Name)"
                type = 'library'
                name = $dependency.Name
                version = $dependency.Commit
                hashes = @(@{ alg = 'SHA-256'; content = $dependency.Hash })
                licenses = @(@{ license = @{ id = $dependency.License } })
                externalReferences = @(@{ type = 'distribution'; url = "https://github.com/csukuangfj/$($dependency.Name)/archive/$($dependency.Commit).zip" })
                properties = @(@{ name = 'winwispr:linkage'; value = 'statically linked into sherpa runtime' })
            })
        }
    }
}
$components = @($components | Sort-Object @{ Expression = { $_.'bom-ref' } })

$metadataProperties = @()
foreach ($lockFile in $lockFiles) {
    $relativePath = Convert-ToRelativePath -Path $lockFile['Path']
    $sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $lockFile['Path']).Hash.ToLowerInvariant()
    $metadataProperties += [ordered]@{ name = "winwispr:lock-file:$($lockFile['Scope'])"; value = $relativePath }
    $metadataProperties += [ordered]@{ name = "winwispr:lock-file:$($lockFile['Scope']):sha256"; value = $sha256 }
}

$sbom = [ordered]@{
    bomFormat = 'CycloneDX'
    specVersion = '1.6'
    version = 1
    metadata = [ordered]@{
        component = [ordered]@{
            type = 'application'
            name = 'WinWispr'
        }
        properties = $metadataProperties
    }
    components = $components
}

$outputDirectory = Split-Path -Parent $fullOutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$json = $sbom | ConvertTo-Json -Depth 20
[IO.File]::WriteAllText($fullOutputPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
