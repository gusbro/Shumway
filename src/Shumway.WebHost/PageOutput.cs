using System.Text;

namespace Shumway.WebHost;

/// <summary>A session's output on its way to the page: what has been written
/// and not painted yet.
///
/// <para>A write from the engine thread cannot touch JavaScript, so it is
/// buffered and one flush is posted to the page thread. At most one flush is
/// in flight, posted on the first buffered write and not on a timer: a prompt
/// written before a read crosses on the next turn, and when the page is not
/// keeping up everything written meanwhile arrives as one string. Ordinary
/// output and diagnostics share one list, so they keep the order they were
/// written in.</para>
///
/// <para>Past <see cref="HighWater"/> the writer waits. That throttle is what
/// keeps Stop working: the page thread that paints is the one a cancel has to
/// reach, and a goal that writes without pause would otherwise fill its
/// queue.</para></summary>
public sealed class PageOutput
{
    /// <summary>How much unpainted output a search may run ahead by before it
    /// is made to wait.</summary>
    public const int HighWater = 64 * 1024;

    private readonly SynchronizationContext? _pageThread;
    private readonly ISessionPage _page;
    private readonly object _lock = new();
    private readonly List<(bool AsError, StringBuilder Text)> _segments = new();
    private readonly ManualResetEventSlim _drained = new(true);
    private bool _flushPending;
    private int _chars;

    public PageOutput(SynchronizationContext? pageThread, ISessionPage page)
    {
        _pageThread = pageThread;
        _page = page ?? throw new ArgumentNullException(nameof(page));
        Writer = new Channel(this, asError: false);
        ErrorWriter = new Channel(this, asError: true);
    }

    /// <summary>Program output.</summary>
    public TextWriter Writer { get; }

    /// <summary>Diagnostics, marked as such on the page.</summary>
    public TextWriter ErrorWriter { get; }

    public void Write(bool asError, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_pageThread is null || SynchronizationContext.Current == _pageThread)
        {
            // Already where the page can be touched. What is buffered goes
            // first, or this write would jump ahead of it.
            Flush();
            Emit(asError, text);
            return;
        }
        Buffer(asError, text);
    }

    private void Buffer(bool asError, string text)
    {
        bool schedule, overHighWater;
        lock (_lock)
        {
            if (_segments.Count > 0 && _segments[^1].AsError == asError)
                _segments[^1].Text.Append(text);
            else
                _segments.Add((asError, new StringBuilder(text)));
            _chars += text.Length;
            schedule = !_flushPending;
            _flushPending = true;
            overHighWater = _chars > HighWater;
            if (overHighWater) _drained.Reset();
        }
        if (schedule) _pageThread!.Post(static o => ((PageOutput)o!).Flush(), this);
        // A bounded wait: a throttle, never a block. If the page has stopped
        // painting (a hidden tab) the search goes on. Only ever on an engine
        // thread: the page thread took the branch in Write.
        if (overHighWater)
        {
            _drained.Wait(100);
            // The search wakes the instant the flush lands and refills, so the
            // page thread would go straight back to painting. These few
            // milliseconds per 64 KB are the gap a cancel gets through.
            Thread.Sleep(5);
        }
    }

    /// <summary>Paints everything buffered. Page thread only.</summary>
    public void Flush()
    {
        (bool AsError, string Text)[] segments;
        lock (_lock)
        {
            _flushPending = false;
            if (_segments.Count == 0) { _chars = 0; _drained.Set(); return; }
            segments = new (bool, string)[_segments.Count];
            for (int i = 0; i < _segments.Count; i++)
                segments[i] = (_segments[i].AsError, _segments[i].Text.ToString());
            _segments.Clear();
            _chars = 0;
            _drained.Set();
        }
        foreach (var (asError, text) in segments) Emit(asError, text);
    }

    /// <summary>Throws away what has not been painted: the output an abandoned
    /// query still had queued. Without it the transcript goes on painting long
    /// after Stop.</summary>
    public void Drop()
    {
        lock (_lock)
        {
            _segments.Clear();
            _chars = 0;
            // The abandoned query must not stay parked on the throttle: it has
            // to run on to a safe point to see that it was cancelled.
            _drained.Set();
        }
    }

    private void Emit(bool asError, string text)
    {
        if (asError) _page.WriteError(text); else _page.Write(text);
    }

    private sealed class Channel(PageOutput owner, bool asError) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => owner.Write(asError, value.ToString());
        public override void Write(string? value) => owner.Write(asError, value);
        public override void WriteLine(string? value) => owner.Write(asError, (value ?? "") + "\n");
    }
}
