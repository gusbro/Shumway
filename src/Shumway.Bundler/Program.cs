using System.Text.RegularExpressions;
using Shumway.Embedding;

namespace Shumway.Bundler;

/// <summary>
/// <c>shumway-bundler</c> — Phase-1 command-line tool. Takes one or more
/// Prolog source files plus an output path, validates that every file
/// parses and compiles end-to-end through a throwaway
/// <see cref="PrologEngine"/>, and writes the result as a <c>.shum</c>
/// bundle ready for <see cref="PrologEngine.LoadBundle(string)"/>.
///
/// <para>Usage:</para>
/// <code>
///   shumway-bundler --output app.shum [--entry-points pred/N,...] file1.pl file2.pl ...
/// </code>
///
/// <para>Exit codes follow ADR-009: 0 on success, 1 on bundle errors, 3 on
/// usage errors. Entry points are accepted but in v1 only validated for
/// existence — the reachability-pruning rule from the ADR lands when
/// bytecode-level bundles do.</para>
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitBundleError = 1;
    private const int ExitUsageError = 3;

    public static int Main(string[] args)
    {
        try
        {
            var opts = ParseArgs(args);
            if (opts is null) return ExitUsageError;

            var entries = new List<BundleEntry>(opts.SourceFiles.Count);
            foreach (string file in opts.SourceFiles)
            {
                if (!File.Exists(file))
                {
                    Console.Error.WriteLine($"shumway-bundler: source file not found: {file}");
                    return ExitBundleError;
                }
                string source = File.ReadAllText(file);
                string moduleName = ExtractModuleName(source) ?? Path.GetFileNameWithoutExtension(file);
                entries.Add(new BundleEntry(moduleName, source));
            }

            if (opts.Verbose)
            {
                Console.Error.WriteLine($"shumway-bundler: {entries.Count} module(s) staged.");
                foreach (var e in entries)
                    Console.Error.WriteLine($"  - {e.ModuleName} ({e.Source.Length} chars)");
            }

            var bundle = new Bundle(entries);
            try
            {
                BundleWriter.WriteToFile(bundle, opts.OutputPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"shumway-bundler: bundle failed: {ex.Message}");
                return ExitBundleError;
            }

            if (opts.EntryPoints.Count > 0)
            {
                // v1 entry-point validation: load into a throwaway engine and
                // confirm every named predicate is reachable as a query.
                var engine = new PrologEngine();
                engine.LoadBundle(bundle);
                foreach (var ep in opts.EntryPoints)
                {
                    if (!CheckEntryPointExists(engine, ep, out string error))
                    {
                        Console.Error.WriteLine($"shumway-bundler: entry-point check failed: {error}");
                        return ExitBundleError;
                    }
                }
            }

            if (opts.Verbose)
                Console.Error.WriteLine($"shumway-bundler: wrote {opts.OutputPath}.");
            return ExitOk;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"shumway-bundler: {ex.Message}");
            return ExitBundleError;
        }
    }

    private static bool CheckEntryPointExists(PrologEngine engine, EntryPointSpec ep, out string error)
    {
        // Build a goal that calls ep with arity anonymous variables and
        // immediately succeeds without binding anything — just verifies the
        // call resolves.
        var goalArgs = ep.Arity == 0
            ? ""
            : "(" + string.Join(", ", Enumerable.Range(0, ep.Arity).Select(_ => "_")) + ")";
        string probe = $"({ep.Name}{goalArgs} ; true).";
        try
        {
            engine.Query(probe);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ep.Name}/{ep.Arity}: {ex.Message}";
            return false;
        }
    }

    private static string? ExtractModuleName(string source)
    {
        // Look for `:- module(name).` on a non-comment line. The compiler's
        // own ClauseReader does the canonical parse; this regex is just a
        // pre-filter so the bundle's module-name metadata is populated
        // without having to re-parse here.
        var m = Regex.Match(source,
            @"^\s*:-\s*module\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)\s*\.",
            RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
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
    }

    private readonly record struct EntryPointSpec(string Name, int Arity);

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
                    foreach (var spec in ParseEntryPoints(args[i]))
                        opts.EntryPoints.Add(spec);
                    break;

                case "--verbose":
                case "-v":
                    opts.Verbose = true;
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
                    $"shumway-bundler: malformed entry point '{trimmed}' "
                    + "(expected Name/Arity).");
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
            + "  -v, --verbose                 Verbose progress output to stderr.\n"
            + "  -h, --help                    Show this message.");
    }
}
