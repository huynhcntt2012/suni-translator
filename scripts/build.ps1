$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
& "$PSScriptRoot/build_audio.ps1"
& ./.venv/Scripts/python.exe -m PyInstaller --noconfirm --clean installer/Suni.spec
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
