param(
    [string]$VcRedistDirectory = '',
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $projectRoot
try {
    if (-not $VcRedistDirectory) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) {
            $vs = & $vswhere -latest -products '*' -property installationPath
            if ($vs) {
                $redistRoot = Join-Path $vs 'VC\Redist\MSVC'
                $VcRedistDirectory = Get-ChildItem -LiteralPath $redistRoot -Directory |
                    Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
                    Sort-Object { [version]$_.Name } -Descending |
                    ForEach-Object { Join-Path $_.FullName 'x64\Microsoft.VC143.CRT' } |
                    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
            }
        }
    }
    $runtimeNames = @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
    foreach ($name in $runtimeNames) {
        if (-not $VcRedistDirectory -or -not (Test-Path -LiteralPath (Join-Path $VcRedistDirectory $name))) {
            throw '需要 Visual Studio 的 x64 REDIST 运行库目录。请通过 -VcRedistDirectory 指定 Microsoft.VC143.CRT 文件夹。'
        }
    }
    & (Join-Path $PSScriptRoot 'download-ocr-models.ps1')
    [xml]$project = Get-Content -LiteralPath 'Cut.csproj'
    $version = $project.Project.PropertyGroup.Version
    $destination = Join-Path $projectRoot "artifacts\Cut-$version-windows-x64"
    $arguments = @('publish', 'Cut.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $destination)
    if ($NoRestore) { $arguments += '--no-restore' }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    foreach ($name in $runtimeNames) { Copy-Item -LiteralPath (Join-Path $VcRedistDirectory $name) -Destination $destination -Force }
    Copy-Item -LiteralPath 'README.md' -Destination $destination -Force
    # These linker inputs/debug symbols are not runtime dependencies.
    Get-ChildItem -LiteralPath $destination -File | Where-Object { $_.Extension -in '.lib', '.pdb' } | Remove-Item -Force
    Write-Output "Published: $destination"
    Write-Output 'Run the checks on this directory, then zip it with its Models and Licenses folders.'
}
finally { Pop-Location }
