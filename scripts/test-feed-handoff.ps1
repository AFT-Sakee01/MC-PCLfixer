param(
    [Parameter(Mandatory=$true)][string]$FixtureDirectory,
    [ValidateSet('win-arm64','win-x64')][string]$Runtime='win-arm64'
)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$fixtures=(Resolve-Path -LiteralPath $FixtureDirectory).Path
foreach($name in @('future.exe','expected.sha256','publisher-public.pem','feed.json')) {
    if(-not (Test-Path -LiteralPath (Join-Path $fixtures $name))) {throw "Missing generated feed fixture: $name"}
}
$testRoot=Join-Path $projectRoot ('Work/feed-handoff-'+$Runtime+'-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$target=Join-Path $testRoot 'Plain Craft Launcher 2.exe'
$helper=Join-Path $testRoot 'PCL-Download-Repair.exe'
Copy-Item -LiteralPath (Join-Path $projectRoot 'Work/upstream/latest/Plain Craft Launcher 2.exe') -Destination $target
Copy-Item -LiteralPath (Join-Path $projectRoot "Artifacts/$Runtime/PCL-Download-Repair.exe") -Destination $helper
& $helper enable-auto $target
if($LASTEXITCODE -ne 0){throw 'Initial bootstrap setup failed'}
# This deliberately non-routable test source is overridden only in the new copy.
# The normal verifier must accept its freshly signed cache without networking.
& $helper source-set 'https://recipes.example.test/handoff.json' (Join-Path $fixtures 'publisher-public.pem')
if($LASTEXITCODE -ne 0){throw 'Fixture source setup failed'}
$source=Get-Content -LiteralPath (Join-Path $testRoot 'recipe-source.json') -Raw | ConvertFrom-Json
$sourceBytes=[Text.Encoding]::UTF8.GetBytes($source.url+"`n"+$source.public_key_pem)
$sourceHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($sourceBytes)).ToLowerInvariant()
$cache=Join-Path $testRoot ('recipe-cache/'+$sourceHash)
New-Item -ItemType Directory -Path $cache -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $fixtures 'feed.json') -Destination (Join-Path $cache 'feed.json')
New-Item -ItemType Directory -Path (Join-Path $testRoot 'PCL') | Out-Null
# Suppress unrelated real updates only in the isolated fixture.
'SystemSystemUpdate:3' | Set-Content -LiteralPath (Join-Path $testRoot 'PCL/Setup.ini') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $fixtures 'future.exe') -Destination $target -Force
$expected=(Get-Content -LiteralPath (Join-Path $fixtures 'expected.sha256') -Raw).Trim()
$initial=Start-Process -FilePath $target -WorkingDirectory $testRoot -WindowStyle Hidden -PassThru
$deadline=[DateTime]::UtcNow.AddSeconds(45)
$eventFound=$false
while([DateTime]::UtcNow -lt $deadline) {
    $log=Join-Path $testRoot 'logs/full.jsonl'
    if(Test-Path -LiteralPath $log) {
        $eventFound=[bool](Get-Content -LiteralPath $log | Where-Object {($_ | ConvertFrom-Json).event -eq 'auto_repaired'})
        if($eventFound){break}
    }
    Start-Sleep -Milliseconds 250
}
if(-not $eventFound){throw "Automatic recipe handoff did not finish: $testRoot"}
if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected){throw 'Unexpected final launcher hash'}
$state=Get-Content -LiteralPath ($target+'.pcl-repair.json') -Raw | ConvertFrom-Json
if($state.verified_sha256 -ne $expected){throw 'Bootstrap state did not commit the downloaded-profile result'}
& $helper inspect $target
if($LASTEXITCODE -ne 0){throw 'Cached external profile no longer matches'}
Write-Output "SIGNED RECIPE HANDOFF PASSED: $testRoot"
Write-Output 'The isolated PCL window is left open for visual inspection.'
