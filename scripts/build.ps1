param(
    [ValidateSet("Release", "Debug")] [string]$Config = "Release",
    [string]$tMLSteamPath = "d:/steam/steamapps/common/tModLoader/"
)
$ErrorActionPreference = "Stop"

$Project = Join-Path $PSScriptRoot "..\AllMusicMod.csproj"
$Project = (Resolve-Path $Project).Path

Write-Host "==> Building AllMusic (tML: $tMLSteamPath, Config: $Config)" -ForegroundColor Cyan
if (-not (Test-Path (Join-Path $tMLSteamPath "tModLoader.dll"))) {
    throw "tModLoader.dll not found: $tMLSteamPath"
}

dotnet build $Project -c $Config -p:tMLSteamPath="$tMLSteamPath" `
    --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed, exit code $LASTEXITCODE" }

$modsDir = Join-Path $ENV:USERPROFILE "Documents\My Games\Terraria\tModLoader\Mods"
Write-Host "==> Build outputs (.tmod):" -ForegroundColor Cyan
Get-ChildItem -Path $modsDir -Filter "*.tmod" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object Name, @{N='Size(KB)';E={[math]::Round($_.Length/1KB,1)}}, LastWriteTime

Write-Host "Done." -ForegroundColor Green