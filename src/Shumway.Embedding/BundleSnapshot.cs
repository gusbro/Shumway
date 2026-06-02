namespace Shumway.Embedding;

/// <summary>
/// Save-state chunk 264 — a snapshot of a live <see cref="PrologEngine"/>'s
/// user-visible state, optionally attached to a <see cref="Bundle"/> as a
/// V6 trailer. A bundle with a non-null <c>Snapshot</c> can be loaded by
/// <see cref="PrologEngine.RestoreState"/> to reconstitute the engine's
/// state from scratch (full mode) or to merge dynamic facts into an
/// existing engine (dynamic-only mode).
///
/// <para>The snapshot deliberately holds source-level Prolog text and
/// term-encoded clauses rather than the engine's internal compiled
/// representation: <c>RestoreState</c> re-consults sources and re-asserts
/// dynamic clauses, so the snapshot survives engine internals evolving
/// (bytecode layout changes, atom-table reshuffles, etc.) and the same
/// snapshot can be loaded by a different Shumway minor version.</para>
/// </summary>
public sealed class BundleSnapshot
{
    /// <summary>True when this snapshot only carries dynamic clauses
    /// (no consult history). <see cref="PrologEngine.RestoreState"/>
    /// in that case <em>merges</em> the clauses into the engine's
    /// current state via <c>assertz</c>, skipping the reset / replay
    /// pass.</summary>
    public bool DynamicOnly { get; }

    /// <summary>Every source string previously passed to
    /// <see cref="PrologEngine.ConsultString"/>, in order. Excludes
    /// the prelude (the engine ctor reloads it automatically).
    /// Empty when <see cref="DynamicOnly"/> is true.</summary>
    public IReadOnlyList<string> ConsultHistory { get; }

    /// <summary>One entry per dynamic predicate that has at least one
    /// asserted clause at snapshot time. Clauses are term-encoded via
    /// <see cref="TermCodec"/>, preserving structural sharing but
    /// not the original source layout (the engine's internal AST is
    /// what gets round-tripped).</summary>
    public IReadOnlyList<ShmoDynamicSeed> DynamicClauses { get; }

    public BundleSnapshot(
        bool dynamicOnly,
        IReadOnlyList<string> consultHistory,
        IReadOnlyList<ShmoDynamicSeed> dynamicClauses)
    {
        DynamicOnly = dynamicOnly;
        ConsultHistory = consultHistory;
        DynamicClauses = dynamicClauses;
    }
}
