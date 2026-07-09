using Shumway.Compiler.Wam;

namespace Shumway.Disasm;

/// <summary>
/// <c>shumway-disasm</c> CLI. Compiles the static predicates in a Prolog
/// source and prints their WAM bytecode disassembly — the post-indexing
/// layout (<c>switch_on_term</c> / <c>try</c> / <c>retry</c> / <c>trust</c>
/// chains plus per-clause bodies) the Tier-0 interpreter runs. A diagnostic
/// for inspecting code generation while optimising.
///
/// <para>Usage:</para>
/// <code>
///   shumway-disasm [options] file.pl
///   shumway-disasm -e "conc([],L,L). conc([H|T],L,[H|R]):-conc(T,L,R)."
/// </code>
///
/// <para>Options:</para>
/// <list type="bullet">
/// <item><c>-e, --eval &lt;source&gt;</c> — disassemble inline source instead
/// of a file.</item>
/// <item><c>-p, --pred &lt;Name/Arity&gt;</c> — restrict output to these
/// predicates (repeatable, or comma-separated).</item>
/// <item><c>-h, --help</c> — this help.</item>
/// </list>
///
/// <para>Exit codes: 0 success, 1 a predicate failed to compile, 3 usage
/// error.</para>
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitCompileError = 1;
    private const int ExitUsageError = 3;

    public static int Main(string[] args)
    {
        string? source = null;
        string? inputPath = null;
        var filter = new List<(string, int)>();
        bool emitDebugInfo = false;   // default: release (what the engine runs by default)
        bool arityCompat = false;     // --arity: lex Arity/Prolog32 sources
        bool audit = false;           // --audit: emit indexing-quality verdict lines
        bool census = false;          // --census: emit one opcode-pair/shape tally line
        bool detcensus = false;       // --detcensus: emit redundant-cut / det-fixpoint tally
        bool foldcensus = false;      // --foldcensus: ADR-031 Guard,!,Body/Rest fold sizing

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-h" or "--help":
                    PrintHelp();
                    return ExitOk;
                case "--debug":
                    emitDebugInfo = true;
                    break;
                case "--release":
                    emitDebugInfo = false;
                    break;
                case "-a" or "--arity":
                    arityCompat = true;
                    break;
                case "--audit":
                    audit = true;
                    break;
                case "--census":
                    census = true;
                    break;
                case "--detcensus":
                    detcensus = true;
                    break;
                case "--foldcensus":
                    foldcensus = true;
                    break;
                case "-e" or "--eval":
                    if (++i >= args.Length) return Usage("missing source after " + a);
                    source = args[i];
                    break;
                case "-p" or "--pred":
                    if (++i >= args.Length) return Usage("missing indicator after " + a);
                    foreach (string part in args[i].Split(',', StringSplitOptions.RemoveEmptyEntries
                                                              | StringSplitOptions.TrimEntries))
                    {
                        if (!TryParseIndicator(part, out var ind))
                            return Usage($"bad predicate indicator '{part}' (expected Name/Arity)");
                        filter.Add(ind);
                    }
                    break;
                default:
                    if (a.StartsWith('-')) return Usage("unknown option " + a);
                    if (inputPath is not null) return Usage("more than one input file");
                    inputPath = a;
                    break;
            }
        }

        if (source is null && inputPath is null)
            return Usage("no input (give a file or -e <source>)");
        if (source is not null && inputPath is not null)
            return Usage("give either a file or -e <source>, not both");

        if (source is null)
        {
            try { source = File.ReadAllText(inputPath!); }
            catch (Exception ex) { return Usage($"cannot read {inputPath}: {ex.Message}"); }
        }

        if (audit)
        {
            // Emit one tab-separated verdict line per predicate for corpus-wide
            // aggregation: AUDIT <cat> <clauses> <worst> <potential> <arg> <name>/<arity>
            try
            {
                foreach (var e in PredicateDisassembler.AuditIndexing(source, arityCompat))
                {
                    if (e.Clauses < 2) continue;   // single-clause preds don't index
                    Console.WriteLine(string.Join('\t',
                        "AUDIT", e.Category, e.Clauses, e.WorstBucket, e.PotAtomInt,
                        e.PotStruct, e.PotStructNoWild, e.CutPct, e.Det ? "det" : "-",
                        e.DiscrimArg, $"{e.Name}/{e.Arity}", e.WorstKey));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: parse failed: {ex.Message}");
                return ExitCompileError;
            }
            return ExitOk;
        }

        if (census)
        {
            // One tab-separated tally line for the whole source, for corpus-wide
            // aggregation across files (sum the columns): CENSUS <preds> <ops>
            // <pairs> <clauses> <tailClauses> <cutTailClauses> <cut>deallocate_proceed
            // <cut>proceed <call><cut> <deallocate><exec> <cut>deallocate <cut><exec>
            try
            {
                var c = PredicateDisassembler.CensusOpcodes(source, arityCompat);
                Console.WriteLine(string.Join('\t', "CENSUS",
                    c.Predicates, c.Ops, c.Pairs, c.Clauses, c.TailClauses,
                    c.CutTailClauses, c.CutDeallocProceed, c.CutProceed, c.CallCut,
                    c.DeallocExecute, c.CutDealloc, c.CutExecute));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: parse failed: {ex.Message}");
                return ExitCompileError;
            }
            return ExitOk;
        }

        if (detcensus)
        {
            // One line per source: DETCENSUS <preds> <detPreds> <clauses>
            // <neckLastCut> <deepLastCut> <elideBuiltin> <elideIntra>
            // <blockedCross> <blockedNondet>
            try
            {
                var d = PredicateDisassembler.CensusDet(source, arityCompat);
                Console.WriteLine(string.Join('\t', "DETCENSUS",
                    d.Predicates, d.DetPredicates, d.Clauses, d.NeckLastCut,
                    d.DeepLastCut, d.ElideBuiltin, d.ElideIntra, d.BlockedCross,
                    d.BlockedNondet));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: parse failed: {ex.Message}");
                return ExitCompileError;
            }
            return ExitOk;
        }

        if (foldcensus)
        {
            // One line per source: FOLDCENSUS <preds> <candidates> <trivial>
            // <threaded> <candidateClauses>
            try
            {
                var f = PredicateDisassembler.CensusFold(source, arityCompat);
                Console.WriteLine(string.Join('\t', "FOLDCENSUS",
                    f.Predicates, f.Candidates, f.Trivial, f.Threaded, f.CandidateClauses));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: parse failed: {ex.Message}");
                return ExitCompileError;
            }
            return ExitOk;
        }

        IReadOnlyList<PredicateDisassembler.Entry> entries;
        try
        {
            entries = PredicateDisassembler.Disassemble(
                source, filter.Count > 0 ? filter : null, emitDebugInfo, arityCompat);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: parse failed: {ex.Message}");
            return ExitCompileError;
        }

        if (entries.Count == 0)
        {
            Console.Error.WriteLine(filter.Count > 0
                ? "no matching predicate found"
                : "no predicates in input");
            return filter.Count > 0 ? ExitCompileError : ExitOk;
        }

        bool anyError = false;
        bool first = true;
        foreach (var e in entries)
        {
            if (!first) Console.WriteLine();
            first = false;
            if (e.Error is not null)
            {
                Console.WriteLine($"=== {e.Name}/{e.Arity} ===");
                Console.WriteLine($"  error: {e.Error}");
                anyError = true;
            }
            else
            {
                Console.Write(e.Text);
            }
        }
        return anyError ? ExitCompileError : ExitOk;
    }

    private static bool TryParseIndicator(string s, out (string Name, int Arity) ind)
    {
        ind = default;
        int slash = s.LastIndexOf('/');
        if (slash <= 0 || slash == s.Length - 1) return false;
        if (!int.TryParse(s[(slash + 1)..], out int arity) || arity < 0) return false;
        ind = (s[..slash], arity);
        return true;
    }

    private static int Usage(string message)
    {
        Console.Error.WriteLine("shumway-disasm: " + message);
        Console.Error.WriteLine("try 'shumway-disasm --help'");
        return ExitUsageError;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            shumway-disasm — dump WAM bytecode disassembly for Prolog predicates

            USAGE:
              shumway-disasm [options] <file.pl>
              shumway-disasm -e "<source>"

            OPTIONS:
              -e, --eval <source>     disassemble inline source instead of a file
              -p, --pred <Name/Arity> only these predicates (repeatable / comma-separated)
              --release               omit debug-info markers (default — what the
                                      engine runs under compile_mode=release)
              --debug                 include the per-clause meta dbg_info markers
              -a, --arity             lex Arity/Prolog32 sources (#line, $atoms)
              --audit                 emit indexing-quality verdict lines instead
                                      of disassembly (tab-separated, for corpus
                                      aggregation)
              --census                emit one tab-separated opcode-pair / clause-
                                      shape tally line for the source (peephole-
                                      fusion + `!, tailCall` census; sum columns
                                      across files for a corpus total)
              -h, --help              show this help

            Predicates are compiled with first-argument / multi-argument indexing,
            so the output shows the switch_on_term / try / retry / trust dispatch
            the Tier-0 interpreter actually runs. DCG rules are expanded; directives
            are skipped.

            EXAMPLES:
              shumway-disasm benchmarks/vanroy/nreverse.pl
              shumway-disasm -p conc/3 benchmarks/vanroy/nreverse.pl
              shumway-disasm -e "fac(0,1). fac(N,F):-N>0,N1 is N-1,fac(N1,F1),F is N*F1."
            """);
    }
}
