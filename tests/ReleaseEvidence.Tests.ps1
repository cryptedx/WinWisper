$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$evidenceScript = Join-Path $repositoryRoot 'eng\New-ReleaseEvidence.ps1'
$localAppData = if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData) } else { $env:LOCALAPPDATA }
$expectedInstallPath = [IO.Path]::GetFullPath((Join-Path $localAppData 'Programs\WinWispr\WinWispr.exe'))

function Assert-Equal {
    param($Expected, $Actual, [string]$Name)
    if ($Expected -is [int]) {
        if ([int]$Actual -ne $Expected) { throw "$Name`: expected '$Expected', got '$Actual'." }
        return
    }
    if ($Expected -cne $Actual) { throw "$Name`: expected '$Expected', got '$Actual'." }
}

function Assert-True {
    param($Value, [string]$Name)
    if (-not $Value) { throw "$Name`: expected true." }
}

function Assert-False {
    param($Value, [string]$Name)
    if ($Value) { throw "$Name`: expected false." }
}

function Assert-Throws {
    param([scriptblock]$Script, [string]$Name)
    try {
        & $Script
    }
    catch {
        return
    }
    throw "$Name`: expected an exception."
}

function Assert-ThrowsLike {
    param([scriptblock]$Script, [string]$Pattern, [string]$Name)
    try {
        & $Script
    }
    catch {
        if ($_.Exception.Message -notlike $Pattern) {
            throw "$Name`: expected exception like '$Pattern', got '$($_.Exception.Message)'."
        }
        return
    }
    throw "$Name`: expected an exception."
}

function Write-TextFile {
    param([string]$Path, [string]$Content)
    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function New-DirectoryJunction {
    param([string]$Path, [string]$Target)
    New-Item -ItemType Directory -Force -Path $Target | Out-Null
    $output = & cmd.exe /c mklink /J "$Path" "$Target" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "mklink /J failed: $output"
    }
}

function New-TinyZip {
    param([string]$Path, [string]$EntryName, [string]$Content)
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Force
    }
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry($EntryName)
        $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
        try {
            $writer.Write($Content)
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Write-Sha256Sidecar {
    param([string]$Path)
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$Path.sha256" -Value "$hash *$([IO.Path]::GetFileName($Path))" -Encoding ascii
}

function Get-ZipNames {
    param([string]$Path)
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        @($zip.Entries | ForEach-Object { $_.FullName })
    }
    finally {
        $zip.Dispose()
    }
}

function New-Fixture {
    param([string]$Root)
    $inputRoot = Join-Path $Root 'inputs'
    $logs = Join-Path $Root 'logs'
    New-Item -ItemType Directory -Force -Path $inputRoot, $logs | Out-Null

    $commitsPath = Join-Path $inputRoot 'commits.txt'
    $portableZip = Join-Path $inputRoot 'WinWispr-portable-v1.3.0.zip'
    $installer = Join-Path $inputRoot 'WinWispr-Setup-v1.3.0.exe'
    $modelPackage = Join-Path $inputRoot 'WinWispr-Models-v1.exe'
    $sbom = Join-Path $inputRoot 'sbom-fixture.json'
    $acceptance = Join-Path $inputRoot 'acceptance.txt'

    Write-TextFile -Path $commitsPath -Content ("feat: add release evidence" + [string][char]0x1e + "fix: preserve app data")
    New-TinyZip -Path $portableZip -EntryName 'WinWispr.exe' -Content 'portable'
    Write-TextFile -Path $installer -Content ('installer' * 8192)
    Write-TextFile -Path $modelPackage -Content ('model package' * 8192)
    Write-TextFile -Path $sbom -Content '{"bomFormat":"CycloneDX"}'
    Write-TextFile -Path $acceptance -Content "INTERACTIVE_SESSION=1`nAPP_RUNNING=1`nINSTALL_PATH=$expectedInstallPath`nAPP_DATA=preserved`nVERIFY=PASS`n"
    Write-TextFile -Path (Join-Path $logs 'build.log') -Content 'build ok'
    Write-TextFile -Path (Join-Path $logs 'tests.log') -Content 'tests ok'

    Write-Sha256Sidecar -Path $portableZip
    Write-Sha256Sidecar -Path $installer
    Write-Sha256Sidecar -Path $modelPackage

    [pscustomobject]@{
        CommitsPath = $commitsPath
        SourceArchiveSha256 = ('b' * 64)
        PortableZip = $portableZip
        Installer = $installer
        ModelPackage = $modelPackage
        SigningThumbprint = ('F' * 40)
        Sbom = $sbom
        Acceptance = $acceptance
        Logs = $logs
        OutputRoot = (Join-Path $Root 'out')
    }
}

function Invoke-Evidence {
    param($Fixture)
    & $evidenceScript -Version '1.3.0' -PreviousTag 'v1.2.3' `
        -SourceCommit ('a' * 40) -SourceArchiveSha256 $Fixture.SourceArchiveSha256 `
        -CommitMessagesPath $Fixture.CommitsPath -PortableArchivePath $Fixture.PortableZip `
        -InstallerPath $Fixture.Installer -ModelPackagePath $Fixture.ModelPackage `
        -SigningThumbprint $Fixture.SigningThumbprint -SbomPath $Fixture.Sbom `
        -AcceptanceLogPath $Fixture.Acceptance -LogDirectory $Fixture.Logs `
        -OutputRoot $Fixture.OutputRoot
}

function Test-Sha256Sums {
    param([string]$Directory)
    $manifest = Join-Path $Directory 'SHA256SUMS.txt'
    foreach ($line in Get-Content -LiteralPath $manifest) {
        $match = [regex]::Match($line, '^([0-9a-f]{64}) \*(.+)$')
        if (-not $match.Success) {
            throw "Invalid SHA256SUMS line: $line"
        }
        $path = Join-Path $Directory ($match.Groups[2].Value.Replace('/', [IO.Path]::DirectorySeparatorChar))
        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
        Assert-Equal $match.Groups[1].Value $actual "hash $($match.Groups[2].Value)"
    }
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('ReleaseEvidence.Tests.' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null

try {
    $fixture = New-Fixture -Root (Join-Path $tempRoot 'happy')
    Write-TextFile -Path $fixture.Installer -Content ('installer' * 262144)
    Write-Sha256Sidecar -Path $fixture.Installer
    Invoke-Evidence -Fixture $fixture | Out-Null

    $versionDir = Join-Path $fixture.OutputRoot 'v1.3.0'
    $releaseZip = Join-Path $fixture.OutputRoot 'WinWispr-release-v1.3.0.zip'
    $releaseSidecar = "$releaseZip.sha256"
    Assert-True (Test-Path -LiteralPath $versionDir -PathType Container) 'version evidence directory'
    Assert-True (Test-Path -LiteralPath $releaseZip -PathType Leaf) 'release ZIP'
    Assert-True (Test-Path -LiteralPath $releaseSidecar -PathType Leaf) 'release ZIP sidecar'

    $names = @(Get-ZipNames -Path $releaseZip | Sort-Object)
    $expectedNames = @(
        'BUILD-INFO.json',
        'CHANGELOG-v1.3.0.md',
        'SBOM.cdx.json',
        'SECURITY-REPORT.md',
        'SHA256SUMS.txt',
        'THIRD-PARTY-NOTICES.md',
        'VM-ACCEPTANCE.txt',
        'WinWispr-Setup-v1.3.0.exe',
        'WinWispr-Setup-v1.3.0.exe.sha256',
        'WinWispr-portable-v1.3.0.zip',
        'WinWispr-portable-v1.3.0.zip.sha256',
        'logs/build.log',
        'logs/tests.log'
    ) | Sort-Object
    Assert-Equal ($expectedNames -join '|') ($names -join '|') 'release ZIP members'
    $zip = [IO.Compression.ZipFile]::OpenRead($releaseZip)
    try {
        foreach ($entry in $zip.Entries | Where-Object { $_.FullName -match '\.(zip|exe)$' }) {
            # .NET Framework stores NoCompression as uncompressed Deflate blocks.
            Assert-True ($entry.CompressedLength -ge $entry.Length -and
                $entry.CompressedLength -le $entry.Length * 1.001 + 64) "uncompressed payload $($entry.FullName)"
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $actual = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
            finally { $stream.Dispose(); $sha.Dispose() }
            $entryName = [IO.Path]::GetFileName($entry.FullName)
            $original = if ($entryName.EndsWith('.zip', [StringComparison]::Ordinal)) {
                $fixture.PortableZip
            } else {
                $fixture.Installer
            }
            Assert-Equal ((Get-FileHash $original -Algorithm SHA256).Hash.ToLowerInvariant()) $actual 'stored payload bytes'
        }
    }
    finally { $zip.Dispose() }

    $buildInfo = Get-Content -LiteralPath (Join-Path $versionDir 'BUILD-INFO.json') -Raw | ConvertFrom-Json
    Assert-Equal '1.3.0' ([string]$buildInfo.version) 'build info version'
    Assert-Equal 'v1.2.3' ([string]$buildInfo.previousTag) 'build info previous tag'
    Assert-Equal ('a' * 40) ([string]$buildInfo.sourceCommit) 'build info source commit'
    Assert-Equal ('b' * 64) ([string]$buildInfo.sourceArchiveSha256) 'build info source archive hash'
    Assert-Equal ('F' * 40) ([string]$buildInfo.signingThumbprint) 'build info signing thumbprint'
    Assert-Equal 'WinWispr-Models-v1.exe' ([string]$buildInfo.modelPackage) 'build info model package'
    Assert-Equal ((Get-FileHash $fixture.ModelPackage -Algorithm SHA256).Hash.ToLowerInvariant()) ([string]$buildInfo.modelPackageSha256) 'build info model package hash'
    Assert-False ([bool]$buildInfo.enterpriseReady) 'enterpriseReady'
    Assert-True ([bool]$buildInfo.vmVerified) 'vmVerified'
    Assert-Equal 2 $buildInfo.commitsSincePreviousTag.Count 'build info commit count'
    Test-Sha256Sums -Directory $versionDir
    $canonicalNotices = Get-Content -LiteralPath (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md') -Raw
    $packedNotices = Get-Content -LiteralPath (Join-Path $versionDir 'THIRD-PARTY-NOTICES.md') -Raw
    Assert-Equal $canonicalNotices $packedNotices 'canonical third-party notices'

    Assert-Throws {
        $bad = New-Fixture -Root (Join-Path $tempRoot 'bad-version')
        & $evidenceScript -Version 'v1.3.0' -PreviousTag 'v1.2.3' `
            -SourceCommit ('a' * 40) -SourceArchiveSha256 $bad.SourceArchiveSha256 `
            -CommitMessagesPath $bad.CommitsPath -PortableArchivePath $bad.PortableZip `
            -InstallerPath $bad.Installer -ModelPackagePath $bad.ModelPackage `
            -SigningThumbprint $bad.SigningThumbprint -SbomPath $bad.Sbom `
            -AcceptanceLogPath $bad.Acceptance -LogDirectory $bad.Logs `
            -OutputRoot $bad.OutputRoot
    } 'strict version validation'

    Assert-Throws {
        $bad = New-Fixture -Root (Join-Path $tempRoot 'missing-acceptance')
        Remove-Item -LiteralPath $bad.Acceptance -Force
        Invoke-Evidence -Fixture $bad
    } 'missing acceptance log'

    Assert-ThrowsLike {
        $bad = New-Fixture -Root (Join-Path $tempRoot 'missing-app-running')
        Write-TextFile -Path $bad.Acceptance -Content "INTERACTIVE_SESSION=1`nINSTALL_PATH=$expectedInstallPath`nAPP_DATA=preserved`nVERIFY=PASS`n"
        Invoke-Evidence -Fixture $bad
    } '*APP_RUNNING=1*' 'missing APP_RUNNING acceptance marker'

    Assert-ThrowsLike {
        $bad = New-Fixture -Root (Join-Path $tempRoot 'duplicate-app-running')
        Write-TextFile -Path $bad.Acceptance -Content "INTERACTIVE_SESSION=1`nAPP_RUNNING=1`nAPP_RUNNING=1`nINSTALL_PATH=$expectedInstallPath`nAPP_DATA=preserved`nVERIFY=PASS`n"
        Invoke-Evidence -Fixture $bad
    } '*APP_RUNNING=1*' 'duplicate APP_RUNNING acceptance marker'

    $notPresent = New-Fixture -Root (Join-Path $tempRoot 'app-data-not-present')
    Write-TextFile -Path $notPresent.Acceptance -Content "INTERACTIVE_SESSION=1`nAPP_RUNNING=1`nINSTALL_PATH=$expectedInstallPath`nAPP_DATA=not-present`nVERIFY=PASS`n"
    Invoke-Evidence -Fixture $notPresent | Out-Null
    Assert-True (Test-Path -LiteralPath (Join-Path $notPresent.OutputRoot 'v1.3.0\VM-ACCEPTANCE.txt') -PathType Leaf) 'APP_DATA not-present acceptance'

    Assert-ThrowsLike {
        $bad = New-Fixture -Root (Join-Path $tempRoot 'missing-app-data')
        Write-TextFile -Path $bad.Acceptance -Content "INTERACTIVE_SESSION=1`nAPP_RUNNING=1`nINSTALL_PATH=$expectedInstallPath`nVERIFY=PASS`n"
        Invoke-Evidence -Fixture $bad
    } '*APP_DATA*' 'missing APP_DATA acceptance marker'

    Assert-ThrowsLike {
        $bad = New-Fixture -Root (Join-Path $tempRoot 'wrong-app-data')
        Write-TextFile -Path $bad.Acceptance -Content "INTERACTIVE_SESSION=1`nAPP_RUNNING=1`nINSTALL_PATH=$expectedInstallPath`nAPP_DATA=changed`nVERIFY=PASS`n"
        Invoke-Evidence -Fixture $bad
    } '*APP_DATA*' 'wrong APP_DATA acceptance marker'

    Assert-ThrowsLike {
        $bad = New-Fixture -Root (Join-Path $tempRoot 'duplicate-app-data')
        Write-TextFile -Path $bad.Acceptance -Content "INTERACTIVE_SESSION=1`nAPP_RUNNING=1`nINSTALL_PATH=$expectedInstallPath`nAPP_DATA=preserved`nAPP_DATA=preserved`nVERIFY=PASS`n"
        Invoke-Evidence -Fixture $bad
    } '*APP_DATA*' 'duplicate APP_DATA acceptance marker'

    Assert-ThrowsLike {
        $bad = New-Fixture -Root (Join-Path $tempRoot 'both-app-data')
        Write-TextFile -Path $bad.Acceptance -Content "INTERACTIVE_SESSION=1`nAPP_RUNNING=1`nINSTALL_PATH=$expectedInstallPath`nAPP_DATA=preserved`nAPP_DATA=not-present`nVERIFY=PASS`n"
        Invoke-Evidence -Fixture $bad
    } '*APP_DATA*' 'both APP_DATA acceptance markers'

    Assert-ThrowsLike {
        $bad = New-Fixture -Root (Join-Path $tempRoot 'extra-app-data')
        Write-TextFile -Path $bad.Acceptance -Content "INTERACTIVE_SESSION=1`nAPP_RUNNING=1`nINSTALL_PATH=$expectedInstallPath`nAPP_DATA=preserved`nAPP_DATA=changed`nVERIFY=PASS`n"
        Invoke-Evidence -Fixture $bad
    } '*APP_DATA*' 'extra APP_DATA acceptance marker'

    Assert-ThrowsLike {
        $bad = New-Fixture -Root (Join-Path $tempRoot 'missing-install-path')
        Write-TextFile -Path $bad.Acceptance -Content "INTERACTIVE_SESSION=1`nAPP_RUNNING=1`nAPP_DATA=preserved`nVERIFY=PASS`n"
        Invoke-Evidence -Fixture $bad
    } '*INSTALL_PATH*' 'missing INSTALL_PATH acceptance marker'

    Assert-ThrowsLike {
        $bad = New-Fixture -Root (Join-Path $tempRoot 'wrong-install-path')
        Write-TextFile -Path $bad.Acceptance -Content "INTERACTIVE_SESSION=1`nAPP_RUNNING=1`nINSTALL_PATH=C:\Temp\WinWispr.exe`nAPP_DATA=preserved`nVERIFY=PASS`n"
        Invoke-Evidence -Fixture $bad
    } '*INSTALL_PATH*' 'wrong INSTALL_PATH acceptance marker'

    Assert-ThrowsLike {
        $bad = New-Fixture -Root (Join-Path $tempRoot 'duplicate-install-path')
        Write-TextFile -Path $bad.Acceptance -Content "INTERACTIVE_SESSION=1`nAPP_RUNNING=1`nINSTALL_PATH=$expectedInstallPath`nINSTALL_PATH=$expectedInstallPath`nAPP_DATA=preserved`nVERIFY=PASS`n"
        Invoke-Evidence -Fixture $bad
    } '*INSTALL_PATH*' 'duplicate INSTALL_PATH acceptance marker'

    Assert-ThrowsLike {
        $bad = New-Fixture -Root (Join-Path $tempRoot 'extra-install-path')
        Write-TextFile -Path $bad.Acceptance -Content "INTERACTIVE_SESSION=1`nAPP_RUNNING=1`nINSTALL_PATH=$expectedInstallPath`nINSTALL_PATH=C:\Temp\WinWispr.exe`nAPP_DATA=preserved`nVERIFY=PASS`n"
        Invoke-Evidence -Fixture $bad
    } '*INSTALL_PATH*' 'extra INSTALL_PATH acceptance marker'

    Assert-Throws {
        $bad = New-Fixture -Root (Join-Path $tempRoot 'bad-sidecar')
        Set-Content -LiteralPath "$($bad.PortableZip).sha256" -Value ("0" * 64 + " *WinWispr-portable-v1.3.0.zip") -Encoding ascii
        Invoke-Evidence -Fixture $bad
    } 'mismatched artifact sidecar'

    Assert-ThrowsLike {
        $bad = New-Fixture -Root (Join-Path $tempRoot 'portable-with-model')
        New-TinyZip -Path $bad.PortableZip -EntryName 'ModelData/model.tar.bz2' -Content 'model'
        Write-Sha256Sidecar -Path $bad.PortableZip
        Invoke-Evidence $bad
    } '*must not contain models*' 'model in portable archive'

    Assert-Throws {
        $bad = New-Fixture -Root (Join-Path $tempRoot 'forbidden-output')
        $bad.OutputRoot = Join-Path $tempRoot 'models'
        Invoke-Evidence -Fixture $bad
    } 'forbidden output path'

    Assert-Throws {
        $bad = New-Fixture -Root (Join-Path $tempRoot 'existing-final')
        New-Item -ItemType Directory -Force -Path (Join-Path $bad.OutputRoot 'v1.3.0') | Out-Null
        Invoke-Evidence -Fixture $bad
    } 'existing final directory'

    Assert-Throws {
        $bad = New-Fixture -Root (Join-Path $tempRoot 'reparse-log')
        $realLogs = $bad.Logs
        $junctionLogs = Join-Path (Join-Path $tempRoot 'reparse-log') 'logs-junction'
        New-DirectoryJunction -Path $junctionLogs -Target $realLogs
        $bad.Logs = $junctionLogs
        Invoke-Evidence -Fixture $bad
    } 'reparse log directory'

    Assert-Throws {
        $bad = New-Fixture -Root (Join-Path $tempRoot 'reparse-log-child')
        $realChild = Join-Path $bad.Logs 'real-child'
        $junctionChild = Join-Path $bad.Logs 'junction-child'
        New-DirectoryJunction -Path $junctionChild -Target $realChild
        Invoke-Evidence -Fixture $bad
    } 'reparse log child'

    Assert-Throws {
        $bad = New-Fixture -Root (Join-Path $tempRoot 'reparse-output')
        $realOut = Join-Path (Join-Path $tempRoot 'reparse-output') 'real-out'
        $junctionOut = Join-Path (Join-Path $tempRoot 'reparse-output') 'out-junction'
        New-DirectoryJunction -Path $junctionOut -Target $realOut
        $bad.OutputRoot = $junctionOut
        Invoke-Evidence -Fixture $bad
    } 'reparse output root'

    $preexisting = New-Fixture -Root (Join-Path $tempRoot 'preexisting-sidecar')
    New-Item -ItemType Directory -Force -Path $preexisting.OutputRoot | Out-Null
    $preexistingZip = Join-Path $preexisting.OutputRoot 'WinWispr-release-v1.3.0.zip'
    $preexistingSidecar = "$preexistingZip.sha256"
    Set-Content -LiteralPath $preexistingSidecar -Value 'preexisting-sidecar' -Encoding ascii
    Assert-ThrowsLike { Invoke-Evidence -Fixture $preexisting } '*already exists*' 'existing archive sidecar'
    Assert-Equal 'preexisting-sidecar' ((Get-Content -LiteralPath $preexistingSidecar -Raw).Trim()) 'existing sidecar preserved'
    Assert-False (Test-Path -LiteralPath $preexistingZip) 'existing sidecar did not create zip'
    Assert-False (Test-Path -LiteralPath (Join-Path $preexisting.OutputRoot 'v1.3.0')) 'existing sidecar did not create final directory'

    $multiline = New-Fixture -Root (Join-Path $tempRoot 'multiline-commit')
    $message = "feat: release evidence`n`nBody line 1`nBody line 2"
    Write-TextFile -Path $multiline.CommitsPath -Content $message
    Invoke-Evidence -Fixture $multiline | Out-Null
    $multilineBuildInfo = Get-Content -LiteralPath (Join-Path $multiline.OutputRoot 'v1.3.0\BUILD-INFO.json') -Raw | ConvertFrom-Json
    Assert-Equal 1 $multilineBuildInfo.commitsSincePreviousTag.Count 'multiline commit count'
    Assert-Equal $message ([string]$multilineBuildInfo.commitsSincePreviousTag[0]) 'multiline commit body preserved'
    $multilineChangelog = Get-Content -LiteralPath (Join-Path $multiline.OutputRoot 'v1.3.0\CHANGELOG-v1.3.0.md') -Raw
    Assert-True ($multilineChangelog.Contains($message)) 'multiline changelog entry preserved'

    $messageWithWhitespace = "feat: preserve release framing`n`n body keeps leading whitespace`ntrailing space "
    $framed = New-Fixture -Root (Join-Path $tempRoot 'framed-whitespace')
    Write-TextFile -Path $framed.CommitsPath -Content $messageWithWhitespace
    Invoke-Evidence -Fixture $framed | Out-Null
    $framedBuildInfo = Get-Content -LiteralPath (Join-Path $framed.OutputRoot 'v1.3.0\BUILD-INFO.json') -Raw | ConvertFrom-Json
    Assert-Equal 1 $framedBuildInfo.commitsSincePreviousTag.Count 'framed whitespace commit count'
    Assert-Equal $messageWithWhitespace ([string]$framedBuildInfo.commitsSincePreviousTag[0]) 'framed whitespace commit preserved'

    $separator = [string][char]0x1e
    foreach ($badFrame in @(
        @{ Name = 'leading-rs'; Content = "${separator}feat: release evidence" },
        @{ Name = 'trailing-rs'; Content = "feat: release evidence${separator}" },
        @{ Name = 'double-rs'; Content = "feat: release evidence${separator}${separator}fix: reject bad frame" },
        @{ Name = 'whitespace-record'; Content = "feat: release evidence${separator} `t `r`n ${separator}fix: reject bad frame" }
    )) {
        $bad = New-Fixture -Root (Join-Path $tempRoot ('bad-frame-' + $badFrame.Name))
        Write-TextFile -Path $bad.CommitsPath -Content $badFrame.Content
        Assert-ThrowsLike { Invoke-Evidence -Fixture $bad } '*Commit message record*' "bad commit RS framing $($badFrame.Name)"
    }

    Write-Output 'ReleaseEvidence tests passed.'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
