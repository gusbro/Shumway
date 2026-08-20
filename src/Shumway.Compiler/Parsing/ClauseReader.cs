using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;

namespace Shumway.Compiler.Parsing;

/// <summary>
/// Reads a Prolog source one clause at a time. Each iteration of
/// <see cref="ReadAll"/> parses the next clause (up to and including the
/// terminating dot) and yields a <see cref="Clause"/>. The reader owns an
/// <see cref="OperatorTable"/> and executes <c>:- op(...)</c> directives in place
/// the moment they're recognised, so operator declarations affect the parsing of
/// every subsequent clause in the same source — matching the standard Prolog
/// consult semantics.
///
/// <para>Other directives (<c>:- dynamic</c>, <c>:- public</c>, etc.) are recognised
/// only insofar as they're classified <see cref="ClauseKind.Directive"/>; their
/// effects on the predicate table are deferred to the consult/compile pass.</para>
/// </summary>
public sealed class ClauseReader
{
    private readonly Parser _parser;
    private OperatorTable _operators;

    /// <summary>ADR-046 — maps a module name to its operator layer (a table
    /// parented by the base/user table this reader started with). Set by the
    /// embedding consult and the shmo compiler; when null, a
    /// <c>:- module/2</c> directive keeps the current table (legacy global
    /// behaviour).</summary>
    public Func<string, OperatorTable>? ModuleLayerProvider;

    /// <summary>The table the reader is CURRENTLY parsing with — the module
    /// layer after a <c>:- module/2</c> switch, else the table it was
    /// constructed with. The consult pipeline injects imported operators
    /// here when a mid-file <c>use_module</c> brings some in.</summary>
    public OperatorTable CurrentOperators => _operators;
    private readonly PrologFlags _flags;

    public ClauseReader(string source)
        : this(new global::Shumway.Compiler.Lexer.Lexer(source), OperatorTable.Default())
    {
    }

    public ClauseReader(global::Shumway.Compiler.Lexer.Lexer lexer, OperatorTable operators)
        : this(lexer, operators, new PrologFlags())
    {
    }

    public ClauseReader(
        global::Shumway.Compiler.Lexer.Lexer lexer,
        OperatorTable operators,
        PrologFlags flags)
    {
        ArgumentNullException.ThrowIfNull(lexer);
        ArgumentNullException.ThrowIfNull(operators);
        ArgumentNullException.ThrowIfNull(flags);
        _operators = operators;
        _flags = flags;
        _lexer = lexer;
        // The flag affects LEXING ($...$ atoms, #line markers),
        // so the lexer adopts the caller's setting up front; the
        // set_prolog_flag directive (below) can also flip it mid-file.
        lexer.ArityCompat = flags.ArityCompat;
        lexer.DigitSeparators = flags.DigitSeparators;
        lexer.LenientQuoteCharLiteral = flags.LenientQuoteCharLiteral;
        lexer.LenientEscapes = flags.LenientEscapes;
        if (flags.ArityCompat) DefineArityCompatOperators(operators);
        _parser = new Parser(lexer, operators, flags);
    }

    /// <summary>Operators Arity sources rely on for their
    /// declaration directives. <c>:- extrn foo/3:far, bar/2.</c>
    /// declares external predicates; parsed like the other declaration
    /// prefixes (fx 1150) so the directive at least round-trips —
    /// downstream it's an unrecognised directive and surfaces as an
    /// arity_compat warning rather than a parse error.</summary>
    private static void DefineArityCompatOperators(OperatorTable operators)
    {
        operators.Define("extrn", 1150, OperatorType.Fx);
        // Arity spells negation `not Goal`; the standard tables keep `not`
        // a plain atom (ISO/GNU/SWI — `X == not` must parse).
        operators.Define("not", 900, OperatorType.Fy);
    }

    private readonly global::Shumway.Compiler.Lexer.Lexer _lexer;

    // ------------------------------------------------------------------
    // `:- define(TermA = TermB).` (ALWAYS active, not gated
    // by arity_compat). After the directive, every subterm of a
    // subsequent clause that is value-equal to TermA is replaced by
    // TermB. The directive is consumed here — it never reaches the
    // compile/consult directive pass. Fast path: the overwhelmingly
    // common LHS is an atom, probed O(1) by name; non-atom LHSs go in a
    // (rare) linear list.
    // ------------------------------------------------------------------
    private Dictionary<string, Term>? _atomDefines;
    private List<(Term Lhs, Term Rhs)>? _otherDefines;

    /// <summary>Recognises and records a
    /// <c>:- define(TermA = TermB).</c> directive. Returns true when the
    /// directive was consumed here (the caller drops it). Semantic
    /// choices:
    /// <list type="bullet">
    /// <item>Always active — NOT gated by arity_compat. The directive
    /// has no clash with any ISO directive name.</item>
    /// <item>A redefinition of the same LHS overwrites the earlier
    /// mapping (last definition wins from that point in the source
    /// on).</item>
    /// <item>A structurally wrong define (<c>:- define(x).</c> without
    /// <c>=</c>, wrong arity, …) throws <see cref="ParseException"/> —
    /// an error diagnostic in the collecting path, never a crash.</item>
    /// </list></summary>
    private bool TryHandleDefineDirective(Clause clause)
    {
        if (clause.Term is not CompoundTerm d || d.Args.Length != 1) return false;
        if (d.Args[0] is not CompoundTerm { Functor: "define" } def) return false;
        if (def.Args.Length != 1
            || def.Args[0] is not CompoundTerm { Functor: "=", Args.Length: 2 } eq)
            throw new ParseException(
                "define directive: expected :- define(TermA = TermB).",
                clause.Position);

        Term lhs = eq.Args[0];
        Term rhs = eq.Args[1];
        if (lhs is AtomTerm lhsAtom)
        {
            // Fast path: atom LHS keyed by name, O(1) probe per atom
            // subterm during substitution.
            (_atomDefines ??= new Dictionary<string, Term>())[lhsAtom.Name] = rhs;
        }
        else
        {
            _otherDefines ??= new List<(Term, Term)>();
            for (int i = 0; i < _otherDefines.Count; i++)
            {
                if (_otherDefines[i].Lhs.Equals(lhs))
                {
                    _otherDefines[i] = (lhs, rhs);
                    return true;
                }
            }
            _otherDefines.Add((lhs, rhs));
        }
        return true;
    }

    /// <summary>Applies every active define to
    /// <paramref name="clause"/>'s term. Semantic choices:
    /// <list type="bullet">
    /// <item>SINGLE PASS: every subterm is checked once, top-down,
    /// against ALL active defines together. A substituted result is NOT
    /// re-scanned — <c>define(a = f(a))</c> cannot loop, and with both
    /// <c>define(a = b)</c> and <c>define(b = c)</c> active, <c>a</c>
    /// rewrites to <c>b</c>, not <c>c</c> (no re-expansion).</item>
    /// <item>A matched subterm's interior is not walked — the RHS is
    /// inserted verbatim (and the immutable RHS node is shared by every
    /// substitution site).</item>
    /// <item>Functor NAMES are not renamed: <c>define(f = g)</c>
    /// rewrites the ATOM <c>f</c> wherever it occurs as a (sub)term, but
    /// <c>f(1)</c> keeps its functor — a compound's name is not a
    /// subterm position.</item>
    /// <item>Defines do not apply inside another define directive's own
    /// arguments (callers consume defines BEFORE calling this).</item>
    /// </list></summary>
    private Clause ApplyDefines(Clause clause)
    {
        if (_atomDefines is null && _otherDefines is null) return clause;
        Term substituted = SubstituteDefines(clause.Term);
        // Re-classify: substitution at the clause's top level could in
        // principle change its shape (e.g. an atom clause rewritten to a
        // :-/2 term).
        return ReferenceEquals(substituted, clause.Term)
            ? clause
            : Clause.From(substituted);
    }

    private Term SubstituteDefines(Term t)
    {
        // Whole-subterm match first (top-down). Atom subterms only ever
        // match the name-keyed dictionary (an AtomTerm LHS always lands
        // there); everything else probes the rare non-atom list.
        if (t is AtomTerm a)
        {
            if (_atomDefines is not null
                && _atomDefines.TryGetValue(a.Name, out Term? atomRhs))
                return atomRhs;
            return t;
        }
        if (_otherDefines is not null)
        {
            foreach (var (lhs, rhs) in _otherDefines)
                if (lhs.Equals(t)) return rhs;
        }
        if (t is CompoundTerm c)
        {
            Term[]? newArgs = null;
            for (int i = 0; i < c.Args.Length; i++)
            {
                Term s = SubstituteDefines(c.Args[i]);
                if (newArgs is null && !ReferenceEquals(s, c.Args[i]))
                    newArgs = (Term[])c.Args.Clone();
                if (newArgs is not null) newArgs[i] = s;
            }
            if (newArgs is not null)
                return new CompoundTerm(c.Functor, newArgs) { Position = c.Position };
        }
        return t;
    }

    public OperatorTable Operators => _operators;

    /// <summary>Parses every clause in the underlying source and yields them in
    /// order. Returns once the underlying token stream reports EOF.</summary>
    public IEnumerable<Clause> ReadAll()
    {
        while (!_parser.IsAtEnd())
        {
            Term term = _parser.ReadClauseTerm();
            Clause clause = Clause.From(term);

            // A `:- define(A = B).` directive is consumed
            // here, BEFORE substitution — active defines are not applied
            // inside another define directive's own arguments.
            if (clause.Kind == ClauseKind.Directive
                && TryHandleDefineDirective(clause))
                continue;

            clause = ApplyDefines(clause);

            if (clause.Kind == ClauseKind.Directive)
            {
                ProcessDirective(clause);
                if (TryHandleAritySectionDirective(clause, out var sectionRepl))
                {
                    // `:- c.` → captured `$native_decls` directive; `:- prolog.` → drop.
                    if (sectionRepl is not null) yield return sectionRepl;
                    continue;
                }
            }

            yield return clause;
        }
    }

    /// <summary>arity_compat only — Arity native-code
    /// sections. <c>:- c.</c> switches the source to embedded C that
    /// must be skipped RAW (it isn't parseable as Prolog) until a line
    /// starting with the directive <c>:- prolog.</c> (or EOF, which
    /// ends the module normally); <c>:- prolog.</c> met in normal
    /// Prolog mode is a silent no-op. Returns true when the directive
    /// was consumed here (the caller drops it — it never reaches the
    /// compile/consult directive pass).</summary>
    private bool TryHandleAritySectionDirective(Clause clause, out Clause? replacement)
    {
        replacement = null;
        if (!_flags.ArityCompat) return false;
        if (clause.Term is not CompoundTerm d || d.Args.Length != 1
            || d.Args[0] is not AtomTerm a)
            return false;
        if (a.Name == "c")
        {
            // The lexer takes over the raw character stream; a stale
            // peeked token would replay ahead of the resumed input.
            _parser.DiscardLookahead();
            string declsText = _lexer.SkipNativeCodeSection();
            // ADR-022 — capture the raw `:- c` declaration
            // text in a synthetic `:- '$native_decls'(RawText)` directive (raw
            // text as a non-interned StringTerm) so a later stage can hand it to
            // the C-subset parser. Until then it is an ignored directive
            // (`$native_decls` is in ShmoCompiler.RecognizedDirectives so no
            // arity_compat warning fires).
            replacement = Clause.From(new CompoundTerm(":-",
                new Term[]
                {
                    new CompoundTerm("$native_decls",
                        new Term[] { new StringTerm(declsText, Shumway.Core.TextKind.Codes) { Position = clause.Position } })
                        { Position = clause.Position },
                })
                { Position = clause.Position });
            return true;
        }
        return a.Name == "prolog";
    }

    /// <summary>Like <see cref="ReadAll"/> but with C-style error
    /// recovery: a <see cref="ParseException"/> on one clause is
    /// captured as a <see cref="ClauseOrError.Error"/> entry and the
    /// reader resyncs to the next clause-terminator dot before trying
    /// the next clause. Stops yielding once
    /// <paramref name="maxErrors"/> errors have accumulated (default
    /// 100) — preventing a hopelessly malformed file from drowning
    /// the diagnostics stream.</summary>
    public IEnumerable<ClauseOrError> ReadAllCollectingErrors(int maxErrors = 100)
    {
        int errors = 0;
        while (true)
        {
            Clause? parsed = null;
            ClauseOrError? errorEntry = null;
            bool atEnd = false;
            try
            {
                // IsAtEnd peeks the next token, so it too can
                // throw on unlexable input — it must sit inside the
                // recovery try or a bad character right after a clause
                // escapes as a crash.
                if (_parser.IsAtEnd())
                {
                    atEnd = true;
                }
                else
                {
                    Term term = _parser.ReadClauseTerm();
                    parsed = Clause.From(term);
                    if (parsed.Kind == ClauseKind.Directive)
                    {
                        try
                        {
                            // define directives are consumed
                            // pre-substitution (a malformed one — e.g.
                            // `:- define(x).` without `=` — throws and is
                            // captured as a diagnostic like any other
                            // directive error).
                            if (TryHandleDefineDirective(parsed))
                            {
                                parsed = null;   // consumed
                            }
                            else
                            {
                                parsed = ApplyDefines(parsed);
                                ProcessDirective(parsed);
                            }
                        }
                        catch (ParseException dirEx)
                        {
                            // A directive that parsed structurally but
                            // failed in ApplyOpDirective / etc. counts as
                            // an error but the directive clause itself
                            // is discarded.
                            errorEntry = ClauseOrError.Error(dirEx.Message, dirEx.Position);
                            parsed = null;
                        }
                        if (parsed is not null
                            && TryHandleAritySectionDirective(parsed, out var sectionRepl))
                            // `:- c.` → captured `$native_decls` directive (yielded
                            // below); `:- prolog.` → null (dropped).
                            parsed = sectionRepl;
                    }
                    else
                    {
                        parsed = ApplyDefines(parsed);
                    }
                }
            }
            catch (ParseException ex)
            {
                errorEntry = ClauseOrError.Error(ex.Message, ex.Position);
                _parser.SkipToClauseTerminator();
            }
            catch (LexerException ex)
            {
                // A tokenizer error (e.g. Arity's backquote
                // char literals — a character Shumway has no lexeme
                // for) is a diagnostic like any parse error, not a
                // crash. SkipToClauseTerminator steps over unlexable
                // characters raw, so the resync always progresses.
                errorEntry = ClauseOrError.Error(ex.Message, ex.Position);
                _parser.SkipToClauseTerminator();
            }
            if (atEnd) yield break;

            if (errorEntry is not null)
            {
                yield return errorEntry;
                errors++;
                if (errors >= maxErrors)
                {
                    yield return ClauseOrError.Error(
                        $"Too many parse errors ({errors}); stopping.",
                        default);
                    yield break;
                }
                continue;
            }

            if (parsed is not null)
                yield return ClauseOrError.Ok(parsed);
        }
    }

    private void ProcessDirective(Clause clause)
    {
        // The clause's term is :-/1; the actual directive body is its only arg.
        if (clause.Term is not CompoundTerm dCompound || dCompound.Args.Length != 1) return;
        Term body = dCompound.Args[0];

        if (body is CompoundTerm { Functor: "op", Args: var opArgs } && opArgs.Length == 3)
            ApplyOpDirective(opArgs, clause.Position);
        else if (body is CompoundTerm { Functor: "set_prolog_flag", Args: var spfArgs }
                 && spfArgs.Length == 2)
            ApplySetPrologFlagDirective(spfArgs, clause.Position);
        else if (body is CompoundTerm { Functor: "char_conversion", Args: var ccArgs }
                 && ccArgs.Length == 2)
            ApplyCharConversionDirective(ccArgs, clause.Position);
        else if (body is CompoundTerm { Functor: "module", Args: [var modName, var exportList] })
        {
            // ADR-046 — the rest of the file parses with the module's own
            // operator layer; ops in the export list land there too.
            if (ModuleLayerProvider is not null && modName is AtomTerm modAtom)
            {
                _operators = ModuleLayerProvider(modAtom.Name);
                _parser.SwitchOperators(_operators);
            }
            ApplyModuleListOps(exportList, clause.Position);
        }
    }

    /// <summary>SICStus/Scryer allow <c>op(P, T, N)</c> entries in a module's export
    /// list (<c>:- module(atts, [op(1199, fx, attribute), ...])</c>): the operator is
    /// active for the rest of the module's own source AND becomes importable. We
    /// activate each here, in place, so the module body parses with them — the same
    /// point a standalone <c>:- op</c> directive would take effect.</summary>
    private void ApplyModuleListOps(Term exportList, SourcePosition pos)
    {
        Term cursor = exportList;
        while (cursor is CompoundTerm { Functor: ".", Args: [var element, var rest] })
        {
            if (element is CompoundTerm { Functor: "op", Args: var opArgs } && opArgs.Length == 3)
                ApplyOpDirective(opArgs, pos);
            cursor = rest;
        }
    }

    /// <summary>ISO §6.4.2 / §8.14.9. The directive
    /// <c>:- char_conversion(In, Out)</c> registers an in-character
    /// to out-character mapping that the lexer applies to the start
    /// of the next token. An identity mapping (<c>In == Out</c>)
    /// removes the entry per ISO. <c>In</c> and <c>Out</c> must be
    /// one-character atoms.</summary>
    private void ApplyCharConversionDirective(Term[] args, SourcePosition pos)
    {
        if (args[0] is not AtomTerm inAtom || inAtom.Name.Length != 1)
            throw new ParseException(
                "char_conversion/2 directive: first argument must be a one-character atom.", pos);
        if (args[1] is not AtomTerm outAtom || outAtom.Name.Length != 1)
            throw new ParseException(
                "char_conversion/2 directive: second argument must be a one-character atom.", pos);
        char inCh = inAtom.Name[0];
        char outCh = outAtom.Name[0];
        if (inCh == outCh)
            _flags.CharConversion.Remove(inCh);
        else
            _flags.CharConversion[inCh] = outCh;
    }

    private void ApplySetPrologFlagDirective(Term[] args, SourcePosition pos)
    {
        if (args[0] is not AtomTerm flagName)
            throw new ParseException(
                "set_prolog_flag/2 directive: first argument must be an atom.", pos);
        if (args[1] is not AtomTerm valueName)
            throw new ParseException(
                "set_prolog_flag/2 directive: second argument must be an atom.", pos);
        if (flagName.Name == "double_quotes")
        {
            _flags.DoubleQuotes = valueName.Name switch
            {
                "codes"  => DoubleQuotesMode.Codes,
                "chars"  => DoubleQuotesMode.Chars,
                "atom"   => DoubleQuotesMode.Atom,
                "string" => DoubleQuotesMode.String,
                _ => throw new ParseException(
                    $"set_prolog_flag/2 directive: unknown double_quotes value '{valueName.Name}' "
                    + "(expected codes / chars / atom / string).", pos),
            };
        }
        else if (flagName.Name == "arity_compat")
        {
            // Must take effect during LEXING (it gates the
            // $...$ atom syntax and #line markers), so flip the live
            // lexer too, like char_conversion does via its shared map.
            bool on = valueName.Name switch
            {
                "true" => true,
                "false" => false,
                _ => throw new ParseException(
                    $"set_prolog_flag/2 directive: unknown arity_compat value "
                    + $"'{valueName.Name}' (expected true / false).", pos),
            };
            _flags.ArityCompat = on;
            _lexer.ArityCompat = on;
            if (on) DefineArityCompatOperators(_operators);
        }
        // Unknown flags are silently ignored at parse time — the
        // runtime builtin raises a domain_error instead, which is
        // where the diagnostic is more useful.
    }

    private void ApplyOpDirective(Term[] args, SourcePosition pos)
    {
        if (args[0] is not IntTerm precTerm)
            throw new ParseException(
                "op/3 directive: first argument (precedence) must be an integer.", pos);
        if (args[1] is not AtomTerm typeTerm)
            throw new ParseException(
                "op/3 directive: second argument (type) must be an atom.", pos);

        int precedence = (int)precTerm.Value;
        OperatorType type = ParseOperatorType(typeTerm.Name, pos);

        // ADR-046 — `op(P, T, user:Name)` (SWI's escape) defines in the
        // ROOT (user/global) table regardless of the module being read;
        // `op(P, T, m:Name)` targets module m's layer. An unqualified name
        // defines in the current layer.
        Term nameSpec = args[2];
        OperatorTable target = _operators;
        if (nameSpec is CompoundTerm { Functor: ":", Args: [AtomTerm qual, var inner] })
        {
            nameSpec = inner;
            if (qual.Name == "user")
            {
                target = _operators;
                while (target.Parent is not null) target = target.Parent;
            }
            else if (ModuleLayerProvider is not null)
            {
                target = ModuleLayerProvider(qual.Name);
            }
        }
        foreach (string name in ExpandNames(nameSpec, pos))
            target.Define(name, precedence, type);
    }

    private static IEnumerable<string> ExpandNames(Term spec, SourcePosition pos)
    {
        // Single atom: the operator's name as-is.
        if (spec is AtomTerm a)
        {
            yield return a.Name;
            yield break;
        }

        // Otherwise a Prolog list of atoms: walk the cons cells.
        Term cursor = spec;
        while (cursor is CompoundTerm { Functor: ".", Args: var consArgs } && consArgs.Length == 2)
        {
            if (consArgs[0] is not AtomTerm element)
                throw new ParseException(
                    "op/3 directive: list elements must be atoms.", pos);
            yield return element.Name;
            cursor = consArgs[1];
        }

        if (cursor is not AtomTerm { Name: "[]" })
            throw new ParseException(
                "op/3 directive: third argument must be an atom or a proper list of atoms.",
                pos);
    }

    private static OperatorType ParseOperatorType(string s, SourcePosition pos) => s switch
    {
        "fx" => OperatorType.Fx,
        "fy" => OperatorType.Fy,
        "xf" => OperatorType.Xf,
        "yf" => OperatorType.Yf,
        "xfx" => OperatorType.Xfx,
        "xfy" => OperatorType.Xfy,
        "yfx" => OperatorType.Yfx,
        _ => throw new ParseException(
            $"op/3 directive: unknown operator type '{s}' "
            + "(expected one of fx, fy, xf, yf, xfx, xfy, yfx).", pos),
    };
}
