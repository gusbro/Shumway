namespace Shumway.Core;

/// <summary>the dispatch decision for a runtime meta-call goal,
/// cached per (goal atom id, total arity) in <see cref="Activation.MetaRouteCache"/>.
///
/// <para>A runtime meta-call (<c>call/N</c>, <c>'$call'/2</c>) classifies its
/// goal term by functor every time: intern the functor, compare against the
/// control-construct ids, probe the builtins registry, probe the query's
/// functor-address map. All of that is a pure function of (atom id, arity)
/// for the lifetime of one query's address map — so both dispatchers (the
/// bytecode interpreter's <c>DispatchCall</c> and Tier-1's
/// <c>IlMetaCallHelper.Dispatch</c>) cache the resolved route here and skip
/// straight to the action on a repeat goal.</para>
///
/// <para>Soundness of the cache lifetime: the cache is stamped with the
/// <see cref="Activation.CurrentFunctorAddresses"/> instance it was built
/// against and is discarded when that reference changes (a new query links
/// a new map). Within one query the map is add-only — mid-query auto
/// promotion adds entries, and in-place assertz/asserta/retract
/// patch bytecode without moving a predicate's trampoline
/// address — so a cached resolution never goes stale. Failed resolutions
/// (existence_error) are deliberately NOT cached: the same functor can
/// become resolvable later in the query via auto-promotion.</para>
///
/// <para>Each dispatcher executes a route kind exactly as its own slow path
/// would — the cache shares the <em>resolution</em> (an address or builtin
/// id, dispatcher-independent), never the action.</para></summary>
public enum MetaRouteKind : byte
{
    /// <summary>Goal is a cut-transparent control construct
    /// (<c>,/2</c>, <c>;/2</c>, <c>-&gt;/2</c>): store the cut barrier in
    /// X2, then jump to <see cref="MetaRoute.Arg"/> — the resolved address
    /// of the <c>$call_conj/disj/arrow</c> prelude helper.</summary>
    BarrierHelperJump = 1,

    /// <summary>Jump to <see cref="MetaRoute.Arg"/> — a user predicate's
    /// address (or resume marker), or the <c>$call_neg</c> helper.</summary>
    Jump = 2,

    /// <summary>Goal is <c>!</c>: cut to the call's barrier.</summary>
    Cut = 3,

    /// <summary>Goal is <c>true</c>.</summary>
    True = 4,

    /// <summary>Goal is <c>fail</c> / <c>false</c>.</summary>
    Fail = 5,

    /// <summary>Goal is itself the <c>call/N</c> builtin
    /// (<c>call(call(...), ...)</c>): re-enter dispatch with the builtin's
    /// arity and a fresh barrier. <see cref="MetaRoute.Arg"/> is the
    /// builtin id.</summary>
    CallRecurse = 6,

    /// <summary>Goal is the <c>'$call'/2</c> barrier-carrying meta-call.
    /// <see cref="MetaRoute.Arg"/> is the builtin id. (The interpreter
    /// runs its registered impl like any builtin; the Tier-1 helper
    /// re-enters dispatch with the barrier from X1 — each dispatcher
    /// mirrors its own slow path.)</summary>
    DollarCall = 7,

    /// <summary>Any other builtin: invoke its impl.
    /// <see cref="MetaRoute.Arg"/> is the builtin id.</summary>
    Builtin = 8,
}

/// <summary>A cached meta-call dispatch route — see <see cref="MetaRouteKind"/>.</summary>
public readonly struct MetaRoute
{
    public readonly MetaRouteKind Kind;

    /// <summary>Kind-dependent payload: a code address / resume marker for
    /// the jump kinds, a builtin id for the builtin kinds, unused otherwise.</summary>
    public readonly int Arg;

    public MetaRoute(MetaRouteKind kind, int arg)
    {
        Kind = kind;
        Arg = arg;
    }
}
