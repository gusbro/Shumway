using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>Resume state for a non-deterministic <c>[PrologPredicate]</c>
/// foreign predicate (the <c>IEnumerable&lt;T&gt;</c> return shape). Holds the
/// solution enumerator, the resume PC, the host engine, and a predicate-
/// specific "unify the current value" delegate — plus cached <c>Resume</c> and
/// <c>OnPrune</c> delegates.
///
/// <para>The source generator instantiates ONE of these per foreign call and
/// re-pushes the cached <c>Resume</c> unchanged on every backtrack, rather than
/// allocating a fresh closure (and a fresh <c>Dispose</c> Action) per solution
/// as the pre-cursor generated bridge did. <paramref name="unifyCurrent"/> is a
/// static method group, so its delegate is cached by the compiler — the whole
/// enumeration costs O(1) managed allocation regardless of solution count.</para></summary>
public sealed class NonDetForeignCursor<T>
{
    private readonly IEnumerator<T> _iter;
    private readonly int _returnPc;
    private readonly PrologEngine _host;
    private readonly Func<PrologEngine, Activation, T, bool> _unifyCurrent;
    public readonly Func<Activation, int, bool> Resume;
    public readonly Action OnPrune;

    public NonDetForeignCursor(
        IEnumerator<T> iter, int returnPc, PrologEngine host,
        Func<PrologEngine, Activation, T, bool> unifyCurrent)
    {
        _iter = iter;
        _returnPc = returnPc;
        _host = host;
        _unifyCurrent = unifyCurrent;
        Resume = (e, _) => Advance(e, isResume: true);
        OnPrune = _iter.Dispose;   // chunk 245 — cut past the CP disposes the iterator
    }

    /// <summary>The first step (from the foreign bridge body); returns into the
    /// normal post-builtin flow, so it does not call ResumeAtReturnPc.</summary>
    public bool Start(Activation engine) => Advance(engine, isResume: false);

    private bool Advance(Activation engine, bool isResume)
    {
        if (!_iter.MoveNext())
        {
            _iter.Dispose();
            return false;
        }
        // Push a CP optimistically — if there is no further solution the
        // resume's first MoveNext returns false and the CP collapses cleanly.
        engine.PushBuiltinChoicePoint(Resume, arity: 0, OnPrune);
        bool ok = _unifyCurrent(_host, engine, _iter.Current);
        if (ok && isResume) engine.ResumeAtReturnPc(_returnPc);
        return ok;   // false → the engine backtracks into the CP just pushed
    }
}
