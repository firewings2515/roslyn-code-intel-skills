# 由 start/stop/find 以 dot-source 方式載入。呼叫端須先設好 $ToolRoot。
# 讀取 <ToolRoot>\config.json，設定 $Servers/$Cfg/$Exe；缺檔則用預設值。
# config.json 兩種格式皆可：新的 { default, servers:[{name,port,root,csproj}] }、舊的扁平 { port, root, csproj }。
$cfgPath = Join-Path $ToolRoot "config.json"
$cfgRaw = $null
if (Test-Path $cfgPath) { $cfgRaw = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json }

# 單筆 server 設定正規化：port 轉 int、字串 trim、name 缺省時以 port 命名。
function New-RoslynServerEntry($Obj) {
    $port = 0
    if ($Obj -and $Obj.PSObject.Properties['port']) { $port = [int]$Obj.port }
    if ($port -le 0) { $port = 8123 }
    $name = ""
    if ($Obj -and $Obj.PSObject.Properties['name']) { $name = "$($Obj.name)".Trim() }
    if (-not $name) { $name = "port$port" }
    $root = ""
    if ($Obj -and $Obj.PSObject.Properties['root']) { $root = "$($Obj.root)".Trim() }
    $csproj = ""
    if ($Obj -and $Obj.PSObject.Properties['csproj']) { $csproj = "$($Obj.csproj)".Trim() }
    return [pscustomobject]@{ name = $name; port = $port; root = $root; csproj = $csproj }
}

$Servers = @()
$DefaultName = ""
if ($cfgRaw -and $cfgRaw.PSObject.Properties['servers'] -and $cfgRaw.servers) {
    foreach ($s in @($cfgRaw.servers)) { $Servers += (New-RoslynServerEntry $s) }
    if ($cfgRaw.PSObject.Properties['default']) { $DefaultName = "$($cfgRaw.default)".Trim() }
} elseif ($cfgRaw) {
    $e = New-RoslynServerEntry $cfgRaw   # 舊扁平格式視為單一 entry
    $e.name = "default"
    $Servers += $e
}
if ($Servers.Count -eq 0) { $Servers = @([pscustomobject]@{ name = "default"; port = 8123; root = ""; csproj = "" }) }

# $Cfg = default entry：不帶任何參數時的行為對象。default 指到不存在的名稱時退回第一筆。
$Cfg = $Servers[0]
if ($DefaultName) {
    $d = $Servers | Where-Object { $_.name -eq $DefaultName } | Select-Object -First 1
    if ($d) { $Cfg = $d }
    else { Write-Warning ("config.json 的 default '{0}' 不在 servers 中，改用 '{1}'" -f $DefaultName, $Cfg.name) }
}

# Name 有給就必須命中；否則 Port 有給就找同 port，找不到則沿用 default entry 只換 port；都沒給回 default entry。
function Resolve-RoslynServer([string]$Name = "", [int]$Port = 0) {
    if ($Name) {
        $e = $Servers | Where-Object { $_.name -eq $Name } | Select-Object -First 1
        if (-not $e) { throw ("config.json 沒有名為 '{0}' 的 server；可用名稱: {1}" -f $Name, ((@($Servers) | ForEach-Object { $_.name }) -join ", ")) }
        return $e
    }
    if ($Port -gt 0) {
        $e = $Servers | Where-Object { $_.port -eq $Port } | Select-Object -First 1
        if ($e) { return $e }
        return [pscustomobject]@{ name = $Cfg.name; port = $Port; root = $Cfg.root; csproj = $Cfg.csproj }
    }
    return $Cfg
}

$Exe = Join-Path $ToolRoot "src\bin\Release\net9.0\roslyn-findrefs.exe"
