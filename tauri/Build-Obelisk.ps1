[CmdletBinding()]
param([string]$GoExecutable='go')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$go=$GoExecutable
Push-Location (Join-Path $root 'audio-engine')
try {
    & $go test ./...
    if($LASTEXITCODE -ne 0){throw 'Audio engine checks failed'}
    & $go build -trimpath -ldflags '-s -w' -o obelisk-audio.exe .
    if($LASTEXITCODE -ne 0){throw 'Audio engine build failed'}
} finally {Pop-Location}
dotnet publish (Join-Path $root 'windows/ObeliskBridge/ObeliskBridge.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $PSScriptRoot 'resources/bridge') --nologo -v:q -p:NoWarn=CS0618 -p:DebugSymbols=false -p:DebugType=None
if($LASTEXITCODE -ne 0){throw 'Native bridge build failed'}
Copy-Item -LiteralPath (Join-Path $root 'audio-engine/obelisk-audio.exe') -Destination (Join-Path $PSScriptRoot 'resources/bridge/obelisk-audio.exe') -Force
cargo build --release --locked --manifest-path (Join-Path $PSScriptRoot 'src-tauri/Cargo.toml')
if($LASTEXITCODE -ne 0){throw 'Tauri build failed'}
$publish=Join-Path $PSScriptRoot 'publish'
New-Item -ItemType Directory -Path $publish -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src-tauri/target/release/obelisk.exe') -Destination (Join-Path $publish 'Obelisk.exe') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'resources/bridge') -Destination $publish -Recurse -Force
Write-Output 'Built tauri/publish/Obelisk.exe. Optional audio models, voices and credentials live in LocalAppData/Obelisk, outside this checkout.'
