<#
.SYNOPSIS
  Tao bo cai AIOMMO_DolaAI_Setup_<version>.exe (1 file: cai app + gateway + ffmpeg, tao shortcut, hien dieu khoan su dung).
.DESCRIPTION
  1) build-release.ps1 build app + gateway + ffmpeg vao thu muc tam dist\AIOMMO_DolaAI_v<version> (KHONG dung toi thu muc release,
     nen build duoc ca khi ban dang chay app tu release).
  2) Inno Setup (ISCC.exe) dong thanh dist\installer\AIOMMO_DolaAI_Setup_<version>.exe.
  Chromium (~150 MB) khong nhung vao bo cai: bo cai tai no o buoc cuoi (tuy chon), hoac app de nghi tai o lan chay dau.
  Can Inno Setup 6:  winget install JRSoftware.InnoSetup --scope user
.EXAMPLE
  .\scripts\build-installer.ps1 -Version 1.0.0
  .\scripts\build-installer.ps1 -Version 1.0.0 -SkipBuild     # dong goi lai tu thu muc dist da build san
#>
param(
    [string]$Version = "1.0.0",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root "dist\AIOMMO_DolaAI_v$Version"

$iscc = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Khong tim thay Inno Setup 6 (ISCC.exe). Cai bang: winget install JRSoftware.InnoSetup --scope user" }

if (-not $SkipBuild) {
    Write-Host "[1/2] Build app + gateway + ffmpeg vao $stage ..." -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "build-release.ps1") -Version $Version
    if ($LASTEXITCODE -ne 0) { throw "build-release that bai." }
}
if (-not (Test-Path (Join-Path $stage "AIOMMO DolaAI.exe"))) { throw "Thieu $stage\AIOMMO DolaAI.exe - chay lai khong co -SkipBuild." }

Write-Host "[2/2] Dong goi bo cai bang Inno Setup (vai phut)..." -ForegroundColor Cyan
$iss = Join-Path $root "installer\AIOMMO_DolaAI.iss"
& $iscc "/DAppVersion=$Version" "/DSourceDir=$stage" "/Q" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC that bai (ma $LASTEXITCODE)." }

$out = Join-Path $root "dist\installer\AIOMMO_DolaAI_Setup_$Version.exe"
Write-Host ("Xong: {0}  ({1:N0} MB)" -f $out, ((Get-Item $out).Length / 1MB)) -ForegroundColor Green
