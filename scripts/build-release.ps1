<#
.SYNOPSIS
  Dong goi AIOMMO DolaAI thanh ban chay doc lap (self-contained, single-file, win-x64).
.EXAMPLE
  .\scripts\build-release.ps1 -Version 1.0.0
#>
param(
    [string]$Version = "1.0.0",
    [string]$OutputDir = "dist",
    [switch]$SkipGateway,
    [switch]$Zip,           # them -Zip neu can file zip de gui di
    [switch]$ToRelease      # xuat vao release\AIOMMO_DolaAI (thu muc duoc commit len git: clone ve la chay duoc)
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "DolaCoordinator\DolaCoordinator.csproj"
$distDir = if ($ToRelease) { Join-Path $root "release\AIOMMO_DolaAI" } else { Join-Path $root "$OutputDir\AIOMMO_DolaAI_v$Version" }
$zipPath = Join-Path $root "$OutputDir\AIOMMO_DolaAI_v${Version}_Portable.zip"
$temp = Join-Path $env:TEMP ("DolaCoord_" + [Guid]::NewGuid().ToString("N"))

# Dung neu app / gateway dang chay tu chinh thu muc dich: xoa do dang se lam hong ban dang chay (file bi khoa, xoa duoc mot phan)
$running = Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith($distDir, [StringComparison]::OrdinalIgnoreCase)
}
if ($running) {
    $names = ($running | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ", "
    throw "Dang co chuong trinh chay tu '$distDir': $names. Hay dong AIOMMO DolaAI (va gateway) roi build lai - build luc nay se xoa do thu muc release."
}

Write-Host "[1/3] Bien dich v$Version (self-contained, win-x64)..." -ForegroundColor Cyan
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:Version=$Version -o $temp
if ($LASTEXITCODE -ne 0) { throw "dotnet publish that bai (ma $LASTEXITCODE)" }
Get-ChildItem $temp -Filter *.pdb | Remove-Item -Force

Write-Host "[2/3] Tao thu muc phat hanh (app + gateway)..." -ForegroundColor Cyan
# Ban chay truc tiep tu thu muc release luu du lieu NGUOI DUNG canh gateway (ho so dang nhap cua tung tai khoan, co so du lieu, video).
# Build khong duoc xoa chung: cat ra cho an toan roi tra lai sau khi dong goi xong.
$keepItems = @("gateway\accounts", "gateway\downloads", "gateway\refs", "gateway\tasks.db", "gateway\pool_usage.db")
$stash = Join-Path $env:TEMP ("dola_keep_" + [Guid]::NewGuid().ToString("N"))
$stashed = @()
if ($ToRelease -and (Test-Path $distDir)) {
    foreach ($rel in $keepItems) {
        $src = Join-Path $distDir $rel
        if (Test-Path $src) {
            $dst = Join-Path $stash $rel
            New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
            Move-Item $src $dst -Force
            $stashed += $rel
        }
    }
    if ($stashed.Count -gt 0) { Write-Host "  giu lai du lieu nguoi dung: $($stashed -join ', ')" -ForegroundColor Yellow }
}
try {
    if (Test-Path $distDir) { Remove-Item -Recurse -Force $distDir }
    New-Item -ItemType Directory -Force $distDir | Out-Null
    Copy-Item "$temp\*" $distDir -Recurse -Force
    Remove-Item -Recurse -Force $temp
    Rename-Item (Join-Path $distDir "DolaCoordinator.exe") "AIOMMO DolaAI.exe"
    if ($SkipGateway) {
        Write-Host "  (bo qua gateway: goi nay se can Python + dola-render-gateway rieng)" -ForegroundColor Yellow
    } else {
        & (Join-Path $PSScriptRoot "build-gateway.ps1") -OutDir (Join-Path $distDir "gateway")
    }
}
finally {
    # Tra lai du lieu nguoi dung da cat o tren (neu co) - chay ca khi build o tren bi loi
    foreach ($rel in $stashed) {
        $src = Join-Path $stash $rel
        if (-not (Test-Path $src)) { continue }
        $dst = Join-Path $distDir $rel
        New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
        if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
        Move-Item $src $dst -Force
    }
    if (Test-Path $stash) { Remove-Item -Recurse -Force $stash -ErrorAction SilentlyContinue }
}

# ffmpeg: lay khung hinh cuoi va ghep video cua trang "Kich ban lon". Lay ban build san trong goi imageio-ffmpeg (pip)
# bang chinh venv cua buoc dong goi gateway, chep thanh tools\ffmpeg.exe canh app (khong can cai gi tren may nguoi dung).
$vpy = Join-Path $env:TEMP "dola-gateway-build\venv\Scripts\python.exe"
$ffDest = Join-Path $distDir "tools\ffmpeg.exe"
if (Test-Path $vpy) {
    & $vpy -m pip install -q --disable-pip-version-check imageio-ffmpeg
    $ffSrc = (& $vpy -c "import imageio_ffmpeg; print(imageio_ffmpeg.get_ffmpeg_exe())" | Select-Object -Last 1)
    if ($ffSrc -and (Test-Path $ffSrc)) {
        New-Item -ItemType Directory -Force (Split-Path $ffDest) | Out-Null
        Copy-Item $ffSrc $ffDest -Force
        Write-Host "  ffmpeg: $ffDest" -ForegroundColor Green
    } else { Write-Host "  (khong lay duoc ffmpeg tu imageio-ffmpeg)" -ForegroundColor Yellow }
} else { Write-Host "  (bo qua ffmpeg: chua co venv cua buoc gateway)" -ForegroundColor Yellow }

$guide = @"
AIOMMO DOLAAI v$Version
-----------------------
1. Chay 'AIOMMO DolaAI.exe' (Windows 10/11 x64, khong can cai .NET).
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
    Write-Host "Xong: $distDir\AIOMMO DolaAI.exe" -ForegroundColor Green
}
