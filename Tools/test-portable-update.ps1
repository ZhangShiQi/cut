param([Parameter(Mandatory=$true)][string]$Package, [string]$OutputDirectory)
. "$PSScriptRoot\common.ps1"
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('artifacts\portable-check-' + [guid]::NewGuid().ToString('N')) }
Invoke-DotNet @((Join-Path $projectRoot 'Tests\Updates\bin\Release\net8.0-windows\Cut.UpdateTests.dll'), '--portable', [IO.Path]::GetFullPath($Package), [IO.Path]::GetFullPath($OutputDirectory))
