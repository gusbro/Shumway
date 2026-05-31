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
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitLinkError = 1;
    private const int ExitUsageError = 3;

    public static int Main(string[] args)
    {
        var opts = ParseArgs(args);
        if (opts is null) return ExitUsageError;

        var objects = new List<ShmoObject>();
        foreach (var path in opts.InputPaths)
        {
            try
            {
                objects.Add(ShmoReader.ReadFromFile(path));
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
            VerboseOut = opts.Verbose ? Console.Error : null,
            StripSource = opts.StripSource,
            IncludeCompiledIl = opts.IncludeCompiledIl,
            ForeignAssemblies = opts.ForeignDlls,
        };

        LinkResult result;
        try
        {
            result = ShmoLinker.Link(config);
        }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            Console.Error.WriteLine($"shumway-link: error: {ex.Message}");
            return ExitLinkError;
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
                ShmoBundleMap.WriteToFile(objects, opts.EntryPoints, result, opts.MapPath);
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
                foreignDllPaths: opts.ForeignDlls);
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
        public string MapPath { get; set; } = "";
        public string ExePath { get; set; } = "";
        public string Goal { get; set; } = "";
        public bool SelfContained { get; set; }
        // Chunk 247: foreign-DLL paths (each carrying
        // [PrologPredicate]-decorated static methods). The linker
        // reflects each, registers the discovered name/arity
        // indicators as resolved during reachability, and records
        // the assembly filenames in the bundle so the runtime
        // auto-loads them.
        public List<string> ForeignDlls { get; } = new();
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
                    if (++i >= args.Length) { ReportMissing(arg); return null; }
                    if (!ParseEntryList(args[i], opts.EntryPoints)) return null;
                    break;

                case "--allow-undefined":
                    opts.AllowUndefined = true;
                    break;

                case "--strip":
                case "-s":
                    opts.StripSource = true;
                    break;

                case "--with-compiled-il":
                    opts.IncludeCompiledIl = true;
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
                    opts.SelfContained = true;
                    break;

                case "--verbose":
                case "-v":
                    opts.Verbose = true;
                    break;

                case "--foreign-dll":
                    if (++i >= args.Length) { ReportMissing(arg); return null; }
                    if (!System.IO.File.Exists(args[i]))
                    {
                        Console.Error.WriteLine(
                            $"shumway-link: --foreign-dll '{args[i]}' not found.");
                        return null;
                    }
                    opts.ForeignDlls.Add(System.IO.Path.GetFullPath(args[i]));
                    break;

                default:
                    if (arg.StartsWith("-"))
                    {
                        Console.Error.WriteLine($"shumway-link: unknown option '{arg}'.");
                        return null;
                    }
                    opts.InputPaths.Add(arg);
                    break;
            }
        }

        if (string.IsNullOrEmpty(opts.OutputPath) && string.IsNullOrEmpty(opts.ExePath))
        {
            Console.Error.WriteLine(
                "shumway-link: --output (or --exe) is required.");
            return null;
        }
        if (opts.InputPaths.Count == 0)
        {
            Console.Error.WriteLine("shumway-link: at least one .shmo input is required.");
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
        return opts;
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

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: shumway-link {-o <output.shum> | --exe <output.exe>} {--entry pred/N | --goal Term} [options] <a.shmo b.shmo ...>\n"
            + "\n"
            + "Options:\n"
            + "  -o, --output <path>      Output bundle path. Required unless --exe is given.\n"
            + "      --entry list         One or more entry-point predicates. Comma-\n"
            + "                           separated within a flag; flag is repeatable.\n"
            + "                           At least one entry point is required.\n"
            + "      --allow-undefined    Downgrade missing-predicate errors to warnings\n"
            + "                           and produce the bundle anyway. The engine will\n"
            + "                           raise existence_error/2 if a missing predicate\n"
            + "                           is actually invoked.\n"
            + "  -s, --strip              Strip the embedded Prolog source from each\n"
            + "                           bundle entry. The bundle still dispatches\n"
            + "                           correctly — the engine loads predicates from\n"
            + "                           the precompiled bytecode (chunks 178/179).\n"
            + "                           Useful for size analysis or IP-protection\n"
            + "                           archives. listing/0 output and parser stack\n"
            + "                           traces lose their textual source.\n"
            + "  -m, --map <path>         Write a human-readable map file describing what\n"
            + "                           landed in the bundle: per-module sizes, public\n"
            + "                           / dynamic predicate lists, reached / dropped\n"
            + "                           modules, totals. Linker-style audit output.\n"
            + "  -e, --exe <path>         Emit a single-file native executable for the\n"
            + "                           current platform. The exe loads the bundle and\n"
            + "                           runs --goal at startup, then exits 0 (success)\n"
            + "                           / 1 (failure) / 2 (uncaught Prolog exception).\n"
            + "                           Requires the .NET 10 SDK on this machine. The\n"
            + "                           target machine needs .NET 10 runtime by default\n"
            + "                           (--self-contained avoids that).\n"
            + "  -g, --goal <term>        The Prolog goal the --exe runs at startup. The\n"
            + "                           trailing '.' is optional ('main' and 'main.'\n"
            + "                           are both fine). Validated syntactically at link\n"
            + "                           time. Also added as an implicit reachability\n"
            + "                           root for the linker.\n"
            + "      --self-contained     With --exe, bake the .NET runtime into the\n"
            + "                           executable. Produces a ~70 MB binary that needs\n"
            + "                           nothing installed on the target machine.\n"
            + "                           Default is framework-dependent (~5-10 MB exe,\n"
            + "                           requires .NET runtime on target).\n"
            + "  -v, --verbose            Verbose progress + diagnostics to stderr.\n"
            + "  -h, --help               Show this message.");
    }
}
