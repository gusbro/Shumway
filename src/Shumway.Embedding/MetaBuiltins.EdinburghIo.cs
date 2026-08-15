using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

public static partial class MetaBuiltins
{
    // ============================================================================
    // Edinburgh-style I/O (Arity-Prolog compatible).
    // Thin layer over the StreamRegistry: see/tell open a file and
    // make it the current input/output; seen/told close it and revert to
    // user_input/user_output. get/get0/put/skip operate on character codes.
    // ============================================================================

    public static bool See1(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "see/1");
        string path = RequireAtomPath(engine, register: 0, builtin: "see/1");
        var streams = engine.Streams!;
        // real Edinburgh see/1 semantics:
        // several input files may be open at once, and see/1 on a file that
        // is ALREADY open makes it current again, RESUMING at its position
        // (only seen/0 closes). The previous behaviour (close the old file,
        // reopen from scratch) broke the classic nested-include idiom —
        // `seeing(F) … see(Inner) … seen, see(F)` restarted F from the top,
        // so the outer file's clauses were read twice.
        string full = System.IO.Path.GetFullPath(path);
        foreach (var cand in streams.All())
        {
            if (cand.IsReader && !cand.Closed && cand.Filename is not null
                && System.IO.Path.GetFullPath(cand.Filename) == full)
            {
                streams.SetCurrentInput(cand);
                return true;
            }
        }
        StreamHandle h;
        try
        {
            h = new StreamHandle(
                streams.NextId(), new StreamReader(path), "read", path);
        }
        catch (FileNotFoundException)
        {
            throw new Shumway.Core.PrologRuntimeException(
                $"existence_error(source_sink, '{path}')");
        }
        catch (DirectoryNotFoundException)
        {
            throw new Shumway.Core.PrologRuntimeException(
                $"existence_error(source_sink, '{path}')");
        }
        streams.Add(h);
        streams.SetCurrentInput(h);
        return true;
    }

    public static bool Seeing1(Activation engine)
    {
        var streams = engine.Streams!;
        Cell nameCell = ReferenceEquals(streams.CurrentInput, streams.UserInput)
            ? Cell.Atom(AtomTable.Intern("user", permanent: true).Id)
            : Cell.Atom(AtomTable.Intern(
                streams.CurrentInput.Filename ?? "user", permanent: true).Id);
        return engine.UnifyRegisterWithCell(0, nameCell);
    }

    public static bool Seen0(Activation engine)
    {
        var streams = engine.Streams!;
        if (!ReferenceEquals(streams.CurrentInput, streams.UserInput))
            CloseAndForget(streams, streams.CurrentInput);
        streams.SetCurrentInput(streams.UserInput);
        return true;
    }

    public static bool Tell1(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "tell/1");
        string path = RequireAtomPath(engine, register: 0, builtin: "tell/1");
        var streams = engine.Streams!;
        // Edinburgh tell/1 mirrors see/1 (see See1's note): several
        // output files may be open at once; tell/1 on an already-open file
        // makes it current again, APPENDING where it left off. Only told/0
        // closes. The classic multi-output juggle depends on this:
        // `tell(a), telling(SP), tell(b), … tell(SP), told`.
        string full = System.IO.Path.GetFullPath(path);
        foreach (var cand in streams.All())
        {
            if (!cand.IsReader && !cand.Closed && cand.Filename is not null
                && cand.Writer is not null
                && System.IO.Path.GetFullPath(cand.Filename) == full)
            {
                streams.SetCurrentOutput(cand);
                return true;
            }
        }
        StreamHandle h;
        try
        {
            h = new StreamHandle(
                streams.NextId(), new StreamWriter(path, append: false) { NewLine = "\n" }, "write", path);
        }
        catch (DirectoryNotFoundException)
        {
            throw new Shumway.Core.PrologRuntimeException(
                $"existence_error(source_sink, '{path}')");
        }
        streams.Add(h);
        streams.SetCurrentOutput(h);
        return true;
    }

    public static bool Telling1(Activation engine)
    {
        var streams = engine.Streams!;
        Cell nameCell = ReferenceEquals(streams.CurrentOutput, streams.UserOutput)
            ? Cell.Atom(AtomTable.Intern("user", permanent: true).Id)
            : Cell.Atom(AtomTable.Intern(
                streams.CurrentOutput.Filename ?? "user", permanent: true).Id);
        return engine.UnifyRegisterWithCell(0, nameCell);
    }

    public static bool Told0(Activation engine)
    {
        var streams = engine.Streams!;
        if (!ReferenceEquals(streams.CurrentOutput, streams.UserOutput))
            CloseAndForget(streams, streams.CurrentOutput);
        streams.SetCurrentOutput(streams.UserOutput);
        return true;
    }

    private static void CloseAndForget(StreamRegistry registry, StreamHandle h)
    {
        try { h.Reader?.Dispose(); h.Writer?.Flush(); h.Writer?.Dispose(); }
        catch { /* best-effort close */ }
        registry.Remove(h);
    }

    // ---- get / get0 / put / skip — character-code I/O ----

    public static bool Get1(Activation engine) => ReadPrintableCodeImpl(engine, useStreamReg: false);
    public static bool Get2(Activation engine) => ReadPrintableCodeImpl(engine, useStreamReg: true);
    public static bool Get0_1(Activation engine) => ReadAnyCodeImpl(engine, useStreamReg: false);
    public static bool Get0_2(Activation engine) => ReadAnyCodeImpl(engine, useStreamReg: true);
    public static bool Put1(Activation engine) => WriteCodeImpl(engine, useStreamReg: false);
    public static bool Put2(Activation engine) => WriteCodeImpl(engine, useStreamReg: true);
    public static bool Skip1(Activation engine) => SkipImpl(engine, useStreamReg: false);
    public static bool Skip2(Activation engine) => SkipImpl(engine, useStreamReg: true);

    // Both resolvers delegate to the ONE canonical stream-arg resolver:
    // a stream-term is `'$stream'(Id)` looked up in the registry, and there
    // must not be a second place that decides what a stream argument is.
    private static StreamHandle ResolveInputStream(Activation engine, bool fromStreamArg)
        => fromStreamArg
            ? Shumway.Builtins.StreamBuiltins.ResolveStream(engine, engine.GetRegister(0))
            : engine.Streams!.CurrentInput;

    private static StreamHandle ResolveOutputStream(Activation engine, bool fromStreamArg)
        => fromStreamArg
            ? Shumway.Builtins.StreamBuiltins.ResolveStream(engine, engine.GetRegister(0))
            : engine.Streams!.CurrentOutput;

    private static bool ReadPrintableCodeImpl(Activation engine, bool useStreamReg)
    {
        var h = ResolveInputStream(engine, useStreamReg);
        if (!h.IsReader)
            throw new Shumway.Core.PrologRuntimeException("permission_error(input, stream)");
        // Skip codes < 32 (ASCII control / whitespace).
        int code;
        do { code = h.Reader!.Read(); }
        while (code >= 0 && code < 32);
        int regOut = useStreamReg ? 1 : 0;
        return engine.UnifyRegisterWithCell(regOut, Cell.Int(code));
    }

    private static bool ReadAnyCodeImpl(Activation engine, bool useStreamReg)
    {
        var h = ResolveInputStream(engine, useStreamReg);
        if (!h.IsReader)
            throw new Shumway.Core.PrologRuntimeException("permission_error(input, stream)");
        int code = h.Reader!.Read();
        int regOut = useStreamReg ? 1 : 0;
        return engine.UnifyRegisterWithCell(regOut, Cell.Int(code));
    }

    private static bool WriteCodeImpl(Activation engine, bool useStreamReg)
    {
        var h = ResolveOutputStream(engine, useStreamReg);
        if (!h.IsWriter)
            throw new Shumway.Core.PrologRuntimeException("permission_error(output, stream)");
        int regCode = useStreamReg ? 1 : 0;
        Cell c = MaterializeRegisterAsCell(engine, regCode);
        if (c.Tag == Tag.Ref || c.Tag == Tag.AttVar)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (c.Tag != Tag.Int)
            throw new Shumway.Core.PrologRuntimeException("type_error(integer, _)");
        long code = c.AsInt;
        if (code < 0 || code > char.MaxValue)
            throw new Shumway.Core.PrologRuntimeException(
                "representation_error(character_code)");
        h.Writer!.Write((char)code);
        return true;
    }

    private static bool SkipImpl(Activation engine, bool useStreamReg)
    {
        var h = ResolveInputStream(engine, useStreamReg);
        if (!h.IsReader)
            throw new Shumway.Core.PrologRuntimeException("permission_error(input, stream)");
        int regCode = useStreamReg ? 1 : 0;
        Cell c = MaterializeRegisterAsCell(engine, regCode);
        if (c.Tag == Tag.Ref || c.Tag == Tag.AttVar)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (c.Tag != Tag.Int)
            throw new Shumway.Core.PrologRuntimeException("type_error(integer, _)");
        int target = (int)c.AsInt;
        int code;
        do { code = h.Reader!.Read(); }
        while (code >= 0 && code != target);
        return true;
    }

    public static bool Tab2(Activation engine)
    {
        var h = ResolveOutputStream(engine, fromStreamArg: true);
        if (!h.IsWriter)
            throw new Shumway.Core.PrologRuntimeException("permission_error(output, stream)");
        Cell n = MaterializeRegisterAsCell(engine, 1);
        if (n.Tag == Tag.Ref || n.Tag == Tag.AttVar)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (n.Tag != Tag.Int)
            throw new Shumway.Core.PrologRuntimeException("type_error(integer, _)");
        long count = n.AsInt;
        for (long i = 0; i < count; i++) h.Writer!.Write(' ');
        return true;
    }

    // ============================================================================
    // Arity-Prolog string<->term + search.
    // "string" in Arity means atom; these are write-/writeq-style variants of
    // term_to_atom/2, plus a backtrackable substring search.
    // ============================================================================

    public static bool StringTerm2(Activation engine) => StringTermImpl(engine, quoted: false);
    public static bool StringTermq2(Activation engine) => StringTermImpl(engine, quoted: true);

    private static bool StringTermImpl(Activation engine, bool quoted)
    {
        Cell atomCell = ResolveLocal(engine, engine.GetRegister(0));

        if (atomCell.Tag == Tag.Atom)
        {
            // Atom -> Term: parse the atom name. The parser expects a
            // clause-terminating dot; append one if the user didn't.
            string text = AtomTable.GetById(atomCell.AsAtomId)?.Name ?? "";
            string source = text.TrimEnd().EndsWith(".", StringComparison.Ordinal)
                ? text : text + ".";
            // Parse with the engine's LIVE operator table so `:- op/3`-defined
            // operators read back exactly as the term->atom direction writes them
            // (parsing with the default table made string_term/2 not an inverse).
            var ops = (engine.Host as PrologEngine)?.Operators
                ?? Shumway.Compiler.Parsing.OperatorTable.Default();
            Term parsed;
            try
            {
                var parser = new Shumway.Compiler.Parsing.Parser(
                    new Shumway.Compiler.Lexer.Lexer(source), ops);
                parsed = parser.ReadClauseTerm();
            }
            catch (Exception ex) when (ex is Shumway.Compiler.Parsing.ParseException
                                        or Shumway.Compiler.Lexer.LexerException)
            {
                // Malformed text is a catchable ISO syntax error, not a .NET crash.
                throw new Shumway.Core.PrologRuntimeException("syntax_error", ex.Message);
            }
            Cell newCell = Materializer.MaterializeAsCell(engine, parsed);
            return engine.UnifyRegisterWithCell(1, newCell);
        }

        // Term -> Atom: render with the requested quoting style and
        // intern the result as a fresh atom.
        using var sw = new System.IO.StringWriter();
        Shumway.Builtins.TermRenderer.Render(engine, engine.GetRegister(1), sw,
            new Shumway.Builtins.TermRenderOptions
            {
                Operators = engine.Operators,
                Quoted = quoted,
            });
        string rendered = sw.ToString();
        int newAtomId = AtomTable.Intern(rendered, permanent: false).Id;
        return engine.UnifyRegisterWithCell(0, Cell.Atom(newAtomId));
    }

    public static bool StringSearch3(Activation engine)
        => StringSearchImpl(engine, subReg: 0, hayReg: 1, locReg: 2, arity: 3,
            StringComparison.Ordinal);

    /// <summary>Arity <c>string_search(+Case, +SubString, +String, -Location)</c>:
    /// Case = 0 → case-sensitive, Case = 1 → case-insensitive. Locations are
    /// 0-based (per ARITY.HLP) and enumerate on backtracking.</summary>
    public static bool StringSearch4(Activation engine)
    {
        Cell caseCell = MaterializeRegisterAsCell(engine, 0);
        if (caseCell.Tag == Tag.Ref || caseCell.Tag == Tag.AttVar)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (caseCell.Tag != Tag.Int || caseCell.AsInt is not (0 or 1))
            throw new Shumway.Core.PrologRuntimeException("domain_error", "case_flag");
        var cmp = caseCell.AsInt == 1
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return StringSearchImpl(engine, subReg: 1, hayReg: 2, locReg: 3, arity: 4, cmp);
    }

    private static bool StringSearchImpl(Activation engine, int subReg, int hayReg,
        int locReg, int arity, StringComparison cmp)
    {
        Cell subCell = MaterializeRegisterAsCell(engine, subReg);
        Cell haystackCell = MaterializeRegisterAsCell(engine, hayReg);
        if (subCell.Tag == Tag.Ref || subCell.Tag == Tag.AttVar
            || haystackCell.Tag == Tag.Ref || haystackCell.Tag == Tag.AttVar)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (subCell.Tag != Tag.Atom)
            throw new Shumway.Core.PrologRuntimeException("type_error", "atom");
        if (haystackCell.Tag != Tag.Atom)
            throw new Shumway.Core.PrologRuntimeException("type_error", "atom");
        string sub = AtomTable.GetById(subCell.AsAtomId)?.Name ?? "";
        string hay = AtomTable.GetById(haystackCell.AsAtomId)?.Name ?? "";
        if (sub.Length == 0) return engine.UnifyRegisterWithCell(locReg, Cell.Int(0));

        // Walk the haystack collecting every match position so we can
        // backtrack through them via PushBuiltinChoicePoint.
        var positions = new List<int>();
        int start = 0;
        while (start <= hay.Length - sub.Length)
        {
            int idx = hay.IndexOf(sub, start, cmp);
            if (idx < 0) break;
            positions.Add(idx);
            start = idx + 1;
        }
        if (positions.Count == 0) return false;
        int returnPc = engine.BuiltinReturnPc;
        return IndexEnumCursor.Start(engine, positions.Count, arity, returnPc,
            (e, i) => engine.UnifyRegisterWithCell(locReg, Cell.Int(positions[i])));
    }

}
