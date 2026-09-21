---
name: roslyn-code-intel
description: Precise semantic C# code intelligence for Unity projects via a local Roslyn HTTP server — find references, go-to-definition, hover/type info, callers, implementations/overrides, derived classes, type hierarchy, class-level impact/blast radius, file outline, and workspace symbol search. Use this INSTEAD OF grep/ripgrep/text-search whenever you need accurate C# navigation on .cs code in the configured project: it is cross-assembly and semantically correct (distinguishes overloads, same-named symbols, inherited members). After editing .cs files, hit /sync once (auto-reloads every changed file) so results stay fresh.
---

# roslyn-code-intel — semantic C# code intelligence

A resident Roslyn language server exposes C# code-intelligence over HTTP on a local port taken from
`config.json`, which holds one entry per project under `servers` (the `default` entry is used unless
you name another; port **8123** out of the box). It loads the whole solution (every Unity-generated
csproj of that entry's project) as a semantic model, so queries are **precise and cross-assembly**
— far better than text search for C#.

## Common mistakes / 常見誤用 (read before querying)
1. **`symbol=` takes a simple name only** (e.g. `TryLoadGame`). A dotted full name (`LobbyManager.TryLoadGame`) or one with a namespace returns `no symbol found` → use `/symbols?query=` fuzzy search to locate it first.
2. **It does not take a full method signature** (with parentheses and parameter types) — always pass the simple name.
3. **On Windows PowerShell use `curl.exe`** (`curl` is an alias for `Invoke-WebRequest` and will blow up if used directly).
4. **After editing `.cs` files, refresh BEFORE querying** — otherwise you hit a stale snapshot. Easiest: `/sync` (no args) reloads every indexed file that changed on disk; you don't need to track which files were edited. `/reload?file=…` still works when you want to refresh exactly one file.

## Layout (this skill folder; all paths below are relative to it)
```
SKILL.md               # this file (skill root)
config.json            # servers[] of { name, port, root, csproj } + default  ← single source of truth
scripts/               # start.cmd  stop.cmd  rebuild.cmd  find.cmd  sync.cmd  (+ .ps1)
src/                   # source; build output at src/bin/Release/net9.0/roslyn-findrefs.exe
README.md
```
Commands below are relative to this folder. The project root and port come from `config.json`; don't hardcode them.
Every script takes `-Name <entry>` to pick one entry, and `start`/`stop`/`rebuild` also take `-All` for every entry.
The older flat `{ port, root, csproj }` config is still read — as a single entry named `default`.

## When to use
- Where a type/method/field is **used** (`/findrefs`) — beats grep (no hits from comments/strings/same-named symbols).
- **Go-to-definition** (`/definition`), **type info / docs** (`/hover`).
- **Who calls this** (`/callers`); **who implements/overrides/derives** (`/implementations` `/overrides` `/derived` `/hierarchy`).
- **What breaks if I change this class** (`/impact`) — the whole BFS runs server-side, so you get the blast radius in one call instead of walking `/findrefs` hop by hop.
- **Outline** a file (`/outline`) or **search** a symbol by substring (`/symbols`) instead of reading whole files.

## When NOT to use
- Non-C# files, or code outside the configured Unity project.
- Compile-error checking — NOT provided by design; don't expect diagnostics.
- A symbol you just wrote in an unsaved buffer — the server reads from disk; save first, then `/reload`.

## Step 0 — ensure the server is running
```bash
curl -s "http://127.0.0.1:8123/health"          # port = the config.json `servers` entry you want
```
- `{"ready":true,...}` → proceed; the reply's `scope` says which project that server holds.
- Refused or `"ready":false` → start it (cold build ~1–2 min, then instant); run from the tool folder:
```bash
scripts/start.cmd                  # all config servers (ones already listening are skipped)
scripts/start.cmd -Name clean      # one named entry
scripts/start.cmd -All             # every entry (ones already listening are skipped)
scripts/startone.cmd                # only the default entry (add -Name to pick another)
```
Missing/incomplete build output (no exe or no `roslyn-findrefs.runtimeconfig.json`) is handled automatically — start runs `dotnet build -c Release` first (needs .NET SDK). Every start writes a step log to `logs/start-*.log`; on failure the last line names the failing step.
If you shouldn't launch long-running processes, ask the user to run `scripts/start.cmd`.

## Core workflow (IMPORTANT)
The model is an in-memory snapshot. **After you edit .cs files, refresh before querying:**
```bash
# 1) edit file(s)
# 2) refresh — you do NOT need to know which files changed:
curl -s "http://127.0.0.1:8123/sync"      # scans mtimes of all indexed files, reloads the changed ones (~0.1–1s)
# 3) query
```
- `/sync` catches every on-disk change (any editor/tool, git checkout/revert, submodules included) — safe to call anytime, no-op if nothing changed.
- Know the exact file? `curl -s "http://127.0.0.1:8123/reload?file=Assets/Scripts/Foo.cs"` refreshes just that one (~ms). Paths may be repo-relative.
- Added a NEW file / changed project structure → `curl "http://127.0.0.1:8123/rescan"` (full rebuild; wait for /health ready). `/sync` flags this for you: `needRescan:true` when csproj files changed or indexed files disappeared.

> Two different "rebuilds", don't confuse them:
> - **indexed Unity code changed** (a `.cs` under the project root) → `/reload?file=…` or `/rescan` (no restart needed).
> - **the tool's own source changed** (`src/*.cs` of this skill) → `scripts/rebuild.cmd` = stop → `dotnet build -c Release` → restart (skips restart if the build fails). Needs .NET 9 SDK.

## Endpoints (all GET)
Most take `?symbol=NAME` (bare `Foo`, `Namespace.Type`, or `Type.Member`).
`definition` / `hover` / `findrefs` also take a cursor: `?file=PATH&line=L&col=C` (1-based).
`file=` accepts a **repo-relative** path (resolved against the project root) or an absolute path.

| Endpoint | Params | Returns |
|---|---|---|
| `/findrefs` | symbol \| file+line+col | every semantic reference (deduped, cross-assembly) |
| `/definition` | symbol \| cursor | declaration location(s) |
| `/hover` | symbol \| cursor | kind, signature, base type, interfaces, XML doc |
| `/outline` | file | all types & members in the file (+ line numbers) |
| `/symbols` | query [&limit] | fuzzy (substring) symbol search across the solution |
| `/implementations` | symbol | implementers of an interface/abstract member |
| `/overrides` | symbol | members overriding this one |
| `/derived` | symbol | subclasses of a type |
| `/callers` | symbol | methods that call this method (deduped) |
| `/hierarchy` | symbol | base chain + interfaces + derived classes |
| `/impact` | symbol [&depth] [&direction] | class-level blast radius, BFS by level (see below) |
| `/reload` | file | refresh one edited file |
| `/sync` | — | reload ALL files changed on disk since load (mtime scan); flags `needRescan` |
| `/rescan` | — | full rebuild |
| `/health` | — | status / ready |

### Examples (repo-relative file paths)
```bash
curl -s "http://127.0.0.1:8123/findrefs?symbol=BetInfo"
curl -s "http://127.0.0.1:8123/definition?symbol=PlayerSystem"
curl -s "http://127.0.0.1:8123/hover?symbol=BetLevelsInfo"
curl -s "http://127.0.0.1:8123/callers?symbol=GetBetLevel"
curl -s "http://127.0.0.1:8123/derived?symbol=BaseSystem"
curl -s "http://127.0.0.1:8123/symbols?query=WeaponMana&limit=20"
curl -s "http://127.0.0.1:8123/outline?file=Assets/Scripts/BetLevelsInfo.cs"
curl -s "http://127.0.0.1:8123/definition?file=Assets/Scripts/BetLevelsInfo.cs&line=9&col=35"   # cursor
curl -s "http://127.0.0.1:8123/impact?symbol=BetInfo&depth=1&direction=in"
# thin client (== /findrefs):
scripts/find.cmd PlayerSystem
```

### `/impact` — "what breaks if I change this class"
Answers **class-level** blast radius: the BFS runs to completion inside the server, so one call replaces a chain of `/findrefs` hops.

| Param | Default | Notes |
|---|---|---|
| `symbol` | — | a type (bare `Foo`, `Namespace.Type`, or a cursor `file`+`line`+`col`). A **member** target is rolled up to the type that declares it. Bare-name collisions return the same `ambiguous` payload as `/findrefs` → re-query with the FQN. |
| `depth` | `1` | clamped to **1–3**. |
| `direction` | `in` | `in` = who depends on the target (the blast radius), `out` = what the target depends on, `both` = the union. |

- **Edge (A→X):** any member or declaration of named type `A` references `X` **or any member `X` declares**. So a class that never names the type but touches `x.SomeField` still shows up — that is the point, and it is why the result is a superset of `/findrefs` on the type alone.
- **Attribution:** a reference is credited to the **innermost named type** enclosing it, so a nested type is its own node (`AW.ArcheryTable.ParseData`, not `AW.ArcheryTable`). Self-references are dropped and each pair of types is counted once.
- **Guardrails:** hard cap of **200 classes** returned. `count` per level is always the **true** total even when the class list was cut; `truncated:true` says the listing was capped (and that deeper levels were not expanded). Output is deliberately thin — `{name, full, file}` only, no snippets and no per-reference locations; go to `/findrefs` for those.
- **Cost:** depth 1 is ~50 ms–2 s. A high fan-in type at depth 2–3 can take a minute or more before the cap stops it (`ArkGame.BaseSystem` depth 2 ≈ 73 s) — start at depth 1 and widen only if you need to.
- **Snapshot:** like every other endpoint it reads the current in-memory model and never syncs implicitly — **call `/sync` first** if you have edited `.cs` files.

```bash
curl -s "http://127.0.0.1:8123/sync"
curl -s "http://127.0.0.1:8123/impact?symbol=BetLevelsInfo&depth=2&direction=in"
```
Returns `{symbol, resolved, direction, depth, totalClasses, truncated, note, levels:[{depth, count, classes:[{name, full, file}]}], ms}` — `file` is repo-relative.

## Reading the output (JSON)
- `/findrefs` → `{symbol, kind, definedIn, count, files, ms, references:[{project, file, line, col}]}` — `file:line:col` clickable; `project` = owning assembly.
- `/sync` → `{reloaded, files:[...], missing, failed, changedProjects, needRescan, note, ms}` — `needRescan:true` means csproj files changed (e.g. Unity regenerated after adding/deleting .cs) or indexed files vanished: run `/rescan` (details in `note`). Non-empty `failed` → that file couldn't be applied this pass; call `/sync` again.
- `/definition` → `{symbol, kind, count, definitions:[{file,line,col}]}`.
- `/hover` → `{display, kind, baseType, interfaces, returnType, namespace, assembly, doc, definition:[...]}`.
- Others → `{count, results/symbols/callers:[...]}` with `full` (fully-qualified name) + `locations`.

## Gotchas (already handled)
- **Shared source across assemblies:** in Unity projects some `.cs` compile into several assemblies. Finds run over all declarations and dedup by physical `(file,line,col)`, so counts aren't inflated; `definedIn` may list several assemblies — expected.
- **Ambiguous bare names:** for `?symbol=Foo`, a named **type** is preferred when a type and member share the name. Need a member? use `Type.Member` or a cursor position.
- **Same-named distinct types:** if a bare name matches TWO+ different types (e.g. `GameCore.PlayerSystem` vs `MiniGame.PlayerSystem`), `/findrefs` returns `{"ambiguous":true,"matches":[{type,assemblies,file,line}...]}` instead of merging them — re-query with the fully-qualified name (`?symbol=GameCore.PlayerSystem`) or a cursor position. (Other endpoints like `/callers` `/derived` pick the first matching type; use an FQN/cursor there too when a name collides.)
- **First query after (re)build** takes a few seconds (builds the reference index); later queries are 25–700 ms.
- Only `<Compile>`-listed, on-disk source is indexed; precompiled DLL plugins are references only.
- **`#if` regions:** each assembly is parsed with its csproj `<DefineConstants>` (UNITY_EDITOR and any project-defined symbols), so code inside those directives IS covered — results match VS Code's "Find All References" on real usages. (`/findrefs` lists usages, not the declaration itself — that's the only expected diff vs VS Code, which includes the declaration.)
