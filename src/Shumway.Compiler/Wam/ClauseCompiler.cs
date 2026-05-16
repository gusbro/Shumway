using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Compiler.Wam;

/// <summary>
/// Compiles a single Prolog clause to WAM bytecode. Current scope covers facts,
/// rules with non-trivial bodies, head args ranging over atoms / integers /
/// variables / anonymous / compounds (including lists), and the conjunctive
/// body operator <c>,/2</c>. Disjunction, cut and other control constructs
/// follow in later chunks (8d, 8e).
///
/// <para><b>Head compilation</b> is the two-pass BFS from 8b. Pass 1 handles
/// top-level head arguments, deferring compounds; pass 2 drains the worklist,
/// emitting <c>get_structure</c> / <c>get_list</c> + <c>unify_*</c>. The pass-1
/// dispatcher routes permanent variables (see below) through
/// <c>get_variable_y</c> / <c>get_value_y</c> instead of leaving them in X.</para>
///
/// <para><b>Body compilation</b> uses chunk analysis to classify variables:</para>
/// <list type="bullet">
/// <item>The clause's body is split into chunks at every <c>call</c>. Head + the
///   first body goal share chunk 0; each subsequent goal is its own chunk.</item>
/// <item>A variable that appears in two or more chunks must survive a call and
///   is allocated a <b>permanent</b> Y slot. Variables confined to one chunk
///   stay in X (temporary).</item>
/// </list>
///
/// <para>Each body goal compiles to argument-prep instructions followed by a
/// <c>call</c>. For temp vars: <c>put_variable_x</c> on first occurrence,
/// <c>put_value_x</c> after. For permanents: <c>put_variable_y</c> on first
/// occurrence (initializes Y[i] to a fresh unbound and writes a REF to X[arg]),
/// <c>put_value_y</c> after (copies Y[i] into X[arg]). Atoms / integers / nil
/// use the obvious <c>put_*</c> instructions; compound args use
/// <c>put_structure</c> / <c>put_list</c> + the same <c>unify_*</c> family,
/// with nested compounds going through the BFS worklist as in the head.</para>
///
/// <para><b>Allocate / deallocate</b> wrap multi-chunk bodies. The clause starts
/// with <c>allocate N</c> (N = number of permanents); the last goal's
/// argument-prep is followed by <c>deallocate; execute target</c>. Single-chunk
/// bodies don't need a frame and the only goal is a tail call
/// (<c>put-args; execute target</c>).</para>
///
/// <para><b>Last call optimization (LCO)</b>: the last body goal always uses
/// <c>execute</c> instead of <c>call</c>, so the engine doesn't push a return
/// frame just to come right back. The <c>deallocate</c> precedes the
/// <c>execute</c> when a frame is active, freeing the Y slots before transfer.</para>
///
/// <para>Inter-clause references are emitted with the target operand set to 0.
/// Each <c>call</c> / <c>execute</c> is recorded in
/// <see cref="CompiledClause.CallSites"/> so the linker (the test harness for
/// now; a real linker comes with the bundler) can patch the operand once all
/// clauses' addresses are known.</para>
/// </summary>
public sealed class ClauseCompiler
{
    public CompiledClause Compile(Clause clause)
    {
        ArgumentNullException.ThrowIfNull(clause);

        switch (clause.Kind)
        {
            case ClauseKind.Fact:
                return CompileClauseTerm(clause.Term, bodyTerm: null);
            case ClauseKind.Rule:
                CompoundTerm rule = (CompoundTerm)clause.Term;
                Term head = rule.Args[0];
                Term body = rule.Args[1];
                if (body is AtomTerm { Name: "true" })
                    return CompileClauseTerm(head, bodyTerm: null);
                return CompileClauseTerm(head, body);
            case ClauseKind.Directive:
                throw new NotSupportedException(
                    "Directives are handled by ClauseReader, not by the clause compiler.");
            case ClauseKind.DcgRule:
                throw new NotSupportedException(
                    "DCG rules require a separate translation pass — not yet implemented.");
            default:
                throw new InvalidOperationException($"Unknown clause kind: {clause.Kind}.");
        }
    }

    private CompiledClause CompileClauseTerm(Term headTerm, Term? bodyTerm)
    {
        (string name, Term[] headArgs) = DecomposeHead(headTerm);
        List<Term> goals = bodyTerm is null ? new List<Term>() : FlattenConjunction(bodyTerm);

        // For each named (non-anonymous) variable, record which chunk indices it
        // appears in. Chunk 0 = head + first goal; chunk i >= 1 = goal i.
        var permanents = ClassifyPermanents(headArgs, goals);
        var state = new CompileState(headArgs.Length, permanents);

        // An environment frame is required whenever the body has more than one
        // goal — even without permanent variables — because the first call's
        // saved CP would otherwise clobber the original return address.
        // Single-goal bodies can tail-call directly (no frame, no allocate).
        bool needFrame = goals.Count > 1;
        if (needFrame)
            state.Emitter.EmitAllocate(state.PermanentCount);

        // ----- Head -----
        for (int i = 0; i < headArgs.Length; i++)
            CompileHeadArg(state, headArgs[i], i);
        DrainPendingCompounds(state);

        // ----- Body goals -----
        if (goals.Count == 0)
        {
            // Pure fact / trivial-body rule.
            state.Emitter.EmitProceed();
        }
        else
        {
            for (int i = 0; i < goals.Count; i++)
            {
                bool isLast = i == goals.Count - 1;
                CompileBodyGoal(state, goals[i], isLast, needFrame);
            }
        }

        int functorId = InternFunctor(name, headArgs.Length);
        return new CompiledClause(
            state.Emitter.ToBytes(),
            functorId,
            headArgs.Length,
            state.Xs.RegisterCount,
            state.PermanentCount,
            state.CallSites);
    }

    // ============================================================================
    // Chunk classification
    // ============================================================================

    /// <summary>Walks the head and body, collecting the set of chunks each named
    /// variable appears in. Returns the names that appear in at least two
    /// chunks — those need permanent (Y) storage to survive an intervening call.
    /// The result is deterministic (insertion-ordered) so Y slot indices are
    /// stable for given source.</summary>
    private static List<string> ClassifyPermanents(Term[] headArgs, List<Term> goals)
    {
        var occurs = new Dictionary<string, HashSet<int>>();
        var order = new List<string>();

        void Visit(Term t, int chunk)
        {
            switch (t)
            {
                case VarTerm v when v.Name != "_":
                    if (!occurs.TryGetValue(v.Name, out var s))
                    {
                        occurs[v.Name] = s = new HashSet<int>();
                        order.Add(v.Name);
                    }
                    s.Add(chunk);
                    break;
                case CompoundTerm c:
                    foreach (Term arg in c.Args) Visit(arg, chunk);
                    break;
            }
        }

        // Head is in chunk 0.
        foreach (Term arg in headArgs) Visit(arg, 0);
        // Goal i is in chunk i — the first goal joins the head; later goals each
        // start a new chunk after the previous call.
        for (int i = 0; i < goals.Count; i++) Visit(goals[i], i);

        var perms = new List<string>();
        foreach (string name in order)
            if (occurs[name].Count >= 2)
                perms.Add(name);
        return perms;
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

    private void CompileHeadArg(CompileState s, Term arg, int argSlot)
    {
        switch (arg)
        {
            case AtomTerm a:
                s.Emitter.EmitGetAtom(InternAtom(a.Name), argSlot);
                break;

            case IntTerm n:
                CheckInt32(n);
                s.Emitter.EmitGetInteger((int)n.Value, argSlot);
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

            case FloatTerm:
                throw new NotSupportedException(
                    "Float head arguments are not yet supported.");
            case StringTerm:
                throw new NotSupportedException(
                    "String head arguments are not yet supported.");
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
            if (isList)
                s.Emitter.EmitGetList(slot);
            else
                s.Emitter.EmitGetStructure(InternFunctor(comp.Functor, comp.Args.Length), slot);

            foreach (Term sub in comp.Args)
                CompileUnifyArg(s, sub);
        }
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
                CheckInt32(n);
                s.Emitter.EmitUnifyInteger((int)n.Value);
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
                int xFresh = s.Xs.AllocateFresh(v.Name);
                s.Emitter.EmitUnifyVariableX(xFresh);
                break;

            case VarTerm v:
                s.Emitter.EmitUnifyValueX(s.Xs.GetSlot(v.Name));
                break;

            case CompoundTerm c:
                int temp = s.Xs.AllocateAnonymousSlot();
                s.Emitter.EmitUnifyVariableX(temp);
                s.Pending.Enqueue((temp, c));
                break;

            case FloatTerm:
                throw new NotSupportedException(
                    "Float arguments inside compound terms are not yet supported.");
            case StringTerm:
                throw new NotSupportedException(
                    "String arguments inside compound terms are not yet supported.");
            default:
                throw new NotSupportedException(
                    $"Unsupported sub-argument type {arg.GetType().Name}.");
        }
    }

    // ============================================================================
    // Body compilation
    // ============================================================================

    private void CompileBodyGoal(CompileState s, Term goal, bool isLast, bool hasFrame)
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
            default:
                throw new NotSupportedException(
                    $"Goal type {goal.GetType().Name} is not yet supported in clause bodies.");
        }

        // Emit argument-prep for each goal arg.
        for (int i = 0; i < gArgs.Length; i++)
            CompileBodyArg(s, gArgs[i], i);
        DrainPendingCompounds(s);

        int functorId = InternFunctor(fName, gArgs.Length);
        if (isLast)
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
            // num_live_perms — passed informationally so the env trimming pass
            // (a future optimisation) can shrink the frame. For now we always
            // pass the full count; the interpreter ignores it.
            s.Emitter.EmitCall(targetAddress: 0, numLivePermanents: s.PermanentCount);
            s.CallSites.Add(new CallSite(callPos, functorId, IsExecute: false));
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
                CheckInt32(n);
                s.Emitter.EmitPutInteger((int)n.Value, argSlot);
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
                bool isList = c.Functor == "." && c.Args.Length == 2;
                if (isList)
                    s.Emitter.EmitPutList(argSlot);
                else
                    s.Emitter.EmitPutStructure(InternFunctor(c.Functor, c.Args.Length), argSlot);
                // Sub-args run in write mode; the same CompileUnifyArg dispatcher
                // handles them. Nested compounds are deferred onto the pending
                // queue and drained by DrainPendingCompounds.
                foreach (Term sub in c.Args)
                    CompileUnifyArg(s, sub);
                break;

            case FloatTerm:
                throw new NotSupportedException(
                    "Float body arguments are not yet supported.");
            case StringTerm:
                throw new NotSupportedException(
                    "String body arguments are not yet supported.");
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

    private static void CheckInt32(IntTerm n)
    {
        if (n.Value < int.MinValue || n.Value > int.MaxValue)
            throw new NotSupportedException(
                $"Integer literal {n.Value} doesn't fit in a 32-bit operand. "
                + "BigInt support lands later.");
    }

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
        public int PermanentCount { get; }
        public Queue<(int Slot, CompoundTerm Compound)> Pending { get; } = new();
        public List<CallSite> CallSites { get; } = new();

        public CompileState(int arity, IReadOnlyList<string> permanents)
        {
            Xs = new VariableMap(arity);
            for (int i = 0; i < permanents.Count; i++)
                Ys[permanents[i]] = i;
            PermanentCount = permanents.Count;
        }
    }
}
