using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>The functor table is managed state a compiled module cannot
/// reach, so the host mirrors it into linear memory. The mirror is an EXACT
/// copy of the table's own packed (atomId, arity) array, not a projection of
/// it: a value that diverges wasm-side can then be named rather than only
/// counted, and the copy is a memcpy instead of a probe per id.</summary>
public sealed class FunctorMirrorTests
{
    [Fact]
    public void TheImageMirrorsEveryFunctorExactly()
    {
        // A corpus that interns a spread of names and arities, including the
        // ones the sub-index hops walk into.
        using var h = new WasmProgramHarness("""
            :- public m/2.
            m([w(a)|T], T).
            m([w(b, c)|T], T).
            m([parse(X, Y, Z)|T], f(X, Y, Z, T)).
            m([V|T], kept(V, T)).
            """);
        // Run one goal so the image is staged the way a real query stages it.
        h.Fresh();
        Cell arg = h.MakePartialList([], h.MakeStruct("w", h.MakeAtom("a")));
        Assert.False(h.SolveWith("m", arg, null));   // not a list: the catch-all needs one


        int checked_ = 0;
        for (int fid = 0; fid < WasmProgramHarness.MirroredCount; fid++)
        {
            if (!FunctorTable.TryLookup(fid, out var want)) continue;
            Assert.Equal(want, h.MirroredFunctor(fid));
            checked_++;
        }
        // ANTI-VACUITY: an empty table would make the loop above prove nothing.
        Assert.True(checked_ > 300, $"only {checked_} functors mirrored");
    }
}
