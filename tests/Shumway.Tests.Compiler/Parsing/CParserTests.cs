using System.Linq;
using Shumway.Compiler.NativeC;
using Xunit;

namespace Shumway.Tests.Compiler.Parsing;

/// <summary>ADR-022 step 2 — the embedded-C subset parser, against real corpus
/// shapes. Statements (a <c>{ … }</c> block) and declarations (a <c>:- c</c>
/// region) parse to the <see cref="CStmt"/> / <see cref="CDecl"/> AST.</summary>
public sealed class CParserTests
{
    // ----- statements -----

    [Fact]
    public void StrcmpBlock_CallsAndBind()
    {
        // strings.pl strcmp_p/3
        var s = CParser.ParseStatements(
            "'MakeCString'( lbuf, LLen, &LS);\n" +
            "'MakeCString'( rbuf, RLen, &RS);\n" +
            "X is 'strcmp'( lbuf, rbuf)");
        Assert.Equal(3, s.Count);

        var c0 = Assert.IsType<CCallStmt>(s[0]);
        var call0 = Assert.IsType<CCallExpr>(c0.Call);
        Assert.Equal("MakeCString", call0.Name);
        Assert.Equal(3, call0.Args.Count);
        Assert.IsType<CIdentExpr>(call0.Args[0]);                 // lbuf
        var addr = Assert.IsType<CAddrOfExpr>(call0.Args[2]);     // &LS
        Assert.Equal("LS", Assert.IsType<CIdentExpr>(addr.Operand).Name);

        var bind = Assert.IsType<CBindStmt>(s[2]);
        Assert.Equal("X", bind.Var);
        var strcmp = Assert.IsType<CCallExpr>(bind.Value);
        Assert.Equal("strcmp", strcmp.Name);
        Assert.Equal(2, strcmp.Args.Count);
    }

    [Fact]
    public void VarDecl_AssignBind_AndUnquotedCall()
    {
        // i_next_literal block 2 + string_length_bytes shape
        var s = CParser.ParseStatements(
            "id: long;\n" +
            "id = 'get_next_literal'(Mod, StartMsgId, pTranslateRef1);\n" +
            "MsgId is id");
        Assert.Equal(3, s.Count);

        var decl = Assert.IsType<CVarDeclStmt>(s[0]);
        Assert.Equal("id", decl.Var);
        Assert.Equal("long", decl.Type.Name);
        Assert.Equal(0, decl.Type.PointerDepth);

        var asg = Assert.IsType<CAssignStmt>(s[1]);
        Assert.Equal("id", Assert.IsType<CIdentExpr>(asg.Target).Name);
        Assert.Equal("get_next_literal", Assert.IsType<CCallExpr>(asg.Value).Name);

        var bind = Assert.IsType<CBindStmt>(s[2]);
        Assert.Equal("MsgId", bind.Var);
        Assert.Equal("id", Assert.IsType<CIdentExpr>(bind.Value).Name);
    }

    [Fact]
    public void UnquotedCall_Void_Deref_AddressOf()
    {
        // arity.pl `Len is strlen(S)` ; getexept `'MakePrologString'('get_exepath'(void), &Path)` ; `*Att = X`
        var s = CParser.ParseStatements(
            "Len is strlen(S);\n" +
            "'MakePrologString'('get_exepath'(void), &Path);\n" +
            "*Att = X");
        Assert.Equal(3, s.Count);

        var bind = Assert.IsType<CBindStmt>(s[0]);
        Assert.Equal("strlen", Assert.IsType<CCallExpr>(bind.Value).Name);   // unquoted fn

        var outer = Assert.IsType<CCallExpr>(Assert.IsType<CCallStmt>(s[1]).Call);
        Assert.Equal("MakePrologString", outer.Name);
        var inner = Assert.IsType<CCallExpr>(outer.Args[0]);
        Assert.Equal("get_exepath", inner.Name);
        Assert.Empty(inner.Args);                                            // (void) → no args

        var asg = Assert.IsType<CAssignStmt>(s[2]);
        var deref = Assert.IsType<CDerefExpr>(asg.Target);
        Assert.Equal("Att", Assert.IsType<CIdentExpr>(deref.Operand).Name);
    }

    [Fact]
    public void CommaSeparators_Equivalent()
    {
        // Arity uses `,` and `;` interchangeably.
        var s = CParser.ParseStatements("Len: int, Len is strlen(S)");
        Assert.Equal(2, s.Count);
        Assert.IsType<CVarDeclStmt>(s[0]);
        Assert.IsType<CBindStmt>(s[1]);
    }

    // ----- declarations -----

    [Fact]
    public void CRegion_GlobalsAndPrototype()
    {
        // strings.pl :- c region
        var d = CParser.ParseDeclarations(
            "char lbuf[255];\nchar rbuf[255];\nint strcmp(const char*, const char*);\n");
        Assert.Equal(3, d.Count);

        var g0 = Assert.IsType<CGlobalVar>(d[0]);
        Assert.Equal("lbuf", g0.Name);
        Assert.Equal("char", g0.Type.Name);
        Assert.Equal(255, g0.ArrayLength);

        var proto = Assert.IsType<CPrototype>(d[2]);
        Assert.Equal("strcmp", proto.Name);
        Assert.Equal("int", proto.ReturnType.Name);
        Assert.Equal(2, proto.Params.Count);
        Assert.Equal("char", proto.Params[0].Type.Name);
        Assert.Equal(1, proto.Params[0].Type.PointerDepth);   // const char* → char*
    }

    [Fact]
    public void CRegion_Typedef_And_VoidAndMultiwordTypes()
    {
        // arity.pl :- c region
        var d = CParser.ParseDeclarations(
            "void debug_enable_tracing(int kind);\n" +
            "unsigned long strlen(const char* s);\n" +
            "typedef char *pchar;\n");
        Assert.Equal(3, d.Count);

        var p0 = Assert.IsType<CPrototype>(d[0]);
        Assert.Equal("void", p0.ReturnType.Name);
        Assert.Single(p0.Params);
        Assert.Equal("int", p0.Params[0].Type.Name);

        var p1 = Assert.IsType<CPrototype>(d[1]);
        Assert.Equal("strlen", p1.Name);
        Assert.Equal("unsigned long", p1.ReturnType.Name);

        var td = Assert.IsType<CTypedef>(d[2]);
        Assert.Equal("pchar", td.Alias);
        Assert.Equal("char", td.Underlying.Name);
        Assert.Equal(1, td.Underlying.PointerDepth);
    }

    [Fact]
    public void CRegion_SkipsNoise_KeepsRealDecls()
    {
        // Preprocessed-header noise: #line, comments, pragmas, a struct typedef,
        // a function-pointer typedef — all skipped; the real decls survive.
        var d = CParser.ParseDeclarations(
            "#line 1 \"foo.h\"\n" +
            "#pragma warning(push)\n" +
            "/* a comment */\n" +
            "typedef struct t_reftype { int n; struct t_reftype** p; } *reftype, t_reftype;\n" +
            "typedef void(__stdcall* CallbackT)(int size);\n" +
            "typedef int *pint;\n" +
            "int real_fn(int a);\n");
        // Only the simple typedef + the prototype survive.
        Assert.Contains(d, x => x is CTypedef { Alias: "pint" });
        Assert.Contains(d, x => x is CPrototype { Name: "real_fn" });
        Assert.DoesNotContain(d, x => x is CTypedef { Alias: "reftype" });
        Assert.DoesNotContain(d, x => x is CTypedef { Alias: "CallbackT" });
    }

    [Fact]
    public void DeferredForms_ThrowCleanly()
    {
        // The term/reftype tier (struct member access `->`, the Arity `..`
        // operator) and C casts are NOT in the int/float/string subset. They must
        // fail as a clean CParseException (so step 4 reports a compile error),
        // never crash. Validated against the corpus: 100% of `:- c` regions and
        // ~97% of `{...}` blocks parse; the rest are exactly these forms.
        Assert.Throws<CParseException>(() => CParser.ParseStatements("Type is ((*RefType)->ntype)"));
        Assert.Throws<CParseException>(() => CParser.ParseStatements("X is (char*)Y"));
    }

    [Fact]
    public void CRegion_ExternGlobal_StorageClassDropped()
    {
        // zebra.ari `extern char achZtime[10];`
        var d = CParser.ParseDeclarations("extern char achZtime[10];\nstatic int n;\n");
        var g0 = Assert.IsType<CGlobalVar>(d[0]);
        Assert.Equal("achZtime", g0.Name);
        Assert.Equal("char", g0.Type.Name);
        Assert.Equal(10, g0.ArrayLength);
        Assert.Contains(d, x => x is CGlobalVar { Name: "n", Type.Name: "int" });
    }
}
