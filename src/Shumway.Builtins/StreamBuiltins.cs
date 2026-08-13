using System.IO;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// File and terminal text streams, backed by the per-engine
/// <see cref="StreamRegistry"/>. Foreign cells holding a
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
    public static StreamHandle ResolveStream(Activation engine, Cell cell)
    {
        Cell d = Resolve(engine, cell);
        if (d.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");

        StreamRegistry registry = engine.Streams
            ?? throw new InvalidOperationException("Activation has no stream registry.");

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

    private static StreamHandle ResolveReader(Activation engine, Cell cell)
    {
        var h = ResolveStream(engine, cell);
        if (!h.IsReader)
            throw new PrologRuntimeException("permission_error", "input,stream");
        return h;
    }

    private static StreamHandle ResolveWriter(Activation engine, Cell cell)
    {
        var h = ResolveStream(engine, cell);
        if (!h.IsWriter)
            throw new PrologRuntimeException("permission_error", "output,stream");
        return h;
    }

    // ---------- open/3, close/1 ----------

    public static bool Open(Activation engine)
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
            ?? throw new InvalidOperationException("Activation has no stream registry.");

        int id = registry.NextId();
        StreamHandle handle;
        try
        {
            handle = IsWindowsNullDevice(path)
                ? NullDeviceHandle(id, mode, path, alias: null)
                : mode switch
            {
                "write"  => new StreamHandle(id, new StreamWriter(path, append: false) { NewLine = "\n" }, "write", path),
                "append" => new StreamHandle(id, new StreamWriter(path, append: true) { NewLine = "\n" }, "append", path),
                "read"   => new StreamHandle(id, new StreamReader(path), "read", path),
                _ => throw new PrologRuntimeException("domain_error",
                    "stream_mode (Phase 1 supports write / append / read)"),
            };
        }
        catch (FileNotFoundException)
        {
            throw new PrologRuntimeException("existence_error", "source_sink", engine, pathCell);
        }
        catch (DirectoryNotFoundException)
        {
            throw new PrologRuntimeException("existence_error", "source_sink", engine, pathCell);
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
    /// The four-argument form takes an options list, recognising
    /// <c>alias(Name)</c> (registers the stream under a user-chosen
    /// atom), <c>type(text|binary)</c> (binary opens a raw byte
    /// stream for the §8.13 byte I/O builtins),
    /// <c>encoding(utf8|iso_latin_1|ascii)</c> (SWI-style text
    /// encoding; default UTF-8) and
    /// <c>eof_action(error|eof_code|reset)</c> (accepted; reads at EOF
    /// follow the default end_of_file handling). Any other option
    /// raises <c>domain_error(stream_option, _)</c>.
    /// </summary>
    public static bool OpenWithOptions(Activation engine)
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
        bool binary = false;
        System.Text.Encoding? encoding = null;
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
                    // ISO §8.11.5.3: alias(A) with A not an atom makes the whole
                    // option invalid → domain_error(stream_option, alias(A))
                    // (GNU + Neumerkel agree), NOT type_error(atom) — consistent
                    // with the type(...) case below.
                    if (argCell.Tag != Tag.Atom)
                        throw new PrologRuntimeException("domain_error", "stream_option");
                    alias = AtomTable.GetById(argCell.AsAtomId)?.Name ?? "";
                    break;
                case "type":
                    if (argCell.Tag == Tag.Ref)
                        throw new PrologRuntimeException("instantiation_error");
                    if (argCell.Tag != Tag.Atom)
                        throw new PrologRuntimeException("domain_error", "stream_option");
                    string typeName = AtomTable.GetById(argCell.AsAtomId)?.Name ?? "";
                    if (typeName == "binary") binary = true;
                    else if (typeName != "text")
                        throw new PrologRuntimeException("domain_error", "stream_option");
                    break;
                case "encoding":
                    // SWI-style: encoding(utf8 | iso_latin_1 | ascii). The
                    // default StreamReader/Writer is UTF-8; iso_latin_1 maps
                    // bytes 0x80–0xFF to the SAME code points (Latin-1 is the
                    // first 256 of Unicode), so reading a Latin-1 file with
                    // it is byte-value-faithful — a UTF-8 read turns each
                    // such byte into U+FFFD.
                    if (argCell.Tag == Tag.Ref)
                        throw new PrologRuntimeException("instantiation_error");
                    if (argCell.Tag != Tag.Atom)
                        throw new PrologRuntimeException("domain_error", "stream_option");
                    string encName = AtomTable.GetById(argCell.AsAtomId)?.Name ?? "";
                    encoding = encName switch
                    {
                        "utf8" => new System.Text.UTF8Encoding(false),
                        "iso_latin_1" => System.Text.Encoding.Latin1,
                        "ascii" => System.Text.Encoding.ASCII,
                        _ => throw new PrologRuntimeException(
                            "domain_error", "stream_option"),
                    };
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
            ?? throw new InvalidOperationException("Activation has no stream registry.");

        // ISO permission_error(open, source_sink, alias(_)) when the
        // requested alias is already taken.
        if (alias is not null && registry.IsAliasTaken(alias))
            throw new PrologRuntimeException("permission_error", "open,source_sink");

        int id = registry.NextId();
        StreamHandle handle;
        try
        {
            if (IsWindowsNullDevice(path))
            {
                handle = binary
                    ? new StreamHandle(id, Stream.Null, mode, path, alias)
                    : NullDeviceHandle(id, mode, path, alias);
            }
            else if (binary)
            {
                // Binary streams open the file as a raw FileStream;
                // ISO §8.13's byte I/O builtins read / write through
                // StreamHandle.BinaryStream.
                FileMode fm = mode switch
                {
                    "write"  => FileMode.Create,
                    "append" => FileMode.Append,
                    "read"   => FileMode.Open,
                    _ => throw new PrologRuntimeException("domain_error", "stream_mode"),
                };
                FileAccess fa = mode == "read" ? FileAccess.Read : FileAccess.Write;
                handle = new StreamHandle(id, new FileStream(path, fm, fa),
                    mode, path, alias);
            }
            else
            {
                // With no encoding option, keep the platform defaults
                // (StreamReader's UTF-8 with BOM detection).
                handle = mode switch
                {
                    "write"  => new StreamHandle(id,
                        encoding is null
                            ? new StreamWriter(path, append: false) { NewLine = "\n" }
                            : new StreamWriter(path, false, encoding) { NewLine = "\n" },
                        "write", path, alias),
                    "append" => new StreamHandle(id,
                        encoding is null
                            ? new StreamWriter(path, append: true) { NewLine = "\n" }
                            : new StreamWriter(path, true, encoding) { NewLine = "\n" },
                        "append", path, alias),
                    "read"   => new StreamHandle(id,
                        encoding is null
                            ? new StreamReader(path)
                            : new StreamReader(path, encoding),
                        "read", path, alias),
                    _ => throw new PrologRuntimeException("domain_error",
                        "stream_mode (Phase 1 supports write / append / read)"),
                };
            }
        }
        catch (FileNotFoundException)
        {
            throw new PrologRuntimeException("existence_error", "source_sink", engine, pathCell);
        }
        catch (DirectoryNotFoundException)
        {
            throw new PrologRuntimeException("existence_error", "source_sink", engine, pathCell);
        }
        catch (IOException ex)
        {
            throw new PrologRuntimeException("system_error", ex.Message);
        }

        registry.Add(handle);
        Cell foreignCell = engine.MakeForeign(handle);
        return engine.UnifyRegisterWithCell(2, foreignCell);
    }

    /// <summary>.NET's FileStream refuses device paths, but portable Prolog
    /// opens the platform null device by name (Logtalk os::null_device_path).
    /// "nul"/"nul:" on Windows map to <see cref="Stream.Null"/>; /dev/null
    /// opens natively on Unix so no mapping is needed there.</summary>
    private static bool IsWindowsNullDevice(string path) =>
        OperatingSystem.IsWindows()
        && (path.Equals("nul", StringComparison.OrdinalIgnoreCase)
            || path.Equals("nul:", StringComparison.OrdinalIgnoreCase));

    private static StreamHandle NullDeviceHandle(int id, string mode, string path, string? alias) =>
        mode switch
        {
            "write" or "append" => new StreamHandle(
                id, new StreamWriter(Stream.Null), mode, path, alias),
            "read" => new StreamHandle(id, new StreamReader(Stream.Null), "read", path, alias),
            _ => throw new PrologRuntimeException("domain_error",
                "stream_mode (Phase 1 supports write / append / read)"),
        };

    public static bool Close(Activation engine)
    {
        var h = ResolveStream(engine, engine.GetRegister(0));
        if (h.Reader is not null) h.Reader.Dispose();
        if (h.Writer is not null) { h.Writer.Flush(); h.Writer.Dispose(); }
        if (h.BinaryStream is not null)
        {
            try { h.BinaryStream.Flush(); } catch { }
            h.BinaryStream.Dispose();
        }
        engine.Streams!.Remove(h);
        return true;
    }

    /// <summary><c>close(+Stream, +Options)</c> — ISO §8.11.6. The
    /// options list (<c>force(Bool)</c>, <c>timeout(Seconds)</c>)
    /// is parsed shallowly: <c>force(true)</c> swallows close
    /// exceptions. Other options are accepted but ignored.
    /// Without an options list, <c>close/1</c> is the entry point.</summary>
    public static bool Close2(Activation engine)
    {
        var h = ResolveStream(engine, engine.GetRegister(0));
        bool force = ContainsForceTrue(engine, engine.GetRegister(1));
        try
        {
            if (h.Reader is not null) h.Reader.Dispose();
            if (h.Writer is not null) { h.Writer.Flush(); h.Writer.Dispose(); }
            if (h.BinaryStream is not null)
            {
                try { h.BinaryStream.Flush(); } catch { if (!force) throw; }
                h.BinaryStream.Dispose();
            }
        }
        catch when (force) { /* force(true) swallows close errors */ }
        engine.Streams!.Remove(h);
        return true;
    }

    private static bool ContainsForceTrue(Activation engine, Cell listCell)
    {
        Cell cursor = DerefLocal(engine, listCell);
        int trueAtomId = AtomTable.Intern("true", permanent: true).Id;
        int forceFunctorId = FunctorTable.Intern(
            AtomTable.Intern("force", permanent: true).Id, 1);
        while (cursor.Tag == Tag.Lis)
        {
            int headIdx = cursor.AsHeapIndex;
            Cell head = DerefLocal(engine, engine.GetHeap(headIdx));
            if (head.Tag == Tag.Str)
            {
                int fIdx = head.AsHeapIndex;
                Cell fCell = engine.GetHeap(fIdx);
                if (fCell.Tag == Tag.Functor && fCell.AsFunctorId == forceFunctorId)
                {
                    Cell arg = DerefLocal(engine, engine.GetHeap(fIdx + 1));
                    if (arg.Tag == Tag.Atom && arg.AsAtomId == trueAtomId) return true;
                }
            }
            cursor = DerefLocal(engine, engine.GetHeap(headIdx + 1));
        }
        return false;
    }

    private static Cell DerefLocal(Activation engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        return engine.GetHeap(engine.Deref(c.AsHeapIndex));
    }

    // ---------- write/2, nl/1, get_char/2, peek_char/2 ----------

    /// <summary><c>write(Stream, Term)</c> — renders Term to the
    /// stream's writer in canonical form (matches write/1 over
    /// <see cref="Activation.Out"/>).</summary>
    public static bool WriteToStream(Activation engine)
    {
        var h = ResolveWriter(engine, engine.GetRegister(0));
        if (h.IsBinary)
            // Text write on a binary stream is
            // permission_error(output, binary_stream, _) (ISO §8.14.2.3.g).
            throw new PrologRuntimeException("permission_error", "output,binary_stream");
        TermRenderer.Render(engine, engine.GetRegister(1), h.Writer!,
            new TermRenderOptions { Operators = engine.Operators });
        return true;
    }

    /// <summary><c>nl(Stream)</c> — writes a newline to the given stream.</summary>
    public static bool NlOnStream(Activation engine)
    {
        var h = ResolveWriter(engine, engine.GetRegister(0));
        if (h.IsBinary)
            throw new PrologRuntimeException("permission_error", "output,binary_stream");
        h.Writer!.WriteLine();
        return true;
    }

    /// <summary><c>get_char(Stream, Char)</c> — reads one character from
    /// the stream and unifies the result with <c>Char</c> as a
    /// single-character atom. End of stream returns the atom
    /// <c>end_of_file</c>.</summary>
    public static bool GetChar(Activation engine)
    {
        var h = ResolveReader(engine, engine.GetRegister(0));
        return ReadCharInto(engine, h, regOut: 1);
    }

    /// <summary><c>peek_char(Stream, Char)</c> — returns the next char
    /// without consuming it. EOF yields <c>end_of_file</c>.</summary>
    public static bool PeekChar(Activation engine)
    {
        var h = ResolveReader(engine, engine.GetRegister(0));
        return PeekCharInto(engine, h, regOut: 1);
    }

    // ---------- §8.12 character / code I/O ----------

    /// <summary><c>get_char/1</c> — reads one character from the
    /// current input stream. ISO §8.12.1.</summary>
    public static bool GetChar0(Activation engine)
    {
        var h = engine.Streams?.CurrentInput
            ?? throw new InvalidOperationException("Activation has no stream registry.");
        return ReadCharInto(engine, h, regOut: 0);
    }

    /// <summary><c>peek_char/1</c> — peeks one character from the
    /// current input stream. ISO §8.12.2.</summary>
    public static bool PeekChar0(Activation engine)
    {
        var h = engine.Streams?.CurrentInput
            ?? throw new InvalidOperationException("Activation has no stream registry.");
        return PeekCharInto(engine, h, regOut: 0);
    }

    /// <summary><c>put_char/1</c> — writes a single-character atom to
    /// the current output stream. ISO §8.12.3.</summary>
    public static bool PutChar1(Activation engine)
    {
        var h = engine.Streams?.CurrentOutput
            ?? throw new InvalidOperationException("Activation has no stream registry.");
        WriteOneChar(engine, h, regChar: 0);
        return true;
    }

    /// <summary><c>put_char(+Stream, +Char)</c> — ISO §8.12.3.</summary>
    public static bool PutChar2(Activation engine)
    {
        var h = ResolveWriter(engine, engine.GetRegister(0));
        WriteOneChar(engine, h, regChar: 1);
        return true;
    }

    /// <summary><c>get_code/1</c> — reads one character code from
    /// current input. EOF returns -1. ISO §8.12.4.</summary>
    public static bool GetCode0(Activation engine)
    {
        var h = engine.Streams?.CurrentInput
            ?? throw new InvalidOperationException("Activation has no stream registry.");
        return ReadCodeInto(engine, h, regOut: 0);
    }

    /// <summary><c>get_code(+Stream, -Code)</c> — ISO §8.12.4.</summary>
    public static bool GetCode2(Activation engine)
    {
        var h = ResolveReader(engine, engine.GetRegister(0));
        return ReadCodeInto(engine, h, regOut: 1);
    }

    /// <summary><c>peek_code/1</c> — ISO §8.12.5.</summary>
    public static bool PeekCode0(Activation engine)
    {
        var h = engine.Streams?.CurrentInput
            ?? throw new InvalidOperationException("Activation has no stream registry.");
        return PeekCodeInto(engine, h, regOut: 0);
    }

    /// <summary><c>peek_code(+Stream, -Code)</c> — ISO §8.12.5.</summary>
    public static bool PeekCode2(Activation engine)
    {
        var h = ResolveReader(engine, engine.GetRegister(0));
        return PeekCodeInto(engine, h, regOut: 1);
    }

    /// <summary><c>put_code(+Code)</c> — writes the character with the
    /// given code to current output. ISO §8.12.6.</summary>
    public static bool PutCode1(Activation engine)
    {
        var h = engine.Streams?.CurrentOutput
            ?? throw new InvalidOperationException("Activation has no stream registry.");
        WriteOneCode(engine, h, regCode: 0);
        return true;
    }

    /// <summary><c>put_code(+Stream, +Code)</c> — ISO §8.12.6.</summary>
    public static bool PutCode2(Activation engine)
    {
        var h = ResolveWriter(engine, engine.GetRegister(0));
        WriteOneCode(engine, h, regCode: 1);
        return true;
    }

    // ---------- §8.13 byte I/O ----------

    /// <summary><c>get_byte/1</c> — reads one byte from current
    /// input. EOF returns -1. ISO §8.13.1.</summary>
    public static bool GetByte0(Activation engine)
    {
        var h = engine.Streams?.CurrentInput
            ?? throw new InvalidOperationException("Activation has no stream registry.");
        return ReadByteInto(engine, h, regOut: 0);
    }

    /// <summary><c>get_byte(+Stream, -Byte)</c> — ISO §8.13.1.</summary>
    public static bool GetByte2(Activation engine)
    {
        var h = ResolveStream(engine, engine.GetRegister(0));
        return ReadByteInto(engine, h, regOut: 1);
    }

    /// <summary><c>peek_byte/1</c> — ISO §8.13.2.</summary>
    public static bool PeekByte0(Activation engine)
    {
        var h = engine.Streams?.CurrentInput
            ?? throw new InvalidOperationException("Activation has no stream registry.");
        return PeekByteInto(engine, h, regOut: 0);
    }

    /// <summary><c>peek_byte(+Stream, -Byte)</c> — ISO §8.13.2.</summary>
    public static bool PeekByte2(Activation engine)
    {
        var h = ResolveStream(engine, engine.GetRegister(0));
        return PeekByteInto(engine, h, regOut: 1);
    }

    /// <summary><c>put_byte/1</c> — writes one byte to current
    /// output. ISO §8.13.3.</summary>
    public static bool PutByte1(Activation engine)
    {
        var h = engine.Streams?.CurrentOutput
            ?? throw new InvalidOperationException("Activation has no stream registry.");
        WriteOneByte(engine, h, regByte: 0);
        return true;
    }

    /// <summary><c>put_byte(+Stream, +Byte)</c> — ISO §8.13.3.</summary>
    public static bool PutByte2(Activation engine)
    {
        var h = ResolveStream(engine, engine.GetRegister(0));
        WriteOneByte(engine, h, regByte: 1);
        return true;
    }

    // ---------- byte helpers ----------

    private static bool ReadByteInto(Activation engine, StreamHandle h, int regOut)
    {
        if (!h.IsReader)
            throw new PrologRuntimeException("permission_error", "input,stream");
        if (!h.IsBinary)
            // ISO §8.13.1.3.g: byte I/O on a text stream is
            // permission_error(input, text_stream, _).
            throw new PrologRuntimeException("permission_error", "input,text_stream");
        int b = h.BinaryStream!.ReadByte();
        return engine.UnifyRegisterWithCell(regOut, Cell.Int(b));
    }

    private static bool PeekByteInto(Activation engine, StreamHandle h, int regOut)
    {
        if (!h.IsReader)
            throw new PrologRuntimeException("permission_error", "input,stream");
        if (!h.IsBinary)
            throw new PrologRuntimeException("permission_error", "input,text_stream");
        var bs = h.BinaryStream!;
        if (!bs.CanSeek)
            throw new PrologRuntimeException("permission_error", "reposition,stream");
        long pos = bs.Position;
        int b = bs.ReadByte();
        bs.Position = pos;
        return engine.UnifyRegisterWithCell(regOut, Cell.Int(b));
    }

    private static void WriteOneByte(Activation engine, StreamHandle h, int regByte)
    {
        if (!h.IsWriter)
            throw new PrologRuntimeException("permission_error", "output,stream");
        if (!h.IsBinary)
            // ISO §8.13.3.3.g: byte I/O on a text stream is
            // permission_error(output, text_stream, _).
            throw new PrologRuntimeException("permission_error", "output,text_stream");
        Cell c = Resolve(engine, engine.GetRegister(regByte));
        if (c.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        if (c.Tag != Tag.Int)
            throw new PrologRuntimeException("type_error", "byte");
        long v = c.AsInt;
        if (v < 0 || v > 255)
            throw new PrologRuntimeException("type_error", "byte");
        h.BinaryStream!.WriteByte((byte)v);
    }

    // ---------- character / code helpers ----------

    private static bool ReadCharInto(Activation engine, StreamHandle h, int regOut)
    {
        if (!h.IsReader)
            throw new PrologRuntimeException("permission_error", "input,stream");
        if (h.IsBinary)
            // ISO §8.12.1.3.g: char I/O on a binary stream is
            // permission_error(input, binary_stream, _).
            throw new PrologRuntimeException("permission_error", "input,binary_stream");
        int c = h.Reader!.Read();
        // Same single-char-atom cache as PeekCharInto.
        int atomId = c < 0
            ? _eofAtomId
            : AtomTable.GetSingleCharAtomId(c);
        if (atomId < 0)
            atomId = AtomTable.Intern(((char)c).ToString(), permanent: false).Id;
        return engine.UnifyRegisterWithCell(regOut, Cell.Atom(atomId));
    }

    private static bool PeekCharInto(Activation engine, StreamHandle h, int regOut)
    {
        if (!h.IsReader)
            throw new PrologRuntimeException("permission_error", "input,stream");
        if (h.IsBinary)
            throw new PrologRuntimeException("permission_error", "input,binary_stream");
        int c = h.Reader!.Peek();
        // Hot path: the cached single-char atom id is a pure array
        // index — saves the lock + dictionary probe + 1-char string
        // allocation that AtomTable.Intern would have done. EOF is a
        // 10-char atom that doesn't fit the cache; interned once as
        // permanent.
        int atomId = c < 0
            ? _eofAtomId
            : AtomTable.GetSingleCharAtomId(c);
        if (atomId < 0)
            atomId = AtomTable.Intern(((char)c).ToString(), permanent: false).Id;
        return engine.UnifyRegisterWithCell(regOut, Cell.Atom(atomId));
    }

    private static readonly int _eofAtomId =
        AtomTable.Intern("end_of_file", permanent: true).Id;

    private static bool ReadCodeInto(Activation engine, StreamHandle h, int regOut)
    {
        if (!h.IsReader)
            throw new PrologRuntimeException("permission_error", "input,stream");
        if (h.IsBinary)
            throw new PrologRuntimeException("permission_error", "input,binary_stream");
        int c = h.Reader!.Read();
        // ISO §8.12.4: EOF is the integer -1.
        return engine.UnifyRegisterWithCell(regOut, Cell.Int(c));
    }

    private static bool PeekCodeInto(Activation engine, StreamHandle h, int regOut)
    {
        if (!h.IsReader)
            throw new PrologRuntimeException("permission_error", "input,stream");
        if (h.IsBinary)
            throw new PrologRuntimeException("permission_error", "input,binary_stream");
        int c = h.Reader!.Peek();
        return engine.UnifyRegisterWithCell(regOut, Cell.Int(c));
    }

    private static void WriteOneChar(Activation engine, StreamHandle h, int regChar)
    {
        if (h.IsBinary)
            throw new PrologRuntimeException("permission_error", "output,binary_stream");
        Cell c = Resolve(engine, engine.GetRegister(regChar));
        if (c.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        if (c.Tag != Tag.Atom)
            throw new PrologRuntimeException("type_error", "character");
        string name = AtomTable.GetById(c.AsAtomId)?.Name ?? "";
        if (name.Length != 1)
            throw new PrologRuntimeException("type_error", "character");
        h.Writer!.Write(name[0]);
    }

    private static void WriteOneCode(Activation engine, StreamHandle h, int regCode)
    {
        if (h.IsBinary)
            throw new PrologRuntimeException("permission_error", "output,binary_stream");
        Cell c = Resolve(engine, engine.GetRegister(regCode));
        if (c.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        if (c.Tag != Tag.Int)
            throw new PrologRuntimeException("type_error", "integer");
        long code = c.AsInt;
        if (code < 0 || code > char.MaxValue)
            throw new PrologRuntimeException("representation_error", "character_code");
        h.Writer!.Write((char)code);
    }

    // ---------- current_input / current_output / set_input / set_output ----------

    /// <summary><c>current_input(Stream)</c> — ISO §8.11.1. Unifies
    /// <c>Stream</c> with the current input handle (a Foreign cell
    /// wrapping its <see cref="StreamHandle"/>).</summary>
    public static bool CurrentInput(Activation engine)
    {
        StreamRegistry registry = engine.Streams
            ?? throw new InvalidOperationException("Activation has no stream registry.");
        Cell handleCell = engine.MakeForeign(registry.CurrentInput);
        return engine.UnifyRegisterWithCell(0, handleCell);
    }

    /// <summary><c>current_output(Stream)</c> — ISO §8.11.2.</summary>
    public static bool CurrentOutput(Activation engine)
    {
        StreamRegistry registry = engine.Streams
            ?? throw new InvalidOperationException("Activation has no stream registry.");
        Cell handleCell = engine.MakeForeign(registry.CurrentOutput);
        return engine.UnifyRegisterWithCell(0, handleCell);
    }

    /// <summary><c>set_input(Stream)</c> — ISO §8.11.3. Reassigns the
    /// current input cursor; <c>set_input(user_input)</c> resets to
    /// the terminal default.</summary>
    public static bool SetInput(Activation engine)
    {
        var h = ResolveStream(engine, engine.GetRegister(0));
        engine.Streams!.SetCurrentInput(h);
        return true;
    }

    /// <summary><c>set_output(Stream)</c> — ISO §8.11.4.</summary>
    public static bool SetOutput(Activation engine)
    {
        var h = ResolveStream(engine, engine.GetRegister(0));
        engine.Streams!.SetCurrentOutput(h);
        return true;
    }

    // ---------- flush_output ----------

    /// <summary><c>flush_output/0</c> — ISO §8.11.7. Flushes the
    /// current output stream.</summary>
    public static bool FlushOutput0(Activation engine)
    {
        var h = engine.Streams?.CurrentOutput ?? throw new InvalidOperationException(
            "Activation has no stream registry.");
        h.Writer!.Flush();
        return true;
    }

    /// <summary><c>flush_output(Stream)</c> — ISO §8.11.7.</summary>
    public static bool FlushOutput1(Activation engine)
    {
        var h = ResolveWriter(engine, engine.GetRegister(0));
        h.Writer!.Flush();
        return true;
    }

    // ---------- at_end_of_stream ----------

    /// <summary><c>at_end_of_stream(Stream)</c> — ISO §8.11.9.</summary>
    public static bool AtEndOfStream1(Activation engine)
    {
        var h = ResolveStream(engine, engine.GetRegister(0));
        if (h.Reader is null) return false;       // a writer is never "at end"
        // user_input's underlying console reader doesn't support
        // non-blocking Peek — report "not at end" conservatively.
        if (ReferenceEquals(h, engine.Streams!.UserInput)) return false;
        return h.Reader.Peek() < 0;
    }

    /// <summary><c>at_end_of_stream/0</c> — checks current_input.</summary>
    public static bool AtEndOfStream0(Activation engine)
    {
        var h = engine.Streams?.CurrentInput;
        if (h?.Reader is null) return false;
        if (ReferenceEquals(h, engine.Streams!.UserInput)) return false;
        return h.Reader.Peek() < 0;
    }

    // ---------- Helpers ----------

    private static Cell Resolve(Activation engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        return engine.GetHeap(engine.Deref(c.AsHeapIndex));
    }
}
