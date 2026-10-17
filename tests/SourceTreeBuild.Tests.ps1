$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../eng/SourceTreeBuild.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('source-tree-test-' + [Guid]::NewGuid().ToString('N'))
$marker = Join-Path $root 'dev-check/source-tree.sha256'
$treeA = 'a' * 64
$treeB = 'b' * 64

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -cne $Actual) { throw "${Message}: expected '$Expected', got '$Actual'" }
}

try {
    # No recorded tree: the output may come from anything, so rebuild.
    Assert-Equal 'rebuild' (Start-SourceTreeBuild -MarkerPath $marker -SourceTreeSha256 $treeA) 'first build'
    Complete-SourceTreeBuild -MarkerPath $marker -SourceTreeSha256 $treeA
    Assert-Equal $treeA ([IO.File]::ReadAllText($marker)) 'recorded tree'

    # The same tree again stays incremental.
    Assert-Equal 'incremental' (Start-SourceTreeBuild -MarkerPath $marker -SourceTreeSha256 $treeA) 'same tree'
    # A build that never completes leaves no claim, so the next one rebuilds.
    if (Test-Path -LiteralPath $marker) { throw 'Starting a build must remove the marker.' }
    Assert-Equal 'rebuild' (Start-SourceTreeBuild -MarkerPath $marker -SourceTreeSha256 $treeA) 'after an unfinished build'
    Complete-SourceTreeBuild -MarkerPath $marker -SourceTreeSha256 $treeA

    # Another checkout's tree on the shared mirror forces a rebuild, and back again.
    Assert-Equal 'rebuild' (Start-SourceTreeBuild -MarkerPath $marker -SourceTreeSha256 $treeB) 'other tree'
    Complete-SourceTreeBuild -MarkerPath $marker -SourceTreeSha256 $treeB
    Assert-Equal 'rebuild' (Start-SourceTreeBuild -MarkerPath $marker -SourceTreeSha256 $treeA) 'first tree again'

    # Only a full SHA-256 is accepted.
    $rejected = $false
    try { Start-SourceTreeBuild -MarkerPath $marker -SourceTreeSha256 'abc' | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'A malformed tree name must be rejected.' }
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Output 'SourceTreeBuild tests: PASS'
