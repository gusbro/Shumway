using Shumway.Embedding;

namespace Shumway.Bundler;

/// <summary>
/// <c>shumway-bundler</c> CLI. Since chunk 72 this is a thin wrapper
/// around <see cref="Shumway.Embedding.Bundler.Build"/>: argument
/// parsing here, orchestration in the library.
///
/// <para>Usage:</para>
/// <code>
///   shumway-bundler --output app.shum [--entry-points pred/N,...] file1.pl file2.pl
/// </code>
///
/// <para>Exit codes follow ADR-009: 0 on success, 1 on bundle errors,
/// 3 on usage errors.</para>
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitBundleError = 1;
    private const int ExitUsageError = 3;

    public static int Main(string[] args)
    {
        var opts = ParseArgs(args);
        if (opts is null) return ExitUsageError;

        var config = new BundleConfig
        {
            SourceFiles = opts.SourceFiles,
            OutputPath = opts.OutputPath,
            EntryPoints = opts.EntryPoints,
            IncludeCompiledBytecode = opts.WithBytecode,
            IncludeCompiledIl = opts.WithCompiledIl,
            Verbose = opts.Verbose,
            VerboseOut = opts.Verbose ? Console.Error : null,
        };

        var result = Shumway.Embedding.Bundler.Build(config);
        foreach (var d in result.Diagnostics)
        {
            var stream = d.Severity == BundleSeverity.Error
                ? Console.Error : Console.Out;
            string prefix = d.Severity switch
            {
                BundleSeverity.Error => "error",
                BundleSeverity.Warning => "warning",
                _ => "info",
            };
            string sourcePart = d.Source is null ? "" : $" ({d.Source})";
            stream.WriteLine($"shumway-bundler: {prefix}: {d.Message}{sourcePart}");
        }
        return result.Success ? ExitOk : ExitBundleError;
    }

    // ------------------------------------------------------------------------
    // Argument parsing
    // ------------------------------------------------------------------------

    private sealed class Options
    {
        public List<string> SourceFiles { get; } = new();
        public string OutputPath { get; set; } = "";
        public List<EntryPointSpec> EntryPoints { get; } = new();
        public bool Verbose { get; set; }
        public bool WithBytecode { get; set; }
        public bool WithCompiledIl { get; set; }
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

                case "--entry-points":
                    if (++i >= args.Length) { ReportMissing(arg); return null; }
                    try
                    {
                        foreach (var spec in ParseEntryPoints(args[i]))
                            opts.EntryPoints.Add(spec);
                    }
                    catch (ArgumentException ex)
                    {
                        Console.Error.WriteLine(ex.Message);
                        return null;
                    }
                    break;

                case "--verbose":
                case "-v":
                    opts.Verbose = true;
                    break;

                case "--with-bytecode":
                    opts.WithBytecode = true;
                    break;

                case "--with-compiled-il":
                    opts.WithCompiledIl = true;
                    break;

                default:
                    if (arg.StartsWith("-"))
                    {
                        Console.Error.WriteLine($"shumway-bundler: unknown option '{arg}'.");
                        return null;
                    }
                    opts.SourceFiles.Add(arg);
                    break;
            }
        }

        if (string.IsNullOrEmpty(opts.OutputPath))
        {
            Console.Error.WriteLine("shumway-bundler: --output is required.");
            return null;
        }
        if (opts.SourceFiles.Count == 0)
        {
            Console.Error.WriteLine("shumway-bundler: at least one source file is required.");
            return null;
        }
        return opts;
    }

    private static IEnumerable<EntryPointSpec> ParseEntryPoints(string specList)
    {
        foreach (string item in specList.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = item.Trim();
            int slash = trimmed.LastIndexOf('/');
            if (slash <= 0 || !int.TryParse(trimmed.AsSpan(slash + 1), out int arity) || arity < 0)
                throw new ArgumentException(
                    $"shumway-bundler: malformed entry point '{trimmed}' (expected Name/Arity).");
            yield return new EntryPointSpec(trimmed[..slash], arity);
        }
    }

    private static void ReportMissing(string option) =>
        Console.Error.WriteLine($"shumway-bundler: option '{option}' requires a value.");

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: shumway-bundler --output <path> [options] <source files...>\n"
            + "\n"
            + "Options:\n"
            + "  -o, --output <path>           Output bundle path (required).\n"
            + "      --entry-points list       Comma-separated list of Name/Arity entries\n"
            + "                                to validate after writing.\n"
            + "      --with-bytecode           Embed pre-compiled WAM bytecode in the\n"
            + "                                bundle. LoadBundle uses it to pre-warm\n"
            + "                                Tier-1 IL — first call hits IL.\n"
            + "      --with-compiled-il        Emit a persisted .NET assembly holding\n"
            + "                                Tier-1 IL for every IL-eligible predicate\n"
            + "                                and embed it in the bundle. LoadBundle\n"
            + "                                binds methods directly — no Sigil emit\n"
            + "                                at consult time.\n"
            + "  -v, --verbose                 Verbose progress output to stderr.\n"
            + "  -h, --help                    Show this message.");
    }
}
