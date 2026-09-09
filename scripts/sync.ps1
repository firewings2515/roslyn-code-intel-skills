<#
  一鍵刷新：掃描全部已索引 .cs 的 mtime，重載所有異動檔（不必知道改了哪些檔）。
  等同 curl http://127.0.0.1:<port>/sync。需 server 執行中（先 .\start.ps1）。
  用法:  .\sync.ps1            # port 讀 ..\config.json 的 default entry
         .\sync.ps1 -Name clean
         .\sync.ps1 -Raw       # 直接吐 JSON（給程式解析）
         .\sync.ps1 -Port 8200
#>
param(
    [int]$Port = 0,
    [string]$Name = "",
    [switch]$Raw
)
$ToolRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "_common.ps1")
try { $sv = Resolve-RoslynServer $Name $Port }
catch { Write-Host $_.Exception.Message -ForegroundColor Red; exit 1 }
if ($Port -le 0) { $Port = [int]$sv.port }   # -Port 明示時覆寫 entry 的 port

try { $r = Invoke-RestMethod "http://127.0.0.1:$Port/sync" -TimeoutSec 300 }
catch {
    $resp = $_.Exception.Response
    if ($resp -and [int]$resp.StatusCode -eq 503) {
        Write-Warning "server 執行中但模型建置尚未完成(503)— 等 /health 回 ready 再 sync"; exit 2
    }
    Write-Error "無法連到 port $Port 的 server — 請先執行 .\start.ps1"; exit 1
}

if ($Raw) { $r | ConvertTo-Json -Depth 6; exit 0 }
Write-Host ("reloaded {0} file(s) ({1}ms)" -f $r.reloaded, $r.ms) -ForegroundColor Cyan
$r.files | ForEach-Object { "  $_" }
if ($r.missing) { Write-Host "missing (已索引但磁碟上不見了):" -ForegroundColor Yellow; $r.missing | ForEach-Object { "  $_" } }
if ($r.failed) { Write-Host "failed (這次讀不到，稍後再 sync 一次):" -ForegroundColor Yellow; $r.failed | ForEach-Object { "  $_" } }
if ($r.needRescan) { Write-Host "NOTE: $($r.note)" -ForegroundColor Yellow }
