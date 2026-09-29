# wall42 W2 安裝驗收：dist\wall42-small.exe 與 wall42.exe 各跑一輪「雙擊只詢問 → 安裝 → 在 WorkerW 底下跑 → 更新 → 從安裝目錄解除安裝」。
# 全部在沙盒：WALL42_HOME（取代 %LOCALAPPDATA%\wall42）、WALL42_DATA（取代 %APPDATA%\wall42）、WALL42_REG_ROOT（開機啟動與
# 「應用程式」清單的登錄機碼）。不碰使用者真的登錄檔、設定與安裝；不動任何視窗（只對本腳本自己開的行程的對話框送 WM_CLOSE），
# 其他視窗前後比對位置／大小。本腳本開的 wall42 最後一律依 pid 確認結束。系統桌布設定前後比對。
# 用法：powershell -ExecutionPolicy Bypass -File tests\w2_install_e2e.ps1   （先跑 windows\pack.ps1）
param([string]$Out = (Join-Path $PSScriptRoot 'w2_install_result.txt'), [int]$Warm = 25)
$ErrorActionPreference = 'Stop'
$dist = Join-Path $PSScriptRoot '..\dist'
$repoPresets = Join-Path $PSScriptRoot '..\..\presets'
$results = New-Object System.Collections.Generic.List[string]
$pass = 0; $fail = 0
function Check($name, $ok, $detail = '') {
  if ($ok) { $script:pass++; Write-Host "  PASS $name  $detail" -ForegroundColor Green } else { $script:fail++; Write-Host "  FAIL $name  $detail" -ForegroundColor Red }
  $results.Add(('{0} {1}  {2}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail))
}
function Note($s) { Write-Host "  $s" -ForegroundColor DarkGray; $results.Add("NOTE $s") }
function WaitUntil([scriptblock]$c, [int]$ms) { $u = (Get-Date).AddMilliseconds($ms); while ((Get-Date) -lt $u) { if (& $c) { return $true }; Start-Sleep -Milliseconds 150 }; [bool](& $c) }
function Run($exe, $argline) {
  # raw command line, stdout captured; WinExe, so wait for the process itself
  $psi = New-Object Diagnostics.ProcessStartInfo $exe, $argline
  $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
  $p = [Diagnostics.Process]::Start($psi)
  $o = $p.StandardOutput.ReadToEndAsync(); $e = $p.StandardError.ReadToEndAsync()
  if (-not $p.WaitForExit(60000)) { $p.Kill(); return @(-1, '', 'timeout') }
  @($p.ExitCode, $o.Result.Trim(), $e.Result.Trim())
}
Add-Type -Namespace W42 -Name Win -UsingNamespace System.Text, System.Collections.Generic -MemberDefinition @'
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfoW(uint a, uint p, StringBuilder s, uint f);
public struct RECT { public int L, T, R, B; }
public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassNameW(h, s, 256); return s.ToString(); }
public static string Text(IntPtr h) { var s = new StringBuilder(256); GetWindowTextW(h, s, 256); return s.ToString(); }
public static uint Pid(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
// visible top-level windows: "hwnd<TAB>pid<TAB>class<TAB>rect<TAB>iconic<TAB>title"
public static List<string> Top() {
  var l = new List<string>();
  EnumWindows((h, _) => { if (IsWindowVisible(h)) { RECT r; GetWindowRect(h, out r);
    l.Add(h.ToInt64() + "\t" + Pid(h) + "\t" + Cls(h) + "\t" + r.L + "," + r.T + "," + r.R + "," + r.B + "\t" + IsIconic(h) + "\t" + Text(h)); } return true; }, IntPtr.Zero);
  return l;
}
// wall42.surface windows anywhere under the desktop: "pid<TAB>parent class"
public static List<string> Surfaces() {
  var l = new List<string>();
  EnumWindows((top, _) => { EnumChildWindows(top, (h, __) => { if (Cls(h) == "wall42.surface") l.Add(Pid(h) + "\t" + Cls(GetParent(h)) + "\t" + Cls(top)); return true; }, IntPtr.Zero); return true; }, IntPtr.Zero);
  return l;
}
public static string Wallpaper() { var s = new StringBuilder(1024); SystemParametersInfoW(0x73, 1024, s, 0); return s.ToString(); }
'@
function TopMap { $m = @{}; foreach ($l in [W42.Win]::Top()) { $f = $l -split "`t"; $m[$f[0]] = @{ pid = [int]$f[1]; cls = $f[2]; rect = $f[3]; iconic = $f[4]; title = $f[5] } }; $m }
function Mine($work) { @(Get-Process wall42 -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($work, 'OrdinalIgnoreCase') }) }

# ── the real things this test must not touch ──
$realRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$realRun = (Get-ItemProperty $realRunKey -ErrorAction SilentlyContinue).wall42
$realUninstall = Test-Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\wall42'
$realData = Join-Path $env:APPDATA 'wall42'
function DataState { if (Test-Path $realData) { (Get-ChildItem $realData -Recurse -File -Force | ForEach-Object { "$($_.FullName)=$((Get-FileHash $_.FullName).Hash)" }) -join ';' } else { '(none)' } }
$realDataBefore = DataState
$realHome = Join-Path $env:LOCALAPPDATA 'wall42'
$realBinBefore = Test-Path "$realHome\bin"; $realPresetsBefore = Test-Path "$realHome\presets"
$wallBefore = [W42.Win]::Wallpaper()
$wallRegBefore = (Get-ItemProperty 'HKCU:\Control Panel\Desktop').WallPaper
$before = TopMap
$started = New-Object System.Collections.Generic.List[int]
$memory = @()

$regRoot = 'Software\wall42-e2e\CurrentVersion'
$reg = "HKCU:\$regRoot"
try {
foreach ($name in 'wall42-small.exe', 'wall42.exe') {
  Write-Host "--- $name ---" -ForegroundColor Cyan
  # LOCALAPPDATA\Temp spelled long: %TEMP% is often an 8.3 short path
  $work = Join-Path $env:LOCALAPPDATA ('Temp\w42-e2e-' + [IO.Path]::GetFileNameWithoutExtension($name))
  if (Test-Path $work) { Remove-Item -Recurse -Force $work }
  New-Item -ItemType Directory -Force "$work\download" | Out-Null
  $exe = Join-Path "$work\download" $name
  Copy-Item (Join-Path $dist $name) $exe
  $env:WALL42_HOME = "$work\home"; $env:WALL42_DATA = "$work\data"; $env:WALL42_REG_ROOT = $regRoot
  $bin = "$work\home\bin"; $installed = "$bin\wall42.exe"

  $v = Run $exe '--version'
  Check "$name --version" ($v[0] -eq 0 -and $v[1] -match '^\d+\.\d+\.\d+$') $v[1]
  $ver = $v[1]

  # ── double-click: only asks ──
  $p = Start-Process $exe -PassThru; $started.Add($p.Id)
  $asked = WaitUntil { @([W42.Win]::Top() | Where-Object { ($_ -split "`t")[1] -eq "$($p.Id)" }).Count -gt 0 } 20000
  $dlg = @([W42.Win]::Top() | Where-Object { ($_ -split "`t")[1] -eq "$($p.Id)" })
  $desc = ($dlg | ForEach-Object { $f = $_ -split "`t"; "$($f[2]) '$($f[5])'" }) -join ' | '
  Check "$name 雙擊：跳出詢問視窗（TaskDialog）" ($asked -and ($dlg | Where-Object { ($_ -split "`t")[2] -eq '#32770' -and ($_ -split "`t")[5] -eq 'wall42' })) $desc
  foreach ($l in $dlg) { [W42.Win]::PostMessageW([IntPtr][long](($l -split "`t")[0]), 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
  $closed = $p.WaitForExit(10000)
  Check "$name 雙擊後關掉：什麼都沒裝、沒有東西在跑" ($closed -and $p.ExitCode -eq 0 -and -not (Test-Path "$work\home") -and -not (Test-Path "$work\data") -and -not (Test-Path "$reg\Uninstall\wall42") -and -not (Get-ItemProperty "$reg\Run" -ErrorAction SilentlyContinue).wall42 -and (Mine $work).Count -eq 0 -and @([W42.Win]::Surfaces() | Where-Object { ($_ -split "`t")[0] -eq "$($p.Id)" }).Count -eq 0)

  # ── install ──
  $r = Run $exe '--install --quiet'
  Check "$name --install 結束碼 0" ($r[0] -eq 0) "exit=$($r[0]) $($r[1]) $($r[2])"
  $files = @(Get-ChildItem $bin -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object Name)
  Check "$name bin 只有 wall42.exe" (($files -join ',') -eq 'wall42.exe') ($files -join ',')
  Check "$name 安裝的 wall42.exe 就是下載的那個" ((Get-FileHash $installed).Hash -eq (Get-FileHash $exe).Hash)
  $want = @(Get-ChildItem $repoPresets -Filter *.json | Sort-Object Name)
  $got = @(Get-ChildItem "$work\home\presets" -Filter *.json -ErrorAction SilentlyContinue | Sort-Object Name)
  $same = ($want.Count -eq $got.Count) -and -not (Compare-Object ($want | ForEach-Object { "$($_.Name) $((Get-FileHash $_.FullName).Hash)" }) ($got | ForEach-Object { "$($_.Name) $((Get-FileHash $_.FullName).Hash)" }))
  Check "$name presets 全部放到 home\presets（與 repo 逐位元組相同）" $same "$($got.Count)/$($want.Count)"
  $cfg = "$work\data\config.json"
  $cfgOk = $false; try { $null = Get-Content $cfg -Raw | ConvertFrom-Json; $cfgOk = $true } catch { }
  Check "$name 設定檔不存在 → 建立預設值" ($cfgOk -and $r[1] -match 'config: created') $cfg
  Check "$name 開機啟動（測試機碼 Run）" ((Get-ItemProperty "$reg\Run" -ErrorAction SilentlyContinue).wall42 -eq "`"$installed`"")
  $u = Get-ItemProperty "$reg\Uninstall\wall42" -ErrorAction SilentlyContinue
  Check "$name 列在「應用程式」：名稱、版本、圖示、解除安裝指令" ($u -and $u.DisplayName -eq 'wall42' -and $u.DisplayVersion -eq $ver -and $u.DisplayIcon -eq $installed -and $u.UninstallString -eq "`"$installed`" --uninstall" -and $u.QuietUninstallString -eq "`"$installed`" --uninstall --quiet" -and $u.EstimatedSize -gt 0) "$($u.DisplayVersion) $($u.UninstallString) $($u.EstimatedSize) KB"
  $vi = (Get-Item $installed).VersionInfo
  Check "$name 檔案版本資訊" ($vi.ProductName -eq 'wall42' -and $vi.ProductVersion.Split('+')[0] -eq $ver -and $vi.FileDescription) "$($vi.ProductName) $($vi.ProductVersion.Split('+')[0]) '$($vi.FileDescription)'"

  # ── running from bin, under the WorkerW ──
  $up = WaitUntil { (Mine $work).Count -eq 1 -and @([W42.Win]::Surfaces() | Where-Object { ($_ -split "`t")[0] -eq "$((Mine $work)[0].Id)" }).Count -gt 0 } 20000
  $proc = @(Mine $work)[0]; if ($proc) { $started.Add($proc.Id) }
  $surf = @([W42.Win]::Surfaces() | Where-Object { $proc -and ($_ -split "`t")[0] -eq "$($proc.Id)" })
  Check "$name 裝好的 wall42 從 bin 跑起來、畫面掛在 WorkerW 底下" ($up -and $proc.Path -eq $installed -and $surf.Count -gt 0 -and -not ($surf | Where-Object { ($_ -split "`t")[1] -ne 'WorkerW' })) "pid=$($proc.Id) surfaces: $(($surf | ForEach-Object { ($_ -split "`t")[1..2] -join '<' }) -join ', ')"
  $log = "$work\home\wall42.log"
  Check "$name log 在沙盒 home、讀的是沙盒設定檔" ((Test-Path $log) -and (Select-String -Path $log -SimpleMatch "start pid=$($proc.Id)" -Quiet) -and (Select-String -Path $log -SimpleMatch "config=$cfg" -Quiet))
  Start-Sleep $Warm
  $proc.Refresh()
  $state = (Select-String -Path $log -SimpleMatch -Pattern 'fps=[' | Select-Object -Last 1).Line
  $memory += [pscustomobject]@{ exe = $name; size = (Get-Item $exe).Length / 1MB; ws = $proc.WorkingSet64 / 1MB; priv = $proc.PrivateMemorySize64 / 1MB; state = $state }
  Note ("{0} 記憶體（啟動 {1} 秒後）：工作集 {2:N1} MB、private {3:N1} MB；{4}" -f $name, $Warm, ($proc.WorkingSet64 / 1MB), ($proc.PrivateMemorySize64 / 1MB), $state)

  # ── double-click again, same version installed: says so, does not install twice ──
  $p = Start-Process $exe -PassThru; $started.Add($p.Id)
  $asked = WaitUntil { @([W42.Win]::Top() | Where-Object { ($_ -split "`t")[1] -eq "$($p.Id)" }).Count -gt 0 } 20000
  foreach ($l in @([W42.Win]::Top() | Where-Object { ($_ -split "`t")[1] -eq "$($p.Id)" })) { [W42.Win]::PostMessageW([IntPtr][long](($l -split "`t")[0]), 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
  $closed = $p.WaitForExit(10000)
  Check "$name 已裝同版本再雙擊：只顯示「已經安裝好了」、原本那個繼續跑" ($asked -and $closed -and (Mine $work).Count -eq 1 -and (Mine $work)[0].Id -eq $proc.Id)

  # ── update: pretend an older version is installed; the user's config and own presets stay ──
  Set-ItemProperty "$reg\Uninstall\wall42" DisplayVersion '0.0.1'
  $userCfg = '{ "motion": { "particleCount": 123 } }'
  [IO.File]::WriteAllText($cfg, $userCfg)
  Set-Content "$work\home\presets\mine.json" '{}' -Encoding ascii
  $r = Run $exe '--install --quiet'
  $newProc = $null
  $restarted = WaitUntil { $m = @(Mine $work); $m.Count -eq 1 -and $m[0].Id -ne $proc.Id } 15000
  $newProc = @(Mine $work)[0]; if ($newProc) { $started.Add($newProc.Id) }
  Check "$name 更新：結束碼 0、舊的停掉、新的從 bin 重新啟動" ($r[0] -eq 0 -and $r[1] -match "updated 0\.0\.1 -> $([regex]::Escape($ver))" -and $proc.HasExited -and $restarted -and $newProc.Path -eq $installed) "$($r[1] -replace ';.*', '') old=$($proc.Id) new=$($newProc.Id)"
  Check "$name 更新：版本號回到 $ver、設定檔與使用者自己的 preset 都沒被動" ((Get-ItemProperty "$reg\Uninstall\wall42").DisplayVersion -eq $ver -and [IO.File]::ReadAllText($cfg) -eq $userCfg -and (Test-Path "$work\home\presets\mine.json") -and $r[1] -match 'config: kept')
  $pid2 = $newProc.Id
  Check "$name 更新後畫面重新掛上 WorkerW" (WaitUntil { @([W42.Win]::Surfaces() | Where-Object { ($_ -split "`t")[0] -eq "$pid2" }).Count -gt 0 } 15000)

  # ── uninstall the way Settings → Apps does it: the installed exe, which deletes its own folder after exiting ──
  $r = Run $installed '--uninstall --quiet'
  Check "$name --uninstall（從安裝目錄）結束碼 0" ($r[0] -eq 0) "$($r[1]) $($r[2])"
  Check "$name 解除安裝：wall42 停了、畫面從 WorkerW 拿掉" ((WaitUntil { $newProc.HasExited } 5000) -and (Mine $work).Count -eq 0 -and @([W42.Win]::Surfaces() | Where-Object { ($_ -split "`t")[0] -eq "$pid2" }).Count -eq 0)
  Check "$name 解除安裝：bin 刪掉（exe 結束後由隱藏 cmd 重試刪除）" (WaitUntil { -not (Test-Path $bin) } 20000)
  Check "$name 解除安裝：開機啟動與「應用程式」項目都拿掉" (-not (Get-ItemProperty "$reg\Run" -ErrorAction SilentlyContinue).wall42 -and -not (Test-Path "$reg\Uninstall\wall42"))
  $left = @(Get-ChildItem "$work\home" -Force | ForEach-Object Name)
  Check "$name 解除安裝：log 刪掉；只留 presets 與設定檔（跟 Mac 一樣保留）" ((($left -join ',') -eq 'presets') -and [IO.File]::ReadAllText($cfg) -eq $userCfg -and (Test-Path "$work\home\presets\mine.json")) "home: $($left -join ',')"

  foreach ($v in 'WALL42_HOME', 'WALL42_DATA', 'WALL42_REG_ROOT') { Remove-Item "Env:$v" }
  Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
}
finally {
  # everything this script started, by pid
  foreach ($id in $started) { $q = Get-Process -Id $id -ErrorAction SilentlyContinue; if ($q -and $q.ProcessName -eq 'wall42') { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue; Note "stopped leftover pid $id" } }
  Remove-Item -Recurse -Force 'HKCU:\Software\wall42-e2e' -ErrorAction SilentlyContinue
}

$alive = @($started | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue | Where-Object ProcessName -eq 'wall42' })
Check '本腳本開的行程全部結束（依 pid）' ($alive.Count -eq 0) "$($started.Count) 個 pid"
Check '使用者真的開機啟動、「應用程式」項目沒被動到' (((Get-ItemProperty $realRunKey -ErrorAction SilentlyContinue).wall42 -eq $realRun) -and ((Test-Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\wall42') -eq $realUninstall))
Check '使用者真的 %APPDATA%\wall42 沒被動到' ((DataState) -eq $realDataBefore) $realDataBefore
Check '使用者真的 %LOCALAPPDATA%\wall42 沒有多出 bin／presets' (((Test-Path "$realHome\bin") -eq $realBinBefore) -and ((Test-Path "$realHome\presets") -eq $realPresetsBefore))
Check '系統桌布設定沒變' (([W42.Win]::Wallpaper() -eq $wallBefore) -and ((Get-ItemProperty 'HKCU:\Control Panel\Desktop').WallPaper -eq $wallRegBefore)) "'$wallBefore'"
$after = TopMap
$moved = foreach ($k in $before.Keys) { if ($after.ContainsKey($k) -and ($after[$k].rect -ne $before[$k].rect -or $after[$k].iconic -ne $before[$k].iconic)) { "$($before[$k].cls) '$($before[$k].title)' $($before[$k].rect) -> $($after[$k].rect)" } }
Check ('其他視窗位置／大小／最小化不變（{0} 個）' -f $before.Count) (@($moved).Count -eq 0) ($moved -join '; ')
foreach ($m in $memory) { $results.Add(('MEM {0,-18} {1,6:N1} MB file  WS {2,6:N1} MB  private {3,6:N1} MB' -f $m.exe, $m.size, $m.ws, $m.priv)) }
$results.Add("TOTAL pass=$pass fail=$fail")
$results | Out-File $Out -Encoding utf8
Write-Host "結果：$pass 通過、$fail 失敗" -ForegroundColor Cyan
if ($fail -gt 0) { exit 1 }
