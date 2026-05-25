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

        var config = new LinkConfig
        {
            Objects = objects,
            EntryPoints = opts.EntryPoints,
            AllowUndefined = opts.AllowUndefined,
            VerboseOut = opts.Verbose ? Console.Error : null,
            StripSource = opts.StripSource,
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

        try
        {
            File.WriteAllBytes(opts.OutputPath, result.Bytes!);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"shumway-link: error writing '{opts.OutputPath}': {ex.Message}");
            return ExitLinkError;
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

        if (opts.Verbose)
        {
            Console.Error.WriteLine(
                $"shumway-link: wrote {opts.OutputPath} "
                + $"(modules={result.ReachedModules.Count}, "
                + $"dropped={result.UnreachableModules.Count}, "
                + $"preds={result.ReachedPredicates.Count}, "
                + $"bytes={result.Bytes!.Length}).");
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
        public string MapPath { get; set; } = "";
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

                case "--map":
                case "-m":
                    if (++i >= args.Length) { ReportMissing(arg); return null; }
                    opts.MapPath = args[i];
                    break;

                case "--verbose":
                case "-v":
                    opts.Verbose = true;
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

        if (string.IsNullOrEmpty(opts.OutputPath))
        {
            Console.Error.WriteLine("shumway-link: --output is required.");
            return null;
        }
        if (opts.InputPaths.Count == 0)
        {
            Console.Error.WriteLine("shumway-link: at least one .shmo input is required.");
            return null;
        }
        if (opts.EntryPoints.Count == 0)
        {
            Console.Error.WriteLine(
                "shumway-link: at least one --entry pred/N is required "
                + "(otherwise no module would be reachable).");
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
            "Usage: shumway-link -o <output.shum> --entry pred/N[,pred2/N...] [options] <a.shmo b.shmo ...>\n"
            + "\n"
            + "Options:\n"
            + "  -o, --output <path>      Output bundle path (required).\n"
            + "      --entry list         One or more entry-point predicates. Comma-\n"
            + "                           separated within a flag; flag is repeatable.\n"
            + "                           At least one entry point is required.\n"
            + "      --allow-undefined    Downgrade missing-predicate errors to warnings\n"
            + "                           and produce the bundle anyway. The engine will\n"
            + "                           raise existence_error/2 if a missing predicate\n"
            + "                           is actually invoked.\n"
            + "  -s, --strip              Strip the embedded Prolog source from each\n"
            + "                           bundle entry. Useful for size analysis or IP-\n"
            + "                           protection archives. Current engine.LoadBundle\n"
            + "                           re-consults source so stripped bundles cannot\n"
            + "                           currently dispatch their predicates — a warning\n"
            + "                           is emitted.\n"
            + "  -m, --map <path>         Write a human-readable map file describing what\n"
            + "                           landed in the bundle: per-module sizes, public\n"
            + "                           / dynamic predicate lists, reached / dropped\n"
            + "                           modules, totals. Linker-style audit output.\n"
            + "  -v, --verbose            Verbose progress + diagnostics to stderr.\n"
            + "  -h, --help               Show this message.");
    }
}
