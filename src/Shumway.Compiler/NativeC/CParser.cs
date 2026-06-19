namespace Shumway.Compiler.NativeC;

/// <summary>Recursive-descent parser for the embedded-C subset (ADR-022), with
/// two entry points: <see cref="ParseStatements"/> for a <c>{ … }</c> native goal
/// (strict — the blocks are well-formed) and <see cref="ParseDeclarations"/> for a
/// <c>:- c</c> region (lenient — it skips struct/union/function-pointer typedefs
/// and any other shape it does not model, so the noise of preprocessed headers
/// does not fail the whole region).</summary>
public sealed class CParser
{
    private readonly List<CToken> _t;
    private int _i;

    private CParser(List<CToken> tokens) { _t = tokens; }

    private static readonly HashSet<string> TypeKeywords = new()
    {
        "void", "char", "short", "int", "long", "float", "double", "unsigned", "signed",
    };

    private CToken Peek(int ahead = 0)
        => _i + ahead < _t.Count ? _t[_i + ahead] : _t[^1];
    private CToken Next() { var t = Peek(); if (_i < _t.Count - 1) _i++; return t; }
    private bool Is(CTokenKind k) => Peek().Kind == k;
    private bool IsKw(string w) => Peek().Kind == CTokenKind.Ident && Peek().Text == w;

    private CToken Expect(CTokenKind k)
    {
        if (!Is(k)) throw new CParseException($"expected {k}, got {Peek()}", Peek().Offset);
        return Next();
    }

    // -----------------------------------------------------------------------
    // Entry points
    // -----------------------------------------------------------------------

    /// <summary>Parses a <c>{ … }</c> block's raw text into a statement list.</summary>
    public static List<CStmt> ParseStatements(string text)
    {
        var p = new CParser(CLexer.Tokenize(text));
        var stmts = new List<CStmt>();
        while (!p.Is(CTokenKind.Eof))
        {
            // Tolerate leading / repeated separators (`;` and `,` are
            // interchangeable in Arity native blocks).
            if (p.Is(CTokenKind.Semicolon) || p.Is(CTokenKind.Comma)) { p.Next(); continue; }
            int before = p._i;
            stmts.Add(p.ParseStatement());
            if (p._i == before)
                throw new CParseException($"no progress at {p.Peek()}", p.Peek().Offset);
        }
        return stmts;
    }

    /// <summary>Parses a <c>:- c</c> region's raw text into a declaration list,
    /// skipping anything not modelled.</summary>
    public static List<CDecl> ParseDeclarations(string text)
    {
        var p = new CParser(CLexer.Tokenize(text));
        var decls = new List<CDecl>();
        while (!p.Is(CTokenKind.Eof))
        {
            int before = p._i;
            var d = p.TryParseDecl();
            if (d is not null) decls.Add(d);
            if (p._i == before) p.Next();   // guarantee progress past unparseable noise
        }
        return decls;
    }

    // -----------------------------------------------------------------------
    // Statements
    // -----------------------------------------------------------------------

    private CStmt ParseStatement()
    {
        // `Var : type`
        if (Is(CTokenKind.Ident) && Peek(1).Kind == CTokenKind.Colon)
        {
            string name = Next().Text;
            Expect(CTokenKind.Colon);
            return new CVarDeclStmt(name, ParseType());
        }

        CExpr left = ParseExpr();

        // `Var is Expr`
        if (IsKw("is"))
        {
            Next();
            CExpr value = ParseExpr();
            if (left is not CIdentExpr id)
                throw new CParseException("`is` requires a variable on the left", Peek().Offset);
            return new CBindStmt(id.Name, value);
        }

        // `lhs = Expr`
        if (Is(CTokenKind.Equals))
        {
            Next();
            return new CAssignStmt(left, ParseExpr());
        }

        // bare native call / expression statement
        return new CCallStmt(left);
    }

    // -----------------------------------------------------------------------
    // Expressions
    // -----------------------------------------------------------------------

    private CExpr ParseExpr() => ParseAdditive();

    private CExpr ParseAdditive()
    {
        var e = ParseMultiplicative();
        while (Is(CTokenKind.Plus) || Is(CTokenKind.Minus))
        {
            char op = Is(CTokenKind.Plus) ? '+' : '-';
            Next();
            e = new CBinaryExpr(op, e, ParseMultiplicative());
        }
        return e;
    }

    private CExpr ParseMultiplicative()
    {
        var e = ParseUnary();
        while (Is(CTokenKind.Star) || Is(CTokenKind.Slash))
        {
            char op = Is(CTokenKind.Star) ? '*' : '/';
            Next();
            e = new CBinaryExpr(op, e, ParseUnary());
        }
        return e;
    }

    private CExpr ParseUnary()
    {
        if (Is(CTokenKind.Amp)) { Next(); return new CAddrOfExpr(ParseUnary()); }
        if (Is(CTokenKind.Star)) { Next(); return new CDerefExpr(ParseUnary()); }
        return ParsePrimary();
    }

    private CExpr ParsePrimary()
    {
        var t = Peek();
        switch (t.Kind)
        {
            case CTokenKind.Int:
                Next();
                return new CIntExpr(t.IntValue);
            case CTokenKind.String:
                Next();
                return new CStringExpr(t.Text);
            case CTokenKind.QuotedName:
                Next();
                return Is(CTokenKind.LParen)
                    ? new CCallExpr(t.Text, ParseArgs())
                    : new CIdentExpr(t.Text);
            case CTokenKind.Ident:
                Next();
                if (t.Text == "void") return new CVoidExpr();   // a `(void)` argument
                return Is(CTokenKind.LParen)
                    ? new CCallExpr(t.Text, ParseArgs())
                    : new CIdentExpr(t.Text);
            case CTokenKind.LParen:
                Next();
                var inner = ParseExpr();
                Expect(CTokenKind.RParen);
                return inner;
            default:
                throw new CParseException($"unexpected {t} in expression", t.Offset);
        }
    }

    private List<CExpr> ParseArgs()
    {
        Expect(CTokenKind.LParen);
        var args = new List<CExpr>();
        if (!Is(CTokenKind.RParen))
        {
            while (true)
            {
                args.Add(ParseExpr());
                if (Is(CTokenKind.Comma)) { Next(); continue; }
                break;
            }
        }
        Expect(CTokenKind.RParen);
        // `f(void)` — the lone void marker means "no arguments".
        if (args is [CVoidExpr]) args.Clear();
        return args;
    }

    // -----------------------------------------------------------------------
    // Types (shared by statements and declarations)
    // -----------------------------------------------------------------------

    private CType ParseType()
    {
        var parts = new List<string>();
        while (Is(CTokenKind.Ident))
        {
            string w = Peek().Text;
            // Storage-class / cv qualifiers carry no type information — drop them
            // (`extern char achZtime[10];`, `const char*`, `static int x;`).
            if (w is "const" or "signed" or "extern" or "static" or "register"
                or "volatile" or "inline" or "__inline") { Next(); continue; }
            if (TypeKeywords.Contains(w)) { parts.Add(w); Next(); continue; }
            if (parts.Count == 0) { parts.Add(w); Next(); }        // a typedef name
            break;
        }
        if (parts.Count == 0)
            throw new CParseException($"expected a type, got {Peek()}", Peek().Offset);
        int depth = 0;
        while (Is(CTokenKind.Star)) { depth++; Next(); }
        return new CType(string.Join(" ", parts), depth);
    }

    // -----------------------------------------------------------------------
    // Declarations (lenient)
    // -----------------------------------------------------------------------

    private CDecl? TryParseDecl()
    {
        if (IsKw("typedef")) return TryParseTypedef();

        int start = _i;
        try
        {
            CType type = ParseType();
            if (!Is(CTokenKind.Ident)) { SkipDecl(); return null; }
            string name = Next().Text;

            if (Is(CTokenKind.LParen))   // function prototype
            {
                var (ps, variadic) = ParseProtoParams();
                SkipDecl();              // step over the `;` (and any trailing junk)
                return new CPrototype(name, type, ps, variadic);
            }
            if (Is(CTokenKind.LBracket)) // global array  `char buf[255];`
            {
                Next();
                int? len = Is(CTokenKind.Int) ? (int)Next().IntValue : null;
                if (Is(CTokenKind.RBracket)) Next();
                SkipDecl();
                return new CGlobalVar(name, type, len);
            }
            if (Is(CTokenKind.Semicolon)) // global scalar  `reftype p;`
            {
                Next();
                return new CGlobalVar(name, type, null);
            }
            SkipDecl();                  // multiple declarators / initialiser / etc.
            return null;
        }
        catch (CParseException)
        {
            _i = start;
            SkipDecl();
            return null;
        }
    }

    private CDecl? TryParseTypedef()
    {
        Next();   // 'typedef'
        // struct / union / enum typedefs are not modelled.
        if (IsKw("struct") || IsKw("union") || IsKw("enum")) { SkipDecl(); return null; }
        int start = _i;
        try
        {
            CType type = ParseType();
            if (!Is(CTokenKind.Ident)) { _i = start; SkipDecl(); return null; }
            string alias = Next().Text;
            if (Is(CTokenKind.Semicolon)) { Next(); return new CTypedef(alias, type); }
            SkipDecl();   // multiple aliases / function-pointer form — skip
            return null;
        }
        catch (CParseException)
        {
            _i = start;
            SkipDecl();
            return null;
        }
    }

    private (List<CParam> Params, bool Variadic) ParseProtoParams()
    {
        Expect(CTokenKind.LParen);
        var ps = new List<CParam>();
        bool variadic = false;
        if (!Is(CTokenKind.RParen))
        {
            while (true)
            {
                // C varargs `...` (three Other '.' tokens).
                if (Is(CTokenKind.Other) && Peek().Text == ".")
                {
                    while (Is(CTokenKind.Other) && Peek().Text == ".") Next();
                    variadic = true;
                    break;
                }
                CType pType = ParseType();
                string? pName = Is(CTokenKind.Ident) ? Next().Text : null;
                while (Is(CTokenKind.LBracket))   // array param `[]`
                {
                    Next();
                    if (Is(CTokenKind.Int)) Next();
                    if (Is(CTokenKind.RBracket)) Next();
                }
                ps.Add(new CParam(pType, pName));
                if (Is(CTokenKind.Comma)) { Next(); continue; }
                break;
            }
        }
        Expect(CTokenKind.RParen);
        // `(void)` — an explicit empty parameter list.
        if (ps is [{ Type.Name: "void", Type.PointerDepth: 0 }]) ps.Clear();
        return (ps, variadic);
    }

    /// <summary>Skips to (and past) the next top-level <c>;</c>, descending
    /// through balanced <c>{}</c> / <c>()</c> / <c>[]</c> so a struct body or a
    /// parenthesised group does not end the skip early. Stops at EOF.</summary>
    private void SkipDecl()
    {
        int depth = 0;
        while (!Is(CTokenKind.Eof))
        {
            var k = Next().Kind;
            switch (k)
            {
                case CTokenKind.LBrace or CTokenKind.LParen or CTokenKind.LBracket:
                    depth++;
                    break;
                case CTokenKind.RBrace or CTokenKind.RParen or CTokenKind.RBracket:
                    if (depth > 0) depth--;
                    break;
                case CTokenKind.Semicolon when depth == 0:
                    return;
            }
        }
    }
}
