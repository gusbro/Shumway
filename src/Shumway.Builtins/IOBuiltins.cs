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
        TermRenderer.Render(engine, engine.GetRegister(0), engine.Out, DefaultOptions(engine));
        return true;
    }

    /// <summary><c>nl</c> — writes a single newline character.</summary>
    public static bool Nl(Engine engine)
    {
        engine.Out.WriteLine();
        return true;
    }

    /// <summary><c>writeln(X)</c> — equivalent to <c>write(X), nl</c>.</summary>
    public static bool Writeln(Engine engine)
    {
        TermRenderer.Render(engine, engine.GetRegister(0), engine.Out, DefaultOptions(engine));
        engine.Out.WriteLine();
        return true;
    }

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
        var options = new TermRenderOptions { Operators = engine.Operators };
        Cell optsCell = Resolve(engine, engine.GetRegister(1));
        while (optsCell.Tag == Tag.Lis)
        {
            int headIdx = optsCell.AsHeapIndex;
            Cell head = Resolve(engine, engine.GetHeap(headIdx));
            ApplyOption(engine, head, options);
            optsCell = Resolve(engine, engine.GetHeap(headIdx + 1));
        }
        TermRenderer.Render(engine, engine.GetRegister(0), engine.Out, options);
        return true;
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
        TermRenderer.Render(engine, engine.GetRegister(0), engine.Out, DefaultOptions(engine));
        return true;
    }

    /// <summary><c>print(X)</c> — ISO defines this as a portray/1 hook
    /// fallback to <c>write/1</c>. Phase 1 implements only the
    /// <c>write/1</c> fallback path.</summary>
    public static bool Print(Engine engine)
    {
        TermRenderer.Render(engine, engine.GetRegister(0), engine.Out, DefaultOptions(engine));
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
        Cell handle = Resolve(engine, engine.GetRegister(0));
        if (handle.Tag != Tag.Foreign)
            throw new PrologRuntimeException("type_error", "stream");
        var writer = engine.AsForeign<System.IO.StreamWriter>(handle);
        if (writer is null)
            throw new PrologRuntimeException("existence_error", "stream");
        return FormatImpl(engine, writer, fmtReg: 1, argsReg: 2, "format/3");
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
