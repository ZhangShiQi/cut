. "$PSScriptRoot\common.ps1"
. "$PSScriptRoot\publish-release.ps1"
function Assert([bool]$Value, [string]$Message) { if (!$Value) { throw $Message } }
foreach ($url in @('http://api.github.com/repos/ZhangShiQi/cut','https://api.github.com/repos/other/cut','https://evil.example/repos/ZhangShiQi/cut','https://api.github.com:8443/repos/ZhangShiQi/cut','https://user@api.github.com/repos/ZhangShiQi/cut')) {
    $rejected = $false
    try { Invoke-ReleaseApi -Url $url -Token 'synthetic' } catch { $rejected = $true }
    Assert $rejected 'Publisher accepted an untrusted endpoint.'
}
$fixture = Join-Path $projectRoot ('artifacts\publisher-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$package = Join-Path $fixture 'Cut-1.0.0-win-x64-portable.zip'
[IO.File]::WriteAllText($package, 'synthetic package')
[IO.File]::WriteAllText(($package + '.sha256'), 'synthetic checksum')
$script:published = $false; $script:badDigest = $false; $script:existingPublished = $false; $script:uploads = 0
function Get-ReleaseState { param($Tag,$Token) if ($script:existingPublished) { return @{ id=1; draft=$false } }; return $null }
function Invoke-ReleaseApi {
    param($Method='GET',$Url,$Token,$Body,$FilePath,[switch]$AllowNotFound)
    if ($FilePath) {
        $script:uploads++
        $hash = (Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($script:badDigest) { $hash = 'wrong' }
        return @{ digest=('sha256:' + $hash) }
    }
    if ($Method -eq 'PATCH') { Assert ($script:uploads -eq 2) 'Published before both assets were verified.'; $script:published=$true; return @{} }
    return @{ id=1; draft=$true; assets=@() }
}
Send-LocalRelease -Tag v1.0.0 -Version 1.0.0 -Commit ('a' * 40) -Package $package -Notes 'test' -Token 'synthetic'
Assert $script:published 'Verified draft not published in mock.'
$script:published=$false; $script:uploads=0; $script:badDigest=$true
$rejected=$false
try { Send-LocalRelease -Tag v1.0.0 -Version 1.0.0 -Commit ('a' * 40) -Package $package -Notes 'test' -Token 'synthetic' } catch { $rejected=$true }
Assert ($rejected -and !$script:published) 'Digest mismatch did not leave draft unpublished.'
$script:badDigest=$false; $script:existingPublished=$true
$rejected=$false
try { Send-LocalRelease -Tag v1.0.0 -Version 1.0.0 -Commit ('a' * 40) -Package $package -Notes 'test' -Token 'synthetic' } catch { $rejected=$true }
Assert $rejected 'Published Release was overwritten.'
$source=Join-Path $fixture 'archive'; New-Item -ItemType Directory -Path $source | Out-Null
[IO.File]::WriteAllText((Join-Path $source 'file.txt'), 'unicode content')
$archive=Join-Path $fixture 'archive.zip'
New-CutArchive -Directory $source -ZipPath $archive -PackageName 'Cut-1.0.0-win-x64-portable'
$zip=[IO.Compression.ZipFile]::OpenRead($archive)
try { Assert ($zip.Entries[0].FullName -eq 'Cut-1.0.0-win-x64-portable/file.txt') 'ZIP separators are not portable.' } finally { $zip.Dispose() }
$rejected=$false; try { New-CutArchive -Directory $source -ZipPath $archive -PackageName 'test' } catch { $rejected=$true }; Assert $rejected 'Existing archive overwritten.'
New-Item -ItemType Directory -Path (Join-Path $source 'Data') | Out-Null
[IO.File]::WriteAllText((Join-Path $source 'Data\private.txt'), 'private')
$rejected=$false; try { New-CutArchive -Directory $source -ZipPath (Join-Path $fixture 'private.zip') -PackageName 'test' } catch { $rejected=$true }; Assert $rejected 'Personal data packaged.'
Write-Output 'PASS publisher endpoint boundaries, draft verification, digest mismatch, published-version protection and archive validation (mock HTTP only).'
