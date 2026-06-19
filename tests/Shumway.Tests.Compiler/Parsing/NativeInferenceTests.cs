using System.Collections.Generic;
using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Compiler.NativeC;
using Shumway.Compiler.Parsing;
using Xunit;

namespace Shumway.Tests.Compiler.Parsing;

/// <summary>ADR-022 step 3 — type+mode inference for a native block's Prolog
/// variables, driven by the surrounding Prolog guards plus the block-local
/// declarations, the <c>is</c> right-hand-side type, and the string intrinsics.
/// Drives the real flow: parse the clause (step 1 captures the block), parse the
/// block's C text (step 2), then infer.</summary>
public sealed class NativeInferenceTests
{
    private static IEnumerable<CompoundTerm> Find(Term t, string f, int a)
    {
        if (t is CompoundTerm c)
        {
            if (c.Functor == f && c.Args.Length == a) yield return c;
            foreach (var x in c.Args) foreach (var m in Find(x, f, a)) yield return m;
        }
    }

    private static NativeBlockInfo Infer(string clauseSrc, string cDecls = "")
    {
        var rule = new ClauseReader(new Shumway.Compiler.Lexer.Lexer(clauseSrc),
                OperatorTable.Default(), new PrologFlags { ArityCompat = true })
            .ReadAll().First(c => c.Kind is ClauseKind.Rule or ClauseKind.Fact);
        var ng = Find(rule.Term, "$native_goal", 1).First();
        var block = CParser.ParseStatements(((StringTerm)ng.Args[0]).Content);
        var decls = cDecls.Length == 0 ? new List<CDecl>() : CParser.ParseDeclarations(cDecls);
        return NativeInference.Analyze(rule.Term, block, decls);
    }

    private static void AssertVar(NativeBlockInfo i, string name, NativeKind k, NativeMode m)
        => Assert.Equal(new NativeVar(name, k, m), i.PrologVars.Single(v => v.Name == name));

    [Fact]
    public void StringInput_IntOutput_FromGuardAndLocalDecl()
    {
        // string_length_bytes: atom(S) → S string input; Len: int decl + bound in
        // the block → int output.
        var i = Infer(
            "string_length_bytes(S, L):- atom(S), { Len: int, Len is strlen(S) }, integer(Len), !, L=Len.\n");
        Assert.Empty(i.Diagnostics);
        AssertVar(i, "S", NativeKind.String, NativeMode.Input);
        AssertVar(i, "Len", NativeKind.Int, NativeMode.Output);
        Assert.Equal(2, i.PrologVars.Count);   // L is not used inside the block
    }

    [Fact]
    public void Output_LongFromIsRhsLocal_VarGuard_GlobalAndLocalClassified()
    {
        // var(MsgId) → output; `MsgId is id` with `id: long` local → long.
        var i = Infer(
            "t(Mod, MsgId) :- integer(Mod), var(MsgId), "
            + "{ id: long; id = 'get_next_literal'(Mod, pTranslateRef1); MsgId is id }.\n",
            "reftype pTranslateRef1;\nint get_next_literal(int, reftype);\n");
        Assert.Empty(i.Diagnostics);
        AssertVar(i, "Mod", NativeKind.Int, NativeMode.Input);
        AssertVar(i, "MsgId", NativeKind.Long, NativeMode.Output);
        Assert.Contains("id", i.Locals);                 // block-local C temporary
        Assert.Contains("pTranslateRef1", i.Globals);    // :- c global
        Assert.DoesNotContain(i.PrologVars, v => v.Name == "id");
    }

    [Fact]
    public void StringIntrinsics_And_PrototypeReturn_LengthDiscarded()
    {
        // strcmp_p: &LS/&RS via MakeCString → string inputs; X is strcmp(...) →
        // int output (from the :- c prototype). The length args LLen/RLen — Prolog
        // vars here — are consumed by the intrinsic and must NOT need a type.
        var i = Infer(
            "strcmp_p(LS, RS, X):- LLen = 255, RLen = 255, "
            + "{ 'MakeCString'(lbuf, LLen, &LS); 'MakeCString'(rbuf, RLen, &RS); X is 'strcmp'(lbuf, rbuf) }, !.\n",
            "char lbuf[255];\nchar rbuf[255];\nint strcmp(const char*, const char*);\n");
        Assert.Empty(i.Diagnostics);
        AssertVar(i, "LS", NativeKind.String, NativeMode.Input);
        AssertVar(i, "RS", NativeKind.String, NativeMode.Input);
        AssertVar(i, "X", NativeKind.Int, NativeMode.Output);
        Assert.Equal(3, i.PrologVars.Count);             // LLen / RLen discarded
        Assert.Contains("lbuf", i.Globals);
    }

    [Fact]
    public void MakePrologString_OutputString()
    {
        var i = Infer(
            "p(Path) :- { 'MakePrologString'('get_exepath'(void), &Path) }.\n",
            "const char* get_exepath(void);\n");
        Assert.Empty(i.Diagnostics);
        AssertVar(i, "Path", NativeKind.String, NativeMode.Output);
    }

    [Fact]
    public void Uninferrable_Variable_IsADiagnostic()
    {
        // A is passed to a non-intrinsic function with no guard / decl / prototype:
        // its type cannot be inferred — a diagnostic, not a binding.
        var i = Infer("f(A) :- { ret = foo(A) }.\n");
        Assert.NotEmpty(i.Diagnostics);
        Assert.Contains(i.Diagnostics, d => d.Contains("'A'"));
        Assert.DoesNotContain(i.PrologVars, v => v.Name == "A");
    }
}
