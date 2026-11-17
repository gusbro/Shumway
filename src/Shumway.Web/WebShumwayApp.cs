using System.Runtime.InteropServices.JavaScript;
using Shumway.Embedding;
using Shumway.TopLevel;
using Shumway.WebHost;

namespace Shumway.Web;

/// <summary>
/// WebShumway's engine side: the surface JavaScript calls, over the shared
/// <see cref="TopLevelSession"/> the console REPL also drives.
///
/// <para><b>Why every export returns a Task.</b> The app is built with
/// <c>WasmEnableThreads</c>, which moves the .NET runtime off the browser's UI
/// thread. JavaScript then reaches it by posting to that thread, and the runtime
/// rejects synchronous exports outright ("Cannot call synchronous C# methods") —
/// a synchronous call would have to block the UI thread waiting for a reply,
/// which is the freeze this design exists to prevent. Work that can take time
/// goes one hop further, onto a pool thread (<c>Task.Run</c>), leaving the
/// runtime thread free to receive <see cref="QueryCancel"/> while a search
/// runs.</para>
///
/// <para>Solutions are pulled one at a time (<see cref="QueryNext"/>), which is
/// what lets the UI offer "next solution" the way the REPL offers <c>;</c>.</para>
/// </summary>
internal static partial class WebShumwayApp
{
    /// <summary>The page's sessions (ADR-068). WebShumway is a host with
    /// exactly one: the engine, its thread, its input and its output live in
    /// it, and the exports below speak for it.
    ///
    /// <para>Everything that touches the engine queues on the session's
    /// thread (<see cref="OnEngine{T}"/>): an activation is single-threaded
    /// inside, and the editor asks for highlighting on every keystroke, which
    /// reads the operator table a consult is busy changing.
    /// <see cref="QueryCancel"/> is deliberately outside: it must reach a
    /// running search, and it only sets a flag the engine reads at a safe
    /// point.</para></summary>
    private static SessionHost? _host;
    private static HostSession? _only;

    /// <summary>The one session's tier settings: what jit_compile/1 chose.</summary>
    internal static readonly WasmTierSettings Tier = new();

    private static HostSession S
        => _only ?? throw new InvalidOperationException("The runtime has not started.");

    private static Task<T> OnEngine<T>(Func<T> work) => S.OnEngine(work);

    /// <summary>The managed id of the engine thread, for the diagnostics that
    /// have to prove the pinning is real.</summary>
    internal static int EngineThreadId => S.EngineThreadId;

    /// <summary>The id of the thread a piece of engine work actually ran on --
    /// read from inside the queue, which is the only place that can answer
    /// honestly.</summary>
    internal static Task<int> OnEngineThreadId()
        => OnEngine(() => Environment.CurrentManagedThreadId);

    /// <summary>The function-table length as the engine thread sees it: with
    /// pinning, modules accumulate in one table.</summary>
    internal static Task<int> OnEngineTableLength()
        => OnEngine(TableLengthHere);

    /// <summary>The one session's side of the page.</summary>
    private sealed class Page : ISessionPage
    {
        public void Write(string text) => WriteToPage(text);
        public void WriteError(string text) => WriteErrorToPage(text);
        public void AskForInput() => WebShumwayApp.AskForInput();
    }

    /// <summary>Reply prefixes from <see cref="QueryNext"/>. One character, so a
    /// search that steps solution by solution crosses to JavaScript cheaply.</summary>
    private const char TagSolution = 's';   // a solution; more may follow
    private const char TagLast = 'l';       // the last solution
    private const char TagFailed = 'f';     // no (more) solutions
    private const char TagError = 'e';      // the query raised
    private const char TagAborted = 'a';    // abort/0 ended the query

    /// <summary>Appends engine output to the page. Prolog writes reach this through
    /// the session's <see cref="PageOutput"/>, which the engine holds as its
    /// output stream.</summary>
    [JSImport("ui.write", "main.js")]
    internal static partial void WriteToPage(string text);

    /// <summary>Appends a diagnostic to the page, marked as one.</summary>
    [JSImport("ui.writeError", "main.js")]
    internal static partial void WriteErrorToPage(string text);

    /// <summary>Appends one of the engine's own asides: what it compiled, what
    /// it restored. Neither program output nor a diagnostic -- the first
    /// competes with what the program prints, the second says something went
    /// wrong.</summary>
    [JSImport("ui.writeNote", "main.js")]
    internal static partial void WriteNoteToPage(string text);

    /// <summary>The runtime thread's context, captured while we are on it.
    /// JavaScript interop is thread-affine: <see cref="WriteToPage"/> may only be
    /// called from the thread that owns the JavaScript side. The search does not
    /// run there, so output made during a query is posted back through this.</summary>
    private static SynchronizationContext? _jsThread;

    private static void Main()
    {
        // A Main is required to start the runtime; the app itself is driven from
        // JavaScript through the exports below. This runs on the runtime thread,
        // which is the only place its context can be captured.
        _jsThread = SynchronizationContext.Current;
        _host = new SessionHost(_jsThread, maxSessions: 1);
        _only = _host.Create("main", new Page(), _ => BootEngine(Tier));
        // A pause requested for a run that just ended must not ambush the next.
        _only.RunEnded += () => Volatile.Write(ref _breakNowRequested, 0);

        // Standard output and error have no home in a browser — they go to the
        // developer console, where a user never looks. Anything written to them
        // (the runtime's own report of a failure it could not hand back, above
        // all) belongs in the transcript: this is a Prolog top level, its users
        // are programmers, and a page that fails silently is worse than one that
        // fails loudly.
        Console.SetOut(_only.Output.Writer);
        Console.SetError(_only.Output.ErrorWriter);

        // browser-wasm has no Brotli codec, and a bundle is compressed with it
        // by default. Reading was never affected (the format says whether a
        // bundle is compressed); writing one is, and compiling a library writes
        // one. Set here rather than at the call site: nothing this build
        // produces can be compressed.
        BundleFormat.DisableCompression = true;

        // Intern the whole builtin block here, while this is provably the
        // only thread (exports are not callable until Main returns). Boot()
        // runs on a pool thread concurrently with page exports, and a stray
        // intern from, say, an early highlight landing mid-registration would
        // give atom/functor ids a per-boot shuffle.
        Shumway.Builtins.StandardBuiltins.EnsureRegistered();
    }

    /// <summary>Creates the engine. Returns a short description of what booted,
    /// or a message starting with "error:" if it could not.</summary>
    [JSExport]
    internal static Task<string> Boot()
        => OnEngine(() =>
        {
            try
            {
                StartEngine();
                // Same version the desktop banner and version_data report.
                return Tier0Only
                    ? $"Shumway Prolog {PrologEngine.VersionString} ready ({TierNow()})."
                    : $"Shumway Prolog {PrologEngine.VersionString} ready.";
            }
            catch (Exception ex)
            {
                return "error: " + ex.Message;
            }
        });

    /// <summary>Throws away the engine and starts another. Switching workspace
    /// offers this because a workspace is a project: carrying the last one's
    /// consulted predicates into it would let a query answer from a program the
    /// user is no longer looking at.</summary>
    [JSExport]
    internal static Task<string?> EngineReset()
        => OnEngine(() =>
        {
            try
            {
                // Whatever was open goes with the engine being discarded,
                // a goal blocked on input included: the session sees to it.
                StartEngine();
                return (string?)null;
            }
            catch (Exception ex) { return "error: " + ex.Message; }
        });

    /// <summary>What runs the user's goals now, for the page's notes: the
    /// wasm tier when it is on, the interpreter otherwise, and always the
    /// interpreter in debug mode, where everything consulted is compiled for
    /// the debugger.</summary>
    [JSExport]
    internal static Task<string> TierName() => OnEngine(TierNow);

    private static string TierNow()
        => _debugMode ? "Tier-0 interpreter" : TierOf(S.TopLevel?.Engine, Tier);

    private static string TierOf(PrologEngine? engine, WasmTierSettings tier)
    {
        if (tier.Disabled || !Shumway.Core.RuntimeCaps.SupportsWasmCodegen
            || engine?.IlPromotion.Wasm is not { Enabled: true })
            return "Tier-0 interpreter";
        return tier.Cps
            ? "Tier-1 WebAssembly, continuation functions"
            : "Tier-1 WebAssembly";
    }

    // Set by DebugEnable on the engine it starts; any other start clears it.
    private static bool _debugMode;

    private static void StartEngine()
    {
        _debugMode = false;
        // Relative paths in Prolog (consult('lists.pl'), open/4) resolve
        // against the active workspace, so a program means what it says. The
        // directory is the session's engine's, not the process's (ADR-071).
        EnsureWorkspace();
        // Output, warnings, user_error, input: the session wires them.
        S.StartEngine();
        // Libraries are global: every engine, including one started by switching
        // workspace, knows them.
        RegisterLibraries();
    }

    /// <summary>Loads the editor's buffer. Returns null on success, or the error
    /// text. RE-consults: the predicates the buffer defines are replaced, so
    /// pressing the button twice does not define everything twice.
    ///
    /// <para>On a pool thread like the search: compiling a large program is not
    /// instantaneous either, and the page must stay drawable while it
    /// happens.</para></summary>
    [JSExport]
    internal static Task<string?> ConsultBuffer(string source, string dialect)
        => OnEngine(() =>
        {
            // Any query still open is over. It was asked of the program as it
            // was, and that program is about to change; holding its cursor open
            // across the change would let the user ask for the next solution of
            // a search whose clauses no longer exist. The gate guarantees no
            // search is mid-step here, so ending it is safe.
            EndRun();
            try
            {
                // A buffer opened from a library is that library's source, and
                // it means what its own system says it means: Scryer's
                // double_quotes is not SWI's is not ISO's. Reading it as ISO
                // gets it wrong — and quietly, since most of a file parses
                // either way.
                TopLevelSession top = S.TopLevel!;
                top.Engine.WithLibraryDialect(dialect, () =>
                {
                    top.ReconsultBuffer(source);
                    return true;
                });
                return (string?)null;
            }
            catch (Exception ex) { return Describe(ex); }
        });

    /// <summary>Begins a query. Returns null when it started, or the error text —
    /// a syntax error surfaces here, because the engine parses before it runs.</summary>
    [JSExport]
    internal static Task<string?> QueryStart(string queryText)
    {
        S.CancelRun();
        return OnEngine(() =>
        {
            try
            {
                S.StartQuery(queryText);
                return (string?)null;
            }
            catch (Exception ex) { return Describe(ex); }
        });
    }

    /// <summary>Takes the next solution. The reply is one tag character followed by
    /// the text: see TagSolution / TagLast / TagFailed / TagError.
    ///
    /// <para>Runs the search on a pool thread and hands JavaScript a promise. The
    /// search is synchronous — it blocks whatever thread it is on until it has an
    /// answer — so the one thing that must not happen is for that thread to be the
    /// one drawing the page. Off the UI thread, the page keeps responding and
    /// <see cref="QueryCancel"/> can actually reach the engine while it is
    /// working.</para></summary>
    [JSExport]
    internal static Task<string> QueryNext(int width)
        => OnEngine(() =>
        {
            QueryStep step = S.Next(width <= 20 ? 80 : width);
            return step.Kind switch
            {
                QueryStepKind.Solution => TagSolution + step.Text,
                QueryStepKind.Last => TagLast + step.Text,
                QueryStepKind.Failed => TagFailed.ToString(),
                QueryStepKind.Aborted => TagAborted.ToString(),
                _ => TagError + step.Text,
            };
        });

    /// <summary>Abandons the running query, if any. Called from the UI thread
    /// while the search may be running on a pool thread: it only sets the
    /// cancellation token, which the engine observes at its next safe point, so
    /// it is prompt rather than instantaneous. Disposing is left to whoever
    /// finishes the run, or the token would be pulled out from under it.</summary>
    [JSExport]
    internal static Task<bool> QueryCancel()
    {
        // The abandoned query's unpainted output goes with it.
        bool wasRunning = S.Cancel();
        // A search stopped at a breakpoint is blocked, not running: the token
        // alone would never be observed. Wake it so it can see the cancel.
        TryReleaseStop("continue");
        // Task<bool> rather than a bare Task: a non-generic Task is not
        // marshalable, and a synchronous void is not callable under threads.
        return Task.FromResult(wasRunning);
    }

    /// <summary>Predicate names starting with <paramref name="prefix"/>, for the
    /// editor's completion. Newline-separated: a plain string crosses to
    /// JavaScript far more cheaply than an array of them.</summary>
    [JSExport]
    internal static Task<string> Complete(string prefix)
        => OnEngine(() =>
            S.TopLevel is { } top ? string.Join('\n', top.Complete(prefix)) : "");

    /// <summary>Highlighting for the editor: flat triples
    /// <c>start,length,kind,…</c> comma-separated. <c>kind</c> indexes
    /// <see cref="SpanKind"/>; the spans cover the text exactly and in order, so
    /// the renderer can emit them one after another.
    ///
    /// <para>A string rather than an <c>int[]</c> because an array inside a Task
    /// is not marshalable, and under threads every export is a Task. The parse on
    /// the JavaScript side is cheap next to re-rendering the overlay, which is
    /// what this call is for.</para>
    ///
    /// <para>Uses the ENGINE'S lexer and the live operator table, so the editor
    /// agrees with the reader — including operators the consulted program
    /// declared itself.</para></summary>
    [JSExport]
    internal static Task<string> Highlight(string source)
        => OnEngineOrParked(() =>
        {
            // Reads the live operator table, which a consult mutates — hence the
            // gate. It also means highlighting waits behind a running search;
            // the page's editor draws its text without waiting for the colours.
            var spans = SyntaxHighlighter.Highlight(source, S.TopLevel?.Engine.Operators);
            var sb = new System.Text.StringBuilder(spans.Count * 12);
            for (int i = 0; i < spans.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(spans[i].Start).Append(',')
                  .Append(spans[i].Length).Append(',')
                  .Append((int)spans[i].Kind);
            }
            return sb.ToString();
        });

    /// <summary>Every documented predicate, as JSON: <c>[{category, name, arity,
    /// template, summary}]</c>. The page turns it into a searchable reference —
    /// the same metadata that generates <c>docs/guide/predicates.md</c>, so what
    /// the browser shows cannot drift from what the engine documents.</summary>
    [JSExport]
    internal static Task<string> PredicateReference()
        => OnEngine(() =>
        {
            var json = new MemoryStream();
            using (var w = new System.Text.Json.Utf8JsonWriter(json))
            {
                w.WriteStartArray();
                foreach (var entry in PredicateDoc.Entries())
                {
                    w.WriteStartObject();
                    w.WriteString("category", entry.Category);
                    w.WriteString("name", entry.Name);
                    w.WriteNumber("arity", entry.Arity);
                    w.WriteString("template", entry.Template);
                    w.WriteString("summary", entry.Summary);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            return System.Text.Encoding.UTF8.GetString(json.ToArray());
        });

    /// <summary>The <see cref="SpanKind"/> names, in ordinal order, so the page
    /// can turn a kind index into a CSS class without hard-coding the enum.</summary>
    [JSExport]
    internal static Task<string> HighlightKinds()
        => Task.FromResult(
            string.Join(',', Enum.GetNames<SpanKind>().Select(n => n.ToLowerInvariant())));

    private static void EndRun() => S.EndRun();

    /// <summary>The full diagnostic — the error plus the engine's call stack with
    /// source positions — as the REPL shows it, newline-separated for the page.</summary>
    private static string Describe(Exception ex) => S.Describe(ex);
}
