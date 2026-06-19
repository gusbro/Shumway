using System;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-022 — the consult-time wiring: a real Arity source with a
/// <c>:- c</c> region and an embedded <c>{...}</c> block, CONSULTED into an engine,
/// runs the native block (the transform rewrites <c>$native_goal</c> → a synthesized
/// foreign at consult). The interop class is supplied via
/// <see cref="PrologEngine.UseNativeInterop"/>.</summary>
public sealed class NativeWiringTests
{
    private static class Interop
    {
        public static int strcmp(string a, string b) => Math.Sign(string.CompareOrdinal(a, b));
        public static long sum(long a, long b) => a + b;
    }

    [Fact]
    public void ConsultedNativeBlock_CallsInterop()
    {
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(Interop));
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            ":- c.\nint strcmp(const char*, const char*);\n:- prolog.\n" +
            "cmp(A, B, R) :- atom(A), atom(B), { R is 'strcmp'(A, B) }, integer(R).\n");

        Assert.True(e.Query("cmp(abc, abc, R), R == 0.").Success);
        Assert.True(e.Query("cmp(abc, abd, R), R == -1.").Success);
        Assert.True(e.Query("cmp(abd, abc, R), R == 1.").Success);
    }

    [Fact]
    public void ConsultedNativeBlock_IntegerInputsAndArithmetic()
    {
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(Interop));
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            ":- c.\nlong sum(int, int);\n:- prolog.\n" +
            "calc(A, B, R) :- integer(A), integer(B), { T: long; T is 'sum'(A, B); R is T * 2 }, integer(R).\n");

        Assert.True(e.Query("calc(3, 4, R), R == 14.").Success);   // (3+4)*2
    }

    [Fact]
    public void UnsupportedBlock_IsAConsultError()
    {
        // A reftype block (the deferred tier) cannot be compiled — consulting must
        // FAIL, never silently no-op it (a no-op'd block would misbehave unnoticed).
        var e = new PrologEngine();
        var ex = Assert.Throws<InvalidOperationException>(() => e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            "p(ok) :- { X is ((*RefType)->ntype) }.\n"));
        Assert.Contains("native block", ex.Message);
    }

    [Fact]
    public void MissingInteropFunction_IsAConsultError()
    {
        // The interop class provides strcmp/sum but not 'nope' — consulting a block
        // that calls it must fail loudly, naming the missing function.
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(Interop));
        var ex = Assert.Throws<InvalidOperationException>(() => e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            ":- c.\nint nope(int);\n:- prolog.\n" +
            "f(A, R) :- integer(A), { R is 'nope'(A) }, integer(R).\n"));
        Assert.Contains("nope", ex.Message);
    }
}
