param([Parameter(Mandatory=$true)][string]$Version)
. "$PSScriptRoot\common.ps1"
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Use a three-part stable version.' }
$path = Join-Path $projectRoot 'Directory.Build.props'
$text = [IO.File]::ReadAllText($path)
$text = [regex]::Replace($text, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
Write-Output "Version set to $Version. Add CHANGELOG.md notes before releasing."
