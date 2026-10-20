param(
    [ValidateSet('win-arm64','win-x64')][string]$Runtime = 'win-arm64',
    [string]$Fixture = (Join-Path (Split-Path $PSScriptRoot -Parent) 'Work\upstream\latest\Plain Craft Launcher 2.exe')
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $projectRoot ('Work\update-e2e-' + $Runtime + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $testRoot 'PCL') | Out-Null
$target = Join-Path $testRoot 'Plain Craft Launcher 2.exe'
$temporaryUpdater = Join-Path $testRoot 'PCL\Plain Craft Launcher 2.exe'
$helper = Join-Path $testRoot 'PCL-Download-Repair.exe'
Copy-Item -LiteralPath $Fixture -Destination $target
Copy-Item -LiteralPath $Fixture -Destination $temporaryUpdater
Copy-Item -LiteralPath (Join-Path $projectRoot "Artifacts\$Runtime\PCL-Download-Repair.exe") -Destination $helper
# This is a fresh test-only setting, never the user's archive configuration.
Set-Content -LiteralPath (Join-Path $testRoot 'PCL\Setup.ini') -Value 'SystemSystemUpdate:3' -Encoding utf8
& $helper enable-auto $target
if ($LASTEXITCODE -ne 0) { throw 'Bootstrap setup failed' }
$expected = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
$configHash = (Get-FileHash -LiteralPath ($target + '.config') -Algorithm SHA256).Hash
$sentinel = $null
try {
    # The real PCL updater terminates its supplied parent PID. Give it a process
    # created only for this test; never enumerate or stop an existing launcher.
    $sentinel = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') `
        -ArgumentList '-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 120"' -WindowStyle Hidden -PassThru
    $start = [Diagnostics.ProcessStartInfo]::new($temporaryUpdater)
    $start.WorkingDirectory = $testRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($arg in @('--update', [string]$sentinel.Id, 'Plain Craft Launcher 2.exe', 'Plain Craft Launcher 2.exe', 'True')) {
        $start.ArgumentList.Add($arg)
    }
    $updater = [Diagnostics.Process]::Start($start)
    $complete = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        Start-Sleep -Milliseconds 300
        $log = Join-Path $testRoot 'logs\full.jsonl'
        if ((Test-Path -LiteralPath $log) -and (Select-String -LiteralPath $log -SimpleMatch '"event":"auto_repaired"' -Quiet)) {
            $complete = $true
            break
        }
    }
    if (-not $complete) { throw "Real updater handoff did not finish. Inspect $testRoot" }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Unexpected repaired output' }
    if ((Get-FileHash -LiteralPath ($target + '.config') -Algorithm SHA256).Hash -ne $configHash) { throw 'Updater changed local bootstrap config' }
    $state = Get-Content -LiteralPath ($target + '.pcl-repair.json') -Raw | ConvertFrom-Json
    if ($state.verified_sha256 -ne $expected) { throw 'Bootstrap state did not commit repaired hash' }
    & $helper inspect $target
    if ($LASTEXITCODE -ne 0) { throw 'Final structure verification failed' }
    Write-Output "REAL UPDATE HANDOFF PASSED ($Runtime)"
    Write-Output "Test directory: $testRoot"
    Write-Output 'The test PCL window is left open for visual inspection.'
}
finally {
    if ($null -ne $sentinel) {
        if (-not $sentinel.HasExited) { $sentinel.Kill() }
        $sentinel.Dispose()
    }
}
