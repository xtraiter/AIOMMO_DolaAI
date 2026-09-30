<#
.SYNOPSIS
  Dong goi dola-render-gateway (Python) thanh dola-gateway.exe - nguoi dung khong can cai Python.
.DESCRIPTION
  Can Python 3.10+ tren MAY BUILD (khong can tren may nguoi dung). Tao venv tam trong %TEMP%, cai thu vien + PyInstaller,
  dong goi thu muc onedir roi chep web/ va extensions/ canh file exe. Khong dong vao ma nguon.
  Ket qua: <OutDir>\dola-gateway.exe  (+ _internal, web, extensions). accounts/, downloads/, *.db tao ra canh exe khi chay.
.EXAMPLE
  .\scripts\build-gateway.ps1 -OutDir dist\gateway
#>
param(
    [string]$OutDir = "dist\gateway",
    [string]$Python = "py -3"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$src  = Join-Path $root "dola-render-gateway"
$out  = if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $root $OutDir }
$work = Join-Path $env:TEMP "dola-gateway-build"
$venv = Join-Path $work "venv"
$vpy  = Join-Path $venv "Scripts\python.exe"

New-Item -ItemType Directory -Force $work | Out-Null

Write-Host "[1/4] Chuan bi moi truong build..." -ForegroundColor Cyan
if (-not (Test-Path $vpy)) {
    $parts = $Python.Split(' ')
    & $parts[0] $parts[1..($parts.Length - 1)] -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw "Khong tao duoc venv (kiem tra Python)." }
}
& $vpy -m pip install -q --disable-pip-version-check -r (Join-Path $src "requirements.txt") pyinstaller
if ($LASTEXITCODE -ne 0) { throw "pip install that bai." }

Write-Host "[2/4] Dong goi bang PyInstaller (vai phut)..." -ForegroundColor Cyan
$dist = Join-Path $work "dist"
Push-Location $src
try {
    & $vpy -m PyInstaller gateway_main.py --name dola-gateway --onedir --noconfirm --clean --console `
        --distpath $dist --workpath (Join-Path $work "build") --specpath (Join-Path $work "spec") `
        --paths . --collect-all patchright --collect-submodules uvicorn `
        --hidden-import open_profile --hidden-import add_account_cookie --hidden-import fb_to_dola `
        --add-data "$src\protocol\js;protocol\js" --add-data "$src\bdcaptcha.js;." `
        --exclude-module tkinter
    if ($LASTEXITCODE -ne 0) { throw "PyInstaller that bai." }
} finally { Pop-Location }

Write-Host "[3/4] Chep vao $out ..." -ForegroundColor Cyan
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item (Join-Path $dist "dola-gateway\*") $out -Recurse -Force
Copy-Item (Join-Path $src "web") (Join-Path $out "web") -Recurse -Force
Copy-Item (Join-Path $src "extensions") (Join-Path $out "extensions") -Recurse -Force

Write-Host "[4/4] Xong: $out\dola-gateway.exe" -ForegroundColor Green
Write-Host "Chromium se duoc tai tu dong 1 lan tren may dau tien dung (app goi 'dola-gateway.exe install-browser')."
