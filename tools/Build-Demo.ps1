$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
dotnet run --project (Join-Path $PSScriptRoot 'DemoExport/DemoExport.csproj') -- $root
if($LASTEXITCODE -ne 0){throw 'Synthetic fixture export failed'}
$destination=Join-Path $root 'docs/tour'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -Path (Join-Path $root 'tauri/web/*') -Destination $destination -Recurse -Force
Write-Output 'Interactive tour built from synthetic data only.'
