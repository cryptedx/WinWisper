# MSBuild decides what to rebuild by comparing time stamps. The VM mirror keeps
# the Mac's time stamps and is shared by every checkout and worktree, so a file
# edited before another checkout's last build looks older than that build's
# output, and an incremental build silently keeps the old code. The caller
# therefore names the synced tree (its file list, sizes, and time stamps), and
# a build stays incremental only when its output was built from that same tree.

# Returns 'incremental' when the output next to $MarkerPath was built from
# $SourceTreeSha256, otherwise 'rebuild'. The marker is removed either way, so
# a build that fails or is interrupted leaves no claim about its output.
function Start-SourceTreeBuild {
    param(
        [Parameter(Mandatory)] [string]$MarkerPath,
        [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{64}$')] [string]$SourceTreeSha256
    )
    $built = $null
    if (Test-Path -LiteralPath $MarkerPath -PathType Leaf) {
        $built = ([IO.File]::ReadAllText($MarkerPath)).Trim()
        Remove-Item -LiteralPath $MarkerPath -Force
    }
    if ($built -ceq $SourceTreeSha256) { 'incremental' } else { 'rebuild' }
}

# Records that the output next to $MarkerPath is now built from $SourceTreeSha256.
function Complete-SourceTreeBuild {
    param(
        [Parameter(Mandatory)] [string]$MarkerPath,
        [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{64}$')] [string]$SourceTreeSha256
    )
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $MarkerPath) | Out-Null
    [IO.File]::WriteAllText($MarkerPath, $SourceTreeSha256, [Text.Encoding]::ASCII)
}
