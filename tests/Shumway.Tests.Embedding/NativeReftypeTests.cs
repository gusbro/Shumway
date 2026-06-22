using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

// ADR-024 — the generic-term interop (reftype tier), primary layer. A reftype is a
// zero-copy cursor (TermSlot wrapped as a Foreign cell) over a real Prolog term.
public sealed class NativeReftypeTests
{
    private static PrologEngine Eng()
    {
        var e = new PrologEngine();
        return e;
    }

    // ---- Prolog round-trip: fill_par (term → slot) then reftype_term (slot → term).

    private static bool RoundTrips(string termText)
    {
        var e = Eng();
        return e.Query(
            $"'$new_reftype_slot'(R), fill_par({termText}, R), reftype_term(T, R), T == {termText}."
        ).Success;
    }

    [Theory]
    [InlineData("42")]
    [InlineData("-7")]
    [InlineData("3.5")]
    [InlineData("hello")]
    [InlineData("[]")]
    [InlineData("foo(1, bar, 2.5)")]
    [InlineData("a(b(c), d(1, 2))")]
    [InlineData("[1, 2, 3]")]
    [InlineData("[a, [b, c], 4]")]
    public void RoundTrip_PreservesTerm(string term) => Assert.True(RoundTrips(term));

    [Fact]
    public void RoundTrip_Variable_StaysUnbound()
    {
        var e = Eng();
        Assert.True(e.Query(
            "'$new_reftype_slot'(R), fill_par(X, R), reftype_term(T, R), var(T).").Success);
    }

    [Fact]
    public void Preftype_SucceedsOnSlot_FailsOnNonSlot()
    {
        var e = Eng();
        Assert.True(e.Query("'$new_reftype_slot'(R), preftype(R).").Success);
        Assert.False(e.Query("preftype(foo).").Success);
    }

    // ---- The C# accessor API (ReftypeApi / TermSlot) — what interop code uses.

    [Fact]
    public void Api_BuildsCompound()
    {
        var s = new TermSlot();
        ReftypeApi.putfunctor_c("point", 2, s);
        Assert.True(ReftypeApi.getfuncarg_c(s, 1, out var a1));
        ReftypeApi.putint_c(7, a1);
        Assert.True(ReftypeApi.getfuncarg_c(s, 2, out var a2));
        ReftypeApi.putatm_c("origin", a2);

        Assert.Equal(TermSlot.Functor, ReftypeApi.findtype_c(s));
        Assert.True(ReftypeApi.getfunctor_c(s, out var name, out var arity));
        Assert.Equal("point", name);
        Assert.Equal(2, arity);
        Assert.Equal("point(7, origin)", s.Materialize().ToString());
    }

    [Fact]
    public void Api_ReadsScalars()
    {
        var i = new TermSlot(); i.PutInt(99);
        Assert.Equal(TermSlot.Integer, ReftypeApi.findtype_c(i));
        Assert.True(ReftypeApi.getint_c(i, out var iv));
        Assert.Equal(99, iv);

        var f = new TermSlot(); f.PutFloat(2.5);
        Assert.Equal(TermSlot.Floating, ReftypeApi.findtype_c(f));
        Assert.True(ReftypeApi.getflt_c(f, out var fv));
        Assert.Equal(2.5, fv);

        var a = new TermSlot(); a.PutAtom("hi");
        Assert.Equal(TermSlot.String, ReftypeApi.findtype_c(a));   // atom reads as STRING(4)
        Assert.True(ReftypeApi.gettxt_c(a, out var av));
        Assert.Equal("hi", av);
    }

    [Fact]
    public void Api_ReadsCompoundArgs()
    {
        // build f(10, g(20)) then read it back through the accessor API.
        var s = new TermSlot();
        ReftypeApi.putfunctor_c("f", 2, s);
        ReftypeApi.getfuncarg_c(s, 1, out var a1); ReftypeApi.putint_c(10, a1);
        ReftypeApi.getfuncarg_c(s, 2, out var a2); ReftypeApi.putfunctor_c("g", 1, a2);
        ReftypeApi.getfuncarg_c(a2, 1, out var b1); ReftypeApi.putint_c(20, b1);

        Assert.True(ReftypeApi.getfuncarg_c(s, 2, out var read2));
        Assert.Equal(TermSlot.Functor, ReftypeApi.findtype_c(read2));
        Assert.True(ReftypeApi.getfunctor_c(read2, out var gn, out var ga));
        Assert.Equal("g", gn);
        Assert.Equal(1, ga);
        Assert.True(ReftypeApi.getfuncarg_c(read2, 1, out var read21));
        Assert.True(ReftypeApi.getint_c(read21, out var v));
        Assert.Equal(20, v);
    }

    // ---- Stage 2 part 1: the term-interface predicates are recognized by name;
    // their prlg_ifce.pl source clauses (reftype-struct tier, which we never
    // compile) are dropped under arity_compat, and the builtins provide them.

    [Fact]
    public void InterfaceClausesDropped_BuiltinsProvideThem()
    {
        var e = new PrologEngine();
        // These clauses carry reftype-struct-tier native blocks that would NOT
        // compile (`(*Ref)->ntype`). Under arity_compat they are dropped and the
        // builtins take over — consult succeeds and the predicates work.
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            "reftype_term(_, R) :- { Ref: preftype; Ref is R; T is ((*Ref)->ntype) }, fail.\n" +
            "fill_par(_, R) :- { Ref: preftype; Ref is R; 'freepar'(Ref) }, fail.\n" +
            "use(In, Out) :- '$new_reftype_slot'(S), fill_par(In, S), reftype_term(Out, S).\n");
        Assert.True(e.Query("use(foo(1, 2), T), T == foo(1, 2).").Success);
    }

    [Fact]
    public void Api_Equrefs()
    {
        var a = new TermSlot(); a.PutInt(5);
        var b = new TermSlot(); b.PutInt(5);
        var c = new TermSlot(); c.PutInt(6);
        Assert.True(ReftypeApi.equrefs_c(a, b));
        Assert.False(ReftypeApi.equrefs_c(a, c));
    }

    // ---- Stage 2 parts 2-3: the full Arity flow — a reftype global as a slot,
    // &name in a native block, a C interop function manipulating the TermSlot.

    private static class TermInterop
    {
        // reads an int from the slot, writes back a compound result(int+1).
        public static int bump(TermSlot r)
        {
            if (!ReftypeApi.getint_c(r, out var v)) return 0;
            ReftypeApi.putfunctor_c("result", 1, r);
            ReftypeApi.getfuncarg_c(r, 1, out var a1);
            ReftypeApi.putint_c(v + 1, a1);
            return 1;
        }
    }

    [Fact]
    public void FullFlow_GlobalSlot_NativeBlock_InteropManipulatesTerm()
    {
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(TermInterop));
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            ":- c.\nreftype par1ref;\nint bump(reftype);\n:- prolog.\n" +
            "go(In, Out) :-\n" +
            "  { Ptr: preftype; Ptr is &par1ref },\n" +
            "  fill_par(In, Ptr),\n" +
            "  { ret: int; ret = 'bump'(par1ref); Ret is ret },\n" +
            "  Ret =:= 1,\n" +
            "  reftype_term(Out, Ptr).\n");
        // In=10 → C reads 10, builds result(11) into the slot → Out = result(11).
        Assert.True(e.Query("go(10, Out), Out == result(11).").Success);
    }
}
