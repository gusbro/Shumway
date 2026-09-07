namespace Shumway.Embedding;

/// <summary>The sink <c>with_output_to/2</c> captures into, with a ceiling.
///
/// <para>A goal whose output the caller means to hold as a term can write
/// more than a term can be: a non-terminating one writes without end. The
/// capture stops at the point where the text stops being representable —
/// <see cref="Shumway.Core.Cell.MaxPstrLength"/> code units — rather than
/// growing until the process cannot (issue #112, where the giant text
/// reached the user as the cell encoding's own .NET range check).</para></summary>
internal sealed class BoundedCaptureWriter : System.IO.StringWriter
{
    private readonly int _limit;
    private readonly bool _truncate;

    /// <param name="truncate">What reaching the ceiling means. A capture the
    /// PROGRAM asked for refuses (false) with
    /// <c>resource_error(text_length)</c>: it wanted the text, and there is
    /// no text to give it. A capture a HOST asked for with a ceiling of its
    /// own (true) keeps the prefix and drops the rest — it is comparing the
    /// text against something it already has, so past its own longest
    /// pattern the answer cannot change. Truncating also leaves the goal
    /// RUNNING, which is how a looping goal still reaches the time limit
    /// that decides it loops.</param>
    public BoundedCaptureWriter(int limit, bool truncate = false)
    {
        _limit = limit;
        _truncate = truncate;
        NewLine = "\n";
    }

    /// <summary>False once the ceiling has swallowed something.</summary>
    public bool Complete { get; private set; } = true;

    /// <summary>How much of <paramref name="incoming"/> may still be
    /// written: all of it, what fits, or none.</summary>
    private int Allow(int incoming)
    {
        long room = (long)_limit - GetStringBuilder().Length;
        if (incoming <= room) return incoming;
        if (!_truncate)
            throw new Shumway.Core.PrologRuntimeException(
                "resource_error", "text_length");
        Complete = false;
        return room > 0 ? (int)room : 0;
    }

    public override void Write(char value)
    {
        if (Allow(1) == 1) base.Write(value);
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        int n = Allow(value.Length);
        if (n == value.Length) base.Write(value);
        else if (n > 0) base.Write(value.AsSpan(0, n));
    }

    public override void Write(char[] buffer, int index, int count)
    {
        int n = Allow(count);
        if (n > 0) base.Write(buffer, index, n);
    }

    public override void Write(System.ReadOnlySpan<char> buffer)
    {
        int n = Allow(buffer.Length);
        if (n > 0) base.Write(buffer[..n]);
    }
}
