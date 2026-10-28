param([string]$VcRedistDirectory = '', [switch]$NoRestore, [string]$OutputDirectory)
# Backward-compatible local packaging entry. Never uploads or publishes.
& "$PSScriptRoot\package-portable.ps1" @PSBoundParameters
