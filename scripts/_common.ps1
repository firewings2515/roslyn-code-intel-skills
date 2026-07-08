# 由 start/stop/find 以 dot-source 方式載入。呼叫端須先設好 $ToolRoot。
# 讀取 <ToolRoot>\config.json，設定 $Cfg 與 $Exe；缺檔則用預設值。
$cfgPath = Join-Path $ToolRoot "config.json"
if (Test-Path $cfgPath) {
    $Cfg = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
} else {
    $Cfg = [pscustomobject]@{ port = 8123; root = ""; csproj = "" }
}
$Exe = Join-Path $ToolRoot "src\bin\Release\net9.0\roslyn-findrefs.exe"
