$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
& ./.venv/Scripts/python.exe -m PyInstaller --noconfirm --clean installer/Suni.spec
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
