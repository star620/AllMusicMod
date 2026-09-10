param(
    [string]$tMLSteamPath = "d:/steam/steamapps/common/tModLoader/",
    [string]$ServerConfig = ""
)
$ErrorActionPreference = "Stop"

# 运行 tModLoader 专用服务器来加载并测试本机 Mods 里的 mod。
# 依赖更多 issue 4 端：MidiGmMod.tmod 已由 scripts/build.ps1 生成到用户 Mods 目录。

$serverExe = Join-Path $tMLSteamPath "tModLoader.dll"
if (-not (Test-Path $serverExe)) { throw "tModLoader.dll not found: $serverExe" }

Write-Host "==> Starting tModLoader dedicated server..." -ForegroundColor Cyan

$argsList = @("-server")
if ($ServerConfig -ne "") {
    $argsList += "-config"
    $argsList += $ServerConfig
}

# 在独立窗口启动，进程信息打印到屏幕便于交互式选择世界/端口。
Start-Process -FilePath "dotnet" -ArgumentList (@($serverExe) + $argsList) -WorkingDirectory $tMLSteamPath
Write-Host "Server window launched. Logs: $tMLSteamPath\tModLoader-Logs" -ForegroundColor Green