param([string]$DotNet='dotnet')
$ErrorActionPreference='Stop'
& $DotNet publish (Join-Path $PSScriptRoot 'desktop/TrialServer.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $PSScriptRoot 'artifacts/server')
if($LASTEXITCODE -ne 0){throw 'Server build failed'}
Write-Output 'Server build completed; copy the complete components directory beside the EXE.'
