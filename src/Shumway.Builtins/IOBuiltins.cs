using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Basic I/O builtins. All output goes through <see cref="Activation.Out"/>,
/// which defaults to <see cref="System.Console.Out"/> but can be swapped
/// for a <see cref="System.IO.StringWriter"/> (or any other writer) by the
/// embedding caller — particularly useful for capturing program output in
/// tests.
/// </summary>
public static class IOBuiltins
{
    /// <summary><c>write(X)</c> — writes X to the engine's output sink
    /// using operator-form rendering for known operators (no trailing
    /// newline). Atom quoting is off; pass <c>quoted(true)</c> through
    /// <c>write_term/2</c> if you need the parseable form.</summary>
    public static bool Write(Activation engine)
    {
        TermRenderer.Render(engine, engine.GetRegister(0), CurrentWriter(engine), DefaultOptions(engine));
        return true;
    }

    /// <summary>The current output writer. Prefers the
    /// per-engine stream registry's current_output (so
    /// <c>set_output/1</c> redirects all the write-family builtins);
    /// falls back to <see cref="Activation.Out"/> for engines without a
    /// registry. Refuses a binary stream — text I/O on a binary
    /// stream is permission_error(output, binary_stream, _).</summary>
    private static System.IO.TextWriter CurrentWriter(Activation engine)
    {
        if (engine.Streams is { } reg)
        {
            var h = reg.CurrentOutput;
            if (h.IsBinary)
                throw new PrologRuntimeException("permission_error", "output,binary_stream");
            if (h.Writer is not null) return h.Writer;
        }
        return engine.Out;
    }

    /// <summary><c>nl</c> — writes a single newline character to the
    /// current output stream. With a per-engine stream registry,
    /// <c>set_output/1</c> changes where this writes; without one,
    /// falls back to <see cref="Activation.Out"/>.</summary>
    public static bool Nl(Activation engine)
    {
        var cur = engine.Streams?.CurrentOutput;
        if (cur is { IsBinary: true })
            throw new PrologRuntimeException("permission_error", "output,binary_stream",
                engine, StreamBuiltins.MakeStreamTerm(engine, cur));
        var w = cur?.Writer ?? engine.Out;
        // A Prolog newline is LF on every platform (ADR-045); TextWriter's
        // WriteLine would emit the host's CRLF into an in-memory sink.
        w.Write('\n');
        return true;
    }

    /// <summary><c>writeln(X)</c> — equivalent to <c>write(X), nl</c>.</summary>
    public static bool Writeln(Activation engine)
    {
        var w = CurrentWriter(engine);
        TermRenderer.Render(engine, engine.GetRegister(0), w, DefaultOptions(engine));
        w.Write('\n');
        return true;
    }

    /// <summary><c>writeq(X)</c> — ISO §8.14.5. Writes <c>X</c> in
    /// quoted form: atoms and strings get quote characters where
    /// needed so the output reads back. Equivalent to
    /// <c>write_term(X, [quoted(true), numbervars(true)])</c>.</summary>
    public static bool Writeq1(Activation engine)
    {
        var opts = QuotedOptions(engine);
        TermRenderer.Render(engine, engine.GetRegister(0), CurrentWriter(engine), opts);
        return true;
    }

    /// <summary><c>writeq(+Stream, X)</c> — stream-aware writeq.
    /// ISO §8.14.5.</summary>
    public static bool Writeq2(Activation engine)
    {
        var h = StreamBuiltins.ResolveStream(engine, engine.GetRegister(0));
        if (h.IsBinary)
            throw new PrologRuntimeException("permission_error", "output,binary_stream");
        if (h.Writer is null)
            throw new PrologRuntimeException("permission_error", "output,stream");
        var opts = QuotedOptions(engine);
        TermRenderer.Render(engine, engine.GetRegister(1), h.Writer, opts);
        return true;
    }

    private static TermRenderOptions QuotedOptions(Activation engine) =>
        new TermRenderOptions
        {
            Operators = engine.Operators,
            Quoted = true,
            Numbervars = true,
        };

    private static TermRenderOptions DefaultOptions(Activation engine) =>
        new TermRenderOptions { Operators = engine.Operators };

    /// <summary><c>write_term(Term, Options)</c> — writes <c>Term</c>
    /// to the engine's output sink, honouring the boolean options
    /// <c>quoted/1</c>, <c>ignore_ops/1</c>, and <c>numbervars/1</c>.
    /// Any other option name is silently ignored (matching SWI's
    /// behaviour for unknown options).</summary>
    public static bool WriteTerm(Activation engine)
    {
        var options = ReadWriteTermOptions(engine, optsReg: 1);
        TermRenderer.Render(engine, engine.GetRegister(0), CurrentWriter(engine), options);
        return true;
    }

    /// <summary><c>write_term(+Stream, +Term, +Options)</c> — ISO
    /// §8.14.3.</summary>
    public static bool WriteTerm3(Activation engine)
    {
        var h = StreamBuiltins.ResolveStream(engine, engine.GetRegister(0));
        if (h.IsBinary)
            throw new PrologRuntimeException("permission_error", "output,binary_stream");
        if (h.Writer is null)
            throw new PrologRuntimeException("permission_error", "output,stream");
        var options = ReadWriteTermOptions(engine, optsReg: 2);
        TermRenderer.Render(engine, engine.GetRegister(1), h.Writer, options);
        return true;
    }

    /// <summary>Options write_term recognises but does not act on (SWI
    /// extras real code passes) — accepted so a domain_error only fires on
    /// genuinely unknown names.</summary>
    private static readonly System.Collections.Generic.HashSet<string> IgnoredWriteOptions = new()
    {
        "fullstop", "nl", "dotlists", "brace_terms",
        "attributes", "blobs", "character_escapes", "cycles", "partial",
        "portray_goal", "spacing", "no_lists", "priority",
    };

    /// <summary>format's <c>~ND</c>: the last <paramref name="frac"/> digits
    /// go after a decimal point and only the part to its left is grouped in
    /// threes.</summary>
    private static string GroupedDecimal(System.Numerics.BigInteger v, int frac)
    {
        bool neg = v.Sign < 0;
        string digits = System.Numerics.BigInteger.Abs(v).ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        string tail = "";
        if (frac > 0)
        {
            if (digits.Length <= frac) digits = digits.PadLeft(frac + 1, '0');
            tail = "." + digits[^frac..];
            digits = digits[..^frac];
        }
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0) sb.Append(',');
            sb.Append(digits[i]);
        }
        return (neg ? "-" : "") + sb + tail;
    }

    /// <summary>C's printf writes a two-digit exponent (<c>1.23e-06</c>);
    /// .NET's "G" writes <c>E-06</c> and sometimes three digits.</summary>
    private static string NormaliseExponent(string s)
    {
        int e = s.IndexOfAny(new[] { 'E', 'e' });
        if (e < 0) return s;
        string mant = s[..e];
        char sign = s[e + 1] is '+' or '-' ? s[e + 1] : '+';
        string exp = (s[e + 1] is '+' or '-' ? s[(e + 2)..] : s[(e + 1)..]).TrimStart('0');
        if (exp.Length == 0) exp = "0";
        if (exp.Length < 2) exp = exp.PadLeft(2, '0');
        return mant + "e" + sign + exp;
    }

    private static TermRenderOptions ReadWriteTermOptions(Activation engine, int optsReg)
    {
        var options = new TermRenderOptions { Operators = engine.Operators };
        ApplyWriteOptions(engine, Resolve(engine, engine.GetRegister(optsReg)), options);
        return options;
    }

    /// <summary>Applies a write-option LIST (already dereferenced) onto
    /// <paramref name="options"/>. Shared by write_term/2,3 and format's
    /// <c>~W</c>.</summary>
    private static void ApplyWriteOptions(
        Activation engine, Cell listStart, TermRenderOptions options)
    {
        Cell optsCell = listStart;
        // ISO §8.14.2.3: the list and every element must be instantiated;
        // an improper tail is type_error(list, WholeList); an element that
        // is not a recognised Name(Arg) option is domain_error(write_option,
        // Element); a bool option with a non-true/false argument reports the
        // WHOLE option as the culprit.
        while (true)
        {
            if (optsCell.Tag is Tag.Ref or Tag.AttVar)
                throw new PrologRuntimeException("instantiation_error");
            if (optsCell.Tag == Tag.Atom && optsCell.AsAtomId == AtomTable.EmptyListId)
                break;
            if (optsCell.Tag != Tag.Lis)
                throw new PrologRuntimeException("type_error", "list", engine, listStart);
            int headIdx = optsCell.AsHeapIndex;
            Cell head = Resolve(engine, engine.GetHeap(headIdx));
            ApplyOption(engine, head, options);
            optsCell = Resolve(engine, engine.GetHeap(headIdx + 1));
        }
    }

    private static void ApplyOption(Activation engine, Cell optCell, TermRenderOptions options)
    {
        if (optCell.Tag is Tag.Ref or Tag.AttVar)
            throw new PrologRuntimeException("instantiation_error");
        if (optCell.Tag != Tag.Str)
            throw new PrologRuntimeException("domain_error", "write_option", engine, optCell);
        int functorIdx = optCell.AsHeapIndex;
        var (atomId, arity) = FunctorTable.Lookup(
            engine.GetHeap(functorIdx).AsFunctorId);
        if (arity != 1)
            throw new PrologRuntimeException("domain_error", "write_option", engine, optCell);
        string name = AtomTable.GetById(atomId)?.Name ?? "";
        Cell valCell = Resolve(engine, engine.GetHeap(functorIdx + 1));
        if (name == "variable_names")
        {
            ApplyVariableNames(engine, valCell, options, optCell);
            return;
        }
        switch (name)
        {
            case "quoted": options.Quoted = RequireBool(engine, valCell, optCell); break;
            case "ignore_ops": options.IgnoreOps = RequireBool(engine, valCell, optCell); break;
            case "numbervars": options.Numbervars = RequireBool(engine, valCell, optCell); break;
            case "portray_text": options.PortrayText = RequireBool(engine, valCell, optCell); break;
            // Scryer/Trealla spelling of the same rendering choice: a
            // char/code list prints as "..." — decided by CONTENT
            // (ADR-047 decision 7), never by representation.
            case "double_quotes": options.PortrayText = RequireBool(engine, valCell, optCell); break;
            case "max_depth":
                if (valCell.Tag is Tag.Ref or Tag.AttVar)
                    throw new PrologRuntimeException("instantiation_error");
                if (valCell.Tag != Tag.Int || valCell.AsInt < 0)
                    throw new PrologRuntimeException(
                        "domain_error", "write_option", engine, optCell);
                options.MaxDepth = (int)valCell.AsInt;
                break;
            case "portrayed":
            case "portray":
                // SICStus spells it portrayed/1, SWI portray/1 — same hook.
                if (RequireBool(engine, valCell, optCell))
                    options.Portray = engine.PortrayHook;
                else
                    options.Portray = null;
                break;
            default:
                if (!IgnoredWriteOptions.Contains(name))
                    throw new PrologRuntimeException(
                        "domain_error", "write_option", engine, optCell);
                break;
        }
    }

    /// <summary>A bool write-option argument: unbound → instantiation_error;
    /// anything but true/false → domain_error with the WHOLE option
    /// (<c>quoted(fail)</c>) as culprit.</summary>
    private static bool RequireBool(Activation engine, Cell valCell, Cell optCell)
    {
        if (valCell.Tag is Tag.Ref or Tag.AttVar)
            throw new PrologRuntimeException("instantiation_error");
        if (valCell.Tag == Tag.Atom)
        {
            string? v = AtomTable.GetById(valCell.AsAtomId)?.Name;
            if (v == "true") return true;
            if (v == "false") return false;
        }
        throw new PrologRuntimeException("domain_error", "write_option", engine, optCell);
    }

    /// <summary>Parses the <c>variable_names([Name=Var, ...])</c> option
    /// into <see cref="TermRenderOptions.VariableNames"/>, keyed by each
    /// still-unbound <c>Var</c>'s dereferenced heap index. ISO §7.10.5 / §8.14:
    /// an unbound list or an unbound Name raises <c>instantiation_error</c>; a
    /// bound-but-malformed list, a non-<c>=</c>/2 element, or a non-atom Name
    /// raises <c>domain_error(write_option, …)</c>. A bound Var is accepted and
    /// simply carries no name.</summary>
    private static void ApplyVariableNames(
        Activation engine, Cell listCell, TermRenderOptions options, Cell optCell)
    {
        Cell cur = Resolve(engine, listCell);
        if (cur.Tag is Tag.Ref or Tag.AttVar)
            throw new PrologRuntimeException("instantiation_error");
        if (cur.Tag != Tag.Lis
            && !(cur.Tag == Tag.Atom && cur.AsAtomId == AtomTable.EmptyListId))
            throw new PrologRuntimeException("domain_error", "write_option", engine, optCell);
        // A bound-but-malformed element (or improper tail, or non-atom name)
        // is a domain_error even past an earlier unbound element/name — the
        // instantiation_error is deferred (sawUnbound) and only raised if the
        // whole list is otherwise well-formed.
        bool sawUnbound = false;
        // First occurrence WITHIN this list wins (built into localNames below);
        // the merge at the end lets a LATER variable_names OPTION override an
        // earlier one (Neumerkel vn #71 — write_term(T,[variable_names(['Bad'=T]),
        // variable_names(['Good'=T])]) prints Good).
        var localNames = new System.Collections.Generic.Dictionary<int, string>();
        while (cur.Tag == Tag.Lis)
        {
            int headIdx = cur.AsHeapIndex;
            Cell pair = Resolve(engine, engine.GetHeap(headIdx));
            if (pair.Tag is Tag.Ref or Tag.AttVar) { sawUnbound = true; }
            else
            {
                if (pair.Tag != Tag.Str)
                    throw new PrologRuntimeException("domain_error", "write_option", engine, optCell);
                int pairIdx = pair.AsHeapIndex;
                var (pAtom, pArity) = FunctorTable.Lookup(engine.GetHeap(pairIdx).AsFunctorId);
                if (pArity != 2 || (AtomTable.GetById(pAtom)?.Name ?? "") != "=")
                    throw new PrologRuntimeException("domain_error", "write_option", engine, optCell);
                Cell nameCell = Resolve(engine, engine.GetHeap(pairIdx + 1));
                if (nameCell.Tag is Tag.Ref or Tag.AttVar) { sawUnbound = true; }
                else if (nameCell.Tag != Tag.Atom)
                    throw new PrologRuntimeException("domain_error", "write_option", engine, optCell);
                else
                {
                    int varAddr = ResolveVarAddr(engine, engine.GetHeap(pairIdx + 2));
                    if (varAddr >= 0)
                    {
                        // First binding for a given variable in THIS list wins.
                        localNames.TryAdd(
                            varAddr, AtomTable.GetById(nameCell.AsAtomId)?.Name ?? "");
                    }
                }
            }
            cur = Resolve(engine, engine.GetHeap(headIdx + 1));
        }
        if (cur.Tag is Tag.Ref or Tag.AttVar) sawUnbound = true;
        else if (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId)
            throw new PrologRuntimeException("domain_error", "write_option", engine, optCell);
        if (sawUnbound)
            throw new PrologRuntimeException("instantiation_error");
        // Merge into the shared option map with OVERWRITE, so a later
        // variable_names option wins over an earlier one (vn #71). Only reached
        // when this list was well-formed (the throws above bail otherwise).
        if (localNames.Count > 0)
        {
            options.VariableNames ??= new System.Collections.Generic.Dictionary<int, string>();
            foreach (var kv in localNames) options.VariableNames[kv.Key] = kv.Value;
        }
    }

    /// <summary>Dereferences <paramref name="varCell"/>; returns its heap
    /// index when it is still an unbound variable, or -1 otherwise.</summary>
    private static int ResolveVarAddr(Activation engine, Cell varCell)
    {
        if (varCell.Tag == Tag.AttVar) return varCell.AsHeapIndex;
        if (varCell.Tag != Tag.Ref) return -1;
        int addr = engine.Deref(varCell.AsHeapIndex);
        Cell target = engine.GetHeap(addr);
        return (target.Tag == Tag.Ref || target.Tag == Tag.AttVar) ? addr : -1;
    }

    private static bool IsTrueAtom(Cell c) =>
        c.Tag == Tag.Atom &&
        (AtomTable.GetById(c.AsAtomId)?.Name ?? "") == "true";

    /// <summary><c>write_canonical(X)</c> — writes <c>X</c> in canonical
    /// form: quoted, operators ignored, so the output re-reads
    /// unambiguously.</summary>
    public static bool WriteCanonical(Activation engine)
    {
        TermRenderer.Render(engine, engine.GetRegister(0), CurrentWriter(engine),
            CanonicalOptions(engine));
        return true;
    }

    /// <summary><c>write_canonical(+Stream, +Term)</c> — ISO §8.14.6.</summary>
    public static bool WriteCanonical2(Activation engine)
    {
        var h = StreamBuiltins.ResolveStream(engine, engine.GetRegister(0));
        if (h.IsBinary)
            throw new PrologRuntimeException("permission_error", "output,binary_stream");
        if (h.Writer is null)
            throw new PrologRuntimeException("permission_error", "output,stream");
        TermRenderer.Render(engine, engine.GetRegister(1), h.Writer,
            CanonicalOptions(engine));
        return true;
    }

    private static TermRenderOptions CanonicalOptions(Activation engine) =>
        new TermRenderOptions
        {
            Operators = engine.Operators,
            Quoted = true,
            IgnoreOps = true,
        };

    /// <summary><c>print(X)</c> — write/1 with the <c>portray/1</c> hook
    /// consulted for every subterm first (the de-facto standard).</summary>
    public static bool Print(Activation engine)
    {
        var opts = DefaultOptions(engine);
        opts.Portray = engine.PortrayHook;
        TermRenderer.Render(engine, engine.GetRegister(0), CurrentWriter(engine), opts);
        return true;
    }

    /// <summary><c>print(+Stream, +Term)</c>.</summary>
    public static bool Print2(Activation engine)
    {
        var h = StreamBuiltins.ResolveStream(engine, engine.GetRegister(0));
        if (h.IsBinary)
            throw new PrologRuntimeException("permission_error", "output,binary_stream",
                engine, engine.GetRegister(0));
        if (h.Writer is null)
            throw new PrologRuntimeException("permission_error", "output,stream",
                engine, engine.GetRegister(0));
        var opts = DefaultOptions(engine);
        opts.Portray = engine.PortrayHook;
        TermRenderer.Render(engine, engine.GetRegister(1), h.Writer, opts);
        return true;
    }

    /// <summary><c>format(FormatString, Args)</c> — printf-style formatted
    /// output. Core specifiers: <c>~w</c> <c>~q</c> <c>~p</c> <c>~a</c>
    /// <c>~d</c> <c>~D</c> <c>~s</c> <c>~c</c> <c>~e</c>/<c>~f</c>/<c>~g</c>
    /// <c>~r</c>/<c>~R</c> <c>~i</c> <c>~n</c> <c>~~</c> (see
    /// <see cref="FormatImpl"/> for the full dispatch, including numeric /
    /// <c>*</c> / <c>`c</c> prefix arguments).
    /// <para>The format string may be an atom or a PSTR. The args list
    /// must be a proper list — pass <c>[]</c> when no args are needed.</para></summary>
    public static bool Format(Activation engine) =>
        // Current output via the registry (like every other output builtin):
        // set_output/1 and with_output_to/2 must redirect format/2 too.
        FormatImpl(engine, CurrentWriter(engine), fmtReg: 0, argsReg: 1, "format/2");

    /// <summary><c>format(Stream, FormatString, Args)</c> — stream-aware
    /// variant of <see cref="Format"/>. The stream handle must be a
    /// FOREIGN cell wrapping a <see cref="System.IO.StreamWriter"/>.</summary>
    public static bool Format3(Activation engine)
    {
        // Streams are StreamHandle-wrapped via the per-engine
        // StreamRegistry. Resolve through StreamBuiltins so atoms /
        // aliases work too.
        var h = StreamBuiltins.ResolveStream(engine, engine.GetRegister(0));
        if (!h.IsWriter)
            throw new PrologRuntimeException("permission_error", "output,stream");
        return FormatImpl(engine, h.Writer!, fmtReg: 1, argsReg: 2, "format/3");
    }

    private static bool FormatImpl(Activation engine, System.IO.TextWriter realOutput, int fmtReg, int argsReg, string name)
    {
        string fmt = ReadStringArg(engine, engine.GetRegister(fmtReg), name);
        var args = ReadProperListAsCells(engine, engine.GetRegister(argsReg), name);
        int argIdx = 0;

        // Column alignment (~t / ~| / ~+) needs the text SINCE the last
        // column stop in hand to distribute padding, so everything is
        // written into a pending buffer and flushed at each stop, each
        // newline, and at the end.
        var column = new ColumnWriter(realOutput);
        System.IO.TextWriter output = column;

        for (int i = 0; i < fmt.Length; i++)
        {
            char ch = fmt[i];
            if (ch != '~')
            {
                output.Write(ch);
                continue;
            }
            if (++i >= fmt.Length)
                // A lone '~' at the end is a malformed format string.
                // SWI / SICStus surface this as a domain_error on the
                // format spec.
                throw new PrologRuntimeException("domain_error", "format_spec");

            // Optional column/count argument before the spec char: a literal
            // number (`~20|`, `~3c`), `*` (take it from the next argument), or
            // `` `c `` (a fill character for ~t).
            int? num = null;
            if (fmt[i] == '*')
            {
                i++;
                num = (int)FormatIntegerArg(engine, ConsumeArg(args, ref argIdx, name));
            }
            else if (fmt[i] == '`' && i + 1 < fmt.Length)
            {
                num = fmt[i + 1];
                i += 2;
            }
            else
            {
                int v = 0; bool any = false;
                while (i < fmt.Length && fmt[i] >= '0' && fmt[i] <= '9')
                { v = v * 10 + (fmt[i] - '0'); i++; any = true; }
                if (any) num = v;
            }
            if (i >= fmt.Length)
                throw new PrologRuntimeException("domain_error", "format_spec");
            char spec = fmt[i];
            switch (spec)
            {
                case '~':
                    output.Write('~');
                    break;
                case 'n':
                    // ~Nn writes N newlines (the count already went through
                    // the arithmetic path when it came from ~*n).
                    for (int r = 0; r < (num ?? 1); r++) output.Write('\n');
                    break;
                case 'w':
                {
                    // ~w is write/1, which honours numbervars: a term run
                    // through numbervars/3 prints A, B, … not '$VAR'(0).
                    Cell arg = ConsumeArg(args, ref argIdx, name);
                    TermRenderer.Render(engine, arg, output,
                        new TermRenderOptions
                        { Operators = engine.Operators, Numbervars = true });
                    break;
                }
                case 'a':
                {
                    Cell arg = ConsumeArg(args, ref argIdx, name);
                    Cell deref = Resolve(engine, arg);
                    // ~a wants an atom — instantiation_error for an
                    // unbound arg, type_error(atom) otherwise.
                    if (deref.Tag is Tag.Ref or Tag.AttVar)
                        throw new PrologRuntimeException("instantiation_error");
                    if (deref.Tag != Tag.Atom)
                        throw new PrologRuntimeException(
                            "type_error", "atom", engine, deref);
                    output.Write(AtomTable.GetById(deref.AsAtomId)?.Name ?? "");
                    break;
                }
                case 'q':
                {
                    // ~q — writeq: the term quoted where a re-read would
                    // need it (operator form, quoting, numbervars).
                    Cell arg = ConsumeArg(args, ref argIdx, name);
                    TermRenderer.Render(engine, arg, output,
                        new TermRenderOptions
                        {
                            Operators = engine.Operators,
                            Quoted = true,
                            Numbervars = true,
                        });
                    break;
                }
                case 'p':
                {
                    // ~p — print/1: ~w with the portray/1 hook consulted first.
                    Cell arg = ConsumeArg(args, ref argIdx, name);
                    TermRenderer.Render(engine, arg, output,
                        new TermRenderOptions
                        {
                            Operators = engine.Operators,
                            Numbervars = true,
                            Portray = engine.PortrayHook,
                        });
                    break;
                }
                case 'i':
                    // ~i — consume and ignore the next argument (no output).
                    ConsumeArg(args, ref argIdx, name);
                    break;
                case 'd':
                {
                    Cell arg = ConsumeArg(args, ref argIdx, name);
                    if (num is int dneg && dneg < 0)
                        throw new PrologRuntimeException("domain_error", "format_spec");
                    System.Numerics.BigInteger dv = FormatIntegerArg(engine, arg);
                    string ds = dv.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    // ~Nd inserts a decimal point N digits from the right.
                    if (num is int dd && dd > 0)
                    {
                        bool neg = ds.StartsWith('-');
                        if (neg) ds = ds[1..];
                        if (ds.Length <= dd) ds = ds.PadLeft(dd + 1, '0');
                        ds = ds[..^dd] + "." + ds[^dd..];
                        if (neg) ds = "-" + ds;
                    }
                    output.Write(ds);
                    break;
                }
                case 's':
                {
                    Cell arg = ConsumeArg(args, ref argIdx, name);
                    var sb = new System.Text.StringBuilder();
                    Cell cur = ListCursor.Resolve(engine, arg);
                    if (cur.Tag is Tag.Ref or Tag.AttVar)
                        throw new PrologRuntimeException("instantiation_error");
                    // A plain atom is accepted as text (SWI/SICStus), as is a
                    // list of one-char atoms — `~s` is not code-lists-only.
                    if (cur.Tag == Tag.Atom && cur.AsAtomId != AtomTable.EmptyListId)
                        sb.Append(AtomTable.GetById(cur.AsAtomId)?.Name ?? "");
                    else if (cur.Tag is not (Tag.Lis or Tag.Pstr)
                        && !(cur.Tag == Tag.Atom && cur.AsAtomId == AtomTable.EmptyListId))
                        throw new PrologRuntimeException("type_error", "list", engine, cur);
                    // The cursor, not Tag.Lis: a packed list passed the type
                    // check above and then printed nothing.
                    while (ListCursor.TryUncons(engine, cur, out Cell rawHead, out Cell sTail))
                    {
                        Cell head = Resolve(engine, rawHead);
                        if (head.Tag is Tag.Ref or Tag.AttVar)
                            throw new PrologRuntimeException("instantiation_error");
                        if (head.Tag == Tag.Atom)
                        {
                            string cn = AtomTable.GetById(head.AsAtomId)?.Name ?? "";
                            if (cn.Length != 1)
                                throw new PrologRuntimeException(
                                    "type_error", "character", engine, head);
                            sb.Append(cn);
                        }
                        else if (head.Tag == Tag.Int)
                        {
                            // BMP-only, as char_code/2 (truncating builds another char).
                            if (head.AsInt < 0 || head.AsInt > char.MaxValue)
                                throw new PrologRuntimeException(
                                    "representation_error", "character_code");
                            sb.Append((char)head.AsInt);
                        }
                        else
                        {
                            throw new PrologRuntimeException(
                                "type_error", "integer", engine, head);
                        }
                        cur = ListCursor.Resolve(engine, sTail);
                    }
                    if (cur.Tag is Tag.Ref or Tag.AttVar)
                        throw new PrologRuntimeException("instantiation_error");
                    // ~Ns is a field WIDTH: longer text is cut to N, shorter
                    // text is padded out to N with spaces.
                    string sv = sb.ToString();
                    if (num is int swidth)
                        sv = swidth < sv.Length ? sv[..swidth] : sv.PadRight(swidth);
                    output.Write(sv);
                    break;
                }
                case 'e':
                case 'E':
                case 'f':
                case 'g':
                case 'G':
                {
                    // ~Ne / ~Nf / ~Ng — the numeric argument as a float with
                    // N fractional digits (default 6), C-printf style. An
                    // integer argument is accepted and widened.
                    double d = FormatFloatArg(engine, ConsumeArg(args, ref argIdx, name));
                    int prec = num ?? 6;
                    // ~E / ~G are ~e / ~g with an upper-case exponent marker.
                    char lower = char.ToLowerInvariant(spec);
                    string fs;
                    if (lower == 'g')
                    {
                        // C's %g: significant digits, shortest of %e/%f, and a
                        // two-digit exponent — .NET's "G" gives "E-06" and a
                        // three-digit exponent on some inputs.
                        fs = d.ToString(
                            "G" + (prec <= 0 ? 6 : prec),
                            System.Globalization.CultureInfo.InvariantCulture);
                        fs = NormaliseExponent(fs);
                    }
                    else
                    {
                        string netFmt = lower == 'e'
                            ? "0." + new string('0', prec) + "e+00"
                            : "F" + prec;
                        fs = d.ToString(
                            netFmt, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    output.Write(spec is 'E' or 'G' ? fs.ToUpperInvariant() : fs.ToLowerInvariant());
                    break;
                }
                case 'k':
                {
                    // ~k — write_canonical/1 of the argument.
                    TermRenderer.Render(engine, ConsumeArg(args, ref argIdx, name),
                        output, CanonicalOptions(engine));
                    break;
                }
                case 'W':
                {
                    // ~W — write_term/2: TWO arguments, the term and its
                    // option list.
                    Cell wterm = ConsumeArg(args, ref argIdx, name);
                    Cell wopts = ConsumeArg(args, ref argIdx, name);
                    var wo = DefaultOptions(engine);
                    ApplyWriteOptions(engine, wopts, wo);
                    TermRenderer.Render(engine, wterm, output, wo);
                    break;
                }
                case 'N':
                    // ~N — a newline only when not already at column 0
                    // (a leading count is accepted and ignored).
                    if (column.CurrentColumn != 0) output.Write('\n');
                    break;
                case 'r':
                case 'R':
                {
                    // ~Nr / ~NR — the integer argument in radix N (2..36),
                    // digits a-z (~r) or A-Z (~R).
                    int radix = num ?? 8;
                    if (radix < 2 || radix > 36)
                        throw new PrologRuntimeException("domain_error", "radix");
                    System.Numerics.BigInteger iv =
                        FormatIntegerArg(engine, ConsumeArg(args, ref argIdx, name));
                    string r = ToRadix(iv, radix);
                    output.Write(spec == 'R' ? r.ToUpperInvariant() : r);
                    break;
                }
                case 'D':
                {
                    // ~D — thousands separators. ~ND additionally puts the
                    // last N digits after a decimal point, and only the part
                    // to its LEFT is grouped (`~2D` of 123456789 is
                    // 1,234,567.89).
                    if (num is int Dneg && Dneg < 0)
                        throw new PrologRuntimeException("domain_error", "format_spec");
                    Cell deref = FormatIntegerCell(engine, ConsumeArg(args, ref argIdx, name));
                    System.Numerics.BigInteger Dv = deref.Tag switch
                    {
                        Tag.Int => deref.AsInt,
                        Tag.BigInt => engine.AsBigInt(deref),
                        _ => throw new PrologRuntimeException("type_error", "integer"),
                    };
                    output.Write(GroupedDecimal(Dv, num ?? 0));
                    break;
                }
                case 'c':
                {
                    // ~Nc — emit the argument's character code N times (N = 1).
                    // The code goes through the arithmetic path like ~d, so
                    // `~c` of the atom `a` is type_error(evaluable, a/0).
                    System.Numerics.BigInteger cv =
                        FormatIntegerArg(engine, ConsumeArg(args, ref argIdx, name));
                    if (cv < 0 || cv > char.MaxValue)
                        throw new PrologRuntimeException(
                            "representation_error", "character_code");
                    int reps = num ?? 1;
                    for (int r = 0; r < reps; r++) output.Write((char)cv);
                    break;
                }
                case 't':
                    // Fill point: ~t pads with spaces, ~`ct (or ~Nt) with
                    // the given character, when a later stop needs padding.
                    column.AddFillPoint(num.HasValue ? (char)num.Value : ' ');
                    break;
                case '|':
                    // Absolute column stop; bare ~| means "here".
                    column.ColumnStop(num ?? column.CurrentColumn);
                    break;
                case '+':
                    // Relative stop: N columns past where this segment began
                    // (default 8) — the standard table-column idiom.
                    column.ColumnStop(column.SegmentStartColumn + (num ?? 8));
                    break;
                default:
                    // An unknown ~X spec is an ISO domain_error on the
                    // format string.
                    throw new PrologRuntimeException("domain_error", "format_spec");
            }
        }
        column.Flush();
        // §format: every argument must be consumed — a leftover is a
        // format_arguments domain error (SWI raises, SICStus too).
        if (argIdx < args.Count)
            throw new PrologRuntimeException("domain_error", "format_arguments");
        return true;
    }

    /// <summary>format/2's column engine. Text accumulates in a pending
    /// segment; <c>~t</c> records a fill point (with its fill character)
    /// inside that segment; <c>~N|</c> / <c>~N+</c> close the segment,
    /// padding it out to the requested column by distributing the shortfall
    /// over the fill points — all of it at the end when there are none (so
    /// the text is left-aligned), at the front for a leading <c>~t</c>
    /// (right-aligned), split for <c>~t</c> on both sides (centred).</summary>
    private sealed class ColumnWriter : System.IO.TextWriter
    {
        private readonly System.IO.TextWriter _out;
        private readonly System.Text.StringBuilder _seg = new();
        private readonly List<(int Pos, char Fill)> _fills = new();
        private int _lineColumn;      // column where the pending segment starts

        public ColumnWriter(System.IO.TextWriter output) => _out = output;

        public override System.Text.Encoding Encoding => _out.Encoding;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                // A newline ends the segment as-is (no padding) and resets
                // the column origin.
                FlushSegment();
                _out.Write('\n');
                _lineColumn = 0;
                return;
            }
            _seg.Append(value);
        }

        /// <summary>Records a <c>~t</c> fill point at the current position.</summary>
        public void AddFillPoint(char fill) => _fills.Add((_seg.Length, fill));

        /// <summary>Closes the segment at column <paramref name="target"/>
        /// (absolute for <c>~|</c>; the caller resolves <c>~+</c> to one).</summary>
        public void ColumnStop(int target)
        {
            int pad = target - (_lineColumn + _seg.Length);
            if (pad > 0)
            {
                if (_fills.Count == 0)
                {
                    _seg.Append(' ', pad);       // no ~t: pad on the right
                }
                else
                {
                    // Distribute evenly; the remainder goes ONE EACH to the
                    // last fill points, so `~|~t~t~tabc~t~10+` pads 1+2+2+2,
                    // not 1+1+1+4.
                    int each = pad / _fills.Count, extra = pad % _fills.Count;
                    for (int k = _fills.Count - 1; k >= 0; k--)
                    {
                        int n = each + (k >= _fills.Count - extra ? 1 : 0);
                        if (n > 0) _seg.Insert(_fills[k].Pos, new string(_fills[k].Fill, n));
                    }
                }
            }
            FlushSegment();
            // A stop always leaves the cursor at least at the target column.
            _lineColumn = System.Math.Max(_lineColumn, target);
        }

        /// <summary>The column the next character would land in.</summary>
        public int CurrentColumn => _lineColumn + _seg.Length;

        /// <summary>The column this pending segment started at — the origin
        /// a relative <c>~N+</c> stop measures from.</summary>
        public int SegmentStartColumn => _lineColumn;

        private void FlushSegment()
        {
            if (_seg.Length > 0)
            {
                _lineColumn += _seg.Length;
                _out.Write(_seg.ToString());
                _seg.Clear();
            }
            _fills.Clear();
        }

        public override void Flush()
        {
            FlushSegment();
            _out.Flush();
        }
    }

    // ---------- Helpers ----------

    /// <summary>A numeric format argument (<c>~d</c>, <c>~D</c>, <c>~r</c>,
    /// <c>~e/f/g</c>, and the <c>~*n</c> count) is an arithmetic
    /// EXPRESSION, as in SWI and SICStus — so a non-evaluable argument
    /// raises type_error(evaluable, Name/Arity) rather than a bare type
    /// error, and <c>format("~d", [1+1])</c> prints 2.</summary>
    private static Cell FormatIntegerCell(Activation engine, Cell arg)
    {
        Cell deref = Resolve(engine, arg);
        if (deref.Tag is Tag.Ref or Tag.AttVar)
            throw new PrologRuntimeException("instantiation_error");
        if (deref.Tag is Tag.Int or Tag.BigInt) return deref;
        // A float is a type error even though it evaluates.
        if (deref.Tag is Tag.Float or Tag.Rational)
            throw new PrologRuntimeException("type_error", "integer", engine, deref);
        Number n = ArithmeticEvaluator.Evaluate(engine, deref);
        if (n.IsFloat)
            throw new PrologRuntimeException("type_error", "integer", engine, deref);
        return n.ToCell(engine);
    }

    private static System.Numerics.BigInteger FormatIntegerArg(Activation engine, Cell arg)
    {
        Cell c = FormatIntegerCell(engine, arg);
        return c.Tag == Tag.BigInt ? engine.AsBigInt(c) : c.AsInt;
    }

    private static double FormatFloatArg(Activation engine, Cell arg)
    {
        Cell deref = Resolve(engine, arg);
        if (deref.Tag is Tag.Ref or Tag.AttVar)
            throw new PrologRuntimeException("instantiation_error");
        return ArithmeticEvaluator.Evaluate(engine, deref).AsDouble();
    }

    /// <summary>Renders a (possibly big) integer in the given radix
    /// (2..36) with lowercase digits, matching SWI/SICStus <c>~r</c>.</summary>
    private static string ToRadix(System.Numerics.BigInteger value, int radix)
    {
        if (value.IsZero) return "0";
        bool neg = value.Sign < 0;
        System.Numerics.BigInteger v = neg ? -value : value;
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var sb = new System.Text.StringBuilder();
        while (v > 0)
        {
            sb.Insert(0, digits[(int)(v % radix)]);
            v /= radix;
        }
        if (neg) sb.Insert(0, '-');
        return sb.ToString();
    }

    private static Cell ConsumeArg(List<Cell> args, ref int idx, string builtinName)
    {
        if (idx >= args.Count)
            // Format string demanded more args than the caller supplied —
            // SWI treats this as a domain_error on the argument count.
            throw new PrologRuntimeException("domain_error", "format_argument_count");
        return args[idx++];
    }

    private static string ReadStringArg(Activation engine, Cell c, string builtinName)
    {
        Cell d = Resolve(engine, c);
        if (d.Tag == Tag.Atom)
            // `format("", [])` reaches here as the empty LIST under
            // double_quotes=codes — empty text, not the name "[]".
            return d.AsAtomId == AtomTable.EmptyListId
                ? ""
                : AtomTable.GetById(d.AsAtomId)?.Name ?? "";
        if (d.Tag == Tag.Pstr)
            return engine.AsPstrString(engine.Deref(c.AsHeapIndex));
        // A format string may equally be a list of character CODES or of
        // one-char atoms — which is what `format("...", …)` becomes under
        // every double_quotes setting other than `atom`.
        if (d.Tag == Tag.Lis)
            return ReadTextListArg(engine, d);
        // A missing format string is instantiation_error; a wrong-typed
        // one is type_error(atom).
        if (d.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        throw new PrologRuntimeException("type_error", "atom", engine, d);
    }

    /// <summary>Reads a code list or char list into text. The two are not
    /// mixed: the first element decides which, and an element that does not
    /// match it is a type error — a mixed list is a caller bug, not a third
    /// notation.</summary>
    private static string ReadTextListArg(Activation engine, Cell list)
    {
        var sb = new System.Text.StringBuilder();
        Cell cur = ListCursor.Resolve(engine, list);
        bool? codes = null;
        while (ListCursor.TryUncons(engine, cur, out Cell rawHead, out Cell tTail))
        {
            Cell head = Resolve(engine, rawHead);
            if (head.Tag == Tag.Ref)
                throw new PrologRuntimeException("instantiation_error");

            bool isCode = head.Tag == Tag.Int;
            codes ??= isCode;
            if (isCode != codes.Value)
                throw new PrologRuntimeException("type_error", "atom");

            if (isCode)
            {
                long code = head.AsInt;
                if (!Utf16Text.IsScalarValue(code))
                    throw new PrologRuntimeException(
                        "representation_error", "character_code");
                Utf16Text.AppendCodePoint(sb, (int)code);
            }
            else
            {
                if (head.Tag != Tag.Atom)
                    throw new PrologRuntimeException("type_error", "atom");
                string ch = AtomTable.GetById(head.AsAtomId)?.Name ?? "";
                if (ch.Length == 0)
                    throw new PrologRuntimeException("type_error", "character");
                sb.Append(ch);
            }
            cur = Resolve(engine, engine.GetHeap(cur.AsHeapIndex + 1));
        }
        if (cur.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        if (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId)
            throw new PrologRuntimeException("type_error", "atom");
        return sb.ToString();
    }

    private static List<Cell> ReadProperListAsCells(Activation engine, Cell c, string builtinName)
    {
        var result = new List<Cell>();
        Cell cur = ListCursor.Resolve(engine, c);
        while (ListCursor.TryUncons(engine, cur, out Cell head, out Cell tail))
        {
            result.Add(head);
            cur = ListCursor.Resolve(engine, tail);
        }
        if (cur.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        if (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId)
            // Argument list isn't a proper list — the culprit is the list as
            // GIVEN, not the offending tail.
            throw new PrologRuntimeException(
                "type_error", "list", engine, Resolve(engine, c));
        return result;
    }

    private static Cell Resolve(Activation engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        return engine.GetHeap(engine.Deref(c.AsHeapIndex));
    }
}
