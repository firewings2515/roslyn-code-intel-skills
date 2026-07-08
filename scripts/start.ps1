<#
  啟動 roslyn-findrefs 常駐 server。server 跑在**自己的新視窗**(只有這一個視窗);
  啟動器 spawn 後立即返回。關閉該視窗＝停止 server。預設值來自 ..\config.json。
  用法:
    .\start.ps1                 # 全部用 config.json,spawn 後立即返回(只一個視窗)
    .\start.ps1 -Wait           # 在當前視窗等到 ready 才返回(不另開視窗)
    .\start.ps1 -Port 8200      # 臨時換 port
    .\start.ps1 -Csproj "E:\...\Assembly-CSharp.csproj"   # 臨時改單組件模式
#>
param(
    [int]$Port = 0,
    [string]$Root = "",
    [string]$Csproj = "",
    [switch]$Wait      # 在當前視窗等到 ready 才返回（不另開視窗）；預設 spawn 後立即返回
)
$ErrorActionPreference = "Stop"
$ToolRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "_common.ps1")

if ($Port -le 0) { $Port = [int]$Cfg.port }
if (-not $Root) { $Root = "$($Cfg.root)" }
if (-not $Csproj) { $Csproj = "$($Cfg.csproj)" }

if (-not (Test-Path $Exe)) { Write-Error "找不到執行檔: $Exe（先在 src\ 執行 dotnet build -c Release）"; exit 1 }
if (-not $Root -and -not $Csproj) { Write-Error "config.json 未設定 root/csproj，且未帶 -Root/-Csproj 參數 — 無專案可載入"; exit 1 }

$existing = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "server 已在 port $Port 執行中 (PID $($existing.OwningProcess | Select-Object -First 1))。要重啟請先 .\stop.ps1"
    exit 0
}

if ($Csproj) { $svArgs = @("serve", "--csproj", $Csproj, "--port", "$Port") }
else { $svArgs = @("serve", "--root", $Root, "--port", "$Port") }

# 移除「來自網路」封鎖標記（Mark of the Web）。下載/解壓的檔案會被標記，執行時 SmartScreen 阻擋，
# 取消即出現「操作被使用者取消」(ERROR_CANCELLED)。這裡先解除整個工具資料夾的封鎖。
try {
    Get-ChildItem -Path $ToolRoot -Recurse -File -ErrorAction SilentlyContinue |
        Unblock-File -ErrorAction SilentlyContinue
} catch { }

try {
    $proc = Start-Process -FilePath $Exe -ArgumentList $svArgs -WindowStyle Normal -PassThru
} catch {
    Write-Error @"
無法啟動 $Exe：$($_.Exception.Message)
若訊息為「操作被使用者取消 / The operation was canceled by the user」，多半是 Windows 對下載檔案的封鎖(SmartScreen / Mark of the Web)。請擇一處理：
  1) 執行： Get-ChildItem -Path '$ToolRoot' -Recurse | Unblock-File
  2) 或在檔案總管對下載的 zip 按右鍵→內容→勾「解除封鎖」後，再重新解壓。
另外確認已安裝 .NET 9 桌面/執行環境(dotnet --version 顯示 9.x)。
"@
    exit 1
}
Write-Host "已在新視窗啟動 roslyn-findrefs (PID $($proc.Id)) on port $Port。" -ForegroundColor Green
Write-Host "建模進度見該新視窗；印出 'model READY' 後即可查詢。關閉該視窗＝停止 server。"

if ($Wait) {
    Write-Host "等待就緒中..."
    for ($i = 0; $i -lt 150; $i++) {
        Start-Sleep -Seconds 2
        try {
            $h = Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 3
            if ($h.ready) {
                Write-Host ("READY: {0} — {1} 專案, {2} 檔, 建模 {3}ms" -f $h.scope, $h.projects, $h.docs, $h.buildMs) -ForegroundColor Green
                break
            }
        } catch { }
    }
}
