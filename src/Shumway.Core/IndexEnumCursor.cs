namespace Shumway.Core;

/// <summary>Reusable resume driver for a builtin that enumerates a fixed,
/// precomputed set of candidates <c>0.._count-1</c> on backtracking, unifying
/// each via a caller-supplied <paramref name="tryAt"/>. Allocates one cursor +
/// one <c>tryAt</c> delegate per enumeration call, and its choice point is
/// retried in place (<see cref="Activation.ArmBuiltinChoicePoint"/>): nothing
/// per step.
///
/// <para><c>tryAt(engine, i)</c> unifies candidate <c>i</c> into the argument
/// registers and returns whether it matched; a <c>false</c> return lets the
/// engine backtrack into the cursor, which then tries <c>i+1</c>. It runs after
/// the choice point is armed, so what it binds a retry undoes. <c>arity</c>
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
        engine.ArmBuiltinChoicePoint(c._resume, arity, more: count > 1, isResume: false);
        return tryAt(engine, 0);
    }

    private bool Resume(Activation engine, int _)
    {
        int i = _index++;
        engine.ArmBuiltinChoicePoint(_resume, _arity, more: _index < _count, isResume: true);
        bool ok = _tryAt(engine, i);
        if (ok) engine.ResumeAtReturnPc(_returnPc);
        return ok;   // false → engine backtracks into the CP just pushed (next i)
    }
}
