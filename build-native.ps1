param([string]$Zig='zig')
$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
 New-Item -ItemType Directory -Force native/output | Out-Null
 & $Zig cc -target x86_64-windows-gnu -shared -O2 -Wall -Wextra -Werror -Wno-unused-parameter -o native/output/TrialBridge.dll native/bridge.c native/vendor/minhook/src/buffer.c native/vendor/minhook/src/hook.c native/vendor/minhook/src/trampoline.c native/vendor/minhook/src/hde/hde64.c -luser32 -lkernel32
 if($LASTEXITCODE -ne 0){throw 'Native bridge build failed'}
} finally {Pop-Location}
