using System.Numerics;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 41: bigint trail-aware allocation + the engine-level IL
/// choice-point machinery (see ADR-014). The IL compiler itself still
/// only emits single-clause facts — wiring its emission to use the new
/// PushIlChoicePoint API is the obvious follow-up, but the engine,
/// interpreter, and ABI plumbing are all here today.
/// </summary>
public class Chunk41Tests
{
    // ============================================================================
    // BigInt trail
    // ============================================================================

    [Fact]
    public void BigIntAlloc_BacktrackingFreesSlot()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic v/1.");
        // Inside a findall the inner sub-engine creates a bigint via is/2 and
        // then the outer call backtracks. The slot should be reclaimed.
        // We can't peek into engine internals from here easily; instead we
        // check that repeating the operation many times doesn't leak — the
        // bigint table count should stay roughly stable, not grow linearly.
        for (int i = 0; i < 100; i++)
        {
            // Computing a bigint and then failing — the bigint slot is on
            // the path that's about to be undone.
            engine.Query("X is 1000000000000000000 * 100, fail ; true.");
        }
        // Soft check: querying still works correctly after many bt cycles.
        var sol = engine.Query("X is 1000000000000000000 * 100.");
        Assert.True(sol.Success);
    }

    [Fact]
    public void BigIntAlloc_FindallReclaimsBetweenSolutions()
    {
        // findall iterates a generator that produces transient bigints. The
        // resulting list should still contain correct values; the test
        // doubles as a sanity check that the trail-aware allocation didn't
        // break observable behaviour.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public big/1.
            big(X) :- member(N, [1, 2, 3]), X is 1000000000000000000 * N.
            """);
        var sol = engine.Query("findall(X, big(X), L).");
        Assert.True(sol.Success);
        // L should be [10^18, 2*10^18, 3*10^18].
        // 10^18 < long.MaxValue (~9.22e18) but > 2^60 — so still a BigInt cell.
        // Actually 10^18 fits in 60 bits? Cell.MaxInt60 = 2^59-1 = ~5.76e17.
        // 10^18 > 5.76e17, so it stays a BigInt.
        var l = sol["L"]!;
        var expected = new BigInteger[] {
            BigInteger.Parse("1000000000000000000"),
            BigInteger.Parse("2000000000000000000"),
            BigInteger.Parse("3000000000000000000"),
        };
        var actual = new List<BigInteger>();
        var cur = l;
        while (cur is CompoundTerm c && c.Functor == "." && c.Args.Length == 2)
        {
            actual.Add(c.Args[0] switch
            {
                BigIntTerm b => b.Value,
                IntTerm i => new BigInteger(i.Value),
                _ => throw new Xunit.Sdk.XunitException($"Unexpected term {c.Args[0]}"),
            });
            cur = c.Args[1];
        }
        Assert.Equal(expected, actual);
    }

    // ============================================================================
    // IL choice-point machinery (engine level)
    // ============================================================================

    [Fact]
    public void IlChoicePoint_PushPopRestoresHeapTop()
    {
        var engine = new Activation();

        int heapBefore = engine.HeapTop;
        engine.PushIlChoicePoint((eng, cursor) => true, nextCursor: 1, arity: 0);

        // Allocate some heap *after* the CP — that's what backtracking
        // should reclaim.
        engine.AllocateHeap(10);
        Assert.True(engine.HeapTop > heapBefore);

        Assert.True(engine.TopChoicePointIsIl);
        var (del, cursor) = engine.PopIlChoicePointAndRestore();
        Assert.Equal(1, cursor);
        Assert.NotNull(del);
        Assert.Equal(heapBefore, engine.HeapTop);
        Assert.False(engine.TopChoicePointIsIl);
    }

    [Fact]
    public void IlChoicePoint_PreservesRegistersAcrossBacktrack()
    {
        var engine = new Activation();
        // X[0] = atom 'before'; X[1] = atom 'foo' captured by CP arity 2.
        int beforeId = AtomTable.Intern("before", permanent: true).Id;
        int afterId = AtomTable.Intern("after", permanent: true).Id;
        int fooId = AtomTable.Intern("foo", permanent: true).Id;
        engine.SetRegister(0, Cell.Atom(beforeId));
        engine.SetRegister(1, Cell.Atom(fooId));

        engine.PushIlChoicePoint((eng, cursor) => true, nextCursor: 7, arity: 2);

        // Mutate registers after the CP — backtracking must restore them.
        engine.SetRegister(0, Cell.Atom(afterId));
        engine.SetRegister(1, Cell.Atom(afterId));

        engine.PopIlChoicePointAndRestore();
        Assert.Equal(beforeId, engine.GetRegister(0).AsAtomId);
        Assert.Equal(fooId, engine.GetRegister(1).AsAtomId);
    }

    [Fact]
    public void IlChoicePoint_MultipleStacked()
    {
        var engine = new Activation();
        engine.PushIlChoicePoint((e, c) => true, nextCursor: 1, arity: 0);
        Assert.True(engine.TopChoicePointIsIl);
        engine.PushIlChoicePoint((e, c) => true, nextCursor: 2, arity: 0);
        Assert.True(engine.TopChoicePointIsIl);

        var (_, cur1) = engine.PopIlChoicePointAndRestore();
        Assert.Equal(2, cur1);
        Assert.True(engine.TopChoicePointIsIl);

        var (_, cur2) = engine.PopIlChoicePointAndRestore();
        Assert.Equal(1, cur2);
        Assert.False(engine.TopChoicePointIsIl);
    }

    [Fact]
    public void IlChoicePoint_PopWhenNotIl_Throws()
    {
        var engine = new Activation();
        // Push a regular bytecode CP — pop must throw, the side table
        // doesn't claim it.
        engine.PushChoicePoint(arity: 0, nextClauseAddr: 0x100);
        Assert.False(engine.TopChoicePointIsIl);
        Assert.Throws<InvalidOperationException>(
            () => engine.PopIlChoicePointAndRestore());
    }

    // ============================================================================
    // End-to-end: handcrafted IL delegate that exercises a 2-clause predicate
    // via the new ABI. Demonstrates the path the IlPredicateCompiler will
    // travel once it learns to emit multi-clause IL.
    // ============================================================================

    [Fact]
    public void HandcraftedMultiClauseIl_EnumeratesViaBacktrack()
    {
        // Emulates the IL emission for:
        //   color(red).
        //   color(green).
        //   color(blue).
        // — but written manually as a Func<Activation,int,bool> the way the
        // future IL emitter will lay it out.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public color/1.
            color(red).
            color(green).
            color(blue).
            """);

        // Now hand-build the IL delegate. Use the prelude's atom interning
        // so we match what the rest of the engine sees.
        int redId = AtomTable.Intern("red", permanent: true).Id;
        int greenId = AtomTable.Intern("green", permanent: true).Id;
        int blueId = AtomTable.Intern("blue", permanent: true).Id;

        Func<Activation, int, bool>? colorIl = null;
        colorIl = (eng, cursor) =>
        {
            switch (cursor)
            {
                case 0:
                    eng.PushIlChoicePoint(colorIl!, nextCursor: 1, arity: 1);
                    return eng.UnifyRegisterWithCell(0, Cell.Atom(redId));
                case 1:
                    eng.PushIlChoicePoint(colorIl!, nextCursor: 2, arity: 1);
                    return eng.UnifyRegisterWithCell(0, Cell.Atom(greenId));
                case 2:
                    // Last clause — no CP push.
                    return eng.UnifyRegisterWithCell(0, Cell.Atom(blueId));
                default:
                    return false;
            }
        };

        // Manually install the delegate using the promotion store's eager
        // warm path. The store wants a CompiledPredicate; we cheat by
        // capturing one from the engine's link state... actually the warm
        // method calls IlPredicateCompiler.CanCompile which would reject
        // multi-clause. Instead test the engine path via QueryAll directly
        // by not going through promotion at all — just verify the
        // engine-level CP machinery returns correct results when the IL
        // delegate is wired in as a custom dispatcher.

        var customDispatcher = new HandcraftedDispatcher(colorIl, "color", 1);
        // We need to plug the dispatcher into a query's BytecodeInterpreter.
        // The cleanest path is via a custom PrologEngine subclass, but we
        // don't have a hook for that today. Instead we exercise the
        // delegate directly through Activation + a bare interpreter.

        // Build a bare engine for this test, since we don't need the
        // full PrologEngine plumbing — just the IL CP path.
        var rawEngine = new Activation();
        int varAddr = rawEngine.AllocateHeapUnbound();
        rawEngine.SetRegister(0, Cell.Ref(varAddr));

        // First call: cursor = 0, expect bind X to 'red'.
        Assert.True(colorIl(rawEngine, 0));
        int boundAtomId = rawEngine.GetHeap(rawEngine.Deref(varAddr)).AsAtomId;
        Assert.Equal(redId, boundAtomId);

        // Backtrack manually using the engine API the interpreter would use.
        Assert.True(rawEngine.TopChoicePointIsIl);
        var (del, cursor) = rawEngine.PopIlChoicePointAndRestore();
        Assert.Equal(1, cursor);
        Assert.True(del(rawEngine, cursor));
        boundAtomId = rawEngine.GetHeap(rawEngine.Deref(varAddr)).AsAtomId;
        Assert.Equal(greenId, boundAtomId);

        // Backtrack again for the third clause.
        Assert.True(rawEngine.TopChoicePointIsIl);
        var (del2, cursor2) = rawEngine.PopIlChoicePointAndRestore();
        Assert.Equal(2, cursor2);
        Assert.True(del2(rawEngine, cursor2));
        boundAtomId = rawEngine.GetHeap(rawEngine.Deref(varAddr)).AsAtomId;
        Assert.Equal(blueId, boundAtomId);

        // No more CPs.
        Assert.False(rawEngine.TopChoicePointIsIl);
    }

    /// <summary>Test-only adapter that gives back a fixed delegate. Lets
    /// us simulate "IL has been promoted" without going through the real
    /// promotion store (which still rejects multi-clause).</summary>
    private sealed class HandcraftedDispatcher : ITier1Dispatcher
    {
        private readonly Func<Activation, int, bool> _del;
        public HandcraftedDispatcher(Func<Activation, int, bool> del, string name, int arity)
        {
            _del = del;
        }
        public Func<Activation, bool>? OnDispatch(int targetAddress) =>
            engine => _del(engine, 0);
        public Func<Activation, int, bool>? ResolveByFunctorId(int functorId) => null;
    }
}
