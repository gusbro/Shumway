using Shumway.Core;
using Shumway.Embedding;

namespace Shumway.Repl;

/// <summary>
/// A minimal interactive top-level (REPL) for Shumway — Phase 5. It
/// consults any files named on the command line, then reads queries from
/// standard input, runs each, and prints its solutions; pressing ';'
/// after a solution searches for the next. The session ends at
/// <c>halt.</c> or end of input.
///
/// <para>This is a thin client over the <see cref="PrologEngine"/>
/// embedding API — its purpose is interactive exercising of Shumway, not
/// to be a full-featured development environment.</para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine(
            "Shumway Prolog top-level.  End a query with '.'  —  'halt.' or Ctrl-D exits.");

        // Split args at "--": everything before is a file to consult,
        // everything after is exposed to the program as the argv
        // Prolog flag (current_prolog_flag(argv, Argv)). Matches
        // SWI / GNU / SICStus convention.
        int sep = Array.IndexOf(args, "--");
        string[] consultFiles = sep < 0 ? args : args[..sep];
        string[] programArgs = sep < 0 ? Array.Empty<string>() : args[(sep + 1)..];

        var engine = new PrologEngine();
        engine.Flags.Argv = programArgs;
        foreach (string path in consultFiles)
            ConsultFile(engine, path);

        while (true)
        {
            string? query = ReadQuery();
            if (query is null) break;            // end of input
            if (query.Length == 0) continue;     // blank entry

            try
            {
                RunQuery(engine, query);
            }
            catch (Exception ex)
            {
                // A parse failure, an uncaught throw/1, or a runtime error.
                Console.WriteLine($"% {ex.GetType().Name}: {ex.Message}");
                if (Environment.GetEnvironmentVariable("SHUMWAY_DEBUG_TRACE") == "1")
                    Console.WriteLine(ex.StackTrace);
            }

            // halt/0,1 is caught inside the engine's solution iterator; it
            // surfaces as LastHaltExitCode rather than a .NET exception.
            if (engine.LastHaltExitCode is int exitCode)
                return exitCode;
        }

        Console.WriteLine();
        return 0;
    }

    private static void ConsultFile(PrologEngine engine, string path)
    {
        try
        {
            engine.ConsultString(File.ReadAllText(path));
            Console.WriteLine($"% consulted {path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"% could not consult {path}: {ex.Message}");
        }
    }

    /// <summary>Reads one query from standard input, joining lines until a
    /// line ends with the <c>.</c> clause terminator. Returns the empty
    /// string for a blank entry and <c>null</c> at end of input.</summary>
    private static string? ReadQuery()
    {
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            Console.Write(buffer.Length == 0 ? "?- " : "   ");
            string? line = Console.ReadLine();
            if (line is null)
                return buffer.Length == 0 ? null : buffer.ToString().Trim();
            buffer.Append(line).Append('\n');
            string accumulated = buffer.ToString().Trim();
            if (accumulated.Length == 0) return "";
            if (accumulated.EndsWith('.')) return accumulated;
        }
    }

    /// <summary>Runs a query and prints its solutions one at a time.</summary>
    private static void RunQuery(PrologEngine engine, string query)
    {
        using var solutions = engine.QueryAll(query).GetEnumerator();
        if (!solutions.MoveNext())
        {
            // No solutions — print false, unless the goal was `halt`
            // (which Main detects via LastHaltExitCode).
            if (engine.LastHaltExitCode is null)
                Console.WriteLine("false.");
            return;
        }
        while (true)
        {
            Solution solution = solutions.Current;
            Console.Write(solution.Bindings.Count == 0 ? "true" : solution.ToString());
            if (!WantsAnotherSolution())
            {
                Console.WriteLine(".");
                return;
            }
            Console.WriteLine(" ;");
            if (!solutions.MoveNext())
            {
                if (engine.LastHaltExitCode is null)
                    Console.WriteLine("false.");
                return;
            }
        }
    }

    /// <summary>After a solution, asks whether to search for the next:
    /// ';' means yes. A single keypress when interactive, a whole line
    /// when input is redirected — so the top-level stays scriptable.</summary>
    private static bool WantsAnotherSolution()
    {
        if (Console.IsInputRedirected)
        {
            string? line = Console.ReadLine();
            return line is not null && line.TrimStart().StartsWith(';');
        }
        return Console.ReadKey(intercept: true).KeyChar == ';';
    }
}
