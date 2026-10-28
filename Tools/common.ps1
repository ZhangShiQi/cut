$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (Test-Path -LiteralPath (Join-Path $projectRoot '.dotnet\.nuget\packages')) {
    if (!$env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $projectRoot '.dotnet\.nuget\packages' }
    if (!$env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet' }
}
function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('dotnet failed: ' + ($Arguments -join ' ')) }
}
function Get-CutVersion {
    [xml]$props = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
    $version = [string]$props.Project.PropertyGroup.Version
    if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Invalid stable version.' }
    return $version
}
function New-CutArchive([string]$Directory, [string]$ZipPath, [string]$PackageName) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.IO.Compression
    if (Test-Path -LiteralPath $ZipPath) { throw 'Archive already exists; it will not be overwritten.' }
    $zip = [IO.Compression.ZipFile]::Open($ZipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($path in [IO.Directory]::EnumerateFiles($Directory, '*', [IO.SearchOption]::AllDirectories)) {
            if (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'File links cannot be packaged.' }
            $relative = $path.Substring($Directory.TrimEnd('\').Length + 1).Replace('\', '/')
            if ($relative -match '(^|/)(Data|device|updates|\.git|\.cut-update)(/|$)') { throw 'Personal data or update state cannot be packaged.' }
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $path, "$PackageName/$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $zip.Dispose() }
}
