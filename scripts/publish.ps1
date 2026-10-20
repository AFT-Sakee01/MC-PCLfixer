param(
    [string]$Dotnet = (Join-Path (Split-Path $PSScriptRoot -Parent) '.dotnet\dotnet.exe'),
    [string[]]$Runtimes = @('win-arm64','win-x64','linux-arm64','linux-x64')
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
foreach ($runtime in $Runtimes) {
    if ($runtime -notin @('win-arm64','win-x64','linux-arm64','linux-x64')) { throw 'Unsupported runtime identifier' }
    & $Dotnet publish (Join-Path $projectRoot 'src\PclRepair.csproj') -c Release -r $runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o (Join-Path $projectRoot "Artifacts\$runtime") --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $runtime" }
}
