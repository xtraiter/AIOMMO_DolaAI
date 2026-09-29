<#
.SYNOPSIS
  Dong goi DolaCoordinator thanh ban chay doc lap (self-contained, single-file, win-x64).
.EXAMPLE
  .\scripts\build-release.ps1 -Version 1.0.0
#>
param(
    [string]$Version = "1.0.0",
    [string]$OutputDir = "dist",
    [switch]$SkipGateway,
    [switch]$Zip            # them -Zip neu can file zip de gui di; mac dinh chi tao thu muc
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "DolaCoordinator\DolaCoordinator.csproj"
$distDir = Join-Path $root "$OutputDir\DolaCoordinator_v$Version"
$zipPath = Join-Path $root "$OutputDir\DolaCoordinator_v${Version}_Portable.zip"
$temp = Join-Path $env:TEMP ("DolaCoord_" + [Guid]::NewGuid().ToString("N"))

Write-Host "[1/3] Bien dich v$Version (self-contained, win-x64)..." -ForegroundColor Cyan
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:Version=$Version -o $temp
if ($LASTEXITCODE -ne 0) { throw "dotnet publish that bai (ma $LASTEXITCODE)" }
Get-ChildItem $temp -Filter *.pdb | Remove-Item -Force

Write-Host "[2/3] Tao thu muc phat hanh (app + gateway)..." -ForegroundColor Cyan
if (Test-Path $distDir) { Remove-Item -Recurse -Force $distDir }
New-Item -ItemType Directory -Force $distDir | Out-Null
Copy-Item "$temp\*" $distDir -Recurse -Force
Remove-Item -Recurse -Force $temp
if ($SkipGateway) {
    Write-Host "  (bo qua gateway: goi nay se can Python + dola-render-gateway rieng)" -ForegroundColor Yellow
} else {
    & (Join-Path $PSScriptRoot "build-gateway.ps1") -OutDir (Join-Path $distDir "gateway")
}

$guide = @"
DOLA COORDINATOR v$Version
--------------------------
1. Chay DolaCoordinator.exe (Windows 10/11 x64, khong can cai .NET).
2. Gateway nam san trong thu muc 'gateway' (dola-gateway.exe): app tu chay ngam, KHONG can cai Python.
   Lan dau dung co the mat vai phut de tai trinh duyet Chromium. Tab 'Cai dat': quota/ngay, thu muc luu video.
3. Tab 'Tai khoan Dola': them tai khoan (dang nhap thu cong / Google / Facebook / cookie).
4. Tab 'Hang doi render': nhap prompt, thoi luong, ti le, anh tham chieu -> Bat dau.
"@
[System.IO.File]::WriteAllText((Join-Path $distDir "HUONG_DAN.txt"), $guide, [System.Text.UTF8Encoding]::new($false))

if ($Zip) {
    Write-Host "[3/3] Nen ZIP..." -ForegroundColor Cyan
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    # Windows Defender co the quet file exe moi tao va khoa no vai giay -> thu lai
    for ($i = 1; $i -le 5; $i++) {
        try { Compress-Archive -Path "$distDir\*" -DestinationPath $zipPath -Force -ErrorAction Stop; break }
        catch { if ($i -eq 5) { throw }; Start-Sleep -Seconds 3 }
    }
    Write-Host "Xong: $zipPath" -ForegroundColor Green
} else {
    Write-Host "Xong: $distDir\DolaCoordinator.exe" -ForegroundColor Green
}
