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
    private readonly OperatorTable _operators;
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
        _parser = new Parser(lexer, operators, flags);
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

            if (clause.Kind == ClauseKind.Directive)
                ProcessDirective(clause);

            yield return clause;
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
    }

    /// <summary>Chunk 152 — ISO §6.4.2 / §8.14.9. The directive
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

        foreach (string name in ExpandNames(args[2], pos))
            _operators.Define(name, precedence, type);
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
