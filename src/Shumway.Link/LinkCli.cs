using Shumway.Embedding;

namespace Shumway.Link;

/// <summary>
/// <c>shumway-link</c> CLI. Takes one or more <c>.shmo</c>
/// compiled-object files (produced by <c>shumway-compile</c>, chunk
/// 161) plus a set of entry-point predicates and produces a deployable
/// <c>.shum</c> bundle. Surface for the chunk-163 linker.
///
/// <para>Usage:</para>
/// <code>
///   shumway-link -o app.shum \
///     --entry main/0,init/1 --entry shutdown/0 \
///     [--allow-undefined] [-v] \
///     a.shmo b.shmo c.shmo
/// </code>
///
/// <para>Entry points combine across <c>--entry</c> flags and accept a
/// comma-separated list per flag. Every reachable call site is
/// resolved against the union of all loaded <c>.shmo</c>s' public +
/// dynamic predicates, plus the builtin registry and the prelude.
/// Unresolved references are reported as <c>missing_predicate</c>
/// errors; <c>--allow-undefined</c> downgrades them to warnings and
/// still produces the bundle. Modules no root reached are dropped
/// (with an <c>unreachable_module</c> warning).</para>
///
/// <para>Exit codes: 0 on success, 1 on link error, 3 on usage
/// error.</para>
/// </summary>
internal static class LinkCli
{
    private const int ExitOk = 0;
    private const int ExitLinkError = 1;
    private const int ExitUsageError = 3;

    public static int Main(string[] args)
    {
        var opts = ParseArgs(args);
        if (opts is null) return ExitUsageError;

        // Inputs route by extension: .shum is a LIBRARY (its members are
        // pulled on demand, FIFO), anything else is an OBJECT (.shmo, always
        // linked). A .shum must be a shumway-lib librarian archive (it carries
        // its objects); a linked bundle has none and can't serve as a library.
        var objects = new List<ShmoObject>();
        var libraries = new List<LinkLibrary>();
        foreach (var path in opts.InputPaths)
        {
            try
            {
                if (path.EndsWith(".shum", StringComparison.OrdinalIgnoreCase))
                {
                    var lib = BundleReader.ReadFromFile(path);
                    if (lib.ArchiveMembers.Count == 0)
                    {
                        Console.Error.WriteLine(
                            $"shumway-link: library '{path}' is a linked bundle, not a "
                            + "librarian archive — it has no objects to link against. "
                            + "Build a library with shumway-lib.");
                        return ExitLinkError;
                    }
                    var members = new List<ShmoObject>(lib.ArchiveMembers.Count);
                    foreach (var m in lib.ArchiveMembers)
                        members.Add(ShmoReader.FromBytes(m.ShmoBytes));
                    libraries.Add(new LinkLibrary(System.IO.Path.GetFileName(path), members));
                }
                else
                {
                    objects.Add(ShmoReader.ReadFromFile(path));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
            {
                Console.Error.WriteLine($"shumway-link: error reading '{path}': {ex.Message}");
                return ExitLinkError;
            }
        }

        // --goal Z validates syntactically AND becomes an implicit
        // entry point (the goal's head pred drives reachability).
        if (!string.IsNullOrEmpty(opts.Goal))
        {
            if (!ExecutableEmitter.TryValidateGoal(opts.Goal, out _, out var headPred,
                    out string? goalErr))
            {
                Console.Error.WriteLine($"shumway-link: --goal: {goalErr}");
                return ExitUsageError;
            }
            if (!opts.EntryPoints.Contains(headPred))
                opts.EntryPoints.Add(headPred);
        }

        var config = new LinkConfig
        {
            Objects = objects,
            EntryPoints = opts.EntryPoints,
            AllowUndefined = opts.AllowUndefined,
            Libraries = libraries,
            // --exe deploys a startup-sensitive single-engine app, so bake the
            // prelude by default there; otherwise only on explicit opt-in. Under
            // --with-compiled-il / --strip-wam the baked prelude is itself
            // IL-compiled (its static predicates), so an IL --exe starts with a
            // fully precompiled prelude — no parse, no compile.
            BakePrelude = opts.BakePrelude || !string.IsNullOrEmpty(opts.ExePath)
                || !string.IsNullOrEmpty(opts.DllPath),
            PrunePrelude = opts.PrunePrelude,
            VerboseOut = opts.Verbose ? Console.Error : null,
            StripSource = opts.StripSource,
            IncludeCompiledIl = opts.IncludeCompiledIl,
            StripWam = opts.StripWam,
            RegionPruneReport = opts.RegionPruneReport,
            RegionPrune = opts.RegionPrune,
            DumpWamPath = opts.DumpWamPath,
            DumpIlPath = opts.DumpIlPath,
            ForeignAssemblies = opts.ForeignDlls,
            NativeLibraries = opts.NativeDlls,
        };

        LinkResult result;
        Shumway.Compiler.Il.IlPredicateCompiler.CpFreeGuardStats.Reset();
        try
        {
            result = ShmoLinker.Link(config);
        }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            Console.Error.WriteLine($"shumway-link: error: {ex.Message}");
            return ExitLinkError;
        }

        // ADR-032 — verbose optimization panorama: what the link-time passes did,
        // aggregated from the Info diagnostics + the IL emit's CP-free counters.
        if (opts.Verbose)
        {
            var byCode = result.Diagnostics
                .Where(d => d.Severity == LinkSeverity.Info)
                .GroupBy(d => d.Code)
                .ToDictionary(g => g.Key, g => g.Count());
            Console.Error.WriteLine("shumway-link: optimization summary:");
            foreach (var (code, n) in byCode.OrderBy(kv => kv.Key))
                Console.Error.WriteLine($"  {code}: {n} module(s)/site(s)");
            if (opts.IncludeCompiledIl || opts.StripWam)
            {
                foreach (string line in Shumway.Compiler.Il.IlPredicateCompiler
                             .CpFreeGuardStats.Summary().Split('\n'))
                    Console.Error.WriteLine("  " + line.TrimEnd());
                foreach (var kv in Shumway.Compiler.Il.IlPredicateCompiler
                             .CpFreeGuardStats.RejectShapeDetail
                             .OrderByDescending(kv => kv.Value))
                    Console.Error.WriteLine($"      shape[{kv.Key}]: {kv.Value}");
            }
        }

        foreach (var d in result.Diagnostics)
        {
            // In verbose mode the linker already streamed diagnostics
            // to stderr; only re-emit when verbose is off, to avoid
            // duplicates.
            if (opts.Verbose) continue;
            var stream = d.Severity == LinkSeverity.Error
                ? Console.Error : Console.Out;
            string prefix = d.Severity switch
            {
                LinkSeverity.Error => "error",
                LinkSeverity.Warning => "warning",
                _ => "info",
            };
            string sourcePart = d.Source is null ? "" : $" ({d.Source})";
            stream.WriteLine($"shumway-link: {prefix}: {d.Message}{sourcePart}");
        }

        if (!result.Success)
        {
            // A failed link must not leave a stale bundle behind — a later run /
            // --exe would silently pick it up and mask the error (what a C linker
            // does on failure).
            RemoveStaleOutput(opts.OutputPath);
            return ExitLinkError;
        }

        if (!string.IsNullOrEmpty(opts.OutputPath))
        {
            try
            {
                File.WriteAllBytes(opts.OutputPath, result.Bytes!);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"shumway-link: error writing '{opts.OutputPath}': {ex.Message}");
                return ExitLinkError;
            }
        }

        if (!string.IsNullOrEmpty(opts.MapPath))
        {
            try
            {
                // result.LinkedObjects = explicit objects + any pulled library
                // members, so the map lists pulled library modules too.
                ShmoBundleMap.WriteToFile(
                    result.LinkedObjects, opts.EntryPoints, result, opts.MapPath);
                if (opts.Verbose)
                    Console.Error.WriteLine($"shumway-link: wrote map {opts.MapPath}.");
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine(
                    $"shumway-link: error writing map '{opts.MapPath}': {ex.Message}");
                return ExitLinkError;
            }
        }

        if (opts.Verbose && !string.IsNullOrEmpty(opts.OutputPath))
        {
            Console.Error.WriteLine(
                $"shumway-link: wrote {opts.OutputPath} "
                + $"(modules={result.ReachedModules.Count}, "
                + $"dropped={result.UnreachableModules.Count}, "
                + $"preds={result.ReachedPredicates.Count}, "
                + $"bytes={result.Bytes!.Length}).");
        }

        if (!string.IsNullOrEmpty(opts.ExePath))
        {
            var mode = opts.SelfContained
                ? ExecutableDeploymentMode.SelfContained
                : ExecutableDeploymentMode.FrameworkDependent;
            var exeResult = ExecutableEmitter.Emit(
                bundleBytes: result.Bytes!,
                goal: opts.Goal,
                outputPath: opts.ExePath,
                mode: mode,
                verboseOut: opts.Verbose ? Console.Error : null,
                foreignDllPaths: opts.ForeignDlls,
                nativeDllPaths: opts.NativeDlls,
                debug: opts.Debug,
                debugWait: opts.DebugWait);
            foreach (var d in exeResult.Diagnostics)
            {
                var stream = d.Severity == LinkSeverity.Error
                    ? Console.Error : Console.Out;
                stream.WriteLine($"shumway-link: {d.Severity.ToString().ToLowerInvariant()}: {d.Message}");
            }
            if (!exeResult.Success) return ExitLinkError;
            if (!opts.Verbose)
                Console.Error.WriteLine($"shumway-link: wrote {exeResult.OutputPath}.");
        }

        if (!string.IsNullOrEmpty(opts.DllPath))
        {
            var dllResult = LibraryEmitter.Emit(
                bundleBytes: result.Bytes!,
                outputPath: opts.DllPath,
                namespaceName: opts.DllNamespace,
                className: opts.DllClass,
                verboseOut: opts.Verbose ? Console.Error : null,
                foreignDllPaths: opts.ForeignDlls,
                nativeDllPaths: opts.NativeDlls);
            foreach (var d in dllResult.Diagnostics)
            {
                var stream = d.Severity == LinkSeverity.Error ? Console.Error : Console.Out;
                stream.WriteLine($"shumway-link: {d.Severity.ToString().ToLowerInvariant()}: {d.Message}");
            }
            if (!dllResult.Success) return ExitLinkError;
            Console.Error.WriteLine(
                $"shumway-link: wrote {dllResult.OutputPath} "
                + $"(factory {dllResult.FactoryTypeName}.CreateEngine()).");
        }
        return ExitOk;
    }

    // ------------------------------------------------------------------------
    // Argument parsing
    // ------------------------------------------------------------------------

    private sealed class Options
    {
        public List<string> InputPaths { get; } = new();
        public List<PredicateRef> EntryPoints { get; } = new();
        public string OutputPath { get; set; } = "";
        public bool Verbose { get; set; }
        public bool AllowUndefined { get; set; }
        public bool StripSource { get; set; }
        public bool IncludeCompiledIl { get; set; }
        public bool StripWam { get; set; }
        public bool RegionPruneReport { get; set; }
        public bool RegionPrune { get; set; } = true;   // default since chunk 418
        public bool BakePrelude { get; set; }
        public bool PrunePrelude { get; set; }
        public string? DumpWamPath { get; set; }
        public string? DumpIlPath { get; set; }
        public string MapPath { get; set; } = "";
        public string ExePath { get; set; } = "";
        public string Goal { get; set; } = "";
        public bool SelfContained { get; set; }
        // ADR-035 — --debug builds a --exe whose modules are compiled debuggable and whose
        // embedded source is materialised at startup, so a debugger attached to the process
        // can set breakpoints / step. --debug-wait additionally blocks at startup until a
        // debugger has attached and armed its breakpoints (implies --debug).
        public bool Debug { get; set; }
        public bool DebugWait { get; set; }
        // Phase 31 — --dll: emit a .NET class library embedding the bundle, with a
        // generated factory (Namespace.Class.CreateEngine()) a host app calls. No
        // Prolog goal entry point. Namespace defaults to the inferred DLL file name,
        // class to "Bundle".
        public string DllPath { get; set; } = "";
        public string? DllNamespace { get; set; }
        public string? DllClass { get; set; }
        // Chunk 247: foreign-DLL paths (each carrying
        // [PrologPredicate]-decorated static methods). The linker
        // reflects each, registers the discovered name/arity
        // indicators as resolved during reachability, and records
        // the assembly filenames in the bundle so the runtime
        // auto-loads them.
        public List<string> ForeignDlls { get; } = new();
        // --native-dll: native C libraries (DLL/.so/.dylib) backing :- native
        // functions; recorded in the bundle so the runtime auto-loads them.
        public List<string> NativeDlls { get; } = new();
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

                case "--entry":
                case "-E":
                    if (++i >= args.Length) { ReportMissing(arg); return null; }
                    if (!ParseEntryList(args[i], opts.EntryPoints)) return null;
                    break;

                case "--allow-undefined":
                case "-u":
                    opts.AllowUndefined = true;
                    break;

                case "--strip":
                case "-s":
                    opts.StripSource = true;
                    break;

                case "--with-compiled-il":
                case "-i":
                    opts.IncludeCompiledIl = true;
                    break;

                case "--strip-wam":
                    opts.IncludeCompiledIl = true;   // strip-wam implies IL
                    opts.StripWam = true;
                    break;

                case "--prune-report":
                    opts.RegionPruneReport = true;
                    break;

                case "--stdlib":
                    opts.BakePrelude = true;
                    break;

                // Phase 33 T1 — bake only the REACHED prelude predicates
                // (closure over the prelude call graph). Opt-in: runtime-
                // constructed goals naming unreached prelude predicates raise
                // existence_error (declare them :- ensure_linked to keep them).
                case "--prune-prelude":
                    opts.PrunePrelude = true;
                    break;

                case "--no-region-prune":
                    opts.RegionPrune = false;
                    break;

                case "--dump-wam":
                    if (++i >= args.Length) { ReportMissing(arg); return null; }
                    opts.DumpWamPath = args[i];
                    break;

                case "--dump-il":
                    if (++i >= args.Length) { ReportMissing(arg); return null; }
                    opts.DumpIlPath = args[i];
                    opts.IncludeCompiledIl = true;   // nothing to dump unless IL is built
                    break;

                case "--map":
                case "-m":
                    if (++i >= args.Length) { ReportMissing(arg); return null; }
                    opts.MapPath = args[i];
                    break;

                case "--exe":
                case "-e":
                    if (++i >= args.Length) { ReportMissing(arg); return null; }
                    opts.ExePath = args[i];
                    break;

                case "--goal":
                case "-g":
                    if (++i >= args.Length) { ReportMissing(arg); return null; }
                    opts.Goal = args[i];
                    break;

                case "--self-contained":
                case "-c":
                    opts.SelfContained = true;
                    break;

                case "--debug":
                    opts.Debug = true;
                    break;

                case "--debug-wait":
                    opts.Debug = true;
                    opts.DebugWait = true;
                    break;

                case "--dll":
                case "-d":
                    if (++i >= args.Length) { ReportMissing(arg); return null; }
                    opts.DllPath = args[i];
                    break;

                case "--dll-namespace":
                    if (++i >= args.Length) { ReportMissing(arg); return null; }
                    opts.DllNamespace = args[i];
                    break;

                case "--dll-class":
                    if (++i >= args.Length) { ReportMissing(arg); return null; }
                    opts.DllClass = args[i];
                    break;

                case "--verbose":
                case "-v":
                    opts.Verbose = true;
                    break;

                case "--foreign-dll":
                case "-f":
                    if (++i >= args.Length) { ReportMissing(arg); return null; }
                    if (!System.IO.File.Exists(args[i]))
                    {
                        Console.Error.WriteLine(
                            $"shumway-link: --foreign-dll '{args[i]}' not found.");
                        return null;
                    }
                    opts.ForeignDlls.Add(System.IO.Path.GetFullPath(args[i]));
                    break;

                case "--native-dll":
                case "-n":
                    if (++i >= args.Length) { ReportMissing(arg); return null; }
                    if (!System.IO.File.Exists(args[i]))
                    {
                        Console.Error.WriteLine(
                            $"shumway-link: --native-dll '{args[i]}' not found.");
                        return null;
                    }
                    opts.NativeDlls.Add(System.IO.Path.GetFullPath(args[i]));
                    break;

                default:
                    if (arg.StartsWith("-"))
                    {
                        Console.Error.WriteLine($"shumway-link: unknown option '{arg}'.");
                        return null;
                    }
                    // Chunk 435 — wildcard inputs (`shumway-link ... *.shmo`),
                    // expanded here since the Windows shell passes globs
                    // verbatim (the chunk-434 shumway-compile counterpart).
                    if (arg.IndexOfAny(WildcardChars) >= 0)
                    {
                        if (!TryExpandWildcard(arg, opts.InputPaths)) return null;
                        break;
                    }
                    opts.InputPaths.Add(arg);
                    break;
            }
        }

        if (string.IsNullOrEmpty(opts.OutputPath) && string.IsNullOrEmpty(opts.ExePath)
            && string.IsNullOrEmpty(opts.DllPath))
        {
            Console.Error.WriteLine(
                "shumway-link: --output (or --exe / --dll) is required.");
            return null;
        }
        if (opts.InputPaths.Count == 0)
        {
            Console.Error.WriteLine(
                "shumway-link: at least one input is required (.shmo object or .shum library).");
            return null;
        }
        if (opts.EntryPoints.Count == 0 && string.IsNullOrEmpty(opts.Goal))
        {
            Console.Error.WriteLine(
                "shumway-link: at least one --entry pred/N or --goal Term is required "
                + "(otherwise no module would be reachable).");
            return null;
        }
        if (!string.IsNullOrEmpty(opts.ExePath) && string.IsNullOrEmpty(opts.Goal))
        {
            Console.Error.WriteLine(
                "shumway-link: --exe requires --goal Term (the entry call the "
                + "generated executable runs at startup).");
            return null;
        }
        if (opts.SelfContained && string.IsNullOrEmpty(opts.ExePath))
        {
            Console.Error.WriteLine(
                "shumway-link: --self-contained only makes sense with --exe.");
            return null;
        }
        if (opts.Debug && string.IsNullOrEmpty(opts.ExePath))
        {
            Console.Error.WriteLine(
                "shumway-link: --debug / --debug-wait only make sense with --exe.");
            return null;
        }
        if (opts.Debug && opts.StripSource)
        {
            Console.Error.WriteLine(
                "shumway-link: --debug and --strip are contradictory — a debug executable "
                + "materialises its embedded source for the debugger to open, but --strip "
                + "removes that source.");
            return null;
        }
        if (opts.Debug && (opts.IncludeCompiledIl || opts.StripWam))
        {
            Console.Error.WriteLine(
                "shumway-link: --debug is Tier-0 (interpreted) source-level debugging; it is "
                + "incompatible with --with-compiled-il / --strip-wam (Tier-1 IL, which has no "
                + "debug stop sites). Drop the IL flags for a debug build.");
            return null;
        }
        if (!string.IsNullOrEmpty(opts.ExePath) && !string.IsNullOrEmpty(opts.DllPath))
        {
            Console.Error.WriteLine(
                "shumway-link: --exe and --dll are mutually exclusive (an executable runs a "
                + "goal at startup; a library is loaded by a host app).");
            return null;
        }
        if ((opts.DllNamespace is not null || opts.DllClass is not null)
            && string.IsNullOrEmpty(opts.DllPath))
        {
            Console.Error.WriteLine(
                "shumway-link: --dll-namespace / --dll-class only make sense with --dll.");
            return null;
        }
        return opts;
    }

    private static readonly char[] WildcardChars = { '*', '?' };

    /// <summary>Chunk 435 — expands a wildcard input argument against the
    /// file system (directory part + pattern part), appending matches in
    /// case-insensitive sorted order. Returns false (after printing the
    /// error) when nothing matches or the directory is unusable. Mirrors
    /// shumway-compile's chunk-434 expansion.</summary>
    private static bool TryExpandWildcard(string arg, List<string> into)
    {
        string dir = System.IO.Path.GetDirectoryName(arg) is { Length: > 0 } d ? d : ".";
        string pattern = System.IO.Path.GetFileName(arg);
        List<string> matches;
        try
        {
            matches = System.IO.Directory.EnumerateFiles(dir, pattern).ToList();
        }
        catch (Exception ex) when (ex is System.IO.IOException
                                || ex is System.IO.DirectoryNotFoundException
                                || ex is ArgumentException
                                || ex is UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"shumway-link: cannot expand '{arg}': {ex.Message}");
            return false;
        }
        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"shumway-link: no files match '{arg}'.");
            return false;
        }
        matches.Sort(StringComparer.OrdinalIgnoreCase);
        into.AddRange(matches);
        return true;
    }

    private static bool ParseEntryList(string specList, List<PredicateRef> output)
    {
        foreach (string item in specList.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = item.Trim();
            int slash = trimmed.LastIndexOf('/');
            if (slash <= 0 || !int.TryParse(trimmed.AsSpan(slash + 1), out int arity) || arity < 0)
            {
                Console.Error.WriteLine(
                    $"shumway-link: malformed --entry '{trimmed}' (expected Name/Arity).");
                return false;
            }
            output.Add(new PredicateRef(trimmed[..slash], arity));
        }
        return true;
    }

    private static void ReportMissing(string option) =>
        Console.Error.WriteLine($"shumway-link: option '{option}' requires a value.");

    /// <summary>A failed link must not leave a stale bundle behind, so remove any
    /// pre-existing output — what a C linker does when linking fails.</summary>
    private static void RemoveStaleOutput(string? output)
    {
        if (string.IsNullOrEmpty(output)) return;
        try
        {
            if (System.IO.File.Exists(output)) System.IO.File.Delete(output);
        }
        catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"shumway-link: warning: could not remove stale {output}: {ex.Message}");
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: shumway-link {-o <output.shum> | -e <output.exe>} {-E pred/N | -g Goal} [options] <a.shmo ... [lib.shum ...]>\n"
            + "\n"
            + "Links compiled Prolog modules (.shmo, produced by shumway-compile) into a\n"
            + "single runnable bundle (.shum) or a native executable. Only code reachable\n"
            + "from the entry points is kept. Inputs may use wildcards (e.g. *.shmo) —\n"
            + "expanded by the linker itself, in sorted order, so they work from any shell.\n"
            + "\n"
            + "Inputs route by extension. A .shmo is an object and is always linked. A .shum\n"
            + "is a LIBRARY (a librarian archive built by shumway-lib): its modules are pulled\n"
            + "in only on demand, to satisfy a reference the objects leave unresolved, like a\n"
            + "C archive (.a/.lib). Libraries are searched in the order given; the first that\n"
            + "provides a needed predicate wins. Modules no reference reaches are not linked.\n"
            + "\n"
            + "Options:\n"
            + "  -o, --output <path>      Output bundle path. Required unless --exe is given.\n"
            + "  -E, --entry list         Entry-point predicate(s), e.g. main/0. Comma-\n"
            + "                           separated within one flag; the flag is repeatable.\n"
            + "                           At least one entry point (or --goal) is required.\n"
            + "  -u, --allow-undefined    Turn missing-predicate errors into warnings and\n"
            + "                           produce the bundle anyway. Calling a missing\n"
            + "                           predicate at runtime raises an existence_error.\n"
            + "  -s, --strip              Do not embed the Prolog source text in the bundle.\n"
            + "                           Programs run unchanged (execution uses the\n"
            + "                           compiled code), but listing/1 and source positions\n"
            + "                           in error messages are unavailable. Use for smaller\n"
            + "                           bundles or to avoid shipping source code.\n"
            + "  -i, --with-compiled-il   Also precompile predicates to .NET IL and embed\n"
            + "                           the resulting assembly in the bundle. The engine\n"
            + "                           then runs them as compiled .NET code from the\n"
            + "                           start, with no compilation pause at runtime.\n"
            + "                           Bigger bundle, faster execution. Related\n"
            + "                           predicates are compiled together into shared\n"
            + "                           methods and redundant standalone copies are\n"
            + "                           pruned (see --no-region-prune to disable).\n"
            + "      --no-region-prune    With --with-compiled-il: emit one standalone IL\n"
            + "                           method per predicate instead of the default\n"
            + "                           shared-method layout. Mainly for inspecting the\n"
            + "                           generated code; bundles are larger and typically\n"
            + "                           slower.\n"
            + "      --stdlib             Embed the precompiled standard library (the\n"
            + "                           prelude) in the bundle so loading it skips\n"
            + "                           compiling the stdlib at startup (and, under\n"
            + "                           --with-compiled-il, ships it as Tier-1 IL too).\n"
            + "                           Automatic with --exe. Larger bundle, faster start;\n"
            + "                           load via PrologEngine.FromBundle (a plain\n"
            + "                           new PrologEngine()+LoadBundle ignores it).\n"
            + "      --prune-prelude      With --stdlib/--exe: bake only the prelude\n"
            + "                           predicates the program can reach (closure over\n"
            + "                           the prelude call graph) instead of the whole\n"
            + "                           library. Runtime-constructed goals naming an\n"
            + "                           unreached prelude predicate raise\n"
            + "                           existence_error; declare such predicates\n"
            + "                           :- ensure_linked to keep them.\n"
            + "      --strip-wam          Implies --with-compiled-il, and additionally\n"
            + "                           drops the portable bytecode of every predicate\n"
            + "                           that has compiled IL. Smaller bundles. The result\n"
            + "                           requires the .NET JIT (it cannot run under\n"
            + "                           Native AOT).\n"
            + "  -f, --foreign-dll <path> A .NET assembly exposing predicates written in C#\n"
            + "                           ([PrologPredicate] static methods). They resolve\n"
            + "                           as foreign predicates during linking, and the\n"
            + "                           bundle records the assembly name so the engine\n"
            + "                           loads it automatically at runtime. Repeatable.\n"
            + "                           --exe copies each foreign DLL next to the\n"
            + "                           produced executable.\n"
            + "  -n, --native-dll <path>  A native C library (DLL/.so/.dylib) backing\n"
            + "                           ':- native' functions (resolved by P/Invoke). The\n"
            + "                           bundle records its name so the engine loads it\n"
            + "                           automatically at runtime. Repeatable. --exe copies\n"
            + "                           each next to the produced executable.\n"
            + "  -m, --map <path>         Write a human-readable report of what was linked:\n"
            + "                           per-module sizes, exported / dynamic predicates,\n"
            + "                           dropped modules, totals.\n"
            + "      --prune-report       Report (with --verbose) how much standalone\n"
            + "                           compiled code the shared-method layout can prune.\n"
            + "                           Analysis only; the bundle is unchanged.\n"
            + "      --dump-wam <path>    Append a disassembly of the bytecode the bundle\n"
            + "                           ships to <path>, for inspection. Appends across\n"
            + "                           runs; delete the file between runs.\n"
            + "      --dump-il <path>     Append the compiled .NET IL the bundle ships to\n"
            + "                           <path> (implies --with-compiled-il). Appends\n"
            + "                           across runs; delete the file between runs.\n"
            + "  -e, --exe <path>         Produce a single-file native executable for the\n"
            + "                           current platform. The executable loads the bundle\n"
            + "                           and runs --goal at startup, then exits 0 (success)\n"
            + "                           / 1 (failure) / 2 (uncaught Prolog exception).\n"
            + "                           Building requires the .NET 10 SDK; running needs\n"
            + "                           the .NET 10 runtime unless --self-contained.\n"
            + "  -g, --goal <term>        The Prolog goal the --exe runs at startup. The\n"
            + "                           trailing '.' is optional ('main' and 'main.' are\n"
            + "                           both fine). Checked syntactically at link time,\n"
            + "                           and counted as an entry point for reachability.\n"
            + "  -c, --self-contained     With --exe, bundle the .NET runtime into the\n"
            + "                           executable (~70 MB, runs on a machine with\n"
            + "                           nothing installed). Default is framework-\n"
            + "                           dependent (~5-10 MB, needs the .NET runtime).\n"
            + "      --debug              With --exe, build the executable in debug mode:\n"
            + "                           its modules compile debuggable and materialise\n"
            + "                           their embedded source at startup, so a debugger\n"
            + "                           attached to the process can set breakpoints and\n"
            + "                           step. Requires the bundle to carry source (compile\n"
            + "                           the inputs with --debug; not with --strip).\n"
            + "      --debug-wait         Like --debug, but the executable also blocks at\n"
            + "                           startup until a debugger has attached and armed\n"
            + "                           its breakpoints — so the first goal can be stopped\n"
            + "                           in. Implies --debug.\n"
            + "  -d, --dll <path>         Produce a .NET class library (.dll) embedding the\n"
            + "                           bundle, with a generated factory a host app calls:\n"
            + "                           `var e = Ns.Class.CreateEngine();`. No Prolog goal —\n"
            + "                           the host picks the goals. The Shumway engine DLLs\n"
            + "                           are copied next to the output. Needs --entry for\n"
            + "                           reachability (no --goal). Mutually exclusive w/ --exe.\n"
            + "      --dll-namespace <ns> Namespace for the generated factory (default: inferred\n"
            + "                           from the .dll file name, e.g. myprog.dll -> MyProg).\n"
            + "      --dll-class <name>   Class name for the generated factory (default: Bundle).\n"
            + "  -v, --verbose            Verbose progress + diagnostics to stderr.\n"
            + "  -h, --help               Show this message.");
    }
}
