using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Separate compilation through the consult pipeline: an ephemeral engine
/// consults the root file(s) (resolving each <c>use_module</c> chain over the
/// given library directories), so in-file <c>term_expansion</c> /
/// <c>goal_expansion</c> hooks actually run — a library that generates
/// clauses by executing Prolog (Scryer's atts emits each module's
/// get_atts/put_atts, clpz builds its cis* arithmetic) compiles complete,
/// which the file-at-a-time <see cref="ShmoCompiler"/> cannot do.
///
/// <para>Every module the consult loaded is serialised to its own
/// <see cref="ShmoObject"/>, and each object is <b>self-contained</b>: it
/// carries its own clauses, the operators its consult defined, and the
/// dynamic seeds of the predicates it declares. So a dependency (say
/// <c>clpz</c>) compiles to the same <c>.shmo</c> regardless of which root
/// pulled it in — separate compilations and a single batch produce the same
/// object set, and any reachability-complete subset links correctly.</para>
/// </summary>
public static class ShmoViaConsult
{
    /// <summary>Consults <paramref name="rootPath"/> in a fresh engine and
    /// returns one compiled object per module the consult loaded, root first.
    /// Thin wrapper over <see cref="CompileMany"/>.</summary>
    public static List<(string ModuleName, ShmoObject Object)> Compile(
        string rootPath,
        IReadOnlyList<string> libraryDirs,
        ShmoBuildMode buildMode,
        List<ShmoCompileError> errors,
        string? dialect = null,
        System.IO.TextWriter? warnings = null)
    {
        var rich = CompileMany(
            new[] { rootPath }, libraryDirs, buildMode, errors, dialect, warnings);
        var outp = new List<(string, ShmoObject)>(rich.Count);
        foreach (var r in rich) outp.Add((r.ModuleName, r.Object));
        return outp;
    }

    /// <summary>Consults every path in <paramref name="rootPaths"/> into one
    /// ephemeral engine (so a module shared by several roots — a library, its
    /// dependencies — loads and compiles once), and returns one object per
    /// module the consult loaded. Each result carries the module's source
    /// timestamp (the <c>.pl</c> it came from, or the runtime assembly for a
    /// baked-in library) and whether it is one of the passed roots — the
    /// separate-compilation CLI uses these to skip regenerating a dependency
    /// <c>.shmo</c> that is already up to date.
    ///
    /// <para><paramref name="warnings"/> takes the load-time warnings the
    /// consult produces (a directive that raised, a <c>use_module</c> target
    /// that is not there). Left null they go where an engine's warnings go,
    /// standard error, which is right for a CLI compiling what its user
    /// asked for; a host compiling on its own behalf passes a writer, since
    /// warnings from a library nobody is watching are not the user's
    /// business.</para></summary>
    public static List<(string ModuleName, ShmoObject Object, System.DateTime SourceTimeUtc, bool IsRoot)> CompileMany(
        IReadOnlyList<string> rootPaths,
        IReadOnlyList<string> libraryDirs,
        ShmoBuildMode buildMode,
        List<ShmoCompileError> errors,
        string? dialect = null,
        System.IO.TextWriter? warnings = null)
    {
        // Strict: consult mode is how the toolchain compiles sources, so a
        // clause that does not parse fails the build (the object it would
        // land in is what everything downstream links), where the same file
        // loaded into a live engine reports it and carries on.
        var e = new PrologEngine { StrictConsultSyntax = true };
        if (warnings is not null) e.Warnings = warnings;
        // ADR-040: a library collection may be a non-shumway dialect. An
        // explicit dialect applies to every dir; otherwise each dir spec may
        // carry its own `dialect:path` prefix (AddLibraryDirectorySpec parses it).
        foreach (string d in libraryDirs)
            if (dialect is { Length: > 0 }) e.AddLibraryDirectory(d, dialect);
            else e.AddLibraryDirectorySpec(d);
        // Each root's own directory is an implicit library dir (the C
        // `#include "..."` rule) — added after the explicit -L dirs, which win.
        foreach (string rp in rootPaths)
            if (System.IO.Path.GetDirectoryName(
                    System.IO.Path.GetFullPath(rp)) is { Length: > 0 } ownDir)
                e.AddLibraryDirectory(ownDir);

        var pre = new HashSet<string>(e.Modules.Keys);

        // Consult every root into the one engine. Track each root's module
        // name (its `:- module` directive, else the filename — a bare file
        // must become a named module or it would merge into `user`, which is
        // not a linkable object). The first root is the "primary" — it carries
        // any dynamic seeds no module declares (auto-promoted functors).
        var rootModules = new List<string>();
        var rootModuleSet = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (string rp in rootPaths)
        {
            // ADR-040: a root is a library of its collection's dialect as much
            // as the dependencies it pulls in, so it is read the way
            // use_module would load it (the dialect's flags, operators and
            // shim) and its module records the dialect. Read plainly, a Scryer
            // library failed on an operator only Scryer's VM declares.
            string? rootDialect = dialect is { Length: > 0 }
                ? dialect
                : e.DialectForResolvedPath(System.IO.Path.GetFullPath(rp));
            string text = Shumway.Core.TextFile.ReadAllText(rp);
            var m = System.Text.RegularExpressions.Regex.Match(
                text, @"^\s*:-\s*module\(\s*'?([^,)'\s]+)",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            string name;
            if (m.Success)
            {
                name = m.Groups[1].Value;
                e.WithLibraryDialect(rootDialect, () => { e.ConsultFile(rp); return 0; });
            }
            else
            {
                name = System.IO.Path.GetFileNameWithoutExtension(rp);
                // ConsultString has no path — record the root's source file so
                // its timestamp is right (roots regenerate anyway, but keep it honest).
                e._moduleSourceFile[name] = System.IO.Path.GetFullPath(rp);
                e.WithLibraryDialect(rootDialect,
                    () => { e.ConsultString($":- module('{name}').\n" + text); return 0; });
            }
            if (rootModuleSet.Add(name)) rootModules.Add(name);
        }
        string? primaryRoot = rootModules.Count > 0 ? rootModules[0] : null;

        // Assign each dynamic functor to exactly one owning module, so its
        // clauses are seeded once across the object set. The owner is the
        // module whose consult declared it `:- dynamic`; a functor whose
        // declarer is not emitted (auto-promoted, or declared by the baked
        // prelude) goes to the primary root.
        var dynOwner = new Dictionary<int, string>();
        foreach (int fid in e._dynStore.Functors)
        {
            string? decl = e.DynamicDeclaringModule(fid);
            dynOwner[fid] = decl is { } d && !pre.Contains(d)
                ? d
                : (primaryRoot ?? decl ?? PrologEngine.DefaultModuleName);
        }

        System.DateTime bakedTimeUtc = BakedSourceTimeUtc();

        var results = new List<(string, ShmoObject, System.DateTime, bool)>();
        foreach (var (name, manifest) in e.Modules)
        {
            if (pre.Contains(name)) continue;
            if (name == PrologEngine.DefaultModuleName && manifest.Clauses.Count == 0)
                continue;
            bool isRoot = rootModuleSet.Contains(name);

            var publicSet = new HashSet<PredicateRef>();
            foreach (int fid in manifest.PublicFunctors) publicSet.Add(RefOf(fid));
            var exports = new List<PredicateRef>();
            foreach (int fid in manifest.ExportFunctors) exports.Add(RefOf(fid));
            var imports = new List<ShmoImportEntry>();
            foreach (var (fid, src) in manifest.Imports)
                imports.Add(new ShmoImportEntry(RefOf(fid), src));

            // This module's own dynamic declarations, and the seeds of the
            // functors it owns (each functor seeded by exactly one module).
            var dynamicSet = new HashSet<PredicateRef>();
            foreach (int fid in manifest.DynamicFunctors) dynamicSet.Add(RefOf(fid));
            var raw = new List<Clause>(manifest.Clauses);
            // ADR-055: what this module gave user (user:Head) is, in a linked
            // program, a public of this module: global, its body still run
            // here, where the module's own qualification is redundant.
            foreach (var (fsrc, ftgt, fclause) in e._foreignClauses)
                if (ftgt == PrologEngine.DefaultModuleName && fsrc == "module:" + name)
                {
                    raw.Add(WithoutOwnQualification(fclause, name));
                    publicSet.Add(RefOf(ConsultPipeline.HeadFunctorIdOf(fclause)));
                }
            foreach (int fid in e._dynStore.Functors)
                if (dynOwner.TryGetValue(fid, out var owner) && owner == name)
                {
                    dynamicSet.Add(RefOf(fid));
                    if (e._dynStore.TryGetClauses(fid, out var dcls)) raw.AddRange(dcls);
                }

            // This module's own operators (a `:- op` under its consult).
            // ADR-046 — the ones in its export list persist with a '*'
            // type suffix so a load re-advertises them to importers.
            var ops = new List<ShmoOperatorDef>();
            e._moduleExportedOps.TryGetValue(name, out var exportedOps);
            foreach (var (opName, prec, type) in e.ModuleOperators(name))
            {
                string tn = OpTypeName(type);
                bool exported = exportedOps is not null
                    && exportedOps.Any(x => x.Name == opName
                        && x.Precedence == prec);
                ops.Add(new ShmoOperatorDef(prec, exported ? tn + "*" : tn, opName));
            }

            var localErrors = new List<ShmoCompileError>();
            var res = ShmoCompiler.CompileFromParts(
                name,
                source: "",
                rawClauses: raw,
                publicSet: publicSet,
                dynamicSet: dynamicSet,
                ensureLinked: new List<PredicateRef>(),
                qualifiedRefs: new List<QualifiedPredicateRef>(),
                buildMode: buildMode,
                errors: localErrors,
                operatorDefs: ops.Count > 0 ? ops : null,
                isExportQualified: manifest.IsExportQualified,
                exports: exports,
                imports: imports,
                dialect: manifest.Dialect,
                metaArgSpec: e.MetaArgSpec,
                // A Module:Goal resolves as the consult that loaded this module
                // resolves it, against the same modules: compiled to ':'/2 it
                // is a meta-call, and on the wasm tier an exit to the host
                // (atts generates one per clause of a module's get_atts).
                qualifiedResolver: e.ResolveQualifiedStatic);
            if (res.Object is null)
            {
                foreach (var err in localErrors)
                    errors.Add(new ShmoCompileError(
                        $"[{name}] {err.Message}", err.Line, err.Column));
                continue;
            }

            System.DateTime srcTime = SourceTimeUtc(e, name, bakedTimeUtc);
            var entry = (name, res.Object, srcTime, isRoot);
            if (name == primaryRoot) results.Insert(0, entry); else results.Add(entry);
        }
        return results;
    }

    /// <summary>The module's source timestamp: the <c>.pl</c> it was consulted
    /// from, or — for a baked-in library / prelude / shim (no source file) —
    /// the runtime assembly holding the embedded source.</summary>
    private static System.DateTime SourceTimeUtc(PrologEngine e, string moduleName, System.DateTime bakedTimeUtc)
    {
        string? sf = e.ModuleSourceFile(moduleName);
        if (sf is { Length: > 0 })
        {
            try { if (System.IO.File.Exists(sf)) return System.IO.File.GetLastWriteTimeUtc(sf); }
            catch { /* fall through to baked */ }
        }
        return bakedTimeUtc;
    }

    /// <summary>Timestamp of the assembly whose embedded strings are the
    /// baked-in libraries' source, so a dependency with no <c>.pl</c> is dated
    /// against the runtime that produced it. <see cref="System.DateTime.UtcNow"/>
    /// as a last resort forces regeneration.</summary>
    /// <summary><paramref name="clause"/> with each body goal written
    /// <c>module:Goal</c> back to <c>Goal</c>, through the control
    /// constructs: the consult qualified it for running outside the
    /// module.</summary>
    private static Clause WithoutOwnQualification(Clause clause, string module)
    {
        if (clause.Term is not CompoundTerm { Functor: ":-", Args: [var head, var body] } rule)
            return clause;
        Term stripped = Shumway.Compiler.Parsing.GoalTreeRewrite.Apply(body,
            c => c.Args.Length == 2 && c.Functor is "," or ";" or "->" or "*->",
            g => g is CompoundTerm { Functor: ":", Args: [AtomTerm m, var inner] } && m.Name == module
                ? inner : g);
        return ReferenceEquals(stripped, body)
            ? clause
            : new Clause(clause.Kind,
                new CompoundTerm(":-", new[] { head, stripped }) { Position = rule.Position },
                clause.Position);
    }

    private static System.DateTime BakedSourceTimeUtc()
    {
        try
        {
            string loc = typeof(PrologEngine).Assembly.Location;
            if (loc is { Length: > 0 } && System.IO.File.Exists(loc))
                return System.IO.File.GetLastWriteTimeUtc(loc);
        }
        catch { /* fall through */ }
        return System.DateTime.UtcNow;
    }

    private static PredicateRef RefOf(int fid)
    {
        var (atomId, arity) = FunctorTable.Lookup(fid);
        return new PredicateRef(AtomTable.GetById(atomId)?.Name ?? "?", arity);
    }

    private static string OpTypeName(OperatorType t) => t.ToString().ToLowerInvariant();
}
