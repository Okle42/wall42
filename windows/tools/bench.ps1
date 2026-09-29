# wall42 實機量測：啟動 → 暖機 → 量 CPU／記憶體 → 看視窗階層 → 確認依 pid 結束
# powershell -File tools\bench.ps1 -Label paused   [-ForceDraw] [-NoDraw] [-Seconds 20] [-Warmup 8] [-Config path]
param(
    [string]$Label = "run",
    [switch]$ForceDraw,
    [switch]$NoDraw,
    [int]$Seconds = 20,
    [int]$Warmup = 8,
    [string]$Config = "",
    [string]$Capture = "",
    [int]$Report = 10
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "Wall42.Win\bin\Release\net8.0-windows\wall42.exe"
$inspect = Join-Path $root "tools\Inspect\bin\Release\net8.0-windows\Inspect.exe"

$env:WALL42_DURATION = [string]($Warmup + $Seconds + 6)
$env:WALL42_REPORT = [string]$Report
$env:WALL42_FORCE_DRAW = $(if ($ForceDraw) { "1" } else { "" })
$env:WALL42_NO_DRAW = $(if ($NoDraw) { "1" } else { "" })
$env:WALL42_CONFIG = $Config

$p = Start-Process -FilePath $exe -PassThru
try {
    Start-Sleep -Seconds $Warmup
    $p.Refresh()
    $c0 = $p.TotalProcessorTime.TotalSeconds
    $t0 = Get-Date
    Start-Sleep -Seconds $Seconds
    $p.Refresh()
    $c1 = $p.TotalProcessorTime.TotalSeconds
    $dt = ((Get-Date) - $t0).TotalSeconds
    $cores = [Environment]::ProcessorCount
    $one = ($c1 - $c0) / $dt * 100
    "{0}: cpu {1:N3}% of one core = {2:N4}% whole machine ({3} logical)  workingSet {4:N1} MB  private {5:N1} MB  over {6:N1}s" -f `
        $Label, $one, ($one / $cores), $cores, ($p.WorkingSet64 / 1MB), ($p.PrivateMemorySize64 / 1MB), $dt
    if ($Capture) { & $inspect $Capture } else { & $inspect }
}
finally {
    if (-not $p.WaitForExit(15000)) { Stop-Process -Id $p.Id -Force; "stopped pid $($p.Id)" } else { "pid $($p.Id) exited by itself (code $($p.ExitCode))" }
    Remove-Item Env:WALL42_DURATION, Env:WALL42_REPORT, Env:WALL42_FORCE_DRAW, Env:WALL42_NO_DRAW, Env:WALL42_CONFIG -ErrorAction SilentlyContinue
}
