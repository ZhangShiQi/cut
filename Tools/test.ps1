param([string]$OutputDirectory)
. "$PSScriptRoot\common.ps1"
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('artifacts\checks-' + [guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Push-Location $projectRoot
try {
    Invoke-DotNet @('build', 'Cut.csproj', '-c', 'Release', '--nologo')
    Invoke-DotNet @('build', 'Tests\Updates\Cut.UpdateTests.csproj', '-c', 'Release', '--nologo')
    $application = Join-Path $projectRoot 'bin\Release\net8.0-windows\win-x64\Cut.exe'
    # Each existing suite uses synthetic fixtures, test files and isolated settings.
    $suites = @('self', 'qr', 'window', 'ocr', 'translation', 'ai', 'vision', 'annotation', 'scroll', 'auto-scroll', 'scroll-optimization', 'scroll-permissive', 'startup', 'update-ui', 'recording', 'recording-native', 'recording-ui-native')
    foreach ($suite in $suites) {
        $process = Start-Process -FilePath $application -ArgumentList "--$suite-test" -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
        if (!$process.WaitForExit(240000)) { $process.Kill(); throw "Test timed out: $suite" }
        if ($process.ExitCode -ne 0) { throw "Application suite failed: $suite. Inspect artifacts/*-test.txt." }
        Write-Output "PASS application suite: $suite"
    }
    Invoke-DotNet @((Join-Path $projectRoot 'Tests\Updates\bin\Release\net8.0-windows\Cut.UpdateTests.dll'), (Join-Path $OutputDirectory 'updates'))
    & (Join-Path $PSScriptRoot 'test-release.ps1')
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'result.txt'), "ALL APPLICATION AND UPDATE CHECKS PASSED`r`n")
}
finally { Pop-Location }
