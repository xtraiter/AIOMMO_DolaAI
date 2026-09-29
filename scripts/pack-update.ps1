<#
.SYNOPSIS
  Tao goi cap nhat (zip chua DolaCoordinator.exe) va file version.json co SHA-256.
.DESCRIPTION
  App chi cai goi neu: URL HTTPS, cung host voi version.json, va SHA-256 khop.
  Dang len HTTPS: version.json va goi zip (cung mot host), roi dat URL version.json trong tab Cai dat.
.EXAMPLE
  .\scripts\pack-update.ps1 -Version 1.1.0 -DownloadBaseUrl https://updates.example.com/dola
#>
param(
    [Parameter(Mandatory = $true)] [string]$Version,
    [Parameter(Mandatory = $true)] [string]$DownloadBaseUrl,
    [string]$Changelog = "",
    [string]$OutputDir = "dist\updates"
)

$ErrorActionPreference = "Stop"
if ($DownloadBaseUrl -notmatch '^https://') { throw "DownloadBaseUrl phai la HTTPS." }

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "DolaCoordinator\DolaCoordinator.csproj"
$out = Join-Path $root $OutputDir
$temp = Join-Path $env:TEMP ("DolaUpd_" + [Guid]::NewGuid().ToString("N"))
$zip = Join-Path $out "update_v$Version.zip"

New-Item -ItemType Directory -Force $out | Out-Null

Write-Host "[1/3] Bien dich v$Version..." -ForegroundColor Cyan
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:Version=$Version -o $temp
if ($LASTEXITCODE -ne 0) { throw "dotnet publish that bai (ma $LASTEXITCODE)" }

Write-Host "[2/3] Nen goi + tinh SHA-256..." -ForegroundColor Cyan
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path "$temp\DolaCoordinator.exe" -DestinationPath $zip -Force
Remove-Item -Recurse -Force $temp
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash

Write-Host "[3/3] Ghi version.json..." -ForegroundColor Cyan
$info = [ordered]@{
    version        = $Version
    releaseDate    = (Get-Date).ToString("yyyy-MM-dd")
    downloadUrl    = "$($DownloadBaseUrl.TrimEnd('/'))/update_v$Version.zip"
    changelog      = $Changelog
    mandatory      = $false
    sha256Checksum = $hash
}
[System.IO.File]::WriteAllText((Join-Path $out "version.json"), ($info | ConvertTo-Json), [System.Text.UTF8Encoding]::new($false))

Write-Host "Xong. Dang 2 file trong $out len $DownloadBaseUrl" -ForegroundColor Green
Write-Host "SHA-256: $hash"
