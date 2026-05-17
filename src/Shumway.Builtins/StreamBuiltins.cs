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

        StreamWriter? writer = mode switch
        {
            "write"  => new StreamWriter(path, append: false),
            "append" => new StreamWriter(path, append: true),
            _ => null,
        };
        if (writer is null)
            throw new PrologRuntimeException("domain_error",
                "stream_mode (Phase 1 only supports write / append)");

        Cell handle = engine.MakeForeign(writer);
        return engine.UnifyRegisterWithCell(2, handle);
    }

    public static bool Close(Engine engine)
    {
        Cell handleCell = Resolve(engine, engine.GetRegister(0));
        if (handleCell.Tag != Tag.Foreign)
            throw new PrologRuntimeException("type_error", "stream");
        var writer = engine.AsForeign<StreamWriter>(handleCell);
        if (writer is null)
            throw new PrologRuntimeException("existence_error", "stream");
        writer.Flush();
        writer.Dispose();
        return true;
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

    private static Cell Resolve(Engine engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        return engine.GetHeap(engine.Deref(c.AsHeapIndex));
    }
}
