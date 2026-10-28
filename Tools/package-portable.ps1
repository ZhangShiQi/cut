param([string]$OutputDirectory, [string]$VcRedistDirectory = '', [switch]$NoRestore)
. "$PSScriptRoot\common.ps1"
$version = Get-CutVersion
$packageName = "Cut-$version-win-x64-portable"
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot "artifacts\packages\$packageName" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $OutputDirectory) -or (Test-Path -LiteralPath ($OutputDirectory + '.zip'))) { throw 'Output already exists. Use a new directory.' }
if (!$VcRedistDirectory) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $vs = & $vswhere -latest -products '*' -property installationPath
        if ($vs) {
            $VcRedistDirectory = Get-ChildItem -LiteralPath (Join-Path $vs 'VC\Redist\MSVC') -Directory |
                Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending |
                ForEach-Object { Join-Path $_.FullName 'x64\Microsoft.VC143.CRT' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        }
    }
}
$runtimeNames = @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
foreach ($name in $runtimeNames) {
    if (!$VcRedistDirectory -or !(Test-Path -LiteralPath (Join-Path $VcRedistDirectory $name))) { throw 'Specify -VcRedistDirectory pointing to the x64 Microsoft.VC143.CRT redist directory.' }
}
$buildRoot = Join-Path $projectRoot ('artifacts\package-build-' + [guid]::NewGuid().ToString('N'))
$runtime = Join-Path $buildRoot 'runtime'
$launcher = Join-Path $buildRoot 'launcher'
$restore = @(); if ($NoRestore) { $restore = @('--no-restore') }
Push-Location $projectRoot
try {
    & (Join-Path $PSScriptRoot 'download-ocr-models.ps1')
    Invoke-DotNet (@('publish', 'Cut.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--artifacts-path', (Join-Path $buildRoot 'build'), '-p:PublishSingleFile=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $runtime) + $restore)
    Invoke-DotNet (@('publish', 'Launcher\Cut.Launcher.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--artifacts-path', (Join-Path $buildRoot 'build'), '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $launcher) + $restore)
    New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'tools\runtime') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $launcher 'Cut.Launcher.exe') -Destination (Join-Path $OutputDirectory 'Cut.exe')
    Copy-Item -Path (Join-Path $runtime '*') -Destination (Join-Path $OutputDirectory 'tools\runtime') -Recurse
    foreach ($name in $runtimeNames) { Copy-Item -LiteralPath (Join-Path $VcRedistDirectory $name) -Destination (Join-Path $OutputDirectory 'tools\runtime') }
    Get-ChildItem -LiteralPath (Join-Path $OutputDirectory 'tools\runtime') -File | Where-Object { $_.Extension -in '.lib', '.pdb' } | Remove-Item -Force
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'portable.flag'), 'Cut portable; user settings remain in LocalAppData/Cut.')
    [IO.File]::WriteAllText((Join-Path $OutputDirectory '使用说明.txt'), "Cut $version`r`n运行根目录 Cut.exe，保留完整目录。配置位于 %LOCALAPPDATA%\Cut。`r`n设置中检查更新，完成编辑后点击重启并更新。旧版 0.13.0 及以前需手动安装本版一次。`r`n不要删除 .cut-update 或当前/上一版本目录。普通更新不替换稳定启动器。`r`n")
    $files = [ordered]@{}
    Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($OutputDirectory.TrimEnd('\').Length + 1).Replace('\', '/')
        $files[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $commit = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine source commit.' }
    $sourceDirty = [bool]@(& git status --porcelain --untracked-files=normal).Count
    $manifest = [ordered]@{ application='Cut'; version=$version; runtimeIdentifier='win-x64'; selfContained=$true; singleFile=$false; formatVersion=2; launcherProtocol=1; dataSchemaVersion=1; runtimePath='tools/runtime/Cut.exe'; executableSha256=$files['Cut.exe']; runtimeSha256=$files['tools/runtime/Cut.exe']; sourceCommit=$commit; sourceDirty=$sourceDirty; files=$files }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'release-manifest.json'), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    New-CutArchive -Directory $OutputDirectory -ZipPath ($OutputDirectory + '.zip') -PackageName $packageName
    $hash = (Get-FileHash -LiteralPath ($OutputDirectory + '.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($OutputDirectory + '.zip.sha256'), "$hash *$packageName.zip`r`n", [Text.UTF8Encoding]::new($false))
    Write-Output "Portable ZIP: $OutputDirectory.zip"
}
finally { Pop-Location }
