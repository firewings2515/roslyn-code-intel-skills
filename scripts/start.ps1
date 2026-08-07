<#
  啟動 roslyn-findrefs 常駐 server。server 跑在**自己的新視窗**(只有這一個視窗);
  啟動器 spawn 後立即返回。關閉該視窗＝停止 server。預設值來自 ..\config.json。
  - 每次啟動會在 ..\logs\start-*.log 留下逐步記錄，失敗時最後一行就是出錯的步驟。
  - 執行檔缺失或不完整(缺 runtimeconfig.json)時，自動嘗試 dotnet build -c Release(需 .NET SDK)。
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

# ---- 逐步 log：每一步先寫入 log 再執行；中途失敗時，log 最後一行即為出錯步驟 ----
$LogDir = Join-Path $ToolRoot "logs"
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$LogFile = Join-Path $LogDir ("start-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
function Log([string]$Msg) {
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $Msg
    Write-Host $line
    Add-Content -Path $LogFile -Value $line -Encoding UTF8
}
function Fail([string]$Step, [string]$Msg) {
    Log ("FAILED @ 步驟[{0}]" -f $Step)
    Log $Msg
    Write-Host "完整記錄: $LogFile" -ForegroundColor Yellow
    exit 1
}

Log "步驟[設定]: 讀取 config.json"
try {
    . (Join-Path $PSScriptRoot "_common.ps1")
} catch {
    Fail "設定" "讀取 config.json 失敗：$($_.Exception.Message)"
}
if ($Port -le 0) { $Port = [int]$Cfg.port }
if (-not $Root) { $Root = "$($Cfg.root)" }
if (-not $Csproj) { $Csproj = "$($Cfg.csproj)" }
Log "步驟[設定]: port=$Port root=$Root csproj=$Csproj"
if (-not $Root -and -not $Csproj) { Fail "設定" "config.json 未設定 root/csproj，且未帶 -Root/-Csproj 參數 — 無專案可載入" }

# ---- 執行檔檢查：缺 exe 或 runtimeconfig.json(缺了會被誤判為 self-contained app 而起不來)就自動建置 ----
$RuntimeCfg = Join-Path $ToolRoot "src\bin\Release\net9.0\roslyn-findrefs.runtimeconfig.json"
if (-not (Test-Path $Exe) -or -not (Test-Path $RuntimeCfg)) {
    Log "步驟[建置]: 執行檔或 runtimeconfig.json 缺失，嘗試自動建置"
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Fail "建置" "找不到 dotnet CLI — 請安裝 .NET 9(含)以上 SDK，或手動於 src\ 執行 dotnet build -c Release"
    }
    $srcDir = Join-Path $ToolRoot "src"
    $buildLog = Join-Path $LogDir ("build-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
    Log "步驟[建置]: dotnet build -c Release（完整輸出: $buildLog）"
    cmd /c "cd /d `"$srcDir`" && dotnet build -c Release > `"$buildLog`" 2>&1"
    if ($LASTEXITCODE -ne 0) { Fail "建置" "dotnet build 失敗 (exit $LASTEXITCODE)，詳見 $buildLog" }
    if (-not (Test-Path $Exe) -or -not (Test-Path $RuntimeCfg)) { Fail "建置" "建置成功但產物仍缺失: $Exe" }
    Log "步驟[建置]: 建置成功"
}

Log "步驟[埠檢查]: 檢查 port $Port"
$existing = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if ($existing) {
    Log ("server 已在 port {0} 執行中 (PID {1})。要重啟請先 .\stop.ps1" -f $Port, ($existing.OwningProcess | Select-Object -First 1))
    exit 0
}

# 移除「來自網路」封鎖標記（Mark of the Web）。下載/解壓的檔案會被標記，執行時 SmartScreen 阻擋，
# 取消即出現「操作被使用者取消」(ERROR_CANCELLED)。這裡先解除整個工具資料夾的封鎖。
Log "步驟[解封鎖]: 移除 Mark of the Web 標記"
try {
    Get-ChildItem -Path $ToolRoot -Recurse -File -ErrorAction SilentlyContinue |
        Unblock-File -ErrorAction SilentlyContinue
} catch { }

if ($Csproj) { $svArgs = @("serve", "--csproj", $Csproj, "--port", "$Port") }
else { $svArgs = @("serve", "--root", $Root, "--port", "$Port") }

Log "步驟[啟動]: $Exe $($svArgs -join ' ')"
try {
    $proc = Start-Process -FilePath $Exe -ArgumentList $svArgs -WindowStyle Normal -PassThru
} catch {
    Fail "啟動" @"
無法啟動 $Exe：$($_.Exception.Message)
若訊息為「操作被使用者取消 / The operation was canceled by the user」，多半是 Windows 對下載檔案的封鎖(SmartScreen / Mark of the Web)。請擇一處理：
  1) 執行： Get-ChildItem -Path '$ToolRoot' -Recurse | Unblock-File
  2) 或在檔案總管對下載的 zip 按右鍵→內容→勾「解除封鎖」後，再重新解壓。
另外確認已安裝 .NET 9 桌面/執行環境(dotnet --version 顯示 9.x)。
"@
}
Start-Sleep -Seconds 2
if ($proc.HasExited) {
    Fail "啟動" "server 程序啟動後立即結束 (exit code $($proc.ExitCode)) — 請在當前視窗手動執行「& '$Exe' $($svArgs -join ' ')」查看錯誤訊息"
}
Log "已在新視窗啟動 roslyn-findrefs (PID $($proc.Id)) on port $Port。"
Write-Host "建模進度見該新視窗；印出 'model READY' 後即可查詢。關閉該視窗＝停止 server。"

if ($Wait) {
    Log "步驟[等待就緒]: 輪詢 /health（最多 300 秒）"
    $ready = $false
    for ($i = 0; $i -lt 150; $i++) {
        Start-Sleep -Seconds 2
        if ($proc.HasExited) { Fail "等待就緒" "server 程序已結束 (exit code $($proc.ExitCode)) — 建模過程出錯，請看 server 視窗訊息" }
        try {
            $h = Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 3
            if ($h.ready) {
                Log ("READY: {0} — {1} 專案, {2} 檔, 建模 {3}ms" -f $h.scope, $h.projects, $h.docs, $h.buildMs)
                $ready = $true
                break
            }
        } catch { }
    }
    if (-not $ready) { Fail "等待就緒" "逾時 300 秒仍未 ready — server 可能仍在建模或已卡住，請看 server 視窗" }
}
