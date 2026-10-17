#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
script="$repo_root/eng/New-Sbom.ps1"
notices="$repo_root/THIRD-PARTY-NOTICES.md"

test -f "$script"
test -f "$notices"

rg -q --fixed-strings '[Parameter(Mandatory)]' "$script"
rg -q --fixed-strings '[string]$OutputPath' "$script"
rg -q --fixed-strings '[string]$RuntimeHashPath' "$script"
if rg -n --fixed-strings '[string]$RuntimeHashPath = (Join-Path $PSScriptRoot' "$script"; then
    echo 'New-Sbom must resolve its default runtime path after $PSScriptRoot is initialized.' >&2
    exit 1
fi
rg -q --fixed-strings '[switch]$AllowMissingRuntime' "$script"
rg -q --fixed-strings 'Set-StrictMode' "$script"
rg -q --fixed-strings 'src\WinWispr\packages.lock.json' "$script"
rg -q --fixed-strings 'tests\WinWispr.Tests\packages.lock.json' "$script"
rg -q --fixed-strings 'ConvertFrom-Json' "$script"
rg -q --fixed-strings 'ConvertTo-Json' "$script"
rg -q --fixed-strings '[Convert]::FromBase64String' "$script"
rg -q --fixed-strings "ToString('x2')" "$script"
rg -q --fixed-strings 'contentHash' "$script"
rg -q --fixed-strings 'SHA-512' "$script"
rg -q --fixed-strings 'nuget:content-hash-base64' "$script"
rg -q --fixed-strings 'winwispr:scope' "$script"
rg -q --fixed-strings "Scope = 'app'" "$script"
rg -q --fixed-strings "Scope = 'test'" "$script"
rg -q --fixed-strings "name = 'winwispr:scope'" "$script"
rg -q --fixed-strings "value = \$Package['Scope']" "$script"
rg -q --fixed-strings 'runtime.sha256' "$script"
rg -q --fixed-strings 'ModelManifest.cs' "$script"
rg -q --fixed-strings 'ArchiveBytes' "$script"
rg -q --fixed-strings 'ArchiveSha256' "$script"
rg -q --fixed-strings 'Uri:' "$script"
rg -q --fixed-strings 'secrets?' "$script"
rg -q --fixed-strings 'models?' "$script"
rg -q --fixed-strings 'audio' "$script"
rg -q --fixed-strings 'transcripts?' "$script"

rg -q --fixed-strings 'License: MIT' "$notices"
rg -q --fixed-strings 'License: Apache-2.0' "$notices"
rg -q --fixed-strings 'License: CC-BY-4.0' "$notices"
rg -q --fixed-strings 'https://creativecommons.org/licenses/by/4.0/' "$notices"
rg -q --fixed-strings 'https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3' "$notices"
rg -q --fixed-strings 'License: NOASSERTION' "$notices"
rg -q --fixed-strings 'release blocker' "$notices"

# The bundled translation model: SBOM provenance and the attribution its license (CC BY 4.0) requires.
rg -q --fixed-strings 'translation-model-pins.json' "$script"
rg -q --fixed-strings 'model:opus-mt-int8-v1' "$script"
rg -q --fixed-strings 'ModelSpec $Name' "$script"
rg -q --fixed-strings 'Read-TranslationFiles -Pins $pins' "$script"
rg -q --fixed-strings 'Opus-MT opus-mt-de-en and opus-mt-en-de' "$notices"
rg -q --fixed-strings 'Language Technology Research Group at the University of Helsinki' "$notices"
rg -q --fixed-strings 'https://github.com/Helsinki-NLP/Opus-MT' "$notices"
rg -q --fixed-strings 'https://huggingface.co/Xenova/opus-mt-de-en' "$notices"
rg -q --fixed-strings 'https://huggingface.co/Xenova/opus-mt-en-de' "$notices"
rg -q --fixed-strings '399dfd68706739fffd503f876093e455ae268a06' "$notices"
rg -q --fixed-strings '1ca130c44c4c5441ef16d48aae521a424ab644f7' "$notices"
rg -q --fixed-strings 'converted to ONNX and quantized to int8' "$notices"
rg -q --fixed-strings 'eng/translation-model-pins.json' "$notices"
rg -q --fixed-strings 'Apache-2.0' "$notices"

if [ "$(uname -s)" = Darwin ]; then
    # New-Sbom.ps1 targets Windows; under PowerShell on macOS it does not finish.
    echo 'SKIP: PowerShell SBOM generation is Windows-only; static SBOM contract checks passed.'
    exit 0
fi

runner=''
if command -v pwsh >/dev/null 2>&1; then
    runner="$(command -v pwsh)"
elif command -v powershell >/dev/null 2>&1; then
    runner="$(command -v powershell)"
fi

if [ -z "$runner" ]; then
    echo 'SKIP: no PowerShell runner on PATH; static SBOM contract checks passed.'
    exit 0
fi

tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT
output="$tmp_dir/sbom.cdx.json"
missing_runtime_hash="$tmp_dir/missing-runtime.sha256"

if "$runner" -NoLogo -NoProfile -File "$script" -OutputPath "$output" -RuntimeHashPath "$missing_runtime_hash" >/dev/null 2>&1; then
    echo 'New-Sbom.ps1 accepted a missing runtime hash.' >&2
    exit 1
fi

"$runner" -NoLogo -NoProfile -File "$script" -OutputPath "$output" -AllowMissingRuntime
test -f "$output"

if "$runner" -NoLogo -NoProfile -File "$script" >/dev/null 2>&1; then
    echo 'New-Sbom.ps1 accepted a missing -OutputPath.' >&2
    exit 1
fi

mkdir "$tmp_dir/models"
if "$runner" -NoLogo -NoProfile -File "$script" -OutputPath "$tmp_dir/models/sbom.cdx.json" -AllowMissingRuntime >/dev/null 2>&1; then
    echo 'New-Sbom.ps1 accepted an output path below models.' >&2
    exit 1
fi

runtime_hash="$tmp_dir/runtime.sha256"
cat >"$runtime_hash" <<'EOF'
sherpa-onnx-c-api.dll 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
EOF
runtime_output="$tmp_dir/sbom-runtime.cdx.json"
"$runner" -NoLogo -NoProfile -File "$script" -OutputPath "$runtime_output" -RuntimeHashPath "$runtime_hash"

validator="$tmp_dir/validate-sbom.ps1"
cat >"$validator" <<'PS1'
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Path,
    [switch]$ExpectRuntime
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Equal {
    param([string]$Expected, [string]$Actual, [string]$Message)
    if ($Expected -cne $Actual) { throw "${Message}: expected '$Expected', got '$Actual'." }
}

function Get-PropertyValue {
    param($Object, [string]$Name)
    $property = @($Object.properties | Where-Object { $_.name -ceq $Name }) | Select-Object -First 1
    if ($null -eq $property) { return '' }
    return [string]$property.value
}

function Get-Component {
    param($Components, [string]$Name, [string]$Scope)
    return @($Components | Where-Object {
        $_.name -ceq $Name -and (Get-PropertyValue $_ 'winwispr:scope') -ceq $Scope
    }) | Select-Object -First 1
}

$data = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
Assert-Equal 'CycloneDX' ([string]$data.bomFormat) 'bomFormat'
Assert-Equal '1.6' ([string]$data.specVersion) 'specVersion'
Assert-Equal '1' ([string]$data.version) 'version'
Assert-Equal 'WinWispr' ([string]$data.metadata.component.name) 'metadata component'

$components = @($data.components)
$refs = @($components | ForEach-Object { [string]$_.'bom-ref' })
Assert-Equal ([string]$refs.Count) ([string](@($refs | Sort-Object -Unique).Count)) 'unique bom-ref'

$app = Get-Component $components 'Microsoft.WindowsAppSDK' 'app'
Assert-True ($null -ne $app) 'missing Microsoft.WindowsAppSDK app component'
Assert-Equal '2.4.0' ([string]$app.version) 'WindowsAppSDK version'
Assert-Equal 'Direct' (Get-PropertyValue $app 'nuget:dependency-type') 'WindowsAppSDK dependency type'
Assert-Equal 'SHA-512' ([string]$app.hashes[0].alg) 'WindowsAppSDK hash algorithm'
Assert-Equal 'b1d4b8f0214be44ab3141e5aeb746beaa74ae65f0daa6c56289fc35f0b05f306820cb5dd0fc4a34a720e7e9c73ab87f869485a797100c15440f6b0fc42f33eda' ([string]$app.hashes[0].content) 'WindowsAppSDK hash'
Assert-Equal '128' ([string]([string]$app.hashes[0].content).Length) 'WindowsAppSDK hash length'
Assert-Equal 'sdS48CFL5EqzFB5a63Rr6qdK5l8NqmxWKJ/DXwsF8waCDLXdD8SjSnIOfpxzq4f4aUhaeXEAwVRA9rD8QvM+2g==' (Get-PropertyValue $app 'nuget:content-hash-base64') 'WindowsAppSDK base64 hash'

$test = Get-Component $components 'MSTest.TestFramework' 'test'
Assert-True ($null -ne $test) 'missing MSTest.TestFramework test component'
Assert-Equal '4.3.3' ([string]$test.version) 'MSTest version'
Assert-Equal 'Direct' (Get-PropertyValue $test 'nuget:dependency-type') 'MSTest dependency type'
Assert-Equal '638392461ed5295de75befef22abdf277b1d2d67d527d1b5d4aa73ca7140ab7061fdfa4c697b7bdeff067cbe264f3ea927f8b1de171ea788b2eaa40d5ede34f3' ([string]$test.hashes[0].content) 'MSTest hash'

$model = @($components | Where-Object { $_.name -ceq 'Parakeet TDT 0.6B V3 INT8' }) | Select-Object -First 1
Assert-True ($null -ne $model) 'missing model component'
Assert-Equal 'machine-learning-model' ([string]$model.type) 'model type'
Assert-Equal '487170055' (Get-PropertyValue $model 'model:archive-bytes') 'model archive bytes'
Assert-Equal 'SHA-256' ([string]$model.hashes[0].alg) 'model hash algorithm'
Assert-Equal '5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf' ([string]$model.hashes[0].content) 'model hash'
Assert-Equal 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2' ([string]$model.externalReferences[0].url) 'model URL'

$translation = @($components | Where-Object { $_.name -ceq 'Opus-MT de-en and en-de INT8' }) | Select-Object -First 1
Assert-True ($null -ne $translation) 'missing translation model component'
Assert-Equal 'machine-learning-model' ([string]$translation.type) 'translation model type'
Assert-Equal 'model:opus-mt-int8-v1' ([string]$translation.'bom-ref') 'translation model bom-ref'
Assert-Equal 'opus-mt-int8-v1' ([string]$translation.version) 'translation model version'
Assert-True ($null -eq $translation.PSObject.Properties['hashes']) 'the translation model is no archive, so it has no single hash'
Assert-Equal 'CC-BY-4.0' ([string]$translation.licenses[0].license.id) 'translation license'
Assert-Equal 'app' (Get-PropertyValue $translation 'winwispr:scope') 'translation scope'
Assert-Equal 'bundled' (Get-PropertyValue $translation 'winwispr:distribution') 'translation distribution'
Assert-Equal 'TranslationData' (Get-PropertyValue $translation 'winwispr:package-path') 'translation package path'
Assert-Equal '10' (Get-PropertyValue $translation 'model:files') 'translation file count property'
Assert-Equal '217948232' (Get-PropertyValue $translation 'model:total-bytes') 'translation total bytes'
Assert-Equal 'Xenova/opus-mt-de-en@399dfd68706739fffd503f876093e455ae268a06' (Get-PropertyValue $translation 'model:export:de-en') 'de-en export provenance'
Assert-Equal 'Xenova/opus-mt-en-de@1ca130c44c4c5441ef16d48aae521a424ab644f7' (Get-PropertyValue $translation 'model:export:en-de') 'en-de export provenance'
Assert-Equal 'Helsinki-NLP/opus-mt-de-en@1a922f3b32a8e809e17a47d4b32142d8105924e5' (Get-PropertyValue $translation 'model:original:de-en') 'de-en original provenance'
Assert-Equal 'Helsinki-NLP/opus-mt-en-de@6183067f769a302e3861815543b9f312c71b0ca4' (Get-PropertyValue $translation 'model:original:en-de') 'en-de original provenance'
$translationFiles = @($translation.components)
Assert-Equal '10' ([string]$translationFiles.Count) 'translation file components'
foreach ($translationFile in $translationFiles) {
    Assert-Equal 'file' ([string]$translationFile.type) 'translation file type'
    Assert-Equal 'SHA-256' ([string]$translationFile.hashes[0].alg) 'translation file hash algorithm'
    Assert-True ([string]$translationFile.hashes[0].content -cmatch '^[0-9a-f]{64}$') 'translation file hash'
    Assert-True ([string]$translationFile.externalReferences[0].url -cmatch '^https://huggingface\.co/Xenova/opus-mt-(de-en|en-de)/resolve/[0-9a-f]{40}/') 'translation file URL'
}
$germanEncoder = @($translationFiles | Where-Object { $_.name -ceq 'opus-mt-de-en/onnx/encoder_model_quantized.onnx' }) | Select-Object -First 1
Assert-True ($null -ne $germanEncoder) 'missing de-en encoder file component'
Assert-Equal '4cedda8f8c89b72a42b3c6cd1e7a27f2de24457093e3bf80cb3e46829641fcd8' ([string]$germanEncoder.hashes[0].content) 'de-en encoder hash'
Assert-Equal 'https://huggingface.co/Xenova/opus-mt-de-en/resolve/399dfd68706739fffd503f876093e455ae268a06/onnx/encoder_model_quantized.onnx' ([string]$germanEncoder.externalReferences[0].url) 'de-en encoder URL'
Assert-Equal '49366942' (Get-PropertyValue $germanEncoder 'model:bytes') 'de-en encoder bytes'
Assert-Equal '2' ([string]@($components | Where-Object { $_.type -ceq 'machine-learning-model' }).Count) 'model component count'

Assert-Equal 'src/WinWispr/packages.lock.json' (Get-PropertyValue $data.metadata 'winwispr:lock-file:app') 'app lock path'
Assert-Equal '64' ([string](Get-PropertyValue $data.metadata 'winwispr:lock-file:app:sha256').Length) 'app lock hash length'
Assert-Equal 'tests/WinWispr.Tests/packages.lock.json' (Get-PropertyValue $data.metadata 'winwispr:lock-file:test') 'test lock path'
Assert-Equal '64' ([string](Get-PropertyValue $data.metadata 'winwispr:lock-file:test:sha256').Length) 'test lock hash length'

if ($ExpectRuntime) {
    $runtime = @($components | Where-Object { $_.name -ceq 'sherpa-onnx-c-api.dll' }) | Select-Object -First 1
    Assert-True ($null -ne $runtime) 'missing native runtime component'
    Assert-Equal 'file' ([string]$runtime.type) 'runtime type'
    Assert-Equal 'SHA-256' ([string]$runtime.hashes[0].alg) 'runtime hash algorithm'
    Assert-Equal '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef' ([string]$runtime.hashes[0].content) 'runtime hash'
    Assert-True ((Get-PropertyValue $runtime 'winwispr:native-runtime-provenance') -like '*runtime.sha256') 'runtime provenance'
}
PS1

"$runner" -NoLogo -NoProfile -File "$validator" -Path "$output"
"$runner" -NoLogo -NoProfile -File "$validator" -Path "$runtime_output" -ExpectRuntime
