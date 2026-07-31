using System.Collections.Immutable;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Interpreter;

namespace Shumway.Embedding;

public sealed partial class PrologEngine
{
    /// <summary>Loads the CLP(FD) constraint library into this
    /// engine, making the finite-domain constraints — <c>#=</c>, <c>#\=</c>,
    /// <c>#&lt;</c>, <c>#&gt;</c>, <c>#=&lt;</c>, <c>#&gt;=</c>, <c>in</c>,
    /// <c>ins</c> — and their operators available to subsequently consulted
    /// source and queries. CLP(FD) is opt-in: an engine that never calls
    /// this carries none of the library's weight.</summary>
    public void UseClpfd()
    {
        ConsultString(Clpfd.Source);
        MarkModuleNonDebuggable(Clpfd.ModuleName);   // ADR-035 — a library, not the user's code
    }

    /// <summary>Loads the CLP(R) constraint library into this
    /// engine, making linear-equality constraints over the reals available
    /// through the <c>{Constraint}</c> wrapper. CLP(R) is opt-in: an engine
    /// that never calls this carries none of the library's weight.
    ///
    /// <para>CLP(R) and CLP(FD) can share an engine — both declare their
    /// <c>verify_attributes/4</c> hook <c>:- multifile</c> — as long as no
    /// variable carries both libraries' constraints.</para></summary>
    public void UseClpr()
    {
        ConsultString(Clpr.Source);
        MarkModuleNonDebuggable(Clpr.ModuleName);   // ADR-035 — a library, not the user's code
    }

    /// <summary>Loads the coroutining library into this engine:
    /// <c>freeze/2</c>, <c>frozen/2</c> and the <c>dif/2</c> disequality
    /// constraint. Opt-in like the CLP libraries, and built on the same
    /// multifile <c>verify_attributes/4</c> hook, so it coexists with
    /// CLP(FD)/CLP(R) on one engine.</summary>
    public void UseCoroutining()
    {
        ConsultString(Coroutining.Source);
        MarkModuleNonDebuggable(Coroutining.ModuleName);   // ADR-035 — a library, not the user's code
    }

    // Compatibility libraries loaded on demand by use_module(library(Name)),
    // tracked so a repeated import (or a program that imports the same library
    // as one of its dependencies) does not re-consult and trip the
    // public-predicate uniqueness check.
    private readonly HashSet<string> _loadedCompatLibraries = new();

    /// <summary>Loads a built-in Scryer/Trealla compatibility library by name
    /// (see <see cref="CompatLibraries"/>), idempotently. Returns <c>true</c>
    /// if <paramref name="name"/> is a known compatibility library (whether it
    /// carries Prolog source or is a prelude-covered no-op), <c>false</c> for
    /// an unknown library name.</summary>
    // ADR-040 — the preferred dialect for resolving an ambiguous library name.
    // null = auto (no preference); coexistence still works because the registry
    // falls back to every pack, so a name unique to one dialect always resolves.
    private string? _activeLibraryDialect;

    /// <summary>Selects the preferred dialect (<c>scryer</c>, <c>swi</c>, …) for
    /// resolving a <c>use_module(library(X))</c> whose name two dialects both
    /// provide (ADR-040 explicit selection). Does NOT restrict loading: a library
    /// unique to another dialect still resolves (coexistence is the default), so
    /// a Scryer <c>clpz</c> and an SWI <c>http</c> load together regardless. Also
    /// settable from Prolog with <c>set_prolog_flag(library_dialect, swi)</c>.</summary>
    public void SetLibraryDialect(string dialect)
    {
        if (!DialectRegistry.IsKnownDialect(dialect))
            throw new System.ArgumentException($"unknown library dialect '{dialect}'");
        _activeLibraryDialect = dialect;
    }

    /// <summary>The active library dialect, or null for auto. Read by the
    /// <c>library_dialect</c> prolog flag.</summary>
    internal string? ActiveLibraryDialect => _activeLibraryDialect;

    // ADR-040 D5.2 — a search directory tagged with the dialect its libraries
    // are written in. Resolving library(X) from a tagged dir loads X (and its
    // pack-resolved dependency subtree) in that dialect, parsed with the
    // dialect's double_quotes. Keyed by normalised full directory path.
    private System.Collections.Generic.Dictionary<string, string>? _libraryDirDialect;

    /// <summary>Adds <paramref name="path"/> to the library search path AND tags
    /// it with a dialect (ADR-040 D5.2): a <c>use_module(library(X))</c> that
    /// resolves <c>X</c> from here loads it in <paramref name="dialect"/> — the
    /// dir's dialect becomes active (name resolution + double_quotes) for that
    /// load. Pointing <c>-L</c> at a Scryer checkout as <c>scryer</c> and an SWI
    /// one as <c>swi</c> lets both systems' libraries load, each correctly.</summary>
    public void AddLibraryDirectory(string path, string dialect)
    {
        if (!DialectRegistry.IsKnownDialect(dialect))
            throw new System.ArgumentException($"unknown library dialect '{dialect}'");
        AddLibraryDirectory(path);
        string full;
        try { full = System.IO.Path.GetFullPath(path); } catch { full = path; }
        (_libraryDirDialect ??= new(System.StringComparer.OrdinalIgnoreCase))[full] = dialect;
    }

    // The dialect a resolved library path belongs to (its directory's tag), or
    // null when the directory is untagged.
    private string? DialectForResolvedPath(string resolvedPath)
    {
        if (_libraryDirDialect is null) return null;
        string? dir = System.IO.Path.GetDirectoryName(resolvedPath);
        if (dir is null) return null;
        try { dir = System.IO.Path.GetFullPath(dir); } catch { /* use as-is */ }
        return _libraryDirDialect.TryGetValue(dir, out string? d) ? d : null;
    }

    // Runs <paramref name="body"/> with <paramref name="dialect"/> active — its
    // name resolution preferred and its double_quotes in force — restoring both
    // after. The subtree a library pulls in inherits the dialect for the load.
    private T WithDialect<T>(string dialect, System.Func<T> body)
    {
        string? savedDialect = _activeLibraryDialect;
        var savedDq = Flags.DoubleQuotes;
        _activeLibraryDialect = dialect;
        Flags.DoubleQuotes = DialectRegistry.DoubleQuotesOf(dialect);
        try { return body(); }
        finally { _activeLibraryDialect = savedDialect; Flags.DoubleQuotes = savedDq; }
    }

    internal bool UseCompatLibrary(string name)
    {
        if (!DialectRegistry.TryResolve(_activeLibraryDialect, name,
                out string source, out var doubleQuotes, out _))
            return false;
        // recordInHistory:false — the importing program's own source (which
        // carries the use_module directive) is what SaveState replays; the
        // directive re-loads the library on restore, so recording the library
        // body too would double-consult it (and trip public uniqueness).
        if (_loadedCompatLibraries.Add(name) && source.Length > 0)
        {
            // ADR-040 Component 4 — parse the shim with its dialect's
            // double_quotes (Scryer = chars, SWI = codes), then restore, so two
            // dialects' libraries parse correctly in the same engine.
            var savedDq = Flags.DoubleQuotes;
            Flags.DoubleQuotes = doubleQuotes;
            try { ConsultStringInner(source, recordInHistory: false); }
            finally { Flags.DoubleQuotes = savedDq; }
        }
        return true;
    }

    // ADR-038 — the ordered library search path. Directories come from (in this
    // precedence) the file_search_path(library, Dir) / library_directory(Dir)
    // dynamic facts, the AddLibraryDirectory API, the SHUMWAY_LIBRARY_PATH env
    // var, and (added by the REPL/CLI) the shipped lib/ directory. Lazily built
    // so the env read happens once, on first library resolution.
    private List<string>? _libraryDirs;

    private void EnsureLibraryDirs()
    {
        if (_libraryDirs is not null) return;
        _libraryDirs = new List<string>();
        string? env = Environment.GetEnvironmentVariable("SHUMWAY_LIBRARY_PATH");
        if (!string.IsNullOrEmpty(env))
            foreach (string d in env.Split(System.IO.Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                // Each entry may carry a :dialect tag (ADR-040 D5.2).
                AddLibraryDirectorySpec(d);
    }

    private void AddLibraryDirNormalized(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string full;
        try { full = System.IO.Path.GetFullPath(path); } catch { full = path; }
        if (!_libraryDirs!.Contains(full, StringComparer.OrdinalIgnoreCase))
            _libraryDirs!.Add(full);
    }

    /// <summary>Adds <paramref name="path"/> to this engine's library search
    /// path (ADR-038), so a later <c>use_module(library(X))</c> can resolve
    /// <c>X.pl</c> / <c>X.shum</c> under it. Idempotent; the directory need not
    /// exist yet.</summary>
    public void AddLibraryDirectory(string path)
    {
        EnsureLibraryDirs();
        AddLibraryDirNormalized(path);
    }

    /// <summary>Adds a library directory from a CLI/env spec that MAY carry a
    /// dialect tag as a trailing <c>:dialect</c> (ADR-040 D5.2) — e.g.
    /// <c>C:/Scryer/lib:scryer</c> or <c>/opt/swipl/library:swi</c>. The tag is
    /// recognised only when the text after the LAST colon is a known dialect, so
    /// a plain Windows path (<c>C:/foo</c>) or an untagged dir is unaffected. The
    /// same spec form is accepted in <c>SHUMWAY_LIBRARY_PATH</c> entries and the
    /// REPL/CLI <c>-L</c> flag.</summary>
    public void AddLibraryDirectorySpec(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return;
        int colon = spec.LastIndexOf(':');
        if (colon > 0)
        {
            string suffix = spec[(colon + 1)..];
            if (DialectRegistry.IsKnownDialect(suffix))
            {
                AddLibraryDirectory(spec[..colon], suffix);
                return;
            }
        }
        AddLibraryDirectory(spec);
    }

    /// <summary>Adds Shumway's shipped default library directories to the search
    /// path (ADR-038): a <c>lib/</c> folder beside the running executable (where
    /// the REPL/CLI's copy of the repo <c>lib/</c> lands, and where <c>--exe</c>
    /// deploys it) and, if different, a <c>lib/</c> under the current directory.
    /// The REPL/CLIs call this at startup so <c>use_module(library(X))</c> finds
    /// the bundled libraries with no configuration.</summary>
    public void AddDefaultLibraryDirectories()
    {
        AddLibraryDirIfExists(System.IO.Path.Combine(AppContext.BaseDirectory, "lib"));
        AddLibraryDirIfExists(System.IO.Path.Combine(
            System.IO.Directory.GetCurrentDirectory(), "lib"));
    }

    private void AddLibraryDirIfExists(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path)) AddLibraryDirectory(path);
        }
        catch { /* an inaccessible probe path is simply skipped */ }
    }

    // The library directories in resolution order: dynamic facts first (so a
    // program's own :- file_search_path / library_directory wins), then the
    // API/env/shipped dirs.
    private IEnumerable<string> EnumerateLibraryDirs()
    {
        int fsp = FunctorTable.Intern(AtomTable.Intern("file_search_path").Id, 2);
        if (_dynStore.TryGetClauses(fsp, out var fspClauses))
            foreach (Clause cl in fspClauses)
                if (cl.Term is CompoundTerm { Functor: "file_search_path",
                        Args: [AtomTerm { Name: "library" }, var d] }
                    && TryDirText(d, out string dir))
                    yield return dir;

        int ld = FunctorTable.Intern(AtomTable.Intern("library_directory").Id, 1);
        if (_dynStore.TryGetClauses(ld, out var ldClauses))
            foreach (Clause cl in ldClauses)
                if (cl.Term is CompoundTerm { Functor: "library_directory", Args: [var d] }
                    && TryDirText(d, out string dir))
                    yield return dir;

        EnsureLibraryDirs();
        foreach (string d in _libraryDirs!) yield return d;
    }

    private static bool TryDirText(Term t, out string dir)
    {
        switch (t)
        {
            case AtomTerm a: dir = a.Name; return true;
            case StringTerm s: dir = s.Content; return true;
            default: dir = ""; return false;
        }
    }

    /// <summary>Resolves <c>library(<paramref name="name"/>)</c> to a file on the
    /// library search path (ADR-038): the first <c>Dir/name.pl</c> or
    /// <c>Dir/name.shum</c> that exists, in search-path order. Returns the full
    /// path in <paramref name="path"/>, or <c>false</c> if none is found.</summary>
    internal bool TryResolveLibrary(string name, out string path)
    {
        foreach (string dir in EnumerateLibraryDirs())
        {
            foreach (string ext in LibraryExtensions)
            {
                string candidate = System.IO.Path.Combine(dir, name + ext);
                try
                {
                    if (System.IO.File.Exists(candidate))
                    {
                        path = System.IO.Path.GetFullPath(candidate);
                        return true;
                    }
                }
                catch { /* an invalid path component — skip this candidate */ }
            }
        }
        path = "";
        return false;
    }

    private static readonly string[] LibraryExtensions = { ".pl", ".shum" };

    // ADR-038 — the module name the most recent consult defined, set at
    // manifest creation (ConsultPipeline). A nested library consult sets it, so
    // ExecuteUseModuleDirective reads it right after ConsultFile returns to learn
    // which module a use_module(library(X)) actually loaded (X.pl may declare a
    // module named other than X).
    internal string? _lastConsultedModuleName;

    // ADR-038 — resolved library path → the module its file defined, so a second
    // import of the same library (idempotent, not re-consulted) still yields the
    // module name for the importer's import table.
    private readonly Dictionary<string, string> _libraryModuleByPath =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Executes a <c>use_module/1</c> directive body.
    /// <c>library(Name)</c> loads a constraint/compatibility library or resolves
    /// a <c>.pl</c>/<c>.shum</c> on the search path; a plain atom names a file to
    /// consult. Returns the name of the loaded <em>export-qualified</em> module
    /// (ADR-038 — the importer builds its import table from this module's
    /// exports), or <c>null</c> for a legacy bare-global module, a baked library,
    /// or an unresolved/failed import. <paramref name="throwOnUnresolved"/>
    /// selects behaviour for an unknown library / missing file: the consult-time
    /// directive path warns and continues (<c>false</c>); the goal-form
    /// <c>use_module/1</c> builtin raises an ISO error (<c>true</c>).</summary>
    // Depth of use_module-driven loads in progress. A module file consulted
    // DIRECTLY (depth 0 — REPL command line, consult/1, embedding
    // ConsultFile/ConsultString) auto-imports its exports into `user`
    // (SWI behaviour); a dependency loaded via use_module only feeds the
    // IMPORTER's table.
    internal int _useModuleLoadDepth;

    internal string? ExecuteUseModuleDirective(Term spec, bool throwOnUnresolved = false)
    {
        _useModuleLoadDepth++;
        try { return ExecuteUseModuleDirectiveCore(spec, throwOnUnresolved); }
        finally { _useModuleLoadDepth--; }
    }

    private string? ExecuteUseModuleDirectiveCore(Term spec, bool throwOnUnresolved)
    {
        if (spec is CompoundTerm { Functor: "library", Args: [AtomTerm lib] })
        {
            switch (lib.Name)
            {
                // (1) baked C# libraries — take precedence, they carry native
                // hooks and stay bare-global (no import table).
                case "clpfd": UseClpfd(); return null;
                case "clpr":  UseClpr();  return null;
                case "coroutining": UseCoroutining(); return null;
                default:
                    // (2) ADR-038 — a .pl/.shum on the library search path.
                    if (TryResolveLibrary(lib.Name, out string libPath))
                    {
                        // ADR-040 D5.2 — a dir tagged with a dialect loads its
                        // libraries in that dialect (name resolution +
                        // double_quotes) for the whole subtree.
                        string? dirDialect = DialectForResolvedPath(libPath);
                        return dirDialect is not null
                            ? WithDialect(dirDialect, () => LoadResolvedLibrary(lib.Name, libPath))
                            : LoadResolvedLibrary(lib.Name, libPath);
                    }
                    // (3) built-in Scryer/Trealla compatibility table.
                    if (UseCompatLibrary(lib.Name)) return null;
                    // (4) genuinely unknown.
                    if (throwOnUnresolved)
                        throw new Shumway.Core.PrologRuntimeException(
                            $"existence_error(library, {lib.Name})");
                    Console.Error.WriteLine(
                        $"warning: unknown library '{lib.Name}' in use_module/1 — ignored");
                    return null;
            }
        }
        if (spec is AtomTerm fileAtom)
        {
            // Already-loaded module (e.g. consulted directly on the command
            // line, or imported earlier) — importing it again is a no-op, but
            // still yield its name so an importer can build its import table.
            if (_modules.ContainsKey(fileAtom.Name))
                return ExportQualifiedNameOrNull(fileAtom.Name);
            string path = fileAtom.Name;
            if (_consultBaseDir is not null && !System.IO.Path.IsPathRooted(path))
                path = System.IO.Path.Combine(_consultBaseDir, path);
            if (!System.IO.Path.HasExtension(path) && System.IO.File.Exists(path + ".pl"))
                path += ".pl";
            if (!System.IO.File.Exists(path))
            {
                if (throwOnUnresolved)
                    throw new Shumway.Core.PrologRuntimeException(
                        $"existence_error(source_sink, '{fileAtom.Name}')");
                Console.Error.WriteLine(
                    $"warning: use_module/1 target '{fileAtom.Name}' not found — ignored");
                return null;
            }
            return LoadResolvedLibrary(fileAtom.Name, path);
        }
        return null;
    }

    // Consults a library resolved off the search path, idempotently, and returns
    // the loaded module's name when it is export-qualified (ADR-038), else null.
    // ConsultFile is extension-routed (.shum → LoadBundle, else source). A failed
    // import warns and continues — a predicate genuinely needed surfaces a clearer
    // existence_error at its call site — rather than aborting the importing consult.
    private string? LoadResolvedLibrary(string name, string path)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch { full = path; }
        // Already loaded via this path: don't re-consult, but recover the module.
        if (_libraryModuleByPath.TryGetValue(full, out string? known))
            return ExportQualifiedNameOrNull(known);
        if (_consultedPaths.Contains(full))
            return null;   // loaded by another route; no recorded module mapping
        try
        {
            ConsultFile(path);
            string? loaded = _lastConsultedModuleName;
            if (loaded is not null) _libraryModuleByPath[full] = loaded;
            return ExportQualifiedNameOrNull(loaded);
        }
        catch (System.Exception ex)
        {
            Console.Error.WriteLine(
                $"warning: use_module(library({name})) failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>One bare module aliased into <c>user</c> (or, for a collision
    /// skip, the indicators that collided).</summary>
    internal readonly record struct BareModulePromotion(
        string Module, List<(string Name, int Arity)> Predicates);

    /// <summary>The outcome of <see cref="PromoteBareBundleModulesToUser"/>:
    /// modules whose locals were aliased into <c>user</c>, and modules skipped
    /// wholesale because a predicate name collided.</summary>
    internal readonly record struct BundlePromotionResult(
        List<BareModulePromotion> Promoted,
        List<BareModulePromotion> SkippedForCollision);

    /// <summary>REPL usability (ADR-038): a bundle loaded interactively leaves
    /// the top level standing in <c>user</c>, so the bundle's module-local
    /// predicates are invisible — unlike consulting the equivalent source,
    /// where you stand in the file's module and can call its predicates. Alias
    /// each "bare" (non-export-qualified) module's local predicates into
    /// <c>user</c>'s import table (<c>name → module</c>, resolving to
    /// <c>module$name</c>) so the top level can call them. Libraries
    /// (<c>:- module(Name, [Exports])</c>) are never touched — their names are
    /// deliberately namespaced.
    ///
    /// <para>Full fidelity to "standing in the module": <c>user</c> also
    /// inherits each promoted module's IMPORT table, so a raw goal using a name
    /// the module imported from a library (e.g. <c>X in 1..3</c> when it did
    /// <c>use_module(library(clpz))</c>) resolves the same way the module's own
    /// clauses do.</para>
    ///
    /// <para>Collisions are handled ALL-OR-NOTHING per module: if any name a
    /// module would contribute to <c>user</c> (a local alias or an inherited
    /// import) would land under two different targets — another bare module's,
    /// or one already claimed in <c>user</c> — that whole module is skipped, so
    /// <c>user</c> never sees a module half-promoted. The decision is computed
    /// over all candidates at once, so a name shared by two modules skips both.
    /// Public/dynamic predicates are already bare-global and need no alias.</para></summary>
    internal BundlePromotionResult PromoteBareBundleModulesToUser()
    {
        var promoted = new List<BareModulePromotion>();
        var skipped = new List<BareModulePromotion>();
        if (!_modules.TryGetValue(DefaultModuleName, out ModuleManifest? userManifest))
            return new BundlePromotionResult(promoted, skipped);

        // Bare candidates: non-library modules carrying aliasable locals.
        var candidates = new List<string>();
        foreach (var (name, m) in _modules)
        {
            if (name == DefaultModuleName || name == PreludeModuleName) continue;
            if (m.IsExportQualified) continue;   // library — never promote
            if (_precompiledModuleLocals.TryGetValue(name, out var locals)
                && locals.Count > 0)
                candidates.Add(name);
        }
        if (candidates.Count == 0)
            return new BundlePromotionResult(promoted, skipped);

        // Each candidate contributes name→target entries: a local fid targets
        // its own module (module$name); an imported fid targets its source.
        List<(int Fid, string Target)> Contributions(string mod)
        {
            var list = new List<(int, string)>();
            foreach (int fid in _precompiledModuleLocals[mod]) list.Add((fid, mod));
            foreach (var (fid, src) in _modules[mod].Imports) list.Add((fid, src));
            return list;
        }

        // Global fid → distinct targets, seeded with what user already resolves,
        // so >1 target on a fid means a genuine disagreement (a collision).
        var targets = new Dictionary<int, HashSet<string>>();
        foreach (var (fid, src) in userManifest.Imports)
            (targets[fid] = new HashSet<string>()).Add(src);
        foreach (string mod in candidates)
            foreach (var (fid, tgt) in Contributions(mod))
            {
                if (!targets.TryGetValue(fid, out var set))
                    targets[fid] = set = new HashSet<string>();
                set.Add(tgt);
            }

        bool changed = false;
        foreach (string mod in candidates)
        {
            var contrib = Contributions(mod);
            // Collision = any contributed fid whose global target set disagrees.
            var colliding = new List<(string Name, int Arity)>();
            foreach (var (fid, _) in contrib)
                if (targets[fid].Count > 1)
                {
                    var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
                    colliding.Add((Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "?", arity));
                }
            if (colliding.Count > 0)
            {
                skipped.Add(new BareModulePromotion(mod, colliding));
                continue;   // all-or-nothing: promote none of this module
            }
            // Clean — commit its locals (reported) and inherited imports (silent).
            var aliased = new List<(string, int)>();
            foreach (int fid in _precompiledModuleLocals[mod])
                if (userManifest.Imports.TryAdd(fid, mod))
                {
                    changed = true;
                    var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
                    aliased.Add((Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "?", arity));
                }
            foreach (var (fid, src) in _modules[mod].Imports)
                if (userManifest.Imports.TryAdd(fid, src))
                    changed = true;
            promoted.Add(new BareModulePromotion(mod, aliased));
        }
        if (changed) InvalidatePersistent();
        return new BundlePromotionResult(promoted, skipped);
    }

    /// <summary>ADR-038 — imports the whole export surface of
    /// <paramref name="sourceModule"/> into the top-level <c>user</c> module's
    /// import table (first-import-wins), so an interactive query following a
    /// goal-form <c>use_module(library(X))</c> resolves the imported predicates.
    /// Invalidates the rewrite caches when it adds anything.</summary>
    internal void ImportAllExportsIntoUser(string sourceModule)
    {
        if (!_modules.TryGetValue(sourceModule, out ModuleManifest? srcManifest)) return;
        if (!_modules.TryGetValue(DefaultModuleName, out ModuleManifest? userManifest)) return;
        bool changed = false;
        List<int>? added = null;
        Dictionary<string, List<int>>? kept = null;
        foreach (int fid in srcManifest.ExportFunctors)
        {
            if (userManifest.Imports.TryAdd(fid, sourceModule))
            {
                changed = true;
                (added ??= new List<int>()).Add(fid);
            }
            else if (userManifest.Imports[fid] is { } existing && existing != sourceModule)
            {
                kept ??= new Dictionary<string, List<int>>();
                if (!kept.TryGetValue(existing, out var list))
                    kept[existing] = list = new List<int>();
                list.Add(fid);
            }
        }
        if (kept is not null)
            foreach (var (winner, fids) in kept)
                Console.Error.WriteLine(
                    $"warning: {IndicatorList(fids)} already imported from "
                    + $"'{winner}' — keeping '{winner}', ignoring '{sourceModule}'.");
        if (added is not null) WarnImportsShadowGlobals(added, sourceModule);
        if (changed) InvalidatePersistent();
    }

    // The prelude is exempt from shadow warnings — importing a name it also
    // defines is the libc analogy, routine and intentional.
    private const string PreludeModuleName = "$prelude";

    /// <summary>Top-level imports win over bare-global publics, so loading two
    /// libraries with overlapping surfaces (the clpfd + clpz coexistence
    /// surprise) silently reroutes bare calls. Warn, aggregated per shadowed
    /// module, when freshly added `user` imports hide an already-loaded
    /// module's public predicates.</summary>
    private void WarnImportsShadowGlobals(List<int> addedFids, string sourceModule)
    {
        Dictionary<string, List<int>>? shadowed = null;
        foreach (int fid in addedFids)
        {
            foreach (var (modName, m) in _modules)
            {
                if (modName == DefaultModuleName || modName == PreludeModuleName
                    || modName == sourceModule) continue;
                if (!m.PublicFunctors.Contains(fid)) continue;
                shadowed ??= new Dictionary<string, List<int>>();
                if (!shadowed.TryGetValue(modName, out var list))
                    shadowed[modName] = list = new List<int>();
                list.Add(fid);
                break;
            }
        }
        if (shadowed is null) return;
        foreach (var (owner, fids) in shadowed)
            Console.Error.WriteLine(
                $"warning: importing {IndicatorList(fids)} from '{sourceModule}' "
                + $"shadows the global definition(s) from '{owner}' at the top level.");
    }

    /// <summary>The reverse load order: a module's bare-global publics landing
    /// while `user` already imports those names — the imports keep winning, so
    /// the newly loaded module's definitions are unreachable bare. Aggregated
    /// per import source.</summary>
    internal void WarnPublicShadowedByUserImports(string moduleName, ModuleManifest manifest)
    {
        if (moduleName == DefaultModuleName || moduleName == PreludeModuleName) return;
        if (!_modules.TryGetValue(DefaultModuleName, out ModuleManifest? user)) return;
        if (user.Imports.Count == 0) return;
        Dictionary<string, List<int>>? bySource = null;
        foreach (int fid in manifest.PublicFunctors)
        {
            if (!user.Imports.TryGetValue(fid, out var src) || src == moduleName) continue;
            bySource ??= new Dictionary<string, List<int>>();
            if (!bySource.TryGetValue(src, out var list))
                bySource[src] = list = new List<int>();
            list.Add(fid);
        }
        if (bySource is null) return;
        foreach (var (src, fids) in bySource)
            Console.Error.WriteLine(
                $"warning: the global {IndicatorList(fids)} from '{moduleName}' "
                + $"is shadowed at the top level by the existing import(s) from '{src}'.");
    }

    /// <summary>Consult-path recording of `user`-level imports (a
    /// <c>:- use_module</c> in a plain non-module file). Directive semantics
    /// keep the LAST import on a collision (unchanged); emits the same
    /// user-level shadow warnings as the goal-form import.</summary>
    internal void RecordUserImports(
        ModuleManifest userManifest, IEnumerable<KeyValuePair<int, string>> imports)
    {
        Dictionary<string, List<int>>? addedBySource = null;
        foreach (var (fid, src) in imports)
        {
            if (userManifest.Imports.TryGetValue(fid, out var existing))
            {
                if (existing != src)
                {
                    Console.Error.WriteLine(
                        $"warning: {IndicatorList(new List<int> { fid })} import from "
                        + $"'{src}' replaces the earlier import from '{existing}'.");
                    userManifest.Imports[fid] = src;
                }
                continue;
            }
            userManifest.Imports[fid] = src;
            addedBySource ??= new Dictionary<string, List<int>>();
            if (!addedBySource.TryGetValue(src, out var list))
                addedBySource[src] = list = new List<int>();
            list.Add(fid);
        }
        if (addedBySource is not null)
        {
            foreach (var (src, fids) in addedBySource)
                WarnImportsShadowGlobals(fids, src);
        }
    }

    private readonly Dictionary<string,
        (object ClausesRef, int ClauseCount, int PublicCount, int ExportCount,
         HashSet<int> Mangled)> _moduleMangledCache = new();

    /// <summary>True when every recorded static Module:Goal resolution of a
    /// cached transform still resolves the same today — the per-entry
    /// revalidation that lets an unrelated module load reuse the transform
    /// verbatim (a version-counter key re-transformed every qualified-goal
    /// user per consult of the clpz load chain).</summary>
    internal bool QualifiedResolutionsStillValid(
        Dictionary<(string Mod, string Name, int Arity), string?>? resolutions)
    {
        if (resolutions is null) return true;
        foreach (var (key, resolved) in resolutions)
            if (ResolveQualifiedStatic(key.Mod, key.Name, key.Arity) != resolved)
                return false;
        return true;
    }

    /// <summary>Compile-time resolution of a statically written
    /// <c>Module:Goal</c> body goal — mirrors the runtime PrepareMqualGoal
    /// chain exactly: the module's mangled definitions → its import table →
    /// the bare name (own legacy publics, globals, builtins, prelude,
    /// dynamics). Returns <c>null</c> when the module isn't loaded (keep the
    /// runtime ':'/2 dispatch; a later load makes
    /// <see cref="QualifiedResolutionsStillValid"/> re-transform the
    /// caller).</summary>
    internal string? ResolveQualifiedStatic(string module, string name, int arity)
    {
        if (!_modules.TryGetValue(module, out ModuleManifest? m)) return null;
        int fid = Shumway.Core.FunctorTable.Intern(
            Shumway.Core.AtomTable.Intern(name, permanent: true).Id, arity);
        if (_dynStore.IsDynamic(fid)) return name;   // dynamics are flat-global
        if (GetModuleMangledSet(module, m).Contains(fid)) return module + "$" + name;
        if (m.Imports.TryGetValue(fid, out string? src)) return src + "$" + name;
        return name;
    }

    // The functors module `m` links under its mangled name: clause heads
    // (minus legacy publics — those stay bare) plus an export-qualified
    // module's exports, plus a precompiled bundle's locals. A dynamic fid in
    // the set is harmless: ResolveQualifiedStatic's dynamic check runs FIRST,
    // so the fingerprint doesn't need to track dynamic promotions.
    private HashSet<int> GetModuleMangledSet(string moduleName, ModuleManifest m)
    {
        if (_moduleMangledCache.TryGetValue(moduleName, out var e)
            && ReferenceEquals(e.ClausesRef, m.Clauses)
            && e.ClauseCount == m.Clauses.Count
            && e.PublicCount == m.PublicFunctors.Count
            && e.ExportCount == m.ExportFunctors.Count)
            return e.Mangled;
        var set = new HashSet<int>();
        foreach (var c in m.Clauses)
        {
            if (c.Kind is Shumway.Compiler.Ast.ClauseKind.Directive) continue;
            set.Add(ConsultPipeline.HeadFunctorIdOf(c));
        }
        if (m.IsExportQualified) set.UnionWith(m.ExportFunctors);
        else set.ExceptWith(m.PublicFunctors);
        if (_precompiledModuleLocals.TryGetValue(moduleName, out var bundleLocals))
            set.UnionWith(bundleLocals);
        _moduleMangledCache[moduleName] =
            (m.Clauses, m.Clauses.Count, m.PublicFunctors.Count,
             m.ExportFunctors.Count, set);
        return set;
    }

    private static string IndicatorList(List<int> fids)
    {
        const int cap = 8;
        var parts = new List<string>(Math.Min(fids.Count, cap));
        for (int i = 0; i < fids.Count && i < cap; i++)
        {
            var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fids[i]);
            parts.Add($"{Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "?"}/{arity}");
        }
        string joined = string.Join(", ", parts);
        return fids.Count > cap ? $"{joined} (+{fids.Count - cap} more)" : joined;
    }

    // The module name if it names an export-qualified module (ADR-038), else null
    // — a legacy bare-global module contributes no import entries.
    private string? ExportQualifiedNameOrNull(string? moduleName) =>
        moduleName is not null
        && _modules.TryGetValue(moduleName, out ModuleManifest? m)
        && m.IsExportQualified
            ? moduleName
            : null;

    internal int _nativeBlockConsultSeq;
    // the engine's monotonic synthesized-helper sequence: every
    // consult/assert transform on this engine draws unique helper ids, so a
    // second consult's `$disj_N` can never collide with the first's in the same
    // module. Per-engine (not global) so the atom space stays bounded across
    // engines/processes; the query stub uses the reserved `$q` prefix instead.
    private int _metaHelperSeq;
    internal int NextMetaHelperId() => ++_metaHelperSeq;


    /// <summary>ADR-025 — enables the inline if-then-else lowering: an eligible
    /// plain-goal <c>(C -&gt; T ; E)</c> / <c>(A ; B)</c> compiles INSIDE the host
    /// clause (get_level; try_me_else; cut; jump) instead of a synthesized
    /// 2-clause helper reached by a Call. STATIC consult paths only — the
    /// runtime assert path always uses the helper form. Default OFF (stage (c)
    /// of the ADR-025 rollout): a predicate with an inline ITE is not yet
    /// Tier-1-promotable (the IL compiler rejects the shape gracefully and it
    /// stays on Tier-0), so flipping this on trades Tier-1 eligibility for the
    /// Tier-0 win until the ADR's stage (b) lands. Set before consulting.</summary>
    public bool EnableInlineIte { get; set; }
}
