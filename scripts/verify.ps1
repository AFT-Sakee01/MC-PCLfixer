param(
    [string]$Dotnet = (Join-Path (Split-Path $PSScriptRoot -Parent) '.dotnet\dotnet.exe'),
    [string]$Fixture = (Join-Path (Split-Path $PSScriptRoot -Parent) 'Work\upstream\latest\Plain Craft Launcher 2.exe')
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path -LiteralPath $Fixture)) { throw 'Supply -Fixture with an unmodified PCL 2.99.0.0 executable.' }
$testPath = Join-Path $projectRoot ('Work\tests\verify-' + [guid]::NewGuid().ToString('N'))
& $Dotnet run --project (Join-Path $projectRoot 'tests\Tests.csproj') -c Release -- $Fixture $testPath
if ($LASTEXITCODE -ne 0) { throw 'Behavior checks failed' }
& $Dotnet build (Join-Path $projectRoot 'bootstrap\tests\BootstrapTests.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Bootstrap test build failed' }
& $Dotnet build (Join-Path $projectRoot 'bootstrap\tests\BootstrapFixture.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Bootstrap CLR fixture build failed' }
& (Join-Path $projectRoot 'bootstrap\tests\bin\Release\net48\PCL.Repair.Bootstrap.Tests.exe') (Join-Path $projectRoot 'bootstrap\tests\bin\Release\net48\BootstrapFixture.exe')
if ($LASTEXITCODE -ne 0) { throw 'Bootstrap protocol checks failed' }
& $Dotnet run --project (Join-Path $projectRoot 'tools\feed-publisher\FeedPublisher.csproj') -c Release -- self-test
if ($LASTEXITCODE -ne 0) { throw 'Recipe publisher checks failed' }
& $Dotnet run --project (Join-Path $projectRoot 'tools\recipe-maintainer\RecipeMaintainer.csproj') -c Release -- self-test $Fixture (Join-Path $testPath 'maintenance')
if ($LASTEXITCODE -ne 0) { throw 'Recipe maintainer checks failed' }
python -X utf8 (Join-Path $projectRoot 'tests\test_maintainer.py')
if ($LASTEXITCODE -ne 0) { throw 'Maintenance orchestration checks failed' }
python -X utf8 (Join-Path $projectRoot 'tests\test_official_pcl.py')
if ($LASTEXITCODE -ne 0) { throw 'Official distribution download checks failed' }
python (Join-Path $projectRoot 'scripts\check-version.py')
if ($LASTEXITCODE -ne 0) { throw 'Version check failed' }
python (Join-Path $projectRoot 'Docs\validate_docs.py') --root $projectRoot --strict
if ($LASTEXITCODE -ne 0) { throw 'Documentation check failed' }
