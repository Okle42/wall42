# 打包成單一 exe，放在 windows\dist\（不進 git）
#   wall42.exe         內含 .NET runtime，下載就能用
#   wall42-small.exe   需要先裝 .NET 8 Runtime（沒裝時 Windows 會跳出官方下載連結）
# 兩個都內含 presets\*.json；雙擊會先問要不要安裝（見 Wall42.Win\Package.cs）。
# 不壓縮（EnableCompressionInSingleFile）：壓縮版啟動時把整個 runtime 解壓到記憶體，常駐程式划不來（ShoWork42 W4 量過）。
# 需要：.NET 8 SDK。用法：powershell -ExecutionPolicy Bypass -File pack.ps1 [-Version 0.2.0] [-Compressed]
param([string]$Version = '', [switch]$Compressed)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'Wall42.Win\Wall42.Win.csproj'
$work = Join-Path $root 'Wall42.Win\obj\pack'
$dist = Join-Path $root 'dist'
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $work, $dist | Out-Null
$extra = @(); if ($Version) { $extra += "-p:Version=$Version" }
if ($Compressed) { $extra += '-p:EnableCompressionInSingleFile=true' }

$variants = @(
  @{ name = 'wall42.exe'; selfContained = 'true' },
  @{ name = 'wall42-small.exe'; selfContained = 'false' }
)
if ($Compressed) { $variants = @(@{ name = 'wall42-compressed.exe'; selfContained = 'true' }) }
$i = 0
foreach ($v in $variants) {
  $i++
  Write-Host "$i/$($variants.Count) $($v.name)" -ForegroundColor Cyan
  $out = Join-Path $work $v.name
  & dotnet.exe publish $proj -c Release -r win-x64 --self-contained $v.selfContained -nologo -v q `
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:Wall42Pack=true -o $out @extra
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish $($v.name) failed ($LASTEXITCODE)" }
  $exe = @(Get-ChildItem $out -File)
  if ($exe.Count -ne 1) { throw "expected exactly one file in $out, got: $($exe.Name -join ', ')" }
  Copy-Item $exe[0].FullName (Join-Path $dist $v.name) -Force
}
# the next normal build must not reuse the packed (preset-embedding) compile
Remove-Item -Recurse -Force (Join-Path $root 'Wall42.Win\obj\Release') -ErrorAction SilentlyContinue

$sums = foreach ($v in $variants) {
  $f = Get-Item (Join-Path $dist $v.name)
  $h = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  Write-Host ('  {0,-22} {1,7:N1} MB  {2}  版本 {3}' -f $f.Name, ($f.Length / 1MB), $h, $f.VersionInfo.ProductVersion.Split('+')[0])
  "$h  $($f.Name)"
}
if (-not $Compressed) { $sums | Out-File (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii }
Write-Host "完成：$dist" -ForegroundColor Green
