param([switch]$RecipeOnly)
# Legacy analysis helper. Production binaries use publish.ps1 and .NET 10.
# Keep the historical command usable, but never build a runtime with missing refs.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$vendorPath = Join-Path $projectRoot 'tools\vendor\Mono.Cecil.dll'
$buildPath = Join-Path $projectRoot 'Work\build'
New-Item -ItemType Directory -Path $buildPath -Force | Out-Null
Copy-Item -LiteralPath $vendorPath -Destination (Join-Path $buildPath 'Mono.Cecil.dll') -Force
& $compilerPath /nologo /optimize+ /target:exe /platform:anycpu /r:System.Runtime.Serialization.dll "/r:$vendorPath" "/out:$buildPath\MakeRecipe.exe" "$projectRoot\src\Recipe.cs" "$projectRoot\tools\MakeRecipe.cs"
if ($LASTEXITCODE -ne 0) { throw 'Recipe compiler failed' }
Write-Output 'Analysis generator built. Use scripts/publish.ps1 for the repair tool.'
