using Shumway.Compiler.Wasm;

namespace Shumway.Web;

/// <summary>What <c>jit_compile/1</c> chose for one session (ADR-068). It
/// outlives the engine: a restart builds a new one, and must come back in
/// the mode the session was working under. One per session, so one session's
/// choice changes neither another's next boot nor the form another's modules
/// compile to.</summary>
internal sealed class WasmTierSettings
{
    /// <summary>Set by jit_compile(off): the boot attaches no tier and
    /// installs no bundle module, so a restart gives an engine with no wasm
    /// in it. Cleared by any jit_compile that turns the tier back on.</summary>
    public bool Disabled;

    /// <summary>The threshold a restart comes back with.</summary>
    public int BootThreshold = 1;

    /// <summary>Whether the restarted engine compiles in batches.</summary>
    public bool BootBatch = true;

    /// <summary>False for a session whose program runs inside one query for
    /// as long as it lives, as a notebook's does. What a goal of that query
    /// consults is linked into the query's own code, and a batch compiles
    /// against the static link, which the query does not run: its modules
    /// would send the query to addresses that hold other code. There
    /// jit_compile(all) is a threshold of one.</summary>
    public bool BatchAllowed = true;

    /// <summary>The settings of such a session: each predicate is compiled
    /// where it runs, when it has been called enough.</summary>
    public static WasmTierSettings ForOneLongQuery()
        => new() { BatchAllowed = false, BootBatch = false, BootThreshold = LazyThreshold };

    /// <summary>The threshold jit_compile(on) names.</summary>
    public const int LazyThreshold = 16;

    /// <summary>The form the session's next builds compile to: continuation
    /// functions (ADR-061) or partitions.</summary>
    public bool Cps = WasmPredicateCompiler.CpsMode;

    /// <summary>Under <see cref="Cps"/>, what a function holds.</summary>
    public WasmCpsGrain Grain = WasmPredicateCompiler.CpsGrain;
}
