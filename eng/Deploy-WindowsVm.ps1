[CmdletBinding()]
param(
    [switch]$RebuildRuntime,
    [Parameter(Mandatory)]
    [string]$RepositoryPath,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$SourceCommit,
    [string]$ReleaseRequestPath,
    [string]$ReleaseRequestSha256,
    [string]$SourceArchiveSha256,
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string]$SourceTreeSha256,
    [switch]$DryRun,
    [switch]$Check,
    [switch]$NoBuild,
    [string]$TestFilter,
    # Builds the WinWispr Models installer (or reuses the cached one) and reports it; nothing is installed.
    [switch]$ModelPackage,
    [ValidateSet('clean', 'dirty')]
    [string]$WorkingTreeState = 'clean'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ArtifactHash.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path $PSScriptRoot 'ReleaseRequest.ps1')
. (Join-Path $PSScriptRoot 'DevelopmentVersion.ps1')
. (Join-Path $PSScriptRoot 'SourceTreeBuild.ps1')
. (Join-Path $PSScriptRoot 'ModelPackage.ps1')
. (Join-Path $PSScriptRoot 'CodeSigning.ps1')

$workspace = [IO.Path]::GetFullPath($RepositoryPath)
$source = $workspace
$artifacts = Join-Path $workspace 'artifacts'
$controlRoot = Join-Path $artifacts '.vm-control'
$portableArtifacts = Join-Path $artifacts 'portable'
$installerArtifacts = Join-Path $artifacts 'installer'
$modelArtifacts = Join-Path $artifacts 'models'
$runtimeCache = Join-Path $artifacts 'runtime-cache\win-x64'
$runtimeMarker = Join-Path $artifacts 'runtime-cache\build-script.sha256'
$install = Join-Path $env:LOCALAPPDATA 'Programs\WinWispr'
$installExe = Join-Path $install 'WinWispr.exe'
$appData = Join-Path $env:LOCALAPPDATA 'WinWispr'
$taskName = 'WinWispr-Interactive-Verify'
$installerTaskName = 'WinWispr-Installer-Install'
$taskCreated = $false
$installerTaskCreated = $false
$startedProcessIds = @()
$appDataExisted = Test-Path -LiteralPath $appData
$runtimeBuild = 'cache'
$releaseMode = -not [string]::IsNullOrWhiteSpace($ReleaseRequestPath)
$defaultTestFilter = if ($releaseMode) {
    'TestCategory!=WindowsAcceptance'
} else {
    'TestCategory!=WindowsAcceptance&TestCategory!=TaskBoardE2E'
}
$releaseRequest = $null
$releaseVersion = $null
$releaseLogDirectory = $null
$releaseAcceptanceLog = $null
$releaseRequestPathFull = $null
$workerLock = $null
$checkArtifacts = Join-Path $artifacts 'dev-check'
$totalTimer = [Diagnostics.Stopwatch]::StartNew()
# The last phase that started; a failure is reported as WORKER_FAILURE=<phase>: <error>.
$currentPhase = 'setup'

if ($Check -and ($releaseMode -or $DryRun -or $RebuildRuntime)) {
    throw '-Check cannot be combined with deployment or release options.'
}
if ($ModelPackage -and ($Check -or $releaseMode -or $DryRun -or $RebuildRuntime -or $NoBuild)) {
    throw '-ModelPackage cannot be combined with checks, releases, dry-runs, or runtime rebuilds.'
}
if (-not $Check -and (-not [string]::IsNullOrEmpty($TestFilter) -or $WorkingTreeState -ne 'clean')) {
    throw 'Working-tree checks and test filters require -Check.'
}
if ($PSBoundParameters.ContainsKey('TestFilter') -and [string]::IsNullOrWhiteSpace($TestFilter)) {
    throw 'TestFilter must not be empty.'
}
if ($NoBuild -and -not $Check) {
    throw '-NoBuild requires -Check.'
}

function Write-Phase {
    param([string]$Name, [string]$State, [double]$Seconds = 0)
    $line = 'PHASE={0} STATE={1} UTC={2} SECONDS={3}' -f $Name, $State,
        [DateTime]::UtcNow.ToString('o'), $Seconds.ToString('F3', [Globalization.CultureInfo]::InvariantCulture)
    Write-Host $line
    if ($State -eq 'START') { $script:currentPhase = $Name }
    if ($script:releaseLogDirectory) {
        Add-Content -LiteralPath (Join-Path $script:releaseLogDirectory 'timings.log') -Value $line -Encoding utf8
    }
}

function Invoke-RequiredCommand {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$ArgumentList,
        [string]$LogPath
    )

    $commandLine = "$FilePath $($ArgumentList -join ' ')"
    $phase = if ($LogPath) { [IO.Path]::GetFileNameWithoutExtension($LogPath) } else { [IO.Path]::GetFileName($FilePath) }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    Write-Phase $phase 'START'
    $savedErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& $FilePath @ArgumentList 2>&1 | ForEach-Object {
            $line = $_.ToString()
            Write-Host $line
            $line
        })
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
        @("> $commandLine") + $output | Set-Content -LiteralPath $LogPath -Encoding utf8
    }
    Write-Phase $phase $(if ($exitCode -eq 0) { 'PASS' } else { 'FAIL' }) $timer.Elapsed.TotalSeconds
    if ($exitCode -ne 0) {
        $details = @($output | Select-Object -Last 40)
        if ($details.Count -gt 0) {
            throw ("Command failed with exit code ${exitCode}: $commandLine`n" + ($details -join [Environment]::NewLine))
        }
        throw "Command failed with exit code ${exitCode}: $commandLine"
    }
}

function Get-ReleaseLogPath {
    param([Parameter(Mandatory)] [string]$Name)

    if ([string]::IsNullOrWhiteSpace($script:releaseLogDirectory)) {
        return $null
    }
    Join-Path $script:releaseLogDirectory $Name
}

function Assert-Sha256 {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$ExpectedSha256,
        [Parameter(Mandatory)] [string]$Name
    )

    if ($ExpectedSha256 -cnotmatch '^[0-9a-fA-F]{64}$') {
        throw "$Name SHA-256 is invalid: $ExpectedSha256"
    }
    $actual = (Get-ArtifactSha256 -LiteralPath $Path)
    if ($actual -cne $ExpectedSha256.ToLowerInvariant()) {
        throw "$Name SHA-256 mismatch: expected $($ExpectedSha256.ToLowerInvariant()), got $actual"
    }
    $actual
}

function Test-ReparseItem {
    param([Parameter(Mandatory)]$Item)
    (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
}

function Assert-NoReparsePath {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Name,
        [switch]$RequireExists
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if ($RequireExists -and -not (Test-Path -LiteralPath $fullPath)) {
        throw "${Name} is missing: $fullPath"
    }

    $root = [IO.Path]::GetPathRoot($fullPath)
    $current = $root
    foreach ($segment in @($fullPath.Substring($root.Length) -split '[\\/]' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        $current = Join-Path $current $segment
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (Test-ReparseItem -Item $item) {
                throw "${Name} contains a reparse point: $current"
            }
        }
        else {
            break
        }
    }
    $fullPath
}

function Assert-NoReparseTree {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Name
    )

    $root = Assert-NoReparsePath -Path $Path -Name $Name -RequireExists
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
    $root
}

function Remove-SafeTree {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Name
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (Test-ReparseItem -Item $item) {
        throw "Refusing to remove ${Name} because it is a reparse point: $Path"
    }
    if ($item.PSIsContainer) {
        Assert-NoReparseTree -Path $Path -Name $Name | Out-Null
    }
    Remove-Item -LiteralPath $Path -Recurse -Force
}

function Clear-SafeDirectory {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Name
    )

    Assert-NoReparseTree -Path $Path -Name $Name | Out-Null
    foreach ($child in @(Get-ChildItem -LiteralPath $Path -Force)) {
        Remove-SafeTree -Path $child.FullName -Name $Name
    }
}

function Assert-ControlFile {
    param([Parameter(Mandatory)] [string]$Path)

    $fullPath = Assert-NoReparsePath -Path $Path -Name 'Release control file' -RequireExists
    $controlRoot = [IO.Path]::GetFullPath($script:controlRoot).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($controlRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release control path must be below ${controlRoot}: $Path"
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Release control file not found: $fullPath"
    }
    $fullPath
}

function Get-RepositoryHead {
    $gitArguments = @('-c', 'safe.directory=*', '-C', $workspace, 'rev-parse', 'HEAD')
    $output = @(& git.exe @gitArguments 2>&1 | ForEach-Object { $_.ToString().Trim() })
    if ($LASTEXITCODE -ne 0 -or $output.Count -ne 1 -or $output[0] -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Could not read the repository HEAD from $workspace."
    }
    $output[0].ToLowerInvariant()
}

function Get-FileNames {
    param([Parameter(Mandatory)] [string]$Path)

    $root = (Get-Item -LiteralPath $Path).FullName.TrimEnd('\')
    @(Get-ChildItem -LiteralPath $Path -Recurse -File -Force |
        ForEach-Object { $_.FullName.Substring($root.Length + 1).Replace('\', '/') })
}

function Get-ZipNames {
    param([Parameter(Mandatory)] [string]$Path)

    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        @($zip.Entries | ForEach-Object { $_.FullName })
    }
    finally {
        $zip.Dispose()
    }
}

function Assert-NoMacMetadata {
    param([Parameter(Mandatory)] [string[]]$Names)

    $bad = @($Names | Where-Object {
        $_ -match '(?i)(^|/)(\.DS_Store|__MACOSX|\.AppleDouble)(/|$)' -or
        $_ -match '(?i)(^|/)\._'
    })
    if ($bad.Count -gt 0) {
        throw "macOS metadata found: $($bad -join ', ')"
    }
}

function Assert-PortableNames {
    param([Parameter(Mandatory)] [string[]]$Names)

    $forbidden = @($Names | Where-Object {
        ($_ -notlike 'VoiceData/*') -and ($_ -notlike 'TranslationData/*') -and ($_ -match '(?i)\.(onnx|wav|flac|mp3|m4a|ogg|pcm|gguf|appinstaller)$' -or
        $_ -match '(?i)(^|/)tokens\.txt$' -or
        $_ -match '(?i)(history|transcript|polish|whisper|llama)')
    })
    if ($forbidden.Count -gt 0) {
        throw "Forbidden portable payload: $($forbidden -join ', ')"
    }

    foreach ($required in @(
        'DirectML.dll',
        'onnxruntime.dll',
        'sherpa-onnx-c-api.dll',
        'sherpa-onnx-cxx-api.dll',
        'runtime.sha256',
        'runtime.dependencies.json',
        'BUILD-INFO.json',
        'WinWispr.pri',
        'THIRD-PARTY-NOTICES.md')) {
        if (-not ($Names | Where-Object { $_.EndsWith($required, [StringComparison]::OrdinalIgnoreCase) })) {
            throw "Missing portable file: $required"
        }
    }

    $executables = @($Names | Where-Object { $_.EndsWith('WinWispr.exe', [StringComparison]::OrdinalIgnoreCase) })
    if ($executables.Count -ne 1) {
        throw "Expected exactly one WinWispr.exe, found $($executables.Count)."
    }
}

# Portable and Setup carry no models; they ship only in the WinWispr Models installer.
function Assert-ModelFreeNames {
    param([Parameter(Mandatory)] [string[]]$Names)

    $models = @($Names | Where-Object { $_ -match '(?i)^(ModelData|TranslationData|VoiceData)/' })
    if ($models.Count -gt 0) {
        throw "The app package contains model files: $($models -join ', ')"
    }
}

# The cached WinWispr Models installer for the version in eng\model-package.json, built and signed when it is
# missing or does not verify. Its inputs are large and change rarely, so deployments do not rebuild it.
function Get-ModelPackageInstaller {
    param([Parameter(Mandatory)] $Pin)

    $package = Read-ModelPackage -EngRoot (Join-Path $source 'eng')
    $path = Join-Path $modelArtifacts (Get-ModelPackageName -Package $package)
    $reusable = $false
    if ((Test-Path -LiteralPath $path -PathType Leaf) -and (Test-Path -LiteralPath "$path.sha256" -PathType Leaf)) {
        try {
            $recorded = ((Get-Content -Raw -LiteralPath "$path.sha256").Trim() -split '\s+')[0]
            Assert-CodeSignature -Path $path -Pin $Pin
            $reusable = $recorded -ceq (Get-ArtifactSha256 -LiteralPath $path)
        }
        catch {
            Write-Host "MODEL_PACKAGE_REBUILD=$($_.Exception.Message)"
        }
    }
    if ($reusable) {
        Write-Output 'MODEL_PACKAGE_BUILD=cache'
    }
    else {
        Push-Location $source
        try {
            Invoke-RequiredCommand -FilePath 'powershell.exe' -ArgumentList @(
                '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
                (Join-Path $source 'eng\Build-ModelPackage.ps1'), '-Sign', '-OutputDirectory', $modelArtifacts) `
                -LogPath (Get-ReleaseLogPath -Name 'model-package.log')
        }
        finally {
            Pop-Location
        }
        Write-Output 'MODEL_PACKAGE_BUILD=rebuilt'
    }
    Write-Output "MODEL_PACKAGE_VERSION=$([int]$package.version)"
    Write-Output "MODEL_PACKAGE=$path"
    Write-Output "MODEL_PACKAGE_SHA256=$(Get-ArtifactSha256 -LiteralPath $path)"
}

# The WinWispr Models installer must leave the pinned speech model archive, translation model and voices in
# the installation directory, where the app reads them.
function Assert-InstalledModels {
    param([Parameter(Mandatory)] [string]$InstallDirectory)

    $package = Read-ModelPackage -EngRoot (Join-Path $source 'eng')
    $archive = Join-Path $InstallDirectory 'ModelData\model.tar.bz2'
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf) -or
        (Get-ArtifactSha256 -LiteralPath $archive) -cne [string]$package.speechArchive.sha256) {
        throw 'The installed speech model archive is missing or changed.'
    }
    Assert-InstalledTranslationModel -InstallDirectory $InstallDirectory
    foreach ($manifest in Get-ChildItem -LiteralPath (Join-Path $source 'eng\voices') -Filter 'vits-piper-*.json' -File) {
        $installed = Join-Path $InstallDirectory "VoiceData\$($manifest.BaseName)\voice.json"
        if (-not (Test-Path -LiteralPath $installed -PathType Leaf) -or
            (Get-ArtifactSha256 -LiteralPath $installed) -cne (Get-ArtifactSha256 -LiteralPath $manifest.FullName)) {
            throw "The installed voice is missing or changed: $($manifest.BaseName)"
        }
    }
}

# The WinWispr Models installer must leave the ten pinned translation model files in TranslationData.
function Assert-InstalledTranslationModel {
    param([Parameter(Mandatory)] [string]$InstallDirectory)

    $pins = Get-Content -Raw -LiteralPath (Join-Path $source 'eng\translation-model-pins.json') | ConvertFrom-Json
    foreach ($direction in $pins.directions.PSObject.Properties) {
        foreach ($file in $direction.Value.files) {
            $path = Join-Path (Join-Path $InstallDirectory 'TranslationData') (($direction.Value.folder + '/' + $file.path).Replace('/', '\'))
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
                (Get-Item -LiteralPath $path).Length -ne [long]$file.bytes -or
                (Get-ArtifactSha256 -LiteralPath $path) -cne [string]$file.sha256) {
                throw "The installed translation model is missing or changed: $($direction.Value.folder)/$($file.path)"
            }
        }
    }
}

function Test-RuntimeDirectory {
    param([Parameter(Mandatory)] [string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    foreach ($required in @('DirectML.dll', 'onnxruntime.dll', 'sherpa-onnx-c-api.dll', 'sherpa-onnx-cxx-api.dll', 'runtime.sha256', 'runtime.dependencies.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $required))) { return $false }
    }

    try {
        $dependencies = Get-Content -Raw -LiteralPath (Join-Path $Path 'runtime.dependencies.json') | ConvertFrom-Json
    }
    catch {
        return $false
    }
    if ($dependencies.ttsEnabled -ne $true -or
        $dependencies.ttsBoundaryVersion -ne 2 -or
        $dependencies.espeakSourceSha256 -cne 'e4e262cbe34f7fe21f91f1ba3397f2728e1f30eafbae7853f2b753a9ed13f0dd' -or
        $dependencies.piperPhonemizeSourceSha256 -cne 'd9cca4e2bdc7d6dd8dffb96a4668283dbd3f77a9c194a3e530c1e8eba9406a5d' -or
        $dependencies.directMlAdapterSelectionVersion -ne 1 -or
        $dependencies.sherpaCommit -cne '3dc7c569f31ca2cd4a20ed6f7db780327e6714c5' -or
        $dependencies.onnxRuntimeDirectMlVersion -cne '1.24.4' -or
        $dependencies.onnxRuntimeDirectMlPackageSha256 -cne '57e9f11b73437bef7a309496135d4c1f96b1a8e9ddba60013fa27bfc1d788681' -or
        $dependencies.directMlVersion -cne '1.15.4' -or
        $dependencies.directMlPackageSha256 -cne '4e7cb7ddce8cf837a7a75dc029209b520ca0101470fcdf275c1f49736a3615b9') {
        return $false
    }

    foreach ($line in @(Get-Content -LiteralPath (Join-Path $Path 'runtime.sha256'))) {
        $parts = $line.Trim() -split '\s+', 2
        if ($parts.Count -ne 2 -or [IO.Path]::GetFileName($parts[0]) -ne $parts[0]) { return $false }
        $file = Join-Path $Path $parts[0]
        if (-not (Test-Path -LiteralPath $file)) { return $false }
        $actual = (Get-ArtifactSha256 -LiteralPath $file)
        if ($actual -ine $parts[1]) { return $false }
    }
    $true
}

function Assert-EmptyDirectory {
    param([Parameter(Mandatory)] [string]$Path)

    if (@(Get-ChildItem -LiteralPath $Path -Force).Count -ne 0) {
        throw "Directory is not empty: $Path"
    }
}

function Stop-RunningWinWispr {
    $deadline = (Get-Date).AddSeconds(10)
    while ($true) {
        $processes = @(Get-Process -Name WinWispr -ErrorAction SilentlyContinue)
        if ($processes.Count -eq 0) {
            Start-Sleep -Milliseconds 250
            if (@(Get-Process -Name WinWispr -ErrorAction SilentlyContinue).Count -eq 0) {
                return
            }
            continue
        }

        foreach ($process in $processes) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
        if ((Get-Date) -ge $deadline) {
            $remaining = @(Get-Process -Name WinWispr -ErrorAction SilentlyContinue)
            if ($remaining.Count -gt 0) {
                throw "WinWispr could not be stopped before deployment. Remaining PIDs: $($remaining.Id -join ', ')"
            }
            return
        }
        Start-Sleep -Milliseconds 250
    }
}

# A release peaks at about 5 GiB beyond its build: the packages, a staged copy
# of them for the evidence, and the evidence ZIP.
function Assert-FreeSpace {
    $requiredGiB = if ($Check) { 4 } else { 8 }
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($workspace))
    $freeGiB = $drive.AvailableFreeSpace / 1GB
    Write-Host ('DISK_FREE_GIB={0:N1} DISK_REQUIRED_GIB={1}' -f $freeGiB, $requiredGiB)
    if ($freeGiB -ge $requiredGiB) { return }
    $largest = @(foreach ($parent in @($artifacts, (Split-Path -Parent (Split-Path -Parent $workspace)))) {
        Get-ChildItem -LiteralPath $parent -Directory -Force -ErrorAction SilentlyContinue | ForEach-Object {
            $bytes = (Get-ChildItem -LiteralPath $_.FullName -Recurse -File -Force -ErrorAction SilentlyContinue |
                Measure-Object -Property Length -Sum).Sum
            [pscustomobject]@{ Path = $_.FullName; GiB = [double]$bytes / 1GB }
        }
    }) | Sort-Object -Property GiB -Descending | Select-Object -First 8
    $listing = ($largest | ForEach-Object { '{0:N1} GiB {1}' -f $_.GiB, $_.Path }) -join '; '
    throw ("Only {0:N1} GiB free on {1}; this run needs {2} GiB. Largest folders: {3}. " +
        'Free space on the VM, then run again; folders of other projects are the user''s decision.') -f
        $freeGiB, $drive.Name, $requiredGiB, $listing
}

function Assert-BuildPreconditions {
    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $sessions = (& query.exe user 2>&1 | Out-String)
    if ($sessionId -le 0 -or $sessions -notmatch "(?im)\b$sessionId\s+(Active|Aktiv)\b") {
        throw 'Builds and tests require an active interactive Windows session.'
    }
    $busy = @(Get-CimInstance Win32_Process | Where-Object {
        ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -match '(?i)\b(build|restore|publish|test)\b' -and $_.CommandLine -like "*$workspace*") -or
        ($_.Name -in @('WinWispr.Tests.exe', 'WinWispr.SettingsUiAcceptance.exe', 'ISCC.exe'))
    })
    if ($busy.Count -gt 0) {
        throw "Another build, test, or installer is running; processes left untouched: $($busy.ProcessId -join ', ')"
    }
    if (-not $Check) { Stop-RunningWinWispr }
    if (@(Get-Process -Name 'Microsoft.UI.Xaml.Markup.Compiler' -ErrorAction SilentlyContinue).Count -gt 0) {
        throw 'A previous Microsoft.UI.Xaml.Markup.Compiler process is still running; close the old build first.'
    }
}

# Desktop E2E tests inject keys and need the foreground. A hung window (its ghost
# keeps the foreground), an elevated window (the test cannot take the foreground
# from it), or another project's app under C:\windev (other agents' runs, which do
# not take the WinWispr VM lock) makes them time out. Runs that can include those
# tests refuse to start; routine checks only report the windows.
function Assert-UndisturbedDesktop {
    param([Parameter(Mandatory)] [bool]$Strict)

    if (-not ('WinWispr.Deploy.ProcessToken' -as [type])) {
        Add-Type -Namespace WinWispr.Deploy -Name ProcessToken -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int id);
[DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
[DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int infoClass, out int value, int length, out int returned);
[DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
// True when this medium-integrity worker cannot read the token (elevated or protected) or the token is elevated.
public static bool IsElevated(int processId)
{
    IntPtr process = OpenProcess(0x1000, false, processId);
    if (process == IntPtr.Zero) return true;
    try
    {
        IntPtr token;
        if (!OpenProcessToken(process, 0x0008, out token)) return true;
        try
        {
            int elevated, returned;
            return GetTokenInformation(token, 20, out elevated, 4, out returned) && elevated != 0;
        }
        finally { CloseHandle(token); }
    }
    finally { CloseHandle(process); }
}
'@
    }

    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $workspaceRoot = Split-Path -Parent $workspace
    $windows = @(Get-Process | Where-Object { $_.SessionId -eq $sessionId -and $_.MainWindowHandle -ne [IntPtr]::Zero })
    $blocking = @()
    $foreign = @()
    foreach ($process in $windows) {
        $path = try { $process.Path } catch { $null }
        $title = $process.MainWindowTitle
        if ($title.Length -gt 60) { $title = $title.Substring(0, 57) + '...' }
        $label = "'$title' of $($process.ProcessName) (pid $($process.Id))"
        # Windows suspends background store apps (Settings); they report not responding but hold no foreground.
        $suspended = @($process.Threads | Where-Object { $_.ThreadState -ne 'Wait' -or $_.WaitReason -ne 'Suspended' }).Count -eq 0
        $reason = if (-not $process.Responding -and -not $suspended) {
            'not responding'
        } elseif ([WinWispr.Deploy.ProcessToken]::IsElevated($process.Id)) {
            'elevated'
        } elseif ($path -and $path.StartsWith('C:\windev\', [StringComparison]::OrdinalIgnoreCase) -and
            -not $path.StartsWith("$workspaceRoot\", [StringComparison]::OrdinalIgnoreCase)) {
            'another project'
        }
        if ($reason) { $blocking += "$label, $reason" } elseif ($process.ProcessName -ne 'explorer') { $foreign += $label }
    }
    if ($foreign.Count -gt 0) { Write-Output "DESKTOP_WINDOWS=$($foreign -join '; ')" }
    if ($blocking.Count -eq 0) { return }
    $message = "Windows that disturb desktop E2E tests: $($blocking -join '; '). " +
        'Close them, or pause the session that starts them, and rerun.'
    if ($Strict) { throw $message }
    Write-Output "DESKTOP_WARNING=$message"
}

# --no-build reruns the tests against the last --check build. Start-SourceTreeBuild
# removes the marker when a build starts and Complete-SourceTreeBuild writes it
# when the build succeeded, so the marker names the tree of the last completed
# check build. Output from another tree (another checkout, or files changed
# since) is never reused: the caller rebuilds instead.
function Assert-ReusableCheckBuild {
    param(
        [Parameter(Mandatory)] [string]$MarkerPath,
        [Parameter(Mandatory)] [string]$RunnerPath
    )
    if (-not (Test-Path -LiteralPath $MarkerPath -PathType Leaf) -or -not (Test-Path -LiteralPath $RunnerPath -PathType Leaf)) {
        throw "No completed --check build to reuse in the mirror ($MarkerPath or $RunnerPath is missing); run --check without --no-build."
    }
    $builtTree = ([IO.File]::ReadAllText($MarkerPath)).Trim()
    if ($builtTree -cne $SourceTreeSha256) {
        throw "The last --check build in the mirror was built from source tree $builtTree, not from this checkout's tree $SourceTreeSha256 (another checkout's build, or files changed since); run --check without --no-build."
    }
    Write-Output 'BUILD_MODE=reused'
    Write-Output "REUSED_BUILD_TREE=$builtTree"
    Write-Output "REUSED_BUILD_UTC=$((Get-Item -LiteralPath $MarkerPath).LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ssZ'))"
    Write-Output "REUSED_RUNNER_UTC=$((Get-Item -LiteralPath $RunnerPath).LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ssZ'))"
}

# Runs Setup or the WinWispr Models installer silently for the current user into the installation directory,
# in the interactive session. It is done when DonePath exists and the task has finished with exit code 0.
function Install-Installer {
    param(
        [Parameter(Mandatory)] [string]$SetupPath,
        [string]$DonePath = $installExe
    )

    if (Get-ScheduledTask -TaskName $installerTaskName -ErrorAction SilentlyContinue) {
        throw "Scheduled task already exists: $installerTaskName"
    }

    $arguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /DIR="' + $install + '"'
    $action = New-ScheduledTaskAction -Execute $SetupPath -Argument $arguments
    $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $installerTaskName -Action $action -Principal $principal | Out-Null
    $script:installerTaskCreated = $true

    try {
        Start-ScheduledTask -TaskName $installerTaskName
        $deadline = (Get-Date).AddMinutes(5)
        while ($true) {
            $task = Get-ScheduledTask -TaskName $installerTaskName -ErrorAction SilentlyContinue
            $info = Get-ScheduledTaskInfo -TaskName $installerTaskName -ErrorAction SilentlyContinue
            if ((Test-Path -LiteralPath $DonePath) -and $null -ne $task -and $task.State -ne 'Running') {
                if ($info.LastTaskResult -ne 0) {
                    throw "Installer task failed. LastTaskResult=$($info.LastTaskResult)"
                }
                return
            }
            if ((Get-Date) -ge $deadline) {
                throw "Installer task did not finish successfully within 5 minutes. LastTaskResult=$($info.LastTaskResult)"
            }
            Start-Sleep -Seconds 1
        }
    }
    finally {
        if ($script:installerTaskCreated) {
            Unregister-ScheduledTask -TaskName $installerTaskName -Confirm:$false -ErrorAction SilentlyContinue
            $script:installerTaskCreated = $false
        }
    }
}

function Assert-InteractiveLaunch {
    $launchVerified = $false
    if (@(Get-Process -Name WinWispr -ErrorAction SilentlyContinue).Count -gt 0) {
        throw 'WinWispr started unexpectedly before the interactive verification.'
    }

    $interactiveSessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $sessionOutput = (& query.exe user 2>&1 | Out-String)
    if ($interactiveSessionId -le 0 -or $sessionOutput -notmatch "(?im)\b$interactiveSessionId\s+(Active|Aktiv)\b") {
        throw "Worker is not in an active interactive Windows session (Session $interactiveSessionId)."
    }
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        throw "Scheduled task already exists: $taskName"
    }

    $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument ('/d /c start "" "' + $installExe + '"')
    $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal | Out-Null
    $script:taskCreated = $true

    try {
        Start-ScheduledTask -TaskName $taskName
        Start-Sleep -Seconds 8
        $processes = @(Get-Process -Name WinWispr -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -ieq $installExe })
        $script:startedProcessIds = @($processes | ForEach-Object { $_.Id })
        if ($processes.Count -ne 1) {
            throw "Expected one WinWispr process from Session $interactiveSessionId, found $($processes.Count)."
        }
        if ($processes[0].SessionId -ne $interactiveSessionId) {
            throw "WinWispr started in Session $($processes[0].SessionId), expected Session $interactiveSessionId."
        }
        $launchVerified = $true
        # Compatibility flag: 1 means verified, not a Windows session ID.
        Write-Output 'INTERACTIVE_SESSION=1'
        Write-Output "INTERACTIVE_SESSION_ID=$interactiveSessionId"
    }
    finally {
        if ($script:taskCreated) {
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
            $script:taskCreated = $false
        }
        if (-not $launchVerified) {
            foreach ($processId in $script:startedProcessIds) {
                Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

try {
    Assert-NoReparsePath -Path $workspace -Name 'Repository mirror' -RequireExists | Out-Null
    foreach ($requiredPath in @(
        (Join-Path $workspace '.git'),
        (Join-Path $workspace 'WinWispr.sln'),
        (Join-Path $workspace 'eng\Deploy-WindowsVm.ps1'),
        (Join-Path $workspace 'eng\Build-Portable.ps1'))) {
        if (-not (Test-Path -LiteralPath $requiredPath)) {
            throw "Repository mirror is missing: $requiredPath"
        }
    }
    New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
    Assert-NoReparsePath -Path $artifacts -Name 'Artifacts directory' -RequireExists | Out-Null
    New-Item -ItemType Directory -Force -Path $controlRoot | Out-Null
    $lockPath = Assert-NoReparsePath -Path (Join-Path $controlRoot 'worker.lock') -Name 'Worker lock'
    $workerLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)

    $repositoryHead = Get-RepositoryHead
    if ($repositoryHead -cne $SourceCommit.ToLowerInvariant()) {
        $hint = if ($NoBuild) { '; --no-build does not sync, so run --check without --no-build' } else { '' }
        throw "Repository mirror HEAD mismatch: expected $($SourceCommit.ToLowerInvariant()), got $repositoryHead$hint"
    }
    if ($SourceArchiveSha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'Source archive SHA-256 is required for direct deployment provenance.'
    }
    if ([string]::IsNullOrEmpty($SourceTreeSha256)) {
        throw 'Source tree SHA-256 is required to decide whether the build can stay incremental.'
    }

    if ($DryRun -and -not $releaseMode) {
        throw '-DryRun requires -ReleaseRequestPath.'
    }
    if ($releaseMode) {
        if ([string]::IsNullOrWhiteSpace($ReleaseRequestSha256)) {
            throw 'Release mode requires a release request and its SHA-256.'
        }
        $releaseRequestPathFull = Assert-ControlFile -Path $ReleaseRequestPath
        Assert-Sha256 -Path $releaseRequestPathFull -ExpectedSha256 $ReleaseRequestSha256 -Name 'Release request' | Out-Null
        $releaseRequest = Read-ReleaseRequest -ArchivePath $releaseRequestPathFull
        if ($releaseRequest.SourceCommit -cne $SourceCommit) {
            throw 'Release request source commit does not match the repository mirror commit.'
        }
        $releaseVersion = $releaseRequest.Version
        if ($DryRun) {
            Write-Output "RELEASE_VERSION=$releaseVersion"
            return
        }
        $releaseLogDirectory = Join-Path $artifacts "release-logs\v$releaseVersion"
        if (Test-Path -LiteralPath $releaseLogDirectory) {
            Remove-SafeTree -Path $releaseLogDirectory -Name 'Release log directory'
        }
        New-Item -ItemType Directory -Force -Path $releaseLogDirectory | Out-Null
        Assert-NoReparsePath -Path $releaseLogDirectory -Name 'Release log directory' -RequireExists | Out-Null
        $releaseAcceptanceLog = Join-Path $releaseLogDirectory 'vm-acceptance.log'
    }
    elseif (-not $Check -and -not $ModelPackage) {
        $releaseVersion = Get-DevelopmentVersion -Workspace $workspace -Head $SourceCommit
        Write-Output "DEPLOYMENT_VERSION=$releaseVersion"
    }

    if ($Check) {
        Assert-NoReparsePath -Path $checkArtifacts -Name 'Development output' | Out-Null
        $releaseLogDirectory = Join-Path $checkArtifacts ('logs\' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff') + '-' + $PID)
        New-Item -ItemType Directory -Force -Path $releaseLogDirectory | Out-Null
        @('MODE=development-check', "SOURCE_COMMIT=$SourceCommit", "WORKING_TREE=$WorkingTreeState",
            'SOURCE_ARCHIVE_SCOPE=HEAD-only; development outputs include working-tree changes',
            "TEST_SCOPE=$(if ($TestFilter) { 'filtered' } else { 'deterministic' })",
            "BUILD=$(if ($NoBuild) { 'reused' } else { 'built' })",
            "TEST_FILTER=$(if ($TestFilter) { $TestFilter } else { $defaultTestFilter })") |
            Set-Content -LiteralPath (Join-Path $releaseLogDirectory 'check-info.txt') -Encoding utf8
        Get-Content -LiteralPath (Join-Path $releaseLogDirectory 'check-info.txt') | ForEach-Object { Write-Host $_ }
    }

    # Pruning frees what it can first; the space check then fails before the
    # build instead of at the end of a long release.
    & (Join-Path $PSScriptRoot 'Prune-VmArtifacts.ps1') -ArtifactsPath $artifacts | ForEach-Object { Write-Host $_ }
    Assert-FreeSpace
    if ($ModelPackage) {
        Get-ModelPackageInstaller -Pin (Read-CodeSigningPin)
        Write-Output 'VERIFY=PASS'
        Write-Phase 'total' 'PASS' $totalTimer.Elapsed.TotalSeconds
        return
    }
    # Deployments and releases sign; the certificate must be present and trusted before the long build.
    $signingPin = $null
    if (-not $Check) {
        $signingPin = Read-CodeSigningPin
        Get-CodeSigningCertificate -Pin $signingPin | Out-Null
        Write-Output "SIGNING_CERTIFICATE=$($signingPin.thumbprint)"
    }
    Assert-BuildPreconditions
    # --no-build reuses the last check build, so it needs neither the runtime nor a build.
    if (-not $NoBuild) {
        $runtimeTimer = [Diagnostics.Stopwatch]::StartNew()
        Write-Phase 'runtime' 'START'

        $runtimeScript = Join-Path $source 'eng\Build-SherpaRuntime.ps1'
        $runtimeFingerprint = (Get-ArtifactSha256 -LiteralPath $runtimeScript)
        $cacheValid = (Test-Path -LiteralPath $runtimeMarker) -and
            ((Get-Content -Raw -LiteralPath $runtimeMarker).Trim() -ieq $runtimeFingerprint) -and
            (Test-RuntimeDirectory -Path $runtimeCache)
        $sourceRuntime = Join-Path $source 'src\WinWispr\Runtime\win-x64'

        if ($RebuildRuntime -or -not $cacheValid) {
            Push-Location $source
            try {
                Invoke-RequiredCommand -FilePath 'powershell.exe' -ArgumentList @(
                    '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $runtimeScript) `
                    -LogPath (Get-ReleaseLogPath -Name 'runtime.log')
            }
            finally {
                Pop-Location
            }
            if (-not (Test-RuntimeDirectory -Path $sourceRuntime)) {
                throw 'The rebuilt sherpa runtime failed validation.'
            }
            if (Test-Path -LiteralPath $runtimeCache) {
                Remove-SafeTree -Path $runtimeCache -Name 'runtime cache'
            }
            New-Item -ItemType Directory -Force -Path $runtimeCache | Out-Null
            Copy-Item -Path (Join-Path $sourceRuntime '*') -Destination $runtimeCache -Recurse -Force
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $runtimeMarker) | Out-Null
            [IO.File]::WriteAllText($runtimeMarker, $runtimeFingerprint, [Text.Encoding]::ASCII)
            $runtimeBuild = 'rebuilt'
            Write-Output 'RUNTIME=rebuilt'
        }
        else {
            New-Item -ItemType Directory -Force -Path $sourceRuntime | Out-Null
            foreach ($file in Get-ChildItem -LiteralPath $runtimeCache -File) {
                $destination = Join-Path $sourceRuntime $file.Name
                if (-not (Test-Path -LiteralPath $destination) -or
                    (Get-ArtifactSha256 -LiteralPath $destination) -cne (Get-ArtifactSha256 -LiteralPath $file.FullName)) {
                    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
                }
            }
            if (-not (Test-RuntimeDirectory -Path $sourceRuntime)) {
                throw 'The cached sherpa runtime failed validation after restore.'
            }
            Write-Output 'RUNTIME=cache'
        }
        Write-Phase 'runtime' 'PASS' $runtimeTimer.Elapsed.TotalSeconds
    }

    Push-Location $source
    try {
        $solution = Join-Path $source 'WinWispr.sln'
        $buildProperties = @('-p:Platform=x64', '-p:SelfContained=true',
            '-p:WindowsPackageType=None', '-p:WindowsAppSDKSelfContained=true',
            '-p:ResolveAssemblyReferencesFindRelatedSatellites=false', '-p:PublishSingleFile=false')
        # Checks build into dev-check; deployments and releases into the bin and obj
        # directories of the source tree. Each output records the tree it was built from.
        $sourceTreeMarker = if ($Check) {
            Join-Path $checkArtifacts 'source-tree.sha256'
        } else {
            Join-Path $artifacts 'build-source-tree.sha256'
        }
        $runnerPath = Join-Path $source 'tests\WinWispr.Tests\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\WinWispr.Tests.exe'
        if ($Check) {
            $runnerPath = Join-Path $checkArtifacts 'bin\WinWispr.Tests\release_win-x64\WinWispr.Tests.exe'
        }
        if ($NoBuild) {
            Write-Phase 'reuse-build' 'START'
            Assert-ReusableCheckBuild -MarkerPath $sourceTreeMarker -RunnerPath $runnerPath
            Write-Phase 'reuse-build' 'PASS'
        } else {
            $buildMode = Start-SourceTreeBuild -MarkerPath $sourceTreeMarker -SourceTreeSha256 $SourceTreeSha256
            Write-Output "BUILD_MODE=$buildMode"
            if ($Check) {
                $buildProperties += @('--artifacts-path', $checkArtifacts)
            } else {
                $buildProperties += @("-p:Version=$releaseVersion", "-p:AssemblyVersion=$releaseVersion",
                    "-p:FileVersion=$releaseVersion", "-p:InformationalVersion=$releaseVersion")
                Invoke-RequiredCommand -FilePath 'dotnet.exe' -ArgumentList @('build-server', 'shutdown') `
                    -LogPath (Get-ReleaseLogPath -Name 'build-server-shutdown.log')
            }
            Invoke-RequiredCommand -FilePath 'dotnet.exe' -ArgumentList (@('restore', $solution, '--locked-mode') + $buildProperties) `
                -LogPath (Get-ReleaseLogPath -Name 'restore.log')
            $buildArguments = @('build', $solution, '-c', 'Release', '--no-restore', '--nologo', '-v:minimal')
            if ($buildMode -eq 'rebuild') { $buildArguments += '--no-incremental' }
            Invoke-RequiredCommand -FilePath 'dotnet.exe' -ArgumentList ($buildArguments + $buildProperties) `
                -LogPath (Get-ReleaseLogPath -Name 'build.log')
            Complete-SourceTreeBuild -MarkerPath $sourceTreeMarker -SourceTreeSha256 $SourceTreeSha256
        }
        if (-not (Test-Path -LiteralPath $runnerPath -PathType Leaf)) {
            throw "Direct test runner was not produced: $runnerPath"
        }
        # The default --check filter excludes every desktop E2E category.
        Write-Phase 'desktop' 'START'
        Assert-UndisturbedDesktop -Strict ($releaseMode -or [bool]$TestFilter)
        Write-Phase 'desktop' 'PASS'
        Invoke-RequiredCommand -FilePath $runnerPath -ArgumentList @(
            '--filter', $(if ($TestFilter) { $TestFilter } else { $defaultTestFilter })) `
            -LogPath (Get-ReleaseLogPath -Name 'tests.log')

        if ($Check) {
            Write-Phase 'total' 'PASS' $totalTimer.Elapsed.TotalSeconds
            Write-Output "CHECK_LOG_DIRECTORY=$releaseLogDirectory"
            Write-Output 'CHECK=PASS'
            return
        }

        $portableArguments = @(
            '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
            (Join-Path $source 'eng\Build-Portable.ps1'),
            '-Version', $releaseVersion,
            '-NoBuild',
            '-Sign',
            '-SourceCommit', $(if ($releaseMode) { $releaseRequest.SourceCommit } else { $SourceCommit }),
            '-SourceArchiveSha256', $SourceArchiveSha256,
            '-RuntimeBuild', $runtimeBuild)
        Invoke-RequiredCommand -FilePath 'powershell.exe' -ArgumentList $portableArguments `
            -LogPath (Get-ReleaseLogPath -Name 'portable.log')
    }
    finally {
        if (-not $Check) { & dotnet.exe build-server shutdown *> $null }
        Pop-Location
    }

    $portableName = 'WinWispr-portable-v' + $releaseVersion + '.zip'
    $portableZips = @(Get-ChildItem -LiteralPath $portableArtifacts -Filter $portableName -File)
    if ($portableZips.Count -ne 1) {
        throw "Expected exactly one direct portable ZIP, found $($portableZips.Count)."
    }
    $portableNames = Get-ZipNames -Path $portableZips[0].FullName
    Assert-PortableNames -Names $portableNames
    Assert-ModelFreeNames -Names $portableNames

    $installerPath = $null
    $sbomPath = $null
    Push-Location $source
    try {
        $installerArguments = @(
            '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
            (Join-Path $source 'eng\Build-Installer.ps1'),
            '-Version', $releaseVersion,
            '-PortableArchivePath', $portableZips[0].FullName,
            '-Sign')
        Invoke-RequiredCommand -FilePath 'powershell.exe' -ArgumentList $installerArguments `
            -LogPath (Get-ReleaseLogPath -Name 'installer.log')

        if ($releaseMode) {
            $sbomPath = Join-Path $source "artifacts\sbom\SBOM-v$releaseVersion.cdx.json"
            Invoke-RequiredCommand -FilePath 'powershell.exe' -ArgumentList @(
                '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
                (Join-Path $source 'eng\New-Sbom.ps1'),
                '-OutputPath', $sbomPath) `
                -LogPath (Get-ReleaseLogPath -Name 'sbom.log')
        }
    }
    finally {
        Pop-Location
    }
    $sourceInstaller = Join-Path $source 'artifacts\installer'
    $installerName = 'WinWispr-Setup-v' + $releaseVersion + '.exe'
    $setupFiles = @(Get-ChildItem -LiteralPath $sourceInstaller -Filter $installerName -File)
    if ($setupFiles.Count -ne 1) {
        throw "Expected exactly one source installer, found $($setupFiles.Count)."
    }
    $installerPath = $setupFiles[0].FullName
    if (-not (Test-Path -LiteralPath "$installerPath.sha256" -PathType Leaf)) {
        throw "Installer sidecar was not produced: $installerPath.sha256"
    }
    if ($releaseMode -and -not (Test-Path -LiteralPath $sbomPath -PathType Leaf)) {
        throw "SBOM was not produced: $sbomPath"
    }

    # The app needs the models, which only the WinWispr Models installer carries.
    $modelPackageLines = @(Get-ModelPackageInstaller -Pin $signingPin)
    $modelPackageLines | ForEach-Object { Write-Output $_ }
    $modelPackagePath = ($modelPackageLines | Where-Object { $_ -like 'MODEL_PACKAGE=*' } | Select-Object -First 1).Substring('MODEL_PACKAGE='.Length)
    $modelPackageSha256 = ($modelPackageLines | Where-Object { $_ -like 'MODEL_PACKAGE_SHA256=*' } | Select-Object -First 1).Substring('MODEL_PACKAGE_SHA256='.Length)

    New-Item -ItemType Directory -Force -Path $portableArtifacts, $installerArtifacts | Out-Null
    $currentZip = Join-Path $portableArtifacts 'WinWispr-portable-current.zip'
    $checksum = Join-Path $portableArtifacts 'WinWispr-portable-current.zip.sha256'
    $currentSetup = Join-Path $installerArtifacts 'WinWispr-Setup.exe'
    $setupChecksum = Join-Path $installerArtifacts 'WinWispr-Setup.exe.sha256'
    Copy-Item -LiteralPath $portableZips[0].FullName -Destination $currentZip -Force
    $portableHash = (Get-ArtifactSha256 -LiteralPath $currentZip)
    [IO.File]::WriteAllText($checksum, "$portableHash  WinWispr-portable-current.zip`r`n", [Text.Encoding]::ASCII)
    Copy-Item -LiteralPath $installerPath -Destination $currentSetup -Force
    $setupHash = (Get-ArtifactSha256 -LiteralPath $currentSetup)
    [IO.File]::WriteAllText($setupChecksum, "$setupHash  WinWispr-Setup.exe`r`n", [Text.Encoding]::ASCII)

    $settingsFile = Join-Path $appData 'settings.json'
    $installTimer = [Diagnostics.Stopwatch]::StartNew()
    Write-Phase 'install-and-launch' 'START'
    # The dictionary lives in settings.json.dictionary. Before that file exists, the app migrates
    # the legacy PersonalDictionary field of settings.json into it on first launch.
    $dictionaryPath = "$settingsFile.dictionary"
    # A settings file written after the split has no such field; strict mode forbids reading a missing one.
    $legacyDictionary = ''
    if (-not (Test-Path -LiteralPath $dictionaryPath) -and (Test-Path -LiteralPath $settingsFile)) {
        $legacyField = (Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json -ErrorAction Stop).PSObject.Properties['PersonalDictionary']
        if ($null -ne $legacyField) {
            $legacyDictionary = [string]$legacyField.Value
        }
    }
    $dictionaryFilesBefore = @{}
    foreach ($dictionaryFile in @("$settingsFile.dictionary", "$settingsFile.dictionary.bak")) {
        if (Test-Path -LiteralPath $dictionaryFile) {
            $dictionaryFilesBefore[$dictionaryFile] = Get-ArtifactSha256 -LiteralPath $dictionaryFile
        }
    }
    Stop-RunningWinWispr
    if (Test-Path -LiteralPath $install) {
        Assert-NoReparseTree -Path $install -Name 'Install directory' | Out-Null
    }
    New-Item -ItemType Directory -Force -Path $install | Out-Null
    Clear-SafeDirectory -Path $install -Name 'installation directory'
    Install-Installer -SetupPath $currentSetup
    Assert-ModelFreeNames -Names (Get-FileNames -Path $install)
    Install-Installer -SetupPath $modelPackagePath -DonePath (Join-Path $install 'ModelsUninstall\unins000.exe')
    Assert-PortableNames -Names (Get-FileNames -Path $install)
    Assert-InstalledModels -InstallDirectory $install
    Write-Output 'TRANSLATION_MODEL=installed'
    Write-Output 'MODELS=installed'
    foreach ($signed in @($currentSetup, $modelPackagePath, $installExe, (Join-Path $install 'WinWispr.dll'),
            (Join-Path $install 'unins000.exe'), (Join-Path $install 'ModelsUninstall\unins000.exe'))) {
        Assert-CodeSignature -Path $signed -Pin $signingPin
    }
    Write-Output "SIGNATURES=valid $($signingPin.thumbprint)"
    Assert-InteractiveLaunch

    if ($appDataExisted -and -not (Test-Path -LiteralPath $appData)) {
        throw 'Deployment removed the existing WinWispr app-data directory.'
    }
    if ($legacyDictionary.Length -gt 0) {
        $migrationDeadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
        while (-not (Test-Path -LiteralPath $dictionaryPath) -and [DateTimeOffset]::UtcNow -lt $migrationDeadline) {
            Start-Sleep -Milliseconds 250
        }
        $migratedDictionary = if (Test-Path -LiteralPath $dictionaryPath) {
            [IO.File]::ReadAllText($dictionaryPath)
        } else { $null }
        if ($migratedDictionary -cne $legacyDictionary) {
            throw 'The app did not migrate the legacy personal dictionary into settings.json.dictionary.'
        }
        Write-Output 'PERSONAL_DICTIONARY=migrated'
    }
    foreach ($dictionaryFile in $dictionaryFilesBefore.Keys) {
        if (-not (Test-Path -LiteralPath $dictionaryFile) -or
            (Get-ArtifactSha256 -LiteralPath $dictionaryFile) -cne $dictionaryFilesBefore[$dictionaryFile]) {
            throw 'Deployment changed or removed a personal dictionary file or its backup.'
        }
    }
    if ($dictionaryFilesBefore.Count -gt 0) {
        Write-Output 'PERSONAL_DICTIONARY_FILES=preserved'
    }
    $appDataStatus = if ($appDataExisted) { 'preserved' } else { 'not-present' }
    Write-Phase 'install-and-launch' 'PASS' $installTimer.Elapsed.TotalSeconds

    if ($releaseMode) {
        @(
            "INTERACTIVE_SESSION=1",
            "APP_RUNNING=1",
            "INSTALL_PATH=$installExe",
            "APP_DATA=$appDataStatus",
            'VERIFY=PASS'
        ) | Set-Content -LiteralPath $releaseAcceptanceLog -Encoding ascii

        $commitMessagesPath = Join-Path $releaseLogDirectory 'commit-messages.txt'
        [IO.File]::WriteAllText(
            $commitMessagesPath,
            ($releaseRequest.CommitMessages -join [string][char]0x1e),
            [Text.UTF8Encoding]::new($false))
        $evidenceOutput = Join-Path $controlRoot 'release'
        if (Test-Path -LiteralPath $evidenceOutput) {
            Remove-SafeTree -Path $evidenceOutput -Name 'release control output'
        }
        $evidenceTimer = [Diagnostics.Stopwatch]::StartNew()
        Write-Phase 'evidence' 'START'
        $evidenceOutputLines = @(& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `
            (Join-Path $source 'eng\New-ReleaseEvidence.ps1') `
            -Version $releaseVersion `
            -PreviousTag $releaseRequest.PreviousTag `
            -SourceCommit $releaseRequest.SourceCommit `
            -SourceArchiveSha256 $SourceArchiveSha256 `
            -CommitMessagesPath $commitMessagesPath `
            -PortableArchivePath $portableZips[0].FullName `
            -InstallerPath $installerPath `
            -ModelPackagePath $modelPackagePath `
            -SigningThumbprint $signingPin.thumbprint `
            -SbomPath $sbomPath `
            -AcceptanceLogPath $releaseAcceptanceLog `
            -LogDirectory $releaseLogDirectory `
            -OutputRoot $evidenceOutput 2>&1 | ForEach-Object { $_.ToString() })
        $evidenceExitCode = $LASTEXITCODE
        Write-Phase 'evidence' $(if ($evidenceExitCode -eq 0) { 'PASS' } else { 'FAIL' }) $evidenceTimer.Elapsed.TotalSeconds
        $evidenceOutputLines | Set-Content -LiteralPath (Join-Path $releaseLogDirectory 'evidence.log') -Encoding utf8
        $evidenceOutputLines |
            Where-Object { $_ -notlike 'RELEASE_ARCHIVE=*' -and $_ -notlike 'RELEASE_SHA256=*' } |
            ForEach-Object { Write-Host $_ }
        if ($evidenceExitCode -ne 0) {
            throw "Release evidence build failed with exit code $evidenceExitCode."
        }
        $releaseArchiveLine = @($evidenceOutputLines | Where-Object { $_ -like 'RELEASE_ARCHIVE=*' })
        $releaseShaLine = @($evidenceOutputLines | Where-Object { $_ -like 'RELEASE_SHA256=*' })
        if ($releaseArchiveLine.Count -ne 1 -or $releaseShaLine.Count -ne 1) {
            throw 'Release evidence build must report RELEASE_ARCHIVE and RELEASE_SHA256 exactly once.'
        }
        Write-Output "RELEASE_VERSION=$releaseVersion"
        Write-Output $releaseArchiveLine[0]
        Write-Output $releaseShaLine[0]
    }

    $profileTempDirs = @(
        Get-ChildItem -LiteralPath $env:USERPROFILE -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -cmatch '^WinWispr-(publish|installer)-' }
        Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -cmatch '^WinWispr-(publish|installer)-' })
    if ($profileTempDirs.Count -gt 0) {
        throw "Unexpected deployment directories remain: $($profileTempDirs.FullName -join ', ')"
    }

    Write-Output "ARTIFACT_SHA256=$portableHash"
    Write-Output "INSTALLER_SHA256=$setupHash"
    Write-Output "INSTALLER_PATH=$currentSetup"
    Write-Output "INSTALL_PATH=$installExe"
    Write-Output "RUNTIME_BUILD=$runtimeBuild"
    Write-Output "APP_DATA=$appDataStatus"
    Write-Output 'APP_RUNNING=1'
    Write-Output 'VERIFY=PASS'
    Write-Phase 'total' 'PASS' $totalTimer.Elapsed.TotalSeconds
}
catch {
    # One line for the caller's FAILED summary: the phase and the first line of the error.
    $failure = $_
    $failureLine = @($failure.Exception.Message -split '\r?\n' | Where-Object { $_.Trim() } | Select-Object -First 1)
    Write-Output "WORKER_FAILURE=${currentPhase}: $(if ($failureLine.Count -gt 0) { $failureLine[0].Trim() } else { $failure.Exception.GetType().Name })"
    Write-Phase 'total' 'FAIL' $totalTimer.Elapsed.TotalSeconds
    throw
}
finally {
    if ($null -ne $workerLock) { $workerLock.Dispose() }
    if ($releaseMode -and -not [string]::IsNullOrWhiteSpace($releaseRequestPathFull)) {
        Remove-Item -LiteralPath $releaseRequestPathFull -Force -ErrorAction SilentlyContinue
    }
}
