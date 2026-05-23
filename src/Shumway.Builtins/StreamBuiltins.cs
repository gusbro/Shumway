using System.IO;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// File and terminal text streams, backed by the per-engine
/// <see cref="StreamRegistry"/> (chunk 140). Foreign cells holding a
/// <see cref="StreamHandle"/> are the canonical stream-arg form; the
/// conventional atom <c>user_input</c> / <c>user_output</c> and any
/// user-defined alias also resolve to a registered handle.
/// </summary>
public static class StreamBuiltins
{
    // ---------- Stream-arg resolution ----------

    /// <summary>Resolves a stream argument cell to its
    /// <see cref="StreamHandle"/>. Accepts a Foreign cell holding the
    /// handle directly, or an atom matching a registered alias.
    /// Throws ISO-shaped errors for the failure modes ISO §8.11
    /// specifies.</summary>
    public static StreamHandle ResolveStream(Engine engine, Cell cell)
    {
        Cell d = Resolve(engine, cell);
        if (d.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");

        StreamRegistry registry = engine.Streams
            ?? throw new InvalidOperationException("Engine has no stream registry.");

        if (d.Tag == Tag.Foreign)
        {
            var h = engine.AsForeign<StreamHandle>(d);
            if (h is null || h.Closed)
                throw new PrologRuntimeException("existence_error", "stream");
            return h;
        }
        if (d.Tag == Tag.Atom)
        {
            string name = AtomTable.GetById(d.AsAtomId)?.Name ?? "";
            var h = registry.GetByAlias(name);
            if (h is null || h.Closed)
                throw new PrologRuntimeException("existence_error", "stream");
            return h;
        }
        // ISO §8.11.6.3.b: a bound non-stream-non-alias is
        // type_error(stream_or_alias, _).
        throw new PrologRuntimeException("type_error", "stream_or_alias");
    }

    private static StreamHandle ResolveReader(Engine engine, Cell cell)
    {
        var h = ResolveStream(engine, cell);
        if (!h.IsReader)
            throw new PrologRuntimeException("permission_error", "input,stream");
        return h;
    }

    private static StreamHandle ResolveWriter(Engine engine, Cell cell)
    {
        var h = ResolveStream(engine, cell);
        if (!h.IsWriter)
            throw new PrologRuntimeException("permission_error", "output,stream");
        return h;
    }

    // ---------- open/3, close/1 ----------

    public static bool Open(Engine engine)
    {
        Cell pathCell = Resolve(engine, engine.GetRegister(0));
        Cell modeCell = Resolve(engine, engine.GetRegister(1));
        if (pathCell.Tag == Tag.Ref || modeCell.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        if (pathCell.Tag != Tag.Atom)
            throw new PrologRuntimeException("type_error", "atom");
        if (modeCell.Tag != Tag.Atom)
            throw new PrologRuntimeException("type_error", "atom");

        string path = AtomTable.GetById(pathCell.AsAtomId)?.Name ?? "";
        string mode = AtomTable.GetById(modeCell.AsAtomId)?.Name ?? "";

        StreamRegistry registry = engine.Streams
            ?? throw new InvalidOperationException("Engine has no stream registry.");

        int id = registry.NextId();
        StreamHandle handle;
        try
        {
            handle = mode switch
            {
                "write"  => new StreamHandle(id, new StreamWriter(path, append: false), "write", path),
                "append" => new StreamHandle(id, new StreamWriter(path, append: true), "append", path),
                "read"   => new StreamHandle(id, new StreamReader(path), "read", path),
                _ => throw new PrologRuntimeException("domain_error",
                    "stream_mode (Phase 1 supports write / append / read)"),
            };
        }
        catch (FileNotFoundException)
        {
            throw new PrologRuntimeException("existence_error", "source_sink");
        }
        catch (DirectoryNotFoundException)
        {
            throw new PrologRuntimeException("existence_error", "source_sink");
        }
        catch (IOException ex)
        {
            throw new PrologRuntimeException("system_error", ex.Message);
        }

        registry.Add(handle);
        Cell foreignCell = engine.MakeForeign(handle);
        return engine.UnifyRegisterWithCell(2, foreignCell);
    }

    /// <summary><c>open(+File, +Mode, -Stream, +Options)</c> — ISO §8.11.5.
    /// The four-argument form takes an options list; chunk 140c
    /// recognises <c>alias(Name)</c> (registers the stream under a
    /// user-chosen atom), <c>type(text|binary)</c> (text is the only
    /// mode actually supported; binary is accepted and ignored) and
    /// <c>eof_action(error|eof_code|reset)</c> (stored but currently
    /// honoured only via the default end_of_file handling). Any
    /// other option raises <c>domain_error(stream_option, _)</c>.
    /// </summary>
    public static bool OpenWithOptions(Engine engine)
    {
        Cell pathCell = Resolve(engine, engine.GetRegister(0));
        Cell modeCell = Resolve(engine, engine.GetRegister(1));
        Cell optsCell = Resolve(engine, engine.GetRegister(3));
        if (pathCell.Tag == Tag.Ref || modeCell.Tag == Tag.Ref || optsCell.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        if (pathCell.Tag != Tag.Atom)
            throw new PrologRuntimeException("type_error", "atom");
        if (modeCell.Tag != Tag.Atom)
            throw new PrologRuntimeException("type_error", "atom");

        string path = AtomTable.GetById(pathCell.AsAtomId)?.Name ?? "";
        string mode = AtomTable.GetById(modeCell.AsAtomId)?.Name ?? "";

        // Parse the options list. Each option is a 1-arg compound;
        // anything else is a stream_option domain error.
        string? alias = null;
        Cell cur = optsCell;
        while (cur.Tag == Tag.Lis)
        {
            Cell head = Resolve(engine, engine.GetHeap(cur.AsHeapIndex));
            if (head.Tag == Tag.Ref)
                throw new PrologRuntimeException("instantiation_error");
            if (head.Tag != Tag.Str)
                throw new PrologRuntimeException("domain_error", "stream_option");

            int functorIdx = head.AsHeapIndex;
            int functorId = engine.GetHeap(functorIdx).AsFunctorId;
            var (optAtomId, optArity) = FunctorTable.Lookup(functorId);
            string optName = AtomTable.GetById(optAtomId)?.Name ?? "";
            if (optArity != 1)
                throw new PrologRuntimeException("domain_error", "stream_option");

            Cell argCell = Resolve(engine, engine.GetHeap(functorIdx + 1));
            switch (optName)
            {
                case "alias":
                    if (argCell.Tag == Tag.Ref)
                        throw new PrologRuntimeException("instantiation_error");
                    if (argCell.Tag != Tag.Atom)
                        throw new PrologRuntimeException("type_error", "atom");
                    alias = AtomTable.GetById(argCell.AsAtomId)?.Name ?? "";
                    break;
                case "type":
                    // text/binary: accept both but Shumway only does text.
                    if (argCell.Tag == Tag.Ref)
                        throw new PrologRuntimeException("instantiation_error");
                    if (argCell.Tag != Tag.Atom)
                        throw new PrologRuntimeException("domain_error", "stream_option");
                    string typeName = AtomTable.GetById(argCell.AsAtomId)?.Name ?? "";
                    if (typeName != "text" && typeName != "binary")
                        throw new PrologRuntimeException("domain_error", "stream_option");
                    break;
                case "eof_action":
                    // Recognised but not yet plumbed through the read
                    // side; reading at EOF always returns the
                    // end_of_file atom (matching eof_code).
                    break;
                case "reposition":
                    // Recognised; SeekablePosition is implicit on
                    // file streams — no plumbing needed yet.
                    break;
                default:
                    throw new PrologRuntimeException("domain_error", "stream_option");
            }
            cur = Resolve(engine, engine.GetHeap(cur.AsHeapIndex + 1));
        }
        if (cur.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        if (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId)
            throw new PrologRuntimeException("type_error", "list");

        StreamRegistry registry = engine.Streams
            ?? throw new InvalidOperationException("Engine has no stream registry.");

        // ISO permission_error(open, source_sink, alias(_)) when the
        // requested alias is already taken.
        if (alias is not null && registry.IsAliasTaken(alias))
            throw new PrologRuntimeException("permission_error", "open,source_sink");

        int id = registry.NextId();
        StreamHandle handle;
        try
        {
            handle = mode switch
            {
                "write"  => new StreamHandle(id, new StreamWriter(path, append: false), "write", path, alias),
                "append" => new StreamHandle(id, new StreamWriter(path, append: true), "append", path, alias),
                "read"   => new StreamHandle(id, new StreamReader(path), "read", path, alias),
                _ => throw new PrologRuntimeException("domain_error",
                    "stream_mode (Phase 1 supports write / append / read)"),
            };
        }
        catch (FileNotFoundException)
        {
            throw new PrologRuntimeException("existence_error", "source_sink");
        }
        catch (DirectoryNotFoundException)
        {
            throw new PrologRuntimeException("existence_error", "source_sink");
        }
        catch (IOException ex)
        {
            throw new PrologRuntimeException("system_error", ex.Message);
        }

        registry.Add(handle);
        Cell foreignCell = engine.MakeForeign(handle);
        return engine.UnifyRegisterWithCell(2, foreignCell);
    }

    public static bool Close(Engine engine)
    {
        var h = ResolveStream(engine, engine.GetRegister(0));
        if (h.Reader is not null) h.Reader.Dispose();
        if (h.Writer is not null) { h.Writer.Flush(); h.Writer.Dispose(); }
        engine.Streams!.Remove(h);
        return true;
    }

    // ---------- write/2, nl/1, get_char/2, peek_char/2 ----------

    /// <summary><c>write(Stream, Term)</c> — renders Term to the
    /// stream's writer in canonical form (matches write/1 over
    /// <see cref="Engine.Out"/>).</summary>
    public static bool WriteToStream(Engine engine)
    {
        var h = ResolveWriter(engine, engine.GetRegister(0));
        TermRenderer.Render(engine, engine.GetRegister(1), h.Writer!,
            new TermRenderOptions { Operators = engine.Operators });
        return true;
    }

    /// <summary><c>nl(Stream)</c> — writes a newline to the given stream.</summary>
    public static bool NlOnStream(Engine engine)
    {
        var h = ResolveWriter(engine, engine.GetRegister(0));
        h.Writer!.WriteLine();
        return true;
    }

    /// <summary><c>get_char(Stream, Char)</c> — reads one character from
    /// the stream and unifies the result with <c>Char</c> as a
    /// single-character atom. End of stream returns the atom
    /// <c>end_of_file</c>.</summary>
    public static bool GetChar(Engine engine)
    {
        var h = ResolveReader(engine, engine.GetRegister(0));
        int c = h.Reader!.Read();
        Cell value = c < 0
            ? Cell.Atom(AtomTable.Intern("end_of_file", permanent: true).Id)
            : Cell.Atom(AtomTable.Intern(((char)c).ToString(), permanent: false).Id);
        return engine.UnifyRegisterWithCell(1, value);
    }

    /// <summary><c>peek_char(Stream, Char)</c> — returns the next char
    /// without consuming it. EOF yields <c>end_of_file</c>.</summary>
    public static bool PeekChar(Engine engine)
    {
        var h = ResolveReader(engine, engine.GetRegister(0));
        int c = h.Reader!.Peek();
        Cell value = c < 0
            ? Cell.Atom(AtomTable.Intern("end_of_file", permanent: true).Id)
            : Cell.Atom(AtomTable.Intern(((char)c).ToString(), permanent: false).Id);
        return engine.UnifyRegisterWithCell(1, value);
    }

    // ---------- current_input / current_output / set_input / set_output ----------

    /// <summary><c>current_input(Stream)</c> — ISO §8.11.1. Unifies
    /// <c>Stream</c> with the current input handle (a Foreign cell
    /// wrapping its <see cref="StreamHandle"/>).</summary>
    public static bool CurrentInput(Engine engine)
    {
        StreamRegistry registry = engine.Streams
            ?? throw new InvalidOperationException("Engine has no stream registry.");
        Cell handleCell = engine.MakeForeign(registry.CurrentInput);
        return engine.UnifyRegisterWithCell(0, handleCell);
    }

    /// <summary><c>current_output(Stream)</c> — ISO §8.11.2.</summary>
    public static bool CurrentOutput(Engine engine)
    {
        StreamRegistry registry = engine.Streams
            ?? throw new InvalidOperationException("Engine has no stream registry.");
        Cell handleCell = engine.MakeForeign(registry.CurrentOutput);
        return engine.UnifyRegisterWithCell(0, handleCell);
    }

    /// <summary><c>set_input(Stream)</c> — ISO §8.11.3. Reassigns the
    /// current input cursor; <c>set_input(user_input)</c> resets to
    /// the terminal default.</summary>
    public static bool SetInput(Engine engine)
    {
        var h = ResolveStream(engine, engine.GetRegister(0));
        engine.Streams!.SetCurrentInput(h);
        return true;
    }

    /// <summary><c>set_output(Stream)</c> — ISO §8.11.4.</summary>
    public static bool SetOutput(Engine engine)
    {
        var h = ResolveStream(engine, engine.GetRegister(0));
        engine.Streams!.SetCurrentOutput(h);
        return true;
    }

    // ---------- flush_output ----------

    /// <summary><c>flush_output/0</c> — ISO §8.11.7. Flushes the
    /// current output stream.</summary>
    public static bool FlushOutput0(Engine engine)
    {
        var h = engine.Streams?.CurrentOutput ?? throw new InvalidOperationException(
            "Engine has no stream registry.");
        h.Writer!.Flush();
        return true;
    }

    /// <summary><c>flush_output(Stream)</c> — ISO §8.11.7.</summary>
    public static bool FlushOutput1(Engine engine)
    {
        var h = ResolveWriter(engine, engine.GetRegister(0));
        h.Writer!.Flush();
        return true;
    }

    // ---------- at_end_of_stream ----------

    /// <summary><c>at_end_of_stream(Stream)</c> — ISO §8.11.9.</summary>
    public static bool AtEndOfStream1(Engine engine)
    {
        var h = ResolveStream(engine, engine.GetRegister(0));
        if (h.Reader is null) return false;       // a writer is never "at end"
        // user_input's underlying console reader doesn't support
        // non-blocking Peek — report "not at end" conservatively.
        if (ReferenceEquals(h, engine.Streams!.UserInput)) return false;
        return h.Reader.Peek() < 0;
    }

    /// <summary><c>at_end_of_stream/0</c> — checks current_input.</summary>
    public static bool AtEndOfStream0(Engine engine)
    {
        var h = engine.Streams?.CurrentInput;
        if (h?.Reader is null) return false;
        if (ReferenceEquals(h, engine.Streams!.UserInput)) return false;
        return h.Reader.Peek() < 0;
    }

    // ---------- Helpers ----------

    private static Cell Resolve(Engine engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        return engine.GetHeap(engine.Deref(c.AsHeapIndex));
    }
}
