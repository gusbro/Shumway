using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

public static partial class MetaBuiltins
{
    /// <summary><c>is_stream(@Term)</c> (SWI) — succeeds iff Term is the
    /// stream-term of an open stream (<c>'$stream'(Id)</c>) or a registered
    /// stream alias atom. Never throws — a non-stream fails.</summary>
    public static bool IsStream(Activation engine)
        => Shumway.Builtins.StreamBuiltins.IsOpenStream(engine, engine.GetRegister(0));

    /// <summary><c>current_stream(?Filename, ?Mode, ?Stream)</c> —
    /// ISO §8.11.8.1. Enumerates every registered stream on
    /// backtracking. Filename and Mode arguments unify against each
    /// handle's metadata; the stream arg is bound to a Foreign cell
    /// wrapping the underlying <see cref="Shumway.Core.StreamHandle"/>.
    ///</summary>
    public static bool CurrentStream(Activation engine)
    {
        var registry = engine.Streams
            ?? throw new InvalidOperationException("Activation has no stream registry.");
        var handles = registry.All().ToArray();
        int returnPc = engine.BuiltinReturnPc;
        // arity 3 (current_stream/3): save the arg registers so a wrapping
        // findall can't clobber them between solutions (
        // missed for these enumerators).
        return IndexEnumCursor.Start(engine, handles.Length, 3, returnPc,
            (e, i) => CurrentStreamUnify(e, handles, i));
    }

    private static bool CurrentStreamUnify(
        Activation engine, Shumway.Core.StreamHandle[] handles, int idx)
    {
        var h = handles[idx];
        string fnText = h.Filename is string f
            ? Shumway.Core.PrologPath.ToCanonical(f) : (h.Alias ?? "");
        Cell fnCell = Cell.Atom(AtomTable.Intern(fnText, permanent: false).Id);
        Cell modeCell = Cell.Atom(AtomTable.Intern(h.Mode, permanent: true).Id);
        Cell streamCell = Shumway.Builtins.StreamBuiltins.MakeStreamTerm(engine, h);

        if (!engine.UnifyRegisterWithCell(0, fnCell)) return false;
        if (!engine.UnifyRegisterWithCell(1, modeCell)) return false;
        if (!engine.UnifyRegisterWithCell(2, streamCell)) return false;
        return true;
    }

    /// <summary><c>stream_property(?Stream, ?Property)</c> — ISO §8.11.8.2.
    /// Enumerates (Stream, Property) pairs for every registered stream.
    /// Properties: <c>file_name(F)</c>, <c>mode(M)</c>,
    /// <c>alias(A)</c>, <c>input</c>, <c>output</c>,
    /// <c>end_of_stream(at|not)</c>.</summary>
    /// <summary>Property names stream_property/2 recognises — a BOUND
    /// second argument outside this set is domain_error(stream_property).</summary>
    private static readonly HashSet<string> KnownStreamProperties = new()
    {
        "file_name", "mode", "alias", "input", "output", "end_of_stream",
        "position", "type", "reposition", "eof_action", "encoding", "bom",
    };

    public static bool StreamProperty(Activation engine)
    {
        var registry = engine.Streams
            ?? throw new InvalidOperationException("Activation has no stream registry.");
        // §8.11.8.3: a bound first argument that is not a stream term is
        // domain_error(stream, S); a bound property outside the recognised
        // set is domain_error(stream_property, P).
        Term sArg = MaterializeRegister(engine, 0);
        if (sArg is not VarTerm
            && sArg is not CompoundTerm { Functor: "$stream", Args.Length: 1 })
            throw new ShumwayPrologException(IsoError.DomainError("stream", sArg));
        Term pArg = MaterializeRegister(engine, 1);
        bool knownProp = pArg switch
        {
            VarTerm => true,
            AtomTerm a => a.Name is "input" or "output",
            CompoundTerm { Args.Length: 1 } c => KnownStreamProperties.Contains(c.Functor),
            _ => false,
        };
        if (!knownProp)
            throw new ShumwayPrologException(IsoError.DomainError("stream_property", pArg));
        // Bound-argument filtering: with the stream and/or the property
        // functor known, only matching pairs enter the enumeration — a
        // ground query like stream_property(S, mode(M)) is DETERMINISTIC
        // (the SICStus conformity tests assert no choice point is left).
        Shumway.Core.StreamHandle? onlyStream = null;
        if (sArg is CompoundTerm { Functor: "$stream", Args: [IntTerm sid] })
        {
            onlyStream = registry.GetById((int)sid.Value);
            if (onlyStream is null)
                throw new ShumwayPrologException(IsoError.ExistenceError("stream", sArg));
        }
        string? onlyProp = pArg switch
        {
            AtomTerm pa => pa.Name,
            CompoundTerm pc => pc.Functor,
            _ => null,
        };
        bool Want(string name) => onlyProp is null || onlyProp == name;
        var pairs = new List<(Shumway.Core.StreamHandle Handle, Term Property)>();
        foreach (var h in registry.All())
        {
            if (onlyStream is not null && !ReferenceEquals(h, onlyStream)) continue;
            if (Want("file_name") && h.Filename is string fn)
                pairs.Add((h, new CompoundTerm("file_name",
                    new Term[] { new AtomTerm(Shumway.Core.PrologPath.ToCanonical(fn)) })));
            if (Want("mode"))
                pairs.Add((h, new CompoundTerm("mode", new Term[] { new AtomTerm(h.Mode) })));
            if (Want("alias") && h.Alias is string al)
                pairs.Add((h, new CompoundTerm("alias", new Term[] { new AtomTerm(al) })));
            if (Want("encoding") && h.EncodingName is string en)
                pairs.Add((h, new CompoundTerm("encoding", new Term[] { new AtomTerm(en) })));
            if (Want("bom") && h.HadBom is bool hb)
                pairs.Add((h, new CompoundTerm("bom",
                    new Term[] { new AtomTerm(hb ? "true" : "false") })));
            if (h.IsReader ? Want("input") : Want("output"))
                pairs.Add((h, h.IsReader ? (Term)new AtomTerm("input") : new AtomTerm("output")));
            if (h.IsReader && Want("end_of_stream"))
            {
                // §8.11.8: not / at / past. A binary reader has no
                // TextReader — probe the seekable stream instead.
                string state = h.PastEof ? "past"
                    : ReferenceEquals(h, registry.UserInput) ? "not"
                    : h.Reader is not null ? (h.Reader.Peek() >= 0 ? "not" : "at")
                    : h.BinaryStream is { CanSeek: true } bs
                        ? (bs.Position < bs.Length ? "not" : "at")
                        : "not";
                pairs.Add((h, new CompoundTerm("end_of_stream",
                    new Term[] { new AtomTerm(state) })));
            }
            // position/1 — seekable position when the underlying .NET
            // stream has one; otherwise the chars-consumed count of a
            // tracking reader still IS a stream position (user_input has
            // one this way — GNU and SWI report one for stdin too), it
            // just cannot be repositioned to.
            long? pos = TryGetStreamPosition(h);
            long? propPos = pos
                ?? (h.Reader is Shumway.Core.PositionTrackingReader tr
                    ? tr.CharsConsumed : null);
            if (Want("position") && propPos.HasValue)
                pairs.Add((h, new CompoundTerm("position",
                    new Term[] { new IntTerm(propPos.Value) })));
            if (Want("type"))
                pairs.Add((h, new CompoundTerm("type",
                    new Term[] { new AtomTerm(h.IsBinary ? "binary" : "text") })));
            if (Want("reposition"))
                pairs.Add((h, new CompoundTerm("reposition",
                    new Term[] { new AtomTerm(
                        pos.HasValue && h.Repositionable ? "true" : "false") })));
            if (h.IsReader && Want("eof_action"))
                pairs.Add((h, new CompoundTerm("eof_action",
                    new Term[] { new AtomTerm(h.EofAction) })));
        }
        // The name filter above still leaves one candidate per stream for a
        // property like `alias(user_output)`. Compare the GROUND arguments too,
        // so the cursor enumerates SOLUTIONS and a fully-specified query is
        // deterministic.
        if (pArg is CompoundTerm pWanted)
            pairs.RemoveAll(pr => !PropertyArgsCanMatch(pr.Property, pWanted));

        int returnPc = engine.BuiltinReturnPc;
        var pairArr = pairs.ToArray();
        return IndexEnumCursor.Start(engine, pairArr.Length, 2, returnPc,  // arity 2 (stream_property/2)
            (e, i) => StreamPropertyUnify(e, pairArr, i));
    }

    /// <summary>Cheap pre-unification filter: could this candidate property
    /// unify with the bound one? Only GROUND atom / integer arguments are
    /// compared — anything else is left for the real unification.</summary>
    private static bool PropertyArgsCanMatch(Term candidate, CompoundTerm wanted)
    {
        if (candidate is not CompoundTerm c
            || c.Functor != wanted.Functor
            || c.Args.Length != wanted.Args.Length)
            return false;
        for (int i = 0; i < c.Args.Length; i++)
        {
            switch (c.Args[i], wanted.Args[i])
            {
                case (AtomTerm a, AtomTerm b) when a.Name != b.Name: return false;
                case (IntTerm a, IntTerm b) when a.Value != b.Value: return false;
                case (AtomTerm, IntTerm):
                case (IntTerm, AtomTerm): return false;
            }
        }
        return true;
    }

    private static bool StreamPropertyUnify(
        Activation engine, (Shumway.Core.StreamHandle Handle, Term Property)[] pairs, int idx)
    {
        var (h, prop) = pairs[idx];
        Cell streamCell = Shumway.Builtins.StreamBuiltins.MakeStreamTerm(engine, h);
        Cell propCell = Materializer.MaterializeAsCell(engine, prop);

        if (!engine.UnifyRegisterWithCell(0, streamCell)) return false;
        if (!engine.UnifyRegisterWithCell(1, propCell)) return false;
        return true;
    }

    /// <summary>Returns the stream's logical position when the stream is
    /// repositionable, or null otherwise (e.g. console-backed
    /// user_input / user_output). For a text read stream this is the
    /// <em>characters consumed</em> (tracked by
    /// <see cref="Shumway.Core.PositionTrackingReader"/> — the raw
    /// <c>BaseStream.Position</c> over-reports by the StreamReader's
    /// read-ahead buffer, e.g. a <c>Peek()</c> for the
    /// <c>end_of_stream</c> property buffers the whole file); for binary
    /// and write streams it is the byte position. Used both for the
    /// <c>position(N)</c> property of <c>stream_property/2</c> and as
    /// the seekable-stream check for <c>set_stream_position/2</c>.
    ///</summary>
    private static long? TryGetStreamPosition(Shumway.Core.StreamHandle h)
    {
        try
        {
            // Binary stream first — its raw .NET Stream is the
            // authoritative position source.
            if (h.BinaryStream is System.IO.Stream bs)
                return bs.CanSeek ? bs.Position : null;
            if (h.Reader is Shumway.Core.PositionTrackingReader ptr)
                return (ptr.Inner is System.IO.StreamReader sr && sr.BaseStream.CanSeek)
                    || (ptr.Inner is Shumway.Core.Utf8TextReader ur && ur.BaseStream.CanSeek)
                    ? ptr.CharsConsumed
                    : null;
            if (h.Writer is System.IO.StreamWriter sw)
            {
                if (!sw.BaseStream.CanSeek) return null;
                sw.Flush();   // buffered chars must count toward the position
                return sw.BaseStream.Position;
            }
        }
        catch (NotSupportedException) { /* fall through */ }
        catch (ObjectDisposedException) { /* fall through */ }
        return null;
    }

    /// <summary><c>set_stream_position(+Stream, +Position)</c> — ISO
    /// §8.11.10. Position is an integer matching what
    /// <c>stream_property(_, position(N))</c> yields: characters
    /// consumed for a text read stream, byte offset for binary and
    /// write streams. A text read stream repositions by rewinding the
    /// base stream, discarding the StreamReader's read-ahead buffer,
    /// and re-consuming N characters — O(N), but exact for any
    /// encoding.</summary>
    public static bool SetStreamPosition(Activation engine)
    {
        var h = Shumway.Builtins.StreamBuiltins.ResolveStream(
            engine, engine.GetRegister(0));
        Cell posCell = MaterializeRegisterAsCell(engine, 1);
        if (posCell.Tag == Tag.Ref)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (posCell.Tag != Tag.Int)
            throw new Shumway.Core.PrologRuntimeException(
                "domain_error", "stream_position", engine, posCell);
        long target = posCell.AsInt;

        // §8.11.7.3: `open(…, [reposition(false)])` refuses the seek even when
        // the underlying stream could do it.
        if (!h.Repositionable)
            throw new Shumway.Core.PrologRuntimeException(
                "permission_error", "reposition,stream", engine,
                Shumway.Builtins.StreamBuiltins.MakeStreamTerm(engine, h));

        // Text read stream — reposition through the char-count tracker.
        if (h.Reader is Shumway.Core.PositionTrackingReader ptr)
        {
            bool rewound = false;
            if (ptr.Inner is System.IO.StreamReader sr && sr.BaseStream.CanSeek)
            {
                sr.BaseStream.Position = 0;
                sr.DiscardBufferedData();
                rewound = true;
            }
            else if (ptr.Inner is Shumway.Core.Utf8TextReader ur && ur.BaseStream.CanSeek)
            {
                ur.Rewind();
                rewound = true;
            }
            if (rewound)
            {
                ptr.ResetCount();
                for (long i = 0; i < target; i++)
                    if (ptr.Read() < 0) break;   // EOF before target — clamp
                return true;
            }
        }

        System.IO.Stream? baseStream = h.BinaryStream
            ?? (h.Writer is System.IO.StreamWriter sw ? sw.BaseStream : null);
        if (baseStream is null || !baseStream.CanSeek)
            throw new Shumway.Core.PrologRuntimeException(
                "permission_error", "reposition,stream");

        // Writer needs flush before the seek so any buffered output
        // lands at the *current* position rather than the new one.
        if (h.Writer is System.IO.StreamWriter w) w.Flush();
        baseStream.Position = target;
        return true;
    }

    private static Cell MaterializeRegisterAsCell(Activation engine, int reg)
    {
        Cell c = engine.GetRegister(reg);
        if (c.Tag != Tag.Ref) return c;
        return engine.GetHeap(engine.Deref(c.AsHeapIndex));
    }

    /// <summary><c>read_term_from_stream(Stream, Term)</c> — reads
    /// characters from a read-mode stream until it sees a clause-ending
    /// <c>.</c> followed by whitespace or EOF, parses the buffer as a
    /// Prolog term, and unifies the result with <c>Term</c>. Hits EOF
    /// before any text yields the atom <c>end_of_file</c>.</summary>
    /// <summary>read_term/2 has TWO readings: ISO §8.14.1's
    /// <c>read_term(-Term, +Options)</c> over current input, and the
    /// stream-first <c>read_term(+Stream, -Term)</c> Shumway also accepts.
    /// A LIST (or <c>[]</c>) in argument 2 selects the ISO form — a stream
    /// term is never a list, so the two never collide.</summary>
    public static bool ReadTermFromStream(Activation engine)
    {
        Cell arg1 = ResolveLocal(engine, engine.GetRegister(0));
        Cell arg2 = ResolveLocal(engine, engine.GetRegister(1));
        // A list in argument 2, or an argument 1 that cannot be a stream
        // (unbound — the term to read), selects the ISO reading.
        bool arg1CouldBeStream =
            (arg1.Tag == Tag.Str
             && FunctorTable.Lookup(engine.GetHeap(arg1.AsHeapIndex).AsFunctorId)
                is var (said, sar)
             && sar == 1 && AtomTable.GetById(said)?.Name == "$stream")
            || (arg1.Tag == Tag.Atom
                && engine.Streams?.GetByAlias(
                    AtomTable.GetById(arg1.AsAtomId)?.Name ?? "") is not null);
        bool isoForm = arg2.Tag == Tag.Lis
            || (arg2.Tag == Tag.Atom && arg2.AsAtomId == AtomTable.EmptyListId)
            || arg2.Tag == Tag.Pstr
            || !arg1CouldBeStream;
        if (isoForm)
        {
            var input = engine.Streams?.CurrentInput
                ?? throw new InvalidOperationException("Activation has no stream registry.");
            return ReadTermWithOptionsCore(
                engine, ResolveTextReaderFromHandle(input), termReg: 0, optReg: 1);
        }
        return ReadOneTermInto(engine,
            ResolveTextReader(engine, engine.GetRegister(0)), regOut: 1);
    }

    /// <summary><c>read/1</c> — ISO §8.14.2. Reads one term from the
    /// current input stream.</summary>
    public static bool Read1(Activation engine)
    {
        var h = engine.Streams?.CurrentInput
            ?? throw new InvalidOperationException("Activation has no stream registry.");
        return ReadOneTermInto(engine, ResolveTextReaderFromHandle(h), regOut: 0);
    }

    /// <summary><c>read(+Stream, -Term)</c> — ISO §8.14.2.</summary>
    public static bool Read2(Activation engine) =>
        ReadOneTermInto(engine,
            ResolveTextReader(engine, engine.GetRegister(0)), regOut: 1);

    private static System.IO.TextReader ResolveTextReader(Activation engine, Cell streamArg)
    {
        var h = Shumway.Builtins.StreamBuiltins.ResolveStream(engine, streamArg);
        // §8.14.1.3: reading from an output stream is
        // permission_error(input, stream, S) — with the stream-or-alias
        // ARGUMENT as culprit; a binary one is permission_error(input,
        // binary_stream, S).
        if (!h.IsReader)
            throw new Shumway.Core.PrologRuntimeException(
                "permission_error", "input,stream", engine, ResolveLocal(engine, streamArg));
        if (h.IsBinary)
            throw new Shumway.Core.PrologRuntimeException(
                "permission_error", "input,binary_stream",
                engine, ResolveLocal(engine, streamArg));
        return ResolveTextReaderFromHandle(h);
    }

    /// <summary>mirror of <see cref="ResolveTextReader"/>
    /// for write-mode streams. Used by <c>portray_clause/2</c>.</summary>
    private static System.IO.TextWriter ResolveTextWriter(Activation engine, Cell streamArg)
    {
        var h = Shumway.Builtins.StreamBuiltins.ResolveStream(engine, streamArg);
        if (!h.IsWriter)
            throw new PrologRuntimeException("permission_error", "output,stream");
        if (h.IsBinary)
            throw new PrologRuntimeException("permission_error", "output,binary_stream");
        return h.Writer!;
    }

    private static System.IO.TextReader ResolveTextReaderFromHandle(Shumway.Core.StreamHandle h)
    {
        if (!h.IsReader)
            throw new PrologRuntimeException("permission_error", "input,stream");
        if (h.IsBinary)
            // ISO §8.14.2.3.g — text-term read on a binary stream.
            throw new PrologRuntimeException("permission_error", "input,binary_stream");
        return h.Reader!;
    }

    /// <summary>Marks the handle past-eof after a term read consumed the
    /// end of input — <c>read(X)</c> yielding end_of_file leaves
    /// <c>end_of_stream(past)</c> (§8.11.8), like get_char does.</summary>
    private static void MarkPastEof(Activation engine, System.IO.TextReader reader)
    {
        if (engine.Streams is not { } reg) return;
        foreach (var h in reg.All())
            if (ReferenceEquals(h.Reader, reader)) { h.PastEof = true; return; }
    }

    /// <summary>The §8.11 past-end discipline for the TERM readers, mirroring
    /// what get_char and friends already do: a read on a stream that is
    /// already past end-of-stream raises permission_error(input,
    /// past_end_of_stream, S) under eof_action(error) — the ISO default for
    /// opened streams.</summary>
    private static void CheckPastEofByReader(Activation engine, System.IO.TextReader reader)
    {
        if (engine.Streams is not { } reg) return;
        foreach (var h in reg.All())
            if (ReferenceEquals(h.Reader, reader))
            {
                Shumway.Builtins.StreamBuiltins.CheckPastEof(engine, h);
                return;
            }
    }

    private static bool ReadOneTermInto(Activation engine, System.IO.TextReader reader, int regOut)
    {
        CheckPastEofByReader(engine, reader);
        Term? parsed = ParseOneTerm(engine, reader);
        if (parsed is null)
        {
            MarkPastEof(engine, reader);
            int eofId = AtomTable.Intern("end_of_file", permanent: true).Id;
            return engine.UnifyRegisterWithCell(regOut, Cell.Atom(eofId));
        }
        Cell cell = Materializer.MaterializeAsCell(engine, parsed);
        return engine.UnifyRegisterWithCell(regOut, cell);
    }

    /// <summary>Accumulates the source text of one clause from
    /// <paramref name="reader"/> (see <see cref="SentenceScanner"/>) and parses
    /// it with the engine's live operator table. Returns the parsed AST, or
    /// <c>null</c> when only layout/comments remained before end-of-file (the
    /// <c>end_of_file</c> case).</summary>
    private static Term? ParseOneTerm(Activation engine, System.IO.TextReader reader)
    {
        string? text = SentenceScanner.ReadSentenceText(reader, out _);
        if (text is null) return null;
        // Parse with the engine's LIVE operator table, not the static
        // default: a runtime `op/3` (e.g. the classic `op(200, fy, ['#'])`
        // before reading a spec file) must be in force for read/1,2, exactly
        // as it already is for consult and string_term/2.
        return ParseClauseText(engine, text);
    }

    /// <summary><c>read_term(+Stream, -Term, +Options)</c> — ISO §8.14.1. Reads
    /// one term and honours the read options <c>variable_names/1</c>,
    /// <c>singletons/1</c> and <c>variables/1</c> (other options — e.g.
    /// <c>double_quotes/1</c> — are ignored). Getting these bound to proper
    /// lists is not cosmetic: a source loader that reads with
    /// <c>singletons(S)</c> and then walks <c>S</c> with <c>member/2</c> loops
    /// forever if <c>S</c> is left unbound (Logtalk's linter does exactly this).
    /// The option-list variables share the returned term's variable cells
    /// (built via a wrapper compound so the materializer ties same-named
    /// variables together).</summary>
    /// <summary>Read options the reader recognises. A bound element outside
    /// this set is domain_error(read_option, Element) — §8.14.1.3.</summary>
    private static readonly HashSet<string> KnownReadOptions = new()
    {
        "variable_names", "singletons", "variables", "syntax_errors",
        "term_position", "subterm_positions", "double_quotes", "cycles",
        "backquoted_string", "character_escapes", "module", "comments",
    };

    /// <summary>§8.14.1.3 validation of read_term's option list, before any
    /// input is consumed: unbound list or element → instantiation_error;
    /// improper/non-list → type_error(list, WholeList); an element that is
    /// not a recognised Name(Arg) → domain_error(read_option, Element).</summary>
    private static void ValidateReadOptions(Activation engine, int optReg)
    {
        Term listTerm = MaterializeRegister(engine, optReg);
        Cell node = ResolveLocal(engine, engine.GetRegister(optReg));
        while (true)
        {
            if (node.Tag is Tag.Ref or Tag.AttVar)
                throw new ShumwayPrologException(IsoError.InstantiationError());
            if (node.Tag == Tag.Atom && node.AsAtomId == AtomTable.EmptyListId) return;
            if (node.Tag != Tag.Lis)
                throw new ShumwayPrologException(IsoError.TypeError("list", listTerm));
            int hb = node.AsHeapIndex;
            Cell head = ResolveLocal(engine, engine.GetHeap(hb));
            if (head.Tag is Tag.Ref or Tag.AttVar)
                throw new ShumwayPrologException(IsoError.InstantiationError());
            // Only the option NAME is validated here. The list-valued options
            // are OUTPUT arguments: their value unifies after the read, so
            // read_term(T, [singletons(1)]) on "a." FAILS (Cor.3, Neumerkel
            // cases 47-49/69-70) — it is not a domain_error, and a syntax
            // error in the input still surfaces first.
            bool ok = false;
            if (head.Tag == Tag.Str)
            {
                var (aid, ar) = FunctorTable.Lookup(
                    engine.GetHeap(head.AsHeapIndex).AsFunctorId);
                ok = ar == 1
                    && KnownReadOptions.Contains(AtomTable.GetById(aid)?.Name ?? "");
            }
            if (!ok)
            {
                Term culprit = engine.MaterializeCellToTerm is { } mat
                    && mat(head) is Term ht ? ht : new VarTerm("_");
                throw new ShumwayPrologException(
                    IsoError.DomainError("read_option", culprit));
            }
            node = ResolveLocal(engine, engine.GetHeap(hb + 1));
        }
    }

    public static bool ReadTermWithOptions(Activation engine)
    {
        ValidateReadOptions(engine, 2);
        return ReadTermWithOptionsCore(
            engine, ResolveTextReader(engine, engine.GetRegister(0)),
            termReg: 1, optReg: 2);
    }

    private static bool ReadTermWithOptionsCore(
        Activation engine, System.IO.TextReader reader, int termReg, int optReg)
    {
        ValidateReadOptions(engine, optReg);
        CheckPastEofByReader(engine, reader);
        Term? parsed = ParseOneTerm(engine, reader);
        if (parsed is null)
        {
            MarkPastEof(engine, reader);
            int eofId = AtomTable.Intern("end_of_file", permanent: true).Id;
            if (!engine.UnifyRegisterWithCell(termReg, Cell.Atom(eofId))) return false;
            // ISO: at end_of_file the read options unify with the empty list.
            int nilSlot = engine.AllocateHeap(1);
            engine.SetHeap(nilSlot, Cell.Atom(AtomTable.EmptyListId));
            return UnifyReadOptions(engine, optReg, nilSlot, nilSlot, nilSlot);
        }

        // Named (non-"_") variables in first-appearance order, with occurrence
        // counts (singletons = named vars seen exactly once, name not starting
        // with '_').
        var order = new List<string>();
        var counts = new Dictionary<string, int>();
        CollectNamedVars(parsed, order, counts);

        // Wrap the term with the option-value lists so a single materialize
        // call binds each `Name = VarTerm(Name)` pair to the SAME heap variable
        // the term uses (VarTerm equality is by name; the materializer shares
        // named vars within one call).
        Term vnList = new AtomTerm("[]");
        Term singList = new AtomTerm("[]");
        for (int i = order.Count - 1; i >= 0; i--)
        {
            string nm = order[i];
            Term pair = new CompoundTerm("=",
                new Term[] { new AtomTerm(nm), new VarTerm(nm) });
            vnList = new CompoundTerm(".", new Term[] { pair, vnList });
            // `_X` IS a singleton for read_term/2,3: only `_` itself is
            // anonymous. (The compiler's singleton WARNING is the place where
            // a leading underscore means "deliberately unused" — not here.)
            if (counts[nm] == 1)
                singList = new CompoundTerm(".", new Term[] { pair, singList });
        }

        var wrapper = new CompoundTerm("$rt",
            new Term[] { parsed, vnList, singList });
        Cell wcell = Materializer.MaterializeAsCell(engine, wrapper);
        // MaterializeAsCell returns a REF to the STR block; deref to the Str
        // cell, whose AsHeapIndex is the functor slot with args right after it
        // (heap[functorIdx] = functor id; heap[functorIdx + 1 + i] = arg i).
        Cell strCell = ResolveLocal(engine, wcell);
        int functorIdx = strCell.AsHeapIndex;
        int parsedIdx = functorIdx + 1, vnIdx = functorIdx + 2, singIdx = functorIdx + 3;

        if (!engine.UnifyRegisterWithCell(termReg, engine.GetHeap(parsedIdx)))
            return false;

        // variables/1: every distinct variable in the term, first-appearance
        // order (includes anonymous variables, unlike variable_names).
        var visited = new HashSet<int>();
        var vars = new List<int>();
        CollectVars(engine, parsedIdx, visited, vars);
        Cell varsCell = Cell.Atom(AtomTable.EmptyListId);
        for (int i = vars.Count - 1; i >= 0; i--)
        {
            int b = engine.AllocateHeap(2);
            engine.SetHeap(b, Cell.Ref(vars[i]));
            engine.SetHeap(b + 1, varsCell);
            varsCell = Cell.Lis(b);
        }
        int varsIdx = engine.AllocateHeap(1);
        engine.SetHeap(varsIdx, varsCell);

        return UnifyReadOptions(engine, optReg, vnIdx, singIdx, varsIdx);
    }

    /// <summary>Collects the named (non-anonymous) variable names of an AST in
    /// first-appearance order, counting occurrences of each.
    ///
    /// <para>Iterative over an explicit work list: a read term is user data of
    /// any depth, and recursion overflowed the C# stack — which kills the
    /// process, not the read — at some ten thousand list elements. Arguments
    /// are pushed right to left, so they pop in source order and the names
    /// come out in first-appearance order.</para></summary>
    private static void CollectNamedVars(
        Term root, List<string> order, Dictionary<string, int> counts)
    {
        var work = new List<Term>(32) { root };
        while (work.Count > 0)
        {
            Term t = work[^1];
            work.RemoveAt(work.Count - 1);
            switch (t)
            {
                case VarTerm v when v.Name != "_":
                    if (counts.TryGetValue(v.Name, out int n)) counts[v.Name] = n + 1;
                    else { order.Add(v.Name); counts[v.Name] = 1; }
                    break;
                case CompoundTerm c:
                    for (int i = c.Args.Length - 1; i >= 0; i--) work.Add(c.Args[i]);
                    break;
            }
        }
    }

    /// <summary>Walks a <c>read_term/3</c> options list (in register
    /// <paramref name="optReg"/>) and unifies the argument of each
    /// <c>variable_names/1</c> / <c>singletons/1</c> / <c>variables/1</c> option
    /// with the value at the corresponding heap index. Unknown options are
    /// skipped; a partial or non-list tail simply ends the walk.</summary>
    private static bool UnifyReadOptions(
        Activation engine, int optReg, int vnIdx, int singIdx, int varsIdx)
    {
        Cell node = ResolveLocal(engine, engine.GetRegister(optReg));
        while (node.Tag == Tag.Lis)
        {
            int hb = node.AsHeapIndex;                 // heap[hb]=head, heap[hb+1]=tail
            Cell head = ResolveLocal(engine, engine.GetHeap(hb));
            if (head.Tag == Tag.Str)
            {
                int sfx = head.AsHeapIndex;            // heap[sfx]=functor id
                var (aid, ar) = FunctorTable.Lookup(engine.GetHeap(sfx).AsFunctorId);
                if (ar == 1)
                {
                    string nm = AtomTable.GetById(aid)?.Name ?? "";
                    int valIdx = nm switch
                    {
                        "variable_names" => vnIdx,
                        "singletons"     => singIdx,
                        "variables"      => varsIdx,
                        _                => -1,
                    };
                    if (valIdx >= 0 && !engine.Unify(sfx + 1, valIdx))
                        return false;
                }
            }
            node = ResolveLocal(engine, engine.GetHeap(hb + 1));
        }
        return true;
    }

    /// <summary>The engine's live operator table (runtime `op/3` additions
    /// included) — the table every term-READING builtin must parse with; the
    /// static default only as the bare-Activation-test fallback.</summary>
    private static Shumway.Compiler.Parsing.OperatorTable LiveOperators(Activation engine)
        => (engine.Host as PrologEngine)?.Operators
           ?? Shumway.Compiler.Parsing.OperatorTable.Default();

    /// <summary>The engine's live flags, for the same reason as
    /// <see cref="LiveOperators"/>: a runtime
    /// <c>set_prolog_flag(double_quotes, chars)</c> must govern how
    /// <c>read/1</c> parses a following <c>"..."</c> (Neumerkel #171 — the
    /// query parser honoured it, the read family did not).</summary>
    private static Shumway.Compiler.Parsing.PrologFlags LiveFlags(Activation engine)
        => (engine.Host as PrologEngine)?.Flags
           ?? new Shumway.Compiler.Parsing.PrologFlags();

    /// <summary>Parses one clause from <paramref name="source"/> with the live
    /// operator table and flags, converting a parser/lexer failure into a
    /// catchable ISO <c>syntax_error</c> instead of letting a raw .NET
    /// exception escape (ISO §7.10.3 — a malformed term read is a syntax
    /// error, catchable by the program).</summary>
    private static Term ParseClauseText(Activation engine, string source)
    {
        try
        {
            var parser = new Shumway.Compiler.Parsing.Parser(
                new Shumway.Compiler.Lexer.Lexer(source), LiveOperators(engine),
                LiveFlags(engine));
            return parser.ReadClauseTerm();
        }
        catch (Exception ex) when (ex is Shumway.Compiler.Parsing.ParseException
                                    or Shumway.Compiler.Lexer.LexerException)
        {
            if (ex is Shumway.Compiler.Parsing.ParseException { RepresentationFlaw: { } flaw })
                throw new Shumway.Core.PrologRuntimeException("representation_error", flaw);
            throw new Shumway.Core.PrologRuntimeException("syntax_error", ex.Message);
        }
    }

    /// <summary>The <see cref="Activation.NumberFromChars"/> hook: reads
    /// <paramref name="chars"/> as a full Prolog TERM (the ISO number_chars
    /// semantics) and, if it is a number, returns its boxed value
    /// (<see cref="long"/> / <see cref="double"/> /
    /// <see cref="System.Numerics.BigInteger"/>); otherwise <c>null</c>. Lets
    /// <c>AtomCharBuiltins</c> resolve the operator/quoting cases its token parser
    /// cannot — `'-'1` → -1, `- /**/1` → -1 — while a non-number (`'+'1` → +(1),
    /// `1+1`, an atom) yields null and stays a syntax error. A trailing space +
    /// dot terminate the clause (space so a graphic-atom tail can't fuse the dot).</summary>
    private static object? NumberFromCharsHook(Activation engine, string chars)
    {
        // number_chars is STRICTER than a clause read: the chars must be
        // exactly layout* + number, with NOTHING after — `"3 "`, `"3."`,
        // `"3% junk"` and `"0%0'"` are all syntax errors, though a clause
        // read would skip the trailing layout/comment or treat `.` as the
        // terminator (GNU agrees on every one). Enforced positionally: the
        // parse must succeed at EOF (rejects `"3."` — the Dot token
        // survives) AND the last real token must end exactly where the
        // string does (rejects any trailing layout or comment — parser
        // lookahead lexes past those to find EOF, so IsAtEnd alone cannot
        // see them). Layout and comments BEFORE or INSIDE the term stay
        // fine: `" 0"`, `"- /**/1"`.
        Term parsed;
        try
        {
            var lexer = new Shumway.Compiler.Lexer.Lexer(chars);
            var parser = new Shumway.Compiler.Parsing.Parser(
                lexer, LiveOperators(engine));
            parsed = parser.ReadTerm();
            if (!parser.IsAtEnd()) return null;   // trailing token → not a number
            if (lexer.LastTokenEndOffset != chars.Length)
                return null;                      // trailing layout/comment
        }
        catch (Shumway.Compiler.Parsing.ParseException ex)
        {
            // A perfect number that is merely above max_float must surface as
            // representation_error(max_float) (number_chars_cont 82), not be
            // swallowed into "not a number" → syntax_error(illegal_number).
            if (ex.RepresentationFlaw is { } flaw)
                throw new Shumway.Core.PrologRuntimeException("representation_error", flaw);
            return null;   // not parseable → not a number
        }
        catch (Shumway.Compiler.Lexer.LexerException)
        {
            return null;   // not tokenizable → not a number
        }
        return parsed switch
        {
            Shumway.Compiler.Ast.IntTerm it => it.Value,
            Shumway.Compiler.Ast.BigIntTerm bt => bt.Value,
            // The parser rejects infinite float tokens before building the
            // term, so a FloatTerm here is always finite; the guard stays as
            // a tripwire against a future producer handing back an infinity.
            Shumway.Compiler.Ast.FloatTerm ft =>
                double.IsFinite(ft.Value) ? (object)ft.Value : null,
            _ => null,
        };
    }

    /// <summary>Installs <see cref="NumberFromCharsHook"/> on
    /// <paramref name="engine"/> — called from query setup.</summary>
    internal static void WireNumberFromChars(Activation engine)
        => engine.NumberFromChars = s => NumberFromCharsHook(engine, s);

    /// <summary>Installs the <c>portray/1</c> hook write_term's
    /// <c>portrayed(true)</c> and print/1,2 call for every subterm. The user's
    /// portray/1 runs RE-ENTRANTLY on the live activation with current output
    /// redirected at <paramref name="engine"/>'s writer, so whatever it writes
    /// lands where the term would have. Succeeding means "I produced the
    /// output"; failing means "render it normally".</summary>
    internal static void WirePortrayHook(Activation engine, PrologEngine host)
        => engine.PortrayHook = (e, cell, output) => PortraySubterm(e, host, cell, output);

    private static readonly int _portrayFunctorId =
        FunctorTable.Intern(AtomTable.Intern("portray", permanent: true).Id, 1);

    private static bool PortraySubterm(
        Activation engine, PrologEngine host, Cell cell, System.IO.TextWriter output)
    {
        // No user portray/1 at all: the common case, and it must cost nothing.
        if (!host.HasPredicate(_portrayFunctorId)) return false;
        if (engine.ReentrantSolve is not { } solve) return false;

        // portray/1's argument is the subterm ITSELF — no copy: the hook is
        // allowed to inspect bindings, and copying would hide them.
        int goalBase = engine.AllocateHeap(2);
        engine.SetHeap(goalBase, Cell.Functor(_portrayFunctorId));
        engine.SetHeap(goalBase + 1, cell);
        Cell goal = Cell.Str(goalBase);

        // Whatever portray/1 writes has to land in THIS renderer's sink, which
        // is not necessarily current output (format_to_atom, with_output_to,
        // write_term to a stream).
        var registry = engine.Streams;
        StreamHandle? savedOut = registry?.CurrentOutput;
        var sink = new StreamHandle(-1, output, "write", null);
        try
        {
            registry?.SetCurrentOutput(sink);
            return solve(goal);
        }
        finally
        {
            if (savedOut is not null) registry?.SetCurrentOutput(savedOut);
        }
    }

    /// <summary><c>'$wot_begin'(Sink)</c> — the primitive under the prelude's
    /// <c>with_output_to/2</c>: validates the sink (<c>atom(_)</c> /
    /// <c>string(_)</c>) and redirects CURRENT OUTPUT — the stream registry's,
    /// which is where every write-family builtin actually writes — to an
    /// in-memory stream, remembering the handle it displaced. The GOAL then
    /// runs in the LIVE engine — the old C# implementation spawned a
    /// sub-engine, and every side effect the goal made (an <c>op/3</c>, an
    /// <c>assertz</c>, a flag) silently vanished with it.</summary>
    public static bool WotBegin(Activation engine)
    {
        ReadSink(engine, out _, out _);   // validate before touching anything
        var reg = engine.Streams
            ?? throw new InvalidOperationException(
                "with_output_to/2 requires the engine's stream registry.");
        // Memory capture is not a Windows text file: nl / ~n inside the goal
        // must contribute "\n" to the captured atom (GNU/SWI behaviour —
        // sub_atom(Captured, _, _, _, '\n') patterns rely on it), while file
        // streams keep the platform newline.
        var sw = new System.IO.StringWriter { NewLine = "\n" };
        var handle = reg.Add(new StreamHandle(
            reg.NextId(), sw, "write", filename: null, alias: null));
        var stack = WotStackOf(engine);
        stack.Push((reg.CurrentOutput, handle, sw, engine.Out));
        reg.SetCurrentOutput(handle);
        // Also swap Activation.Out: listing/1, portray_clause/1 and friends
        // write through it directly (registry-less fallback path), and their
        // output must be captured too.
        engine.Out = sw;
        return true;
    }

    private static System.Collections.Generic.Stack<
        (StreamHandle Prev, StreamHandle Mem, System.IO.StringWriter Sw,
         System.IO.TextWriter PrevOut)>
        WotStackOf(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "with_output_to/2 requires the engine to be hosted by a PrologEngine.");
        return host.WotStack ??= new();
    }

    /// <summary><c>'$wot_end'(Sink)</c> — pops the redirection installed by
    /// <c>'$wot_begin'</c> and unifies the sink's argument with the captured
    /// text (atom or string per the sink functor). Runs whether or not the
    /// goal succeeded — the SWI convention exposes the capture either way —
    /// so the prelude calls it from every arm of its catch.</summary>
    public static bool WotEnd(Activation engine)
    {
        var reg = engine.Streams
            ?? throw new InvalidOperationException(
                "with_output_to/2 requires the engine's stream registry.");
        var stack = WotStackOf(engine);
        if (stack.Count == 0)
            throw new InvalidOperationException(
                "'$wot_end' without a matching '$wot_begin'.");
        var (prev, mem, sw, prevOut) = stack.Pop();
        reg.SetCurrentOutput(prev);
        reg.Remove(mem);
        engine.Out = prevOut;
        string captured = sw.ToString();

        ReadSink(engine, out string sinkType, out int functorIdx);
        int sinkArgAddr = functorIdx + 1;
        if (sinkType == "atom")
        {
            int aid = AtomTable.Intern(captured, permanent: false).Id;
            int slot = engine.AllocateHeap(1);
            engine.SetHeap(slot, Cell.Atom(aid));
            return engine.Unify(sinkArgAddr, slot);
        }
        else // string
        {
            // `string(S)` is the SWI sink, and double_quotes=string is a
            // compatibility alias for chars (ADR-047 decision 5).
            int pstrIdx = engine.MakePstr(captured, TextKind.Chars);
            int slot = engine.AllocateHeap(1);
            engine.SetHeap(slot, Cell.Ref(pstrIdx));
            return engine.Unify(sinkArgAddr, slot);
        }
    }

    private static void ReadSink(Activation engine, out string sinkType, out int functorIdx)
    {
        Cell sinkCell = ResolveLocal(engine, engine.GetRegister(0));
        if (sinkCell.Tag != Tag.Str)
            throw new ShumwayPrologException(
                IsoError.TypeError("output_sink_spec", new VarTerm("_")));
        functorIdx = sinkCell.AsHeapIndex;
        var (atomId, arity) = FunctorTable.Lookup(
            engine.GetHeap(functorIdx).AsFunctorId);
        sinkType = AtomTable.GetById(atomId)?.Name ?? "";
        if (arity != 1 || (sinkType != "atom" && sinkType != "string"))
            throw new ShumwayPrologException(
                IsoError.DomainError("output_sink", new VarTerm("_")));
    }

}
