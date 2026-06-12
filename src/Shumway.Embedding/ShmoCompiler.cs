using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;

namespace Shumway.Embedding;

/// <summary>
/// Compiles one Prolog source file (or in-memory source) into an in-
/// memory <see cref="ShmoObject"/>. Used by the <c>shumway-compile</c>
/// CLI (chunk 161) and by the linker's tests; the in-process embedder
/// can also call it to produce <c>.shmo</c> artifacts on the fly.
///
/// <para>Pipeline:</para>
/// <list type="number">
/// <item>Parse with <see cref="ClauseReader"/>.</item>
/// <item>Apply <see cref="DcgTransform"/> (DCG rules become normal rules
/// with the diff-list pair appended to head &amp; goals).</item>
/// <item>Walk directives for <c>:- module/1</c>, <c>:- public/1</c>,
/// <c>:- dynamic/1</c>, <c>:- ensure_linked/1</c> (the last is chunk 162).</item>
/// <item>For every non-directive clause, classify the head's
/// <c>Name/Arity</c>, attach the right visibility, and walk the body
/// emitting call edges into the per-predicate call graph.</item>
/// <item>Compile the surviving rule/fact clauses via
/// <see cref="ModuleCompiler"/> and encode through
/// <see cref="CompiledModuleCodec"/>.</item>
/// </list>
///
/// <para>The linker (chunk 163) filters out builtins from the call
/// graph and resolves the remainder against the union of every loaded
/// <c>.shmo</c>'s <c>:- public</c>/<c>:- dynamic</c> set. Anything still
/// unresolved is the missing-predicate report.</para>
/// </summary>
public static class ShmoCompiler
{
    /// <summary>Chunk 436 — every directive name some stage of the
    /// toolchain actually handles: the ShmoCompiler itself (module /
    /// public / dynamic / visible / ensure_linked), the ClauseReader's
    /// in-place pre-pass (op / set_prolog_flag / char_conversion, plus
    /// the arity_compat <c>c</c> / <c>prolog</c> section markers), and
    /// the PrologEngine consult pass (discontiguous / multifile /
    /// table / mode). Anything else is an UNKNOWN directive: under
    /// <c>arity_compat</c> it's reported as a warning and skipped
    /// (Arity sources carry directives like <c>extrn</c> that have no
    /// Shumway meaning); without the flag behaviour is unchanged.</summary>
    internal static readonly HashSet<string> RecognizedDirectives = new()
    {
        "module", "public", "dynamic", "visible", "ensure_linked",
        "op", "set_prolog_flag", "char_conversion",
        "discontiguous", "multifile", "table", "mode",
        "c", "prolog",
    };

    /// <summary>Chunk 436 — unknown directives whose arity_compat
    /// warning is suppressed entirely. Callers (or embedders) add names
    /// of directives they know are harmless noise in their corpus.
    /// Chunk 437 seeds <c>extrn</c> — Arity's external-predicate
    /// declaration is ubiquitous in real sources and has no Shumway
    /// meaning (the linker resolves cross-module calls itself), so it
    /// is ignored without even a warning. The engine consult path
    /// (<see cref="PrologEngine"/>'s ConsultString warning pass)
    /// consults this same set.</summary>
    public static readonly HashSet<string> SilentlyIgnoredDirectives = new()
    {
        "extrn",
    };

    /// <summary>Compiles <paramref name="path"/> to a <see cref="ShmoObject"/>.
    /// The module name defaults to the file's bare name (without
    /// extension) when no <c>:- module(Name).</c> directive is
    /// present.</summary>
    public static ShmoObject CompileFile(string path,
        ShmoBuildMode buildMode = ShmoBuildMode.Release)
    {
        ArgumentNullException.ThrowIfNull(path);
        string source = File.ReadAllText(path);
        string fallback = Path.GetFileNameWithoutExtension(path);
        return CompileSource(source, fallback, buildMode);
    }

    /// <summary>Compiles <paramref name="source"/> in memory.
    /// <paramref name="moduleNameFallback"/> is the module name when the
    /// source has no <c>:- module/1</c> directive (chunk 440 — it is no
    /// longer collapsed to "user"; per-file module identity is what keeps
    /// two module-less files' locals from aliasing). Pass the empty
    /// string for "<c>user</c>" — Shumway's default.</summary>
    public static ShmoObject CompileSource(string source,
        string moduleNameFallback = "user",
        ShmoBuildMode buildMode = ShmoBuildMode.Release)
    {
        var result = TryCompileSource(source, moduleNameFallback, buildMode,
            maxErrors: 100);
        if (result.Errors.Count > 0)
        {
            var first = result.Errors[0];
            throw new InvalidOperationException(
                $"{first.Line}:{first.Column}: {first.Message}");
        }
        return result.Object!;
    }

    /// <summary>Compiles <paramref name="source"/> with C-style error
    /// recovery: a <see cref="Shumway.Compiler.Parsing.ParseException"/>
    /// on one clause is captured as an error and the parser resyncs to
    /// the next clause-terminator dot before trying the next one.
    /// Malformed directives (<c>:- public foo.</c>, etc.) are likewise
    /// captured. The result carries every error AND — only when zero
    /// errors fired — the resulting <see cref="ShmoObject"/>. Stops
    /// after <paramref name="maxErrors"/> errors (default 100).</summary>
    public static ShmoCompileResult TryCompileSource(string source,
        string moduleNameFallback = "user",
        ShmoBuildMode buildMode = ShmoBuildMode.Release,
        int maxErrors = 100,
        bool arityCompat = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        Shumway.Builtins.StandardBuiltins.EnsureRegistered();
        // The engine-resident meta-builtins (catch/3, assertz/1,
        // call/N, findall/3, etc.) are normally registered by the
        // PrologEngine constructor. The ShmoCompiler doesn't spin
        // one up, so register them here too — otherwise the WAM
        // compiler doesn't see them as builtins and emits Execute
        // instead of CallBuiltin for those goals. A later runtime
        // (or IL-promoted) dispatch then tries to find them as
        // user predicates and raises existence_error/2.
        MetaBuiltins.EnsureRegistered();

        var errors = new List<ShmoCompileError>();
        var warnings = new List<ShmoCompileError>();
        var allClauses = new List<Clause>();
        // Phase 30: `shumway-compile --arity` pre-enables the Arity
        // compatibility mode for the whole file (the in-file
        // set_prolog_flag(arity_compat, _) directive can still flip it).
        var readerFlags = new Shumway.Compiler.Parsing.PrologFlags
        { ArityCompat = arityCompat };
        // Chunk 441 — record whether Arity mode was EVER on during the
        // compile (the --arity pre-enable, or an in-file
        // set_prolog_flag(arity_compat, true) flip at any point). The
        // resulting ShmoObject carries it so the linker can apply Arity
        // call semantics (undeclared predicate → implicit empty
        // dynamic) to this module's unresolved references.
        bool arityEverOn = arityCompat;
        foreach (var entry in new ClauseReader(new Lexer(source),
                     OperatorTable.Default(), readerFlags)
                 .ReadAllCollectingErrors(maxErrors))
        {
            arityEverOn |= readerFlags.ArityCompat;
            if (entry.IsError)
                errors.Add(new ShmoCompileError(entry.ErrorMessage!,
                    entry.ErrorPosition.Line, entry.ErrorPosition.Column));
            else if (entry.Clause is not null)
                allClauses.Add(entry.Clause);
        }
        arityEverOn |= readerFlags.ArityCompat;
        if (errors.Count >= maxErrors)
            return new ShmoCompileResult(null, errors, warnings);
        // First pass: walk RAW (untransformed) clauses to read
        // directives (so we know which predicates are dynamic) and
        // collect the raw bodies. We need the raw bodies for two
        // reasons:
        //
        //   1. Chunk 209: a `:- dynamic foo/N.` clause must be
        //      serialised RAW to DynamicSeeds, because the engine's
        //      SetupQueryFromTerm runs DcgTransform / MetaTransform /
        //      PhraseTransform on _dynamicClauses AGAIN — mirroring
        //      what ConsultString does at line 4022. Pre-transforming
        //      here would double-apply.
        //
        //   2. Local-pred call-graph edges from inside catch / findall /
        //      etc. need to be visible — CollectCalls descends into
        //      meta-builtin goal args (chunk 209) so the raw body still
        //      enumerates everything reachable.
        // Chunk 440 — the fallback (the file's base name when compiling a
        // file, "user" for bare in-memory sources) IS the module name when
        // no `:- module/1` directive is present. The chunk-209 forcing of
        // PrologEngine.DefaultModuleName here made every module-less file
        // compile as module "user": two such files could never be linked
        // together (duplicate_module), and their locals would have aliased
        // (`user$helper`) even if the linker had allowed it. The consumers
        // chunk 209 was protecting — dynamic-seed rehydration's
        // ModuleRewrite context and the bundle-local-fid feed — are now
        // per-entry-module-aware (see PrologEngine.LoadEntryFromBytecode +
        // SetupQueryFromTerm's _dynamicSeedModule attribution).
        string moduleName = string.IsNullOrEmpty(moduleNameFallback)
            ? PrologEngine.DefaultModuleName
            : moduleNameFallback;
        var publicSet = new HashSet<PredicateRef>();
        var dynamicSet = new HashSet<PredicateRef>();
        var ensureLinked = new List<PredicateRef>();
        var qualifiedRefs = new List<QualifiedPredicateRef>();
        var rawClauses = new List<Clause>();

        foreach (var clause in allClauses)
        {
            if (clause.Kind == ClauseKind.Directive
                && clause.Term is CompoundTerm d
                && d.Functor == ":-" && d.Args.Length == 1)
            {
                try
                {
                    ProcessDirective(d.Args[0], ref moduleName,
                        publicSet, dynamicSet, ensureLinked);
                }
                catch (InvalidOperationException ex)
                {
                    errors.Add(new ShmoCompileError(ex.Message,
                        clause.Position.Line, clause.Position.Column));
                }
                // Chunk 436 (arity_compat only): an unrecognised
                // directive is a WARNING, not an error — Arity sources
                // carry directives (`:- extrn ...`, `:- disable_*`)
                // with no Shumway meaning; compilation continues.
                // readerFlags reflects in-file set_prolog_flag flips.
                if (readerFlags.ArityCompat)
                {
                    string dirName = d.Args[0] switch
                    {
                        AtomTerm dirAtom => dirAtom.Name,
                        CompoundTerm dirComp => dirComp.Functor,
                        _ => "",
                    };
                    if (dirName.Length > 0
                        && !RecognizedDirectives.Contains(dirName)
                        && !SilentlyIgnoredDirectives.Contains(dirName))
                        warnings.Add(new ShmoCompileError(
                            $"unknown directive '{dirName}' ignored (arity_compat)",
                            clause.Position.Line, clause.Position.Column));
                }
                continue;
            }
            rawClauses.Add(clause);
        }
        if (errors.Count > 0)
            return new ShmoCompileResult(null, errors, warnings);

        return CompileFromParts(
            moduleName,
            source, rawClauses, publicSet, dynamicSet, ensureLinked,
            qualifiedRefs, buildMode, errors, warnings, arityEverOn);
    }

    /// <summary>Chunk 411 — the compile back-half, shared by
    /// <see cref="TryCompileSource"/> (after its parse + directive pass) and the
    /// linker's cross-module unfold recompile (which reconstructs the inputs
    /// from a V4 <c>.shmo</c>'s metadata + <c>ClauseTerms</c>/<c>DynamicSeeds</c>
    /// and re-enters here with rewritten clauses). <paramref name="moduleName"/>
    /// is the RESOLVED runtime module name (the `:- module/1` directive's
    /// argument, else the per-file fallback — chunk 440).
    /// <paramref name="qualifiedRefs"/> is appended to by the call-graph
    /// walk.</summary>
    internal static ShmoCompileResult CompileFromParts(
        string moduleName,
        string source,
        List<Clause> rawClauses,
        HashSet<PredicateRef> publicSet,
        HashSet<PredicateRef> dynamicSet,
        List<PredicateRef> ensureLinked,
        List<QualifiedPredicateRef> qualifiedRefs,
        ShmoBuildMode buildMode,
        List<ShmoCompileError> errors,
        List<ShmoCompileError>? warnings = null,
        bool arityCompat = false)
    {
        // Partition raw clauses: dynamic-head ones become DynamicSeeds
        // (RAW), the rest go through the same DcgTransform +
        // MetaTransform + PhraseTransform pipeline ConsultString uses.
        // Helper clauses MetaTransform adds (catch's first arg becomes
        // a separate `$catchgoal_N/M` clause, etc.) end up in the
        // static set — they're synthetic, never dynamic.
        var dynamicSeedAccum = new Dictionary<PredicateRef, List<byte[]>>();
        var staticInput = new List<Clause>(rawClauses.Count);
        foreach (var clause in rawClauses)
        {
            PredicateRef? head = TryExtractHead(clause);
            if (head is not null && dynamicSet.Contains(head.Value))
            {
                if (!dynamicSeedAccum.TryGetValue(head.Value, out var seedList))
                {
                    seedList = new List<byte[]>();
                    dynamicSeedAccum[head.Value] = seedList;
                }
                seedList.Add(TermCodec.EncodeClause(clause));
            }
            else
            {
                staticInput.Add(clause);
            }
        }

        // Now apply the static-pipeline transforms. These may add
        // synthetic helper clauses (MetaTransform extracts catch's
        // protected goal, ; / -> branches, etc. into helper preds).
        // Chunk 407 — module-local meta-wrapper unfold first (see
        // MetaWrapperUnfold): staticInput excludes dynamic-head clauses,
        // so any detected wrapper is immutable.
        // Chunk 441 — the pipeline is split at the MetaTransform
        // boundary: preMeta (post-unfold, post-DCG, pre-MetaTransform)
        // is the LAST stage where meta-call structure is still visible
        // (MetaTransform's chunk-205 rewrite turns `call(g(X))` into a
        // direct `g(X)` and inlines findall/bagof/... goals into
        // helper bodies). The DIRECT-vs-META edge marking walks
        // preMeta; the call-graph EDGES still walk the fully
        // transformed clauses below, unchanged.
        var preMeta = DcgTransform.Apply(MetaWrapperUnfold.Apply(staticInput));
        var clauses = PhraseTransform.Apply(MetaTransform.Apply(preMeta));

        // Chunk 441 — module-wide DIRECT / META reference sets. A
        // target lands in metaRefs when referenced from inside a
        // meta-call argument, in directRefs when referenced as a plain
        // body goal; an edge's IsMeta = metaRefs ∧ ¬directRefs (see
        // ShmoCallEdge). Dynamic-head clauses' RAW bodies are walked
        // too, mirroring the edge walk below.
        var metaRefs = new HashSet<PredicateRef>();
        var directRefs = new HashSet<PredicateRef>();
        foreach (var clause in preMeta)
        {
            if (TryExtractHead(clause) is null) continue;
            MarkCalls(ExtractBody(clause), inMeta: false, metaRefs, directRefs);
        }
        foreach (var rawClause in rawClauses)
        {
            PredicateRef? rawHead = TryExtractHead(rawClause);
            if (rawHead is null || !dynamicSet.Contains(rawHead.Value)) continue;
            MarkCalls(ExtractBody(rawClause), inMeta: false, metaRefs, directRefs);
        }

        var definedOrder = new List<PredicateRef>();
        var definedSet = new HashSet<PredicateRef>();
        var callGraph = new Dictionary<PredicateRef, HashSet<PredicateRef>>();
        var staticClauses = new List<Clause>(clauses.Count);

        // Static clauses' call graph: walk transformed bodies (so
        // MetaTransform-emitted helper-call references show up in
        // the graph).
        foreach (var clause in clauses)
        {
            PredicateRef? head = TryExtractHead(clause);
            if (head is null)
            {
                staticClauses.Add(clause);
                continue;
            }
            if (definedSet.Add(head.Value))
                definedOrder.Add(head.Value);
            if (!callGraph.TryGetValue(head.Value, out var edges))
            {
                edges = new HashSet<PredicateRef>();
                callGraph[head.Value] = edges;
            }
            Term body = ExtractBody(clause);
            CollectCalls(body, edges, qualifiedRefs);
            staticClauses.Add(clause);
        }

        // Dynamic clauses' call graph: walk RAW bodies, since that's
        // what the linker needs to know about. The engine's runtime
        // MetaTransform will produce the same helper-call edges then,
        // but the linker can't see across that boundary yet so we
        // walk the user-visible callees only. CollectCalls already
        // descends into catch / findall / etc. (chunk 209), so this
        // surfaces blint_version, blint_exit, etc. from a body like
        // `main :- catch((blint_version(V), ...), E, ...).`.
        foreach (var (head, encodedList) in dynamicSeedAccum)
        {
            if (definedSet.Add(head))
                definedOrder.Add(head);
            if (!callGraph.TryGetValue(head, out var edges))
            {
                edges = new HashSet<PredicateRef>();
                callGraph[head] = edges;
            }
        }
        foreach (var rawClause in rawClauses)
        {
            PredicateRef? head = TryExtractHead(rawClause);
            if (head is null || !dynamicSet.Contains(head.Value)) continue;
            Term body = ExtractBody(rawClause);
            CollectCalls(body, callGraph[head.Value], qualifiedRefs);
        }

        // A :- dynamic declaration with no clauses still counts as
        // defined (with visibility=Dynamic) — the linker uses it to
        // satisfy references.
        foreach (var d in dynamicSet)
        {
            if (definedSet.Add(d))
                definedOrder.Add(d);
            if (!callGraph.ContainsKey(d))
                callGraph[d] = new HashSet<PredicateRef>();
        }

        var defined = new List<ShmoDefinedPredicate>(definedOrder.Count);
        foreach (var p in definedOrder)
        {
            var vis = dynamicSet.Contains(p)
                ? PredicateVisibility.Dynamic
                : publicSet.Contains(p)
                    ? PredicateVisibility.Public
                    : PredicateVisibility.Local;
            defined.Add(new ShmoDefinedPredicate(p, vis));
        }

        // Chunk 176: apply ModuleRewrite so the .shmo's bytecode is
        // runtime-ready — every local-functor head and call site
        // carries the same `module$name` mangling SetupQueryFromTerm
        // would apply at consult time. Without this the bytecode
        // references bare ids that don't match what the engine wires
        // for dispatch, and the LoadBundle precompiled-cache
        // substitution has to stay disabled (PrologEngine.cs:2003-
        // 2017). With it, the .shmo bytecode is byte-identical to
        // what ConsultString produces and the source-less LoadBundle
        // path can plug the predicates directly into the static link
        // region without re-consulting source.
        var publicFids = new HashSet<int>();
        foreach (var p in publicSet)
            publicFids.Add(Shumway.Core.FunctorTable.Intern(
                Shumway.Core.AtomTable.Intern(p.Name, permanent: true).Id, p.Arity));
        var dynamicFids = new HashSet<int>();
        foreach (var d in dynamicSet)
            dynamicFids.Add(Shumway.Core.FunctorTable.Intern(
                Shumway.Core.AtomTable.Intern(d.Name, permanent: true).Id, d.Arity));
        var localFids = new HashSet<int>();
        foreach (var p in definedOrder)
        {
            int fid = Shumway.Core.FunctorTable.Intern(
                Shumway.Core.AtomTable.Intern(p.Name, permanent: true).Id, p.Arity);
            if (!publicFids.Contains(fid)) localFids.Add(fid);
        }
        // The mangling context uses the RESOLVED runtime module name (the
        // directive's argument, else the per-file fallback — chunk 440),
        // matching what the engine applies when it loads the entry: the
        // source-bearing LoadBundle path consults under the entry's module
        // name, and the source-less path's dynamic-seed rehydration rewrites
        // under the same name via _dynamicSeedModule.
        var rewriteCtx = new ModuleRewrite.Context(moduleName, localFids, dynamicFids);
        var rewritten = new List<Clause>(staticClauses.Count);
        foreach (var clause in staticClauses)
            rewritten.Add(ModuleRewrite.Rewrite(clause, rewriteCtx));

        // Chunk 177: Release drops the per-clause Meta/DbgInfo markers
        // from the emitted bytecode. Combined with the Source-string
        // strip below, a Release .shmo carries no debug information at
        // all — that's what `-r` promises and IP-protection workflows
        // rely on. Debug keeps the markers so stack-trace mapping
        // continues to work in dev builds.
        var moduleCompiler = new ModuleCompiler
        {
            EmitDebugInfo = buildMode != ShmoBuildMode.Release,
        };
        var module = moduleCompiler.Compile(rewritten);
        byte[] bytecode = CompiledModuleCodec.Encode(module);

        // Chunk 177: Release also drops the Source string from the
        // .shmo. Combined with the bytecode-side DbgInfo strip above,
        // the Release artifact contains no recoverable Prolog source —
        // and chunk 178's source-less LoadBundle path means the engine
        // can still dispatch it. Debug keeps Source so the linker's
        // map output, the IL warmup's source-position helpers, and
        // standard "consult from source" tooling stay intact.
        string persistedSource = buildMode == ShmoBuildMode.Release ? "" : source;

        // Chunk 441 — stamp each edge with the module-wide
        // DIRECT-vs-META marker (meta-only targets get IsMeta=true).
        var callGraphRO = new Dictionary<PredicateRef, IReadOnlyList<ShmoCallEdge>>();
        foreach (var (k, v) in callGraph)
        {
            var edgeArr = new ShmoCallEdge[v.Count];
            int ei = 0;
            foreach (var t in v)
                edgeArr[ei++] = new ShmoCallEdge(
                    t, metaRefs.Contains(t) && !directRefs.Contains(t));
            callGraphRO[k] = edgeArr;
        }

        var dynamicSeeds = new List<ShmoDynamicSeed>(dynamicSeedAccum.Count);
        foreach (var (ind, encodedList) in dynamicSeedAccum)
            dynamicSeeds.Add(new ShmoDynamicSeed(ind, encodedList));

        // Chunk 411 — the LTO channel: persist the RAW static clauses
        // (pre-unfold, pre-pipeline) so the linker can re-run the full
        // transform stack (cross-module unfold included) and recompile this
        // module without its source. Release included by design — the .shmo
        // is an intermediate artifact; IP stripping applies to .shum/exe.
        var clauseTerms = new List<byte[]>(staticInput.Count);
        foreach (var clause in staticInput)
            clauseTerms.Add(TermCodec.EncodeClause(clause));

        var obj = new ShmoObject(
            moduleName: moduleName,
            source: persistedSource,
            bytecode: bytecode,
            defined: defined,
            ensureLinked: ensureLinked,
            callGraph: callGraphRO,
            qualifiedRefs: qualifiedRefs,
            buildMode: buildMode,
            dynamicSeeds: dynamicSeeds,
            clauseTerms: clauseTerms,
            arityCompat: arityCompat);
        return new ShmoCompileResult(obj, errors, warnings);
    }

    /// <summary>Like <see cref="TryCompileSource"/> but reads the
    /// source from <paramref name="path"/> and uses the file's bare
    /// name (sans extension) as the module-name fallback.</summary>
    public static ShmoCompileResult TryCompileFile(string path,
        ShmoBuildMode buildMode = ShmoBuildMode.Release,
        int maxErrors = 100,
        bool arityCompat = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        string source = File.ReadAllText(path);
        string fallback = Path.GetFileNameWithoutExtension(path);
        return TryCompileSource(source, fallback, buildMode, maxErrors, arityCompat);
    }

    // ------------------------------------------------------------------------
    // Directive handling
    // ------------------------------------------------------------------------

    /// <summary>Returns true iff the directive was a <c>:- module/1</c>;
    /// a module directive overwrites <paramref name="moduleName"/> (which
    /// arrives pre-seeded with the per-file fallback — chunk 440).</summary>
    private static bool ProcessDirective(Term body, ref string moduleName,
        HashSet<PredicateRef> publicSet,
        HashSet<PredicateRef> dynamicSet,
        List<PredicateRef> ensureLinked)
    {
        if (body is CompoundTerm m && m.Functor == "module" && m.Args.Length == 1
            && m.Args[0] is AtomTerm a)
        {
            moduleName = a.Name;
            return true;
        }
        if (body is CompoundTerm pub && pub.Functor == "public" && pub.Args.Length == 1)
        {
            foreach (var spec in ReadFunctorSpecs(pub.Args[0], "public"))
                publicSet.Add(spec);
            return false;
        }
        // `dynamic` and its Arity-Prolog alias `visible` (chunk 265).
        if (body is CompoundTerm dyn
            && (dyn.Functor == "dynamic" || dyn.Functor == "visible")
            && dyn.Args.Length == 1)
        {
            foreach (var spec in ReadFunctorSpecs(dyn.Args[0], dyn.Functor))
                dynamicSet.Add(spec);
            return false;
        }
        // :- ensure_linked(Indicator) — GNU-Prolog-style hint that the
        // named predicate is reachable even though the static call
        // graph doesn't show it (typically because it's the target of a
        // runtime meta-call). The linker (chunk 163) treats every
        // ensure_linked indicator as an additional reachability root,
        // so the predicate's defining module survives dead-code
        // elimination and its own callees get walked.
        if (body is CompoundTerm el && el.Functor == "ensure_linked" && el.Args.Length == 1)
        {
            foreach (var spec in ReadFunctorSpecs(el.Args[0], "ensure_linked"))
                ensureLinked.Add(spec);
            return false;
        }
        // Other directives (op/3, set_prolog_flag, etc.) are ignored
        // by the shmo writer — they don't affect link-time semantics.
        return false;
    }

    private static IEnumerable<PredicateRef> ReadFunctorSpecs(Term arg, string directive)
    {
        var collected = new List<PredicateRef>();
        if (TryReadSpecForms(arg, collected)) return collected;
        throw new InvalidOperationException(
            $"Malformed :- {directive} directive (expected Name/Arity, a list of them, "
            + "or a comma-separated sequence).");
    }

    /// <summary>Accepts a single <c>Name/Arity</c>, a Prolog list of
    /// them, or a comma-conjunction (<c>a/0, b/1, c/2</c>) — GNU
    /// Prolog's grouped <c>:- dynamic</c> / <c>:- public</c> form.
    /// Returns <c>false</c> if any leaf doesn't parse as a functor
    /// spec.</summary>
    private static bool TryReadSpecForms(Term term, List<PredicateRef> output)
    {
        if (TryReadFunctorSpec(term, out var single))
        {
            output.Add(single);
            return true;
        }
        // Comma-conjunction: walk both sides.
        if (term is CompoundTerm conj && conj.Functor == "," && conj.Args.Length == 2)
        {
            return TryReadSpecForms(conj.Args[0], output)
                && TryReadSpecForms(conj.Args[1], output);
        }
        // Prolog list: walk cons cells.
        Term cursor = term;
        int start = output.Count;
        while (cursor is CompoundTerm cons && cons.Functor == "." && cons.Args.Length == 2)
        {
            if (!TryReadFunctorSpec(cons.Args[0], out var spec))
            {
                output.RemoveRange(start, output.Count - start);
                return false;
            }
            output.Add(spec);
            cursor = cons.Args[1];
        }
        if (cursor is AtomTerm { Name: "[]" }) return true;
        output.RemoveRange(start, output.Count - start);
        return false;
    }

    private static bool TryReadFunctorSpec(Term term, out PredicateRef spec)
    {
        if (term is CompoundTerm slash && slash.Functor == "/" && slash.Args.Length == 2
            && slash.Args[0] is AtomTerm name)
        {
            Term arityTerm = slash.Args[1];
            // Phase 30 (arity_compat) — strip an Arity directive annotation
            // (`foo/8:far`, `f/2:system(...)`); see PrologEngine's twin.
            if (arityTerm is CompoundTerm colon && colon.Functor == ":"
                && colon.Args.Length == 2)
                arityTerm = colon.Args[0];
            if (arityTerm is IntTerm arity)
            {
                spec = new PredicateRef(name.Name, (int)arity.Value);
                return true;
            }
        }
        spec = default;
        return false;
    }

    // ------------------------------------------------------------------------
    // Clause head extraction
    // ------------------------------------------------------------------------

    private static PredicateRef? TryExtractHead(Clause c)
    {
        Term headTerm = c.Kind == ClauseKind.Rule
            && c.Term is CompoundTerm rule
            && rule.Functor == ":-" && rule.Args.Length == 2
                ? rule.Args[0]
                : c.Term;

        return headTerm switch
        {
            AtomTerm at => new PredicateRef(at.Name, 0),
            CompoundTerm ct => new PredicateRef(ct.Functor, ct.Args.Length),
            _ => null,
        };
    }

    private static Term ExtractBody(Clause c)
    {
        if (c.Kind == ClauseKind.Rule
            && c.Term is CompoundTerm rule
            && rule.Functor == ":-" && rule.Args.Length == 2)
        {
            return rule.Args[1];
        }
        return new AtomTerm("true");
    }

    // ------------------------------------------------------------------------
    // Body walking — extract every call site
    // ------------------------------------------------------------------------

    private static void CollectCalls(Term body,
        HashSet<PredicateRef> edges,
        List<QualifiedPredicateRef> qualifiedRefs)
    {
        switch (body)
        {
            case CompoundTerm c:
                // Conjunction / disjunction / if-then / soft cut / not-provable
                // — control structures, descend into args but emit nothing.
                if ((c.Functor == "," || c.Functor == ";" || c.Functor == "->"
                     || c.Functor == "*->" ) && c.Args.Length == 2)
                {
                    CollectCalls(c.Args[0], edges, qualifiedRefs);
                    CollectCalls(c.Args[1], edges, qualifiedRefs);
                    return;
                }
                if ((c.Functor == "\\+" || c.Functor == "not") && c.Args.Length == 1)
                {
                    CollectCalls(c.Args[0], edges, qualifiedRefs);
                    return;
                }
                // Module-qualified goal: Module:Goal. Emit a qualified
                // ref (resolved against that module's public set by the
                // linker) and don't add the goal to the unqualified
                // edges — it's not a free reference.
                if (c.Functor == ":" && c.Args.Length == 2
                    && c.Args[0] is AtomTerm modAtom)
                {
                    AddQualifiedCallTarget(modAtom.Name, c.Args[1], qualifiedRefs);
                    return;
                }
                // call/1 with a statically known goal: descend.
                if (c.Functor == "call" && c.Args.Length == 1)
                {
                    CollectCalls(c.Args[0], edges, qualifiedRefs);
                    return;
                }
                // Meta-builtins that take a Goal argument and run it.
                // Without descending into the goal, the call graph
                // misses every predicate that appears only inside
                // catch / findall / bagof / setof / forall / once /
                // ignore / not — and the linker drops them, so the
                // bundle can't dispatch them at runtime. The outer
                // builtin itself stays as a regular edge (it's a
                // registered builtin so the linker resolves it).
                if (c.Functor == "catch" && c.Args.Length == 3)
                {
                    edges.Add(new PredicateRef("catch", 3));
                    CollectCalls(c.Args[0], edges, qualifiedRefs);
                    CollectCalls(c.Args[2], edges, qualifiedRefs);
                    return;
                }
                if ((c.Functor == "findall" && c.Args.Length == 3)
                    || (c.Functor == "bagof" && c.Args.Length == 3)
                    || (c.Functor == "setof" && c.Args.Length == 3))
                {
                    edges.Add(new PredicateRef(c.Functor, 3));
                    CollectCalls(c.Args[1], edges, qualifiedRefs);
                    return;
                }
                if (c.Functor == "findall" && c.Args.Length == 4)
                {
                    edges.Add(new PredicateRef("findall", 4));
                    CollectCalls(c.Args[1], edges, qualifiedRefs);
                    return;
                }
                if (c.Functor == "forall" && c.Args.Length == 2)
                {
                    edges.Add(new PredicateRef("forall", 2));
                    CollectCalls(c.Args[0], edges, qualifiedRefs);
                    CollectCalls(c.Args[1], edges, qualifiedRefs);
                    return;
                }
                if ((c.Functor == "once" || c.Functor == "ignore")
                    && c.Args.Length == 1)
                {
                    edges.Add(new PredicateRef(c.Functor, 1));
                    CollectCalls(c.Args[0], edges, qualifiedRefs);
                    return;
                }
                // Anything else is a direct call site — emit name/arity.
                edges.Add(new PredicateRef(c.Functor, c.Args.Length));
                return;

            case AtomTerm a:
                // Cut is structural — not a call.
                if (a.Name == "!") return;
                // Atom as goal — name/0.
                edges.Add(new PredicateRef(a.Name, 0));
                return;

            // Numbers / strings / vars as goals are call/1 fodder — at
            // shmo time we have no way to resolve them; the user must
            // declare :- ensure_linked/1 for any predicate reachable
            // only via runtime meta-call.
            default:
                return;
        }
    }

    // ------------------------------------------------------------------------
    // Chunk 441 — DIRECT-vs-META reference marking
    // ------------------------------------------------------------------------

    /// <summary>Walks a PRE-MetaTransform body recording every unqualified
    /// reference into <paramref name="directRefs"/> (plain body goal) or
    /// <paramref name="metaRefs"/> (inside a meta-call argument). The meta
    /// positions: <c>call/1</c>'s goal (recursively), a <c>call/N</c>
    /// closure's effective target (<c>call(g, X)</c> → <c>g/1</c>), and the
    /// goal arguments of <c>catch/3</c> (goal + recovery),
    /// <c>findall/3,4</c>, <c>bagof/3</c>, <c>setof/3</c>, <c>forall/2</c>,
    /// <c>once/1</c>, <c>ignore/1</c>, <c>\+/1</c> and <c>not/1</c> — every
    /// construct whose goal is meta-dispatched at runtime, matching the set
    /// <see cref="CollectCalls"/> descends into. Control constructs
    /// (<c>,</c> <c>;</c> <c>-&gt;</c> <c>*-&gt;</c>) propagate the current
    /// context; a <c>^/2</c> existential wrapper inside a meta goal is
    /// transparent. Module-qualified goals are skipped (the qualified-ref
    /// channel resolves those). The walk mirrors <see cref="CollectCalls"/>'
    /// target shapes so the recorded indicators line up with the edges the
    /// transformed-body walk emits.</summary>
    private static void MarkCalls(Term body, bool inMeta,
        HashSet<PredicateRef> metaRefs, HashSet<PredicateRef> directRefs)
    {
        switch (body)
        {
            case CompoundTerm c:
                if ((c.Functor == "," || c.Functor == ";" || c.Functor == "->"
                     || c.Functor == "*->") && c.Args.Length == 2)
                {
                    MarkCalls(c.Args[0], inMeta, metaRefs, directRefs);
                    MarkCalls(c.Args[1], inMeta, metaRefs, directRefs);
                    return;
                }
                // Negation-as-failure runs its argument as a goal — under
                // Arity, `\+ und_fact(X)` over a never-asserted fact
                // predicate is valid (it succeeds). META.
                if ((c.Functor == "\\+" || c.Functor == "not") && c.Args.Length == 1)
                {
                    MarkCalls(c.Args[0], inMeta: true, metaRefs, directRefs);
                    return;
                }
                if (c.Functor == ":" && c.Args.Length == 2
                    && c.Args[0] is AtomTerm)
                    return;   // qualified ref — separate resolution channel.
                if (c.Functor == "call" && c.Args.Length == 1)
                {
                    MarkCalls(c.Args[0], inMeta: true, metaRefs, directRefs);
                    return;
                }
                // call/N closure: the effective runtime target is the
                // closure's functor with the extra args appended —
                // exactly what the chunk-205 static rewrite turns it
                // into, so the recorded indicator matches that edge.
                // Control-construct closures (call((a,b), X) etc.) stay
                // on the runtime CallBuiltin path and produce no edge;
                // skip them here too.
                if (c.Functor == "call" && c.Args.Length >= 2)
                {
                    int extra = c.Args.Length - 1;
                    if (c.Args[0] is AtomTerm closA
                        && !IsControlConstructName(closA.Name, extra))
                        metaRefs.Add(new PredicateRef(closA.Name, extra));
                    else if (c.Args[0] is CompoundTerm closC
                        && !IsControlConstructName(closC.Functor, closC.Args.Length + extra))
                        metaRefs.Add(new PredicateRef(closC.Functor, closC.Args.Length + extra));
                    return;
                }
                if (c.Functor == "catch" && c.Args.Length == 3)
                {
                    MarkCalls(c.Args[0], inMeta: true, metaRefs, directRefs);
                    MarkCalls(c.Args[2], inMeta: true, metaRefs, directRefs);
                    return;
                }
                if ((c.Functor == "findall" || c.Functor == "bagof"
                     || c.Functor == "setof") && c.Args.Length == 3)
                {
                    MarkCalls(c.Args[1], inMeta: true, metaRefs, directRefs);
                    return;
                }
                if (c.Functor == "findall" && c.Args.Length == 4)
                {
                    MarkCalls(c.Args[1], inMeta: true, metaRefs, directRefs);
                    return;
                }
                if (c.Functor == "forall" && c.Args.Length == 2)
                {
                    MarkCalls(c.Args[0], inMeta: true, metaRefs, directRefs);
                    MarkCalls(c.Args[1], inMeta: true, metaRefs, directRefs);
                    return;
                }
                if ((c.Functor == "once" || c.Functor == "ignore")
                    && c.Args.Length == 1)
                {
                    MarkCalls(c.Args[0], inMeta: true, metaRefs, directRefs);
                    return;
                }
                // Existential quantifier inside a bagof/setof goal —
                // transparent wrapper around the real goal.
                if (c.Functor == "^" && c.Args.Length == 2 && inMeta)
                {
                    MarkCalls(c.Args[1], inMeta: true, metaRefs, directRefs);
                    return;
                }
                (inMeta ? metaRefs : directRefs)
                    .Add(new PredicateRef(c.Functor, c.Args.Length));
                return;

            case AtomTerm a:
                if (a.Name == "!") return;
                (inMeta ? metaRefs : directRefs)
                    .Add(new PredicateRef(a.Name, 0));
                return;

            default:
                return;
        }
    }

    /// <summary>The control constructs the chunk-205 static
    /// <c>call/N</c> rewrite refuses to extend (they stay on the runtime
    /// dispatcher and never produce a call-graph edge). Mirrors
    /// <c>MetaTransform.IsStaticallyExtendable</c>'s exclude set.</summary>
    private static bool IsControlConstructName(string name, int effectiveArity)
        => (name, effectiveArity) switch
        {
            (",", 2) or (";", 2) or ("->", 2) or ("*->", 2) => true,
            ("\\+", 1) or ("not", 1) => true,
            ("!", 0) => true,
            ("catch", 3) or ("throw", 1) => true,
            ("call", _) => true,
            _ => false,
        };

    private static void AddQualifiedCallTarget(string module, Term goal,
        List<QualifiedPredicateRef> qrefs)
    {
        switch (goal)
        {
            case AtomTerm a:
                if (a.Name != "!")
                    qrefs.Add(new QualifiedPredicateRef(module, a.Name, 0));
                return;
            case CompoundTerm c:
                qrefs.Add(new QualifiedPredicateRef(module, c.Functor, c.Args.Length));
                return;
        }
    }
}
