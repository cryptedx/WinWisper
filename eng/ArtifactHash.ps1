function Get-ArtifactSha256 {
    param([Parameter(Mandatory)] [string]$LiteralPath)

    # Buffer sequential reads so hashing large files does not issue tiny SMB reads.
    $stream = [IO.FileStream]::new($LiteralPath, [IO.FileMode]::Open,
        [IO.FileAccess]::Read, [IO.FileShare]::Read, 1MB, [IO.FileOptions]::SequentialScan)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}
