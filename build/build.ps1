#requires -Version 5.1
<#
.SYNOPSIS
  Build Suni AI Live Translator: .NET → test → publish (thư mục chạy được, không cần cài .NET).
.EXAMPLE
  ./build/build.ps1 -Publish
  ./build/build.ps1 -Publish -SkipTests -Models base-q5_1,large-v3-turbo-q5_0
.NOTES
  Kết quả: artifacts/publish/SuniTranslator/Suni.App.exe
    workers/whisper/      ASR worker (process riêng)
    models/vad/           Silero VAD
    models/asr/whisper.cpp/  model whisper được chọn (-Models), lấy từ models/asr/whisper.cpp của repo
#>
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [switch] $SkipTests,
    [switch] $Publish,
    # Model whisper đóng gói kèm (tên không gồm "ggml-"/".bin"); mặc định: GPU (turbo) + CPU (base) + máy yếu (tiny).
    [string[]] $Models = @('large-v3-turbo-q5_0', 'base-q5_1', 'tiny-q5_1')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

dotnet build (Join-Path $root 'SuniTranslator.slnx') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Build thất bại.' }
if (-not $SkipTests) {
    dotnet test (Join-Path $root 'SuniTranslator.slnx') -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Test thất bại.' }
}

if ($Publish) {
    $out = Join-Path $root 'artifacts/publish/SuniTranslator'
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }

    # Self-contained: máy đích không cần cài .NET.
    dotnet publish (Join-Path $root 'src/Suni.App') -c $Configuration -r win-x64 --self-contained -o $out -p:DebugType=None
    if ($LASTEXITCODE -ne 0) { throw 'Publish app thất bại.' }
    dotnet publish (Join-Path $root 'src/Suni.AsrWorker') -c $Configuration -r win-x64 --self-contained -o (Join-Path $out 'workers/whisper') -p:DebugType=None
    if ($LASTEXITCODE -ne 0) { throw 'Publish ASR worker thất bại.' }

    # Cắt bớt: runtime whisper chỉ cần trong worker, và chỉ bản Windows x64 (CPU + Vulkan).
    # (Whisper.net chép runtime cho mọi OS, và tham chiếu worker từ app làm chúng lọt cả vào thư mục app.)
    $appRuntimes = Join-Path $out 'runtimes'
    if (Test-Path $appRuntimes) {
        $other = Get-ChildItem $appRuntimes -Recurse -File | Where-Object { $_.Name -notmatch 'whisper' }
        if ($other) { Write-Warning "runtimes/ của app có file không phải whisper, giữ lại: $($other.Name -join ', ')" }
        else { Remove-Item -Recurse -Force $appRuntimes }
    }
    $workerRuntimes = Join-Path $out 'workers/whisper/runtimes'
    Get-ChildItem $workerRuntimes -Directory | Where-Object { $_.Name -notin 'win-x64', 'vulkan' } | Remove-Item -Recurse -Force
    Get-ChildItem (Join-Path $workerRuntimes 'vulkan') -Directory | Where-Object { $_.Name -ne 'win-x64' } | Remove-Item -Recurse -Force

    $modelOut = Join-Path $out 'models/asr/whisper.cpp'
    New-Item -ItemType Directory -Force $modelOut | Out-Null
    foreach ($m in $Models) {
        $src = Join-Path $root "models/asr/whisper.cpp/ggml-$m.bin"
        if (Test-Path $src) { Copy-Item $src $modelOut } else { Write-Warning "Thiếu model $src — bỏ qua." }
    }

    $size = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("Published to {0} ({1:N0} MB)" -f $out, $size)
    # TODO(P1): vpk pack (Velopack) để tạo installer.
}
