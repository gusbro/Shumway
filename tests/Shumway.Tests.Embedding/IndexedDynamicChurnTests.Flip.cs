using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

// A predicate's calls cross the JIT threshold: the next setup lays it out
// indexed in place of its plain chain, with no build of the product and no
// relink of the buffer, and every caller reaches the version.
public sealed partial class IndexedDynamicChurnTests
{
    private const string FlipProgram = """
        :- dynamic d/1, r/1.
        p(X) :- d(X).
        r(X) :- d(X).
        """;

    private static PrologEngine FlipEngine(int threshold)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 0;   // the chains are the interpreter's
        e.JitIndexing.Threshold = threshold;
        e.RegisterPredicates(typeof(Host));
        e.ConsultString(FlipProgram);
        Assert.True(e.Query("assertz(d(a)), assertz(d(b)), assertz(d(c)).").Success);
        return e;
    }

    private static int DFid => FunctorTable.Intern(AtomTable.Intern("d", permanent: true).Id, 1);

    // Three calls, one per query: hot, but still the chain of the cold compile.
    private static PrologEngine HotAfterThreeCalls()
    {
        var e = FlipEngine(threshold: 3);
        for (int i = 0; i < 3; i++) Assert.True(e.Query("d(a).").Success);
        Assert.True(e.JitIndexing.IsHot(DFid));
        Assert.Equal(0, e.HotnessFlipsInPlace);
        return e;
    }

    [Fact]
    public void APredicateCalledPastTheThresholdIsLaidOutIndexedInPlace()
    {
        var e = HotAfterThreeCalls();
        int fid = DFid;
        long builds = e.ProgramProductBuilds, drops = e.PersistentInvalidations;
        int calls = e.JitIndexing.CallCountFor(fid);
        Assert.True(e.Query("d(c).").Success);   // laid out at this setup
        Assert.Equal(1, e.HotnessFlipsInPlace);
        Assert.Equal(builds, e.ProgramProductBuilds);
        Assert.Equal(drops, e.PersistentInvalidations);
        Assert.True(e.JitIndexing.CallCountFor(fid) > calls, "the call was not counted at the version");
        // The version is the indexed layout, the address map names it, and the
        // entry of the cold compile leads to it.
        int at = e._persistentAddressesCache![fid];
        byte[] prog = e._persistentProgram!;
        Assert.Equal(Opcode.EnterDynamic, (Opcode)prog[at]);
        Assert.Equal(Opcode.SwitchOnTerm, (Opcode)prog[at + 1]);
        int old = e._dynamicLink!.Addresses[fid];
        Assert.Equal(Opcode.Execute, (Opcode)prog[old]);
        Assert.Equal(at, BytecodeIO.ReadInt32(prog, old + 1));
        // Every caller reaches it: the query, a static rule, a dynamic rule.
        Assert.True(e.Query("d(b).").Success);
        Assert.False(e.Query("d(z).").Success);
        Assert.True(e.Query("p(b).").Success);
        Assert.True(e.Query("r(b).").Success);
        // The in-place paths take the indexed layout.
        Assert.True(e.Query("retract(d(b)).").Success);
        Assert.Equal(1, e.IndexedRetracts);
        Assert.True(e.Query("assertz(d(q)), asserta(d(z)).").Success);
        Assert.True(e.Query("d(q), d(z), \\+ d(b).").Success);
        // Once: the decision is recorded with the version.
        Assert.Equal(1, e.HotnessFlipsInPlace);
        Assert.Equal(builds, e.ProgramProductBuilds);
        Assert.Equal(drops, e.PersistentInvalidations);
    }

    [Fact]
    public void TheChainTheVersionReplacesGoesToTheFreeList()
    {
        var e = HotAfterThreeCalls();
        long free = FreeBytes(e);
        Assert.True(e.Query("d(c).").Success);
        Assert.Equal(1, e.HotnessFlipsInPlace);
        Assert.True(FreeBytes(e) > free, "the chain's chunks were not freed");
    }

    private static long FreeBytes(PrologEngine e)
    {
        long bytes = 0;
        foreach (var (_, length) in e.DynChains.FreeChunks) bytes += length;
        return bytes;
    }

    // The flip seen at a nested query's setup, with the outer activation on the
    // buffer: the program is laid out again, as before, and the outer goal goes
    // on in its buffer.
    [Fact]
    public void AFlipAtANestedQuerysSetupLaysTheProgramOutAgain()
    {
        var e = FlipEngine(threshold: 2);
        Assert.True(e.Query("d(a).").Success);
        long drops = e.PersistentInvalidations;
        Assert.True(e.Query("d(b), nested_query('d(c)'), d(a).").Success);
        Assert.Equal(0, e.HotnessFlipsInPlace);
        Assert.Equal(drops + 1, e.PersistentInvalidations);
        Assert.True(e.Query("retract(d(b)).").Success);
        Assert.Equal(1, e.IndexedRetracts);
    }

    [Fact]
    public void APredicateWithRulesIsLaidOutAgainWithTheProgram()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 0;
        e.JitIndexing.Threshold = 3;
        e.ConsultString(":- dynamic q/1.");
        Assert.True(e.Query("assertz(q(a)), assertz((q(X) :- X = b)).").Success);
        for (int i = 0; i < 3; i++) Assert.True(e.Query("q(a).").Success);
        long builds = e.ProgramProductBuilds;
        Assert.True(e.Query("q(b).").Success);
        Assert.Equal(0, e.HotnessFlipsInPlace);
        Assert.Equal(builds + 1, e.ProgramProductBuilds);
    }

    // An empty predicate's stub is a plain chain whatever the compile: hot or
    // not, it keeps the one it has, and the clauses asserted after join it.
    [Fact]
    public void AnEmptyPredicateGoingHotKeepsItsChain()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 0;
        e.JitIndexing.Threshold = 3;
        e.ConsultString(":- dynamic d/1.");
        for (int i = 0; i < 3; i++) Assert.False(e.Query("d(a).").Success);
        Assert.True(e.JitIndexing.IsHot(DFid));
        long builds = e.ProgramProductBuilds, drops = e.PersistentInvalidations;
        Assert.False(e.Query("d(a).").Success);
        Assert.Equal(0, e.HotnessFlipsInPlace);
        Assert.Equal(builds, e.ProgramProductBuilds);
        Assert.Equal(drops, e.PersistentInvalidations);
        Assert.True(e.JitIndexing.CompileDecision(DFid));
        Assert.True(e.Query("assertz(d(a)), assertz(d(b)).").Success);
        Assert.True(e.Query("d(b), \\+ d(c).").Success);
        Assert.True(e.Query("retract(d(a)).").Success);
        Assert.Equal(0, e.IndexedRetracts);   // the chain, not the indexed layout
        Assert.Equal(builds, e.ProgramProductBuilds);
    }

    // Nothing to index: the chain stays, and the decision is recorded so the
    // setups stop looking.
    [Fact]
    public void AnArityZeroPredicateGoingHotCostsNothing()
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 0;
        e.JitIndexing.Threshold = 3;
        e.ConsultString(":- dynamic f/0.");
        Assert.True(e.Query("assertz(f).").Success);
        for (int i = 0; i < 3; i++) Assert.True(e.Query("f.").Success);
        long builds = e.ProgramProductBuilds, drops = e.PersistentInvalidations;
        for (int i = 0; i < 3; i++) Assert.True(e.Query("f.").Success);
        Assert.Equal(0, e.HotnessFlipsInPlace);
        Assert.Equal(builds, e.ProgramProductBuilds);
        Assert.Equal(drops, e.PersistentInvalidations);
        int fid = FunctorTable.Intern(AtomTable.Intern("f", permanent: true).Id, 0);
        Assert.True(e.JitIndexing.CompileDecision(fid));
    }
}
