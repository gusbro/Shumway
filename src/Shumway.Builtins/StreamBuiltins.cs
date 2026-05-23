using System.IO;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// File-based text streams. Phase 1 supports writing only —
/// <c>open(Path, write|append, Stream)</c> opens a file in the chosen
/// mode and stashes the underlying <see cref="StreamWriter"/> in the
/// engine's foreign-object table; the handle that comes back is a
/// regular FOREIGN cell. Stream-aware write helpers and <c>close/1</c>
/// round out the toolkit. Reading streams (<c>read_term/2</c>) is
/// deferred — it needs a tokenising parser hook the Phase-1 lexer
/// doesn't expose.
/// </summary>
public static class StreamBuiltins
{
    public static bool Open(Engine engine)
    {
        Cell pathCell = Resolve(engine, engine.GetRegister(0));
        Cell modeCell = Resolve(engine, engine.GetRegister(1));
        if (pathCell.Tag != Tag.Atom)
            throw new PrologRuntimeException("type_error", "atom");
        if (modeCell.Tag != Tag.Atom)
            throw new PrologRuntimeException("type_error", "atom");

        string path = AtomTable.GetById(pathCell.AsAtomId)?.Name ?? "";
        string mode = AtomTable.GetById(modeCell.AsAtomId)?.Name ?? "";

        object? streamObj = mode switch
        {
            "write"  => new StreamWriter(path, append: false),
            "append" => new StreamWriter(path, append: true),
            "read"   => new StreamReader(path),
            _ => null,
        };
        if (streamObj is null)
            throw new PrologRuntimeException("domain_error",
                "stream_mode (Phase 1 supports write / append / read)");

        Cell handle = engine.MakeForeign(streamObj);
        return engine.UnifyRegisterWithCell(2, handle);
    }

    public static bool Close(Engine engine)
    {
        Cell handleCell = Resolve(engine, engine.GetRegister(0));
        if (handleCell.Tag != Tag.Foreign)
            throw new PrologRuntimeException("type_error", "stream");
        object? streamObj = engine.AsForeign(handleCell);
        switch (streamObj)
        {
            case StreamWriter w: w.Flush(); w.Dispose(); return true;
            case StreamReader r: r.Dispose(); return true;
            case null:
                throw new PrologRuntimeException("existence_error", "stream");
            default:
                throw new PrologRuntimeException("type_error", "stream");
        }
    }

    /// <summary><c>write(Stream, Term)</c> — renders Term to the
    /// stream's writer in canonical form (matches write/1 over
    /// <see cref="Engine.Out"/>).</summary>
    public static bool WriteToStream(Engine engine)
    {
        StreamWriter writer = RequireWriter(engine, engine.GetRegister(0));
        TermRenderer.Render(engine, engine.GetRegister(1), writer,
            new TermRenderOptions { Operators = engine.Operators });
        return true;
    }

    /// <summary><c>nl(Stream)</c> — writes a newline to the given stream.</summary>
    public static bool NlOnStream(Engine engine)
    {
        StreamWriter writer = RequireWriter(engine, engine.GetRegister(0));
        writer.WriteLine();
        return true;
    }

    /// <summary><c>get_char(Stream, Char)</c> — reads one character from
    /// the stream and unifies the result with <c>Char</c> as a
    /// single-character atom. End of stream returns the atom
    /// <c>end_of_file</c>.</summary>
    public static bool GetChar(Engine engine)
    {
        StreamReader reader = RequireReader(engine, engine.GetRegister(0));
        int c = reader.Read();
        Cell value = c < 0
            ? Cell.Atom(AtomTable.Intern("end_of_file", permanent: true).Id)
            : Cell.Atom(AtomTable.Intern(((char)c).ToString(), permanent: false).Id);
        return engine.UnifyRegisterWithCell(1, value);
    }

    /// <summary><c>peek_char(Stream, Char)</c> — returns the next char
    /// without consuming it. EOF yields <c>end_of_file</c>.</summary>
    public static bool PeekChar(Engine engine)
    {
        StreamReader reader = RequireReader(engine, engine.GetRegister(0));
        int c = reader.Peek();
        Cell value = c < 0
            ? Cell.Atom(AtomTable.Intern("end_of_file", permanent: true).Id)
            : Cell.Atom(AtomTable.Intern(((char)c).ToString(), permanent: false).Id);
        return engine.UnifyRegisterWithCell(1, value);
    }

    /// <summary><c>current_input(Stream)</c> — ISO §8.11.1. Unifies
    /// <c>Stream</c> with a designator for the current input stream.
    /// Shumway uses the conventional atom <c>user_input</c> for the
    /// terminal-default reader.</summary>
    public static bool CurrentInput(Engine engine) =>
        engine.UnifyRegisterWithCell(0,
            Cell.Atom(AtomTable.Intern("user_input", permanent: true).Id));

    /// <summary><c>current_output(Stream)</c> — ISO §8.11.2. Conventional
    /// atom <c>user_output</c> for the terminal-default writer.</summary>
    public static bool CurrentOutput(Engine engine) =>
        engine.UnifyRegisterWithCell(0,
            Cell.Atom(AtomTable.Intern("user_output", permanent: true).Id));

    /// <summary><c>flush_output/0</c> — ISO §8.11.7. Flushes the
    /// engine's default output writer.</summary>
    public static bool FlushOutput0(Engine engine)
    {
        engine.Out.Flush();
        return true;
    }

    /// <summary><c>flush_output(Stream)</c> — ISO §8.11.7. Flushes the
    /// given stream's writer; the <c>user_output</c> atom maps to
    /// <see cref="Engine.Out"/>.</summary>
    public static bool FlushOutput1(Engine engine)
    {
        Cell h = Resolve(engine, engine.GetRegister(0));
        if (h.Tag == Tag.Atom)
        {
            string name = AtomTable.GetById(h.AsAtomId)?.Name ?? "";
            if (name == "user_output") { engine.Out.Flush(); return true; }
            throw new PrologRuntimeException("existence_error", "stream");
        }
        if (h.Tag != Tag.Foreign)
            throw new PrologRuntimeException("type_error", "stream");
        var writer = engine.AsForeign<StreamWriter>(h);
        if (writer is null)
            throw new PrologRuntimeException("existence_error", "stream");
        writer.Flush();
        return true;
    }

    /// <summary><c>at_end_of_stream(Stream)</c> — ISO §8.11.9. Succeeds
    /// when the given reader has no more bytes available. The
    /// <c>user_input</c> atom maps to a console reader that can't be
    /// peeked at without blocking, so we conservatively report
    /// "not at end" there.</summary>
    public static bool AtEndOfStream1(Engine engine)
    {
        Cell h = Resolve(engine, engine.GetRegister(0));
        if (h.Tag == Tag.Atom)
        {
            string name = AtomTable.GetById(h.AsAtomId)?.Name ?? "";
            if (name == "user_input") return false;
            throw new PrologRuntimeException("existence_error", "stream");
        }
        if (h.Tag != Tag.Foreign)
            throw new PrologRuntimeException("type_error", "stream");
        var reader = engine.AsForeign<StreamReader>(h);
        if (reader is null)
            throw new PrologRuntimeException("existence_error", "stream");
        return reader.Peek() < 0;
    }

    /// <summary><c>at_end_of_stream/0</c> — checks current_input;
    /// Shumway's <c>user_input</c> default is never reported "at end",
    /// matching the conservative behaviour of
    /// <see cref="AtEndOfStream1"/>.</summary>
    public static bool AtEndOfStream0(Engine engine) => false;

    private static StreamWriter RequireWriter(Engine engine, Cell handleCell)
    {
        Cell h = Resolve(engine, handleCell);
        if (h.Tag != Tag.Foreign)
            throw new PrologRuntimeException("type_error", "stream");
        var writer = engine.AsForeign<StreamWriter>(h);
        if (writer is null)
            throw new PrologRuntimeException("existence_error", "stream");
        return writer;
    }

    private static StreamReader RequireReader(Engine engine, Cell handleCell)
    {
        Cell h = Resolve(engine, handleCell);
        if (h.Tag != Tag.Foreign)
            throw new PrologRuntimeException("type_error", "stream");
        var reader = engine.AsForeign<StreamReader>(h);
        if (reader is null)
            throw new PrologRuntimeException("existence_error", "stream");
        return reader;
    }

    private static Cell Resolve(Engine engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        return engine.GetHeap(engine.Deref(c.AsHeapIndex));
    }
}
