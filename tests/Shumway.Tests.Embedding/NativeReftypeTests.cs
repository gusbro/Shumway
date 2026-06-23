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
    public void ReftypeBlock_CompilesToDelegate_NotInterpreter()
    {
        // ADR-024 IL path: a reftype block now compiles to a delegate (no per-call
        // dicts / boxing / tree-walk), for hot loops where the C# method is cheap
        // but called millions of times.
        int before = NativeBlockCompiler.CompiledCount;
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
        Assert.True(e.Query("go(10, Out), Out == result(11).").Success);
        // both reftype blocks compiled to delegates (didn't fall back).
        Assert.True(NativeBlockCompiler.CompiledCount >= before + 2);
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

    private const string FlowProgram =
        ":- set_prolog_flag(arity_compat, true).\n" +
        ":- public go/2.\n" +
        ":- c.\nreftype par1ref;\nint bump(reftype);\n:- prolog.\n" +
        "go(In, Out) :-\n" +
        "  { Ptr: preftype; Ptr is &par1ref },\n" +
        "  fill_par(In, Ptr),\n" +
        "  { ret: int; ret = 'bump'(par1ref); Ret is ret },\n" +
        "  Ret =:= 1,\n" +
        "  reftype_term(Out, Ptr).\n";

    [Fact]
    public void BundleFlow_ReftypeGlobal_SlotsCreatedAtLoad()
    {
        // A source-stripped Release bundle: the `:- c` declarations don't travel,
        // but the reftype block runs in the interpreter and creates its slot
        // on-demand (GetOrCreateReftypeSlot). The interop class is registered before
        // load, as for any native bundle.
        var bytes = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(FlowProgram, "prog", ShmoBuildMode.Release) },
            EntryPoints = new[] { new PredicateRef("go", 2) },
            StripSource = true,
            BakePrelude = true,
        }).Bytes!;
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(TermInterop));
        e.LoadBundle(BundleReader.FromBytes(bytes));
        Assert.True(e.Query("go(10, Out), Out == result(11).").Success);
    }

    // ---- Validation against the real Arity sources (the generic-term interface
    // and two users of it). Compiled when the corpus is present; a no-op otherwise
    // (the corpus lives outside the repo).

    // The worked example from docs/generic-term-interop.md — keeps the doc honest.
    private static class DocInterop
    {
        public static int swap_pair(TermSlot r)
        {
            if (ReftypeApi.findtype_c(r) != 5) return 0;
            ReftypeApi.getfunctor_c(r, out var name, out var arity);
            if (name != "pair" || arity != 2) return 0;
            ReftypeApi.getfuncarg_c(r, 1, out var a); ReftypeApi.getint_c(a, out var av);
            ReftypeApi.getfuncarg_c(r, 2, out var b); ReftypeApi.getint_c(b, out var bv);
            ReftypeApi.putfunctor_c("pair", 2, r);
            ReftypeApi.getfuncarg_c(r, 1, out var n1); ReftypeApi.putint_c(bv, n1);
            ReftypeApi.getfuncarg_c(r, 2, out var n2); ReftypeApi.putint_c(av, n2);
            return 1;
        }
    }

    [Fact]
    public void DocExample_SwapPair_Works()
    {
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(DocInterop));
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            ":- c.\nreftype buf;\nint swap_pair(reftype);\n:- prolog.\n" +
            "swap(P, Q) :-\n" +
            "  { Ptr: preftype; Ptr is &buf },\n" +
            "  fill_par(P, Ptr),\n" +
            "  { ret: int; ret = 'swap_pair'(buf); Ret is ret },\n" +
            "  Ret =:= 1,\n" +
            "  reftype_term(Q, Ptr).\n");
        Assert.True(e.Query("swap(pair(1, 2), Q), Q == pair(2, 1).").Success);
    }

    [Theory]
    [InlineData("prlg_ifce.pl")]   // the interface definition
    [InlineData("i_form_e.pl")]    // a user (fill_par → call C → reftype_term)
    [InlineData("i_gxprg.pl")]     // a user (multiple reftype globals)
    public void RealAritySource_CompilesCleanly(string file)
    {
        string path = System.IO.Path.Combine(@"C:\temp\test", file);
        if (!System.IO.File.Exists(path)) return;   // corpus not present — skip
        string src = System.IO.File.ReadAllText(path);
        var result = ShmoCompiler.TryCompileSource(src, "m", ShmoBuildMode.Release,
            arityCompat: true);
        Assert.True(result.Success,
            result.Errors.Count > 0 ? result.Errors[0].Message : "compile failed");
    }
}
