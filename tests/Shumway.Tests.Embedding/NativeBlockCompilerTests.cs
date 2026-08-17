using System;
using System.Reflection;
using Shumway.Compiler.NativeC;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

// ADR-022 item 2 — the shared C-subset → IL codegen (NativeBlockCompiler). These
// assert the compiler actually compiles the supported tier (non-null, i.e. it is
// not silently always falling back to the interpreter), bails on the deferred /
// unsupported constructs, and that a program driven end-to-end through the
// compiled `$native_run` path produces correct results.
public sealed class NativeBlockCompilerTests
{
    private static class Interop
    {
        public static int strcmp(string a, string b) => Math.Sign(string.CompareOrdinal(a, b));
        public static long sum(long a, long b) => a + b;
    }

    private static readonly Func<string, MethodInfo?> Resolver = name =>
        typeof(Interop).GetMethod(name, BindingFlags.Public | BindingFlags.Static);

    [Fact]
    public void Compiles_StrcmpBlock_NonNull()
    {
        var vars = new[]
        {
            new NativeVar("A", NativeKind.String, NativeMode.Input),
            new NativeVar("B", NativeKind.String, NativeMode.Input),
            new NativeVar("R", NativeKind.Long, NativeMode.Output),
        };
        var stmts = new CStmt[]
        {
            new CBindStmt("R", new CCallExpr("strcmp",
                new CExpr[] { new CIdentExpr("A"), new CIdentExpr("B") })),
        };
        Assert.NotNull(NativeBlockCompiler.TryCompile(vars, stmts, System.Array.Empty<Shumway.Compiler.NativeC.NativeScalarGlobal>(), 0, Resolver));
    }

    [Fact]
    public void Compiles_ArithmeticWithLocal_NonNull()
    {
        var vars = new[]
        {
            new NativeVar("A", NativeKind.Long, NativeMode.Input),
            new NativeVar("B", NativeKind.Long, NativeMode.Input),
            new NativeVar("R", NativeKind.Long, NativeMode.Output),
        };
        var stmts = new CStmt[]
        {
            new CVarDeclStmt("T", new CType("long")),
            new CBindStmt("T", new CCallExpr("sum",
                new CExpr[] { new CIdentExpr("A"), new CIdentExpr("B") })),
            new CBindStmt("R", new CBinaryExpr('*', new CIdentExpr("T"), new CIntExpr(2))),
        };
        Assert.NotNull(NativeBlockCompiler.TryCompile(vars, stmts, System.Array.Empty<Shumway.Compiler.NativeC.NativeScalarGlobal>(), 0, Resolver));
    }

    [Fact]
    public void Bails_OnUnsupportedConstruct_ReturnsNull()
    {
        var vars = new[] { new NativeVar("R", NativeKind.Long, NativeMode.Output) };
        // A pointer deref belongs to the deferred reftype tier — not compilable.
        var stmts = new CStmt[]
        {
            new CBindStmt("R", new CDerefExpr(new CIdentExpr("R"))),
        };
        Assert.Null(NativeBlockCompiler.TryCompile(vars, stmts, System.Array.Empty<Shumway.Compiler.NativeC.NativeScalarGlobal>(), 0, Resolver));
    }

    [Fact]
    public void Bails_OnUnresolvedInterop_ReturnsNull()
    {
        var vars = new[]
        {
            new NativeVar("A", NativeKind.Long, NativeMode.Input),
            new NativeVar("R", NativeKind.Long, NativeMode.Output),
        };
        var stmts = new CStmt[]
        {
            new CBindStmt("R", new CCallExpr("does_not_exist",
                new CExpr[] { new CIdentExpr("A") })),
        };
        Assert.Null(NativeBlockCompiler.TryCompile(vars, stmts, System.Array.Empty<Shumway.Compiler.NativeC.NativeScalarGlobal>(), 0, Resolver));
    }

    [Fact]
    public void CompiledPath_EndToEnd_CorrectResults()
    {
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(Interop));
        e.ConsultString("""
            :- set_prolog_flag(arity_compat, true).
            :- c.
            int strcmp(const char*, const char*);
            long sum(int, int);
            :- prolog.
            cmp(A, B, R) :- atom(A), atom(B), { R is 'strcmp'(A, B) }, integer(R).
            calc(A, B, R) :- integer(A), integer(B), { T: long; T is 'sum'(A, B); R is T * 2 }, integer(R).
            """);
        Assert.True(e.Query("cmp(abc, abd, R), R == -1.").Success);
        Assert.True(e.Query("cmp(abc, abc, R), R == 0.").Success);
        Assert.Equal(14L, e.Query("calc(3, 4, R).").Get<long>("R"));
    }
}
