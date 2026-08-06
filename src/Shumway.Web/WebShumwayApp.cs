using System.Runtime.InteropServices.JavaScript;
using Shumway.Embedding;
using Shumway.TopLevel;

namespace Shumway.Web;

/// <summary>
/// WebShumway's engine side: the surface JavaScript calls, over the shared
/// <see cref="TopLevelSession"/> the console REPL also drives.
///
/// <para><b>Why these are synchronous.</b> The plan is for the engine to move to
/// a Web Worker so a long search cannot freeze the tab. That is a change of
/// TRANSPORT, not of contract: the async seam lives in <c>main.js</c>, which
/// presents an async facade to the UI. Today it resolves these calls directly;
/// under a Worker it will postMessage instead, and no UI code changes. Making
/// these return Task today would only wrap a synchronous result in a promise —
/// the appearance of async without the property that matters.</para>
///
/// <para>Solutions are PULLED one at a time (<see cref="QueryNext"/>), which is
/// what lets the UI offer "next solution" the way the REPL offers <c>;</c>.</para>
/// </summary>
internal static partial class WebShumwayApp
{
    private static TopLevelSession? _session;
    private static QueryRun? _run;

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

    private static void Main()
    {
        // A Main is required to start the runtime; the app itself is driven from
        // JavaScript through the exports below.
    }

    /// <summary>Creates the engine. Returns a short description of what booted,
    /// or a message starting with "error:" if it could not.</summary>
    [JSExport]
    internal static string Boot()
    {
        try
        {
            // Out must be set BEFORE the first query: query setup builds the stream
            // registry, and user_output keeps whatever writer it was handed then.
            PrologEngine engine = BootEngine();
            engine.Out = new PageWriter();
            _session = new TopLevelSession(engine);
            return Tier0Only
                ? "Shumway ready (Tier-0 interpreter)."
                : "Shumway ready.";
        }
        catch (Exception ex)
        {
            return "error: " + ex.Message;
        }
    }

    /// <summary>Loads Prolog source. Returns null on success, or the error text.</summary>
    [JSExport]
    internal static string? Consult(string source)
    {
        try
        {
            _session!.Consult(source);
            return null;
        }
        catch (Exception ex) { return Describe(ex); }
    }

    /// <summary>Begins a query. Returns null when it started, or the error text —
    /// a syntax error surfaces here, because the engine parses before it runs.</summary>
    [JSExport]
    internal static string? QueryStart(string queryText)
    {
        QueryCancel();
        try
        {
            _run = _session!.StartQuery(queryText);
            return null;
        }
        catch (Exception ex) { return Describe(ex); }
    }

    /// <summary>Takes the next solution. The reply is one tag character followed by
    /// the text: see TagSolution / TagLast / TagFailed / TagError.</summary>
    [JSExport]
    internal static string QueryNext(int width)
    {
        if (_run is null) return TagFailed.ToString();
        try
        {
            if (!_run.MoveNext()) { EndRun(); return TagFailed.ToString(); }
            string text = _run.Format(width <= 20 ? 80 : width);
            if (_run.IsLast) { EndRun(); return TagLast + text; }
            return TagSolution + text;
        }
        catch (Exception ex)
        {
            EndRun();
            return TagError + Describe(ex);
        }
    }

    /// <summary>Abandons the running query, if any. The engine stops at its next
    /// safe point, so this is prompt rather than instantaneous.</summary>
    [JSExport]
    internal static void QueryCancel()
    {
        _run?.Cancel();
        EndRun();
    }

    /// <summary>Predicate names starting with <paramref name="prefix"/>, for the
    /// editor's completion. Newline-separated: a plain string crosses to
    /// JavaScript far more cheaply than an array of them.</summary>
    [JSExport]
    internal static string Complete(string prefix)
        => _session is null ? "" : string.Join('\n', _session.Complete(prefix));

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
    /// a program that writes as it searches should be watchable while it runs.</summary>
    private sealed class PageWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value) => WriteToPage(value.ToString());
        public override void Write(string? value)
        {
            if (!string.IsNullOrEmpty(value)) WriteToPage(value);
        }
        public override void WriteLine(string? value) => Write((value ?? "") + "\n");
    }
}
