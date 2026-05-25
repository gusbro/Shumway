using Shumway.Embedding;

namespace Shumway.Compile;

/// <summary>
/// <c>shumway-compile</c> CLI. Takes one or more Prolog source files
/// and emits a per-module compiled-object <c>.shmo</c> artifact for
/// each (chunk 160). The linker (<c>shumway-link</c>, chunk 164)
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
/// predicate list per file (chunk 170).</para>
///
/// <para>Exit codes: 0 on success, 1 on compile error, 3 on usage
/// error. With multiple inputs, a failure on one file still attempts
/// the others — every error is reported, the exit code reflects the
/// worst outcome.</para>
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitCompileError = 1;
    private const int ExitUsageError = 3;

    public static int Main(string[] args)
    {
        var opts = ParseArgs(args);
        if (opts is null) return ExitUsageError;

        bool multi = opts.InputPaths.Count > 1;
        if (multi && !string.IsNullOrEmpty(opts.OutputPath))
        {
            // -o is interpreted as a directory under multi-input.
            try { Directory.CreateDirectory(opts.OutputPath); }
            catch (IOException ex)
            {
                Console.Error.WriteLine(
                    $"shumway-compile: cannot create output directory '{opts.OutputPath}': {ex.Message}");
                return ExitUsageError;
            }
        }

        int exit = ExitOk;
        foreach (var input in opts.InputPaths)
        {
            string output = ResolveOutputPath(input, opts.OutputPath, multi);
            int per = CompileOne(input, output, opts.Verbose, opts.BuildMode);
            if (per != ExitOk) exit = per;
        }
        return exit;
    }

    private static int CompileOne(string input, string output, bool verbose,
        ShmoBuildMode buildMode)
    {
        Console.Error.WriteLine(
            $"shumway-compile: compiling {input} -> {output} "
            + $"[{buildMode.ToString().ToLowerInvariant()}]");
        try
        {
            var result = ShmoCompiler.TryCompileFile(input, buildMode, maxErrors: 100);
            if (!result.Success)
            {
                foreach (var err in result.Errors)
                    Console.Error.WriteLine($"{input}:{err.Line}:{err.Column}: error: {err.Message}");
                Console.Error.WriteLine(
                    $"shumway-compile: {result.Errors.Count} error(s) in {input}.");
                return ExitCompileError;
            }
            var obj = result.Object!;
            ShmoWriter.WriteToFile(obj, output);
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
                                || ex is IOException)
        {
            Console.Error.WriteLine($"shumway-compile: error: {ex.Message}");
            return ExitCompileError;
        }
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
                    opts.BuildMode = ShmoBuildMode.Debug;
                    break;

                case "--release":
                case "-r":
                    opts.BuildMode = ShmoBuildMode.Release;
                    break;

                default:
                    if (arg.StartsWith("-"))
                    {
                        Console.Error.WriteLine($"shumway-compile: unknown option '{arg}'.");
                        return null;
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

    private static void ReportMissing(string option) =>
        Console.Error.WriteLine($"shumway-compile: option '{option}' requires a value.");

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: shumway-compile [options] <source.pl> [<source2.pl> ...]\n"
            + "\n"
            + "Options:\n"
            + "  -o, --output <path>  Output .shmo path (single input) or output\n"
            + "                       directory (multiple inputs). Default: alongside each\n"
            + "                       input with the extension replaced.\n"
            + "  -r, --release        Build in release mode (default).\n"
            + "  -d, --debug          Build in debug mode (records the mode in the .shmo;\n"
            + "                       linker surfaces it in --map output and may keep it\n"
            + "                       source-bearing when --strip is in effect).\n"
            + "  -v, --verbose        Verbose progress output to stderr.\n"
            + "  -h, --help           Show this message.");
    }
}
