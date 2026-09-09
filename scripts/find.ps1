<#
  查詢符號的所有語意引用（需先 .\start.ps1 啟動 server）。port 預設讀 ..\config.json 的 default entry。
  用法:
    .\find.ps1 PlayerSystem              # 人類可讀，輸出 file:line:col
    .\find.ps1 UnlockRequirement.Type    # 支援 Namespace.Type / Type.Member
    .\find.ps1 BetInfo -Raw              # 直接吐 JSON（給程式解析）
    .\find.ps1 BetInfo -Name clean       # 查 config.json 中另一組 server
    .\find.ps1 WeaponManager -Port 8200
#>
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Symbol,
    [int]$Port = 0,
    [string]$Name = "",
    [switch]$Raw
)
$ToolRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "_common.ps1")
try { $sv = Resolve-RoslynServer $Name $Port }
catch { Write-Host $_.Exception.Message -ForegroundColor Red; exit 1 }
if ($Port -le 0) { $Port = [int]$sv.port }   # -Port 明示時覆寫 entry 的 port

$url = "http://127.0.0.1:$Port/findrefs?symbol=" + [uri]::EscapeDataString($Symbol)
try { $r = Invoke-RestMethod $url -TimeoutSec 120 }
catch { Write-Error "無法連到 port $Port 的 server — 請先執行 .\start.ps1"; exit 1 }

if ($Raw) { $r | ConvertTo-Json -Depth 6; exit 0 }
if (-not $r.count) { Write-Host "$Symbol : 0 references ($($r.message))"; exit 0 }
Write-Host ("{0}  [{1}]  defined in: {2}" -f $r.symbol, $r.kind, $r.definedIn) -ForegroundColor Cyan
Write-Host ("{0} references in {1} files ({2}ms)`n" -f $r.count, $r.files, $r.ms)
$r.references | ForEach-Object { "{0}:{1}:{2}  [{3}]" -f $_.file, $_.line, $_.col, $_.project }
