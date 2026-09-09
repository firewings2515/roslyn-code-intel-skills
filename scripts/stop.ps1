<#
  停止 roslyn-findrefs server。port 預設讀 ..\config.json 的 default entry（與 start 對齊，避免關不掉）。
  用法:  .\stop.ps1            # 用 default entry 的 port
         .\stop.ps1 -Name clean
         .\stop.ps1 -All       # 停掉 servers 中每一組
         .\stop.ps1 -Port 8200
#>
param(
    [int]$Port = 0,
    [string]$Name = "",
    [switch]$All
)
$ToolRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "_common.ps1")
if ($All) { $targets = @($Servers) }
else {
    try { $targets = @(Resolve-RoslynServer $Name $Port) }
    catch { Write-Host $_.Exception.Message -ForegroundColor Red; exit 1 }
    # -Port 明示時覆寫 entry 的 port（-All 時各 entry 用自己的 port）
    if ($Port -gt 0) { $targets = @([pscustomobject]@{ name = $targets[0].name; port = $Port; root = $targets[0].root; csproj = $targets[0].csproj }) }
}

foreach ($sv in @($targets)) {
    $svPort = [int]$sv.port
    $conn = Get-NetTCPConnection -LocalPort $svPort -State Listen -ErrorAction SilentlyContinue
    if (-not $conn) { Write-Host "port $svPort 上沒有執行中的 server"; continue }
    $procIds = $conn.OwningProcess | Select-Object -Unique
    foreach ($procId in $procIds) {
        try { Stop-Process -Id $procId -Force; Write-Host ("已停止 PID {0} ([{1}] port {2})" -f $procId, $sv.name, $svPort) }
        catch { Write-Warning "無法停止 PID ${procId}: $_" }
    }
}
