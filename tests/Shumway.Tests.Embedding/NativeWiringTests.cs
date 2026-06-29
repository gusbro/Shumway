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
    public void NativeBlockInDynamicPredicate_Runs()
    {
        // The native transform runs BEFORE the dynamic-clause routing, so a
        // `:- dynamic` predicate whose source clause uses a native block has the
        // block rewritten too; the rewritten clause (carrying $native_run) goes to
        // the runtime store and runs the block exactly as a static clause would.
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(Interop));
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            ":- dynamic cmp/3.\n" +
            ":- c.\nint strcmp(const char*, const char*);\n:- prolog.\n" +
            "cmp(A, B, R) :- atom(A), atom(B), { R is 'strcmp'(A, B) }, integer(R).\n");

        Assert.True(e.Query("cmp(abc, abc, R), R == 0.").Success);
        Assert.True(e.Query("cmp(abd, abc, R), R == 1.").Success);

        // Genuinely dynamic: retract the seeded clause and the predicate stops
        // matching — proving the native-bearing clause lives in the runtime store.
        Assert.True(e.Query("retract((cmp(_,_,_) :- _)).").Success);
        Assert.False(e.Query("cmp(abc, abc, _).").Success);
    }

    [Fact]
    public void PlainScalarGlobal_PersistsAcrossCalls_ArityStaticStorage()
    {
        // ADR-022 — a scalar `:- c` global has Arity static-storage semantics: it
        // is per-engine persistent. A block seeds it from storage on entry and
        // writes it through on every assignment, so increments accumulate across
        // calls and are visible to other blocks. (embedded-native-c.md §2.)
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(Interop));
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            ":- c.\nint counter;\n:- prolog.\n" +
            "incr(X) :- { counter = counter + 1; X is counter }, integer(X).\n" +
            "readc(X) :- { X is counter }, integer(X).\n");

        Assert.Equal(0L, e.Query("readc(X).").Get<long>("X"));   // uninitialised → 0
        Assert.Equal(1L, e.Query("incr(X).").Get<long>("X"));
        Assert.Equal(2L, e.Query("incr(X).").Get<long>("X"));    // persists across calls
        Assert.Equal(3L, e.Query("incr(X).").Get<long>("X"));
        Assert.Equal(3L, e.Query("readc(X).").Get<long>("X"));   // visible to another block
    }

    [Fact]
    public void UndeclaredScalarGlobal_IsAConsultError()
    {
        // A name used as a scalar that is neither a Prolog var, a block-local, nor a
        // declared `:- c` global is a typo / missing declaration — a hard consult
        // error, never a silently zero-initialised local.
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(Interop));
        var ex = Assert.ThrowsAny<InvalidOperationException>(() => e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            "bump(X) :- { counter = counter + 1; X is counter }, integer(X).\n"));
        Assert.Contains("undeclared native global", ex.Message);
        Assert.Contains("counter", ex.Message);
    }

    [Fact]
    public void ExternScalarGlobal_IsDeclared_AndPersists()
    {
        // `extern` counts as declared (CParser folds it into a normal global), so it
        // is not the undeclared-error case; it uses the shared per-engine storage.
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(Interop));
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            ":- c.\nextern int counter;\n:- prolog.\n" +
            "bump(X) :- { counter = counter + 1; X is counter }, integer(X).\n");
        Assert.Equal(1L, e.Query("bump(X).").Get<long>("X"));
        Assert.Equal(2L, e.Query("bump(X).").Get<long>("X"));
    }

    [Fact]
    public void ExternScalarGlobal_SharesStorageAcrossModules()
    {
        // Module a DEFINES counter; module b references it via `extern`. Native
        // global storage is keyed by the bare C name engine-wide, so b sees a's
        // writes — the cross-module C-linkage model.
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(Interop));
        e.ConsultString(
            ":- module(a).\n:- set_prolog_flag(arity_compat, true).\n:- public bump_a/0.\n" +
            ":- c.\nint counter;\n:- prolog.\n" +
            "bump_a :- { counter = counter + 1 }.\n");
        e.ConsultString(
            ":- module(b).\n:- set_prolog_flag(arity_compat, true).\n:- public read_b/1.\n" +
            ":- c.\nextern int counter;\n:- prolog.\n" +
            "read_b(X) :- { X is counter }, integer(X).\n");

        Assert.True(e.Query("bump_a.").Success);
        Assert.True(e.Query("bump_a.").Success);
        Assert.Equal(2L, e.Query("read_b(X).").Get<long>("X"));   // b sees a's writes
    }

    [Fact]
    public void FloatScalarGlobal_PersistsAcrossCalls()
    {
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(Interop));
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            ":- c.\ndouble acc;\n:- prolog.\n" +
            "add(D, X) :- float(D), { acc = acc + D; X is acc }, float(X).\n");

        Assert.Equal(1.5, e.Query("add(1.5, X).").Get<double>("X"));
        Assert.Equal(4.0, e.Query("add(2.5, X).").Get<double>("X"));   // 1.5 + 2.5 persisted
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
        var ex = Assert.ThrowsAny<InvalidOperationException>(() => e.ConsultString(
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
        var ex = Assert.ThrowsAny<InvalidOperationException>(() => e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            ":- c.\nint nope(int);\n:- prolog.\n" +
            "f(A, R) :- integer(A), { R is 'nope'(A) }, integer(R).\n"));
        Assert.Contains("nope", ex.Message);
    }
}
