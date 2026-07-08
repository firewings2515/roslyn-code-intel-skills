<#
  停止 roslyn-findrefs server。port 預設讀 ..\config.json（與 start 對齊，避免關不掉）。
  用法:  .\stop.ps1            # 用 config.json 的 port
         .\stop.ps1 -Port 8200
#>
param([int]$Port = 0)
$ToolRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "_common.ps1")
if ($Port -le 0) { $Port = [int]$Cfg.port }

$conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if (-not $conn) { Write-Host "port $Port 上沒有執行中的 server"; exit 0 }
$procIds = $conn.OwningProcess | Select-Object -Unique
foreach ($procId in $procIds) {
    try { Stop-Process -Id $procId -Force; Write-Host "已停止 PID $procId (port $Port)" }
    catch { Write-Warning "無法停止 PID ${procId}: $_" }
}
