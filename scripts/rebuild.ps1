<#
  改了 src/*.cs 後一鍵重建：停止 → 編譯 → 重啟（全部依 config.json；編譯失敗則不重啟）。
  用法:  .\rebuild.cmd            # 用 config 的 port
         .\rebuild.cmd -Port 8200
#>
param([int]$Port = 0)
$ErrorActionPreference = "Stop"
$ToolRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "_common.ps1")
if ($Port -le 0) { $Port = [int]$Cfg.port }

Write-Host "[1/3] 停止 server (port $Port)..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "stop.ps1") -Port $Port
Start-Sleep -Seconds 1   # 等 exe 檔案鎖釋放

Write-Host "[2/3] 編譯 (dotnet build -c Release)..." -ForegroundColor Cyan
Push-Location (Join-Path $ToolRoot "src")
dotnet build -c Release
$code = $LASTEXITCODE
Pop-Location
if ($code -ne 0) { Write-Error "編譯失敗 (exit $code)，未重啟 server"; exit 1 }

Write-Host "[3/3] 重新啟動 server..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "start.ps1") -Port $Port
