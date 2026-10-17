$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\eng\ArtifactHash.ps1')
$path = [IO.Path]::GetTempFileName()
try {
    [IO.File]::WriteAllText($path, 'abc', [Text.UTF8Encoding]::new($false))
    if ((Get-ArtifactSha256 $path) -cne 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad') {
        throw 'Known SHA-256 vector failed.'
    }
    $bytes = [byte[]]::new(1MB + 17)
    [Random]::new(42).NextBytes($bytes)
    [IO.File]::WriteAllBytes($path, $bytes)
    if ((Get-ArtifactSha256 $path) -ine (Get-FileHash $path -Algorithm SHA256).Hash) {
        throw 'SHA-256 differs across the read buffer boundary.'
    }
    Write-Output 'Artifact SHA-256: PASS'
}
finally { Remove-Item -LiteralPath $path -Force }
