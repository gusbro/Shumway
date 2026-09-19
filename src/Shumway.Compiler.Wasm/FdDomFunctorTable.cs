using Shumway.Core;

namespace Shumway.Compiler.Wasm;

/// <summary>The <c>'$fd_dom'</c> functor cells, indexed by INTERVAL COUNT:
/// entry k is the functor of a domain with k intervals, which has arity 2k
/// (ADR-051).
///
/// <para>A module needs this to build a domain whose interval count differs
/// from the one it started with: removing a value can split an interval or
/// empty one, and the resulting functor is a different functor. Interning is
/// the host's, so the host hands the module a table to index.</para>
///
/// <para>Staged by both worlds, which is why it lives here rather than in
/// either: the desktop copies it into linear memory and the browser pins it
/// once. The contents never change, so neither has anything to keep in
/// step.</para></summary>
public static class FdDomFunctorTable
{
    /// <summary>The largest interval count the table covers. A valve, not a
    /// semantic limit: a domain fragmented past this makes the module exit to
    /// the host, which is what every removal did before the table existed.
    /// The solver stays in single digits on the programs measured, and 64
    /// costs 520 bytes.</summary>
    public const int MaxIntervals = 64;

    /// <summary>Entry k is the functor cell for k intervals; entry 0 is
    /// unused and zero, which is also what an out-of-range count reads as.
    /// Built once: interning is idempotent and functor ids are stable for the
    /// life of the process.</summary>
    public static readonly long[] Cells = Build();

    private static long[] Build()
    {
        var t = new long[MaxIntervals + 1];
        int atom = AtomTable.Intern("$fd_dom", permanent: true).Id;
        for (int k = 1; k <= MaxIntervals; k++)
            t[k] = Cell.Functor(FunctorTable.Intern(atom, 2 * k)).Data;
        return t;
    }
}
