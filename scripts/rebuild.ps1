<#
  改了 src/*.cs 後一鍵重建：停止 → 編譯 → 重啟（編譯失敗則不重啟）。
  不帶參數時只處理「目前實際在監聽」的那幾組；一組都沒跑則重啟 default entry。
  用法:  .\rebuild.cmd            # 目前在跑的那幾組
         .\rebuild.cmd -Name clean
         .\rebuild.cmd -All       # config.json servers 全部
         .\rebuild.cmd -Port 8200
#>
param(
    [int]$Port = 0,
    [string]$Name = "",
    [switch]$All
)
$ErrorActionPreference = "Stop"
$ToolRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "_common.ps1")

if ($All) { $targets = @($Servers) }
elseif ($Name -or $Port -gt 0) {
    try { $sv = Resolve-RoslynServer $Name $Port }
    catch { Write-Host $_.Exception.Message -ForegroundColor Red; exit 1 }
    if ($Port -gt 0) { $sv = [pscustomobject]@{ name = $sv.name; port = $Port; root = $sv.root; csproj = $sv.csproj } }   # -Port 明示時覆寫
    $targets = @($sv)
} else {
    $targets = @($Servers | Where-Object { Get-NetTCPConnection -LocalPort ([int]$_.port) -State Listen -ErrorAction SilentlyContinue })
    if ($targets.Count -eq 0) { $targets = @($Cfg) }
}
$label = (@($targets) | ForEach-Object { "{0}:{1}" -f $_.name, $_.port }) -join ", "

Write-Host "[1/3] 停止 server ($label)..." -ForegroundColor Cyan
foreach ($sv in @($targets)) { & (Join-Path $PSScriptRoot "stop.ps1") -Port ([int]$sv.port) }
Start-Sleep -Seconds 1   # 等 exe 檔案鎖釋放

Write-Host "[2/3] 編譯 (dotnet build -c Release)..." -ForegroundColor Cyan
Push-Location (Join-Path $ToolRoot "src")
dotnet build -c Release
$code = $LASTEXITCODE
Pop-Location
if ($code -ne 0) { Write-Error "編譯失敗 (exit $code)，未重啟 server"; exit 1 }

Write-Host "[3/3] 重新啟動 server ($label)..." -ForegroundColor Cyan
foreach ($sv in @($targets)) {
    & (Join-Path $PSScriptRoot "start.ps1") -Port ([int]$sv.port) -Root "$($sv.root)" -Csproj "$($sv.csproj)"
}
