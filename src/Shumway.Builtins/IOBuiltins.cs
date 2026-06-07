using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>
/// Basic I/O builtins. All output goes through <see cref="Engine.Out"/>,
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
    public static bool Write(Engine engine)
    {
        TermRenderer.Render(engine, engine.GetRegister(0), CurrentWriter(engine), DefaultOptions(engine));
        return true;
    }

    /// <summary>The current output writer. Prefers the chunk-140
    /// per-engine stream registry's current_output (so
    /// <c>set_output/1</c> redirects all the write-family builtins);
    /// falls back to <see cref="Engine.Out"/> for engines without a
    /// registry. Refuses a binary stream — text I/O on a binary
    /// stream is permission_error(output, binary_stream, _).</summary>
    private static System.IO.TextWriter CurrentWriter(Engine engine)
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
    /// current output stream. With the per-engine stream registry
    /// wired (chunk 140), <c>set_output/1</c> changes where this
    /// writes; without one, falls back to <see cref="Engine.Out"/>.</summary>
    public static bool Nl(Engine engine)
    {
        var w = engine.Streams?.CurrentOutput.Writer ?? engine.Out;
        w.WriteLine();
        return true;
    }

    /// <summary><c>writeln(X)</c> — equivalent to <c>write(X), nl</c>.</summary>
    public static bool Writeln(Engine engine)
    {
        var w = CurrentWriter(engine);
        TermRenderer.Render(engine, engine.GetRegister(0), w, DefaultOptions(engine));
        w.WriteLine();
        return true;
    }

    /// <summary><c>writeq(X)</c> — ISO §8.14.5. Writes <c>X</c> in
    /// quoted form: atoms and strings get quote characters where
    /// needed so the output reads back. Equivalent to
    /// <c>write_term(X, [quoted(true), numbervars(true)])</c>.</summary>
    public static bool Writeq1(Engine engine)
    {
        var opts = QuotedOptions(engine);
        TermRenderer.Render(engine, engine.GetRegister(0), CurrentWriter(engine), opts);
        return true;
    }

    /// <summary><c>writeq(+Stream, X)</c> — stream-aware writeq.
    /// ISO §8.14.5.</summary>
    public static bool Writeq2(Engine engine)
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

    private static TermRenderOptions QuotedOptions(Engine engine) =>
        new TermRenderOptions
        {
            Operators = engine.Operators,
            Quoted = true,
            Numbervars = true,
        };

    private static TermRenderOptions DefaultOptions(Engine engine) =>
        new TermRenderOptions { Operators = engine.Operators };

    /// <summary><c>write_term(Term, Options)</c> — writes <c>Term</c>
    /// to the engine's output sink, honouring the boolean options
    /// <c>quoted/1</c>, <c>ignore_ops/1</c>, and <c>numbervars/1</c>.
    /// Any other option name is silently ignored — Phase 1 doesn't yet
    /// support the full ISO menu, and silent skipping matches what
    /// SWI does for unknown options.</summary>
    public static bool WriteTerm(Engine engine)
    {
        var options = ReadWriteTermOptions(engine, optsReg: 1);
        TermRenderer.Render(engine, engine.GetRegister(0), CurrentWriter(engine), options);
        return true;
    }

    /// <summary><c>write_term(+Stream, +Term, +Options)</c> — ISO
    /// §8.14.3.</summary>
    public static bool WriteTerm3(Engine engine)
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

    private static TermRenderOptions ReadWriteTermOptions(Engine engine, int optsReg)
    {
        var options = new TermRenderOptions { Operators = engine.Operators };
        Cell optsCell = Resolve(engine, engine.GetRegister(optsReg));
        while (optsCell.Tag == Tag.Lis)
        {
            int headIdx = optsCell.AsHeapIndex;
            Cell head = Resolve(engine, engine.GetHeap(headIdx));
            ApplyOption(engine, head, options);
            optsCell = Resolve(engine, engine.GetHeap(headIdx + 1));
        }
        return options;
    }

    private static void ApplyOption(Engine engine, Cell optCell, TermRenderOptions options)
    {
        if (optCell.Tag != Tag.Str) return;
        int functorIdx = optCell.AsHeapIndex;
        var (atomId, arity) = FunctorTable.Lookup(
            engine.GetHeap(functorIdx).AsFunctorId);
        if (arity != 1) return;
        string name = AtomTable.GetById(atomId)?.Name ?? "";
        Cell valCell = Resolve(engine, engine.GetHeap(functorIdx + 1));
        bool value = IsTrueAtom(valCell);
        switch (name)
        {
            case "quoted": options.Quoted = value; break;
            case "ignore_ops": options.IgnoreOps = value; break;
            case "numbervars": options.Numbervars = value; break;
            // Unknown options ignored silently.
        }
    }

    private static bool IsTrueAtom(Cell c) =>
        c.Tag == Tag.Atom &&
        (AtomTable.GetById(c.AsAtomId)?.Name ?? "") == "true";

    /// <summary><c>write_canonical(X)</c> — writes <c>X</c> in canonical
    /// form. Phase 1 aliases to <c>write/1</c>; the canonical-form switch
    /// (quoting special characters, qualifying operators) will land with
    /// <c>TermRenderer</c>'s option-aware rewrite.</summary>
    public static bool WriteCanonical(Engine engine)
    {
        TermRenderer.Render(engine, engine.GetRegister(0), CurrentWriter(engine),
            CanonicalOptions(engine));
        return true;
    }

    /// <summary><c>write_canonical(+Stream, +Term)</c> — ISO §8.14.6.</summary>
    public static bool WriteCanonical2(Engine engine)
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

    private static TermRenderOptions CanonicalOptions(Engine engine) =>
        new TermRenderOptions
        {
            Operators = engine.Operators,
            Quoted = true,
            IgnoreOps = true,
        };

    /// <summary><c>print(X)</c> — ISO defines this as a portray/1 hook
    /// fallback to <c>write/1</c>. Phase 1 implements only the
    /// <c>write/1</c> fallback path.</summary>
    public static bool Print(Engine engine)
    {
        TermRenderer.Render(engine, engine.GetRegister(0), CurrentWriter(engine),
            DefaultOptions(engine));
        return true;
    }

    /// <summary><c>format(FormatString, Args)</c> — printf-style formatted
    /// output. The Phase-1 set of specifiers is:
    /// <list type="bullet">
    /// <item><c>~w</c> — writes the next arg via <see cref="TermRenderer"/>.</item>
    /// <item><c>~a</c> — writes the next arg's atom name (must be an atom).</item>
    /// <item><c>~d</c> — writes the next arg's integer value.</item>
    /// <item><c>~s</c> — writes the next arg's code list as a string.</item>
    /// <item><c>~n</c> — writes a newline (no arg consumed).</item>
    /// <item><c>~~</c> — writes a literal <c>~</c>.</item>
    /// </list>
    /// <para>The format string may be an atom or a PSTR. The args list
    /// must be a proper list — pass <c>[]</c> when no args are needed.</para></summary>
    public static bool Format(Engine engine) =>
        FormatImpl(engine, engine.Out, fmtReg: 0, argsReg: 1, "format/2");

    /// <summary><c>format(Stream, FormatString, Args)</c> — stream-aware
    /// variant of <see cref="Format"/>. The stream handle must be a
    /// FOREIGN cell wrapping a <see cref="System.IO.StreamWriter"/>.</summary>
    public static bool Format3(Engine engine)
    {
        // Chunk 140a refactor: streams are StreamHandle-wrapped via
        // the per-engine StreamRegistry. Resolve through StreamBuiltins
        // so atoms / aliases work too.
        var h = StreamBuiltins.ResolveStream(engine, engine.GetRegister(0));
        if (!h.IsWriter)
            throw new PrologRuntimeException("permission_error", "output,stream");
        return FormatImpl(engine, h.Writer!, fmtReg: 1, argsReg: 2, "format/3");
    }

    private static bool FormatImpl(Engine engine, System.IO.TextWriter output, int fmtReg, int argsReg, string name)
    {
        string fmt = ReadStringArg(engine, engine.GetRegister(fmtReg), name);
        var args = ReadProperListAsCells(engine, engine.GetRegister(argsReg), name);
        int argIdx = 0;

        for (int i = 0; i < fmt.Length; i++)
        {
            char ch = fmt[i];
            if (ch != '~')
            {
                output.Write(ch);
                continue;
            }
            if (++i >= fmt.Length)
                // Chunk 131d: a lone '~' at the end is a malformed format
                // string. SWI / SICStus surface this as a domain_error
                // on the format spec.
                throw new PrologRuntimeException("domain_error", "format_spec");

            // Optional column/count argument before the spec char: a literal
            // number (`~20|`, `~3c`), `*` (take it from the next argument), or
            // `` `c `` (a fill character for ~t). (chunk 346)
            int? num = null;
            if (fmt[i] == '*')
            {
                i++;
                Cell c = Resolve(engine, ConsumeArg(args, ref argIdx, name));
                if (c.Tag != Tag.Int) throw new PrologRuntimeException("type_error", "integer");
                num = (int)c.AsInt;
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
                    output.WriteLine();
                    break;
                case 'w':
                {
                    Cell arg = ConsumeArg(args, ref argIdx, name);
                    TermRenderer.Render(engine, arg, output,
                        new TermRenderOptions { Operators = engine.Operators });
                    break;
                }
                case 'a':
                {
                    Cell arg = ConsumeArg(args, ref argIdx, name);
                    Cell deref = Resolve(engine, arg);
                    // Chunk 131d: ~a wants an atom — instantiation_error
                    // for an unbound arg, type_error(atom) otherwise.
                    if (deref.Tag == Tag.Ref)
                        throw new PrologRuntimeException("instantiation_error");
                    if (deref.Tag != Tag.Atom)
                        throw new PrologRuntimeException("type_error", "atom");
                    output.Write(AtomTable.GetById(deref.AsAtomId)?.Name ?? "");
                    break;
                }
                case 'd':
                {
                    Cell arg = ConsumeArg(args, ref argIdx, name);
                    Cell deref = Resolve(engine, arg);
                    if (deref.Tag == Tag.Ref)
                        throw new PrologRuntimeException("instantiation_error");
                    if (deref.Tag != Tag.Int)
                        throw new PrologRuntimeException("type_error", "integer");
                    output.Write(deref.AsInt.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                }
                case 's':
                {
                    Cell arg = ConsumeArg(args, ref argIdx, name);
                    var sb = new System.Text.StringBuilder();
                    Cell cur = Resolve(engine, arg);
                    while (cur.Tag == Tag.Lis)
                    {
                        Cell head = Resolve(engine, engine.GetHeap(cur.AsHeapIndex));
                        if (head.Tag == Tag.Ref)
                            throw new PrologRuntimeException("instantiation_error");
                        if (head.Tag != Tag.Int)
                            throw new PrologRuntimeException("type_error", "character_code");
                        sb.Append((char)head.AsInt);
                        cur = Resolve(engine, engine.GetHeap(cur.AsHeapIndex + 1));
                    }
                    output.Write(sb.ToString());
                    break;
                }
                case 'c':
                {
                    // ~Nc — emit the argument's character code N times (N = 1).
                    Cell deref = Resolve(engine, ConsumeArg(args, ref argIdx, name));
                    if (deref.Tag == Tag.Ref)
                        throw new PrologRuntimeException("instantiation_error");
                    if (deref.Tag != Tag.Int)
                        throw new PrologRuntimeException("type_error", "character_code");
                    int reps = num ?? 1;
                    for (int r = 0; r < reps; r++) output.Write((char)deref.AsInt);
                    break;
                }
                case 't':
                    // Column fill point. Full column alignment (distributing
                    // fill between ~t marks up to a ~| / ~+ stop) is not yet
                    // implemented; without a stop ~t is a no-op anyway, which
                    // covers the common `format('~tword~n')` shape.
                    break;
                case '|':
                case '+':
                    // Column stop — accepted (no padding emitted yet) so a
                    // format string that aligns columns runs rather than
                    // raising a domain_error. Output is unaligned, not wrong.
                    break;
                default:
                    // Chunk 131d: an unknown ~X spec is an ISO
                    // domain_error on the format string.
                    throw new PrologRuntimeException("domain_error", "format_spec");
            }
        }
        return true;
    }

    // ---------- Helpers ----------

    private static Cell ConsumeArg(List<Cell> args, ref int idx, string builtinName)
    {
        if (idx >= args.Count)
            // Chunk 131d: format string demanded more args than the
            // caller supplied — SWI treats this as a domain_error on
            // the argument count of the format directive.
            throw new PrologRuntimeException("domain_error", "format_argument_count");
        return args[idx++];
    }

    private static string ReadStringArg(Engine engine, Cell c, string builtinName)
    {
        Cell d = Resolve(engine, c);
        if (d.Tag == Tag.Atom)
            return AtomTable.GetById(d.AsAtomId)?.Name ?? "";
        if (d.Tag == Tag.Pstr)
            return engine.AsPstrString(engine.Deref(c.AsHeapIndex));
        // Chunk 131d: a missing format string is instantiation_error;
        // a wrong-typed one is type_error(atom).
        if (d.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        throw new PrologRuntimeException("type_error", "atom");
    }

    private static List<Cell> ReadProperListAsCells(Engine engine, Cell c, string builtinName)
    {
        var result = new List<Cell>();
        Cell cur = Resolve(engine, c);
        while (cur.Tag == Tag.Lis)
        {
            result.Add(engine.GetHeap(cur.AsHeapIndex));
            cur = Resolve(engine, engine.GetHeap(cur.AsHeapIndex + 1));
        }
        if (cur.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        if (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId)
            // Chunk 131d: argument list isn't a proper list — ISO type_error(list, _).
            throw new PrologRuntimeException("type_error", "list");
        return result;
    }

    private static Cell Resolve(Engine engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        return engine.GetHeap(engine.Deref(c.AsHeapIndex));
    }
}
