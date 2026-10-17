$ErrorActionPreference = 'Stop'

$workspace = Join-Path ([IO.Path]::GetTempPath()) ("winwispr-sherpa-" + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $workspace 'source'
$build = Join-Path $workspace 'build'
$install = Join-Path $workspace 'install'
$output = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\WinWispr\Runtime\win-x64'))
$expectedCommit = '3dc7c569f31ca2cd4a20ed6f7db780327e6714c5'

function Invoke-RequiredCommand {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,
        [Parameter(Mandatory)]
        [string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($ArgumentList -join ' ')"
    }
}

function Replace-RequiredText {
    param(
        [Parameter(Mandatory)] [string]$Content,
        [Parameter(Mandatory)] [string]$OldValue,
        [Parameter(Mandatory)] [string]$NewValue,
        [Parameter(Mandatory)] [int]$ExpectedCount,
        [Parameter(Mandatory)] [string]$Name
    )

    $count = [regex]::Matches($Content, [regex]::Escape($OldValue)).Count
    if ($count -ne $ExpectedCount) {
        throw "Unexpected ${Name} declaration count: expected $ExpectedCount, found $count."
    }
    $Content.Replace($OldValue, $NewValue)
}

function Get-DumpbinPath {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw "vswhere.exe was not found: $vswhere"
    }

    $installationPath = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
    if ([string]::IsNullOrWhiteSpace($installationPath)) {
        throw 'No Visual Studio installation with the x64 C++ tools was found.'
    }

    $compilerRoot = Join-Path $installationPath 'VC\Tools\MSVC'
    $dumpbin = Get-ChildItem -LiteralPath $compilerRoot -Filter 'dumpbin.exe' -Recurse -File |
        Where-Object { $_.FullName -match '\\bin\\Host(arm64|x64)\\x64\\dumpbin\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $dumpbin) {
        throw "dumpbin.exe was not found below $compilerRoot"
    }

    return $dumpbin.FullName
}

function Get-NativeDependencies {
    param(
        [Parameter(Mandatory)]
        [string]$DumpbinPath,
        [Parameter(Mandatory)]
        [string]$DllPath
    )

    $lines = & $DumpbinPath /dependents $DllPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "dumpbin failed for $DllPath"
    }

    foreach ($line in $lines) {
        $dependency = $line.ToString().Trim()
        if ($dependency -match '^[A-Za-z0-9_.-]+\.dll$') {
            $Matches[0]
        }
    }
}

try {
    $git = (Get-Command git.exe -ErrorAction Stop).Source
    $cmake = if (Test-Path -LiteralPath 'C:\Program Files\CMake\bin\cmake.exe') {
        'C:\Program Files\CMake\bin\cmake.exe'
    }
    else {
        (Get-Command cmake.exe -ErrorAction Stop).Source
    }
    $dumpbin = Get-DumpbinPath

    New-Item -ItemType Directory -Force -Path $workspace | Out-Null
    Invoke-RequiredCommand -FilePath $git -ArgumentList @('clone', '--branch', 'v1.13.5', '--depth', '1', 'https://github.com/k2-fsa/sherpa-onnx.git', $source)

    $actualCommit = (& $git -C $source rev-parse HEAD).Trim()
    if ($actualCommit -ne $expectedCommit) {
        throw "Unexpected sherpa commit: $actualCommit"
    }

    $directMlCmakePath = Join-Path $source 'cmake\onnxruntime-win-x64-directml.cmake'
    $directMlCmake = [IO.File]::ReadAllText($directMlCmakePath)
    $directMlCmake = Replace-RequiredText -Content $directMlCmake `
        -OldValue '1.14.1' -NewValue '1.24.4' -ExpectedCount 5 -Name 'ONNX Runtime DirectML version'
    $directMlCmake = Replace-RequiredText -Content $directMlCmake `
        -OldValue 'c8ae7623385b19cd5de968d0df5383e13b97d1b3a6771c9177eac15b56013a5a' `
        -NewValue '57e9f11b73437bef7a309496135d4c1f96b1a8e9ddba60013fa27bfc1d788681' `
        -ExpectedCount 1 -Name 'ONNX Runtime DirectML package hash'
    $directMlCmake = Replace-RequiredText -Content $directMlCmake `
        -OldValue '1.15.0' -NewValue '1.15.4' -ExpectedCount 5 -Name 'DirectML version'
    $directMlCmake = Replace-RequiredText -Content $directMlCmake `
        -OldValue '10d175f8e97447712b3680e3ac020bbb8eafdf651332b48f09ffee2eec801c23' `
        -NewValue '4e7cb7ddce8cf837a7a75dc029209b520ca0101470fcdf275c1f49736a3615b9' `
        -ExpectedCount 1 -Name 'DirectML package hash'
    [IO.File]::WriteAllText($directMlCmakePath, $directMlCmake, [Text.UTF8Encoding]::new($false))

    # WinWispr needs a concrete DXGI adapter and an observable failure at the
    # C ABI boundary. Keep the patch pinned and fail if upstream context drifts.
    $sessionPath = Join-Path $source 'sherpa-onnx\csrc\session.cc'
    $session = [IO.File]::ReadAllText($sessionPath).Replace("`r`n", "`n")
    $session = Replace-RequiredText -Content $session -ExpectedCount 1 -Name 'DirectML adapter parameter' `
        -OldValue '  ParseConfigFile(config_path, config);' -NewValue @'
  if (provider_str == "directml" && config_path.rfind("device_id=", 0) == 0) {
    config["device_id"] = config_path.substr(10);
    return;
  }
  ParseConfigFile(config_path, config);
'@
    $session = Replace-RequiredText -Content $session -ExpectedCount 1 -Name 'DirectML device index' `
        -OldValue '      int32_t device_id = 0;' -NewValue @'
      int32_t device_id = 0;
      auto device = config.find("device_id");
      if (device != config.end()) {
        size_t consumed = 0;
        device_id = std::stoi(device->second, &consumed);
        if (device_id < 0 || consumed != device->second.size()) {
          throw std::invalid_argument("Invalid DirectML device index");
        }
      }
'@
    $session = Replace-RequiredText -Content $session -ExpectedCount 1 -Name 'DirectML failure propagation' `
        -OldValue @'
      if (status) {
        const auto &api = Ort::GetApi();
        const char *msg = api.GetErrorMessage(status);
        SHERPA_ONNX_LOGE("Failed to enable DirectML: %s. Fallback to cpu", msg);
        api.ReleaseStatus(status);
      }
'@ -NewValue '      Ort::ThrowOnError(status);'
    $session = Replace-RequiredText -Content $session -ExpectedCount 1 -Name 'DirectML unsupported build' `
        -OldValue '      SHERPA_ONNX_LOGE("DirectML is for Windows only. Fallback to cpu!");' `
        -NewValue '      throw std::runtime_error("DirectML is not compiled in");'
    $session = Replace-RequiredText -Content $session -ExpectedCount 1 -Name 'DirectML exception include' `
        -OldValue '#include <string>' -NewValue "#include <string>`n#include <stdexcept>"
    [IO.File]::WriteAllText($sessionPath, $session, [Text.UTF8Encoding]::new($false))

    $cApiPath = Join-Path $source 'sherpa-onnx\c-api\c-api.cc'
    $cApi = [IO.File]::ReadAllText($cApiPath).Replace("`r`n", "`n")
    $cApi = Replace-RequiredText -Content $cApi -ExpectedCount 1 -Name 'Checked recognizer creation' `
        -OldValue @'
const SherpaOnnxOfflineRecognizer *SherpaOnnxCreateOfflineRecognizer(
    const SherpaOnnxOfflineRecognizerConfig *config) {
  sherpa_onnx::OfflineRecognizerConfig recognizer_config =
      GetOfflineRecognizerConfig(config);

  if (!recognizer_config.Validate()) {
    SHERPA_ONNX_LOGE("Errors in config");
    return nullptr;
  }

  SherpaOnnxOfflineRecognizer *recognizer = new SherpaOnnxOfflineRecognizer;

  recognizer->impl =
      std::make_unique<sherpa_onnx::OfflineRecognizer>(recognizer_config);

  return recognizer;
}
'@ -NewValue @'
static thread_local int winwispr_recognizer_status = 0;

extern "C" __declspec(dllexport) int WinWisprGetLastRecognizerStatus() {
  return winwispr_recognizer_status;
}

const SherpaOnnxOfflineRecognizer *SherpaOnnxCreateOfflineRecognizer(
    const SherpaOnnxOfflineRecognizerConfig *config) {
  winwispr_recognizer_status = 0;
  try {
    auto recognizer_config = GetOfflineRecognizerConfig(config);
    if (!recognizer_config.Validate()) return nullptr;
    auto recognizer = std::make_unique<SherpaOnnxOfflineRecognizer>();
    recognizer->impl =
        std::make_unique<sherpa_onnx::OfflineRecognizer>(recognizer_config);
    winwispr_recognizer_status = 1;
    return recognizer.release();
  } catch (...) {
    // No exception text: it may contain user paths. Never cross the C ABI.
    return nullptr;
  }
}
'@
    $cApi = Replace-RequiredText -Content $cApi -ExpectedCount 1 -Name 'Checked SherpaOnnxCreateOfflineTts' `
        -OldValue @'
const SherpaOnnxOfflineTts *SherpaOnnxCreateOfflineTts(
    const SherpaOnnxOfflineTtsConfig *config) {
  auto tts_config = GetOfflineTtsConfig(config);

  if (!tts_config.Validate()) {
    SHERPA_ONNX_LOGE("Errors in config");
    return nullptr;
  }

  SherpaOnnxOfflineTts *tts = new SherpaOnnxOfflineTts;

  tts->impl = std::make_unique<sherpa_onnx::OfflineTts>(tts_config);

  return tts;
}
'@ -NewValue @'
static thread_local int winwispr_tts_status = 0;
extern "C" __declspec(dllexport) int WinWisprGetLastTtsStatus() {
  return winwispr_tts_status;
}

const SherpaOnnxOfflineTts *SherpaOnnxCreateOfflineTts(
    const SherpaOnnxOfflineTtsConfig *config) {
  winwispr_tts_status = 0;
  try {
    if (!config) return nullptr;
    auto tts_config = GetOfflineTtsConfig(config);
    if (!tts_config.Validate()) return nullptr;
    auto tts = std::make_unique<SherpaOnnxOfflineTts>();
    tts->impl = std::make_unique<sherpa_onnx::OfflineTts>(tts_config);
    winwispr_tts_status = 1;
    return tts.release();
  } catch (...) {
    // Never propagate native exceptions or user paths across the C ABI.
    return nullptr;
  }
}
'@
    $cApi = Replace-RequiredText -Content $cApi -ExpectedCount 1 -Name 'Checked SherpaOnnxOfflineTtsGenerateWithConfig' `
        -OldValue @'
const SherpaOnnxGeneratedAudio *SherpaOnnxOfflineTtsGenerateWithConfig(
    const SherpaOnnxOfflineTts *tts, const char *text,
    const SherpaOnnxGenerationConfig *config,
    SherpaOnnxGeneratedAudioProgressCallbackWithArg callback, void *arg) {
  if (!tts) {
    SHERPA_ONNX_LOGE("tts is nullptr");
    return nullptr;
  }

  if (!text) {
    SHERPA_ONNX_LOGE("text is nullptr");
    return nullptr;
  }

  if (!config) {
    SHERPA_ONNX_LOGE("config is nullptr");
    return nullptr;
  }

  if (callback) {
    auto wrapper = [callback, arg](const float *samples, int32_t n,
                                   float progress) {
      return callback(samples, n, progress, arg);
    };

    return SherpaOnnxOfflineTtsGenerateInternal(tts, text, config,
                                                std::move(wrapper));
  } else {
    return SherpaOnnxOfflineTtsGenerateInternal(tts, text, config, nullptr);
  }
}
'@ -NewValue @'
const SherpaOnnxGeneratedAudio *SherpaOnnxOfflineTtsGenerateWithConfig(
    const SherpaOnnxOfflineTts *tts, const char *text,
    const SherpaOnnxGenerationConfig *config,
    SherpaOnnxGeneratedAudioProgressCallbackWithArg callback, void *arg) try {
  if (!tts) {
    SHERPA_ONNX_LOGE("tts is nullptr");
    return nullptr;
  }

  if (!text) {
    SHERPA_ONNX_LOGE("text is nullptr");
    return nullptr;
  }

  if (!config) {
    SHERPA_ONNX_LOGE("config is nullptr");
    return nullptr;
  }

  if (callback) {
    auto wrapper = [callback, arg](const float *samples, int32_t n,
                                   float progress) {
      return callback(samples, n, progress, arg);
    };

    return SherpaOnnxOfflineTtsGenerateInternal(tts, text, config,
                                                std::move(wrapper));
  } else {
    return SherpaOnnxOfflineTtsGenerateInternal(tts, text, config, nullptr);
  }
} catch (...) {
  return nullptr;
}
'@
    [IO.File]::WriteAllText($cApiPath, $cApi, [Text.UTF8Encoding]::new($false))

    # These TTS-only translation units otherwise terminate the host process on
    # invalid metadata/tokens/data. Include metadata macro expansions in the guard.
    foreach ($relative in @('sherpa-onnx\csrc\offline-tts-vits-model.cc', 'sherpa-onnx\csrc\piper-phonemize-lexicon.cc')) {
        $path = Join-Path $source $relative
        $content = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
        $content = Replace-RequiredText -Content $content -ExpectedCount 1 -Name "TTS process error boundary $relative" `
            -OldValue 'namespace sherpa_onnx {' -NewValue @'
#include <stdexcept>
#undef SHERPA_ONNX_EXIT
#define SHERPA_ONNX_EXIT(code) throw std::runtime_error("Invalid local TTS model or data")
namespace sherpa_onnx {
'@
        [IO.File]::WriteAllText($path, $content, [Text.UTF8Encoding]::new($false))
    }
    $vitsPath = Join-Path $source 'sherpa-onnx\csrc\offline-tts-vits-impl.h'
    $vits = [IO.File]::ReadAllText($vitsPath)
    $vits = Replace-RequiredText -Content $vits -ExpectedCount 2 -Name 'VITS frontend error boundary' `
        -OldValue 'SHERPA_ONNX_EXIT(-1);' -NewValue 'throw std::runtime_error("Invalid VITS frontend");'
    $vits = Replace-RequiredText -Content $vits -ExpectedCount 1 -Name 'VITS exception include' `
        -OldValue '#include <memory>' -NewValue "#include <memory>`n#include <stdexcept>"
    [IO.File]::WriteAllText($vitsPath, $vits, [Text.UTF8Encoding]::new($false))

    $cmakeConfigureArguments = @(
        '-S', $source,
        '-B', $build,
        '-A', 'x64',
        '-DCMAKE_BUILD_TYPE=Release',
        "-DCMAKE_INSTALL_PREFIX=$install",
        '-DBUILD_SHARED_LIBS=ON',
        '-DSHERPA_ONNX_ENABLE_DIRECTML=ON',
        '-DSHERPA_ONNX_ENABLE_C_API=ON',
        '-DSHERPA_ONNX_ENABLE_BINARY=OFF',
        '-DSHERPA_ONNX_ENABLE_PORTAUDIO=OFF',
        '-DSHERPA_ONNX_ENABLE_TTS=ON',
        '-DSHERPA_ONNX_ENABLE_SPEAKER_DIARIZATION=OFF',
        '-DSHERPA_ONNX_ENABLE_WEBSOCKET=OFF',
        '-DSHERPA_ONNX_BUILD_C_API_EXAMPLES=OFF'
    )
    Invoke-RequiredCommand -FilePath $cmake -ArgumentList $cmakeConfigureArguments
    Invoke-RequiredCommand -FilePath $cmake -ArgumentList @('--build', $build, '--config', 'Release', '--target', 'install')

    $installedDlls = @(
        Get-ChildItem -LiteralPath $install -Recurse -Filter '*.dll' -File |
            Where-Object { $_.Name -notmatch '(?i)\.Debug\.dll$' } |
            Sort-Object @{ Expression = { if ($_.Directory.Name -eq 'bin') { 0 } else { 1 } } }, Name |
            Group-Object Name |
            ForEach-Object { $_.Group | Select-Object -First 1 }
    )
    if ($installedDlls.Count -eq 0) {
        throw 'The sherpa install produced no DLLs.'
    }

    New-Item -ItemType Directory -Force -Path $output | Out-Null
    Get-ChildItem -LiteralPath $output -Filter '*.dll' -File | Remove-Item -Force
    foreach ($dll in $installedDlls) {
        Copy-Item -LiteralPath $dll.FullName -Destination $output -Force
    }

    $runtimeDlls = @(Get-ChildItem -LiteralPath $output -Filter '*.dll' -File)
    $required = @('sherpa-onnx-c-api.dll', 'onnxruntime.dll')
    foreach ($name in $required) {
        if ($null -eq ($runtimeDlls | Where-Object { $_.Name -ieq $name })) {
            throw "Required runtime DLL is missing: $name"
        }
    }

    $localNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($dll in $runtimeDlls) {
        $localNames.Add($dll.Name) | Out-Null
    }

    $systemDirectories = @(
        (Join-Path $env:windir 'System32'),
        (Join-Path $env:windir 'SysArm32'),
        (Join-Path $env:windir 'SysWOW64')
    )
    $missingDependencies = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($dll in $runtimeDlls) {
        foreach ($dependency in (Get-NativeDependencies -DumpbinPath $dumpbin -DllPath $dll.FullName)) {
            if ($localNames.Contains($dependency) -or $dependency -match '^(api|ext)-ms-win-' -or ($systemDirectories | Where-Object { Test-Path -LiteralPath (Join-Path $_ $dependency) })) {
                continue
            }

            $missingDependencies.Add($dependency) | Out-Null
        }
    }
    if ($missingDependencies.Count -gt 0) {
        throw "Native dependencies are not beside the runtime: $($missingDependencies -join ', ')"
    }

    $hashLines = foreach ($dll in ($runtimeDlls | Sort-Object Name)) {
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $dll.FullName).Hash.ToLowerInvariant()
        "$($dll.Name) $hash"
    }
    [IO.File]::WriteAllLines(
        (Join-Path $output 'runtime.sha256'),
        [string[]]$hashLines,
        [Text.ASCIIEncoding]::new())
    $dependencyManifest = [ordered]@{
        ttsBoundaryVersion = 2
        ttsEnabled = $true
        ttsProvider = 'cpu'
        espeakSourceCommit = 'ed530aa113046142eb5115cf2fc9157854d0ffe1'
        espeakSourceSha256 = 'e4e262cbe34f7fe21f91f1ba3397f2728e1f30eafbae7853f2b753a9ed13f0dd'
        espeakLicense = 'GPL-3.0-or-later'
        piperPhonemizeSourceCommit = 'f3ff95afc03640bc1399e113e83361192a2fafb4'
        piperPhonemizeSourceSha256 = 'd9cca4e2bdc7d6dd8dffb96a4668283dbd3f77a9c194a3e530c1e8eba9406a5d'
        piperPhonemizeLicense = 'MIT'
        directMlAdapterSelectionVersion = 1
        sherpaCommit = $expectedCommit
        onnxRuntimeDirectMlVersion = '1.24.4'
        onnxRuntimeDirectMlPackageSha256 = '57e9f11b73437bef7a309496135d4c1f96b1a8e9ddba60013fa27bfc1d788681'
        directMlVersion = '1.15.4'
        directMlPackageSha256 = '4e7cb7ddce8cf837a7a75dc029209b520ca0101470fcdf275c1f49736a3615b9'
    } | ConvertTo-Json
    [IO.File]::WriteAllText(
        (Join-Path $output 'runtime.dependencies.json'),
        $dependencyManifest,
        [Text.UTF8Encoding]::new($false))

}
finally {
    if (Test-Path -LiteralPath $workspace) {
        Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction SilentlyContinue
    }
}
