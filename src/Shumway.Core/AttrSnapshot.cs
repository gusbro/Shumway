using System.Collections.Generic;

namespace Shumway.Core;

/// <summary>
/// The set of attributed-variable homes as of one moment, taken by
/// <c>call_residue_vars/2</c> and diffed against a later one.
///
/// <para>It holds raw HEAP ADDRESSES, deliberately: an integer observes without
/// retaining, so a variable that becomes garbage during the goal is not pinned
/// by having been recorded. The price is that the heap collector has to
/// relocate these addresses when it moves cells — a nominal type rather than a
/// bare <c>HashSet&lt;int&gt;</c> so the collector can pick them out of the
/// object table without touching a foreign object that merely happens to hold
/// integers.</para>
///
/// <para>Stale-address trap: compaction REUSES addresses, so an entry left
/// unmapped could match an unrelated variable that later lands where the old
/// one was. Relocation has to happen in the same pass as everything else, with
/// no window in between.</para>
/// </summary>
public sealed class AttrSnapshot
{
    public HashSet<int> Homes { get; }

    public AttrSnapshot(IEnumerable<int> homes) => Homes = new HashSet<int>(homes);
}
