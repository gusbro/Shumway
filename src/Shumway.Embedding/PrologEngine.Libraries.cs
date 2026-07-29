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
    internal bool UseCompatLibrary(string name)
    {
        if (!CompatLibraries.TryGet(name, out string source))
            return false;
        // recordInHistory:false — the importing program's own source (which
        // carries the use_module directive) is what SaveState replays; the
        // directive re-loads the library on restore, so recording the library
        // body too would double-consult it (and trip public uniqueness).
        if (_loadedCompatLibraries.Add(name) && source.Length > 0)
            ConsultStringInner(source, recordInHistory: false);
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
                AddLibraryDirNormalized(d);
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
                        return LoadResolvedLibrary(lib.Name, libPath);
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
        foreach (int fid in srcManifest.ExportFunctors)
            if (userManifest.Imports.TryAdd(fid, sourceModule)) changed = true;
        if (changed) InvalidatePersistent();
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
