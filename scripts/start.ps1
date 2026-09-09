<#
  啟動 roslyn-findrefs 常駐 server。server 跑在**自己的新視窗**(只有這一個視窗);
  啟動器 spawn 後立即返回。關閉該視窗＝停止 server。預設值來自 ..\config.json 的 default entry。
  - 每次啟動會在 ..\logs\start-*.log 留下逐步記錄，失敗時最後一行就是出錯的步驟。
  - 執行檔缺失或不完整(缺 runtimeconfig.json)時，自動嘗試 dotnet build -c Release(需 .NET SDK)。
  用法:
    .\start.ps1                 # 不帶任何選組參數＝視同 -All：依序啟動 servers 中每一組(已在跑的跳過)
    .\start.ps1 -One            # 只啟動 default entry
    .\start.ps1 -Name clean     # 指定 config.json servers 中的某一組(單一)
    .\start.ps1 -All            # 依序啟動 servers 中每一組(已在跑的跳過)
    .\start.ps1 -Wait           # 在當前視窗等到 ready 才返回(不另開視窗)
    .\start.ps1 -Port 8200      # 臨時換 port(單一)
    .\start.ps1 -Csproj "E:\...\Assembly-CSharp.csproj"   # 臨時改單組件模式(單一)
#>
param(
    [int]$Port = 0,
    [string]$Root = "",
    [string]$Csproj = "",
    [string]$Name = "",
    [switch]$One,      # 只處理單一 entry(沿用 -Name/-Port/-Root/-Csproj 的 Resolve 邏輯)；未指定挑選條件時等同不帶參數的 default entry
    [switch]$All,      # 依序處理 config.json servers 中每一組
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
# 選組規則：-All，或都沒給選取條件（含不帶任何參數）→ 全部 entry；-One 或給了 -Name/-Port/-Root/-Csproj 任一 → 單一 entry
if ($All -or (-not $One -and -not $Name -and $Port -le 0 -and -not $Root -and -not $Csproj)) {
    $targets = @($Servers)
} else {
    try { $targets = @(Resolve-RoslynServer $Name $Port) }
    catch { Fail "設定" $_.Exception.Message }
    # -Port 明示時覆寫 entry 的 port（-All 時各 entry 用自己的 port）
    if ($Port -gt 0) { $targets = @([pscustomobject]@{ name = $targets[0].name; port = $Port; root = $targets[0].root; csproj = $targets[0].csproj }) }
}
Log ("步驟[設定]: 目標 {0} 組 — {1}" -f $targets.Count, ((@($targets) | ForEach-Object { "{0}:{1}" -f $_.name, $_.port }) -join ", "))

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

# 移除「來自網路」封鎖標記（Mark of the Web）。下載/解壓的檔案會被標記，執行時 SmartScreen 阻擋，
# 取消即出現「操作被使用者取消」(ERROR_CANCELLED)。這裡先解除整個工具資料夾的封鎖。
Log "步驟[解封鎖]: 移除 Mark of the Web 標記"
try {
    Get-ChildItem -Path $ToolRoot -Recurse -File -ErrorAction SilentlyContinue |
        Unblock-File -ErrorAction SilentlyContinue
} catch { }

foreach ($sv in @($targets)) {
    $svName = "$($sv.name)"
    $svPort = [int]$sv.port
    $svRoot = "$($sv.root)"
    $svCsproj = "$($sv.csproj)"
    if ($Root) { $svRoot = $Root }         # -Root/-Csproj 明示時覆寫 entry 的設定
    if ($Csproj) { $svCsproj = $Csproj }
    Log "步驟[設定]: [$svName] port=$svPort root=$svRoot csproj=$svCsproj"
    if (-not $svRoot -and -not $svCsproj) { Fail "設定" "[$svName] config.json 未設定 root/csproj，且未帶 -Root/-Csproj 參數 — 無專案可載入" }

    Log "步驟[埠檢查]: [$svName] 檢查 port $svPort"
    $existing = Get-NetTCPConnection -LocalPort $svPort -State Listen -ErrorAction SilentlyContinue
    if ($existing) {
        Log ("[{0}] server 已在 port {1} 執行中 (PID {2})。要重啟請先 .\stop.ps1 -Port {1}" -f $svName, $svPort, ($existing.OwningProcess | Select-Object -First 1))
        continue
    }

    if ($svCsproj) { $svArgs = @("serve", "--csproj", $svCsproj, "--port", "$svPort") }
    else { $svArgs = @("serve", "--root", $svRoot, "--port", "$svPort") }

    Log "步驟[啟動]: [$svName] $Exe $($svArgs -join ' ')"
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
        Fail "啟動" "[$svName] server 程序啟動後立即結束 (exit code $($proc.ExitCode)) — 請在當前視窗手動執行「& '$Exe' $($svArgs -join ' ')」查看錯誤訊息"
    }
    Log "已在新視窗啟動 roslyn-findrefs [$svName] (PID $($proc.Id)) on port $svPort。"
    Write-Host "建模進度見該新視窗；印出 'model READY' 後即可查詢。關閉該視窗＝停止 server。"

    if ($Wait) {
        Log "步驟[等待就緒]: [$svName] 輪詢 /health（最多 300 秒）"
        $ready = $false
        for ($i = 0; $i -lt 150; $i++) {
            Start-Sleep -Seconds 2
            if ($proc.HasExited) { Fail "等待就緒" "[$svName] server 程序已結束 (exit code $($proc.ExitCode)) — 建模過程出錯，請看 server 視窗訊息" }
            try {
                $h = Invoke-RestMethod "http://127.0.0.1:$svPort/health" -TimeoutSec 3
                if ($h.ready) {
                    Log ("READY [{0}]: {1} — {2} 專案, {3} 檔, 建模 {4}ms" -f $svName, $h.scope, $h.projects, $h.docs, $h.buildMs)
                    $ready = $true
                    break
                }
            } catch { }
        }
        if (-not $ready) { Fail "等待就緒" "[$svName] 逾時 300 秒仍未 ready — server 可能仍在建模或已卡住，請看 server 視窗" }
    }
}
