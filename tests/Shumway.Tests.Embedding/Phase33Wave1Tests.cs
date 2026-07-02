using System;
using System.Text;
using Shumway.Compiler.Ast;
using Shumway.Core;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 wave 1 — correctness-critical fixes from the 2026-06-30 audit
/// (docs/phase-33-backlog.md, E-series). One test region per item.
/// </summary>
public class Phase33Wave1Tests
{
    private readonly ITestOutputHelper _output;
    public Phase33Wave1Tests(ITestOutputHelper output) => _output = output;

    // ---- E2: HGlobal-path free must not free pointers the native side swapped in.
    //      A native fn that replaces cstr with its own malloc'd buffer used to make
    //      the graph-walking Free release a foreign pointer (heap corruption); the
    //      recorded-free path releases exactly Shumway's own allocations. ----

    private const string SwapCSource = """
        #include <stdlib.h>
        #include <string.h>
        #ifdef _MSC_VER
        #define EXPORT __declspec(dllexport)
        #else
        #define EXPORT __attribute__((visibility("default")))
        #endif
        typedef struct t_reftype {
            long long ntype; long long nelem; void* pars;
            union { char* cstr; int cint; double cflt; } crep;
        } t_reftype;
        /* Replaces the atom's cstr with the library's OWN malloc'd buffer —
           the exact shape that corrupted the heap under graph-walking Free. */
        EXPORT int swap_text(t_reftype* r) {
            char* mine = (char*)malloc(8);
            strcpy(mine, "swapped");
            r->crep.cstr = mine;   /* Shumway's original buffer is now unlinked */
            r->nelem = 7;
            return 1;
        }
        """;

    private const string SwapProgram =
        ":- set_prolog_flag(arity_compat, true).\n" +
        ":- native swap_text/1.\n" +
        ":- c.\nreftype par1ref;\nint swap_text(reftype);\n:- prolog.\n" +
        "go(In, Out) :-\n" +
        "  { Ptr: preftype; Ptr is &par1ref },\n" +
        "  fill_par(In, Ptr),\n" +
        "  { ret: int; ret = 'swap_text'(par1ref); Ret is ret },\n" +
        "  Ret =:= 1,\n" +
        "  reftype_term(Out, Ptr).\n";

    [Fact]
    public void E2_NativeSwappedCstr_DoesNotFreeForeignPointer()
    {
        string? dll = NativeTestDll.TryBuild(SwapCSource, "swaptext", out string note);
        if (dll is null)
        {
            _output.WriteLine("SKIPPED E2 swap test: " + note);
            return;
        }
        var e = new Shumway.Embedding.PrologEngine();
        e.UseNativeLibrary(dll);
        e.ConsultString(SwapProgram);
        // The native fn swaps in its own malloc'd string. Dematerialize reads it
        // (borrowed), and the recorded-free must release only Shumway's original
        // allocations — never the foreign pointer. Repeat to shake out corruption.
        for (int i = 0; i < 50; i++)
            Assert.True(e.Query("go(hello, Out), Out == swapped.").Success);
    }

    // ---- E4: a Prolog integer outside int32 cannot round-trip through Arity's
    //      32-bit cint — must raise a catchable representation_error, not truncate. ----

    [Fact]
    public void E4_Int64Materialize_RaisesRepresentationError()
    {
        long tooBig = (long)int.MaxValue + 1;
        var ex = Assert.Throws<PrologRuntimeException>(
            () => Shumway.Embedding.NativeReftype.Materialize(new IntTerm(tooBig)));
        Assert.Contains("representation_error", ex.Message);

        var exNeg = Assert.Throws<PrologRuntimeException>(
            () => Shumway.Embedding.NativeReftype.Materialize(new IntTerm((long)int.MinValue - 1)));
        Assert.Contains("representation_error", exNeg.Message);

        // Boundary values still materialize fine.
        IntPtr ok = Shumway.Embedding.NativeReftype.Materialize(new IntTerm(int.MaxValue));
        Assert.Equal(new IntTerm(int.MaxValue), Shumway.Embedding.NativeReftype.Dematerialize(ok));
        Shumway.Embedding.NativeReftype.Free(ok);
    }

    // ---- E6: NativeTextEncoding must be byte-oriented — UTF-16/32 silently
    //      corrupt NUL-terminated char* marshalling and are rejected. ----

    [Fact]
    public void E6_NativeTextEncoding_RejectsNonByteOriented()
    {
        var e = new Shumway.Embedding.PrologEngine();
        Assert.Throws<ArgumentException>(() => e.NativeTextEncoding = Encoding.Unicode);
        Assert.Throws<ArgumentException>(() => e.NativeTextEncoding = Encoding.UTF32);
        Assert.Throws<ArgumentNullException>(() => e.NativeTextEncoding = null!);
        // Byte-oriented encodings are accepted.
        e.NativeTextEncoding = Encoding.UTF8;
        e.NativeTextEncoding = Encoding.Latin1;
        e.NativeTextEncoding = Encoding.ASCII;
        Assert.Equal(Encoding.ASCII, e.NativeTextEncoding);
    }

    // ---- E3: string_term/2 must parse with the engine's LIVE operator table so
    //      user :- op/3 operators round-trip (it rendered with the live table but
    //      parsed with the default one). ----

    [Fact]
    public void E3_StringTerm_HonorsUserOperatorsOnParse()
    {
        var e = new Shumway.Embedding.PrologEngine();
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            ":- op(700, xfx, ===).\n" +
            "p.\n");
        // Parse direction: the custom operator must be readable.
        Assert.True(e.Query("string_term('a === b', T), T == ===(a, b).").Success);
        // Round-trip: term -> atom -> term is the identity under the live table.
        Assert.True(e.Query("string_term(A, ===(x, y)), string_term(A, T2), T2 == ===(x, y).").Success);
    }

    // ---- E8: malformed text in string_term/2 is a catchable Prolog syntax error,
    //      not an escaping .NET ParseException. ----

    [Fact]
    public void E8_StringTerm_MalformedText_IsCatchableSyntaxError()
    {
        var e = new Shumway.Embedding.PrologEngine();
        e.ConsultString(":- set_prolog_flag(arity_compat, true).\np.\n");
        Assert.True(e.Query(
            "catch(string_term('foo(', _), error(syntax_error(_), _), R = caught), R == caught.").Success);
    }

    // ---- E7: a recorded-DB key with an INNER unbound variable used to store under
    //      a never-matchable key (silent lookup failure); now instantiation_error. ----

    [Fact]
    public void E7_RecordedKey_InnerVariable_RaisesInstantiationError()
    {
        var e = new Shumway.Embedding.PrologEngine();
        e.ConsultString(":- set_prolog_flag(arity_compat, true).\np.\n");
        // foo(X) with X unbound: deep-ground check fires.
        Assert.True(e.Query(
            "catch(recorda(foo(_), v, _), error(instantiation_error, _), R = caught), R == caught.").Success);
        // Ground compound keys still work end-to-end.
        Assert.True(e.Query("recorda(foo(bar), v1, _), recorded(foo(bar), V, _), V == v1.").Success);
    }

    // ---- E9: EnginePool reuse policy — FreshEngine isolates rentals; the default
    //      ReuseState documents that state carries over. ----

    [Fact]
    public void E9_EnginePool_FreshEngine_IsolatesRentals()
    {
        using var pool = Shumway.Embedding.EnginePool.FromSource(
            ":- dynamic(fact/1).\nbase(1).\n", maxSize: 1,
            Shumway.Embedding.PoolReusePolicy.FreshEngine);
        using (var lease = pool.Rent())
        {
            Assert.True(lease.Engine.Query("assertz(fact(9)), fact(9).").Success);
        }
        using (var lease = pool.Rent())
        {
            // A fresh engine: the previous rental's assert is NOT visible.
            Assert.False(lease.Engine.Query("fact(9).").Success);
            Assert.True(lease.Engine.Query("base(1).").Success);
        }
    }

    [Fact]
    public void E9_EnginePool_ReuseState_KeepsStateAcrossRentals()
    {
        using var pool = Shumway.Embedding.EnginePool.FromSource(
            ":- dynamic(fact/1).\nbase(1).\n", maxSize: 1);   // default ReuseState
        using (var lease = pool.Rent())
            Assert.True(lease.Engine.Query("assertz(fact(9)).").Success);
        using (var lease = pool.Rent())
            Assert.True(lease.Engine.Query("fact(9).").Success);   // documented carry-over
    }

    // ---- E10: a '$native_goal' that survives to execution (native transform never
    //      ran / runtime-constructed) is a loud error, not silent success. ----

    [Fact]
    public void E10_UntransformedNativeGoal_IsLoudError()
    {
        var e = new Shumway.Embedding.PrologEngine();
        e.ConsultString("p.\n");
        // Old behavior: '$native_goal'(x) silently succeeded -> R stays unbound and
        // the == fails. New behavior: it throws, catch binds R = caught.
        Assert.True(e.Query(
            "catch('$native_goal'(abc), _, R = caught), R == caught.").Success);
    }
}
