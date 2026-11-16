using System.Text;

namespace Shumway.Jupyter;

/// <summary>The stream the server writes its responses to. Each complete
/// line is handed to <paramref name="deliver"/>, on the engine thread.</summary>
internal sealed class ResponsePipe(Action<string> deliver) : TextWriter
{
    private readonly StringBuilder _line = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (value == '\n')
        {
            string line = _line.ToString();
            _line.Clear();
            deliver(line);
        }
        else if (value != '\r') _line.Append(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        for (int i = 0; i < count; i++) Write(buffer[index + i]);
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        foreach (char c in value) Write(c);
    }
}
