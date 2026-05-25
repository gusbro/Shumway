using Shumway.Embedding;

namespace Shumway.Compile;

/// <summary>
/// <c>shumway-compile</c> CLI. Takes one Prolog source file and emits
/// a per-module compiled-object <c>.shmo</c> artifact (chunk 160). The
/// linker (<c>shumway-link</c>, chunk 164) combines one or more
/// <c>.shmo</c>s into a deployable <c>.shum</c> bundle.
///
/// <para>Usage:</para>
/// <code>
///   shumway-compile [-o output.shmo] [-v] input.pl
/// </code>
///
/// <para>When <c>-o</c> is omitted, the output path is derived from
/// the input by replacing the extension with <c>.shmo</c>.</para>
///
/// <para>Exit codes: 0 on success, 1 on compile error, 3 on usage
/// error.</para>
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

        try
        {
            var obj = ShmoCompiler.CompileFile(opts.InputPath);
            ShmoWriter.WriteToFile(obj, opts.OutputPath);
            if (opts.Verbose)
            {
                Console.Error.WriteLine(
                    $"shumway-compile: wrote {opts.OutputPath} "
                    + $"(module={obj.ModuleName}, "
                    + $"defined={obj.Defined.Count}, "
                    + $"calls={obj.CallGraph.Count}).");
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

    private sealed class Options
    {
        public string InputPath { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public bool Verbose { get; set; }
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

                default:
                    if (arg.StartsWith("-"))
                    {
                        Console.Error.WriteLine($"shumway-compile: unknown option '{arg}'.");
                        return null;
                    }
                    if (opts.InputPath.Length > 0)
                    {
                        Console.Error.WriteLine(
                            "shumway-compile: only one input file is supported per invocation "
                            + $"(extra: '{arg}').");
                        return null;
                    }
                    opts.InputPath = arg;
                    break;
            }
        }

        if (string.IsNullOrEmpty(opts.InputPath))
        {
            Console.Error.WriteLine("shumway-compile: an input source file is required.");
            return null;
        }
        if (string.IsNullOrEmpty(opts.OutputPath))
        {
            opts.OutputPath = Path.ChangeExtension(opts.InputPath, ".shmo");
        }
        return opts;
    }

    private static void ReportMissing(string option) =>
        Console.Error.WriteLine($"shumway-compile: option '{option}' requires a value.");

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: shumway-compile [options] <source.pl>\n"
            + "\n"
            + "Options:\n"
            + "  -o, --output <path>  Output .shmo path. Default: input with .shmo suffix.\n"
            + "  -v, --verbose        Verbose progress output to stderr.\n"
            + "  -h, --help           Show this message.");
    }
}
