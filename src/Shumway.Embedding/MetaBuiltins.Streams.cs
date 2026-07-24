using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

public static partial class MetaBuiltins
{
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
        string fnText = h.Filename ?? h.Alias ?? "";
        Cell fnCell = Cell.Atom(AtomTable.Intern(fnText, permanent: false).Id);
        Cell modeCell = Cell.Atom(AtomTable.Intern(h.Mode, permanent: true).Id);
        Cell streamCell = engine.MakeForeign(h);

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
    public static bool StreamProperty(Activation engine)
    {
        var registry = engine.Streams
            ?? throw new InvalidOperationException("Activation has no stream registry.");
        var pairs = new List<(Shumway.Core.StreamHandle Handle, Term Property)>();
        foreach (var h in registry.All())
        {
            if (h.Filename is string fn)
                pairs.Add((h, new CompoundTerm("file_name", new Term[] { new AtomTerm(fn) })));
            pairs.Add((h, new CompoundTerm("mode", new Term[] { new AtomTerm(h.Mode) })));
            if (h.Alias is string al)
                pairs.Add((h, new CompoundTerm("alias", new Term[] { new AtomTerm(al) })));
            pairs.Add((h, h.IsReader ? (Term)new AtomTerm("input") : new AtomTerm("output")));
            if (h.IsReader)
            {
                string state = (ReferenceEquals(h, registry.UserInput)
                                || h.Reader!.Peek() >= 0)
                    ? "not" : "at";
                pairs.Add((h, new CompoundTerm("end_of_stream",
                    new Term[] { new AtomTerm(state) })));
            }
            // position/1 — present when the underlying
            // .NET stream is seekable. user_input / user_output
            // (console-backed) aren't.
            long? pos = TryGetStreamPosition(h);
            if (pos.HasValue)
                pairs.Add((h, new CompoundTerm("position",
                    new Term[] { new IntTerm(pos.Value) })));
        }
        int returnPc = engine.BuiltinReturnPc;
        var pairArr = pairs.ToArray();
        return IndexEnumCursor.Start(engine, pairArr.Length, 2, returnPc,  // arity 2 (stream_property/2)
            (e, i) => StreamPropertyUnify(e, pairArr, i));
    }

    private static bool StreamPropertyUnify(
        Activation engine, (Shumway.Core.StreamHandle Handle, Term Property)[] pairs, int idx)
    {
        var (h, prop) = pairs[idx];
        Cell streamCell = engine.MakeForeign(h);
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
                return ptr.Inner is System.IO.StreamReader sr && sr.BaseStream.CanSeek
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
            throw new Shumway.Core.PrologRuntimeException("domain_error", "stream_position");
        long target = posCell.AsInt;

        // Text read stream — reposition through the char-count tracker.
        if (h.Reader is Shumway.Core.PositionTrackingReader ptr
            && ptr.Inner is System.IO.StreamReader sr
            && sr.BaseStream.CanSeek)
        {
            sr.BaseStream.Position = 0;
            sr.DiscardBufferedData();
            ptr.ResetCount();
            for (long i = 0; i < target; i++)
                if (ptr.Read() < 0) break;   // EOF before target — clamp
            return true;
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
    public static bool ReadTermFromStream(Activation engine) =>
        ReadOneTermInto(engine,
            ResolveTextReader(engine, engine.GetRegister(0)), regOut: 1);

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

    /// <summary>ISO graphic (symbol) chars — a run of these forms one symbol
    /// atom token, so a '.' preceded by one is part of that token
    /// (<c>=..</c>, <c>:-.</c>-less edge shapes) and never ends the clause.</summary>
    private static bool IsGraphicChar(char c) => c is '#' or '$' or '&' or '*' or '+'
        or '-' or '.' or '/' or ':' or '<' or '=' or '>' or '?' or '@' or '^' or '~' or '\\';

    private static bool ReadOneTermInto(Activation engine, System.IO.TextReader reader, int regOut)
    {
        Term? parsed = ParseOneTerm(engine, reader);
        if (parsed is null)
        {
            int eofId = AtomTable.Intern("end_of_file", permanent: true).Id;
            return engine.UnifyRegisterWithCell(regOut, Cell.Atom(eofId));
        }
        Cell cell = Materializer.MaterializeAsCell(engine, parsed);
        return engine.UnifyRegisterWithCell(regOut, cell);
    }

    /// <summary>Accumulates the source text of one clause from
    /// <paramref name="reader"/> up to its terminating solo <c>.</c> and parses
    /// it with the engine's live operator table. Returns the parsed AST, or
    /// <c>null</c> when only layout/comments remained before end-of-file (the
    /// <c>end_of_file</c> case).</summary>
    private static Term? ParseOneTerm(Activation engine, System.IO.TextReader reader)
    {
        // the old accumulation rule was "stop
        // at any '.' followed by whitespace", which sliced `?X =.. ?Y` in
        // half at univ's second dot (and would equally mis-split a dot
        // inside a quoted atom, a string, or a comment). The end-of-clause
        // token is a SOLO '.' followed by layout/EOF, so track just enough
        // lexical state to recognise it: quoted contexts (' " `, with ''
        // doubling and \-escapes), % line comments, /* */ block comments,
        // 0'c char literals, and symbol runs (a '.' glued to a preceding
        // graphic char is part of that symbol atom, not the terminator).
        var sb = new System.Text.StringBuilder();
        char quote = '\0';            // inside 'x' / "x" / `x` when non-zero
        bool lineComment = false, blockComment = false;
        char prev = '\0';             // previous char in Normal state
        while (true)
        {
            int ci = reader.Read();
            if (ci < 0)
            {
                // end_of_file when nothing but layout/comments accumulated —
                // a trailing-whitespace file must yield end_of_file, not a
                // syntax error.
                if (IsLayoutOnly(sb))
                    return null;   // end_of_file — only layout/comments remained
                break;
            }
            char c = (char)ci;
            sb.Append(c);

            if (lineComment)
            {
                if (c == '\n') lineComment = false;
                continue;
            }
            if (blockComment)
            {
                if (c == '/' && prev == '*') { blockComment = false; prev = '\0'; }
                else prev = c;
                continue;
            }
            if (quote != '\0')
            {
                if (c == '\\' && prev != '\\') { prev = c; continue; }
                if (c == quote && prev != '\\')
                {
                    // '' doubling: peek — a second quote continues the token.
                    if (reader.Peek() == quote) { sb.Append((char)reader.Read()); prev = '\0'; continue; }
                    quote = '\0';
                }
                prev = c == '\\' && prev == '\\' ? '\0' : c;   // \\ consumes the escape
                continue;
            }

            switch (c)
            {
                case '%':
                    lineComment = true;
                    prev = '\0';
                    continue;
                case '*' when prev == '/':
                    blockComment = true;
                    prev = '\0';
                    continue;
                case '\'':
                    // 0'c char literal: consume the (possibly escaped) char raw.
                    if (char.IsDigit(prev))
                    {
                        int lit = reader.Read();
                        if (lit >= 0)
                        {
                            sb.Append((char)lit);
                            if ((char)lit == '\\')
                            {
                                int esc = reader.Read();   // escape body head
                                if (esc >= 0) sb.Append((char)esc);
                            }
                        }
                        prev = '\0';
                        continue;
                    }
                    quote = '\'';
                    prev = '\0';
                    continue;
                case '"':
                case '`':
                    quote = c;
                    prev = '\0';
                    continue;
                case '.':
                    // Solo dot + following layout/EOF = end of clause.
                    if (!IsGraphicChar(prev))
                    {
                        int next = reader.Peek();
                        if (next < 0 || char.IsWhiteSpace((char)next) || next == '%')
                            goto done;
                    }
                    prev = c;
                    continue;
                default:
                    prev = c;
                    continue;
            }
        }
        done: ;

        // parse with the engine's LIVE operator
        // table, not the static default: a runtime `op/3` (e.g. the classic
        // `op(200, fy, ['#', '?'])` before reading a spec file) must be in
        // force for read/1,2, exactly as it already is for consult and
        // string_term/2 (the E3 fix).
        return ParseClauseText(engine, sb.ToString());
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
    public static bool ReadTermWithOptions(Activation engine)
    {
        var reader = ResolveTextReader(engine, engine.GetRegister(0));
        Term? parsed = ParseOneTerm(engine, reader);
        if (parsed is null)
        {
            int eofId = AtomTable.Intern("end_of_file", permanent: true).Id;
            if (!engine.UnifyRegisterWithCell(1, Cell.Atom(eofId))) return false;
            // ISO: at end_of_file the read options unify with the empty list.
            int nilSlot = engine.AllocateHeap(1);
            engine.SetHeap(nilSlot, Cell.Atom(AtomTable.EmptyListId));
            return UnifyReadOptions(engine, 2, nilSlot, nilSlot, nilSlot);
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
            if (counts[nm] == 1 && !nm.StartsWith("_"))
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

        if (!engine.UnifyRegisterWithCell(1, engine.GetHeap(parsedIdx)))
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

        return UnifyReadOptions(engine, 2, vnIdx, singIdx, varsIdx);
    }

    /// <summary>Collects the named (non-anonymous) variable names of an AST in
    /// first-appearance order, counting occurrences of each.</summary>
    private static void CollectNamedVars(
        Term t, List<string> order, Dictionary<string, int> counts)
    {
        switch (t)
        {
            case VarTerm v when v.Name != "_":
                if (counts.TryGetValue(v.Name, out int n)) counts[v.Name] = n + 1;
                else { order.Add(v.Name); counts[v.Name] = 1; }
                break;
            case CompoundTerm c:
                // Walk the list spine iteratively so a long list literal does
                // not recurse once per element.
                Term cursor = c;
                while (cursor is CompoundTerm cc
                       && cc.Functor == "." && cc.Args.Length == 2)
                {
                    CollectNamedVars(cc.Args[0], order, counts);
                    cursor = cc.Args[1];
                }
                if (cursor is CompoundTerm rest && !(rest.Functor == "." && rest.Args.Length == 2))
                    foreach (var arg in rest.Args) CollectNamedVars(arg, order, counts);
                else if (cursor is not CompoundTerm)
                    CollectNamedVars(cursor, order, counts);
                break;
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

    /// <summary>Parses one clause from <paramref name="source"/> with the live
    /// operator table, converting a parser/lexer failure into a catchable ISO
    /// <c>syntax_error</c> instead of letting a raw .NET exception escape (ISO
    /// §7.10.3 — a malformed term read is a syntax error, catchable by the
    /// program).</summary>
    private static Term ParseClauseText(Activation engine, string source)
    {
        try
        {
            var parser = new Shumway.Compiler.Parsing.Parser(
                new Shumway.Compiler.Lexer.Lexer(source), LiveOperators(engine));
            return parser.ReadClauseTerm();
        }
        catch (Exception ex) when (ex is Shumway.Compiler.Parsing.ParseException
                                    or Shumway.Compiler.Lexer.LexerException)
        {
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
        // number_chars is STRICTER than a clause read: the chars must be exactly a
        // number, with no TRAILING content — `"3 "` and `"3."` are syntax errors,
        // though a clause read would skip the trailing space / treat `.` as the
        // terminator. So reject a trailing-whitespace tail, and read WITHOUT a dot
        // (ReadTerm) then require true EOF — a trailing `.` leaves a Dot token, so
        // IsAtEnd is false and `"3."` is rejected. (Leading layout is fine.)
        if (chars.Length != chars.TrimEnd().Length) return null;
        Term parsed;
        try
        {
            var parser = new Shumway.Compiler.Parsing.Parser(
                new Shumway.Compiler.Lexer.Lexer(chars), LiveOperators(engine));
            parsed = parser.ReadTerm();
            if (!parser.IsAtEnd()) return null;   // trailing junk → not a number
        }
        catch (Exception ex) when (ex is Shumway.Compiler.Parsing.ParseException
                                    or Shumway.Compiler.Lexer.LexerException)
        {
            return null;   // not parseable → not a number
        }
        return parsed switch
        {
            Shumway.Compiler.Ast.IntTerm it => it.Value,
            Shumway.Compiler.Ast.BigIntTerm bt => bt.Value,
            Shumway.Compiler.Ast.FloatTerm ft => ft.Value,
            _ => null,
        };
    }

    /// <summary>Installs <see cref="NumberFromCharsHook"/> on
    /// <paramref name="engine"/> — called from query setup.</summary>
    internal static void WireNumberFromChars(Activation engine)
        => engine.NumberFromChars = s => NumberFromCharsHook(engine, s);

    /// <summary>True when the accumulated read/1 text holds no term — only
    /// whitespace, <c>%</c> line comments, and <c>/* */</c> block comments.</summary>
    private static bool IsLayoutOnly(System.Text.StringBuilder sb)
    {
        int i = 0, n = sb.Length;
        while (i < n)
        {
            char ch = sb[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            if (ch == '%')
            {
                while (i < n && sb[i] != '\n') i++;
                continue;
            }
            if (ch == '/' && i + 1 < n && sb[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(sb[i] == '*' && sb[i + 1] == '/')) i++;
                i = System.Math.Min(n, i + 2);
                continue;
            }
            return false;
        }
        return true;
    }

    /// <summary><c>with_output_to(Sink, Goal)</c> — runs <c>Goal</c> with
    /// the engine's output sink temporarily redirected. It
    /// recognises <c>atom(A)</c> and <c>string(S)</c> sinks: both capture
    /// everything <c>Goal</c> writes (via <c>write/1</c>, <c>format/2</c>,
    /// etc.) and unify the result with their inner variable. The sub-
    /// engine spawned for <c>Goal</c> uses the redirected sink for the
    /// duration of the call; the parent's <see cref="PrologEngine.Out"/>
    /// is untouched.</summary>
    public static bool WithOutputTo(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "with_output_to/2 requires the engine to be hosted by a PrologEngine.");

        // Read the Sink term (X[0]) and the Goal term (X[1]).
        Cell sinkCell = ResolveLocal(engine, engine.GetRegister(0));
        if (sinkCell.Tag != Tag.Str)
            throw new ShumwayPrologException(
                IsoError.TypeError("output_sink_spec", new VarTerm("_")));
        int functorIdx = sinkCell.AsHeapIndex;
        var (atomId, arity) = FunctorTable.Lookup(
            engine.GetHeap(functorIdx).AsFunctorId);
        string sinkType = AtomTable.GetById(atomId)?.Name ?? "";
        if (arity != 1 || (sinkType != "atom" && sinkType != "string"))
            throw new ShumwayPrologException(
                IsoError.DomainError("output_sink", new VarTerm("_")));

        Term goal = MaterializeRegister(engine, 1);
        var sw = new System.IO.StringWriter();
        var sub = host.CreateSubEngine();
        sub.Out = sw;

        bool succeeded = false;
        foreach (Solution sol in sub.QueryAll(goal))
        {
            BindBack(engine, sol.Bindings);
            succeeded = true;
            break;
        }

        // Whether or not Goal succeeded, expose the captured text — that's
        // the SWI convention. (Caller can still observe failure via the
        // return value.)
        string captured = sw.ToString();
        int sinkArgAddr = functorIdx + 1;
        if (sinkType == "atom")
        {
            int aid = AtomTable.Intern(captured, permanent: false).Id;
            int slot = engine.AllocateHeap(1);
            engine.SetHeap(slot, Cell.Atom(aid));
            if (!engine.Unify(sinkArgAddr, slot)) return false;
        }
        else // string
        {
            int pstrIdx = engine.MakePstr(captured);
            int slot = engine.AllocateHeap(1);
            engine.SetHeap(slot, Cell.Ref(pstrIdx));
            if (!engine.Unify(sinkArgAddr, slot)) return false;
        }
        return succeeded;
    }

}
