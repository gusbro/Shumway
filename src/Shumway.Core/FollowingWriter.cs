using System.IO;
using System.Text;

namespace Shumway.Core;

/// <summary>A writer that writes, at each call, to the writer its source
/// returns then. The terminal <c>user_output</c> stream writes through one, so
/// a host that swaps the engine's <c>Out</c> after the first query still
/// receives the program's output.</summary>
public sealed class FollowingWriter : TextWriter, ILineStartAware
{
    private readonly Func<TextWriter> _target;

    public FollowingWriter(Func<TextWriter> target) => _target = target;

    private TextWriter Target => _target();

    public override Encoding Encoding => Target.Encoding;

    public override IFormatProvider FormatProvider => Target.FormatProvider;

    // The target's newline, not this writer's: a capture writer or a Prolog
    // file stream writes LF where the console writes the host's newline.
    public override string NewLine => Target.NewLine;

    public bool AtLineStart => Target is not ILineStartAware aware || aware.AtLineStart;

    public override void Write(char value) => Target.Write(value);

    public override void Write(char[] buffer, int index, int count) =>
        Target.Write(buffer, index, count);

    public override void Write(string? value) => Target.Write(value);

    public override void WriteLine() => Target.WriteLine();

    public override void WriteLine(string? value) => Target.WriteLine(value);

    public override void Flush() => Target.Flush();
}
