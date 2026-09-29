<#
.SYNOPSIS
  Tao goi ban giao SACH (zip) tu du an: khong co phien dang nhap, cookie, co so du lieu, file build, anh debug.
.DESCRIPTION
  Khong xoa/sua bat cu thu gi trong du an: chi sao chep co loc vao handover\ roi nen zip, sau do quet tim dau vet bi mat.
.EXAMPLE
  .\scripts\export-handover.ps1
#>
param([string]$Name = "DolaAI_handover")

$ErrorActionPreference = "Stop"
$root  = Split-Path -Parent $PSScriptRoot
$out   = Join-Path $root "handover"
$stage = Join-Path $out $Name
$zip   = Join-Path $out "$Name.zip"

$excludeDirs = @('bin','obj','dist','publish','updates','accounts','downloads','__pycache__','.git','.vs','.venv',
                 'scratch','.design_backup','handover')
$excludeFiles = @('*.db','*.sqlite','*.db-journal','*.db-wal','*.db-shm','*.png','*.jpg','*.log','*.pdb','*_wpftmp.csproj',
                  'cookies.txt','fb_cookies.txt','.env.local','version.json',
                  # file cu da duoc thay bang scripts\ va README.md
                  'README_COORDINATOR.md','CHAY_GATEWAY.bat','THEM_COOKIE.bat','Dang_Nhap_Dola_Browser.bat',
                  'DANG_NHAP_BANG_COOKIE_FACEBOOK.bat','pack_release.ps1','pack_update.ps1','start_server.ps1',
                  'login_dola_interactive.py')

if (-not (Test-Path $out)) { New-Item -ItemType Directory $out | Out-Null }
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }

Write-Host "[1/3] Sao chep co loc..." -ForegroundColor Cyan
# /XD loai thu muc theo ten o moi cap; extension/dola30 (khong phai phien dang nhap) van duoc giu.
$rcArgs = @($root, $stage, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/XD') + $excludeDirs + @('/XF') + $excludeFiles
& robocopy @rcArgs | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy loi (ma $LASTEXITCODE)" }

Write-Host "[2/3] Quet dau vet phien dang nhap / mat khau..." -ForegroundColor Cyan
$patterns = 'sessionid\s*=\s*[0-9a-f]{16,}', 'c_user\s*=\s*\d{6,}', '\bxs\s*=\s*[0-9A-Za-z%]{20,}', 'passwd\s*[:=]\s*"[^"]+"'
$hits = Get-ChildItem $stage -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Length -lt 5MB } |
    Select-String -Pattern $patterns -ErrorAction SilentlyContinue
if ($hits) {
    $hits | Select-Object -First 20 | ForEach-Object { Write-Host ("  " + $_.Path + ":" + $_.LineNumber) -ForegroundColor Yellow }
    throw "Phat hien chuoi giong cookie/mat khau trong goi ban giao. Kiem tra cac file tren truoc khi gui."
}
$db = Get-ChildItem $stage -Recurse -File -Include *.db,*.sqlite -ErrorAction SilentlyContinue
if ($db) { throw "Con file co so du lieu trong goi: $($db.FullName -join ', ')" }

Write-Host "[3/3] Nen zip..." -ForegroundColor Cyan
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path "$stage\*" -DestinationPath $zip -Force

$count = (Get-ChildItem $stage -Recurse -File).Count
Write-Host "Xong: $zip ($count file, $([math]::Round((Get-Item $zip).Length/1MB,1)) MB)" -ForegroundColor Green
