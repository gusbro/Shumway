namespace Shumway.Core;

/// <summary>Reusable resume driver for a builtin that enumerates a fixed,
/// precomputed set of candidates <c>0.._count-1</c> on backtracking, unifying
/// each via a caller-supplied <paramref name="tryAt"/>. Allocates one cursor +
/// one <c>tryAt</c> delegate per enumeration call and re-pushes the cached
/// resume delegate unchanged on every backtrack — nothing per step (the same
/// fix applied to <c>between/3</c> and the other backtrackable builtins, which
/// previously allocated a fresh closure per candidate).
///
/// <para><c>tryAt(engine, i)</c> unifies candidate <c>i</c> into the argument
/// registers and returns whether it matched; a <c>false</c> return lets the
/// engine backtrack into the cursor, which then tries <c>i+1</c> — exactly the
/// behaviour of the per-step <c>…Step</c> methods this replaces. <c>arity</c>
/// is how many argument registers the choice point must save / restore.</para></summary>
public sealed class IndexEnumCursor
{
    private int _index;
    private readonly int _count;
    private readonly int _arity;
    private readonly int _returnPc;
    private readonly Func<Activation, int, bool> _tryAt;
    private readonly Func<Activation, int, bool> _resume;

    private IndexEnumCursor(int count, int arity, int returnPc, Func<Activation, int, bool> tryAt)
    {
        _count = count;
        _arity = arity;
        _returnPc = returnPc;
        _tryAt = tryAt;
        _resume = Resume;
    }

    /// <summary>Starts the enumeration: unifies candidate 0 (returning into the
    /// normal post-builtin flow) and, when more candidates remain, pushes a
    /// cursor that yields the rest on backtracking. Returns false for an empty
    /// set or when candidate 0 fails with no successor.</summary>
    public static bool Start(
        Activation engine, int count, int arity, int returnPc, Func<Activation, int, bool> tryAt)
    {
        if (count <= 0) return false;
        var c = new IndexEnumCursor(count, arity, returnPc, tryAt);
        c._index = 1;
        if (count > 1) engine.PushBuiltinChoicePoint(c._resume, arity);
        return tryAt(engine, 0);
    }

    private bool Resume(Activation engine, int _)
    {
        int i = _index++;
        if (_index < _count) engine.PushBuiltinChoicePoint(_resume, _arity);
        bool ok = _tryAt(engine, i);
        if (ok) engine.ResumeAtReturnPc(_returnPc);
        return ok;   // false → engine backtracks into the CP just pushed (next i)
    }
}
