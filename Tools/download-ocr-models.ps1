$ErrorActionPreference = 'Stop'
$modelRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Models'))
$manifest = Get-Content -LiteralPath (Join-Path $modelRoot 'manifest.json') -Raw | ConvertFrom-Json
foreach ($model in $manifest.models) {
    $destination = Join-Path $modelRoot $model.file
    if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $model.sha256) {
        Write-Output "Verified $($model.file)"
        continue
    }
    $temporary = "$destination.download"
    Write-Output "Downloading $($model.file)"
    Invoke-WebRequest -Uri $model.url -OutFile $temporary
    if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $model.sha256) {
        throw "SHA256 mismatch: $($model.file). Model was not installed."
    }
    Move-Item -LiteralPath $temporary -Destination $destination -Force
}
