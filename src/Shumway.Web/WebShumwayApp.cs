using System.Runtime.InteropServices.JavaScript;
using Shumway.Embedding;
using Shumway.TopLevel;

namespace Shumway.Web;

/// <summary>
/// WebShumway's engine side: the surface JavaScript calls, over the shared
/// <see cref="TopLevelSession"/> the console REPL also drives.
///
/// <para><b>Why every export returns a Task.</b> The app is built with
/// <c>WasmEnableThreads</c>, which moves the .NET runtime OFF the browser's UI
/// thread. JavaScript then reaches it by posting to that thread, and the runtime
/// rejects synchronous exports outright ("Cannot call synchronous C# methods") —
/// a synchronous call would have to block the UI thread waiting for a reply,
/// which is the freeze this design exists to prevent. Work that can take time
/// goes one hop further, onto a POOL thread (<c>Task.Run</c>), leaving the
/// runtime thread free to receive <see cref="QueryCancel"/> while a search
/// runs.</para>
///
/// <para>Solutions are PULLED one at a time (<see cref="QueryNext"/>), which is
/// what lets the UI offer "next solution" the way the REPL offers <c>;</c>.</para>
/// </summary>
internal static partial class WebShumwayApp
{
    private static TopLevelSession? _session;
    private static QueryRun? _run;

    /// <summary>Serializes everything that touches the engine.
    ///
    /// <para>An activation is single-threaded internally — the engine is not a
    /// thing two threads may be inside at once. With the search on a pool thread
    /// there ARE two threads in play, and the editor asks for highlighting on
    /// every keystroke, which reads the live operator table a consult is busy
    /// mutating. So engine work queues rather than overlaps: at most one of
    /// these bodies runs at a time, whichever thread called it.</para>
    ///
    /// <para><see cref="QueryCancel"/> is deliberately outside — it must reach a
    /// running search, and it only sets a flag the engine reads at a safe
    /// point.</para></summary>
    private static readonly SemaphoreSlim _engineGate = new(1, 1);

    private static async Task<T> OnEngine<T>(Func<T> work)
    {
        await _engineGate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(work).ConfigureAwait(false); }
        finally { _engineGate.Release(); }
    }

    /// <summary>Reply prefixes from <see cref="QueryNext"/>. One character, so a
    /// search that steps solution by solution crosses to JavaScript cheaply.</summary>
    private const char TagSolution = 's';   // a solution; more may follow
    private const char TagLast = 'l';       // the last solution
    private const char TagFailed = 'f';     // no (more) solutions
    private const char TagError = 'e';      // the query raised

    /// <summary>Appends engine output to the page. Prolog writes reach this through
    /// <see cref="PageWriter"/>, which the engine holds as its output stream.</summary>
    [JSImport("ui.write", "main.js")]
    internal static partial void WriteToPage(string text);

    /// <summary>Appends a diagnostic to the page, marked as one.</summary>
    [JSImport("ui.writeError", "main.js")]
    internal static partial void WriteErrorToPage(string text);

    /// <summary>The runtime thread's context, captured while we are on it.
    /// JavaScript interop is thread-affine: <see cref="WriteToPage"/> may only be
    /// called from the thread that owns the JavaScript side. The search does not
    /// run there, so output made during a query is posted back through this.</summary>
    private static SynchronizationContext? _jsThread;

    private static void Main()
    {
        // A Main is required to start the runtime; the app itself is driven from
        // JavaScript through the exports below. This runs ON the runtime thread,
        // which is the only place its context can be captured.
        _jsThread = SynchronizationContext.Current;

        // Standard output and error have no home in a browser — they go to the
        // developer console, where a user never looks. Anything written to them
        // (the runtime's own report of a failure it could not hand back, above
        // all) belongs in the transcript: this is a Prolog top level, its users
        // are programmers, and a page that fails silently is worse than one that
        // fails loudly.
        Console.SetOut(new PageWriter());
        Console.SetError(new PageWriter(asError: true));

        // browser-wasm has no Brotli codec, and a bundle is compressed with it
        // by default. Reading was never affected (the format says whether a
        // bundle is compressed); WRITING one is, and compiling a library writes
        // one. Set here rather than at the call site: nothing this build
        // produces can be compressed.
        BundleFormat.DisableCompression = true;
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
                return Tier0Only
                    ? "Shumway ready (Tier-0 interpreter)."
                    : "Shumway ready.";
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
                // Whatever was open belonged to the engine being discarded —
                // including a goal blocked on input, which would otherwise wait
                // for a page that has moved on.
                EndRun();
                _pageInput.SupplyEof();
                StartEngine();
                return (string?)null;
            }
            catch (Exception ex) { return "error: " + ex.Message; }
        });

    private static void StartEngine()
    {
        // Out and In must be set BEFORE the first query: query setup builds the
        // stream registry, and user_output / user_input keep whatever they were
        // handed then.
        PrologEngine engine = BootEngine();
        engine.Out = new PageWriter();
        // Load-time warnings default to standard error, which a page does not
        // have: a use_module that found nothing would load silently and leave
        // the user looking at a program missing half of itself.
        engine.Warnings = new PageWriter();
        engine.In = _pageInput;
        _pageInput.Reset();
        _session = new TopLevelSession(engine);
        // Relative paths in Prolog — consult('lists.pl'), open/4 — resolve
        // against the active workspace, so a program means what it says.
        EnsureWorkspace();
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
                _session!.Engine.WithLibraryDialect(dialect, () =>
                {
                    _session.ReconsultBuffer(source);
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
        _run?.Cancel();
        return OnEngine(() =>
        {
            // A fresh query gets a fresh input stream: an end-of-file answered
            // to the last one must not be the first thing this one reads.
            _pageInput.Reset();
            try
            {
                _run = _session!.StartQuery(queryText);
                return (string?)null;
            }
            catch (Exception ex) { return Describe(ex); }
        });
    }

    /// <summary>Takes the next solution. The reply is one tag character followed by
    /// the text: see TagSolution / TagLast / TagFailed / TagError.
    ///
    /// <para>Runs the search on a POOL THREAD and hands JavaScript a promise. The
    /// search is synchronous — it blocks whatever thread it is on until it has an
    /// answer — so the one thing that must not happen is for that thread to be the
    /// one drawing the page. Off the UI thread, the page keeps responding and
    /// <see cref="QueryCancel"/> can actually reach the engine while it is
    /// working.</para></summary>
    [JSExport]
    internal static Task<string> QueryNext(int width)
        => OnEngine(() =>
        {
            if (_run is null) return TagFailed.ToString();
            try
            {
                if (!_run.MoveNext()) { EndRun(); return TagFailed.ToString(); }
                string text = _run.Format(width <= 20 ? 80 : width);
                if (_run.IsLast) { EndRun(); return TagLast + text; }
                return TagSolution + text;
            }
            catch (OperationCanceledException)
            {
                EndRun();
                return TagFailed.ToString();
            }
            catch (Exception ex)
            {
                EndRun();
                return TagError + Describe(ex);
            }
        });

    /// <summary>Abandons the running query, if any. Called from the UI thread
    /// WHILE the search may be running on a pool thread: it only sets the
    /// cancellation token, which the engine observes at its next safe point, so
    /// it is prompt rather than instantaneous. Disposing is left to whoever
    /// finishes the run, or the token would be pulled out from under it.</summary>
    [JSExport]
    internal static Task<bool> QueryCancel()
    {
        bool wasRunning = _run is not null;
        _run?.Cancel();
        // A search stopped at a breakpoint is BLOCKED, not running: the token
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
            _session is null ? "" : string.Join('\n', _session.Complete(prefix)));

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
    /// <para>Uses the ENGINE'S lexer and the LIVE operator table, so the editor
    /// agrees with the reader — including operators the consulted program
    /// declared itself.</para></summary>
    [JSExport]
    internal static Task<string> Highlight(string source)
        => OnEngineOrParked(() =>
        {
            // Reads the LIVE operator table, which a consult mutates — hence the
            // gate. It also means highlighting waits behind a running search;
            // the page's editor draws its text without waiting for the colours.
            var spans = SyntaxHighlighter.Highlight(source, _session?.Engine.Operators);
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

    private static void EndRun()
    {
        _run?.Dispose();
        _run = null;
    }

    /// <summary>The full diagnostic — the error plus the engine's call stack with
    /// source positions — as the REPL shows it, newline-separated for the page.</summary>
    private static string Describe(Exception ex)
        => _session is null
            ? ex.Message
            : string.Join('\n', ErrorRendering.Describe(_session.Engine, ex));

    /// <summary>The engine's output stream, forwarding to the page. Buffers nothing:
    /// a program that writes as it searches should be watchable while it runs.
    ///
    /// <para>A write from the search thread cannot touch JavaScript directly, so it
    /// is POSTED to the runtime thread. Posts on one context run in the order they
    /// were made, which is the property that matters: a program's output must
    /// reach the page in the order it was written.</para></summary>
    private sealed class PageWriter(bool asError = false) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value) => Write(value.ToString());
        public override void WriteLine(string? value) => Write((value ?? "") + "\n");

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (_jsThread is null || SynchronizationContext.Current == _jsThread)
                Emit(value);
            else
                _jsThread.Post(s => Emit((string)s!), value);
        }

        private void Emit(string text)
        {
            if (asError) WriteErrorToPage(text); else WriteToPage(text);
        }
    }
}
