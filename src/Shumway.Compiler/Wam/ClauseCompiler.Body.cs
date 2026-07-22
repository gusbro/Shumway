using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Compiler.Wam;

public sealed partial class ClauseCompiler
{
    private static void CollectVarNames(Term root, HashSet<string> sink)
    {
        // Iterative: called on each head argument, so a recursive
        // descent overflowed on a deeply-nested head term (e.g. a long list).
        var stack = new Stack<Term>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case VarTerm v when v.Name != "_":
                    sink.Add(v.Name);
                    break;
                case CompoundTerm c:
                    foreach (var arg in c.Args) stack.Push(arg);
                    break;
            }
        }
    }

    private static List<Term> FlattenConjunction(Term body)
    {
        var goals = new List<Term>();
        var stack = new Stack<Term>();
        stack.Push(body);
        while (stack.Count > 0)
        {
            Term t = stack.Pop();
            if (t is CompoundTerm { Functor: ",", Args.Length: 2 } c)
            {
                stack.Push(c.Args[1]);
                stack.Push(c.Args[0]);
            }
            else if (t is AtomTerm { Name: "true" })
            {
                // 'true' has no effect — skip it.
            }
            else
            {
                goals.Add(t);
            }
        }
        return goals;
    }

    // ============================================================================
    // Head compilation (extends 8a / 8b with permanent-routed variables)
    // ============================================================================

    /// <summary>tries to compile <c>A = B</c> inline as head-style
    /// get / unify instead of a call to the <c>=/2</c> builtin. Handles the case
    /// where one side is a temporary (X-register) variable: a SEEN temp unifies
    /// the other side against its register (via <see cref="CompileHeadArg"/>); a
    /// FIRST-OCCURRENCE temp is bound to the other side (built with
    /// <see cref="CompileBodyArg"/> for a non-var, aliased for a seen var).
    /// Returns false — fall back to the builtin call — for permanent (Y)
    /// variables, both-first-occurrence vars, the anonymous variable, and
    /// both-non-var goals.</summary>
    private bool TryCompileUnifyInline(CompileState s, Term a, Term b)
        => TryUnifyVarWithPattern(s, a, b) || TryUnifyVarWithPattern(s, b, a);

    private bool TryUnifyVarWithPattern(CompileState s, Term vTerm, Term p)
    {
        if (vTerm is not VarTerm v || v.Name == "_") return false;
        if (s.Ys.ContainsKey(v.Name)) return false;   // permanent var — fall back

        if (!s.Xs.IsNewName(v.Name))
        {
            // Seen temporary: unify the pattern against V's register, exactly as
            // a head argument is matched.
            CompileHeadArg(s, p, s.Xs.GetSlot(v.Name));
            DrainPendingCompounds(s);
            return true;
        }

        // First-occurrence temporary: V := P.
        switch (p)
        {
            case VarTerm pv when pv.Name != "_"
                    && !s.Ys.ContainsKey(pv.Name) && !s.Xs.IsNewName(pv.Name):
                // V = W where W is a seen temp: V aliases W's register (no opcode).
                s.Xs.Bind(v.Name, s.Xs.GetSlot(pv.Name));
                return true;
            case VarTerm:
                return false;   // V = W with W first-occurrence / permanent / _ — fall back
            default:
                // V = <non-var term>: build the term into V's fresh home.
                int slot = s.Xs.AllocateFresh(v.Name);
                CompileBodyArg(s, p, slot);
                DrainPendingCompounds(s);
                return true;
        }
    }

    private void CompileHeadArg(CompileState s, Term arg, int argSlot)
    {
        switch (arg)
        {
            case AtomTerm a:
                s.Emitter.EmitGetAtom(InternAtom(a.Name), argSlot);
                break;

            case IntTerm n:
                if (FitsInt32(n.Value))
                    s.Emitter.EmitGetInteger((int)n.Value, argSlot);
                else
                    s.Emitter.EmitGetBigInt(
                        _bigIntLiterals.Intern(new System.Numerics.BigInteger(n.Value)),
                        argSlot);
                break;

            case BigIntTerm bn:
                s.Emitter.EmitGetBigInt(_bigIntLiterals.Intern(bn.Value), argSlot);
                break;

            case VarTerm v when v.Name == "_":
                // Anonymous — no constraint, no opcode.
                return;

            case VarTerm v when s.Ys.TryGetValue(v.Name, out int yIdx):
                // Permanent variable. The head provides its first known binding:
                // copy X[argSlot] into Y[yIdx]. Subsequent head occurrences of the
                // same variable get unified against the saved Y.
                if (s.YsInitialized.Contains(v.Name))
                {
                    s.Emitter.EmitGetValueY(yIdx, argSlot);
                }
                else
                {
                    s.Emitter.EmitGetVariableY(yIdx, argSlot);
                    s.YsInitialized.Add(v.Name);
                }
                break;

            case VarTerm v when s.Xs.IsNewName(v.Name):
                // Temp variable, first occurrence. Claim X[argSlot] as its home.
                s.Xs.Bind(v.Name, argSlot);
                break;

            case VarTerm v:
                // Temp variable, subsequent occurrence.
                s.Emitter.EmitGetValueX(s.Xs.GetSlot(v.Name), argSlot);
                break;

            case CompoundTerm c:
                s.Pending.Enqueue((argSlot, c));
                break;

            case FloatTerm f:
                s.Emitter.EmitGetFloat(_floatLiterals.Intern(f.Value), argSlot);
                break;
            case StringTerm str:
                s.Emitter.EmitGetPstr(_stringLiterals.Intern(str.Content), argSlot);
                break;
            default:
                throw new NotSupportedException(
                    $"Head argument type {arg.GetType().Name} is not supported.");
        }
    }

    /// <summary>Drains <see cref="CompileState.Pending"/> until empty. Each item is
    /// a compound that lives at some X slot; expanding it means emitting an open
    /// instruction (<c>get_list</c> or <c>get_structure</c>) and one
    /// <c>unify_*</c> per sub-arg.</summary>
    private void DrainPendingCompounds(CompileState s)
    {
        while (s.Pending.Count > 0)
        {
            var (slot, comp) = s.Pending.Dequeue();
            bool isList = comp.Functor == "." && comp.Args.Length == 2;

            var multiCellTemps = PreEmitMultiCellLiterals(s, comp.Args);

            if (isList)
                s.Emitter.EmitGetList(slot);
            else
                s.Emitter.EmitGetStructure(InternFunctor(comp.Functor, comp.Args.Length), slot);

            for (int i = 0; i < comp.Args.Length; i++)
            {
                bool last = i == comp.Args.Length - 1;
                if (multiCellTemps.TryGetValue(i, out int t))
                    s.Emitter.EmitUnifyValueX(t);
                // ADR-019: a nested compound in the LAST argument position is
                // built inline in the same write stream (unify_structure /
                // unify_list), dropping the temp + deferred get_structure per
                // nesting level. Last position only → the build stays linear
                // (no parent arg to resume).
                else if (last && comp.Args[i] is CompoundTerm lastComp
                         && CanInlineCompound(lastComp))
                    CompileUnifyArgInline(s, lastComp);
                else
                    CompileUnifyArg(s, comp.Args[i]);
            }

            // CSE: this top-level head-argument compound now lives, fully
            // matched, in its (stable) argument register — record it so a later
            // head sub-term equal to it is referenced instead of rebuilt.
            if (s.CseActive && slot < s.Arity && StructuralKey(comp) is string key)
                s.CseMap.TryAdd(key, slot);
        }
    }

    /// <summary>A canonical structural key for CSE, distinguishing functor,
    /// arity, atoms / integers and variable NAMES. Returns null for a term that
    /// can't be safely shared: one containing an anonymous variable (each <c>_</c>
    /// is a distinct fresh variable) or a multi-cell literal (float / string).</summary>
    private static string? StructuralKey(Term t)
    {
        var sb = new System.Text.StringBuilder();
        return AppendStructuralKey(t, sb, 0) ? sb.ToString() : null;
    }

    /// <summary>Depth past which CSE keying is abandoned (returns null, so the
    /// term is not shared). CSE of a large / deep head sub-term is pointless —
    /// the key would be an O(size) string and a recursive comparison
    /// overflowed the C# stack on a long list. A shallow bound keeps the useful
    /// small-compound CSE while sidestepping both costs.</summary>
    private const int StructuralKeyMaxDepth = 64;

    /// <summary>Serialized-key length past which CSE keying is abandoned.
    /// The depth bound alone does NOT bound the WORK: an AST that shares
    /// subterms (a DAG — the runtime materializer preserves sharing, so an
    /// asserted clause's head can carry one) serializes as its unshared TREE,
    /// exponential in depth — observed as a multi-GB StringBuilder hanging a
    /// Logtalk library load inside a runtime assertz. One shared
    /// budget threaded through the walk bounds total work to O(this) per key,
    /// and a key that long is useless for CSE anyway.</summary>
    private const int StructuralKeyMaxLength = 256;

    private static bool AppendStructuralKey(Term t, System.Text.StringBuilder sb, int depth)
    {
        if (sb.Length > StructuralKeyMaxLength) return false;   // budget blown
        switch (t)
        {
            case VarTerm { Name: "_" }: return false;
            case VarTerm v: sb.Append('$').Append(v.Name); return true;
            case AtomTerm a: sb.Append('\'').Append(a.Name); return true;
            case IntTerm n: sb.Append('#').Append(n.Value); return true;
            case BigIntTerm b: sb.Append('#').Append(b.Value); return true;
            case CompoundTerm c:
                if (depth >= StructuralKeyMaxDepth) return false;   // too deep to CSE
                sb.Append(c.Functor).Append('/').Append(c.Args.Length).Append('(');
                foreach (Term a in c.Args)
                {
                    if (!AppendStructuralKey(a, sb, depth + 1)) return false;
                    sb.Append(',');
                }
                sb.Append(')');
                return true;
            default: return false;   // float / string — don't CSE
        }
    }

    /// <summary>Builds a nested compound inline in the current unify stream
    /// (ADR-019). Only valid when <paramref name="c"/> is the last argument of
    /// its parent. Recurses into its own last-argument compound.</summary>
    /// <summary>CSE: if <paramref name="c"/> is structurally identical (incl.
    /// variable names) to a top-level head-argument compound already matched
    /// into an argument register during head matching, emit a
    /// <c>unify_value</c> reference to that register and return true — sharing
    /// the matched structure instead of rebuilding it. Only active while head
    /// matching is in progress (argument registers stable).</summary>
    private static bool TryEmitCse(CompileState s, CompoundTerm c)
    {
        if (!s.CseActive) return false;
        if (StructuralKey(c) is not string key) return false;
        if (!s.CseMap.TryGetValue(key, out int reg)) return false;
        s.Emitter.EmitUnifyValueX(reg);
        return true;
    }

    private void CompileUnifyArgInline(CompileState s, CompoundTerm c)
    {
        // The inline build recurses only into the LAST argument, so the chain
        // is linear — walk it as a loop rather than one C# frame per level.
        // the former recursion overflowed on a long list (whose
        // tail is always the last argument), crashing the host at compile time.
        // The emission order is identical.
        while (true)
        {
            if (TryEmitCse(s, c)) return;
            bool isList = c.Functor == "." && c.Args.Length == 2;
            if (isList)
                s.Emitter.EmitUnifyList();
            else
                s.Emitter.EmitUnifyStructure(InternFunctor(c.Functor, c.Args.Length));

            int n = c.Args.Length;
            for (int i = 0; i < n - 1; i++)
                CompileUnifyArg(s, c.Args[i]);

            // Last argument: continue the inline chain iteratively when it is a
            // further inlinable compound, else emit it and stop.
            if (n > 0 && c.Args[n - 1] is CompoundTerm last && CanInlineCompound(last))
            {
                c = last;
                continue;
            }
            if (n > 0)
                CompileUnifyArg(s, c.Args[n - 1]);
            return;
        }
    }

    /// <summary>True iff <paramref name="c"/> can be built inline in a write
    /// stream: none of its direct arguments is a multi-cell literal (float /
    /// string), which would have to be pre-emitted before the structure header
    /// and so break the contiguous-allocation invariant mid-stream. Such a
    /// compound falls back to the BFS (temp + <c>get_structure</c>), which
    /// pre-emits the literal at a clean point.</summary>
    private static bool CanInlineCompound(CompoundTerm c)
    {
        foreach (Term a in c.Args)
            if (a is FloatTerm or StringTerm) return false;
        return true;
    }

    // ============================================================================
    // ADR-020: reserve-upfront build for non-last nested compounds (body args)
    // ============================================================================

    /// <summary>True if the term tree has a non-last argument that is a compound
    /// — the case the on-demand BFS pays a temp + deferred <c>get_structure</c>
    /// for. Only such trees benefit from the reserve-upfront path.</summary>
    private static bool HasNonLastNestedCompound(Term t)
    {
        // Iterative: a recursive descent overflowed on a deeply
        // nested term (e.g. a long list) even though the answer for a flat list
        // is just false.
        if (t is not CompoundTerm) return false;
        var stack = new Stack<Term>();
        stack.Push(t);
        while (stack.Count > 0)
        {
            if (stack.Pop() is not CompoundTerm c) continue;
            for (int i = 0; i < c.Args.Length; i++)
            {
                bool last = i == c.Args.Length - 1;
                if (!last && c.Args[i] is CompoundTerm) return true;
                if (c.Args[i] is CompoundTerm inner) stack.Push(inner);
            }
        }
        return false;
    }

    /// <summary>True if every nested compound (depth ≥ 1) is inline-buildable.
    /// Reserved mode builds every nested compound in the write stream, so a
    /// nested float/string arg (which needs a mid-stream pre-emit that breaks
    /// contiguity) disqualifies the whole tree — it falls back to the BFS path.
    /// The root's own float/string args are fine (pre-emitted to temps before
    /// the root header).</summary>
    private static bool AllNestedCompoundsInlinable(Term t)
    {
        // Iterative: see HasNonLastNestedCompound.
        if (t is not CompoundTerm) return true;
        var stack = new Stack<Term>();
        stack.Push(t);
        while (stack.Count > 0)
        {
            if (stack.Pop() is not CompoundTerm c) continue;
            foreach (Term a in c.Args)
                if (a is CompoundTerm inner)
                {
                    if (!CanInlineCompound(inner)) return false;
                    stack.Push(inner);
                }
        }
        return true;
    }

    /// <summary>Emits the reserve-upfront root (<c>put_structure_r</c> /
    /// <c>put_list_r</c>) for a body-arg compound and walks its args, building
    /// every nested compound inline via <see cref="CompileReservedUnify"/> and
    /// every scalar via <see cref="CompileUnifyArg"/>. No temp, no deferred
    /// <c>get_structure</c> — the runtime write-pointer frame stack resumes the
    /// parent after each nested compound.</summary>
    private void CompileReservedBuild(CompileState s, CompoundTerm c, int argSlot)
    {
        bool isList = c.Functor == "." && c.Args.Length == 2;
        var multiCellTemps = PreEmitMultiCellLiterals(s, c.Args);
        if (isList)
            s.Emitter.EmitPutListR(argSlot);
        else
            s.Emitter.EmitPutStructureR(InternFunctor(c.Functor, c.Args.Length), argSlot, c.Args.Length);
        for (int i = 0; i < c.Args.Length; i++)
        {
            if (multiCellTemps.TryGetValue(i, out int t))
                s.Emitter.EmitUnifyValueX(t);
            else if (c.Args[i] is CompoundTerm sub)
                CompileReservedUnify(s, sub);
            else
                CompileUnifyArg(s, c.Args[i]);
        }
    }

    /// <summary>Builds a nested compound inline inside a reserved build: emits
    /// <c>unify_structure</c> / <c>unify_list</c> (which push a write-pointer
    /// frame at runtime) and recurses for nested compounds at ANY position
    /// (last or not — the frame stack resumes the parent). CSE still shares a
    /// repeated structure.</summary>
    private void CompileReservedUnify(CompileState s, CompoundTerm c)
    {
        if (TryEmitCse(s, c)) return;
        bool isList = c.Functor == "." && c.Args.Length == 2;
        // AllNestedCompoundsInlinable guarantees no float/string sub-arg here, so
        // this map is empty; kept for symmetry / defence.
        var multiCellTemps = PreEmitMultiCellLiterals(s, c.Args);
        if (isList)
            s.Emitter.EmitUnifyList();
        else
            s.Emitter.EmitUnifyStructure(InternFunctor(c.Functor, c.Args.Length));
        for (int i = 0; i < c.Args.Length; i++)
        {
            if (multiCellTemps.TryGetValue(i, out int t))
                s.Emitter.EmitUnifyValueX(t);
            else if (c.Args[i] is CompoundTerm sub)
                CompileReservedUnify(s, sub);
            else
                CompileUnifyArg(s, c.Args[i]);
        }
    }

    /// <summary>Pre-emits <c>put_float</c> / <c>put_pstr</c> for any float or
    /// string literal among the sub-args, allocating an anonymous X slot for
    /// each. Returns a map from sub-arg index to that slot; the caller emits
    /// <c>unify_value_x</c> against the slot in lieu of the inline <c>unify_*</c>.
    ///
    /// <para>Multi-cell literals can't live inline inside a compound being built
    /// in write mode: they'd corrupt the contiguous arg layout and break the
    /// <c>unify_pointer == heap_top</c> invariant for any subsequent
    /// <c>unify_*</c>. By allocating them ahead of the <c>put_structure</c> we
    /// keep arg cells one-each and let the compound just reference the literal
    /// via the temp register.</para></summary>
    private Dictionary<int, int> PreEmitMultiCellLiterals(CompileState s, Term[] args)
    {
        Dictionary<int, int>? temps = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case FloatTerm f:
                    temps ??= new Dictionary<int, int>();
                    int floatSlot = s.Xs.AllocateAnonymousSlot();
                    s.Emitter.EmitPutFloat(_floatLiterals.Intern(f.Value), floatSlot);
                    temps[i] = floatSlot;
                    break;
                case StringTerm str:
                    temps ??= new Dictionary<int, int>();
                    int strSlot = s.Xs.AllocateAnonymousSlot();
                    s.Emitter.EmitPutPstr(_stringLiterals.Intern(str.Content), strSlot);
                    temps[i] = strSlot;
                    break;
            }
        }
        return temps ?? new Dictionary<int, int>();
    }

    private void CompileUnifyArg(CompileState s, Term arg)
    {
        switch (arg)
        {
            case AtomTerm a:
                if (a.Name == "[]")
                    s.Emitter.EmitUnifyNil();
                else
                    s.Emitter.EmitUnifyAtom(InternAtom(a.Name));
                break;

            case IntTerm n:
                if (FitsInt32(n.Value))
                    s.Emitter.EmitUnifyInteger((int)n.Value);
                else
                    s.Emitter.EmitUnifyBigInt(
                        _bigIntLiterals.Intern(new System.Numerics.BigInteger(n.Value)));
                break;

            case BigIntTerm bn:
                s.Emitter.EmitUnifyBigInt(_bigIntLiterals.Intern(bn.Value));
                break;

            case VarTerm v when v.Name == "_":
                s.Emitter.EmitUnifyVoid(1);
                break;

            case VarTerm v when s.Ys.TryGetValue(v.Name, out int yIdx):
                if (s.YsInitialized.Contains(v.Name))
                {
                    s.Emitter.EmitUnifyValueY(yIdx);
                }
                else
                {
                    s.Emitter.EmitUnifyVariableY(yIdx);
                    s.YsInitialized.Add(v.Name);
                }
                break;

            case VarTerm v when s.Xs.IsNewName(v.Name):
                // Argument-register preferencing: extract straight into the
                // call-arg register this variable flows to, so the later
                // put_value_x is skipped (it sees the variable already in place).
                int xFresh;
                if (s.PreferredReg.TryGetValue(v.Name, out int pref))
                {
                    xFresh = pref;
                    s.Xs.Bind(v.Name, pref);
                }
                else
                {
                    xFresh = s.Xs.AllocateFresh(v.Name);
                }
                s.Emitter.EmitUnifyVariableX(xFresh);
                break;

            case VarTerm v:
                s.Emitter.EmitUnifyValueX(s.Xs.GetSlot(v.Name));
                break;

            case CompoundTerm c:
                // CSE: identical to an already-matched head-arg compound →
                // reference its register instead of rebuilding (works in both
                // modes: read unifies, write copies the shared structure).
                if (TryEmitCse(s, c)) break;
                int temp = s.Xs.AllocateAnonymousSlot();
                s.Emitter.EmitUnifyVariableX(temp);
                s.Pending.Enqueue((temp, c));
                break;

            // FloatTerm and StringTerm are handled upstream by
            // PreEmitMultiCellLiterals — they can't live inline as compound
            // sub-args in write mode without corrupting the heap layout. If
            // one slips through to here it's a bug in the caller.
            default:
                throw new NotSupportedException(
                    $"Unsupported sub-argument type {arg.GetType().Name}.");
        }
    }

    // ============================================================================
    // Body compilation
    // ============================================================================

    private void CompileBodyGoal(CompileState s, Term goal, bool isLast, bool hasFrame, int livePermsAfter, int[]? argOrder = null)
    {
        // Decompose into functor name + args.
        string fName;
        Term[] gArgs;
        switch (goal)
        {
            case AtomTerm a:
                fName = a.Name;
                gArgs = Array.Empty<Term>();
                break;
            case CompoundTerm c:
                fName = c.Functor;
                gArgs = c.Args;
                break;
            case VarTerm v:
                // ISO §7.8.3: a variable in goal position is the
                // meta-call call/1 of that variable. Most Prolog
                // sources (Blint.pl's `ifthen(X,Y) :- X -> !, Y.`,
                // SWI's library, etc.) rely on this. Rewrite to
                // call(X) so the standard meta-call dispatch fires.
                fName = "call";
                gArgs = new Term[] { v };
                break;
            default:
                throw new NotSupportedException(
                    $"Goal type {goal.GetType().Name} is not yet supported in clause bodies.");
        }

        // ADR-018 — arithmetic instruction set. `X is Expr` and the six
        // comparisons compile to a postfix a_eval_* sequence over the eval
        // stack: no expression term, no synthetic variables on the heap.
        if (fName == "is" && gArgs.Length == 2)
        {
            CompileArithIs(s, gArgs[0], gArgs[1], isLast, hasFrame);
            return;
        }
        if (gArgs.Length == 2 &&
            Shumway.Builtins.ArithmeticEvaluator.TryRelOp(fName, out var relOp))
        {
            // Fuse the flat `A cmp B` over simple leaves into one a_int_cmp;
            // otherwise fall back to the postfix a_eval_* sequence.
            if (TryResolveLeaf(s, gArgs[0], out int caK, out int caV)
                && TryResolveLeaf(s, gArgs[1], out int cbK, out int cbV))
            {
                s.Emitter.EmitAIntCmp((int)relOp, caK, caV, cbK, cbV);
            }
            else
            {
                CompileArithExpr(s, gArgs[0]);
                CompileArithExpr(s, gArgs[1]);
                s.Emitter.EmitAEvalCmp((int)relOp);
            }
            EmitArithEpilogue(s, isLast, hasFrame);
            return;
        }

        // inline `=/2` unification. Compile `Var = Term` with the
        // head-matching machinery (get_* / unify_*) instead of a call to the
        // =/2 builtin (which builds the term separately and dispatches). Mirrors
        // GProlog (`X = [A|B]` → get_list + unify). Only the safe temp-X-var
        // cases are inlined here; permanent (Y) vars and both-non-var goals fall
        // back to the builtin path below. This is a pure codegen change — it does
        // not affect the permanent/temporary classification.
        if (fName == "=" && gArgs.Length == 2
            && TryCompileUnifyInline(s, gArgs[0], gArgs[1]))
        {
            EmitArithEpilogue(s, isLast, hasFrame);
            return;
        }

        // Emit argument-prep for each goal arg. When argOrder is supplied
        // (Warren scheduler picked a topological order to minimise saves),
        // emit in that order; otherwise emit in natural arg order.
        if (argOrder is not null)
        {
            foreach (int i in argOrder)
                CompileBodyArg(s, gArgs[i], i);
        }
        else
        {
            for (int i = 0; i < gArgs.Length; i++)
                CompileBodyArg(s, gArgs[i], i);
        }
        DrainPendingCompounds(s);

        int functorId = InternFunctor(fName, gArgs.Length);

        // Builtin dispatch: if this functor is registered as a builtin, emit
        // call_builtin instead of call/execute. Builtins don't jump — they run
        // inline and return — so there's no "execute_builtin" form; the last-
        // goal path is just "call_builtin; (deallocate; ) proceed".
        if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(functorId, out int builtinId))
        {
            // A last goal must not trim the environment. When the clause has
            // no frame the "current" environment is the caller's, and trimming
            // it would discard the caller's still-live Y slots; when the clause
            // does have a frame, the deallocate emitted right after reclaims it
            // anyway. -1 is the interpreter's no-trim sentinel — the parallel
            // to Execute, which carries no trim operand at all.
            s.Emitter.EmitCallBuiltin(builtinId, isLast ? -1 : livePermsAfter);
            if (isLast)
            {
                // fuse Deallocate+Proceed for the common end-of-body epilogue.
                if (hasFrame) s.Emitter.EmitDeallocateProceed();
                else s.Emitter.EmitProceed();
            }
            return;
        }

        if (isLast && DebugCodegen && hasFrame)
        {
            // ADR-035 — leave the choice of last-call optimisation to the
            // runtime. The stub behind the opcode is the return path the
            // LCO-off dispatch points Cp at; the LCO-on dispatch deallocates
            // and jumps straight past it, which is byte-for-byte the behaviour
            // of the `deallocate; execute` below.
            int dlcPos = s.Emitter.Position;
            s.Emitter.EmitDebugLastCall(targetAddress: 0, numLivePermanents: livePermsAfter);
            s.Emitter.EmitDeallocateProceed();
            s.CallSites.Add(new CallSite(dlcPos, functorId, IsExecute: false));
        }
        else if (isLast)
        {
            // Last-call optimization: deallocate (if a frame is live) then execute.
            if (hasFrame)
                s.Emitter.EmitDeallocate();
            int execPos = s.Emitter.Position;
            s.Emitter.EmitExecute(targetAddress: 0);
            s.CallSites.Add(new CallSite(execPos, functorId, IsExecute: true));
        }
        else
        {
            int callPos = s.Emitter.Position;
            s.Emitter.EmitCall(targetAddress: 0, numLivePermanents: livePermsAfter);
            s.CallSites.Add(new CallSite(callPos, functorId, IsExecute: false));
        }
    }

    /// <summary>For each body-goal position <c>i</c>, computes how many Y
    /// slots are still live <em>after</em> goal <c>i</c> completes — i.e.
    /// referenced by any later goal. The result is one more than the
    /// highest Y index used in <c>goals[i+1..]</c> (or 0 when no later
    /// goal touches any permanent). Walking right-to-left in a single
    /// pass keeps the computation linear in clause length.
    ///
    /// <para>The deep-cut Y slot (if one was allocated) counts as a
    /// "permanent" for trimming purposes: it must survive every call
    /// up to the deep <c>!</c> that reads it. <paramref name="cutSlot"/>
    /// is the Y index of that slot when applicable, or -1 when there's
    /// no deep cut.</para></summary>
    private static int[] ComputeLivePermsAfterEachGoal(
        List<Term> goals, IReadOnlyDictionary<string, int> ys,
        int cutSlot, int totalPerms,
        IReadOnlyDictionary<int, int>? iteBarrierSlot = null)
    {
        int n = goals.Count;
        var result = new int[n];
        int maxLiveYIdx = -1;
        for (int i = n - 1; i >= 0; i--)
        {
            // result[i] is the live count AFTER goal i, so it reflects
            // accumulated uses from goals[i+1..n-1] only.
            result[i] = Math.Min(maxLiveYIdx + 1, totalPerms);
            // Now fold in goal[i]'s own usage so result[i-1] sees it.
            if (goals[i] is AtomTerm { Name: "!" })
            {
                // Deep cut at position > 0 reads Y[cutSlot]; neck cut at
                // position 0 reads _b0 directly and doesn't touch any Y.
                if (i > 0 && cutSlot >= 0 && cutSlot > maxLiveYIdx)
                    maxLiveYIdx = cutSlot;
            }
            else
            {
                // ADR-025 bring-up fix — an inline ITE reads its barrier
                // Y slot (get_level_b … cut), which sits ABOVE the named
                // permanents; goals BEFORE the ITE must keep the frame at
                // least that big, exactly like the deep-cut slot above.
                // Without this, a pre-ITE call's env trim let the cond's
                // callee overwrite the slot → a garbage cut barrier
                // (boyer's CompactTrails crash).
                if (iteBarrierSlot is not null
                    && iteBarrierSlot.TryGetValue(i, out int bSlot)
                    && bSlot > maxLiveYIdx)
                    maxLiveYIdx = bSlot;
                UpdateMaxLiveYIdxFromTerm(goals[i], ys, ref maxLiveYIdx);
            }
        }
        return result;
    }

    private static void UpdateMaxLiveYIdxFromTerm(
        Term root, IReadOnlyDictionary<string, int> ys, ref int maxYIdx)
    {
        // Iterative: a recursive descent overflowed on a deeply
        // nested body argument (e.g. a long list).
        var stack = new Stack<Term>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case VarTerm v:
                    if (ys.TryGetValue(v.Name, out int idx) && idx > maxYIdx)
                        maxYIdx = idx;
                    break;
                case CompoundTerm c:
                    foreach (Term arg in c.Args) stack.Push(arg);
                    break;
            }
        }
    }

    // ---------- ADR-018 arithmetic instruction set compilation ----------

    /// <summary>Constant-folds a fully-literal arithmetic expression at compile
    /// time. Returns the result as a numeric literal term, or false for any
    /// expression with a non-literal leaf (variable, atom constant such as
    /// <c>pi</c>, non-arithmetic compound) or one that raises at evaluation (a
    /// zero divisor, an overflow guard) — those are left to runtime so the
    /// behaviour / error fires exactly as <c>is/2</c> would.</summary>
    private static bool TryFoldConstExpr(Term expr, out Term folded)
    {
        folded = null!;
        if (!TryEvalConst(expr, out Shumway.Builtins.Number n)) return false;
        folded = n.IsFloat ? new FloatTerm(n.FloatValue)
            : n.IsBig ? new BigIntTerm(n.BigValue)
            : new IntTerm(n.IntValue);
        return true;
    }

    private static bool TryEvalConst(Term expr, out Shumway.Builtins.Number result)
    {
        result = default;
        switch (expr)
        {
            case IntTerm i: result = new Shumway.Builtins.Number(i.Value); return true;
            case BigIntTerm b: result = new Shumway.Builtins.Number(b.Value); return true;
            case FloatTerm f: result = new Shumway.Builtins.Number(f.Value); return true;
            case CompoundTerm c when c.Args.Length == 2
                    && Shumway.Builtins.ArithmeticEvaluator.TryBinOp(c.Functor, out var bop):
                if (!TryEvalConst(c.Args[0], out var a2) || !TryEvalConst(c.Args[1], out var b2))
                    return false;
                try { result = Shumway.Builtins.ArithmeticEvaluator.ApplyBin(bop, a2, b2); return true; }
                catch (Exception) { return false; }
            case CompoundTerm c when c.Args.Length == 1
                    && Shumway.Builtins.ArithmeticEvaluator.TryUnOp(c.Functor, out var uop):
                if (!TryEvalConst(c.Args[0], out var a1)) return false;
                try { result = Shumway.Builtins.ArithmeticEvaluator.ApplyUn(uop, a1); return true; }
                catch (Exception) { return false; }
            default: return false;
        }
    }

    /// <summary>Compiles <c>Target is Expr</c>: the postfix evaluation of
    /// <paramref name="expr"/> followed by an <c>a_eval_is</c> that delivers the
    /// popped result to <paramref name="target"/>. The target reaches its home
    /// directly (no scratch copy): an existing variable is unified in place
    /// (kind 3 X-reg / 4 Y-slot); a *first-occurrence* variable is bound by a
    /// plain register/Y store (kind 5 / 6) — no unbound heap cell, no
    /// unification — since the result simply becomes its value. Anything else
    /// (a literal target like <c>5 is 2+3</c>) falls back to a scratch + unify.
    /// No expression term is built.</summary>
    private void CompileArithIs(CompileState s, Term target, Term expr, bool isLast, bool hasFrame)
    {
        // Constant folding: a fully-literal arithmetic expression is
        // evaluated at compile time and delivered as a DIRECT unification of the
        // target with the resulting literal — `X is 1*2` becomes `X = 2` (a
        // put_integer; no eval stack, no runtime multiply). The fold reuses the
        // runtime ArithmeticEvaluator, so overflow→bigint, integer division and
        // float coercion are bit-identical to evaluating at run time. An
        // expression that would raise (zero divisor, non-evaluable leaf) is NOT
        // folded — it falls through so the error fires at the right time.
        if (TryFoldConstExpr(expr, out Term folded))
        {
            if (TryCompileUnifyInline(s, target, folded))
            {
                EmitArithEpilogue(s, isLast, hasFrame);
                return;
            }
            // Target the inline =/2 can't take (a permanent Y / literal target):
            // still deliver the folded constant — drops the runtime computation,
            // just keeps the eval-stack delivery below.
            expr = folded;
        }

        // Fuse the flat `Target is A op B` over simple leaf operands into a
        // single a_int_bin (operands resolved before the target, so a
        // first-occurrence target allocation never shadows an operand). Falls
        // through to the postfix a_eval_* sequence for nested / non-leaf cases.
        if (expr is CompoundTerm fc && fc.Args.Length == 2
            && Shumway.Builtins.ArithmeticEvaluator.TryBinOp(fc.Functor, out var fbop)
            && TryResolveLeaf(s, fc.Args[0], out int faK, out int faV)
            && TryResolveLeaf(s, fc.Args[1], out int fbK, out int fbV)
            && TryResolveTarget(s, target, out int ftK, out int ftV))
        {
            s.Emitter.EmitAIntBin((int)fbop, faK, faV, fbK, fbV, ftK, ftV);
            EmitArithEpilogue(s, isLast, hasFrame);
            return;
        }

        CompileArithExpr(s, expr);
        switch (target)
        {
            // Existing variable home — unify the result in place.
            case VarTerm v when v.Name != "_" && s.Ys.TryGetValue(v.Name, out int yIdx)
                    && s.YsInitialized.Contains(v.Name):
                s.Emitter.EmitAEvalIs(4, yIdx);
                break;
            case VarTerm v when v.Name != "_" && !s.Ys.ContainsKey(v.Name)
                    && !s.Xs.IsNewName(v.Name):
                s.Emitter.EmitAEvalIs(3, s.Xs.GetSlot(v.Name));
                break;
            // First-occurrence permanent (Y) variable — store the result and
            // mark it initialised; it never held an unbound var to unify.
            case VarTerm v when v.Name != "_" && s.Ys.ContainsKey(v.Name):
                int newY = s.Ys[v.Name];
                s.YsInitialized.Add(v.Name);
                s.Emitter.EmitAEvalIs(6, newY);   // kind 6 = set Y-slot
                break;
            // First-occurrence temporary (X) variable — store the result into
            // its fresh register home.
            case VarTerm v when v.Name != "_" && s.Xs.IsNewName(v.Name):
                int newX = s.Xs.AllocateFresh(v.Name);
                s.Emitter.EmitAEvalIs(5, newX);   // kind 5 = set X-register
                break;
            // Literal / anonymous / compound target — materialise it and unify.
            default:
                int scratch = s.Xs.AllocateAnonymousSlot();
                CompileBodyArg(s, target, scratch);
                DrainPendingCompounds(s);
                s.Emitter.EmitAEvalIs(3, scratch);
                break;
        }
        EmitArithEpilogue(s, isLast, hasFrame);
    }

    /// <summary>Emits the postfix instructions that leave the value of
    /// <paramref name="expr"/> on the eval stack. Numeric literals push
    /// directly; an existing variable pushes straight from its register / Y-slot
    /// home; a recognised arithmetic compound recurses then applies its op;
    /// anything else (a first-occurrence variable, an atom, a non-arithmetic
    /// compound) is loaded into a scratch register and pushed via
    /// <c>a_eval_push x-reg</c>, which derefs + arithmetically evaluates it at
    /// run time — handling a bound sub-expression, an unbound var
    /// (instantiation_error) and a non-evaluable term (type_error) exactly as
    /// is/2 does.</summary>
    private void CompileArithExpr(CompileState s, Term expr)
    {
        switch (expr)
        {
            case IntTerm n when FitsInt32(n.Value):
                s.Emitter.EmitAEvalPush(0, (int)n.Value);
                return;
            case IntTerm n:
                s.Emitter.EmitAEvalPush(1,
                    _bigIntLiterals.Intern(new System.Numerics.BigInteger(n.Value)));
                return;
            case BigIntTerm bn:
                s.Emitter.EmitAEvalPush(1, _bigIntLiterals.Intern(bn.Value));
                return;
            case FloatTerm f:
                s.Emitter.EmitAEvalPush(2, _floatLiterals.Intern(f.Value));
                return;
            // An already-bound variable evaluates from its home directly — no
            // copy. (An initialised Y-slot or an existing X register; a
            // first-occurrence variable is unbound and falls through to the
            // scratch path, which reproduces is/2's instantiation_error.)
            case VarTerm v when v.Name != "_" && s.Ys.TryGetValue(v.Name, out int yIdx)
                    && s.YsInitialized.Contains(v.Name):
                s.Emitter.EmitAEvalPush(4, yIdx);
                return;
            case VarTerm v when v.Name != "_" && !s.Ys.ContainsKey(v.Name)
                    && !s.Xs.IsNewName(v.Name):
                s.Emitter.EmitAEvalPush(3, s.Xs.GetSlot(v.Name));
                return;
            case CompoundTerm c when c.Args.Length == 2
                    && Shumway.Builtins.ArithmeticEvaluator.TryBinOp(c.Functor, out var bop):
                CompileArithExpr(s, c.Args[0]);
                CompileArithExpr(s, c.Args[1]);
                s.Emitter.EmitAEvalBin((int)bop);
                return;
            case CompoundTerm c when c.Args.Length == 1
                    && Shumway.Builtins.ArithmeticEvaluator.TryUnOp(c.Functor, out var uop):
                CompileArithExpr(s, c.Args[0]);
                s.Emitter.EmitAEvalUn((int)uop);
                return;
            default:
                int scratch = s.Xs.AllocateAnonymousSlot();
                CompileBodyArg(s, expr, scratch);
                DrainPendingCompounds(s);
                s.Emitter.EmitAEvalPush(3, scratch);   // kind 3 = X-register
                return;
        }
    }

    private static void EmitArithEpilogue(CompileState s, bool isLast, bool hasFrame)
    {
        if (!isLast) return;
        if (hasFrame) s.Emitter.EmitDeallocateProceed();
        else s.Emitter.EmitProceed();
    }

    /// <summary>Resolves a simple leaf operand for the fused a_int_* opcodes to
    /// its <c>(kind, value)</c> encoding: a 32-bit integer literal (kind 0), an
    /// already-bound X register (kind 3) or an initialised Y-slot (kind 4).
    /// Returns false for anything that needs the general path — a
    /// first-occurrence (unbound) variable, a bigint / float literal, an atom,
    /// or a nested compound.</summary>
    private static bool TryResolveLeaf(CompileState s, Term term, out int kind, out int val)
    {
        switch (term)
        {
            case IntTerm n when FitsInt32(n.Value):
                kind = 0; val = (int)n.Value; return true;
            case VarTerm v when v.Name != "_" && s.Ys.TryGetValue(v.Name, out int yIdx)
                    && s.YsInitialized.Contains(v.Name):
                kind = 4; val = yIdx; return true;
            case VarTerm v when v.Name != "_" && !s.Ys.ContainsKey(v.Name)
                    && !s.Xs.IsNewName(v.Name):
                kind = 3; val = s.Xs.GetSlot(v.Name); return true;
            default:
                kind = 0; val = 0; return false;
        }
    }

    /// <summary>Resolves a fused a_int_bin target to its <c>(kind, value)</c>:
    /// unify with an existing X register (3) / Y-slot (4), or store into a
    /// first-occurrence X register (5) / Y-slot (6) — the latter allocating /
    /// marking the variable as the result home. Returns false for a literal /
    /// anonymous / compound target (handled by the general path).</summary>
    private static bool TryResolveTarget(CompileState s, Term target, out int kind, out int val)
    {
        switch (target)
        {
            case VarTerm v when v.Name != "_" && s.Ys.TryGetValue(v.Name, out int yIdx)
                    && s.YsInitialized.Contains(v.Name):
                kind = 4; val = yIdx; return true;
            case VarTerm v when v.Name != "_" && !s.Ys.ContainsKey(v.Name)
                    && !s.Xs.IsNewName(v.Name):
                kind = 3; val = s.Xs.GetSlot(v.Name); return true;
            case VarTerm v when v.Name != "_" && s.Ys.ContainsKey(v.Name):
                val = s.Ys[v.Name]; s.YsInitialized.Add(v.Name); kind = 6; return true;
            case VarTerm v when v.Name != "_" && s.Xs.IsNewName(v.Name):
                val = s.Xs.AllocateFresh(v.Name); kind = 5; return true;
            default:
                kind = 0; val = 0; return false;
        }
    }

    private void CompileBodyArg(CompileState s, Term arg, int argSlot)
    {
        switch (arg)
        {
            case AtomTerm a:
                if (a.Name == "[]")
                    s.Emitter.EmitPutNil(argSlot);
                else
                    s.Emitter.EmitPutAtom(InternAtom(a.Name), argSlot);
                break;

            case IntTerm n:
                if (FitsInt32(n.Value))
                    s.Emitter.EmitPutInteger((int)n.Value, argSlot);
                else
                    s.Emitter.EmitPutBigInt(
                        _bigIntLiterals.Intern(new System.Numerics.BigInteger(n.Value)),
                        argSlot);
                break;

            case BigIntTerm bn:
                s.Emitter.EmitPutBigInt(_bigIntLiterals.Intern(bn.Value), argSlot);
                break;

            case VarTerm v when v.Name == "_":
                // Each anonymous gets a fresh heap unbound at argSlot. We give it
                // its own anonymous X slot too so the put_variable_x has somewhere
                // to dispose its REF.
                int anonSlot = s.Xs.AllocateAnonymousSlot();
                s.Emitter.EmitPutVariableX(anonSlot, argSlot);
                break;

            case VarTerm v when s.Ys.TryGetValue(v.Name, out int yIdx):
                if (s.YsInitialized.Contains(v.Name))
                {
                    s.Emitter.EmitPutValueY(yIdx, argSlot);
                }
                else
                {
                    s.Emitter.EmitPutVariableY(yIdx, argSlot);
                    s.YsInitialized.Add(v.Name);
                }
                break;

            case VarTerm v when s.Xs.IsNewName(v.Name):
                // First-time temp var in body context: allocate a slot, then
                // emit put_variable_x to materialise it on heap and replicate
                // the REF into both slot and argSlot.
                int xFresh = s.Xs.AllocateFresh(v.Name);
                s.Emitter.EmitPutVariableX(xFresh, argSlot);
                break;

            case VarTerm v:
                int existingSlot = s.Xs.GetSlot(v.Name);
                // Optimisation: skip the put_value_x when the variable already
                // lives at the destination register. Eliminates the put_value_x N, N
                // no-ops that show up frequently for clauses like p(X) :- q(X).
                if (existingSlot != argSlot)
                    s.Emitter.EmitPutValueX(existingSlot, argSlot);
                break;

            case CompoundTerm c:
                // ADR-020: a body-arg term tree with a non-last nested compound,
                // every nested compound inline-buildable, is built in reserve-
                // upfront write mode (put_structure_r / put_list_r + a runtime
                // write-pointer frame stack) — dropping the temp + deferred
                // get_structure the BFS pays per non-last nesting level. Trees
                // with only last-arg nesting (or none) keep the zero-overhead
                // allocate-on-demand path below unchanged.
                if (HasNonLastNestedCompound(c) && AllNestedCompoundsInlinable(c))
                {
                    CompileReservedBuild(s, c, argSlot);
                    break;
                }
                bool isList = c.Functor == "." && c.Args.Length == 2;
                // Float / string sub-args go through put_*-to-temp + unify_value_x;
                // see PreEmitMultiCellLiterals for why they can't live inline.
                var multiCellTemps = PreEmitMultiCellLiterals(s, c.Args);
                if (isList)
                    s.Emitter.EmitPutList(argSlot);
                else
                    s.Emitter.EmitPutStructure(InternFunctor(c.Functor, c.Args.Length), argSlot);
                // Sub-args run in write mode; the same CompileUnifyArg dispatcher
                // handles them. A nested compound in the LAST position is built
                // inline (ADR-019); other nested compounds are deferred onto the
                // pending queue and drained by DrainPendingCompounds.
                for (int i = 0; i < c.Args.Length; i++)
                {
                    bool last = i == c.Args.Length - 1;
                    if (multiCellTemps.TryGetValue(i, out int t))
                        s.Emitter.EmitUnifyValueX(t);
                    else if (last && c.Args[i] is CompoundTerm lastComp
                             && CanInlineCompound(lastComp))
                        CompileUnifyArgInline(s, lastComp);
                    else
                        CompileUnifyArg(s, c.Args[i]);
                }
                break;

            case FloatTerm f:
                s.Emitter.EmitPutFloat(_floatLiterals.Intern(f.Value), argSlot);
                break;
            case StringTerm str:
                s.Emitter.EmitPutPstr(_stringLiterals.Intern(str.Content), argSlot);
                break;
            default:
                throw new NotSupportedException(
                    $"Body argument type {arg.GetType().Name} is not supported.");
        }
    }

    // ============================================================================
    // Helpers
    // ============================================================================

    private static (string name, Term[] args) DecomposeHead(Term head)
    {
        return head switch
        {
            AtomTerm a => (a.Name, Array.Empty<Term>()),
            CompoundTerm c => (c.Functor, c.Args),
            _ => throw new NotSupportedException(
                $"Clause head must be an atom or compound, got {head.GetType().Name}."),
        };
    }

    /// <summary>Whether <paramref name="value"/> fits in the WAM bytecode's
    /// 32-bit integer-operand encoding. Anything wider rides the
    /// <c>BigInt</c> literal pool via <see cref="Opcode.GetBigInt"/> /
    /// <see cref="Opcode.PutBigInt"/> / <see cref="Opcode.UnifyBigInt"/>
    /// (see ADR-013).</summary>
    private static bool FitsInt32(long value) =>
        value >= int.MinValue && value <= int.MaxValue;

    private static int InternAtom(string name) =>
        AtomTable.Intern(name, permanent: true).Id;

    private static int InternFunctor(string name, int arity) =>
        FunctorTable.Intern(InternAtom(name), arity);

    /// <summary>
    /// Mutable state threaded through head + body compilation. Owns the byte
    /// buffer, the X / Y allocators, the pending-compound queue, and the list
    /// of call sites the linker will patch.
    /// </summary>
    private sealed class CompileState
    {
        public BytecodeEmitter Emitter { get; } = new();
        public VariableMap Xs { get; }
        public Dictionary<string, int> Ys { get; } = new();
        public HashSet<string> YsInitialized { get; } = new();

        /// <summary>Argument-register preferencing: a first-occurrence,
        /// head-extracted X variable that flows to a single first-goal call
        /// argument is allocated directly into that call's argument register, so
        /// the redundant <c>unify_variable_x temp</c> + <c>put_value_x temp,
        /// argReg</c> collapses to one <c>unify_variable_x argReg</c>. Populated
        /// before head compilation; consumed when the variable's
        /// <c>unify_variable</c> is emitted.</summary>
        public Dictionary<string, int> PreferredReg { get; } = new();
        public int PermanentCount { get; }
        public Queue<(int Slot, CompoundTerm Compound)> Pending { get; } = new();
        public List<CallSite> CallSites { get; } = new();

        /// <summary>ADR-025 — clause-local address-operand offsets from the
        /// inline if-then-else lowering (see CompiledClause.DispatchSites).</summary>
        public List<int> DispatchSites { get; } = new();

        /// <summary>ADR-035 — clause-local stop-site offsets (see
        /// CompiledClause.DebugStops). Empty unless compiling debuggable code.</summary>
        public List<DebugStop> DebugStops { get; } = new();

        /// <summary>Clause arity — the argument-register range [0, Arity).</summary>
        public int Arity { get; }

        /// <summary>ADR-019 / CSE: structural key of a top-level head-argument
        /// compound → the argument register holding it. Populated while head
        /// matching is in progress (<see cref="CseActive"/>); a later head
        /// sub-term equal to one of these is referenced via <c>unify_value</c>
        /// instead of being rebuilt. Argument registers are stable during head
        /// matching, so this is only valid then.</summary>
        public Dictionary<string, int> CseMap { get; } = new();
        public bool CseActive { get; set; }

        public CompileState(int arity, IReadOnlyList<string> permanents, int extraPermanentSlots = 0)
        {
            Arity = arity;
            Xs = new VariableMap(arity);
            for (int i = 0; i < permanents.Count; i++)
                Ys[permanents[i]] = i;
            PermanentCount = permanents.Count + extraPermanentSlots;
        }
    }
}
