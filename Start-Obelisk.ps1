[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$app=Join-Path $PSScriptRoot 'tauri/publish/Obelisk.exe'
if(!(Test-Path -LiteralPath $app)){throw 'Build the application with tauri/Build-Obelisk.ps1 first.'}
Start-Process -FilePath $app -WorkingDirectory (Split-Path $app) -WindowStyle Hidden
