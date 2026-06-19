using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Compiler.NativeC;
using Shumway.Compiler.Parsing;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-022 step 4 — a native <c>{...}</c> block, emitted as a synthesized
/// foreign by <see cref="NativeBlockRunner"/>, runs END-TO-END: it marshals string
/// / integer inputs from the goal's registers, calls a C# static method (here a
/// test stand-in for <c>Shumway.Native.Interop</c>; the linker supplies the real
/// one in step 5), handles the MakeCString / MakePrologString intrinsics, and
/// unifies the outputs. Invoked through a real query.</summary>
public sealed class NativeBlockRunnerTests
{
    // Stand-in for the user's Shumway.Native.Interop class.
    private static class Interop
    {
        public static int strcmp(string a, string b) => Math.Sign(string.CompareOrdinal(a, b));
        public static long add(long a, long b) => a + b;
        public static string banner() => "blint";
    }

    private static IEnumerable<CompoundTerm> Find(Term t, string f, int a)
    {
        if (t is CompoundTerm c)
        {
            if (c.Functor == f && c.Args.Length == a) yield return c;
            foreach (var x in c.Args) foreach (var m in Find(x, f, a)) yield return m;
        }
    }

    // Parse the clause (step 1 capture), parse the block (step 2), infer (step 3),
    // build the runner (step 4), register it under a unique name, and return that
    // name + the variable arity so the test can query it.
    private static (string Name, int Arity) Emit(string clauseSrc, string cDecls, string label)
    {
        var rule = new ClauseReader(new Shumway.Compiler.Lexer.Lexer(clauseSrc),
                OperatorTable.Default(), new PrologFlags { ArityCompat = true })
            .ReadAll().First(c => c.Kind is ClauseKind.Rule or ClauseKind.Fact);
        var ng = Find(rule.Term, "$native_goal", 1).First();
        var stmts = CParser.ParseStatements(((StringTerm)ng.Args[0]).Content);
        var decls = cDecls.Length == 0 ? new List<CDecl>() : CParser.ParseDeclarations(cDecls);
        var info = NativeInference.Analyze(rule.Term, stmts, decls);
        Assert.Empty(info.Diagnostics);

        string name = "nbtest_" + label;
        BuiltinsRegistry.Register(name, info.PrologVars.Count,
            NativeBlockRunner.Build(info.PrologVars, stmts));
        return (name, info.PrologVars.Count);
    }

    [Fact]
    public void StringIntrinsics_CallInterop_UnifyInt()
    {
        // strcmp_p: &LS/&RS marshalled (string inputs), X is strcmp(...) → int out.
        // PrologVars order is alphabetical: [LS, RS, X].
        var (n, _) = Emit(
            "strcmp_p(LS, RS, X):- LLen=255, RLen=255, "
            + "{ 'MakeCString'(lbuf, LLen, &LS); 'MakeCString'(rbuf, RLen, &RS); X is 'strcmp'(lbuf, rbuf) }, !.\n",
            "char lbuf[255];\nchar rbuf[255];\nint strcmp(const char*, const char*);\n", "strcmp");
        var e = new PrologEngine(); e.UseNativeInterop(typeof(Interop));
        Assert.True(e.Query($"{n}(abc, abc, X), X == 0.").Success);
        Assert.True(e.Query($"{n}(abc, abd, X), X == -1.").Success);
        Assert.True(e.Query($"{n}(abd, abc, X), X == 1.").Success);
    }

    [Fact]
    public void IntegerInputs_CallInterop_UnifyLong()
    {
        // [A, B, Z]: Z is add(A, B) → long out, add's return from the :- c prototype.
        var (n, _) = Emit(
            "f(A, B, Z):- integer(A), integer(B), { Z is 'add'(A, B) }.\n",
            "long add(int, int);\n", "add");
        var e = new PrologEngine(); e.UseNativeInterop(typeof(Interop));
        Assert.True(e.Query($"{n}(2, 3, Z), Z == 5.").Success);
        Assert.True(e.Query($"{n}(40, 2, Z), Z == 42.").Success);
    }

    [Fact]
    public void Arithmetic_InBind_RunsEndToEnd()
    {
        // Z is A * 2 + 1 — simple native arithmetic, no Interop call. integer(Z)
        // after the block types the output.
        var (n, _) = Emit(
            "f(A, Z):- integer(A), { Z is A * 2 + 1 }, integer(Z).\n", "", "arith");
        var e = new PrologEngine(); e.UseNativeInterop(typeof(Interop));
        Assert.True(e.Query($"{n}(10, Z), Z == 21.").Success);
        Assert.True(e.Query($"{n}(0, Z), Z == 1.").Success);
    }

    [Fact]
    public void MakePrologString_UnifiesOutputString()
    {
        var (n, _) = Emit(
            "g(Out):- { 'MakePrologString'('banner'(void), &Out) }.\n",
            "const char* banner(void);\n", "banner");
        var e = new PrologEngine(); e.UseNativeInterop(typeof(Interop));
        Assert.True(e.Query($"{n}(Out), Out == blint.").Success);
    }
}
