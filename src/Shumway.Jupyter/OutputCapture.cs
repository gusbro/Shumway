using System.Text;

namespace Shumway.Jupyter;

/// <summary>What a session writes to standard output and standard error,
/// kept until the server takes it for the response to a cell.</summary>
internal sealed class OutputCapture : TextWriter
{
    // Not only the engine thread writes: a warning can come from a thread
    // that compiles in the background.
    private readonly object _gate = new();
    private readonly StringBuilder _text = new();

    // A line the engine ends itself (the tracer's) ends as a line Prolog
    // code ends, whatever the platform.
    public OutputCapture() => NewLine = "\n";

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_gate) _text.Append(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (_gate) _text.Append(buffer, index, count);
    }

    public override void Write(string? value)
    {
        lock (_gate) _text.Append(value);
    }

    /// <summary>The text written since the last call.</summary>
    public string Take()
    {
        lock (_gate)
        {
            string text = _text.ToString();
            _text.Clear();
            return text;
        }
    }
}
