using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

// ============================================================================
// Roslyn semantic code-intelligence server for Unity projects (no MSBuild, no Editor).
//   serve --root <dir>    [--port N]      -> load ALL *.csproj as source (cross-assembly)
//   serve --csproj <path> [--port N]      -> single assembly
//   find  [--port N] <Symbol>             -> thin client (find references)
// Omitted args fall back to config.json (searched upward from the exe folder = skill root).
// No hardcoded project paths: without CLI args AND without config, serve refuses to start.
//
// HTTP endpoints (GET, any CLI / curl):
//   /health
//   /findrefs        ?symbol=NAME | ?file=&line=&col=
//   /definition      ?symbol=NAME | ?file=&line=&col=
//   /hover           ?symbol=NAME | ?file=&line=&col=
//   /outline         ?file=PATH
//   /symbols         ?query=SUBSTR [&limit=N]
//   /implementations ?symbol=NAME
//   /overrides       ?symbol=NAME
//   /derived         ?symbol=NAME
//   /callers         ?symbol=NAME
//   /hierarchy       ?symbol=NAME            (base chain + derived)
//   /reload          ?file=PATH              (re-read one edited file from disk)
//   /sync                                    (reload every indexed file whose mtime changed; no args)
//   /rescan                                  (full rebuild: new files / project changes)
// ============================================================================

class Program
{
    static async Task<int> Main(string[] args)
    {
        string mode = args.FirstOrDefault() ?? "help";
        var cfg = LoadConfig();   // config.json found by walking up from the exe folder (skill root)
        int port = ArgInt(args, "--port", cfg.port);
        switch (mode)
        {
            case "serve":
                string? root = ArgStrOpt(args, "--root");
                string? csproj = ArgStrOpt(args, "--csproj");
                if (root == null && csproj == null)   // no CLI choice → config (csproj wins, like start.ps1)
                {
                    if (cfg.csproj != null) csproj = cfg.csproj; else root = cfg.root;
                }
                if (string.IsNullOrWhiteSpace(root) && string.IsNullOrWhiteSpace(csproj))
                {
                    Console.Error.WriteLine("no project to serve: pass --root <dir> or --csproj <path>, or set \"root\"/\"csproj\" in config.json (searched upward from the exe folder)");
                    return 2;
                }
                return await Server.RunAsync(root, csproj, port);
            case "find":
                string symbol = args.LastOrDefault(a => !a.StartsWith("-") && a != "find") ?? "";
                if (string.IsNullOrWhiteSpace(symbol)) { Console.Error.WriteLine("usage: find [--port N] <Symbol>"); return 2; }
                return await Client.QueryAsync(port, symbol);
            default:
                Console.WriteLine("usage:\n  serve --root <dir> [--port 8123]     (cross-assembly)\n  serve --csproj <path> [--port 8123]  (single assembly)\n  find [--port 8123] <Symbol>\n  endpoints: /findrefs /definition /hover /outline /symbols /implementations /overrides /derived /callers /hierarchy /reload /sync /rescan /health");
                return 0;
        }
    }

    static string? ArgStrOpt(string[] a, string k)
    { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
    static int ArgInt(string[] a, string k, int def)
    { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out var v) ? v : def; }

    // config.json lives at the skill root; the exe sits in src/bin/Release/net9.0 under it,
    // so walk up from the exe folder. CLI args always override. No hardcoded project paths.
    static (int port, string? root, string? csproj) LoadConfig()
    {
        static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        try
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                var f = Path.Combine(d.FullName, "config.json");
                if (!File.Exists(f)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var r = doc.RootElement;
                return (
                    r.TryGetProperty("port", out var p) && p.TryGetInt32(out var v) ? v : 8123,
                    NonEmpty(r.TryGetProperty("root", out var rt) ? rt.GetString() : null),
                    NonEmpty(r.TryGetProperty("csproj", out var cp) ? cp.GetString() : null));
            }
        }
        catch (Exception e) { Console.Error.WriteLine($"[config] failed to read config.json: {e.Message}"); }
        return (8123, null, null);
    }
}

// ---------------------------------------------------------------------------
static class Server
{
    static volatile bool _ready;
    static string _status = "starting";
    static volatile Solution? _sol;
    static long _buildMs;
    static int _projCount, _docCount;
    static string _scope = "";
    static string? _root, _csproj;
    // mtime of every indexed source (key = Norm(path)) captured when its text was (re)loaded;
    // /sync diffs these against disk to find files edited since. _projStamp = same for csproj files.
    static readonly ConcurrentDictionary<string, (string path, DateTime mtime)> _fileStamp = new();
    static readonly ConcurrentDictionary<string, DateTime> _projStamp = new();
    static readonly object _updateLock = new();   // serializes read-modify-write swaps of _sol

    static readonly SymbolDisplayFormat Fmt = SymbolDisplayFormat.MinimallyQualifiedFormat;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string? root, string? csproj, int port)
    {
        _root = root; _csproj = csproj;
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"[serve] listening on http://127.0.0.1:{port}  (model warming up...)");
        _ = Task.Run(() => Build());
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    // ===== model build =====
    class Parsed { public string Path = "", Name = ""; public List<string> Sources = new(), HintRefs = new(), ProjRefs = new(), Defines = new(); }
    static string Norm(string p) => Path.GetFullPath(p).ToLowerInvariant();
    // base for resolving relative ?file= inputs (project root, or the single csproj's dir)
    static string BaseDir() => _root ?? (_csproj != null ? Path.GetDirectoryName(Path.GetFullPath(_csproj))! : Directory.GetCurrentDirectory());
    static string ResolveInput(string f) => Path.IsPathRooted(f) ? Path.GetFullPath(f) : Path.GetFullPath(Path.Combine(BaseDir(), f));
    static readonly Dictionary<string, MetadataReference> _metaCache = new();
    static readonly CSharpCompilationOptions CompOpts = new(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true);
    static readonly CSharpParseOptions ParseOpts = new(LanguageVersion.Latest);

    static Parsed ParseCsproj(string csprojPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(csprojPath))!;
        var x = XDocument.Load(csprojPath);
        string Resolve(string rel) => Path.GetFullPath(Path.Combine(dir, rel.Replace('\\', Path.DirectorySeparatorChar)));
        return new Parsed
        {
            Path = Path.GetFullPath(csprojPath),
            Name = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "AssemblyName")?.Value.Trim()
                   ?? Path.GetFileNameWithoutExtension(csprojPath),
            Sources = x.Descendants().Where(e => e.Name.LocalName == "Compile")
                .Select(e => (string?)e.Attribute("Include")).Where(s => !string.IsNullOrEmpty(s))
                .Select(s => Resolve(s!)).Where(File.Exists).Distinct().ToList(),
            HintRefs = x.Descendants().Where(e => e.Name.LocalName == "HintPath")
                .Select(e => e.Value.Trim()).Where(File.Exists).Distinct().ToList(),
            ProjRefs = x.Descendants().Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => (string?)e.Attribute("Include")).Where(s => !string.IsNullOrEmpty(s))
                .Select(s => Resolve(s!)).ToList(),
            // union of all <DefineConstants> (Unity puts UNITY_EDITOR and project-defined symbols here);
            // without these, all #if regions are parsed as excluded and their references become invisible.
            Defines = x.Descendants().Where(e => e.Name.LocalName == "DefineConstants")
                .SelectMany(e => e.Value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith("$(")).Distinct().ToList()
        };
    }
    static MetadataReference? Meta(string path)
    {
        var key = Norm(path);
        lock (_metaCache)
        {
            if (_metaCache.TryGetValue(key, out var m)) return m;
            try { m = MetadataReference.CreateFromFile(path); } catch { m = null; }
            _metaCache[key] = m!; return m;
        }
    }

    static void Build()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            _ready = false; _status = "loading";
            Console.WriteLine($"[serve] loading: parsing csproj + reading sources ({_root ?? _csproj}) ...");
            List<Parsed> parsed;
            if (_root != null)
            {
                _scope = "cross-assembly (all csproj)";
                parsed = new();
                foreach (var f in Directory.GetFiles(_root, "*.csproj"))
                    try { parsed.Add(ParseCsproj(f)); } catch (Exception e) { Console.WriteLine($"[serve] skip {Path.GetFileName(f)}: {e.Message}"); }
            }
            else { _scope = "single: " + Path.GetFileNameWithoutExtension(_csproj!); parsed = new() { ParseCsproj(_csproj!) }; }
            Console.WriteLine($"[serve] parsed {parsed.Count} csproj ({sw.ElapsedMilliseconds}ms) — reading source files...");

            // stamp csproj + source mtimes BEFORE reading text: a write landing in between makes the
            // stamp older than the loaded text, so /sync re-reads it — errs on the safe side.
            _projStamp.Clear(); _fileStamp.Clear();
            foreach (var p in parsed)
            {
                try { _projStamp[Norm(p.Path)] = File.GetLastWriteTimeUtc(p.Path); } catch { }
                foreach (var f in p.Sources)
                {
                    var k = Norm(f);
                    if (!_fileStamp.ContainsKey(k))
                        try { _fileStamp[k] = (f, File.GetLastWriteTimeUtc(f)); } catch { }
                }
            }

            var idByPath = parsed.ToDictionary(p => Norm(p.Path), _ => ProjectId.CreateNewId());
            var projInfos = new List<ProjectInfo>();
            int docs = 0;
            // single mode: also pull ScriptAssemblies DLLs as metadata for dependency resolution
            string[] saExtra = Array.Empty<string>();
            if (_root == null)
            {
                string sa = Path.Combine(Path.GetDirectoryName(parsed[0].Path)!, "Library", "ScriptAssemblies");
                if (Directory.Exists(sa)) saExtra = Directory.GetFiles(sa, "*.dll");
            }
            int readProj = 0;
            foreach (var p in parsed)
            {
                var id = idByPath[Norm(p.Path)];
                var metaRefs = p.HintRefs.Concat(saExtra)
                    .GroupBy(x => Path.GetFileName(x).ToLowerInvariant()).Select(g => g.First())
                    .Select(Meta).Where(m => m != null).Select(m => m!).ToList();
                var projRefs = p.ProjRefs.Select(Norm).Where(idByPath.ContainsKey)
                    .Select(rp => new ProjectReference(idByPath[rp])).Distinct().ToList();
                var docInfos = p.Sources.Select(f => DocumentInfo.Create(
                    DocumentId.CreateNewId(id), Path.GetFileName(f),
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From(File.ReadAllText(f)), VersionStamp.Create(), f)),
                    filePath: f)).ToList();
                docs += docInfos.Count;
                var parseOpts = p.Defines.Count > 0 ? ParseOpts.WithPreprocessorSymbols(p.Defines) : ParseOpts;
                projInfos.Add(ProjectInfo.Create(id, VersionStamp.Create(), p.Name, p.Name, LanguageNames.CSharp,
                    filePath: p.Path, compilationOptions: CompOpts, parseOptions: parseOpts,
                    documents: docInfos, projectReferences: projRefs, metadataReferences: metaRefs));
                if (++readProj % 20 == 0) Console.WriteLine($"[serve]   read {readProj}/{parsed.Count} projects ({docs} docs, {sw.ElapsedMilliseconds}ms)");
            }
            var ws = new AdhocWorkspace();
            ws.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(), projects: projInfos));
            lock (_updateLock) _sol = ws.CurrentSolution;   // don't interleave with a /reload//sync swap
            _projCount = projInfos.Count; _docCount = docs;
            Console.WriteLine($"[serve] loaded {_projCount} projects, {_docCount} docs ({sw.ElapsedMilliseconds}ms) — building compilations...");
            int built = 0;
            foreach (var proj in _sol!.Projects)
            {
                proj.GetCompilationAsync().GetAwaiter().GetResult();
                if (++built % 20 == 0) Console.WriteLine($"[serve]   built {built}/{_projCount}");
            }
            _buildMs = sw.ElapsedMilliseconds; _ready = true; _status = "ready";
            Console.WriteLine($"[serve] model READY [{_scope}]: {_projCount} projects, {_docCount} docs, built in {_buildMs}ms. Queries now instant.");
        }
        catch (Exception ex) { _status = "error: " + ex.Message; Console.WriteLine("[serve] build failed: " + ex); }
    }

    // ===== symbol resolution =====
    static string ShortName(string s) => s.Contains('.') ? s[(s.LastIndexOf('.') + 1)..] : s;

    static async Task<List<ISymbol>> ResolveByName(Solution sol, string symbol)
    {
        var decls = (await SymbolFinder.FindSourceDeclarationsAsync(sol, ShortName(symbol), ignoreCase: false))
            .Where(s => s.Kind is SymbolKind.NamedType or SymbolKind.Method or SymbolKind.Property
                       or SymbolKind.Field or SymbolKind.Event).ToList();
        if (symbol.Contains('.'))
            decls = decls.Where(s => s.ToDisplayString() == symbol || s.ToDisplayString().EndsWith("." + symbol)).ToList();
        // types first (most common intent for a bare name), then members
        return decls.OrderBy(s => s.Kind == SymbolKind.NamedType ? 0 : 1).ToList();
    }

    static async Task<ISymbol?> SymbolAt(Solution sol, string file, int line, int col)
    {
        var full = ResolveInput(file);
        var docId = sol.GetDocumentIdsWithFilePath(full).FirstOrDefault();
        if (docId == null)
            foreach (var pr in sol.Projects)
                foreach (var d in pr.Documents)
                    if (d.FilePath != null && Norm(d.FilePath) == Norm(full)) { docId = d.Id; break; }
        if (docId == null) return null;
        var doc = sol.GetDocument(docId)!;
        var text = await doc.GetTextAsync();
        if (line < 1 || line > text.Lines.Count) return null;
        int pos = Math.Min(text.Length - 1, text.Lines[line - 1].Start + Math.Max(0, col - 1));
        var root = await doc.GetSyntaxRootAsync();
        var model = await doc.GetSemanticModelAsync();
        var token = root!.FindToken(pos);
        var innermost = token.Parent;
        if (innermost == null) return null;
        // cursor sits on a declaration's name?
        var declared = model!.GetDeclaredSymbol(innermost);
        if (declared != null && innermost is Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.ParameterSyntax)
            return declared;
        // otherwise resolve the REFERENCED symbol by climbing name/type/expression syntax
        for (var n = innermost; n is Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax
                                  or Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax; n = n!.Parent)
        {
            var si = model.GetSymbolInfo(n);
            var s = si.Symbol ?? si.CandidateSymbols.FirstOrDefault();
            if (s != null) return s;
        }
        return declared;
    }

    // resolve targets from query: prefer file/line/col, else symbol name
    static async Task<List<ISymbol>> ResolveTargets(Solution sol, string query)
    {
        string file = ParseQuery(query, "file");
        if (!string.IsNullOrEmpty(file)
            && int.TryParse(ParseQuery(query, "line"), out var line)
            && int.TryParse(ParseQuery(query, "col"), out var col))
        {
            var s = await SymbolAt(sol, file, line, col);
            return s != null ? new() { s } : new();
        }
        return await ResolveByName(sol, ParseQuery(query, "symbol"));
    }

    // ===== JSON helpers =====
    static object LocOf(Location l) { var sp = l.GetLineSpan(); return new { file = l.SourceTree?.FilePath, line = sp.StartLinePosition.Line + 1, col = sp.StartLinePosition.Character + 1 }; }
    static List<object> SrcLocs(ISymbol s) => s.Locations.Where(l => l.IsInSource).Select(LocOf).ToList();
    static object SymJson(ISymbol s) => new { name = s.ToDisplayString(Fmt), full = s.ToDisplayString(), kind = s.Kind.ToString(), assembly = s.ContainingAssembly?.Name, locations = SrcLocs(s) };

    // ===== endpoint handlers =====
    static async Task<(int, string)> Dispatch(string path, string query)
    {
        if (path == "/health")
            return (200, JsonSerializer.Serialize(new { status = _status, ready = _ready, scope = _scope, projects = _projCount, docs = _docCount, buildMs = _buildMs }, Json));
        if (path == "/rescan") { Console.WriteLine("[rescan] full model rebuild requested"); _ready = false; _ = Task.Run(() => Build()); return (200, JsonSerializer.Serialize(new { status = "rebuilding" })); }
        if (!_ready || _sol == null) return (503, JsonSerializer.Serialize(new { status = _status, ready = false }));
        var sol = _sol;

        switch (path)
        {
            case "/reload": return (200, await Reload(sol, ParseQuery(query, "file")));
            case "/sync": return (200, Sync(sol));
            case "/findrefs": return (200, await FindRefs(sol, query));
            case "/definition": return (200, await Definition(sol, query));
            case "/hover": return (200, await Hover(sol, query));
            case "/outline": return (200, await Outline(sol, ParseQuery(query, "file")));
            case "/symbols": return (200, await Symbols(sol, ParseQuery(query, "query"), ParseQuery(query, "limit")));
            case "/implementations": return (200, await RelatedSymbols(sol, query, "implementations"));
            case "/overrides": return (200, await RelatedSymbols(sol, query, "overrides"));
            case "/derived": return (200, await RelatedSymbols(sol, query, "derived"));
            case "/callers": return (200, await Callers(sol, query));
            case "/hierarchy": return (200, await Hierarchy(sol, query));
            default: return (404, JsonSerializer.Serialize(new { error = "unknown path", path }));
        }
    }

    static async Task<string> Reload(Solution sol, string file)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(file)) return JsonSerializer.Serialize(new { error = "missing ?file=" });
        var full = ResolveInput(file);
        if (!File.Exists(full)) return JsonSerializer.Serialize(new { file = full, error = "file not found on disk" });
        var ids = sol.GetDocumentIdsWithFilePath(full).ToList();
        if (ids.Count == 0)
            foreach (var pr in sol.Projects)
                foreach (var d in pr.Documents)
                    if (d.FilePath != null && Norm(d.FilePath) == Norm(full)) ids.Add(d.Id);
        if (ids.Count == 0)
            return JsonSerializer.Serialize(new { file = full, updated = 0, note = "not in model; if newly added, call /rescan" });
        var mtime = File.GetLastWriteTimeUtc(full);
        var text = SourceText.From(File.ReadAllText(full));
        int applied = 0;
        lock (_updateLock)
        {
            var s = _sol!;
            // ids may come from a snapshot replaced by a concurrent /rescan — apply only ids the
            // CURRENT solution still contains, so we never throw or clobber a fresh rebuild
            foreach (var id in ids) if (s.ContainsDocument(id)) { s = s.WithDocumentText(id, text); applied++; }
            if (applied > 0) _sol = s;
        }
        if (applied == 0)
            return JsonSerializer.Serialize(new { file = full, updated = 0, note = "model was rebuilt while reloading; retry" });
        _fileStamp[Norm(full)] = (full, mtime);
        return JsonSerializer.Serialize(new { file = full, updatedDocs = applied, ms = sw.ElapsedMilliseconds });
    }

    // reload EVERY indexed file whose on-disk mtime differs from the loaded snapshot — callers
    // don't need to know which files were edited (works regardless of git/submodules/tooling).
    static string Sync(Solution sol)
    {
        var sw = Stopwatch.StartNew();

        // csproj set/mtime changed → <Compile> lists may be stale (Unity regenerates csproj on
        // add/delete) → flag needRescan; /sync itself only refreshes files already in the model.
        var changedProjects = new List<string>();
        bool projectSetChanged = false;
        try
        {
            var onDisk = (_root != null ? Directory.GetFiles(_root, "*.csproj") : new[] { Path.GetFullPath(_csproj!) })
                .ToDictionary(Norm, File.GetLastWriteTimeUtc);
            foreach (var kv in onDisk)
                if (!_projStamp.TryGetValue(kv.Key, out var seen)) projectSetChanged = true;
                else if (seen != kv.Value) changedProjects.Add(Path.GetFileName(kv.Key));
            foreach (var k in _projStamp.Keys)
                if (!onDisk.ContainsKey(k)) projectSetChanged = true;
        }
        catch { }

        // linked docs share one physical path — map path → every DocumentId once, not O(docs) per file
        var byPath = new Dictionary<string, List<DocumentId>>();
        foreach (var pr in sol.Projects)
            foreach (var d in pr.Documents)
                if (d.FilePath != null)
                {
                    var k = Norm(d.FilePath);
                    if (!byPath.TryGetValue(k, out var l)) byPath[k] = l = new();
                    l.Add(d.Id);
                }

        var reloaded = new List<string>(); var missing = new List<string>(); var failed = new List<string>();
        foreach (var kv in _fileStamp)
        {
            string norm = kv.Key, path = kv.Value.path;
            DateTime cur;
            try
            {
                if (!File.Exists(path)) { missing.Add(path); continue; }
                cur = File.GetLastWriteTimeUtc(path);
            }
            catch { failed.Add(path); continue; }
            if (cur == kv.Value.mtime) continue;
            if (!byPath.TryGetValue(norm, out var ids)) continue;
            try
            {
                var text = SourceText.From(File.ReadAllText(path));
                int applied = 0;
                lock (_updateLock)
                {
                    var s = _sol!;
                    // same guard as Reload: a concurrent /rescan invalidates snapshot DocumentIds
                    foreach (var id in ids) if (s.ContainsDocument(id)) { s = s.WithDocumentText(id, text); applied++; }
                    if (applied > 0) _sol = s;
                }
                if (applied == 0) { failed.Add(path); continue; }
                _fileStamp[norm] = (path, cur);
                reloaded.Add(path);
            }
            catch { failed.Add(path); }
        }

        bool needRescan = projectSetChanged || changedProjects.Count > 0 || missing.Count > 0;
        return JsonSerializer.Serialize(new
        {
            reloaded = reloaded.Count,
            files = reloaded,
            missing,            // indexed but no longer on disk (model keeps the stale copy)
            failed,             // not applied this pass (locked mid-write / rebuilt mid-sync) — call /sync again
            changedProjects,    // csproj mtime changed → file lists may differ from the model
            needRescan,
            note = needRescan ? "project structure changed on disk — call /rescan to re-index" : null,
            ms = sw.ElapsedMilliseconds
        }, Json);
    }

    static async Task<string> FindRefs(Solution sol, string query)
    {
        var sw = Stopwatch.StartNew();
        var decls = await ResolveTargets(sol, query);
        if (decls.Count == 0) return JsonSerializer.Serialize(new { count = 0, message = "no symbol found" });

        // A logical declaration = one source file:line. Linked copies across assemblies share it.
        // Genuinely different types that merely share a name have DIFFERENT keys.
        static string DeclKey(ISymbol s)
        {
            var l = s.Locations.FirstOrDefault(x => x.IsInSource);
            if (l == null) return s.ToDisplayString();
            var sp = l.GetLineSpan();
            return $"{l.SourceTree?.FilePath?.ToLowerInvariant()}|{sp.StartLinePosition.Line}";
        }

        var types = decls.Where(s => s.Kind == SymbolKind.NamedType).ToList();
        List<ISymbol> targets;
        if (types.Count > 0)
        {
            var groups = types.GroupBy(DeclKey).ToList();
            if (groups.Count > 1)   // name collision across distinct types → don't silently merge
            {
                var matches = groups.Select(g =>
                {
                    var s = g.First(); var loc = s.Locations.FirstOrDefault(x => x.IsInSource);
                    int ln = 0; string? fp = null;
                    if (loc != null) { var sp = loc.GetLineSpan(); ln = sp.StartLinePosition.Line + 1; fp = loc.SourceTree?.FilePath; }
                    return new { type = s.ToDisplayString(), assemblies = g.Select(x => x.ContainingAssembly?.Name).Distinct(), file = fp, line = ln };
                }).ToList();
                return JsonSerializer.Serialize(new
                {
                    symbol = ParseQuery(query, "symbol"),
                    ambiguous = true,
                    message = "multiple distinct types share this name; re-query with a fully-qualified name (e.g. Namespace.Type) or a cursor position (file+line+col)",
                    matches
                }, Json);
            }
            targets = types;   // all linked copies of the SAME type
        }
        else targets = decls.Take(16).ToList();   // members (e.g. method + overloads): merge

        var seen = new HashSet<string>(); var refs = new List<Ref>();
        foreach (var t in targets)
            foreach (var rf in await SymbolFinder.FindReferencesAsync(t, sol))
                foreach (var loc in rf.Locations)
                {
                    var sp = loc.Location.GetLineSpan();
                    int line = sp.StartLinePosition.Line + 1, col = sp.StartLinePosition.Character + 1;
                    var f = loc.Document.FilePath ?? loc.Document.Name;
                    if (seen.Add($"{f.ToLowerInvariant()}|{line}|{col}")) refs.Add(new Ref(loc.Document.Project.Name, f, line, col));
                }
        var primary = targets[0];
        return JsonSerializer.Serialize(new
        {
            symbol = primary.ToDisplayString(Fmt),
            kind = primary.Kind.ToString(),
            definedIn = string.Join(",", targets.Select(t => t.ContainingAssembly?.Name).Distinct()),
            count = refs.Count,
            files = refs.Select(r => r.file.ToLowerInvariant()).Distinct().Count(),
            ms = sw.ElapsedMilliseconds,
            references = refs.OrderBy(r => r.file).ThenBy(r => r.line).ToList()
        }, Json);
    }

    static async Task<string> Definition(Solution sol, string query)
    {
        var decls = await ResolveTargets(sol, query);
        if (decls.Count == 0) return JsonSerializer.Serialize(new { count = 0, message = "no symbol found" });
        var seen = new HashSet<string>(); var locs = new List<object>();
        foreach (var s in decls)
            foreach (var l in s.Locations.Where(x => x.IsInSource))
            {
                var sp = l.GetLineSpan(); var f = l.SourceTree?.FilePath;
                if (seen.Add($"{f?.ToLowerInvariant()}|{sp.StartLinePosition.Line}|{sp.StartLinePosition.Character}"))
                    locs.Add(new { file = f, line = sp.StartLinePosition.Line + 1, col = sp.StartLinePosition.Character + 1 });
            }
        return JsonSerializer.Serialize(new { symbol = decls[0].ToDisplayString(Fmt), kind = decls[0].Kind.ToString(), count = locs.Count, definitions = locs }, Json);
    }

    static async Task<string> Hover(Solution sol, string query)
    {
        var decls = await ResolveTargets(sol, query);
        if (decls.Count == 0) return JsonSerializer.Serialize(new { message = "no symbol found" });
        var s = decls[0];
        var nt = s as INamedTypeSymbol;
        string? ret = (s as IMethodSymbol)?.ReturnType.ToDisplayString(Fmt)
                    ?? (s as IPropertySymbol)?.Type.ToDisplayString(Fmt)
                    ?? (s as IFieldSymbol)?.Type.ToDisplayString(Fmt);
        return JsonSerializer.Serialize(new
        {
            display = s.ToDisplayString(Fmt),
            kind = s.Kind.ToString(),
            accessibility = s.DeclaredAccessibility.ToString(),
            containingType = s.ContainingType?.ToDisplayString(Fmt),
            @namespace = s.ContainingNamespace?.ToDisplayString(),
            assembly = s.ContainingAssembly?.Name,
            returnType = ret,
            baseType = nt?.BaseType?.ToDisplayString(Fmt),
            interfaces = nt?.Interfaces.Select(i => i.ToDisplayString(Fmt)).ToList(),
            doc = (s.GetDocumentationCommentXml() ?? "").Trim(),
            candidates = decls.Count,
            definition = SrcLocs(s)
        }, Json);
    }

    static async Task<string> Outline(Solution sol, string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return JsonSerializer.Serialize(new { error = "missing ?file=" });
        var full = ResolveInput(file);
        var docId = sol.GetDocumentIdsWithFilePath(full).FirstOrDefault();
        if (docId == null) return JsonSerializer.Serialize(new { file = full, error = "not in model" });
        var doc = sol.GetDocument(docId)!;
        var root = await doc.GetSyntaxRootAsync();
        var model = await doc.GetSemanticModelAsync();
        var items = new List<object>();
        foreach (var node in root!.DescendantNodes())
        {
            ISymbol? sym = node switch
            {
                Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.DelegateDeclarationSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.EventDeclarationSyntax => model!.GetDeclaredSymbol(node),
                _ => null
            };
            if (sym == null) continue;
            var sp = node.GetLocation().GetLineSpan();
            items.Add(new { kind = sym.Kind.ToString(), name = sym.ToDisplayString(Fmt), container = sym.ContainingSymbol?.ToDisplayString(Fmt), line = sp.StartLinePosition.Line + 1 });
        }
        return JsonSerializer.Serialize(new { file = full, count = items.Count, symbols = items }, Json);
    }

    static async Task<string> Symbols(Solution sol, string q, string limitStr)
    {
        if (string.IsNullOrWhiteSpace(q)) return JsonSerializer.Serialize(new { error = "missing ?query=" });
        int limit = int.TryParse(limitStr, out var l) ? l : 50;
        var found = await SymbolFinder.FindSourceDeclarationsAsync(sol,
            n => n.Contains(q, StringComparison.OrdinalIgnoreCase),
            SymbolFilter.TypeAndMember);
        var seen = new HashSet<string>(); var results = new List<object>();
        foreach (var s in found)
        {
            var key = s.ToDisplayString();
            if (!seen.Add(key)) continue;
            results.Add(SymJson(s));
            if (results.Count >= limit) break;
        }
        return JsonSerializer.Serialize(new { query = q, count = results.Count, symbols = results }, Json);
    }

    static async Task<string> RelatedSymbols(Solution sol, string query, string kind)
    {
        var decls = await ResolveTargets(sol, query);
        if (decls.Count == 0) return JsonSerializer.Serialize(new { count = 0, message = "no symbol found" });
        var target = decls.FirstOrDefault(s => s.Kind == SymbolKind.NamedType) ?? decls[0];
        IEnumerable<ISymbol> res = kind switch
        {
            "implementations" => await SymbolFinder.FindImplementationsAsync(target, sol),
            "overrides" => await SymbolFinder.FindOverridesAsync(target, sol),
            "derived" when target is INamedTypeSymbol nt => (await SymbolFinder.FindDerivedClassesAsync(nt, sol)).Cast<ISymbol>(),
            _ => Enumerable.Empty<ISymbol>()
        };
        var list = res.ToList();
        return JsonSerializer.Serialize(new { symbol = target.ToDisplayString(Fmt), kind, count = list.Count, results = list.Select(SymJson).ToList() }, Json);
    }

    static async Task<string> Callers(Solution sol, string query)
    {
        var decls = await ResolveTargets(sol, query);
        var target = decls.FirstOrDefault(s => s.Kind == SymbolKind.Method) ?? decls.FirstOrDefault();
        if (target == null) return JsonSerializer.Serialize(new { count = 0, message = "no method symbol found" });
        var callers = await SymbolFinder.FindCallersAsync(target, sol);
        // dedup by caller + physical location (linked docs across assemblies duplicate callers)
        var byCaller = new Dictionary<string, (string kind, HashSet<string> keys, List<object> locs)>();
        foreach (var c in callers)
        {
            var name = c.CallingSymbol.ToDisplayString(Fmt);
            if (!byCaller.TryGetValue(name, out var e)) { e = (c.CallingSymbol.Kind.ToString(), new(), new()); byCaller[name] = e; }
            foreach (var l in c.Locations.Where(x => x.IsInSource))
            {
                var sp = l.GetLineSpan();
                if (e.keys.Add($"{l.SourceTree?.FilePath?.ToLowerInvariant()}|{sp.StartLinePosition.Line}|{sp.StartLinePosition.Character}"))
                    e.locs.Add(LocOf(l));
            }
        }
        var items = byCaller.Select(kv => new { caller = kv.Key, callerKind = kv.Value.kind, count = kv.Value.locs.Count, locations = kv.Value.locs }).ToList();
        return JsonSerializer.Serialize(new { symbol = target.ToDisplayString(Fmt), callerCount = items.Count, callers = items }, Json);
    }

    static async Task<string> Hierarchy(Solution sol, string query)
    {
        var decls = await ResolveTargets(sol, query);
        var nt = decls.OfType<INamedTypeSymbol>().FirstOrDefault();
        if (nt == null) return JsonSerializer.Serialize(new { message = "no named type found" });
        var bases = new List<string>();
        for (var b = nt.BaseType; b != null && b.SpecialType != SpecialType.System_Object; b = b.BaseType)
            bases.Add(b.ToDisplayString(Fmt));
        var derived = (await SymbolFinder.FindDerivedClassesAsync(nt, sol)).ToList();
        return JsonSerializer.Serialize(new
        {
            type = nt.ToDisplayString(Fmt),
            baseChain = bases,
            interfaces = nt.AllInterfaces.Select(i => i.ToDisplayString(Fmt)).ToList(),
            derived = derived.Select(SymJson).ToList()
        }, Json);
    }

    // ===== HTTP plumbing =====
    static async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            var reqLine = await ReadRequestHeadAsync(stream);
            string path = "/", query = "";
            var parts = reqLine.Split(' ');
            if (parts.Length >= 2)
            {
                var url = parts[1]; int q = url.IndexOf('?');
                path = q >= 0 ? url[..q] : url; query = q >= 0 ? url[(q + 1)..] : "";
            }
            int code = 200; string json;
            try { (code, json) = await Dispatch(path, query); }
            catch (Exception ex) { code = 500; json = JsonSerializer.Serialize(new { error = ex.Message }); }
            await WriteResponseAsync(stream, code, json);
        }
    }

    static string ParseQuery(string query, string key)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            var k = eq >= 0 ? pair[..eq] : pair;
            if (k == key) return WebUtility.UrlDecode(eq >= 0 ? pair[(eq + 1)..] : "");
        }
        return "";
    }

    static async Task<string> ReadRequestHeadAsync(NetworkStream s)
    {
        var sb = new StringBuilder(); var one = new byte[1];
        while (await s.ReadAsync(one, 0, 1) > 0)
        {
            sb.Append((char)one[0]);
            if (sb.Length >= 4 && sb[^1] == '\n' && sb[^2] == '\r' && sb[^3] == '\n' && sb[^4] == '\r') break;
            if (sb.Length > 16000) break;
        }
        int nl = sb.ToString().IndexOf('\n');
        return (nl >= 0 ? sb.ToString()[..nl] : sb.ToString()).TrimEnd('\r');
    }

    static async Task WriteResponseAsync(NetworkStream s, int code, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {code} {(code == 200 ? "OK" : "ERR")}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        var hb = Encoding.ASCII.GetBytes(head);
        await s.WriteAsync(hb, 0, hb.Length);
        await s.WriteAsync(bytes, 0, bytes.Length);
        await s.FlushAsync();
        try { s.Socket.Shutdown(SocketShutdown.Both); } catch { }
    }
}

record Ref(string project, string file, int line, int col);

// ---------------------------------------------------------------------------
static class Client
{
    public static async Task<int> QueryAsync(int port, string symbol)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            using var s = tcp.GetStream();
            var req = $"GET /findrefs?symbol={Uri.EscapeDataString(symbol)} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n";
            var rb = Encoding.ASCII.GetBytes(req);
            await s.WriteAsync(rb, 0, rb.Length);
            using var reader = new StreamReader(s, Encoding.UTF8);
            var all = await reader.ReadToEndAsync();
            int idx = all.IndexOf("\r\n\r\n");
            Console.WriteLine(idx >= 0 ? all[(idx + 4)..] : all);
            return 0;
        }
        catch (SocketException)
        {
            Console.Error.WriteLine($"cannot reach server on 127.0.0.1:{port} — start it with:  serve --root <dir> --port {port}");
            return 1;
        }
    }
}
