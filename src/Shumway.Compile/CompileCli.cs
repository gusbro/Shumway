using System.Text;
using Shumway.Compiler.Il;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;

namespace Shumway.Compile;

/// <summary>
/// <c>shumway-compile</c> CLI. Takes one or more Prolog source files
/// and emits a per-module compiled-object <c>.shmo</c> artifact for
/// each. The linker (<c>shumway-link</c>)
/// combines one or more <c>.shmo</c>s into a deployable <c>.shum</c>
/// bundle.
///
/// <para>Usage:</para>
/// <code>
///   shumway-compile [options] input1.pl [input2.pl ...]
/// </code>
///
/// <para>Per-file behaviour:</para>
/// <list type="bullet">
/// <item>One input + <c>-o file.shmo</c> → that file.</item>
/// <item>One input, no <c>-o</c> → derived from the input
/// (<c>lib.pl</c> → <c>lib.shmo</c>).</item>
/// <item>Many inputs + <c>-o dir</c> → each output lands in <c>dir</c>
/// as <c>basename.shmo</c>.</item>
/// <item>Many inputs, no <c>-o</c> → each output sits next to its
/// source.</item>
/// </list>
///
/// <para>By default the compiler prints <c>compiling X → Y</c> per
/// file to stderr. <c>--verbose</c> adds the public / dynamic
/// predicate list per file.</para>
///
/// <para>Exit codes: 0 on success, 1 on compile error, 3 on usage
/// error. With multiple inputs, a failure on one file still attempts
/// the others — every error is reported, the exit code reflects the
/// worst outcome.</para>
/// </summary>
internal static class CompileCli
{
    private const int ExitOk = 0;
    private const int ExitCompileError = 1;
    private const int ExitUsageError = 3;

    public static int Main(string[] args)
    {
        var opts = ParseArgs(args);
        if (opts is null) return ExitUsageError;

        bool multi = opts.InputPaths.Count > 1;
        // -o names an output DIRECTORY under multi-input, and under --consult
        // (which always emits one .shmo per module in the loaded graph — it is
        // inherently multi-output even from a single input file).
        bool outputIsDir = multi || opts.Consult;
        if (outputIsDir && !string.IsNullOrEmpty(opts.OutputPath))
        {
            try { Directory.CreateDirectory(opts.OutputPath); }
            catch (IOException ex)
            {
                Console.Error.WriteLine(
                    $"shumway-compile: cannot create output directory '{opts.OutputPath}': {ex.Message}");
                return ExitUsageError;
            }
        }

        // --consult loads ALL inputs into ONE ephemeral engine and emits one
        // .shmo per module the whole load brought in (shared dependencies once).
        if (opts.Consult)
            return CompileViaConsultMany(opts.InputPaths, opts);

        int exit = ExitOk;
        foreach (var input in opts.InputPaths)
        {
            string output = ResolveOutputPath(input, opts.OutputPath, multi);
            int per = CompileOne(input, output, opts);
            if (per != ExitOk) exit = per;
        }
        return exit;
    }

    /// <summary>--consult: compile THROUGH the consult pipeline (an ephemeral
    /// engine loads the files, so directives and term/goal-expansion hooks
    /// actually run), emitting one .shmo per module the load brought in. All
    /// inputs are consulted into ONE engine, so a module shared by several
    /// roots — a library and its dependencies — loads and compiles ONCE.
    /// Each module — the roots and every dependency — is written as
    /// &lt;module&gt;.shmo into the output directory (<c>-o &lt;dir&gt;</c>, or
    /// the first input's directory when <c>-o</c> is omitted). Consult is
    /// inherently multi-output, so <c>-o</c> is a directory, never a file.
    ///
    /// <para>A DRAGGED-IN dependency (not one of the passed inputs) is skipped
    /// when its <c>.shmo</c> already exists, is newer than the module's source
    /// (the <c>.pl</c>, or the runtime assembly for a baked-in library), and
    /// was built in the same mode — so compiling <c>a.pl</c> then <c>b.pl</c>
    /// separately regenerates each shared dependency's <c>.shmo</c> only once,
    /// giving the same object set as one batch compilation. The roots
    /// themselves always regenerate.</para></summary>
    private static int CompileViaConsultMany(IReadOnlyList<string> inputs, Options opts)
    {
        Console.Error.WriteLine(
            $"shumway-compile: compiling {inputs.Count} file(s) via consult "
            + $"[{opts.BuildMode.ToString().ToLowerInvariant()}]");
        var errors = new List<ShmoCompileError>();
        List<(string ModuleName, ShmoObject Object, DateTime SourceTimeUtc, bool IsRoot)> objects;
        try
        {
            objects = ShmoViaConsult.CompileMany(inputs, opts.LibraryDirs, opts.BuildMode, errors);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"shumway-compile: consult failed: {ex.Message}");
            return ExitCompileError;
        }
        foreach (var err in errors)
            Console.Error.WriteLine($"error: {err.Message}");
        if (errors.Count > 0 || objects.Count == 0)
        {
            Console.Error.WriteLine(
                $"shumway-compile: {Math.Max(errors.Count, 1)} error(s) (consult mode).");
            return ExitCompileError;
        }
        // Output is a directory: each module is named by its module name (a
        // single -o filename would collide root over deps). -o <dir> when
        // given (already created in Main); else the first input's directory.
        string dir = !string.IsNullOrEmpty(opts.OutputPath)
            ? opts.OutputPath
            : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(inputs[0])) ?? ".";
        try { System.IO.Directory.CreateDirectory(dir); }
        catch (System.IO.IOException) { /* surfaced by the write below */ }
        foreach (var (moduleName, obj, srcTime, isRoot) in objects)
        {
            string path = System.IO.Path.Combine(dir, SanitizeFileName(moduleName) + ".shmo");
            if (!isRoot && ShmoUpToDate(path, srcTime, opts.BuildMode))
            {
                Console.Error.WriteLine($"shumway-compile:   {moduleName}.shmo up to date, skipped");
                continue;
            }
            ShmoWriter.WriteToFile(obj, path);
            Console.Error.WriteLine($"shumway-compile:   {moduleName} -> {path}");
        }
        return ExitOk;
    }

    /// <summary>A dragged-in dependency's <c>.shmo</c> may be reused when it
    /// exists, is at least as new as the module's source, AND was built in the
    /// same mode — never reuse a release object for a debug build (or vice
    /// versa). An unreadable / stale-format object regenerates.</summary>
    private static bool ShmoUpToDate(string path, DateTime sourceTimeUtc, ShmoBuildMode mode)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return false;
            if (System.IO.File.GetLastWriteTimeUtc(path) < sourceTimeUtc) return false;
            try { if (ShmoReader.ReadFromFile(path).BuildMode != mode) return false; }
            catch { return false; }
            return true;
        }
        catch { return false; }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    /// <summary>A <c>-L</c> value may be a <c>dialect:path</c> spec (used with
    /// <c>--consult</c> to load another Prolog system's libraries, ADR-040) or
    /// a plain path. Preserve the former verbatim; resolve the latter.</summary>
    private static string NormalizeLibDirArg(string arg)
    {
        int c = arg.IndexOf(':');
        if (c > 0 && PrologEngine.IsKnownLibraryDialect(arg.Substring(0, c)))
            return arg;
        try { return System.IO.Path.GetFullPath(arg); } catch { return arg; }
    }

    /// <summary>Discoverability: a file that relies on load-time expansion
    /// hooks cannot compile completely file-at-a-time — tell the user about
    /// --consult instead of leaving a cryptic failure (or a silently
    /// incomplete object).</summary>
    private static void MaybeHintConsultMode(string input, bool onlyIfHooks = false)
    {
        string text;
        try { text = System.IO.File.ReadAllText(input); } catch { return; }
        bool hasHooks = text.Contains("term_expansion(")
            || text.Contains("goal_expansion(")
            || text.Contains(":- attribute");
        bool hasLibDeps = text.Contains("use_module(library(");
        if (onlyIfHooks)
        {
            if (hasHooks)
                Console.Error.WriteLine(
                    $"shumway-compile: note: {input} defines load-time expansion hooks "
                    + "(term_expansion / goal_expansion / ':- attribute'); if its clauses "
                    + "are generated by those hooks, compile it with:\n"
                    + $"  shumway-compile --consult -L <libdir> {input}");
            return;
        }
        if (hasHooks || hasLibDeps)
            Console.Error.WriteLine(
                "shumway-compile: hint: this file may rely on operators or expansion "
                + "hooks that only exist once its libraries are LOADED. Try compiling "
                + "through the consult pipeline:\n"
                + $"  shumway-compile --consult -L <libdir> {input}");
    }

    private static int CompileOne(string input, string output, Options opts)
    {
        bool verbose = opts.Verbose;
        ShmoBuildMode buildMode = opts.BuildMode;
        Console.Error.WriteLine(
            $"shumway-compile: compiling {input} -> {output} "
            + $"[{buildMode.ToString().ToLowerInvariant()}]");
        // Register-allocator design survey (ADR-021). Classifies every permanent
        // allocated while compiling this file (Class B = live only across inline
        // goals; Class A = crosses a real call, irreducible). Diagnostic.
        bool ySurvey = Environment.GetEnvironmentVariable("SHUMWAY_Y_SURVEY") == "1";
        if (ySurvey)
            Shumway.Compiler.Wam.ClauseCompiler.YSurvey = new();
        try
        {
            var result = ShmoCompiler.TryCompileFile(input, buildMode, maxErrors: 100,
                arityCompat: opts.ArityCompat);
            // Warnings (e.g. unknown directives under --arity) are
            // reported but never fail the compile.
            foreach (var warn in result.Warnings)
                Console.Error.WriteLine(
                    $"{input}:{warn.Line}:{warn.Column}: warning: {warn.Message}");
            if (!result.Success)
            {
                foreach (var err in result.Errors)
                    Console.Error.WriteLine($"{input}:{err.Line}:{err.Column}: error: {err.Message}");
                Console.Error.WriteLine(
                    $"shumway-compile: {result.Errors.Count} error(s) in {input}.");
                MaybeHintConsultMode(input);
                // Match a C compiler: a failed compile must not leave a stale
                // object behind — a later link would silently pick it up and mask
                // the error. Remove any pre-existing output for this input.
                RemoveStaleOutput(output);
                return ExitCompileError;
            }
            MaybeHintConsultMode(input, onlyIfHooks: true);
            var obj = result.Object!;
            ShmoWriter.WriteToFile(obj, output);
            if (ySurvey && Shumway.Compiler.Wam.ClauseCompiler.YSurvey is { } survey)
            {
                if (survey.Count == 0)
                    Console.Error.WriteLine(
                        "[y-survey] empty — the per-clause collection is compiled in only "
                        + "with a `dotnet build -p:ShumwayDiag=true` build.");
                int totalPerms = survey.Values.Sum(v => v.PermTotal);
                int totalB = survey.Values.Sum(v => v.ClassB);
                Console.Error.WriteLine(
                    $"[y-survey] {input}: permanents={totalPerms} classB(inline-only)={totalB} "
                    + $"({(totalPerms == 0 ? 0 : 100.0 * totalB / totalPerms):F1}%)");
                foreach (var (pred, v) in survey
                    .Where(kv => kv.Value.ClassB > 0)
                    .OrderByDescending(kv => kv.Value.ClassB)
                    .Take(30))
                    Console.Error.WriteLine(
                        $"[y-survey]   {pred,-40} perms={v.PermTotal,3} classB={v.ClassB,3}");
                Shumway.Compiler.Wam.ClauseCompiler.YSurvey = null;
            }
            if (opts.DumpWam || opts.DumpIl)
                DumpArtifacts(obj, input, output, opts);
            if (opts.PruneReport)
                PruneReport(obj, input);
            if (verbose)
            {
                Console.Error.WriteLine(
                    $"  module={obj.ModuleName}, "
                    + $"defined={obj.Defined.Count}, "
                    + $"calls={obj.CallGraph.Count}.");
                var publics = obj.Defined
                    .Where(d => d.Visibility == PredicateVisibility.Public)
                    .Select(d => d.Indicator.ToString())
                    .ToList();
                var dynamics = obj.Defined
                    .Where(d => d.Visibility == PredicateVisibility.Dynamic)
                    .Select(d => d.Indicator.ToString())
                    .ToList();
                if (publics.Count > 0)
                {
                    Console.Error.WriteLine($"  public ({publics.Count}):");
                    foreach (var p in publics) Console.Error.WriteLine($"    {p}");
                }
                if (dynamics.Count > 0)
                {
                    Console.Error.WriteLine($"  dynamic ({dynamics.Count}):");
                    foreach (var d in dynamics) Console.Error.WriteLine($"    {d}");
                }
            }
            return ExitOk;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                || ex is InvalidDataException
                                || ex is IOException
                                || ex is UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"shumway-compile: error: {ex.Message}");
            RemoveStaleOutput(output);
            return ExitCompileError;
        }
    }

    /// <summary>A failed compile must not leave a stale object file behind (a later
    /// link would silently pick it up), so remove any pre-existing output — what a
    /// C compiler does when compilation fails.</summary>
    private static void RemoveStaleOutput(string output)
    {
        try
        {
            if (File.Exists(output)) File.Delete(output);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"shumway-compile: warning: could not remove stale {output}: {ex.Message}");
        }
    }

    /// <summary>Dumps the compiler's intermediate code for one module to text files
    /// for manual analysis (--dump-wam / --dump-il). The module's WAM is decoded from
    /// the just-built .shmo bytecode; the IL is produced by running the Tier-1 IL
    /// compiler over each predicate (with the full module as the callee map, so region
    /// compilation — when --regions is set — sees the local closure). Both append, with
    /// per-predicate headers; the IL dump reuses the IL compiler's own FinishEmit dump
    /// hook (<see cref="IlPredicateCompiler.IlDumpPath"/>).</summary>
    private static void DumpArtifacts(ShmoObject obj, string input, string output, Options opts)
    {
        var module = CompiledModuleCodec.Decode(obj.Bytecode);
        var preds = module.Predicates;

        // ADR-023 priming — the `:- dynamic`/`:- visible` predicates' clauses
        // live in DynamicSeeds, so the static module above is empty for them.
        // ShmoCompiler also compiled a static-style WAM snapshot of them (the
        // form the engine runs from the first call, evicted on the first
        // assert/retract); surface it here so the dump isn't silently empty.
        var snapModule = obj.DynamicSnapshotBytecode is not null
            ? CompiledModuleCodec.Decode(obj.DynamicSnapshotBytecode) : null;
        var snapPreds = snapModule?.Predicates
            ?? (IReadOnlyList<Shumway.Compiler.Wam.CompiledPredicate>)
                  System.Array.Empty<Shumway.Compiler.Wam.CompiledPredicate>();
        var calleeMap = new Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>();
        foreach (var p in preds) calleeMap[p.FunctorId] = p;
        foreach (var p in snapPreds) calleeMap.TryAdd(p.FunctorId, p);

        // Emit IL for EVERY predicate, even one whose body makes a NON-TAIL call
        // to a predicate defined in ANOTHER module / the prelude / the dynamic
        // store. The IL emitter dispatches such a call BY FUNCTOR ID at runtime
        // (EncodeResumeMarker), exactly like the WAM `call` does — it needs only
        // the fid, not the callee's body. The single-file dump's calleeMap can't
        // resolve those, so without help CanCompile rejects the whole predicate
        // ("call->unresolved") and the dump is silent for it — useless. So for
        // each externally-defined callee fid we install a STUB: it lets the gate
        // pass; with regions OFF and InlineRules2 OFF (the dump's settings) the
        // stub is never inlined or inspected, so the emitted code is the real
        // threaded fid-dispatch. (The real --with-compiled-il / --exe link
        // resolves these against the full program.) The stub's enter_dynamic
        // first byte keeps every inliner from ever pulling it in.
        var stubBytecode = new byte[] { (byte)Opcode.EnterDynamic };
        var stubbedExternals = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var p in preds.Concat(snapPreds))
            foreach (var cs in p.CallSites)
            {
                if (cs.IsExecute || cs.CalleeFunctorId < 0
                    || calleeMap.ContainsKey(cs.CalleeFunctorId)) continue;
                var (_, ar) = FunctorTable.Lookup(cs.CalleeFunctorId);
                calleeMap[cs.CalleeFunctorId] = new Shumway.Compiler.Wam.CompiledPredicate(
                    stubBytecode, cs.CalleeFunctorId, ar, clauseCount: 0,
                    callSites: System.Array.Empty<CallSite>(),
                    dispatchSites: System.Array.Empty<int>(),
                    switchTables: System.Array.Empty<SwitchTable>(),
                    switchTableIdSites: System.Array.Empty<int>(),
                    sourcePosition: default);
                stubbedExternals.Add($"{PredName(cs.CalleeFunctorId)}/{ar}");
            }

        // The dump goes next to the .shmo output, named after the source —
        // <source>.wam / <source>.il — so it works with wildcards / multi-file
        // compiles (each source gets its own dump). One file per source: overwrite.
        string DumpPath(string ext)
        {
            string dir = Path.GetDirectoryName(output) ?? "";
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(input) + ext);
        }

        void DumpPred(StringBuilder sb, Shumway.Compiler.Wam.CompiledPredicate p)
        {
            sb.Append($"\n;;; {PredName(p.FunctorId)}/{p.Arity} clauses={p.ClauseCount} bytes={p.Bytecode.Length}\n");
            foreach (var ins in Disassembler.Iterate(p.Bytecode, 0, p.Bytecode.Length))
                sb.Append($"    {ins}\n");
        }

        if (opts.DumpWam)
        {
            string wamPath = DumpPath(".wam");
            var sb = new StringBuilder();
            sb.Append($";;; ===== WAM dump: {input} (module {obj.ModuleName}, {preds.Count} predicates) =====\n");
            foreach (var p in preds)
                DumpPred(sb, p);
            if (snapPreds.Count > 0)
            {
                sb.Append($"\n;;; --- dynamic/visible snapshot: {snapPreds.Count} predicate(s) "
                    + "(run as this WAM/IL from the first call; evicted to the live "
                    + "dynamic chain on the first assert/retract) ---\n");
                foreach (var p in snapPreds)
                    DumpPred(sb, p);
            }
            File.WriteAllText(wamPath, sb.ToString());
            Console.Error.WriteLine(
                $"  WAM dump -> {wamPath} ({preds.Count} static"
                + (snapPreds.Count > 0 ? $" + {snapPreds.Count} dyn-snapshot" : "")
                + " predicates)");
        }

        if (opts.DumpIl)
        {
            string ilPath = DumpPath(".il");
            IlPredicateCompiler.IlDumpPath = ilPath;
            IlPredicateCompiler.RegionCompile = opts.Regions;
            File.WriteAllText(ilPath,   // truncate first; the IL compiler appends below
                $";;; ===== IL dump: {input} (module {obj.ModuleName}, regions={opts.Regions}) =====\n");
            if (stubbedExternals.Count > 0)
                File.AppendAllText(ilPath,
                    ";;; calls to predicates defined OUTSIDE this module are emitted as\n"
                    + ";;; runtime functor-id dispatch (resolved at link); they are: "
                    + string.Join(", ", stubbedExternals) + "\n");
            int ok = 0, skipped = 0;
            void DumpIl(Shumway.Compiler.Wam.CompiledPredicate p)
            {
                var ic = new IlPredicateCompiler();
                if (!ic.CanCompile(p, calleeMap))
                {
                    // Print WHY. The most common reason in a single-file dump is
                    // `call->unresolved`: the predicate makes a NON-TAIL call to a
                    // predicate that isn't defined in THIS module (a prelude /
                    // cross-module / dynamic-store callee). The dump compiles one
                    // .shmo in isolation, so its calleeMap holds only this module's
                    // own predicates; the real --with-compiled-il / --exe link
                    // compiles against the FULL linked program and resolves them.
                    // (A TAIL call to an unknown callee compiles fine — it dispatches
                    // by fid at runtime; only a non-tail call needs the callee known
                    // to emit its continuation.)
                    string reason = ic.DescribeRejection(p, calleeMap);
                    string detail = "";
                    if (reason == "call->unresolved")
                    {
                        var names = p.CallSites
                            .Where(cs => !cs.IsExecute && cs.CalleeFunctorId >= 0
                                         && !calleeMap.ContainsKey(cs.CalleeFunctorId))
                            .Select(cs =>
                            {
                                var (_, ar) = Shumway.Core.FunctorTable.Lookup(cs.CalleeFunctorId);
                                return $"{PredName(cs.CalleeFunctorId)}/{ar}";
                            })
                            .Distinct();
                        string joined = string.Join(", ", names);
                        if (joined.Length > 0)
                            detail = $" — non-tail call(s) to [{joined}] not defined in this module";
                    }
                    File.AppendAllText(ilPath,
                        $";;; (skipped {PredName(p.FunctorId)}/{p.Arity}: {reason}{detail})\n");
                    skipped++;
                    return;
                }
                try { ic.Compile(p, calleeMap); ok++; }          // FinishEmit appends the IL
                catch (Exception ex)
                {
                    File.AppendAllText(ilPath,
                        $";;; (IL compile failed for {PredName(p.FunctorId)}/{p.Arity}: {ex.Message})\n");
                    skipped++;
                }
            }
            // Each module's predicates index that module's OWN float pool;
            // set it so get_float/put_float can bake their values (§ floats).
            var prevPool = IlPredicateCompiler.BeginFloatPool(module.FloatLiterals);
            foreach (var p in preds)
                DumpIl(p);
            IlPredicateCompiler.EndFloatPool(prevPool);
            if (snapPreds.Count > 0)
            {
                File.AppendAllText(ilPath,
                    $";;; --- dynamic/visible snapshot: {snapPreds.Count} predicate(s) "
                    + "(evicted to Tier-0 on the first assert/retract) ---\n");
                var prevSnapPool = IlPredicateCompiler.BeginFloatPool(snapModule!.FloatLiterals);
                foreach (var p in snapPreds)
                    DumpIl(p);
                IlPredicateCompiler.EndFloatPool(prevSnapPool);
            }
            IlPredicateCompiler.IlDumpPath = null;               // don't leak into later files
            Console.Error.WriteLine(
                $"  IL dump -> {ilPath} ({ok} compiled, {skipped} skipped"
                + (opts.Regions ? ", regions on)" : ")"));
        }
    }

    /// <summary>Stage-9 dry run: report which predicates the dead-region reachability
    /// analysis would PRUNE if this module were region-compiled into a bundle — those
    /// reached only as absorbed br-members of some region. Module-level approximation:
    /// the external roots are the call-graph roots (predicates with no in-module caller,
    /// i.e. the entry points / externally-called predicates); a real linker uses the
    /// actual entry-point + public set. Analysis only — changes nothing.</summary>
    private static void PruneReport(ShmoObject obj, string input)
    {
        var module = CompiledModuleCodec.Decode(obj.Bytecode);
        var preds = module.Predicates.ToDictionary(p => p.FunctorId);

        // Call-graph roots: a predicate no OTHER in-module predicate calls.
        var calledByOthers = new HashSet<int>();
        foreach (var p in module.Predicates)
            foreach (var cs in p.CallSites)
                if (cs.CalleeFunctorId != p.FunctorId && preds.ContainsKey(cs.CalleeFunctorId))
                    calledByOthers.Add(cs.CalleeFunctorId);
        var roots = preds.Keys.Where(f => !calledByOthers.Contains(f)).ToList();

        var ic = new IlPredicateCompiler();
        var prunable = RegionReachability.Prunable(
            preds, roots, fid => ic.RegionMemberFids(preds[fid], preds));

        Console.Error.WriteLine(
            $"  prune-report ({input}): {preds.Count} predicates, {roots.Count} roots, "
            + $"{prunable.Count} prunable (reached only as absorbed br-members).");
        foreach (int fid in prunable.OrderBy(x => x))
            Console.Error.WriteLine($"    - {PredName(fid)}");
    }

    private static string PredName(int fid)
    {
        try
        {
            var (atom, _) = FunctorTable.Lookup(fid);
            return AtomTable.GetById(atom)?.Name ?? $"#{fid}";
        }
        catch { return $"#{fid}"; }
    }

    private static string ResolveOutputPath(string input, string output, bool multi)
    {
        if (multi)
        {
            // Multi-input mode: -o is a directory (created above), or
            // empty → each output sits next to its source.
            string basename = Path.GetFileNameWithoutExtension(input) + ".shmo";
            return string.IsNullOrEmpty(output)
                ? Path.ChangeExtension(input, ".shmo")
                : Path.Combine(output, basename);
        }
        // Single-input mode: -o is a full path, or empty → derived.
        return string.IsNullOrEmpty(output)
            ? Path.ChangeExtension(input, ".shmo")
            : output;
    }

    private sealed class Options
    {
        public List<string> InputPaths { get; } = new();
        public string OutputPath { get; set; } = "";
        public bool Verbose { get; set; }
        public ShmoBuildMode BuildMode { get; set; } = ShmoBuildMode.Release;
        public bool DumpWam { get; set; }
        public bool DumpIl { get; set; }
        public bool Regions { get; set; }
        public bool PruneReport { get; set; }
        public bool ArityCompat { get; set; }
        public bool Consult { get; set; }
        public List<string> LibraryDirs { get; } = new();
    }

    private static Options? ParseArgs(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return null;
        }

        var opts = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--help":
                case "-h":
                    PrintUsage();
                    return null;

                case "--output":
                case "-o":
                    if (++i >= args.Length) { ReportMissing(arg); return null; }
                    opts.OutputPath = args[i];
                    break;

                case "--verbose":
                case "-v":
                    opts.Verbose = true;
                    break;

                case "--debug":
                case "-d":
                    // ADR-035 — --debug means source-level DEBUGGABLE: keep the source AND
                    // bake the debug-shape WAM (frames, Y-slots, no trimming/LCO, stop sites,
                    // variable/frame maps) into the .shmo, so a bundle built from it is
                    // debuggable at load with no re-consult. (Plain ShmoBuildMode.Debug —
                    // source retention, release-shape WAM — stays an internal linker mode.)
                    opts.BuildMode = ShmoBuildMode.Debuggable;
                    break;

                case "--release":
                case "-r":
                    opts.BuildMode = ShmoBuildMode.Release;
                    break;

                case "--dump-wam":
                    opts.DumpWam = true;   // dumps to <source>.wam
                    break;

                case "--dump-il":
                    opts.DumpIl = true;    // dumps to <source>.il
                    break;

                case "--arity":
                    opts.ArityCompat = true;
                    break;

                case "--regions":
                    opts.Regions = true;
                    break;

                case "--prune-report":
                    opts.PruneReport = true;
                    break;

                case "--consult":
                case "-c":
                    opts.Consult = true;
                    break;

                case "--library-dir":
                case "-L":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine(
                            "shumway-compile: --library-dir requires a directory argument.");
                        return null;
                    }
                    opts.LibraryDirs.Add(NormalizeLibDirArg(args[i]));
                    break;

                default:
                    if (arg.StartsWith("-"))
                    {
                        Console.Error.WriteLine($"shumway-compile: unknown option '{arg}'.");
                        return null;
                    }
                    // Wildcard inputs (`shumway-compile *.pl`, `src\*.ari`).
                    // The Windows shell hands globs through verbatim, so
                    // expand them here. A pattern matching nothing is a
                    // usage error (silently compiling zero files would
                    // read as success).
                    if (arg.IndexOfAny(WildcardChars) >= 0)
                    {
                        if (!TryExpandWildcard(arg, opts.InputPaths)) return null;
                        break;
                    }
                    opts.InputPaths.Add(arg);
                    break;
            }
        }

        if (opts.InputPaths.Count == 0)
        {
            Console.Error.WriteLine("shumway-compile: at least one input source file is required.");
            return null;
        }
        return opts;
    }

    private static readonly char[] WildcardChars = { '*', '?' };

    /// <summary>Expands a wildcard input argument against the
    /// file system (directory part + pattern part), appending the matches
    /// in case-insensitive sorted order so multi-file output is
    /// deterministic. Returns false (after printing the error) when the
    /// pattern matches nothing or the directory is unusable.</summary>
    private static bool TryExpandWildcard(string arg, List<string> into)
    {
        string dir = Path.GetDirectoryName(arg) is { Length: > 0 } d ? d : ".";
        string pattern = Path.GetFileName(arg);
        List<string> matches;
        try
        {
            matches = Directory.EnumerateFiles(dir, pattern).ToList();
        }
        catch (Exception ex) when (ex is IOException
                                || ex is DirectoryNotFoundException
                                || ex is ArgumentException
                                || ex is UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"shumway-compile: cannot expand '{arg}': {ex.Message}");
            return false;
        }
        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"shumway-compile: no files match '{arg}'.");
            return false;
        }
        matches.Sort(StringComparer.OrdinalIgnoreCase);
        into.AddRange(matches);
        return true;
    }

    private static void ReportMissing(string option) =>
        Console.Error.WriteLine($"shumway-compile: option '{option}' requires a value.");

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: shumway-compile [options] <source.pl> [<source2.pl> ...]\n"
            + "\n"
            + "Compiles Prolog source files into object modules (.shmo). Use shumway-link\n"
            + "to combine .shmo files into a runnable bundle (.shum) or an executable.\n"
            + "Inputs may use wildcards (e.g. *.pl, src\\*.ari) — expanded by the\n"
            + "compiler itself, in sorted order, so they work from any shell.\n"
            + "\n"
            + "Options:\n"
            + "  -o, --output <path>  Output .shmo path (single input) or output\n"
            + "                       directory (multiple inputs). Default: alongside each\n"
            + "                       input with the extension replaced.\n"
            + "  -r, --release        Build in release mode (default). The source text is\n"
            + "                       not embedded in the .shmo.\n"
            + "  -d, --debug          Build a source-level DEBUGGABLE object: keeps the\n"
            + "                       source text AND bakes the debug-shape WAM (stop sites,\n"
            + "                       variable maps) so a bundle/exe built from it can be\n"
            + "                       debugged at load with no re-consult. Slower/larger than\n"
            + "                       release; use for dev builds and `shumway-link --exe\n"
            + "                       --debug`.\n"
            + "  -v, --verbose        Verbose progress to stderr (lists every exported\n"
            + "                       and dynamic predicate per file).\n"
            + "      --dump-wam       Write a human-readable disassembly of each\n"
            + "                       predicate's compiled bytecode to <source>.wam\n"
            + "                       (next to the .shmo output), for inspection.\n"
            + "      --dump-il        Write the .NET IL the engine compiles for each\n"
            + "                       predicate to <source>.il, for inspection. Add\n"
            + "                       --regions to show related predicates compiled\n"
            + "                       together into shared methods, as an optimized\n"
            + "                       bundle lays them out.\n"
            + "      --arity          Enable Arity/Prolog32 compatibility mode (the\n"
            + "                       arity_compat flag): $...$ quoted atoms, C\n"
            + "                       preprocessor #line markers (positions honoured),\n"
            + "                       annotated directives (foo/8:far), :- c. native-\n"
            + "                       code sections (skipped until :- prolog. or EOF),\n"
            + "                       and unknown directives reported as warnings\n"
            + "                       instead of failing the compile. The flag can\n"
            + "                       also be set per file with\n"
            + "                       :- set_prolog_flag(arity_compat, true).\n"
            + "      --regions        With --dump-il, show the shared-method (region)\n"
            + "                       layout instead of one method per predicate.\n"
            + "      --prune-report   Report which of this module's predicates would need\n"
            + "                       no standalone compiled form in an optimized bundle\n"
            + "                       (they are only ever called from inside a shared\n"
            + "                       method). Analysis only; the .shmo is unchanged.\n"
            + "  -c, --consult        Compile by CONSULTING the file: an ephemeral engine\n"
            + "                       loads it (directives execute, term_expansion /\n"
            + "                       goal_expansion hooks run, use_module dependencies\n"
            + "                       load from -L), then every module the load brought in\n"
            + "                       is written as <module>.shmo. Required for libraries\n"
            + "                       that GENERATE clauses at load time (Scryer's atts /\n"
            + "                       clpz / dcgs style) or that need operators defined by\n"
            + "                       their dependencies. Consult is inherently multi-\n"
            + "                       output, so -o names an output DIRECTORY (created if\n"
            + "                       missing); without -o the objects land next to the\n"
            + "                       source. Note: the file's directives DO run during\n"
            + "                       compilation in this mode. The file's own directory is\n"
            + "                       searched for its library dependencies automatically\n"
            + "                       (siblings resolve with no flags).\n"
            + "  -L, --library-dir <dir>  (with --consult) Extra directory searched to\n"
            + "                       resolve use_module(library(X)) when a dependency is\n"
            + "                       NOT next to the compiled file; repeatable, also\n"
            + "                       reads SHUMWAY_LIBRARY_PATH.\n"
            + "  -h, --help           Show this message.\n"
            + "\n"
            + "Note: --dump-wam / --dump-il APPEND; delete the file between runs. They\n"
            + "are inspection aids and do not change the emitted .shmo.");
    }
}
