using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Separate compilation THROUGH the consult pipeline: an ephemeral engine
/// consults the root file (resolving its <c>use_module</c> chain over the
/// given library directories), so in-file <c>term_expansion</c> /
/// <c>goal_expansion</c> hooks actually RUN — a library that generates
/// clauses by executing Prolog (Scryer's atts emits each module's
/// get_atts/put_atts, clpz builds its cis* arithmetic) compiles complete,
/// which the file-at-a-time <see cref="ShmoCompiler"/> cannot do. Every
/// module the consult loaded is serialised to its own <see cref="ShmoObject"/>
/// from the post-expansion manifest, ready for shumway-link.
/// </summary>
public static class ShmoViaConsult
{
    /// <summary>Consults <paramref name="rootPath"/> in a fresh engine (with
    /// <paramref name="libraryDirs"/> on the library search path) and returns
    /// one compiled object per module the consult loaded, root first. Errors
    /// surface as <see cref="ShmoCompileError"/>s; a failed consult throws the
    /// underlying exception.</summary>
    public static List<(string ModuleName, ShmoObject Object)> Compile(
        string rootPath,
        IReadOnlyList<string> libraryDirs,
        ShmoBuildMode buildMode,
        List<ShmoCompileError> errors,
        string? dialect = null)
    {
        // Operator baseline: a fresh engine's table, so the diff below is
        // exactly what this consult chain defined.
        var baseline = new HashSet<(int, OperatorType, string)>(
            new PrologEngine().Operators.Enumerate());

        var e = new PrologEngine();
        // ADR-040: a whole library collection may be a single non-shumway
        // dialect (Scryer, SWI, …). Tag every provided dir with it so the
        // consult applies that dialect's double_quotes + name map.
        foreach (string d in libraryDirs)
            if (dialect is { Length: > 0 }) e.AddLibraryDirectory(d, dialect);
            else e.AddLibraryDirectory(d);
        // The compiled file's own directory is an implicit library dir (the
        // C `#include "..."` rule): a library collection's dependencies are
        // its siblings. Added last — explicit -L / SHUMWAY_LIBRARY_PATH win.
        if (System.IO.Path.GetDirectoryName(
                System.IO.Path.GetFullPath(rootPath)) is { Length: > 0 } ownDir)
            e.AddLibraryDirectory(ownDir);
        var pre = new HashSet<string>(e.Modules.Keys);

        // The root's module name follows the shumway-compile convention: its
        // :- module directive, else the FILENAME (a plain file must become a
        // named module — consulted bare it would merge into `user`, which is
        // not a linkable object). Note _lastConsultedModuleName is useless
        // here: directives (the use_module chain) run after the root's
        // manifest commit, so it ends up naming the last dependency.
        string rootText = System.IO.File.ReadAllText(rootPath);
        string rootModule;
        var moduleMatch = System.Text.RegularExpressions.Regex.Match(
            rootText, @"^\s*:-\s*module\(\s*'?([^,)'\s]+)", System.Text.RegularExpressions.RegexOptions.Multiline);
        if (moduleMatch.Success)
        {
            rootModule = moduleMatch.Groups[1].Value;
            e.ConsultFile(rootPath);
        }
        else
        {
            rootModule = System.IO.Path.GetFileNameWithoutExtension(rootPath);
            e.ConsultString($":- module('{rootModule}').\n" + rootText);
        }

        // Everything this chain defined operator-wise travels with the ROOT
        // object (idempotent to re-define at load).
        var newOps = new List<ShmoOperatorDef>();
        foreach (var (prec, type, name) in e.Operators.Enumerate())
            if (!baseline.Contains((prec, type, name)))
                newOps.Add(new ShmoOperatorDef(prec, OpTypeName(type), name));

        // Dynamic predicates: a fresh engine means every dynamic functor came
        // from this chain. Declarations + current clauses attach to the ROOT
        // object (dynamics are flat-global; one declaring module suffices).
        var dynamicSet = new HashSet<PredicateRef>();
        var dynClauses = new List<Clause>();
        foreach (int fid in e._dynStore.Functors)
        {
            dynamicSet.Add(RefOf(fid));
            if (e._dynStore.TryGetClauses(fid, out var clauses))
                dynClauses.AddRange(clauses);
        }

        var results = new List<(string, ShmoObject)>();
        foreach (var (name, manifest) in e.Modules)
        {
            if (pre.Contains(name)) continue;
            if (name == PrologEngine.DefaultModuleName && manifest.Clauses.Count == 0)
                continue;
            bool isRoot = name == rootModule;

            var publicSet = new HashSet<PredicateRef>();
            foreach (int fid in manifest.PublicFunctors) publicSet.Add(RefOf(fid));
            var exports = new List<PredicateRef>();
            foreach (int fid in manifest.ExportFunctors) exports.Add(RefOf(fid));
            var imports = new List<ShmoImportEntry>();
            foreach (var (fid, src) in manifest.Imports)
                imports.Add(new ShmoImportEntry(RefOf(fid), src));

            var raw = new List<Clause>(manifest.Clauses);
            if (isRoot && dynClauses.Count > 0) raw.AddRange(dynClauses);

            var localErrors = new List<ShmoCompileError>();
            var res = ShmoCompiler.CompileFromParts(
                name,
                source: "",
                rawClauses: raw,
                publicSet: publicSet,
                dynamicSet: isRoot ? dynamicSet : new HashSet<PredicateRef>(),
                ensureLinked: new List<PredicateRef>(),
                qualifiedRefs: new List<QualifiedPredicateRef>(),
                buildMode: buildMode,
                errors: localErrors,
                operatorDefs: isRoot ? newOps : null,
                isExportQualified: manifest.IsExportQualified,
                exports: exports,
                imports: imports);
            if (res.Object is null)
            {
                foreach (var err in localErrors)
                    errors.Add(new ShmoCompileError(
                        $"[{name}] {err.Message}", err.Line, err.Column));
                continue;
            }
            var entry = (name, res.Object);
            if (isRoot) results.Insert(0, entry); else results.Add(entry);
        }
        return results;
    }

    private static PredicateRef RefOf(int fid)
    {
        var (atomId, arity) = FunctorTable.Lookup(fid);
        return new PredicateRef(AtomTable.GetById(atomId)?.Name ?? "?", arity);
    }

    private static string OpTypeName(OperatorType t) => t.ToString().ToLowerInvariant();
}
