namespace Shumway.Core;

/// <summary>SS7.6.2 body conversion for a goal dispatched at a runtime
/// <c>call/N</c> boundary: every variable in goal position within the control
/// skeleton (<c>,</c>/<c>;</c>/<c>-&gt;</c>/<c>*-&gt;</c>) is wrapped as
/// <c>call(V)</c>, sharing the variable cell so later bindings flow.
///
/// <para>The conversion happens ONCE, at the call boundary — never at the
/// <c>'$call'/2</c> sub-dispatches, which execute a body this conversion
/// already produced. Converting lazily is wrong: by the time a sub-dispatch
/// reaches a variable sub-goal its home cell may already hold the value it
/// was bound to mid-body (a <c>!</c>, say), indistinguishable from one
/// written literally — and a metacalled variable's <c>!</c> must cut only
/// within its own call.</para></summary>
public static class MetaBodyConvert
{
    private static readonly int ConjFid =
        FunctorTable.Intern(AtomTable.Intern(",", permanent: true).Id, 2);
    private static readonly int DisjFid =
        FunctorTable.Intern(AtomTable.Intern(";", permanent: true).Id, 2);
    private static readonly int ArrowFid =
        FunctorTable.Intern(AtomTable.Intern("->", permanent: true).Id, 2);
    private static readonly int SoftArrowFid =
        FunctorTable.Intern(AtomTable.Intern("*->", permanent: true).Id, 2);
    private static readonly int Call1Fid =
        FunctorTable.Intern(AtomTable.Intern("call", permanent: true).Id, 1);
    private static readonly int MqualFid =
        FunctorTable.Intern(AtomTable.Intern("$mqual", permanent: true).Id, 2);
    private static readonly int ColonFid =
        FunctorTable.Intern(AtomTable.Intern(":", permanent: true).Id, 2);

    /// <summary>Returns the converted goal — the input untouched (and
    /// <paramref name="wrapped"/> left false) when nothing needed wrapping,
    /// which is allocation-free.</summary>
    public static Cell WrapVariableSubgoals(Activation engine, Cell c, ref bool wrapped)
    {
        Cell d = c.Tag == Tag.Ref ? engine.GetHeap(engine.Deref(c.AsHeapIndex)) : c;
        switch (d.Tag)
        {
            case Tag.Ref:
            case Tag.AttVar:
            {
                int wrapBase = engine.AllocateHeap(2);
                engine.SetHeap(wrapBase, Cell.Functor(Call1Fid));
                engine.SetHeap(wrapBase + 1, d);
                wrapped = true;
                return Cell.Str(wrapBase);
            }
            case Tag.Str:
            {
                int fIdx = d.AsHeapIndex;
                int fid = engine.GetHeap(fIdx).AsFunctorId;
                // A module qualifier is transparent to the conversion: the
                // module is data, the goal is arg 1 — and PrepareMqualGoal
                // DISTRIBUTES '$mqual' over a control construct's args, so
                // by the time the boundary converts, every sub-goal may
                // already sit under one.
                if (fid == MqualFid || fid == ColonFid)
                {
                    Cell inner = engine.GetHeap(fIdx + 2);
                    bool innerSub = false;
                    Cell wInner = WrapVariableSubgoals(engine, inner, ref innerSub);
                    if (!innerSub) return c;
                    wrapped = true;
                    int qBase = engine.AllocateHeap(3);
                    engine.SetHeap(qBase, Cell.Functor(fid));
                    engine.SetHeap(qBase + 1, engine.GetHeap(fIdx + 1));
                    engine.SetHeap(qBase + 2, wInner);
                    return Cell.Str(qBase);
                }
                if (fid != ConjFid && fid != DisjFid
                    && fid != ArrowFid && fid != SoftArrowFid)
                    return c;
                Cell arg0 = engine.GetHeap(fIdx + 1);
                Cell arg1 = engine.GetHeap(fIdx + 2);
                bool sub = false;
                Cell w0 = WrapVariableSubgoals(engine, arg0, ref sub);
                Cell w1 = WrapVariableSubgoals(engine, arg1, ref sub);
                if (!sub) return c;
                wrapped = true;
                int strBase = engine.AllocateHeap(3);
                engine.SetHeap(strBase, Cell.Functor(fid));
                engine.SetHeap(strBase + 1, w0);
                engine.SetHeap(strBase + 2, w1);
                return Cell.Str(strBase);
            }
            default:
                return c;
        }
    }
}
