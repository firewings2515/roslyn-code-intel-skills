# roslyn-code-intel — Unity C# 語意 code intelligence server

用 Roslyn `AdhocWorkspace` 對 Unity 專案提供**語意級、跨組件**的 C# code intelligence——find references、go-to-definition、hover、callers、型別階層、符號搜尋——不跑 MSBuild、不需 Unity Editor、不需 LSP client。
架構 = 常駐 server（建模一次、常駐記憶體）+ 輕量 client / curl（毫秒級查詢）。

skills可以直接給AI使用

> **使用方式（查詢端點、參數、curl 範例、輸出格式、刷新流程、跨組件去重）的完整說明在 [`SKILL.md`](./SKILL.md)** —— 那是單一事實來源，agent 也讀那份。本 README 只談安裝／建置／維運。

## 快速安裝（從原始碼編譯）
本 repo 只含原始碼、不含編譯產物，首次使用需 build 一次（編譯本身 ~1 秒；僅首次需網路下載 NuGet 套件）：

```powershell
# 0) 需求：.NET SDK 9.x（dotnet --version 應顯示 9.x）
# 1) 編譯（產物：src\bin\Release\net9.0\roslyn-findrefs.exe，scripts 會自動找到它）
cd src
dotnet build -c Release
cd ..

# 2) 設定要分析的專案：編輯 config.json，root 填 Unity 專案根目錄
#    （該專案需已由 Unity 產生 *.csproj 且編譯過一次；csproj/root 擇一即可）

# 3) 啟動常駐 server（開在新視窗顯示建模進度，印出 model READY 後即可查詢）
scripts\start.cmd

# 4) 驗證
curl.exe -s "http://127.0.0.1:8123/health"     # {"ready":true,...} 即完成
```

之後若改了 `src/*.cs`，用 `scripts\rebuild.cmd` 一鍵「停止 → 編譯 → 重啟」。完整前置條件見下方「環境需求」。

## 功能總覽
- **參照與導航**：`/findrefs`（所有語意引用，去重、跨組件）、`/definition`、`/hover`、`/callers`
- **型別關係**：`/implementations`、`/overrides`、`/derived`、`/hierarchy`
- **瀏覽與搜尋**：`/outline`（單檔結構）、`/symbols`（全解決方案模糊搜尋）
- **模型維護**：`/sync`（一鍵重載所有磁碟異動檔，~0.3s）、`/reload`（單檔）、`/rescan`（全量重建）、`/health`
- **語意精準**：分辨 overload、同名符號、繼承成員；依各 csproj 的 `DefineConstants` 解析 `#if` 區塊；比 grep/文字搜尋準確得多
- **純本機**：HTTP 只綁 127.0.0.1，查詢零網路依賴

### 範例
```bash
curl -s "http://127.0.0.1:8123/findrefs?symbol=PlayerSystem"
```
```jsonc
{
  "symbol": "PlayerSystem",
  "kind": "NamedType",
  "definedIn": "Assembly-CSharp",
  "count": 27,
  "files": 9,
  "ms": 180,
  "references": [
    { "project": "Assembly-CSharp", "file": "…\\Assets\\Scripts\\Game\\GameClient.cs", "line": 126, "col": 34 },
    { "project": "GameCore",        "file": "…\\Assets\\Scripts\\Core\\ItemSystem.cs", "line": 22,  "col": 17 }
    // …（節錄）
  ]
}
```

## 環境需求
**建置階段（build，一次性 / 改碼後）**
- **.NET SDK 9.x**（能 target `net9.0`）。
- **網路**：僅首次 build 需下載 NuGet 套件 `Microsoft.CodeAnalysis.CSharp.Workspaces 4.11.0`（之後快取於 `~/.nuget`，可離線重建）。
- 不需要 Unity、不需要專案原始碼（純編譯器工具）。

**執行階段（run，啟動 server）**
- **.NET 9 runtime**（框架相依 exe；裝了 .NET 9 SDK 即含。別台機器若只有其他版本，需另裝 .NET 9 Runtime）。
- 該機器上有**已由 Unity 產生 `.csproj`/`.sln`、且至少編譯過一次**的 Unity 專案（需存在 `Library/ScriptAssemblies/*.dll`）。**不需要開著 Unity Editor。**
- csproj 內 `HintPath` 指向的 Unity 安裝存在（UnityEngine DLL）。
- `config.json` 設定的 port 未被占用；執行**完全在本機、不需網路**。
- **Windows + PowerShell**（`scripts/` 用到 `Get-NetTCPConnection`/`taskkill`/`mklink`）。exe 本身跨平台，但腳本為 Windows 專用。
- **RAM（實測跨組件 102 專案 / 1.2 萬檔的 Unity 專案）**：建模完成後約 **1.0 GB**，跑過查詢、參照索引暖起來後穩態約 **1.4 GB**；建議預留 **~2 GB** 餘裕。單組件模式明顯更低。

## 資料夾結構（此資料夾本身就是一個 skill 包）
```
SKILL.md               # 使用說明（在根層，相對路徑）＝ 查詢/端點的唯一文件
config.json            # 設定的唯一來源：port / root / csproj
scripts/               # start.cmd  stop.cmd  find.cmd  sync.cmd  rebuild.cmd  (+ 對應 .ps1、_common.ps1)
src/                   # 原始碼 (Program.cs, roslyn-findrefs.csproj)；build 產物在 src/bin/Release/net9.0/
README.md
```
要當 Claude Code skill 使用時，把整個資料夾連結（或複製）進 skills 目錄即可，例如：
`cmd /c mklink /D "%USERPROFILE%\.claude\skills\roslyn-code-intel" "<放置處>\roslyn-code-intel"`
（`SKILL.md` 已在根層，符合 skill 慣例。）

## config.json（設定集中管理）
```json
{ "port": 8123, "root": "E:\\Path\\To\\YourUnityProject", "csproj": "" }
```
- `start` / `stop` / `find` 三個腳本**都讀這個檔**，所以 port 一定對齊——不會發生 start 用一個 port、stop 用另一個而關不掉。
- `root` 有值 → 跨組件模式；改填 `csproj`（且 `root` 留空）→ 單組件模式。
- 腳本參數（`-Port` / `-Root` / `-Csproj`）僅供臨時覆寫，平常不用帶。

## 快速開始（啟動腳本，都在 `scripts/`）
| 腳本 | 作用 |
|---|---|
| `start.cmd` / `start.ps1` | 依 config 在**新視窗**啟動常駐 server，顯示建模進度；ready 才返回 |
| `stop.cmd` / `stop.ps1` | 依 config 的 port 停掉 server |
| `find.cmd` / `find.ps1` | 查詢符號引用，輸出 `file:line:col [assembly]` |
| `sync.cmd` / `sync.ps1` | 一鍵重載所有磁碟上有異動的已索引 .cs（等同 `GET /sync`） |
| `rebuild.cmd` / `rebuild.ps1` | 改了 `src/*.cs` 後一鍵：**停止 → 編譯 → 重啟**（編譯失敗則不重啟） |

```powershell
scripts\start.cmd                    # 啟動（首次建模約 1–2 分鐘）
scripts\find.cmd PlayerSystem        # 查詢（其餘查詢端點見 SKILL.md）
scripts\stop.cmd                     # 停止（port 來自 config，必定對齊）
```
> server 是**手動啟動**的常駐 process（不設開機自動啟動），開在**自己的視窗**顯示 log。要停：關閉視窗或 `scripts\stop.cmd`。

## build / 執行檔
```
src/bin/Release/net9.0/roslyn-findrefs.exe        # 可獨立呼叫
# 改了 src/*.cs 後，最省事：一鍵 停止→編譯→重啟
scripts\rebuild.cmd
# 或手動重建（需先停 server，否則 exe 被鎖）：
cd src ; dotnet build -c Release
```
> 編譯本身 ~1 秒；慢的是重啟後重建 Roslyn 模型(暖 ~10s、冷 ~130s)。改 `config.json` 只需重啟不需編譯;改被分析的 Unity 原始碼用 `/sync`(自動偵測所有 mtime 異動檔並重載,~0.3s)、`/reload`、`/rescan` 熱更新即可。
server 也可手動下參數啟動（一般用腳本即可）：`roslyn-findrefs.exe serve --root <dir> --port <n>` 或 `--csproj <path>`。省略的參數會自動讀 `config.json`（從 exe 位置往上層找，即 skill 根目錄）；**程式內沒有任何寫死的專案路徑**——參數與 config 都沒給時 serve 會直接報錯退出。

## 效能實測（一個 102 專案 / 1.2 萬檔的實際 Unity 專案）
| 階段 | 耗時 |
|---|---|
| 跨組件建模 | ~75–130s（讀 1.2 萬檔 + 建 compilation） |
| 首次 find-references（索引暖機） | ~7–11s |
| 後續查詢 | 40–700ms（視結果大小） |
| 記憶體 | 數 GB（常駐） |

單組件模式建模僅 ~6s（暖 cache），適合只需 Assembly-CSharp 範圍的快查。

## 停止 server
```powershell
scripts\stop.cmd          # 依 config 的 port 關閉（推薦）
```
或手動：關閉 server 視窗；或 `netstat -ano | findstr 127.0.0.1:8123` 找 PID 後 `taskkill /PID <pid> /F`。

## 已知限制
- 一個 server process 綁一組設定 / 一個 port。要同時跑單組件+跨組件可開兩個 port。
- 只認 `<Compile>` 列到、且檔案存在的原始碼；純預編譯 DLL 插件是以 metadata 參考納入（可被引用但無法在其中找引用）。
- csproj 是 Unity 產生的投影，偶爾略舊（指到已刪檔會自動略過）。
- 跨組件「共用原始碼」去重等行為細節見 `SKILL.md` 的 Gotchas。

## License
MIT — 見 [LICENSE](./LICENSE)。
