<#
.SYNOPSIS
  Dong goi trinh duyet Chromium (thu muc trong %LOCALAPPDATA%\ms-playwright) thanh dola-browser-1243.zip.
.DESCRIPTION
  File nay dang len GitHub Release (tag browser-1243) lam nguon du phong: neu may nguoi dung khong tai duoc Chromium tu CDN cua
  Playwright (proxy/tuong lua/ISP), dola-gateway.exe se tai goi nay tu GitHub. Can may build da chay: dola-gateway.exe install-browser
  (hoac: python -m patchright install chromium).
.EXAMPLE
  .\scripts\pack-browser.ps1
#>
param(
    [string]$Revision = "1243",
    [string]$OutFile = "dist\dola-browser-$Revision.zip"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $env:LOCALAPPDATA "ms-playwright"
$zip = if ([IO.Path]::IsPathRooted($OutFile)) { $OutFile } else { Join-Path $root $OutFile }

$dirs = Get-ChildItem $cache -Directory | Where-Object { $_.Name -match "^(chromium-$Revision|chromium_headless_shell-$Revision|ffmpeg-\d+|winldd-\d+)$" }
if (-not ($dirs | Where-Object Name -eq "chromium-$Revision")) { throw "Chua co chromium-$Revision trong $cache. Chay dola-gateway.exe install-browser truoc." }

New-Item -ItemType Directory -Force (Split-Path $zip) | Out-Null
if (Test-Path $zip) { Remove-Item -Force $zip }
Write-Host "Dong goi: $($dirs.Name -join ', ') -> $zip (vai phut)" -ForegroundColor Cyan
Compress-Archive -Path ($dirs.FullName) -DestinationPath $zip -CompressionLevel Optimal
Write-Host ("Xong: {0} ({1} MB)" -f $zip, [math]::Round((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
Write-Host "Tiep theo: GitHub > Releases > Draft a new release > tag 'browser-$Revision' > keo file zip vao > Publish."
