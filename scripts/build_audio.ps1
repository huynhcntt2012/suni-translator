$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw '.NET Framework 4.x x64 compiler not found' }
& $taskCompiler /nologo /target:exe /platform:x64 /optimize+ '/out:native\windows_audio\SuniProcessLoopback.exe' 'native\windows_audio\ProcessLoopback.cs'
if ($LASTEXITCODE -ne 0) { throw 'Process loopback bridge build failed' }
