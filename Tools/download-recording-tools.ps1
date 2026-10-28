$ErrorActionPreference = 'Stop'
$toolRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\MediaTools\ffmpeg'))
$manifest = Get-Content -LiteralPath (Join-Path $toolRoot 'manifest.json') -Raw | ConvertFrom-Json
$missing = @($manifest.files | Where-Object {
    $file = Join-Path $toolRoot $_.file
    !(Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $_.sha256
})
if (!$missing.Count) { Write-Output "Verified recording tools $($manifest.version)"; return }
$archivePath = Join-Path $toolRoot 'tools.download.zip'
if (!(Test-Path -LiteralPath $archivePath) -or (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $manifest.archiveSha256) {
    Invoke-WebRequest -Uri $manifest.url -OutFile $archivePath
}
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $manifest.archiveSha256) { throw 'Recording archive SHA256 mismatch; tools were not installed.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    foreach ($tool in $manifest.files) {
        $entry = $archive.GetEntry("$($manifest.archiveRoot)/bin/$($tool.file)")
        if (!$entry) { throw "Missing archive entry: $($tool.file)" }
        $destination = Join-Path $toolRoot $tool.file
        $temporary = $destination + '.download'
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $temporary, $true)
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $tool.sha256) { throw "Recording tool SHA256 mismatch: $($tool.file)" }
        Move-Item -LiteralPath $temporary -Destination $destination -Force
    }
}
finally { $archive.Dispose() }
Remove-Item -LiteralPath $archivePath
Write-Output "Installed verified recording tools $($manifest.version)"
