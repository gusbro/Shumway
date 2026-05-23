using System.IO;

namespace Shumway.Core;

/// <summary>
/// A registered Prolog stream. Wraps either a <see cref="TextReader"/>
/// or a <see cref="TextWriter"/> (never both) along with the metadata
/// ISO §8.11 wants reflective access to: mode, alias, source filename
/// (when applicable), and a per-handle id used both for the
/// <c>Foreign</c> cell payload and for ordered enumeration via
/// <c>current_stream/3</c>.
///
/// <para>Stream handles live in <see cref="StreamRegistry"/> on the
/// hosting <see cref="Shumway.Embedding.PrologEngine"/>. The two
/// terminal-default handles — <c>user_input</c> and <c>user_output</c>
/// — are always present; <c>open/3</c> registers a new handle and
/// <c>close/1</c> deregisters it.</para>
/// </summary>
public sealed class StreamHandle
{
    public int Id { get; }

    /// <summary>The underlying reader, or null when this is a writer
    /// handle.</summary>
    public TextReader? Reader { get; }

    /// <summary>The underlying writer, or null when this is a reader
    /// handle.</summary>
    public TextWriter? Writer { get; }

    /// <summary>The mode this stream was opened in — <c>read</c>,
    /// <c>write</c>, or <c>append</c>.</summary>
    public string Mode { get; }

    /// <summary>The path passed to <c>open/3</c> if this is a file
    /// stream; null for the user-terminal defaults.</summary>
    public string? Filename { get; }

    /// <summary>The optional alias set via <c>open/4</c>'s
    /// <c>alias(Name)</c> option. A handle can be referred to by its
    /// alias atom anywhere a stream is required.</summary>
    public string? Alias { get; internal set; }

    /// <summary>True once <c>close/1</c> has run; the handle stays
    /// in the registry briefly so an inadvertent second-close can
    /// report <c>existence_error</c> rather than crashing.</summary>
    public bool Closed { get; internal set; }

    public bool IsReader => Reader is not null;
    public bool IsWriter => Writer is not null;

    public StreamHandle(int id, TextReader reader, string mode, string? filename = null, string? alias = null)
    {
        Id = id;
        Reader = reader;
        Mode = mode;
        Filename = filename;
        Alias = alias;
    }

    public StreamHandle(int id, TextWriter writer, string mode, string? filename = null, string? alias = null)
    {
        Id = id;
        Writer = writer;
        Mode = mode;
        Filename = filename;
        Alias = alias;
    }
}
