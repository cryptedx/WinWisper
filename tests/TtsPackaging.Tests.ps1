param([switch]$RealVoices)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\eng\ArtifactHash.ps1')
. (Join-Path $PSScriptRoot '..\eng\Copy-TtsVoices.ps1')
foreach ($script in @('Build-Portable.ps1', 'Build-Installer.ps1', 'Build-ModelPackage.ps1', 'ModelPackage.ps1', 'CodeSigning.ps1', 'Build-SherpaRuntime.ps1', 'Deploy-WindowsVm.ps1', 'New-Sbom.ps1', 'Copy-TtsVoices.ps1')) {
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot "..\eng\$script"), [ref]$null, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count -ne 0) { throw "PowerShell parse error in ${script}: $parseErrors" }
}
$root = Join-Path ([IO.Path]::GetTempPath()) ('winwispr-tts-packaging-' + [Guid]::NewGuid().ToString('N'))
try {
    if ($RealVoices) {
        $repository = Split-Path -Parent $PSScriptRoot
        Copy-TtsVoices -InputRoot (Join-Path $repository 'artifacts\voices') -Destination (Join-Path $root 'VoiceData') -ManifestRoot (Join-Path $repository 'eng\voices')
        $models = @(Get-ChildItem (Join-Path $root 'VoiceData') -Recurse -Filter '*.onnx')
        if ($models.Count -ne 2) { throw 'Expected both real voice models in release payload.' }
        Write-Output "Real TTS release payload: PASS ($($models.Count) models, all copied file hashes verified)"
        return
    }
    foreach ($language in @('de-DE', 'en-US')) {
        $voiceRoot = Join-Path $root "input\$language"
        New-Item -ItemType Directory -Path (Join-Path $voiceRoot 'data') -Force | Out-Null
        $files = foreach ($name in @('model.onnx', 'tokens.txt', 'LICENSE', 'data/phonemes')) {
            $path = Join-Path $voiceRoot $name
            [IO.File]::WriteAllText($path, 'test fixture only')
            @{ Path = $name; Bytes = (Get-Item -LiteralPath $path).Length; Sha256 = (Get-ArtifactSha256 -LiteralPath $path) }
        }
        @{ Id = $language; Language = $language; Model = 'model.onnx'; Tokens = 'tokens.txt'; License = 'LICENSE'; DataDirectory = 'data'; Files = @($files) } |
            ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $voiceRoot 'voice.json')
    }
    Copy-TtsVoices -InputRoot (Join-Path $root 'input') -Destination (Join-Path $root 'valid')
    if (-not (Test-Path -LiteralPath (Join-Path $root 'valid\de-DE\model.onnx'))) { throw 'German voice was not copied.' }
    if (-not (Test-Path -LiteralPath (Join-Path $root 'valid\en-US\model.onnx'))) { throw 'English voice was not copied.' }
    [IO.File]::WriteAllText((Join-Path $root 'input\de-DE\model.onnx'), 'corrupt')
    $rejected = $false
    try { Copy-TtsVoices -InputRoot (Join-Path $root 'input') -Destination (Join-Path $root 'invalid') }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Corrupt voice was accepted.' }
    Remove-Item -LiteralPath (Join-Path $root 'input\de-DE') -Recurse -Force
    $rejected = $false
    try { Copy-TtsVoices -InputRoot (Join-Path $root 'input') -Destination (Join-Path $root 'missing') }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Missing German voice was accepted.' }
    & (Join-Path $PSScriptRoot '..\eng\New-Sbom.ps1') -OutputPath (Join-Path $root 'sbom.json')
    $bom = Get-Content -Raw -LiteralPath (Join-Path $root 'sbom.json') | ConvertFrom-Json
    foreach ($name in @('espeak-ng', 'piper-phonemize')) {
        $component = @($bom.components | Where-Object { $_.name -eq $name })
        if ($component.Count -ne 1 -or $component[0].hashes[0].content.Length -ne 64 -or
            -not $component[0].licenses[0].license.id) { throw "Missing native source provenance: $name" }
    }
    Write-Output 'TTS packaging integrity: PASS (synthetic file fixtures, no voice acceptance)'
}
finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
