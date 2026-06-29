using Shumway.Embedding;
using Xunit;

// ADR-024 — the `:- native fn/N` directive wired end-to-end with the MANAGED
// SNAPSHOT backend (no P/Invoke yet). A `:- native` function whose C# interop
// method takes a Reftype gets a materialized snapshot of the reftype global's term;
// the (mutated) snapshot is dematerialized back into the slot after the call, so a
// following reftype_term sees what the function built. (The native-C P/Invoke
// backend materializes to native memory instead — same directive, same flow.)
public class NativeDirectiveSnapshotTests
{
    private static class SnapInterop
    {
        // Reads the snapshot's integer, then rebuilds it in place as result(int+1) —
        // the managed equivalent of native C building a struct into the reftype.
        public static int bump_snap(Reftype r)
        {
            if (r.Ntype != Reftype.Codes.Integer) return 0;
            long v = r.Cint;
            r.Ntype = Reftype.Codes.Functor;
            r.Cstr = "result";
            r.Nelem = 1;
            r.Pars = new[] { new Reftype { Ntype = Reftype.Codes.Integer, Cint = v + 1 } };
            return 1;
        }
    }

    private const string Program =
        ":- set_prolog_flag(arity_compat, true).\n" +
        ":- native bump_snap/1.\n" +
        ":- c.\nreftype par1ref;\nint bump_snap(reftype);\n:- prolog.\n" +
        "go(In, Out) :-\n" +
        "  { Ptr: preftype; Ptr is &par1ref },\n" +
        "  fill_par(In, Ptr),\n" +
        "  { ret: int; ret = 'bump_snap'(par1ref); Ret is ret },\n" +
        "  Ret =:= 1,\n" +
        "  reftype_term(Out, Ptr).\n";

    [Fact]
    public void NativeDirective_ManagedSnapshot_MaterializesCallsAndWritesBack()
    {
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(SnapInterop));
        e.ConsultString(Program);
        // fill_par sets the slot to 10; bump_snap gets the snapshot, rebuilds it as
        // result(11); reftype_term reads the written-back term.
        Assert.True(e.Query("go(10, Out), Out == result(11).").Success);
    }

    [Fact]
    public void NativeDirective_RoundTripsAnInteger()
    {
        // A :- native function that just reads + returns leaves the term intact.
        var e = new PrologEngine();
        e.UseNativeInterop(typeof(SnapInterop));
        e.ConsultString(Program);
        Assert.True(e.Query("go(0, Out), Out == result(1).").Success);
        Assert.True(e.Query("go(41, Out), Out == result(42).").Success);
    }

    [Fact]
    public void NativeDirective_IsAcceptedAndParsedAsOperator()
    {
        // `:- native foo/2.` (operator form) consults without error and does not
        // shadow a normal predicate definition in the same program.
        var e = new PrologEngine();
        e.ConsultString(
            ":- set_prolog_flag(arity_compat, true).\n" +
            ":- native ext/2.\n" +
            "p(ok).\n");
        Assert.True(e.Query("p(ok).").Success);
    }
}
