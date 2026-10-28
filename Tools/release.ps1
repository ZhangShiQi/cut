[CmdletBinding(SupportsShouldProcess=$true, ConfirmImpact='Low')]
param([switch]$Publish, [switch]$Login, [string]$OutputDirectory, [string]$VcRedistDirectory = '')
. "$PSScriptRoot\common.ps1"
$version = Get-CutVersion
$tag = "v$version"
$notesText = Get-Content -LiteralPath (Join-Path $projectRoot 'CHANGELOG.md') -Raw -Encoding UTF8
$notes = [regex]::Match($notesText, '(?ms)^## \[' + [regex]::Escape($version) + '\][^\r\n]*\r?\n(.*?)(?=^## |\z)')
if (!$notes.Success -or !$notes.Groups[1].Value.Trim()) { throw 'Release notes missing for this version.' }
if ($WhatIfPreference) {
    Write-Output "Cut $version : local regression tests -> fresh package -> actual portable upgrade/rollback checks."
    if ($Publish) { Write-Output 'Then: verify source/remote -> push commit/tag -> upload draft -> verify assets -> publish.' }
    else { Write-Output 'Local preparation only. No credentials, fetches, commits, tags, pushes, uploads or Releases.' }
    return
}
if ($env:CUT_TEST_FILTER) { throw 'Filtered tests are not permitted for release preparation.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('artifacts\releases\' + $version + '-' + [guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a fresh release output directory.' }
function Read-Git([string[]]$Arguments) {
    $result = @(& git @Arguments)
    if ($LASTEXITCODE -ne 0) { throw ('git failed: ' + ($Arguments -join ' ')) }
    return $result
}
$token = $null
Push-Location $projectRoot
try {
    $gitRoot = (Read-Git @('rev-parse','--show-toplevel')) -join ''
    if ([IO.Path]::GetFullPath($gitRoot).TrimEnd('\','/') -ne $projectRoot.TrimEnd('\','/')) { throw 'Run release preparation from the Cut repository.' }
    $head = (Read-Git @('rev-parse', 'HEAD')) -join ''
    if ($Publish) {
        if (@(Read-Git @('status', '--porcelain', '--untracked-files=normal')).Count) { throw 'Commit all intended files before publishing. No auto-commit is performed.' }
        if (((Read-Git @('symbolic-ref', '--quiet', '--short', 'HEAD')) -join '') -ne 'main') { throw 'Publish from main.' }
        foreach ($direction in @(@('remote','get-url','origin'), @('remote','get-url','--push','--all','origin'))) {
            $urls = @(Read-Git $direction)
            if ($urls.Count -ne 1 -or $urls[0] -notmatch '^https://github\.com/ZhangShiQi/cut(?:\.git)?/?$') { throw 'origin must refer only to the official cut repository.' }
        }
        foreach ($required in @('Tools/release.ps1','Tools/publish-release.ps1','Tools/package-portable.ps1','Tools/test.ps1','Tools/test-portable-update.ps1','Directory.Build.props','CHANGELOG.md','Launcher/Cut.Launcher.csproj','UpdateEngine/Cut.UpdateEngine.csproj')) {
            Read-Git @('cat-file','-e',"HEAD:$required") | Out-Null
        }
    }
    & "$PSScriptRoot\test.ps1" -OutputDirectory (Join-Path $OutputDirectory 'checks')
    $packageDirectory = Join-Path $OutputDirectory "Cut-$version-win-x64-portable"
    & "$PSScriptRoot\package-portable.ps1" -OutputDirectory $packageDirectory -VcRedistDirectory $VcRedistDirectory
    $package = $packageDirectory + '.zip'
    $manifest = Get-Content -LiteralPath (Join-Path $packageDirectory 'release-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.version -ne $version -or $manifest.runtimeIdentifier -ne 'win-x64' -or $manifest.sourceCommit -ne $head -or ($Publish -and $manifest.sourceDirty)) { throw 'Package does not match the verified source/version/platform.' }
    $expected = ((Get-Content -LiteralPath ($package + '.sha256') -Raw).Trim() -split '\s+')[0]
    if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $expected) { throw 'Package checksum mismatch.' }
    & "$PSScriptRoot\test-portable-update.ps1" -Package $package -OutputDirectory (Join-Path $OutputDirectory 'portable-check')
    if (!$Publish) { Write-Output "LOCAL VERIFIED PACKAGE: $package"; Write-Output 'No source refs or remote publishing state were changed.'; return }
    if (((Read-Git @('rev-parse','HEAD')) -join '') -ne $head -or @(Read-Git @('status','--porcelain','--untracked-files=normal')).Count) { throw 'Source changed during verification.' }
    foreach ($line in @(Read-Git @('ls-remote','--tags','origin','refs/tags/v*'))) {
        if ($line -match '\srefs/tags/v(\d+\.\d+\.\d+)(\^\{\})?$' -and [version]$Matches[1] -gt [version]$version) { throw 'A newer version already exists remotely.' }
    }
    Read-Git @('fetch','--no-tags','origin','main') | Out-Null
    & git merge-base --is-ancestor FETCH_HEAD HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Integrate remote main before publishing.' }
    $remoteTag = @(Read-Git @('ls-remote','--tags','origin',"refs/tags/$tag", "refs/tags/$tag^{}"))
    if ($remoteTag.Count -and (($remoteTag[-1] -split '\s+')[0] -ne $head)) { throw 'Remote tag points to another commit; it will not be moved.' }
    $localTag = @(Read-Git @('tag','--list',$tag))
    if ($localTag.Count -and ((Read-Git @('rev-parse',"$tag^{}")) -join '') -ne $head) { throw 'Local tag points to another commit.' }
    . "$PSScriptRoot\publish-release.ps1"
    $token = Get-ReleaseCredential -Login $Login
    Get-ReleaseRepository -Token $token | Out-Null
    $existing = Get-ReleaseState -Tag $tag -Token $token
    if ($existing -and !$existing.draft) { throw 'Published versions are immutable; use a new version.' }
    if (!$PSCmdlet.ShouldProcess('https://github.com/ZhangShiQi/cut', "Push verified $tag and publish its Release")) { return }
    if (!$localTag.Count) { Read-Git @('tag','-a',$tag,'-m',"Cut $version",$head) | Out-Null }
    Read-Git @('push','--atomic','origin',"${head}:refs/heads/main","refs/tags/${tag}:refs/tags/$tag") | Out-Null
    Send-LocalRelease -Tag $tag -Version $version -Commit $head -Package $package -Notes $notes.Groups[1].Value.Trim() -Token $token
    Write-Output "Published: https://github.com/ZhangShiQi/cut/releases/tag/$tag"
}
finally { $token = $null; Pop-Location }
