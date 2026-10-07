namespace Shumway.Core;

/// <summary>SS7.6.2 body conversion for a goal dispatched at a runtime
/// <c>call/N</c> boundary: every variable in goal position within the control
/// skeleton (<c>,</c>/<c>;</c>/<c>-&gt;</c>/<c>*-&gt;</c>) is wrapped as
/// <c>call(V)</c>, sharing the variable cell so later bindings flow.
///
/// <para>The conversion happens once, at the call boundary — never at the
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

    /// <summary>SS7.8.3: a control construct's arguments must convert to a
    /// body before any of it runs — a number in goal position anywhere in the
    /// skeleton makes the whole construct the type_error culprit, and nothing
    /// executes. Expects the construct's two arguments in X0/X1 (the shape
    /// both dispatchers have built by the time they route). Sees through
    /// '$mqual'/':' tags — PrepareMqualGoal distributes the module tag over a
    /// construct's args before this check runs — and strips them from the
    /// culprit so the ball names the goal the caller wrote.
    ///
    /// <para>Also applies the conversion: a variable in goal position within
    /// the assembled construct is wrapped as <c>call(V)</c> in place, exactly
    /// as WrapVariableSubgoals does for a whole-goal <c>call/1</c>. Without
    /// it a metacalled <c>call(',', X=!, (Y=1,X ; Y=2))</c> executes the
    /// bound <c>!</c> as if written literally and cuts the disjunction.</para>
    /// </summary>
    public static void CheckControlGoalFromRegisters(Activation engine, int atomId)
    {
        Cell a = StripQual(engine, engine.GetRegister(0));
        Cell b = StripQual(engine, engine.GetRegister(1));
        if (Convertible(engine, a, true, 0) && Convertible(engine, b, true, 0))
        {
            bool w0 = false;
            Cell c0 = WrapVariableSubgoals(engine, engine.GetRegister(0), ref w0);
            if (w0) engine.SetRegister(0, c0);
            bool w1 = false;
            Cell c1 = WrapVariableSubgoals(engine, engine.GetRegister(1), ref w1);
            if (w1) engine.SetRegister(1, c1);
            return;
        }
        int fid = FunctorTable.Intern(atomId, 2);
        int strBase = engine.AllocateHeap(3);
        engine.SetHeap(strBase, Cell.Functor(fid));
        engine.SetHeap(strBase + 1, a);
        engine.SetHeap(strBase + 2, b);
        var ball = new PrologRuntimeException(
            "type_error", "callable", engine, Cell.Str(strBase));
        // The conversion is call/N's: the ball's context reads call/1
        // (the shape Trealla and Scryer print), unless an inner throw
        // already owns the identity.
        ball.StampBuiltin("call", 1);
        throw ball;
    }

    private static Cell StripQual(Activation engine, Cell c)
    {
        while (true)
        {
            c = Deref(engine, c);
            if (c.Tag != Tag.Str) return c;
            int fIdx = c.AsHeapIndex;
            if (engine.GetHeap(fIdx).AsFunctorId != MqualFid) return c;
            c = engine.GetHeap(fIdx + 2);
        }
    }

    // The skeleton's depth is the program's to choose (a frozen goal list,
    // a conjunction built by a fold), and a .NET stack overflow cannot be
    // caught. So every walk here is mixed: recursive up to this depth, an
    // explicit stack below it.
    private const int RecursionBudget = 256;

    private static bool IsControl(int fid) =>
        fid == ConjFid || fid == DisjFid || fid == ArrowFid || fid == SoftArrowFid;

    /// <summary>§7.8.3: whether every goal position of the control skeleton
    /// holds a callable term or a variable. <paramref name="throughQualifiers"/>
    /// looks through <c>'$mqual'</c>/<c>':'</c> to the goal they tag.</summary>
    public static bool IsBodyConvertible(Activation engine, Cell c, bool throughQualifiers)
        => Convertible(engine, c, throughQualifiers, 0);

    private static bool Convertible(Activation engine, Cell c, bool throughQualifiers, int depth)
    {
        // The right argument loops: a right-nested chain costs no stack.
        while (true)
        {
            c = Deref(engine, c);
            switch (c.Tag)
            {
                case Tag.Ref:
                case Tag.AttVar:
                case Tag.Atom:
                    return true;
                case Tag.Str:
                {
                    int fIdx = c.AsHeapIndex;
                    int fid = engine.GetHeap(fIdx).AsFunctorId;
                    if (IsControl(fid))
                    {
                        if (depth >= RecursionBudget)
                            return ConvertibleDeep(engine, c, throughQualifiers);
                        if (!Convertible(engine, engine.GetHeap(fIdx + 1), throughQualifiers, depth + 1))
                            return false;
                        c = engine.GetHeap(fIdx + 2);
                        continue;
                    }
                    if (throughQualifiers && (fid == MqualFid || fid == ColonFid))
                    {
                        c = engine.GetHeap(fIdx + 2);
                        continue;
                    }
                    return true;
                }
                default:
                    return false;
            }
        }
    }

    /// <summary>Convertible past the recursion budget. The check reads and
    /// never writes, so the visiting order is free.</summary>
    private static bool ConvertibleDeep(Activation engine, Cell root, bool throughQualifiers)
    {
        var pending = new Stack<Cell>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            Cell c = Deref(engine, pending.Pop());
            switch (c.Tag)
            {
                case Tag.Ref:
                case Tag.AttVar:
                case Tag.Atom:
                    continue;
                case Tag.Str:
                {
                    int fIdx = c.AsHeapIndex;
                    int fid = engine.GetHeap(fIdx).AsFunctorId;
                    if (IsControl(fid))
                    {
                        pending.Push(engine.GetHeap(fIdx + 2));
                        pending.Push(engine.GetHeap(fIdx + 1));
                    }
                    else if (throughQualifiers && (fid == MqualFid || fid == ColonFid))
                        pending.Push(engine.GetHeap(fIdx + 2));
                    continue;
                }
                default:
                    return false;
            }
        }
        return true;
    }

    private static Cell Deref(Activation engine, Cell c) =>
        c.Tag == Tag.Ref ? engine.GetHeap(engine.Deref(c.AsHeapIndex)) : c;

    /// <summary>Returns the converted goal — the input untouched (and
    /// <paramref name="wrapped"/> left false) when nothing needed wrapping,
    /// which is allocation-free.
    ///
    /// <para>Besides wrapping unbound sub-goals as <c>call(V)</c>, this
    /// normalises bound goal positions: a cell that reaches a value through a
    /// REF chain is replaced by the dereferenced cell. Downstream a raw REF
    /// in goal position then means exactly "was a variable at the call
    /// boundary" — the discriminator DispatchCall's fresh-barrier rule needs.
    /// Without it, <c>X = !, call((… , X))</c> ships the ref to the sub
    /// dispatch, which cannot tell it from a variable bound MID-body, and
    /// the pre-bound <c>!</c> loses its (real, §7.8.3) cut.</para></summary>
    public static Cell WrapVariableSubgoals(Activation engine, Cell c, ref bool wrapped)
        => Wrap(engine, c, ref wrapped, 0);

    private static Cell Wrap(Activation engine, Cell c, ref bool wrapped, int depth)
    {
        Cell d = Deref(engine, c);
        switch (d.Tag)
        {
            case Tag.Ref:
            case Tag.AttVar:
                wrapped = true;
                return WrapCall(engine, d);
            case Tag.Str:
            {
                int fIdx = d.AsHeapIndex;
                int fid = engine.GetHeap(fIdx).AsFunctorId;
                // A module qualifier is transparent to the conversion: the
                // module is data, the goal is arg 1 — and PrepareMqualGoal
                // distributes '$mqual' over a control construct's args, so
                // by the time the boundary converts, every sub-goal may
                // already sit under one.
                bool qualifier = fid == MqualFid || fid == ColonFid;
                if (!qualifier && !IsControl(fid)) return Normalized(c, d, ref wrapped);
                if (depth >= RecursionBudget) return WrapDeep(engine, c, ref wrapped);
                if (qualifier)
                {
                    bool innerSub = false;
                    Cell wInner = Wrap(engine, engine.GetHeap(fIdx + 2), ref innerSub, depth + 1);
                    if (!innerSub) return Normalized(c, d, ref wrapped);
                    wrapped = true;
                    return Rebuild(engine, fid, 2, engine.GetHeap(fIdx + 1), wInner);
                }
                bool sub = false;
                Cell w0 = Wrap(engine, engine.GetHeap(fIdx + 1), ref sub, depth + 1);
                Cell w1 = Wrap(engine, engine.GetHeap(fIdx + 2), ref sub, depth + 1);
                if (!sub) return Normalized(c, d, ref wrapped);
                wrapped = true;
                return Rebuild(engine, fid, 2, w0, w1);
            }
            default:
                return Normalized(c, d, ref wrapped);
        }
    }

    private static Cell WrapCall(Activation engine, Cell variable)
    {
        int wrapBase = engine.AllocateHeap(2);
        engine.SetHeap(wrapBase, Cell.Functor(Call1Fid));
        engine.SetHeap(wrapBase + 1, variable);
        return Cell.Str(wrapBase);
    }

    /// <summary>One open node of <see cref="WrapDeep"/>: a qualifier waiting
    /// for its goal, or a control construct waiting for its first argument
    /// (<see cref="SecondArg"/> false) or its second.</summary>
    private struct WrapFrame
    {
        public Cell Original;
        public Cell Dereffed;
        public int Fid;
        public bool SecondArg;
        public bool Sub;
        public Cell W0;
    }

    /// <summary>Wrap past the recursion budget: the same post-order rebuild,
    /// children allocated before their parent, with the open nodes on an
    /// explicit stack.</summary>
    private static Cell WrapDeep(Activation engine, Cell root, ref bool wrapped)
    {
        var frames = new WrapFrame[64];
        int count = 0;
        Cell next = root;
        while (true)
        {
            // Descend the leftmost open path down to a leaf.
            Cell result;
            bool changed;
            while (true)
            {
                Cell d = Deref(engine, next);
                if (d.Tag == Tag.Str)
                {
                    int fIdx = d.AsHeapIndex;
                    int fid = engine.GetHeap(fIdx).AsFunctorId;
                    bool qualifier = fid == MqualFid || fid == ColonFid;
                    if (qualifier || IsControl(fid))
                    {
                        if (count == frames.Length) Array.Resize(ref frames, count * 2);
                        frames[count++] = new WrapFrame { Original = next, Dereffed = d, Fid = fid };
                        next = engine.GetHeap(fIdx + (qualifier ? 2 : 1));
                        continue;
                    }
                }
                bool variable = d.Tag is Tag.Ref or Tag.AttVar;
                changed = variable || !next.Equals(d);
                result = variable ? WrapCall(engine, d) : changed ? d : next;
                break;
            }

            // Hand the result up through every node it completes.
            while (true)
            {
                if (count == 0)
                {
                    if (changed) wrapped = true;
                    return result;
                }
                ref WrapFrame top = ref frames[count - 1];
                int fIdx = top.Dereffed.AsHeapIndex;
                bool qualifier = top.Fid == MqualFid || top.Fid == ColonFid;
                if (!qualifier && !top.SecondArg)
                {
                    top.SecondArg = true;
                    top.Sub = changed;
                    top.W0 = result;
                    next = engine.GetHeap(fIdx + 2);
                    break;
                }
                count--;
                bool sub = qualifier ? changed : top.Sub || changed;
                if (sub)
                    result = qualifier
                        ? Rebuild(engine, top.Fid, 2, engine.GetHeap(fIdx + 1), result)
                        : Rebuild(engine, top.Fid, 2, top.W0, result);
                else
                {
                    sub = !top.Original.Equals(top.Dereffed);
                    result = sub ? top.Dereffed : top.Original;
                }
                changed = sub;
            }
        }
    }

    /// <summary>The dereferenced cell, flagging the change so the enclosing
    /// skeleton rebuilds with it; the original when no ref chain was
    /// involved (allocation-free fast path).</summary>
    private static Cell Normalized(Cell original, Cell dereffed, ref bool wrapped)
    {
        if (original.Equals(dereffed)) return original;
        wrapped = true;
        return dereffed;
    }

    /// <summary>Rebuilds a control construct (<paramref name="ctor"/>,
    /// dereferenced) with each goal-position argument tagged
    /// <c>'$mqual'(Module, Arg)</c>, so a module travels into the sub-goals of
    /// a runtime meta-goal. An if-then-else argument (<c>-&gt;</c>,
    /// <c>*-&gt;</c>) is distributed into rather than wrapped: a wrapping tag
    /// would hide it from <c>'$call_disj'</c>'s structural match (ADR-037).
    /// Both dispatchers' PrepareMqualGoal use it.</summary>
    public static Cell DistributeModule(
        Activation engine, Cell ctor, int module, bool arg0Goal, bool arg1Goal)
        => Distribute(engine, ctor, module, arg0Goal, arg1Goal, 0);

    private static Cell Distribute(
        Activation engine, Cell ctor, int module, bool arg0Goal, bool arg1Goal, int depth)
    {
        if (depth >= RecursionBudget)
            return DistributeDeep(engine, ctor, module, arg0Goal, arg1Goal);
        int src = ctor.AsHeapIndex;
        int fid = engine.GetHeap(src).AsFunctorId;
        var (_, arity) = FunctorTable.Lookup(fid);
        Cell a0 = arity > 0 ? engine.GetHeap(src + 1) : default;
        Cell a1 = arity > 1 ? engine.GetHeap(src + 2) : default;
        Cell w0 = arg0Goal && arity > 0 ? TagGoal(engine, module, a0, depth) : a0;
        Cell w1 = arg1Goal && arity > 1 ? TagGoal(engine, module, a1, depth) : a1;
        return Rebuild(engine, fid, arity, w0, w1);
    }

    private static Cell TagGoal(Activation engine, int module, Cell goal, int depth)
    {
        Cell d = Deref(engine, goal);
        return IsIfThen(engine, d)
            ? Distribute(engine, d, module, true, true, depth + 1)
            : TagModule(engine, module, goal);
    }

    private static bool IsIfThen(Activation engine, Cell d)
    {
        if (d.Tag != Tag.Str) return false;
        int fid = engine.GetHeap(d.AsHeapIndex).AsFunctorId;
        return fid == ArrowFid || fid == SoftArrowFid;
    }

    private static Cell TagModule(Activation engine, int module, Cell goal)
    {
        int f = engine.AllocateHeap(3);
        engine.SetHeap(f, Cell.Functor(MqualFid));
        engine.SetHeap(f + 1, Cell.Atom(module));
        engine.SetHeap(f + 2, goal);
        return Cell.Str(f);
    }

    private static Cell Rebuild(Activation engine, int fid, int arity, Cell arg0, Cell arg1)
    {
        int f = engine.AllocateHeap(arity + 1);
        engine.SetHeap(f, Cell.Functor(fid));
        if (arity > 0) engine.SetHeap(f + 1, arg0);
        if (arity > 1) engine.SetHeap(f + 2, arg1);
        return Cell.Str(f);
    }

    /// <summary>One construct of <see cref="DistributeDeep"/>; <see cref="Next"/>
    /// is the next argument to tag (2: both done, rebuild).</summary>
    private struct DistributeFrame
    {
        public int Src;
        public int Fid;
        public int Arity;
        public bool Goal0;
        public bool Goal1;
        public int Next;
        public Cell W0;
        public Cell W1;
    }

    /// <summary>Distribute past the recursion budget, the open if-then-elses
    /// on an explicit stack; same visiting and allocation order.</summary>
    private static Cell DistributeDeep(
        Activation engine, Cell ctor, int module, bool arg0Goal, bool arg1Goal)
    {
        var frames = new DistributeFrame[64];
        int count = 0;
        Open(ref frames, ref count, ctor, arg0Goal, arg1Goal);
        while (true)
        {
            ref DistributeFrame top = ref frames[count - 1];
            if (top.Next < 2)
            {
                int i = top.Next++;
                if (i >= top.Arity) continue;
                Cell a = engine.GetHeap(top.Src + 1 + i);
                Cell w = a;
                if (i == 0 ? top.Goal0 : top.Goal1)
                {
                    Cell d = Deref(engine, a);
                    if (IsIfThen(engine, d))
                    {
                        Open(ref frames, ref count, d, true, true);
                        continue;
                    }
                    w = TagModule(engine, module, a);
                }
                if (i == 0) top.W0 = w; else top.W1 = w;
                continue;
            }
            Cell built = Rebuild(engine, top.Fid, top.Arity, top.W0, top.W1);
            if (--count == 0) return built;
            ref DistributeFrame parent = ref frames[count - 1];
            if (parent.Next == 1) parent.W0 = built; else parent.W1 = built;
        }

        void Open(ref DistributeFrame[] fs, ref int n, Cell c, bool g0, bool g1)
        {
            if (n == fs.Length) Array.Resize(ref fs, n * 2);
            int src = c.AsHeapIndex;
            int fid = engine.GetHeap(src).AsFunctorId;
            fs[n++] = new DistributeFrame
            {
                Src = src, Fid = fid, Arity = FunctorTable.Lookup(fid).Arity,
                Goal0 = g0, Goal1 = g1,
            };
        }
    }
}
