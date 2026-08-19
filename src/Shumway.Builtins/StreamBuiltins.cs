using System.IO;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// File and terminal text streams, backed by the per-engine
/// <see cref="StreamRegistry"/>. The stream-term is the ordinary ground
/// compound <c>'$stream'(Id)</c> (the same shape GNU Prolog uses), whose
/// argument is the registry id; the conventional atom <c>user_input</c> /
/// <c>user_output</c> and any user-defined alias also resolve to a
/// registered handle.
/// </summary>
public static class StreamBuiltins
{
    /// <summary>The stream-term's functor name. A stream-term is an
    /// ORDINARY ground term on purpose: it must survive everything a term
    /// survives — <c>copy_term/2</c>, <c>findall/3</c>, and above all
    /// <c>assertz/1</c> followed by a later <c>clause</c>/<c>retract</c>,
    /// across queries. A managed handle inside a cell cannot (the payload
    /// side table is per-activation), and a foreign-cell stream-term used
    /// to lose its identity when the clause was compiled to bytecode.</summary>
    public const string StreamFunctor = "$stream";

    // ---------- Stream-arg resolution ----------

    /// <summary>Builds the stream-term for <paramref name="h"/> —
    /// <c>'$stream'(Id)</c> on the heap.</summary>
    public static Cell MakeStreamTerm(Activation engine, StreamHandle h)
    {
        int fid = FunctorTable.Intern(
            AtomTable.Intern(StreamFunctor, permanent: true).Id, 1);
        int idx = engine.AllocateHeap(2);
        engine.SetHeap(idx, Cell.Functor(fid));
        engine.SetHeap(idx + 1, Cell.Int(h.Id));
        return Cell.Str(idx);
    }

    /// <summary>Resolves a stream argument cell to its
    /// <see cref="StreamHandle"/>. Accepts the stream-term
    /// <c>'$stream'(Id)</c> or an atom matching a registered alias.
    /// Throws ISO-shaped errors for the failure modes ISO §8.11
    /// specifies, matching what GNU Prolog and SWI both raise.</summary>
    public static StreamHandle ResolveStream(Activation engine, Cell cell)
    {
        Cell d = Resolve(engine, cell);
        if (d.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");

        StreamRegistry registry = engine.Streams
            ?? throw new InvalidOperationException("Activation has no stream registry.");

        if (TryReadStreamId(engine, d, out int id))
        {
            // Well-formed stream-term: a closed / unknown id is
            // existence_error(stream, Culprit) — ISO §8.11, and what GNU
            // and SWI both raise for '$stream'(999).
            var h = registry.GetById(id);
            if (h is null || h.Closed)
                throw new PrologRuntimeException(
                    "existence_error", "stream", engine, d);
            return h;
        }
        if (d.Tag == Tag.Atom)
        {
            string name = AtomTable.GetById(d.AsAtomId)?.Name ?? "";
            var h = registry.GetByAlias(name);
            if (h is null || h.Closed)
                throw new PrologRuntimeException(
                    "existence_error", "stream", engine, d);
            return h;
        }
        // Neither a stream-term nor an alias: ISO §8.11 domain_error
        // (`stream_or_alias` names a DOMAIN, not a type — GNU and SWI agree).
        throw new PrologRuntimeException(
            "domain_error", "stream_or_alias", engine, d);
    }

    /// <summary>True when <paramref name="d"/> is the stream-term
    /// <c>'$stream'(Id)</c> with an integer id — a malformed
    /// <c>'$stream'(foo)</c> or <c>'$stream'(1,2)</c> is NOT a stream-term
    /// and falls through to the domain error.</summary>
    private static bool TryReadStreamId(Activation engine, Cell d, out int id)
    {
        id = 0;
        if (d.Tag != Tag.Str) return false;
        int fIdx = d.AsHeapIndex;
        var (atomId, arity) = FunctorTable.Lookup(engine.GetHeap(fIdx).AsFunctorId);
        if (arity != 1) return false;
        if ((AtomTable.GetById(atomId)?.Name ?? "") != StreamFunctor) return false;
        Cell arg = Resolve(engine, engine.GetHeap(fIdx + 1));
        if (arg.Tag != Tag.Int) return false;
        long v = arg.AsInt;
        if (v < 0 || v > int.MaxValue) return false;
        id = (int)v;
        return true;
    }

    /// <summary>True when <paramref name="cell"/> resolves to an open
    /// stream — the <c>is_stream/1</c> test, which never throws.</summary>
    public static bool IsOpenStream(Activation engine, Cell cell)
    {
        Cell d = Resolve(engine, cell);
        var registry = engine.Streams;
        if (registry is null) return false;
        if (TryReadStreamId(engine, d, out int id))
        {
            var h = registry.GetById(id);
            return h is not null && !h.Closed;
        }
        if (d.Tag == Tag.Atom)
        {
            var h = registry.GetByAlias(AtomTable.GetById(d.AsAtomId)?.Name ?? "");
            return h is not null && !h.Closed;
        }
        return false;
    }

    /// <summary>§8.11.5.3.e/f: the source/sink must be an ATOM (or the
    /// chars-list / PSTR spellings SourceSinkText accepts). A compound is
    /// domain_error(source_sink, S); a number is type_error(atom, S).</summary>
    private static void ValidateSourceSink(Activation engine, Cell pathCell)
    {
        switch (pathCell.Tag)
        {
            case Tag.Atom:
            case Tag.Lis:
            case Tag.Pstr:
            case Tag.String:
                return;
            case Tag.Str:
                throw new PrologRuntimeException(
                    "domain_error", "source_sink", engine, pathCell);
            default:
                throw new PrologRuntimeException("type_error", "atom", engine, pathCell);
        }
    }

    private static StreamHandle ResolveReader(Activation engine, Cell cell)
    {
        var h = ResolveStream(engine, cell);
        if (!h.IsReader)
            // The culprit is the stream-or-alias ARGUMENT as written
            // (§8.12.1.3.d wants e.g. user_output, not a fresh var).
            throw new PrologRuntimeException("permission_error", "input,stream",
                engine, Resolve(engine, cell));
        return h;
    }

    private static StreamHandle ResolveWriter(Activation engine, Cell cell)
    {
        var h = ResolveStream(engine, cell);
        if (!h.IsWriter)
            throw new PrologRuntimeException("permission_error", "output,stream",
                engine, Resolve(engine, cell));
        return h;
    }

    // ---------- open/3, close/1 ----------

    public static bool Open(Activation engine)
    {
        Cell pathCell = Resolve(engine, engine.GetRegister(0));
        Cell modeCell = Resolve(engine, engine.GetRegister(1));
        if (pathCell.Tag == Tag.Ref || modeCell.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        ValidateSourceSink(engine, pathCell);
        if (modeCell.Tag != Tag.Atom)
            throw new PrologRuntimeException("type_error", "atom", engine, modeCell);
        // §8.11.5.3: the Stream argument must be UNBOUND.
        Cell streamOut = Resolve(engine, engine.GetRegister(2));
        if (streamOut.Tag is not (Tag.Ref or Tag.AttVar))
            throw new PrologRuntimeException(
                "uninstantiation_error", "", engine, streamOut);

        string path = SourceSinkText(engine, pathCell);
        string mode = AtomTable.GetById(modeCell.AsAtomId)?.Name ?? "";
        if (mode is not ("read" or "write" or "append"))
            throw new PrologRuntimeException("domain_error", "io_mode", engine, modeCell);

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
        return engine.UnifyRegisterWithCell(2, MakeStreamTerm(engine, handle));
    }

    /// <summary>The source-sink argument's text: an atom as always, or —
    /// Scryer-compat, where text IS a chars list — a proper list of one-char
    /// atoms (a PSTR included). Anything else keeps the ISO
    /// type_error(atom).</summary>
    internal static string SourceSinkText(Activation engine, Cell pathCell)
    {
        if (pathCell.Tag == Tag.Atom)
            return AtomTable.GetById(pathCell.AsAtomId)?.Name ?? "";
        if (pathCell.Tag == Tag.Pstr)
        {
            string s = engine.ReadPstrChain(pathCell, out Cell tail);
            tail = Resolve(engine, tail);
            if (tail.Tag == Tag.Atom && tail.AsAtomId == AtomTable.EmptyListId)
                return s;
        }
        else if (pathCell.Tag == Tag.Lis)
        {
            var sb = new System.Text.StringBuilder();
            Cell cur = pathCell;
            int steps = 0, cap = engine.HeapTop + 1;
            while (cur.Tag == Tag.Lis)
            {
                if (++steps > cap) break;   // cyclic — fall to the type error
                Cell head = Resolve(engine, engine.GetHeap(cur.AsHeapIndex));
                if (head.Tag != Tag.Atom) break;
                string? c = AtomTable.GetById(head.AsAtomId)?.Name;
                if (c is null || c.Length != 1) break;
                sb.Append(c);
                cur = Resolve(engine, engine.GetHeap(cur.AsHeapIndex + 1));
            }
            if (cur.Tag == Tag.Atom && cur.AsAtomId == AtomTable.EmptyListId)
                return sb.ToString();
        }
        throw new PrologRuntimeException("type_error", "atom", engine, pathCell);
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
        ValidateSourceSink(engine, pathCell);
        if (modeCell.Tag != Tag.Atom)
            throw new PrologRuntimeException("type_error", "atom", engine, modeCell);
        // §8.11.5.3: the Stream argument must be UNBOUND.
        Cell streamOut = Resolve(engine, engine.GetRegister(2));
        if (streamOut.Tag is not (Tag.Ref or Tag.AttVar))
            throw new PrologRuntimeException(
                "uninstantiation_error", "", engine, streamOut);

        string path = SourceSinkText(engine, pathCell);
        string mode = AtomTable.GetById(modeCell.AsAtomId)?.Name ?? "";
        if (mode is not ("read" or "write" or "append"))
            throw new PrologRuntimeException("domain_error", "io_mode", engine, modeCell);

        // Parse the options list. Each option is a 1-arg compound;
        // anything else is a stream_option domain error.
        string? alias = null;
        string eofAction = "eof_code";
        bool binary = false;
        System.Text.Encoding? encoding = null;
        Cell cur = optsCell;
        while (cur.Tag == Tag.Lis)
        {
            Cell head = Resolve(engine, engine.GetHeap(cur.AsHeapIndex));
            if (head.Tag == Tag.Ref)
                throw new PrologRuntimeException("instantiation_error");
            if (head.Tag != Tag.Str)
                throw new PrologRuntimeException(
                    "domain_error", "stream_option", engine, head);

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
                    if (argCell.Tag == Tag.Ref)
                        throw new PrologRuntimeException("instantiation_error");
                    if (argCell.Tag != Tag.Atom)
                        throw new PrologRuntimeException("domain_error", "stream_option");
                    eofAction = AtomTable.GetById(argCell.AsAtomId)?.Name ?? "";
                    if (eofAction is not ("error" or "eof_code" or "reset"))
                        throw new PrologRuntimeException("domain_error", "stream_option");
                    break;
                case "reposition":
                    // Recognised; SeekablePosition is implicit on
                    // file streams — no plumbing needed yet.
                    break;
                default:
                    throw new PrologRuntimeException(
                        "domain_error", "stream_option", engine, head);
            }
            cur = Resolve(engine, engine.GetHeap(cur.AsHeapIndex + 1));
        }
        if (cur.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        if (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId)
            throw new PrologRuntimeException("type_error", "list",
                engine, Resolve(engine, engine.GetRegister(3)));

        StreamRegistry registry = engine.Streams
            ?? throw new InvalidOperationException("Activation has no stream registry.");

        // ISO permission_error(open, source_sink, alias(_)) when the
        // requested alias is already taken.
        if (alias is not null && registry.IsAliasTaken(alias))
        {
            // Culprit is the OPTION term alias(A) (§8.11.5.3.j).
            int aliasFid = FunctorTable.Intern(
                AtomTable.Intern("alias", permanent: true).Id, 1);
            int ab = engine.AllocateHeap(2);
            engine.SetHeap(ab, Cell.Functor(aliasFid));
            engine.SetHeap(ab + 1, Cell.Atom(AtomTable.Intern(alias, permanent: true).Id));
            throw new PrologRuntimeException("permission_error", "open,source_sink",
                engine, Cell.Str(ab));
        }

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
                    _ => throw new PrologRuntimeException(
                        "domain_error", "io_mode", engine, modeCell),
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
                    _ => throw new PrologRuntimeException(
                        "domain_error", "io_mode", engine, modeCell),
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

        handle.EofAction = eofAction;
        registry.Add(handle);
        return engine.UnifyRegisterWithCell(2, MakeStreamTerm(engine, handle));
    }

    /// <summary>.NET's FileStream refuses device paths, but portable Prolog
    /// opens the platform null device by name (Logtalk os::null_device_path).
    /// "nul"/"nul:" on Windows map to <see cref="Stream.Null"/>; /dev/null
    /// opens natively on Unix so no mapping is needed there.</summary>
    private static bool IsWindowsNullDevice(string path) =>
        PrologPath.IsNullDevice(path);

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
        // ISO §8.11.6: closing a standard stream has no effect — the
        // user streams outlive every close and stay registered.
        var st = engine.Streams!;
        if (ReferenceEquals(h, st.UserInput) || ReferenceEquals(h, st.UserOutput)
            || ReferenceEquals(h, st.UserError))
            return true;
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
        bool force = ParseCloseOptions(engine, engine.GetRegister(1));
        // ISO §8.11.6: closing a standard stream has no effect — the
        // user streams outlive every close and stay registered.
        var st = engine.Streams!;
        if (ReferenceEquals(h, st.UserInput) || ReferenceEquals(h, st.UserOutput)
            || ReferenceEquals(h, st.UserError))
            return true;
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
        h.Writer!.Write('\n');   // LF on every platform (ADR-045)
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
        var h = ResolveReader(engine, engine.GetRegister(0));
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
        var h = ResolveReader(engine, engine.GetRegister(0));
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
        WriteOneByte(engine, h, regByte: 1, streamReg: 0);
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
        CheckOutArgIsInByte(engine, regOut);
        CheckPastEof(engine, h);
        int b = h.BinaryStream!.ReadByte();
        if (b < 0) h.PastEof = true;
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
        CheckOutArgIsInByte(engine, regOut);
        CheckPastEof(engine, h);
        long pos = bs.Position;
        int b = bs.ReadByte();
        bs.Position = pos;
        return engine.UnifyRegisterWithCell(regOut, Cell.Int(b));
    }

    private static void WriteOneByte(
        Activation engine, StreamHandle h, int regByte, int streamReg = -1)
    {
        if (!h.IsWriter)
            throw new PrologRuntimeException("permission_error", "output,stream",
                engine, streamReg >= 0
                    ? Resolve(engine, engine.GetRegister(streamReg))
                    : MakeStreamTerm(engine, h));
        if (!h.IsBinary)
            // ISO §8.13.3.3.g: byte I/O on a text stream is
            // permission_error(output, text_stream, _).
            throw new PrologRuntimeException("permission_error", "output,text_stream");
        Cell c = Resolve(engine, engine.GetRegister(regByte));
        if (c.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        if (c.Tag != Tag.Int)
            throw new PrologRuntimeException("type_error", "byte", engine, c);
        long v = c.AsInt;
        if (v < 0 || v > 255)
            throw new PrologRuntimeException("type_error", "byte", engine, c);
        h.BinaryStream!.WriteByte((byte)v);
    }

    // ---------- character / code helpers ----------

    /// <summary>The §8.12 pre-read gate: touching a stream whose position
    /// is already past-end-of-stream under <c>eof_action(error)</c> raises
    /// <c>permission_error(input, past_end_of_stream, S)</c>. All other
    /// combinations read normally (eof keeps yielding <c>end_of_file</c> —
    /// GNU's default behaviour).</summary>
    /// <summary>§8.12.1.3.c: a BOUND output argument that could never be a
    /// read result is a type error up front — get_char(S, 1) raises
    /// type_error(in_character, 1), it does not just fail. in_character =
    /// one-char atom or end_of_file.</summary>
    private static void CheckOutArgIsInCharacter(Activation engine, int regOut)
    {
        Cell c = Resolve(engine, engine.GetRegister(regOut));
        if (c.Tag == Tag.Ref || c.Tag == Tag.AttVar) return;
        if (c.Tag == Tag.Atom)
        {
            string? n = AtomTable.GetById(c.AsAtomId)?.Name;
            if (n is not null && (n.Length == 1 || n == "end_of_file"
                || (n.Length == 2 && char.IsSurrogatePair(n[0], n[1]))))
                return;
        }
        throw new PrologRuntimeException("type_error", "in_character", engine, c);
    }

    /// <summary>§8.12.4.3.c: in_character_code = a char code or -1.</summary>
    private static void CheckOutArgIsInCharacterCode(Activation engine, int regOut)
    {
        Cell c = Resolve(engine, engine.GetRegister(regOut));
        if (c.Tag == Tag.Ref || c.Tag == Tag.AttVar) return;
        if (c.Tag != Tag.Int)
            throw new PrologRuntimeException("type_error", "integer", engine, c);
        long v = c.AsInt;
        if (v == -1 || (v >= 0 && v <= 0x10FFFF)) return;
        throw new PrologRuntimeException("representation_error", "in_character_code");
    }

    /// <summary>§8.13.1.3.c: in_byte = 0..255 or -1.</summary>
    private static void CheckOutArgIsInByte(Activation engine, int regOut)
    {
        Cell c = Resolve(engine, engine.GetRegister(regOut));
        if (c.Tag == Tag.Ref || c.Tag == Tag.AttVar) return;
        if (c.Tag == Tag.Int && (c.AsInt == -1 || (c.AsInt >= 0 && c.AsInt <= 255)))
            return;
        throw new PrologRuntimeException("type_error", "in_byte", engine, c);
    }

    /// <summary>§8.11.6 close-option list validation: the list and every
    /// option must be instantiated; the only recognised option is
    /// <c>force(true|false)</c> — anything else is
    /// <c>domain_error(close_option, Culprit)</c>.</summary>
    private static bool ParseCloseOptions(Activation engine, Cell optsCell)
    {
        bool force = false;
        Cell cur = Resolve(engine, optsCell);
        while (true)
        {
            if (cur.Tag is Tag.Ref or Tag.AttVar)
                throw new PrologRuntimeException("instantiation_error");
            if (cur.Tag == Tag.Atom && cur.AsAtomId == AtomTable.EmptyListId)
                return force;
            if (cur.Tag != Tag.Lis)
                throw new PrologRuntimeException("type_error", "list", engine, cur);
            Cell head = Resolve(engine, engine.GetHeap(cur.AsHeapIndex));
            if (head.Tag is Tag.Ref or Tag.AttVar)
                throw new PrologRuntimeException("instantiation_error");
            bool ok = false;
            if (head.Tag == Tag.Str)
            {
                Cell f = engine.GetHeap(head.AsHeapIndex);
                var (aid, ar) = FunctorTable.Lookup(f.AsFunctorId);
                if (ar == 1 && AtomTable.GetById(aid)?.Name == "force")
                {
                    Cell arg = Resolve(engine, engine.GetHeap(head.AsHeapIndex + 1));
                    if (arg.Tag == Tag.Atom)
                    {
                        string? an = AtomTable.GetById(arg.AsAtomId)?.Name;
                        if (an == "true") { force = true; ok = true; }
                        else if (an == "false") ok = true;
                    }
                }
            }
            if (!ok)
                throw new PrologRuntimeException(
                    "domain_error", "close_option", engine, head);
            cur = Resolve(engine, engine.GetHeap(cur.AsHeapIndex + 1));
        }
    }

    private static void CheckPastEof(Activation engine, StreamHandle h)
    {
        if (h.PastEof && h.EofAction == "error")
            throw new PrologRuntimeException("permission_error",
                "input,past_end_of_stream", engine, MakeStreamTerm(engine, h));
        if (h.PastEof && h.EofAction == "reset") h.PastEof = false;
    }

    private static bool ReadCharInto(Activation engine, StreamHandle h, int regOut)
    {
        if (!h.IsReader)
            throw new PrologRuntimeException("permission_error", "input,stream");
        if (h.IsBinary)
            // ISO §8.12.1.3.g: char I/O on a binary stream is
            // permission_error(input, binary_stream, _).
            throw new PrologRuntimeException("permission_error", "input,binary_stream");
        CheckOutArgIsInCharacter(engine, regOut);
        CheckPastEof(engine, h);
        int c = h.Reader!.Read();
        if (c < 0) h.PastEof = true;
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
        CheckOutArgIsInCharacter(engine, regOut);
        CheckPastEof(engine, h);
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
        CheckOutArgIsInCharacterCode(engine, regOut);
        CheckPastEof(engine, h);
        int c = h.Reader!.Read();
        if (c < 0) h.PastEof = true;
        // ISO §8.12.4: EOF is the integer -1.
        return engine.UnifyRegisterWithCell(regOut, Cell.Int(c));
    }

    private static bool PeekCodeInto(Activation engine, StreamHandle h, int regOut)
    {
        if (!h.IsReader)
            throw new PrologRuntimeException("permission_error", "input,stream");
        if (h.IsBinary)
            throw new PrologRuntimeException("permission_error", "input,binary_stream");
        CheckOutArgIsInCharacterCode(engine, regOut);
        CheckPastEof(engine, h);
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
        return engine.UnifyRegisterWithCell(
            0, MakeStreamTerm(engine, registry.CurrentInput));
    }

    /// <summary><c>current_output(Stream)</c> — ISO §8.11.2.</summary>
    public static bool CurrentOutput(Activation engine)
    {
        StreamRegistry registry = engine.Streams
            ?? throw new InvalidOperationException("Activation has no stream registry.");
        return engine.UnifyRegisterWithCell(
            0, MakeStreamTerm(engine, registry.CurrentOutput));
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
        Cell arg = engine.GetRegister(0);
        var h = ResolveStream(engine, arg);
        if (!h.IsReader)
            throw new PrologRuntimeException("permission_error", "input,stream",
                engine, Resolve(engine, arg));
        return AtEnd(engine, h);
    }

    /// <summary><c>at_end_of_stream/0</c> — checks current_input.</summary>
    public static bool AtEndOfStream0(Activation engine)
    {
        var h = engine.Streams?.CurrentInput;
        return h is not null && AtEnd(engine, h);
    }

    /// <summary>True when the position is at OR past end (§8.11.9). A
    /// binary reader has no TextReader — probe the seekable stream.</summary>
    private static bool AtEnd(Activation engine, StreamHandle h)
    {
        if (!h.IsReader) return false;            // a writer is never "at end"
        if (h.PastEof) return true;
        // user_input's underlying console reader doesn't support
        // non-blocking Peek — report "not at end" conservatively.
        if (ReferenceEquals(h, engine.Streams!.UserInput)) return false;
        if (h.Reader is not null) return h.Reader.Peek() < 0;
        return h.BinaryStream is { CanSeek: true } bs && bs.Position >= bs.Length;
    }

    // ---------- Helpers ----------

    private static Cell Resolve(Activation engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        return engine.GetHeap(engine.Deref(c.AsHeapIndex));
    }
}
