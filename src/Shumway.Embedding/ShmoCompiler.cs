using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;

namespace Shumway.Embedding;

/// <summary>
/// Compiles one Prolog source file (or in-memory source) into an in-
/// memory <see cref="ShmoObject"/>. Used by the <c>shumway-compile</c>
/// CLI and by the linker's tests; the in-process embedder
/// can also call it to produce <c>.shmo</c> artifacts on the fly.
///
/// <para>Pipeline:</para>
/// <list type="number">
/// <item>Parse with <see cref="ClauseReader"/>.</item>
/// <item>Apply <see cref="DcgTransform"/> (DCG rules become normal rules
/// with the diff-list pair appended to head &amp; goals).</item>
/// <item>Walk directives for <c>:- module/1</c>, <c>:- public/1</c>,
/// <c>:- dynamic/1</c>, <c>:- ensure_linked/1</c>.</item>
/// <item>For every non-directive clause, classify the head's
/// <c>Name/Arity</c>, attach the right visibility, and walk the body
/// emitting call edges into the per-predicate call graph.</item>
/// <item>Compile the surviving rule/fact clauses via
/// <see cref="ModuleCompiler"/> and encode through
/// <see cref="CompiledModuleCodec"/>.</item>
/// </list>
///
/// <para>The linker filters out builtins from the call
/// graph and resolves the remainder against the union of every loaded
/// <c>.shmo</c>'s <c>:- public</c>/<c>:- dynamic</c> set. Anything still
/// unresolved is the missing-predicate report.</para>
/// </summary>
public static class ShmoCompiler
{
    /// <summary>every directive name some stage of the
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
        "discontiguous", "multifile", "table", "mode", "native",
        "c", "prolog", "meta_predicate", "non_counted_backtracking", "use_module",
        // ISO include/1 (expanded before this pass) + initialization/1
        // (collected by the consult path; carried as an entry goal here).
        "include", "initialization",
        // ADR-022 — the synthetic directive the ClauseReader
        // emits to carry a captured `:- c` region's raw declaration text. An
        // ignored directive until the C-subset parser consumes it (step 2).
        "$native_decls",
    };

    /// <summary>unknown directives whose arity_compat
    /// warning is suppressed entirely. Callers (or embedders) add names
    /// of directives they know are harmless noise in their corpus.
    /// Seeds <c>extrn</c> — Arity's external-predicate
    /// declaration is ubiquitous in real sources and has no Shumway
    /// meaning (the linker resolves cross-module calls itself), so it
    /// is ignored without even a warning. The engine consult path
    /// (<see cref="PrologEngine"/>'s ConsultString warning pass)
    /// consults this same set.</summary>
    public static readonly HashSet<string> SilentlyIgnoredDirectives = new()
    {
        "extrn",
        // SWI/SICStus declaration directives with no Shumway meaning — no-op'd so
        // a library that declares them loads (ADR-040 SWI triage). Semantics we do
        // not (yet) act on: predicate_options (option documentation),
        // module_transparent (module transparency — our $mqual threads modules),
        // create_prolog_flag (user flags), current_arithmetic_function (custom
        // eval), reexport (re-export — the importer sees the source module's
        // exports), redefine_system_predicate, det (determinism doc), at_halt
        // (halt hooks), '$hide', format_predicate (custom ~ directives), encoding
        // (we are UTF-8).
        "predicate_options", "module_transparent", "create_prolog_flag",
        "current_arithmetic_function", "reexport", "redefine_system_predicate",
        "det", "at_halt", "$hide", "format_predicate", "encoding", "volatile",
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
        var result = TryCompileSource(source, fallback, buildMode,
            includeBaseDir: Path.GetDirectoryName(Path.GetFullPath(path)));
        if (result.Errors.Count > 0)
        {
            var first = result.Errors[0];
            throw new InvalidOperationException(
                $"{first.Line}:{first.Column}: {first.Message}");
        }
        return result.Object!;
    }

    /// <summary>Compiles <paramref name="source"/> in memory.
    /// <paramref name="moduleNameFallback"/> is the module name when the
    /// source has no <c>:- module/1</c> directive (it is no
    /// longer collapsed to "user"; per-file module identity is what keeps
    /// two module-less files' locals from aliasing). Pass the empty
    /// string for "<c>user</c>" — Shumway's default.</summary>
    public static ShmoObject CompileSource(string source,
        string moduleNameFallback = "user",
        ShmoBuildMode buildMode = ShmoBuildMode.Release,
        Func<PredicateRef, bool>? clauseFilter = null)
    {
        var result = TryCompileSource(source, moduleNameFallback, buildMode,
            maxErrors: 100, clauseFilter: clauseFilter);
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
    /// <param name="clauseFilter">Prelude pruning — when non-null,
    /// only clauses whose HEAD indicator satisfies the filter are compiled;
    /// directives are unaffected. Used by the linker to bake a reachability-
    /// reduced prelude.</param>
    public static ShmoCompileResult TryCompileSource(string source,
        string moduleNameFallback = "user",
        ShmoBuildMode buildMode = ShmoBuildMode.Release,
        int maxErrors = 100,
        bool arityCompat = false,
        Func<PredicateRef, bool>? clauseFilter = null,
        string? includeBaseDir = null)
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
        // `shumway-compile --arity` pre-enables the Arity
        // compatibility mode for the whole file (the in-file
        // set_prolog_flag(arity_compat, _) directive can still flip it).
        var readerFlags = new Shumway.Compiler.Parsing.PrologFlags
        { ArityCompat = arityCompat };
        // record whether Arity mode was EVER on during the
        // compile (the --arity pre-enable, or an in-file
        // set_prolog_flag(arity_compat, true) flip at any point). The
        // resulting ShmoObject carries it so the linker can apply Arity
        // call semantics (undeclared predicate → implicit empty
        // dynamic) to this module's unresolved references.
        bool arityEverOn = arityCompat;
        var operatorTable = OperatorTable.Default();
        foreach (var entry in new ClauseReader(new Lexer(source),
                     operatorTable, readerFlags)
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

        // ISO `:- include(File)` — textual inclusion (IncludeExpander), so
        // `shumway-compile loader.pl` compiles the whole included tree into
        // one object. Included files parse against the SAME operator table
        // (an :- op/3 in an earlier include is live for later siblings).
        // Errors inside an included file surface as a compile error naming
        // that file (no per-included-file recovery in this first cut).
        if (Shumway.Compiler.Parsing.IncludeExpander.HasInclude(allClauses))
        {
            try
            {
                allClauses = Shumway.Compiler.Parsing.IncludeExpander.Expand(
                    allClauses, includeBaseDir, operatorTable, readerFlags);
            }
            catch (Exception ex) when (ex is Shumway.Compiler.Parsing.ParseException
                                       or FileNotFoundException or InvalidOperationException)
            {
                errors.Add(new ShmoCompileError(ex.Message, 0, 0));
                return new ShmoCompileResult(null, errors, warnings);
            }
        }
        // First pass: walk RAW (untransformed) clauses to read
        // directives (so we know which predicates are dynamic) and
        // collect the raw bodies. We need the raw bodies for two
        // reasons:
        //
        //   1. a `:- dynamic foo/N.` clause must be
        //      serialised RAW to DynamicSeeds, because the engine's
        //      SetupQueryFromTerm runs DcgTransform / MetaTransform /
        //      PhraseTransform on _dynamicClauses AGAIN — mirroring
        //      what ConsultString does at line 4022. Pre-transforming
        //      here would double-apply.
        //
        //   2. Local-pred call-graph edges from inside catch / findall /
        //      etc. need to be visible — CollectCalls descends into
        //      meta-builtin goal args so the raw body still
        //      enumerates everything reachable.
        // the fallback (the file's base name when compiling a
        // file, "user" for bare in-memory sources) IS the module name when
        // no `:- module/1` directive is present. The forcing of
        // PrologEngine.DefaultModuleName here made every module-less file
        // compile as module "user": two such files could never be linked
        // together (duplicate_module), and their locals would have aliased
        // (`user$helper`) even if the linker had allowed it. The consumers
        // this was protecting — dynamic-seed rehydration's
        // ModuleRewrite context and the bundle-local-fid feed — are now
        // per-entry-module-aware (see PrologEngine.LoadEntryFromBytecode +
        // SetupQueryFromTerm's _dynamicSeedModule attribution).
        string moduleName = string.IsNullOrEmpty(moduleNameFallback)
            ? PrologEngine.DefaultModuleName
            : moduleNameFallback;
        var publicSet = new HashSet<PredicateRef>();
        var dynamicSet = new HashSet<PredicateRef>();
        var multifileSet = new HashSet<PredicateRef>();
        var nativeSet = new HashSet<PredicateRef>();
        // ADR-038 — export-qualified module state (a `:- module(Name, [Exports])`
        // source), its library dependencies, and the resolved import table.
        bool isExportQualified = false;
        var exportSet = new HashSet<PredicateRef>();
        var libraryDeps = new List<ShmoLibraryDep>();
        var importEntries = new List<ShmoImportEntry>();
        var ensureLinked = new List<PredicateRef>();
        var tabledSet = new HashSet<PredicateRef>();
        var qualifiedRefs = new List<QualifiedPredicateRef>();
        var rawClauses = new List<Clause>();
        // ADR-022 — accumulate the raw text of every `:- c` region (carried as a
        // synthetic `'$native_decls'(Text)` directive) so CompileFromParts can
        // build the C symbol table for native-block type inference.
        var nativeDecls = new System.Text.StringBuilder();
        // Collect the `:- op/3` definitions this
        // source executed (ClauseReader already applied them to the parse
        // table in-place; here we RECORD them, in source order and with
        // list-name forms expanded) so they travel .shmo → .shum and
        // LoadBundle replays them into the runtime operator table.
        var operatorDefs = new List<ShmoOperatorDef>();

        foreach (var clause in allClauses)
        {
            if (clause.Kind == ClauseKind.Directive
                && clause.Term is CompoundTerm d
                && d.Functor == ":-" && d.Args.Length == 1)
            {
                if (d.Args[0] is CompoundTerm nd && nd.Functor == "$native_decls"
                    && nd.Args.Length == 1 && nd.Args[0] is StringTerm ndText)
                {
                    nativeDecls.Append(ndText.Content).Append('\n');
                    continue;
                }
                if (d.Args[0] is CompoundTerm { Functor: "op", Args.Length: 3 } opDir
                    && opDir.Args[0] is IntTerm opPrio
                    && opDir.Args[1] is AtomTerm opType)
                {
                    // Single atom or the conventional list-of-names form.
                    Term names = opDir.Args[2];
                    if (names is AtomTerm single)
                        operatorDefs.Add(new ShmoOperatorDef(
                            (int)opPrio.Value, opType.Name, single.Name));
                    else
                        while (names is CompoundTerm { Functor: ".", Args.Length: 2 } cons)
                        {
                            if (cons.Args[0] is AtomTerm nm)
                                operatorDefs.Add(new ShmoOperatorDef(
                                    (int)opPrio.Value, opType.Name, nm.Name));
                            names = cons.Args[1];
                        }
                    // Fall through to ProcessDirective/warning path? No —
                    // op/3 is fully handled (parse-time by ClauseReader,
                    // persistence here); nothing else to do.
                    continue;
                }
                // ADR-038 — `:- module(Name, [Exports])` (two-arg) is an
                // export-qualified module: every predicate is mangled Name$x (so
                // publicSet stays empty and ModuleRewrite mangles them all) and the
                // export list is the importable surface.
                if (d.Args[0] is CompoundTerm { Functor: "module", Args.Length: 2 } modDir
                    && modDir.Args[0] is AtomTerm exqName)
                {
                    moduleName = exqName.Name;
                    isExportQualified = true;
                    foreach (var spec in ReadPiListLenient(modDir.Args[1]))
                        exportSet.Add(spec);
                    continue;
                }
                // ADR-038 — `:- use_module(library(X))` / `…, [Filter]`. Record the
                // dependency for the linker; for a filtered file import, resolve the
                // import table now (lib name = module name) so ModuleRewrite mangles
                // the importer's calls to X$pred.
                if (d.Args[0] is CompoundTerm { Functor: "use_module" } umDir
                    && umDir.Args.Length is 1 or 2
                    && TryReadLibrarySpec(umDir.Args[0], out string libName, out bool baked))
                {
                    IReadOnlyList<PredicateRef>? filter = null;
                    if (umDir.Args.Length == 2)
                        filter = new List<PredicateRef>(ReadPiListLenient(umDir.Args[1]));
                    libraryDeps.Add(new ShmoLibraryDep(libName, filter, baked));
                    // /2 imports are resolved from the SOURCE alone (the filter is
                    // the list of imported indicators) — the compiler never needs
                    // the library. /1 (import-all) is resolved by the LINKER, which
                    // has the library's export surface; the compiler only records
                    // the dependency here.
                    if (!baked && filter is not null)
                        foreach (var p in filter)
                            importEntries.Add(new ShmoImportEntry(p, libName));
                    continue;
                }
                try
                {
                    ProcessDirective(d.Args[0], ref moduleName,
                        publicSet, dynamicSet, multifileSet, ensureLinked,
                        tabledSet, nativeSet);
                }
                catch (InvalidOperationException ex)
                {
                    errors.Add(new ShmoCompileError(ex.Message,
                        clause.Position.Line, clause.Position.Column));
                }
                // arity_compat only: an unrecognised
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
            // prelude pruning: drop clauses whose head the
            // filter rejects (directives were handled above and always run).
            if (clauseFilter is not null
                && TryExtractHead(clause) is { } head
                && !clauseFilter(head))
                continue;
            rawClauses.Add(clause);
        }
        if (errors.Count > 0)
            return new ShmoCompileResult(null, errors, warnings);

        return CompileFromParts(
            moduleName,
            source, rawClauses, publicSet, dynamicSet, ensureLinked,
            qualifiedRefs, buildMode, errors, warnings, arityEverOn, tabledSet,
            nativeDecls.Length > 0 ? nativeDecls.ToString() : null, nativeSet,
            operatorDefs, multifileSet,
            isExportQualified: isExportQualified, exports: exportSet,
            imports: importEntries, libraryDeps: libraryDeps);
    }

    /// <summary>the compile back-half, shared by
    /// <see cref="TryCompileSource"/> (after its parse + directive pass) and the
    /// linker's cross-module unfold recompile (which reconstructs the inputs
    /// from a V4 <c>.shmo</c>'s metadata + <c>ClauseTerms</c>/<c>DynamicSeeds</c>
    /// and re-enters here with rewritten clauses). <paramref name="moduleName"/>
    /// is the RESOLVED runtime module name (the `:- module/1` directive's
    /// argument, else the per-file fallback).
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
        bool arityCompat = false,
        HashSet<PredicateRef>? tabledSet = null,
        string? nativeDecls = null,
        HashSet<PredicateRef>? nativeSet = null,
        IReadOnlyList<ShmoOperatorDef>? operatorDefs = null,
        HashSet<PredicateRef>? multifileSet = null,
        bool isExportQualified = false,
        IReadOnlyCollection<PredicateRef>? exports = null,
        IReadOnlyList<ShmoImportEntry>? imports = null,
        IReadOnlyList<ShmoLibraryDep>? libraryDeps = null,
        string? dialect = null)
    {
        // Partition raw clauses: dynamic-head ones become DynamicSeeds
        // (RAW), the rest go through the same DcgTransform +
        // MetaTransform + PhraseTransform pipeline ConsultString uses.
        // Helper clauses MetaTransform adds (catch's first arg becomes
        // a separate `$catchgoal_N/M` clause, etc.) end up in the
        // static set — they're synthetic, never dynamic.
        // ADR-024 — drop the Arity term-interface predicates' source clauses (the
        // builtins provide them) and any redefinition of a Shumway builtin (e.g.
        // make_c_string/4); their native blocks are never compiled. Must run BEFORE
        // the native transform below. Gated on arity_compat.
        if (arityCompat)
        {
            var droppedBuiltins = new List<(string Name, int Arity)>();
            rawClauses = ReftypeInterface.DropInterfaceClauses(rawClauses, droppedBuiltins);
            foreach (var (name, arity) in droppedBuiltins)
                (warnings ??= new List<ShmoCompileError>()).Add(new ShmoCompileError(
                    $"redefinition of builtin {name}/{arity} ignored (arity_compat)", 0, 0));
        }

        // ADR-022 — embedded native blocks. Rewrite each `$native_goal(Text)` to
        // the portable `'$native_run'('$nb$mod$i', Vars)` dispatch and collect the
        // per-block marshalling data, BEFORE partitioning — so a `:- dynamic` /
        // `:- visible` predicate whose source clauses use native code is handled
        // too: its rewritten clause (carrying `$native_run`, a normal builtin) goes
        // to the dynamic seeds and runs the block via the engine's block table
        // exactly as a static clause does (declaring a predicate dynamic is about
        // assert/retract, not about whether its source clauses can compile).
        // Interop resolution is NOT validated here (the interop class isn't known
        // at compile time) — it is enforced at run time when a block executes (an
        // unresolved call throws) and at link time by `--foreign-dll`.
        var nativeBlocks = new List<ShmoNativeBlock>();
        if (NativeTransform.HasNativeBlock(rawClauses))
        {
            var cDecls = string.IsNullOrEmpty(nativeDecls)
                ? new List<Shumway.Compiler.NativeC.CDecl>()
                : Shumway.Compiler.NativeC.CParser.ParseDeclarations(nativeDecls);
            try
            {
                rawClauses = NativeTransform.Apply(rawClauses, cDecls,
                    resolveInterop: null,
                    (name, vars, _, scalars, rawText) =>
                        nativeBlocks.Add(new ShmoNativeBlock(name, rawText, vars, scalars)),
                    "$nb$" + moduleName + "$");
            }
            catch (NativeBlockCompileException ex)
            {
                errors.Add(new ShmoCompileError(ex.Message, ex.Line, ex.Column));
                return new ShmoCompileResult(null, errors, warnings);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(new ShmoCompileError(ex.Message, 0, 0));
                return new ShmoCompileResult(null, errors, warnings);
            }
        }

        var dynamicSeedAccum = new Dictionary<PredicateRef, List<byte[]>>();
        // ADR-023 priming — the raw clauses of each `:- dynamic`/`:- visible`
        // predicate, kept as terms so a static-style WAM/IL SNAPSHOT can be
        // compiled for them (dumped via --dump-wam/--dump-il, IL-bakeable). The
        // seeds above stay the mutable truth; the snapshot is the from-the-first-
        // call form the engine evicts on the first mutation.
        var dynamicClauseAccum = new Dictionary<PredicateRef, List<Clause>>();
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
                    dynamicClauseAccum[head.Value] = new List<Clause>();
                }
                seedList.Add(TermCodec.EncodeClause(clause));
                dynamicClauseAccum[head.Value].Add(clause);
            }
            else
            {
                staticInput.Add(clause);
            }
        }

        // Tabling (the consult-time transform, moved to COMPILE time). A
        // `:- table p/N` predicate's static clauses are re-headed to the
        // semi-naive '$tbase$p'/'$trec$p' split and a driver clause
        // 'p(..) :- $tbl_dispatch(p(..), $tbase$p(..), $trec$p(..))' is added,
        // exactly as PrologEngine.ConsultString does at consult time — but
        // here, so the .shmo bytecode (and the persisted ClauseTerms, which the
        // linker's LTO unfold re-runs) already carry the transformed clauses.
        // p/N, '$tbase$p'/N and '$trec$p'/N become public to keep module
        // mangling from desyncing the driver's data-position references from
        // their definitions (the engine transform makes the same predicates
        // public for the same reason). All three compile to IL normally — the
        // fixpoint's in-progress detection reads the table via clause/2 (a
        // backtrackable builtin), which is sound under Tier-1 since the
        // chunk-e76a535 IsBacktrackable fix; cyclic / mutual-recursive / WFS
        // tabling is covered as IL by TablingBundleTests.
        if (tabledSet is { Count: > 0 })
        {
            var tabledFids = new HashSet<int>();
            foreach (var t in tabledSet)
            {
                int fid = Shumway.Core.FunctorTable.Intern(
                    Shumway.Core.AtomTable.Intern(t.Name, permanent: true).Id, t.Arity);
                tabledFids.Add(fid);
                publicSet.Add(t);
                publicSet.Add(new PredicateRef("$tbase$" + t.Name, t.Arity));
                publicSet.Add(new PredicateRef("$trec$" + t.Name, t.Arity));
            }
            // The engine transform mutates a functor-id `publics` set purely as
            // an OUTPUT (the public indicators are derived above directly), so a
            // throwaway set suffices here.
            staticInput = PrologEngine.TransformTabledPredicates(
                staticInput, tabledFids, new HashSet<int>());

            // Tabled NEGATION (well-founded semantics): when a clause negates a
            // tabled goal the transform adds a '$wfs_mode' marker fact so
            // '$tbl_dispatch' runs the alternating fixpoint. '$wfs_mode'/0 is a
            // DYNAMIC functor (prelude :- dynamic) read at runtime via
            // clause/2 — which in a source-stripped bundle sees only the
            // DYNAMIC store (LoadEntryFromBytecode has no static AST). The
            // engine consult path leaves it a static clause, visible there via
            // StaticClausesFor; here we instead route it to the dynamic seeds
            // (rehydrated into _dynamicClauses at load) so a release / IL bundle
            // exposes it too. Dynamic functors are unmangled and may be declared
            // by multiple modules, so coexisting with the prelude's declaration
            // is fine. The static clause is dropped so the functor isn't
            // compiled both static and dynamic.
            var wfsRef = new PredicateRef("$wfs_mode", 0);
            Clause? wfsClause = null;
            var keptStatic = new List<Clause>(staticInput.Count);
            foreach (var c in staticInput)
            {
                if (TryExtractHead(c) is { Name: "$wfs_mode", Arity: 0 })
                    wfsClause = c;
                else
                    keptStatic.Add(c);
            }
            if (wfsClause is not null)
            {
                staticInput = keptStatic;
                dynamicSet.Add(wfsRef);
                if (!dynamicSeedAccum.TryGetValue(wfsRef, out var seedList))
                    dynamicSeedAccum[wfsRef] = seedList = new List<byte[]>();
                seedList.Add(TermCodec.EncodeClause(wfsClause));
            }
        }

        // Now apply the static-pipeline transforms. These may add
        // synthetic helper clauses (MetaTransform extracts catch's
        // protected goal, ; / -> branches, etc. into helper preds).
        // module-local meta-wrapper unfold first (see
        // MetaWrapperUnfold): staticInput excludes dynamic-head clauses,
        // so any detected wrapper is immutable.
        // the pipeline is split at the MetaTransform
        // boundary: preMeta (post-unfold, post-DCG, pre-MetaTransform)
        // is the LAST stage where meta-call structure is still visible
        // (MetaTransform's rewrite turns `call(g(X))` into a
        // direct `g(X)` and inlines findall/bagof/... goals into
        // helper bodies). The DIRECT-vs-META edge marking walks
        // preMeta; the call-graph EDGES still walk the fully
        // transformed clauses below, unchanged.
        // ADR-037 — lower `Head => Body` (SsuRule) FIRST, mirroring
        // ClausePipeline's SSU→DCG→Meta order. The consult path stores raw
        // SsuRule/DcgRule clauses in the manifest (lowering happens only for the
        // engine's own bytecode), so a re-compile from those parts (ShmoViaConsult,
        // the linker) must re-run the lowering or ClauseCompiler hits an
        // `Unknown clause kind: SsuRule`. SsuTransform is a no-op on non-`=>`
        // clauses, so this is safe for already-lowered input too.
        var lowered = SsuTransform.Apply(staticInput).ToList();
        var preMeta = DcgTransform.Apply(MetaWrapperUnfold.Apply(lowered),
            failFast: buildMode != ShmoBuildMode.Debuggable);
        var clauses = PhraseTransform.Apply(MetaTransform.Apply(preMeta));

        // module-wide DIRECT / META reference sets. A
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
        // descends into catch / findall / etc., so this
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

        // apply ModuleRewrite so the .shmo's bytecode is
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
        // directive's argument, else the per-file fallback),
        // matching what the engine applies when it loads the entry: the
        // source-carrying LoadBundle path consults under the entry's module
        // name, and the source-less path's dynamic-seed rehydration rewrites
        // under the same name via _dynamicSeedModule.
        // ADR-038 — the import table for ModuleRewrite: a bare imported functor id
        // → its source module, so an importer's call to `p` mangles to `Source$p`
        // in the emitted bytecode (matching the runtime resolution).
        var importFidMap = new Dictionary<int, string>();
        if (imports is not null)
            foreach (var imp in imports)
                importFidMap[Shumway.Core.FunctorTable.Intern(
                    Shumway.Core.AtomTable.Intern(imp.Pred.Name, permanent: true).Id,
                    imp.Pred.Arity)] = imp.Source;
        var rewriteCtx = new ModuleRewrite.Context(
            moduleName, localFids, dynamicFids, importFidMap);
        var rewritten = new List<Clause>(staticClauses.Count);
        foreach (var clause in staticClauses)
            rewritten.Add(ModuleRewrite.Rewrite(clause, rewriteCtx));

        // Release drops the per-clause Meta/DbgInfo markers
        // from the emitted bytecode. Combined with the Source-string
        // strip below, a Release .shmo carries no debug information at
        // all — that's what `-r` promises and IP-protection workflows
        // rely on. Debug keeps the markers so stack-trace mapping
        // continues to work in dev builds.
        // ADR-035 — the Debuggable build mode bakes the debuggable WAM (frames on every
        // rule clause, every named var in a Y slot, no trimming, no cut-elision, runtime-
        // switchable last call, stop sites + var maps) straight into the .shmo, so a debug
        // bundle is debuggable with NO re-consult at load. DebugFileId blames the module's
        // own file (by base name, the identity DebugSiteTable uses) for any position that
        // doesn't carry its own — matching the <module>.pl the load path materialises for
        // display. Plain Debug (source-retention only) stays release-shape.
        bool debugBuild = buildMode == ShmoBuildMode.Debuggable;
        var moduleCompiler = new ModuleCompiler
        {
            EmitDebugInfo = buildMode != ShmoBuildMode.Release,
            DebugCodegen = debugBuild,
            DebugFileId = debugBuild
                ? Shumway.Core.DebugSiteTable.InternFile(moduleName + ".pl")
                : 0,
        };
        var module = moduleCompiler.Compile(rewritten);
        byte[] bytecode = CompiledModuleCodec.Encode(module);

        // ADR-023 priming — compile a static-style WAM snapshot of each
        // `:- dynamic`/`:- visible` predicate's clauses (the same ClausePipeline
        // transform + ModuleRewrite a static predicate gets, then compiled as an
        // ordinary try_me_else chain). This is the from-the-first-call form the
        // engine runs and evicts on the first mutation. In-memory only — NOT
        // serialized into the .shmo (the runtime rebuilds its own snapshot from
        // the live clauses); it exists so --dump-wam / --dump-il can show the
        // WAM/IL these predicates actually run, which the empty static module
        // (their clauses live in DynamicSeeds) otherwise hides.
        byte[]? dynamicSnapshotBytecode = null;
        if (dynamicClauseAccum.Count > 0)
        {
            var snapInput = new List<Clause>();
            foreach (var kv in dynamicClauseAccum)
                snapInput.AddRange(
                    ClausePipeline.Apply(kv.Value, new Shumway.Compiler.Modes.ModeTable()));
            var snapRewritten = new List<Clause>(snapInput.Count);
            foreach (var c in snapInput)
                snapRewritten.Add(ModuleRewrite.Rewrite(c, rewriteCtx));
            dynamicSnapshotBytecode =
                CompiledModuleCodec.Encode(moduleCompiler.Compile(snapRewritten));
        }

        // Release also drops the Source string from the
        // .shmo. Combined with the bytecode-side DbgInfo strip above,
        // the Release artifact contains no recoverable Prolog source —
        // and the source-less LoadBundle path means the engine
        // can still dispatch it. Debug keeps Source so the linker's
        // map output, the IL warmup's source-position helpers, and
        // standard "consult from source" tooling stay intact.
        string persistedSource = buildMode == ShmoBuildMode.Release ? "" : source;

        // stamp each edge with the module-wide
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
        {
            // A multifile seed is pre-mangled HERE, under its origin module —
            // several modules contribute clauses to the same fid, so the
            // load-time per-fid seed-module rewrite (one module context for
            // ALL of a fid's clauses) cannot be used. The rewrite is
            // idempotent for the linker's recompile paths: an already-mangled
            // `module$name` never matches a local fid again.
            if (multifileSet?.Contains(ind) == true)
            {
                var raw = dynamicClauseAccum[ind];
                var pre = new List<byte[]>(raw.Count);
                foreach (var c in raw)
                    pre.Add(TermCodec.EncodeClause(ModuleRewrite.Rewrite(c, rewriteCtx)));
                dynamicSeeds.Add(new ShmoDynamicSeed(ind, pre, multifile: true));
            }
            else
                dynamicSeeds.Add(new ShmoDynamicSeed(ind, encodedList));
        }

        // the LTO channel: persist the RAW static clauses
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
            arityCompat: arityCompat,
            nativeBlocks: nativeBlocks,
            nativeFunctions: nativeSet?.ToList(),
            nativeDecls: nativeDecls,
            operators: operatorDefs,
            isExportQualified: isExportQualified,
            exports: exports?.ToList(),
            imports: imports,
            libraryDeps: libraryDeps,
            dialect: dialect);
        obj.DynamicSnapshotBytecode = dynamicSnapshotBytecode;
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
        return TryCompileSource(source, fallback, buildMode, maxErrors, arityCompat,
            includeBaseDir: Path.GetDirectoryName(Path.GetFullPath(path)));
    }

    // ------------------------------------------------------------------------
    // Directive handling
    // ------------------------------------------------------------------------

    /// <summary>Returns true iff the directive was a <c>:- module/1</c>;
    /// a module directive overwrites <paramref name="moduleName"/> (which
    /// arrives pre-seeded with the per-file fallback).</summary>
    private static bool ProcessDirective(Term body, ref string moduleName,
        HashSet<PredicateRef> publicSet,
        HashSet<PredicateRef> dynamicSet,
        HashSet<PredicateRef> multifileSet,
        List<PredicateRef> ensureLinked,
        HashSet<PredicateRef> tabledSet,
        HashSet<PredicateRef> nativeSet)
    {
        // ADR-024 — `:- native fn/N` marks a native (P/Invoke / managed-snapshot)
        // function. Recorded so a source-stripped bundle restores it at load.
        if (body is CompoundTerm nat && nat.Functor == "native" && nat.Args.Length == 1)
        {
            foreach (var spec in ReadFunctorSpecs(nat.Args[0], "native"))
                nativeSet.Add(spec);
            return false;
        }
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
        // `dynamic` and its Arity-Prolog spelling `visible` — both declare a
        // mutable predicate. A visible/dynamic predicate WITH clauses still gets
        // a build-time WAM/IL snapshot (dumped, IL-bakeable) that runs from the
        // first call and is evicted on the first assert/retract (ADR-023).
        if (body is CompoundTerm dyn
            && (dyn.Functor == "dynamic" || dyn.Functor == "visible")
            && dyn.Args.Length == 1)
        {
            foreach (var spec in ReadFunctorSpecs(dyn.Args[0], dyn.Functor))
                dynamicSet.Add(spec);
            return false;
        }
        // `:- multifile foo/N` — several modules contribute clauses to one
        // predicate. Multifile implies dynamic (matching the consult path:
        // MarkDynamic + clause store), so visibility is Dynamic and the
        // linker's globalDynamic namespace — a module LIST per indicator —
        // merges the contributors with no duplicate_public error. The
        // clauses are pre-mangled under THIS module at compile time (see
        // CompileFromParts) so the load path needs no per-fid seed module.
        if (body is CompoundTerm mf && mf.Functor == "multifile" && mf.Args.Length == 1)
        {
            foreach (var spec in ReadFunctorSpecs(mf.Args[0], "multifile"))
            {
                multifileSet.Add(spec);
                dynamicSet.Add(spec);
            }
            return false;
        }
        // :- ensure_linked(Indicator) — GNU-Prolog-style hint that the
        // named predicate is reachable even though the static call
        // graph doesn't show it (typically because it's the target of a
        // runtime meta-call). The linker treats every
        // ensure_linked indicator as an additional reachability root,
        // so the predicate's defining module survives dead-code
        // elimination and its own callees get walked.
        if (body is CompoundTerm el && el.Functor == "ensure_linked" && el.Args.Length == 1)
        {
            foreach (var spec in ReadFunctorSpecs(el.Args[0], "ensure_linked"))
                ensureLinked.Add(spec);
            return false;
        }
        // :- table p/N — tabling. Recorded here so CompileFromParts can apply
        // the semi-naive transform (PrologEngine.TransformTabledPredicates) at
        // COMPILE time, baking the '$tbase$p'/'$trec$p'/driver clauses into the
        // .shmo bytecode. Without this the transform only ran at consult/load
        // time off the entry's SOURCE — so a source-stripped (release) bundle
        // could not table at all, and an IL bundle's raw-predicate IL shadowed
        // the load-time driver. (The engine's consult-time transform stays for
        // plain ConsultString and the debug-bundle re-consult path; no single
        // path double-applies.)
        if (body is CompoundTerm tbl && tbl.Functor == "table" && tbl.Args.Length == 1)
        {
            foreach (var spec in ReadFunctorSpecs(tbl.Args[0], "table"))
                tabledSet.Add(spec);
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

    // ADR-038 — walk a list of Name/Arity predicate indicators, yielding each and
    // silently skipping non-PI entries (e.g. an op/3 spec in a module export list).
    private static IEnumerable<PredicateRef> ReadPiListLenient(Term list)
    {
        Term cursor = list;
        while (cursor is CompoundTerm { Functor: ".", Args: [var head, var tail] })
        {
            if (head is CompoundTerm { Functor: "/", Args: [AtomTerm n, IntTerm a] })
                yield return new PredicateRef(n.Name, (int)a.Value);
            cursor = tail;
        }
    }

    // ADR-038 — recognise a use_module spec: library(X) (baked = a built-in C#
    // constraint library) or a bare atom naming a file.
    private static bool TryReadLibrarySpec(Term spec, out string libName, out bool baked)
    {
        if (spec is CompoundTerm { Functor: "library", Args: [AtomTerm lib] })
        {
            libName = lib.Name;
            baked = lib.Name is "clpfd" or "clpr" or "coroutining";
            return true;
        }
        if (spec is AtomTerm fileAtom)
        {
            libName = fileAtom.Name;
            baked = false;
            return true;
        }
        libName = "";
        baked = false;
        return false;
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
            // arity_compat — strip an Arity directive annotation
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
    // DIRECT-vs-META reference marking
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
                // exactly what the static rewrite turns it
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

    /// <summary>The control constructs the static
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
