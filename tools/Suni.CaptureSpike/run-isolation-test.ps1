#requires -Version 5.1
<#
.SYNOPSIS
  Kiểm thử tự động spike P0 (tương tự TC-01): hai tiến trình phát hai tần số khác nhau,
  thu theo PID từng tiến trình và kiểm tra tín hiệu của tiến trình kia không lọt vào.
  CẢNH BÁO: phát tiếng bíp nhỏ (-20 dBFS) qua loa khoảng 10 giây.
#>
param(
    [string] $OutDir = (Join-Path $env:TEMP 'suni-capture-spike'),
    [double] $Seconds = 4,
    # PID gốc của ứng dụng khác đang phát (vd. Chrome) — thu thêm để tách nhiễu nền khỏi kết quả.
    [int[]] $BackgroundPids = @()
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'bin/Debug/net10.0-windows/Suni.CaptureSpike.exe'
New-Item -ItemType Directory -Force $OutDir | Out-Null

& $exe tone --freq 440 --seconds 10 --amp 0.1 --out (Join-Path $OutDir 'tone_a_440.wav') | Out-Null
& $exe tone --freq 1000 --seconds 10 --amp 0.1 --out (Join-Path $OutDir 'tone_b_1000.wav') | Out-Null

function Start-Player($wav) {
    $cmd = "(New-Object Media.SoundPlayer '$wav').PlaySync()"
    Start-Process powershell.exe -ArgumentList '-NoProfile', '-Command', $cmd -WindowStyle Hidden -PassThru
}

$a = Start-Player (Join-Path $OutDir 'tone_a_440.wav')
$b = Start-Player (Join-Path $OutDir 'tone_b_1000.wav')
try {
    Start-Sleep -Milliseconds 1500 # chờ audio session được tạo
    Write-Host "Player A (440 Hz) PID $($a.Id), Player B (1000 Hz) PID $($b.Id)"
    & $exe list

    $jobs = @(
        @{ Name = 'A_include'; Args = @('--pid', $a.Id) },
        @{ Name = 'B_include'; Args = @('--pid', $b.Id) },
        @{ Name = 'A_exclude'; Args = @('--pid', $a.Id, '--exclude') },
        @{ Name = 'system';    Args = @('--system') },
        @{ Name = 'endpoint';  Args = @('--endpoint') }
    )
    foreach ($bg in $BackgroundPids) { $jobs += @{ Name = "background_$bg"; Args = @('--pid', $bg) } }
    # Thu song song để cả 5 chế độ nghe cùng một đoạn âm thanh.
    $procs = foreach ($j in $jobs) {
        $wav = Join-Path $OutDir "$($j.Name).wav"
        $log = Join-Path $OutDir "$($j.Name).json"
        $argList = @('capture') + $j.Args + @('--seconds', $Seconds, '--out', $wav)
        Start-Process $exe -ArgumentList $argList -RedirectStandardOutput $log -NoNewWindow -PassThru
    }
    $procs | Wait-Process -Timeout ([int]$Seconds + 20)
}
finally {
    foreach ($p in $a, $b) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
}

foreach ($j in $jobs) {
    Write-Host "`n=== $($j.Name) ==="
    Get-Content (Join-Path $OutDir "$($j.Name).json") -Raw
    & $exe analyze (Join-Path $OutDir "$($j.Name).wav") --freqs 440,1000
}
