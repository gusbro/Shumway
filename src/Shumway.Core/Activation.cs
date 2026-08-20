using System.Numerics;

namespace Shumway.Core;

/// <summary>
/// The per-query WAM machine — the execution context for a single Prolog
/// computation: heap, stack, registers, trails, choice points, and the engine
/// bookkeeping (E/B/P/CP/HB) defined by the WAM. Born at every query setup and
/// alive exactly as long as its solution enumeration; the durable Prolog
/// instance (dynamic store, compiled code space, consult history) is the
/// embedding layer's <c>PrologEngine</c>.
///
/// Activations are single-threaded internally (no locks on hot paths) and
/// thread-agile (no <c>[ThreadStatic]</c> state) — see ADR-001. Several can
/// coexist in a process — even over one database — and share the global
/// <see cref="AtomTable"/> and <see cref="FunctorTable"/>.
///
/// The type spans several partial files: this one holds the storage substrate
/// (ADR-001/004/005) and capacity management; Activation.Frames.cs the
/// environment / choice-point / catch frames and cut; Activation.UnifyOps.cs
/// the register-level unify entry points and compound construction;
/// Activation.Terms.cs the value tables, <see cref="Deref"/>,
/// <see cref="Bind"/> (HB check, young-to-old rule) and the
/// <see cref="Unify"/> core; Activation.Tier1.cs the Tier-1 IL support
/// (functor address map, IL choice points, guard stacks); and
/// Engine.HeapGc.cs the ADR-016 heap collector.
/// </summary>
public sealed partial class Activation
{
    private readonly ActivationConfig _config;

    // ----- Heap -----
    private Cell[] _heap;
    private int _heapTop;
    private int _hb;

    // Monotonic count of cells reserved on the WAM heap over the engine's
    // lifetime. Backtracking rewinds _heapTop but never this counter — it
    // is a cumulative "cells ever allocated" tally, not the current high
    // watermark. Bumped once per allocation primitive (AllocateHeap /
    // AllocateHeapUnbound). Purpose: a *deterministic* benchmark metric
    // (independent of wall-clock noise) for changes that add or remove
    // heap allocations — e.g. the read-mode atomic-literal fast path in
    // UnifyHeapWithCell, whose whole effect is skipping one cell per
    // matched literal. Present in every build, so it cancels exactly when
    // comparing two builds. See the harness --alloc mode.
    private long _cellsAllocated;

    // ----- Stack (storage only in this phase; no frame operations yet) -----
    private Cell[] _stack;

    // ----- Registers -----
    private Cell[] _registers;

    // ----- Trails -----
    private int[] _bindingTrail;
    private int _bindingTrailTop;

    private ExtraTrailEntry[] _extraTrail;

    // ----- Per-engine auxiliary value tables (ADR-002) -----
    private readonly List<BigInteger> _bigIntTable = new();
    private readonly List<Rational> _rationalTable = new();
    private readonly List<string> _stringTable = new();
    private readonly List<object?> _foreignTable = new();

    /// <summary>The <c>prefer_rationals</c> prolog_flag snapshot for this
    /// activation (ADR-039): when true, <c>/</c> on two integers yields an
    /// exact rational. Set at query setup from the engine's flag; read on the
    /// arithmetic path. A plain field — activation state is serialized per
    /// thread, so no ThreadStatic (engines stay thread-agile).</summary>
    public bool PreferRationals { get; set; }

    // ----- Attributed-variable storage -----
    // Maps the heap home index of an attributed variable to its
    // attribute record — itself a map from a module's atom id to the
    // heap index of that module's attribute value. An ATTVAR cell's
    // payload is its home index (like a self-REF), which is also its
    // key here, so a bare ATTVAR cell is fully self-describing.
    // Backtracking reverts the ATTVAR cell to a plain REF (via the
    // ValueChange trail); the orphaned record is left in place and is
    // overwritten outright if the heap slot is later reused.
    private readonly Dictionary<int, Dictionary<int, int>> _attrTable = new();
    // Side log for AttrModify trail entries: each records (attvar home
    // index, module id, previous value heap index — or -1 when the
    // module was absent). ExtraTrailEntry.HeapIdx indexes into this list.
    private readonly List<(int Home, int Module, int OldValue)> _attrTrailLog = new();

    // ----- attributed-variable unify-hook wakeups -----
    // When an attributed variable is bound, one wakeup per attribute
    // module is queued here: (module atom id, heap index of that
    // module's attribute value, heap index of the term the variable
    // was bound to). The interpreter drains the queue at the next goal
    // boundary and runs verify_attributes/4 for each entry; a hook
    // failure fails the triggering unification. The queue is transient
    // — not trailed — because it is consumed before the next goal and
    // cleared outright on backtracking.
    private readonly List<(int Module, int AttrValueIdx, int OtherIdx)> _pendingWakeups = new();

    // catch/3 scopes, innermost last. Pushed by '$catch_begin', deactivated
    // by '$catch_end'; both operations are recorded on the extra trail
    // (TrailType.CatchFrame) so backtracking restores the stack. The throw
    // handler walks it from the top to find a matching catcher.
    private readonly List<CatchFrame> _catchFrames = new();

    // ----- pooled scratch for the embedding layer's term
    // walkers (Materializer — findall runs it once per solution). Cleared on
    // use rather than allocated per call. The depth counter guards
    // re-entrancy: only the OUTERMOST walk uses the pooled instance; a nested
    // walk (e.g. a findall nested inside another findall's collect) allocates
    // a fresh one. Engines are single-threaded internally, so no
    // synchronisation is needed.
    //
    // TermReader's own scratch (its work / result stacks and
    // cycle set) moved to a per-thread pool inside TermReader when its walk
    // was made fully iterative, so the former TermWalkScratchSet /
    // TermWalkDepth fields are gone.
    public Dictionary<string, int>? MaterializeScratchMap;
    public int MaterializeDepth;
    // pooled scratch for the heap-to-heap copy_term/2
    // (HeapTermCopy): the source-var→copy-var and source-struct→copy-cell
    // identity maps, cleared on use. Same clear-on-use + depth-guard discipline
    // as the walkers above.
    public Dictionary<int, Cell>? CopyVarScratch;
    public Dictionary<int, Cell>? CopyStructScratch;
    public int CopyTermDepth;
    // Pooled scratch for the findall solution snapshot
    // (FindallSnapshot): the working cell image and the var / struct identity
    // maps, cleared on use. Snapshots never nest (a findall's records run
    // sequentially, and a snapshot never re-enters snapshotting), so no depth
    // guard is needed. Only the detached ToArray() per solution allocates.
    public List<Cell>? FindallSnapCells;
    public Dictionary<int, int>? FindallSnapVarMap;
    public Dictionary<int, int>? FindallSnapStructMap;
    // pooled scratch for the iterative structural-compare walk
    // (==/2, \==/2). The work-stack holds cell pairs still to compare; the
    // visited set holds (aAddr,bAddr) structure-pairs already in progress so a
    // cyclic term terminates co-inductively instead of overflowing the C# stack
    // (an uncatchable crash). Cleared on use; the walk is self-contained (never
    // recurses back into AreStructurallyEqual) so no depth guard is needed.
    private List<Cell>? _structEqStack;
    private HashSet<long>? _structEqVisited;
    // the same, for the ordered standard-order comparison
    // (compare/3, @</2 …, sort/2, keysort/2). Public so the Builtins-assembly
    // StandardOrderComparator can pool them; a separate pair from the ==/2 one
    // so a comparator callback and an ==/2 can't alias, and the ordered walk
    // keeps its own step budget.
    public List<Cell>? CompareStack;
    public HashSet<long>? CompareVisited;

    private int _stackTop;
    private int _extraTrailTop;

    /// <summary>Output sink the I/O builtins (<c>write/1</c>, <c>nl/0</c>,
    /// <c>writeln/1</c>) write into. Defaults to <see cref="Console.Out"/>;
    /// embedding callers can swap in a <see cref="System.IO.StringWriter"/>
    /// or another sink for testing or for capturing program output.</summary>
    public System.IO.TextWriter Out { get; set; } = Console.Out;

    /// <summary>Opaque back-reference to the embedding-layer object that owns
    /// this engine (typically a <c>PrologEngine</c>). Activation itself doesn't
    /// touch the value — it's read by meta-builtins like <c>findall/3</c>
    /// that need to spawn a peer engine to run a sub-query. The Core layer
    /// stays free of any embedding-layer types by keeping this typed as
    /// <see cref="object"/>; callers downcast at the use site.</summary>
    public object? Host { get; set; }

    /// <summary>Operator-lookup view used by the renderer to decide whether
    /// a compound should print in operator form (<c>a + b</c>) or
    /// canonical form (<c>+(a, b)</c>). Set by the embedding layer; left
    /// <c>null</c> means "no operator-form rendering, always canonical".</summary>
    public IOperatorLookup? Operators { get; set; }

    /// <summary>The attached debug session (ADR-035), or <c>null</c> when the
    /// activation is not being debugged or traced — the overwhelmingly common
    /// case. The Tier-0 interpreter raises the four Prolog ports (call, exit,
    /// redo, fail) on it as it runs. When null, each port site costs one
    /// predicted-not-taken null test and nothing else, so a release run pays
    /// no measurable price for the seam.</summary>
    public IDebugSession? Debug { get; set; }

    /// <summary>ADR-035 — whether last-call optimisation is in effect for the
    /// <see cref="Opcode.DebugLastCall"/> sites this activation runs. True (the
    /// default) makes them behave exactly as the <c>deallocate; execute</c> pair
    /// they replaced; false keeps the caller's frame alive across its final
    /// goal, so every predicate has a real exit port and a real frame to read
    /// variables from — at the cost of a control stack that grows with the
    /// logical call depth.
    ///
    /// <para>Only code compiled under <c>compile_mode=debug</c> carries
    /// <c>debug_lastcall</c> at all, so this is inert for release code. It is
    /// read per dispatch rather than baked in, which is what lets a debugger
    /// flip it mid-session.</para></summary>
    public bool LastCallOptimisation { get; set; } = true;

    /// <summary>ADR-035 — for each program address whose opcode byte has been
    /// patched to <see cref="Opcode.Break"/>, the byte that was there. Owned by
    /// the debug service (which does the patching) and shared BY REFERENCE — which is
    /// what lets a breakpoint armed while this query is already running be decoded by
    /// it. Null only when there is no debug session at all; an empty table is not the
    /// same thing as no table, because the next port may fill it.</summary>
    public IReadOnlyDictionary<int, byte>? BreakpointOriginals { get; set; }

    /// <summary>ADR-035 — the opcode a <see cref="Opcode.Break"/> byte at
    /// <paramref name="pc"/> is standing in for. A Break with no table entry means
    /// the code and the breakpoint table have gone out of step, which would send
    /// the interpreter off a cliff — so it fails loudly instead. It should never happen:
    /// the debug service always un-patches the buffer this activation actually runs, so a
    /// removed breakpoint's Break byte is gone from the live buffer, not merely from a stale
    /// copy of the table.</summary>
    public byte BreakpointOriginalAt(int pc)
    {
        if (BreakpointOriginals is not null
            && BreakpointOriginals.TryGetValue(pc, out byte original))
            return original;
        throw new InvalidOperationException(
            $"break opcode at PC=0x{pc:X4} with no breakpoint recorded — "
            + "the code space and the breakpoint table are out of step.");
    }

    // ----- Capacity management -----

    private void EnsureHeapCapacity(int extra)
    {
        if (Profiler.Enabled)
        {
            int before = _heap.Length;
            GrowIfNeeded(ref _heap, _heapTop, extra, _config.MaxHeapSize, "heap");
            if (_heap.Length != before)
            {
                var (cps, floor) = DiagnoseCpFloor();
                System.Console.Error.WriteLine(
                    $"[heapgrow] cap={_heap.Length:N0} heapTop={_heapTop:N0} "
                    + $"cps={cps} bottomCpHeapTop={floor:N0} trappedAboveFloor={_heapTop - floor:N0}");
            }
            return;
        }
        GrowIfNeeded(ref _heap, _heapTop, extra, _config.MaxHeapSize, "heap");
    }

    /// <summary>Diagnostic — walks the choice-point chain and returns the
    /// count of live CPs and the saved <c>HeapTop</c> of the oldest
    /// (bottom-most) one. Backtracking can never reclaim heap below that
    /// floor without failing the whole query, so a low, stable floor while
    /// <see cref="HeapTop"/> balloons signals heap garbage pinned by a
    /// long-lived choice point.</summary>
    private (int Count, int BottomHeapTop) DiagnoseCpFloor()
    {
        int b = _b;
        int count = 0;
        int floor = _heapTop;
        while (b >= 0)
        {
            int arity = (int)_stack[b + CpArityOffset].Data;
            floor = (int)_stack[b + CpHeapTopOffset(arity)].Data;
            count++;
            int prevB = (int)_stack[b + CpBOffset(arity)].Data;
            if (prevB == b) break;
            b = prevB;
        }
        return (count, floor);
    }

    // each wrapper carries the fast capacity compare inline
    // (the AllocateHeap pattern at the top of this file) so the per-push
    // cost at every TrailBinding / PushChoicePoint / Allocate / extra-trail
    // write is one compare + predicted-not-taken branch. GrowIfNeeded's
    // while loop made it non-inlinable, so before this every capacity
    // check was a real call.
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void EnsureStackCapacity(int extra)
    {
        if (_stackTop + extra > _stack.Length)
            GrowIfNeeded(ref _stack, _stackTop, extra, _config.MaxStackSize, "stack");
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void EnsureBindingTrailCapacity(int extra)
    {
        if (_bindingTrailTop + extra > _bindingTrail.Length)
            GrowIfNeeded(ref _bindingTrail, _bindingTrailTop, extra, _config.MaxBindingTrailSize, "binding trail");
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void EnsureExtraTrailCapacity(int extra)
    {
        if (_extraTrailTop + extra > _extraTrail.Length)
            GrowIfNeeded(ref _extraTrail, _extraTrailTop, extra, _config.MaxExtraTrailSize, "extra trail");
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void GrowIfNeeded<T>(ref T[] buffer, int top, int extra, int maxSize, string name)
    {
        long required = (long)top + extra;
        if (required <= buffer.Length) return;

        long newSize = buffer.Length;
        while (newSize < required) newSize *= 2;
        if (maxSize > 0 && newSize > maxSize)
        {
            if (required > maxSize)
                throw new InvalidOperationException($"Activation {name} overflow: would need {required} cells, max is {maxSize}.");
            newSize = maxSize;
        }
        if (newSize > int.MaxValue)
            throw new InvalidOperationException(
                $"Activation {name} overflow: would exceed int.MaxValue "
                + $"(top={top}, extra={extra}, len={buffer.Length}).");
        Profiler.Realloc(name, (long)newSize * System.Runtime.CompilerServices.Unsafe.SizeOf<T>());
        Array.Resize(ref buffer, (int)newSize);
    }

    // ----- Internal / test hooks -----

    /// <summary>Sets <see cref="Hb"/>, the heap-top boundary used by the
    /// young-to-old binding rule. Setting <c>Hb</c> equal to the current
    /// <see cref="HeapTop"/> makes every existing heap cell look "old", so any
    /// subsequent binding to an existing variable will be trailed — useful
    /// when a builtin performs a trial unification and needs the bindings to
    /// be reversible via <see cref="UnwindTrails"/>.</summary>
    public void SetHb(int hb)
    {
        // Pinned mode (TrailEverything): the value is irrelevant — everything trails
        // whatever Hb says — and a caller restoring a saved Hb of int.MaxValue must not
        // trip the range check.
        if (_trailEverything) { _hb = int.MaxValue; return; }
        if (hb < 0 || hb > _heapTop) throw new ArgumentOutOfRangeException(nameof(hb));
        _hb = hb;
    }

    /// <summary>Backwards-compatible alias for <see cref="SetHb"/>, retained
    /// for the test code that referenced it before <c>SetHb</c> became
    /// public.</summary>
    internal void SetHbForTesting(int hb) => SetHb(hb);

    /// <summary>Shrinks (or grows back) the heap-top to <paramref name="newTop"/>.
    /// Builtins that perform a trial allocation and want to release the
    /// heap range on rollback use this together with <see cref="UnwindTrails"/>.
    /// Growing past the current top is rejected — cells beyond the top are
    /// not initialised, and growing here would expose them.</summary>
    public void SetHeapTop(int newTop)
    {
        if (newTop < 0 || newTop > _heapTop)
            throw new ArgumentOutOfRangeException(nameof(newTop),
                $"newTop {newTop} must be in [0, {_heapTop}].");
        _heapTop = newTop;
    }

    /// <summary>Returns true if the two cells are structurally identical — same
    /// shape, same atom/integer values, same variable identities (an unbound
    /// REF is equal to another unbound REF only when they point at the same
    /// heap cell). Used by <c>==/2</c> and <c>\==/2</c>: unlike unification,
    /// this never binds anything.</summary>
    public bool AreStructurallyEqual(Cell a, Cell b)
    {
        // Resolve each cell: follow REFs to their dereference target, and
        // keep the dereferenced REF (a Cell.Ref pointing at the final heap
        // address) when the chain terminates at an unbound variable. This
        // lets two unbound vars compare equal iff they're the same heap cell.
        a = ResolveForStructuralCompare(a);
        b = ResolveForStructuralCompare(b);

        // A PSTR and a cons cell can denote the same list, so the tag test
        // cannot decide the pair — descend instead. (This is why `X = "abc",
        // Y = [97,98,99], X = Y` succeeded and `X == Y` then said false.)
        if (IsListLike(a) && IsListLike(b)) return StructuralCompareIterative(a, b);
        if (a.Tag != b.Tag) return false;
        switch (a.Tag)
        {
            // Leaves: compared inline, no descent. This is the common == case
            // (two vars, two atoms, two ints) and pays nothing for the
            // iterative machinery below.
            case Tag.Ref: return a.AsHeapIndex == b.AsHeapIndex;
            case Tag.Atom: return a.AsAtomId == b.AsAtomId;
            case Tag.Int: return a.AsInt == b.AsInt;
            case Tag.Float:
                return Cell.DecodeFloat(a, _heap[a.FloatPairedIndex])
                    == Cell.DecodeFloat(b, _heap[b.FloatPairedIndex]);
            case Tag.Functor: return a.AsFunctorId == b.AsFunctorId;
            // Foreign cells: identity via the underlying .NET
            // reference. Two foreign cells are == iff their boxed payloads are
            // reference-equal.
            case Tag.Foreign:
                return ReferenceEquals(
                    _foreignTable[a.AsForeignId], _foreignTable[b.AsForeignId]);
            case Tag.BigInt:
                return _bigIntTable[a.AsBigIntId].Equals(_bigIntTable[b.AsBigIntId]);
            case Tag.Rational:
                return _rationalTable[a.AsRationalId].Equals(_rationalTable[b.AsRationalId]);
            case Tag.String:
                return string.Equals(_stringTable[a.AsStringId], _stringTable[b.AsStringId]);
            // Compounds and PSTR descend: use an explicit work-stack (O(1) C#
            // stack, no per-element recursion) with a visited-pair set so a
            // cyclic / rational term (X=f(X), Y=f(Y), X==Y) terminates
            // co-inductively instead of overflowing the C# stack — an
            // uncatchable crash.
            case Tag.Str:
            case Tag.Lis:
            case Tag.Pstr:
                return StructuralCompareIterative(a, b);
            default:
                throw new NotSupportedException(
                    $"AreStructurallyEqual: tag {a.Tag} is not yet supported.");
        }
    }

    /// <summary>Beyond this many descent steps in a single comparison the walk
    /// assumes it may be inside a cycle and switches on the visited-pair set
    /// (see <see cref="StructuralCompareIterative"/>). Chosen well above any
    /// realistic acyclic term so the common case never allocates / probes the
    /// set — a comparison of two 65 000-node terms stays on the fast path — yet
    /// low enough that a cyclic term terminates in well under a millisecond.</summary>
    private const int StructEqCycleThreshold = 1 << 16;

    /// <summary>Iterative structural comparison of two compound / PSTR cells.
    /// Replaces the former mutually-recursive <c>AreStrStructurallyEqual</c> /
    /// <c>AreLisStructurallyEqual</c> descent, which used one C# frame per node
    /// and so overflowed the (guard-less) C# stack on a cyclic term — an
    /// uncatchable process crash. The explicit work-stack keeps
    /// C# stack use O(1) regardless of term depth.
    ///
    /// <para>Cycle handling is <em>lazy</em>: for the first
    /// <see cref="StructEqCycleThreshold"/> descent steps the walk runs without
    /// any bookkeeping — this is the hot path, and a proper acyclic term
    /// finishes here at the same cost as the old spine-loop code (leaves are
    /// compared inline; a list-of-primitives never touches the work-stack).
    /// Only if the step budget is exceeded — which an acyclic term of realistic
    /// size never does, but a cyclic/rational term does immediately — does it
    /// engage a visited set of <c>(aAddr,bAddr)</c> structure-pairs. From then
    /// on, re-encountering a pair already in progress means "equal so far", the
    /// greatest-fixpoint (co-inductive) reading of <c>==</c> that SWI-Prolog
    /// also gives, and the walk terminates. Both scratch collections are pooled
    /// on the engine and cleared on entry; the walk never re-enters
    /// <see cref="AreStructurallyEqual"/>, so no re-entrancy guard is
    /// needed.</para></summary>
    private bool StructuralCompareIterative(Cell topA, Cell topB)
    {
        List<Cell> stack = _structEqStack ??= new List<Cell>(64);
        stack.Clear();
        HashSet<long>? visited = null;   // engaged lazily past the step budget
        int steps = 0;
        // Each pending pair is two consecutive entries (a then b).
        stack.Add(topA); stack.Add(topB);
        while (stack.Count > 0)
        {
            Cell b = stack[stack.Count - 1];
            Cell a = stack[stack.Count - 2];
            stack.RemoveRange(stack.Count - 2, 2);
            a = ResolveForStructuralCompare(a);
            b = ResolveForStructuralCompare(b);

            if (TryCompareLeafPair(a, b, out bool leafEqual))
            {
                if (!leafEqual) return false;
                continue;
            }
            // a and b are same-tag compounds/PSTR (TryCompareLeafPair returns
            // false only when both share a descending tag). Count the descent
            // and, once over budget, start tracking visited structure-pairs.
            if (++steps == StructEqCycleThreshold)
                (visited = _structEqVisited ??= new HashSet<long>()).Clear();

            // Both PSTR: bulk-compare the packed runs and hand on the tails.
            // Strictly an optimisation over the element-wise spine below.
            if (a.Tag == Tag.Pstr && b.Tag == Tag.Pstr)
            {
                if (!ArePstrCodesEqual(a, b)) return false;
                stack.Add(PstrFinalTailCell(a));
                stack.Add(PstrFinalTailCell(b));
                continue;
            }
            switch (a.Tag)
            {
                case Tag.Str:
                {
                    int aIdx = a.AsHeapIndex, bIdx = b.AsHeapIndex;
                    if (visited != null && !visited.Add(((long)aIdx << 32) | (uint)bIdx))
                        break;   // cycle: equal so far
                    int aFid = _heap[aIdx].AsFunctorId, bFid = _heap[bIdx].AsFunctorId;
                    if (aFid != bFid) return false;
                    var (_, arity) = FunctorTable.Lookup(aFid);
                    for (int i = 1; i <= arity; i++)
                    {
                        stack.Add(_heap[aIdx + i]);
                        stack.Add(_heap[bIdx + i]);
                    }
                    break;
                }
                case Tag.Lis:
                case Tag.Pstr:
                {
                    // Walk the spine inline: compare each head-pair in place
                    // (only a compound head is pushed for descent), so a proper
                    // list of primitives runs at spine-loop speed with no
                    // work-stack traffic. Either side may be packed — the uncons
                    // hides which. Past the budget, every visited position pair
                    // is recorded so a cyclic spine (X=[1|X]) terminates.
                    Cell ca = a, cb = b;
                    while (true)
                    {
                        if (visited != null
                            && !visited.Add(((long)SpineKey(ca) << 32) | (uint)SpineKey(cb)))
                            break;   // cyclic spine — equal so far
                        TryUnconsListLike(ca, out Cell hac, out Cell tac);
                        TryUnconsListLike(cb, out Cell hbc, out Cell tbc);
                        Cell ha = ResolveForStructuralCompare(hac);
                        Cell hb = ResolveForStructuralCompare(hbc);
                        if (TryCompareLeafPair(ha, hb, out bool headEqual))
                        {
                            if (!headEqual) return false;
                        }
                        else { stack.Add(ha); stack.Add(hb); }   // compound head

                        Cell ta = ResolveForStructuralCompare(tac);
                        Cell tb = ResolveForStructuralCompare(tbc);
                        if (IsListLike(ta) && IsListLike(tb))
                        {
                            if (++steps == StructEqCycleThreshold)
                                (visited = _structEqVisited ??= new HashSet<long>()).Clear();
                            ca = ta; cb = tb;
                            continue;
                        }
                        stack.Add(ta); stack.Add(tb);   // final tail pair
                        break;
                    }
                    break;
                }
                default:
                    throw new NotSupportedException(
                        $"AreStructurallyEqual: tag {a.Tag} is not yet supported.");
            }
        }
        return true;
    }

    /// <summary>Compares two already-resolved cells when at least one side is a
    /// leaf. Returns <c>true</c> and sets <paramref name="equal"/> when the pair
    /// is fully decided here (mismatched tags, or both leaves); returns
    /// <c>false</c> when both are the same descending tag (Str / Lis / Pstr) and
    /// the caller must recurse into their contents.</summary>
    private bool TryCompareLeafPair(Cell a, Cell b, out bool equal)
    {
        // Both list-like (cons and/or PSTR): not decided here, descend.
        if (IsListLike(a) && IsListLike(b)) { equal = false; return false; }
        if (a.Tag != b.Tag) { equal = false; return true; }
        switch (a.Tag)
        {
            case Tag.Ref: equal = a.AsHeapIndex == b.AsHeapIndex; return true;
            case Tag.Atom: equal = a.AsAtomId == b.AsAtomId; return true;
            case Tag.Int: equal = a.AsInt == b.AsInt; return true;
            case Tag.Float:
                equal = Cell.DecodeFloat(a, _heap[a.FloatPairedIndex])
                     == Cell.DecodeFloat(b, _heap[b.FloatPairedIndex]);
                return true;
            case Tag.Functor: equal = a.AsFunctorId == b.AsFunctorId; return true;
            case Tag.Foreign:
                equal = ReferenceEquals(_foreignTable[a.AsForeignId],
                                        _foreignTable[b.AsForeignId]);
                return true;
            case Tag.BigInt:
                equal = _bigIntTable[a.AsBigIntId].Equals(_bigIntTable[b.AsBigIntId]);
                return true;
            case Tag.Rational:
                equal = _rationalTable[a.AsRationalId].Equals(_rationalTable[b.AsRationalId]);
                return true;
            case Tag.String:
                equal = string.Equals(_stringTable[a.AsStringId], _stringTable[b.AsStringId]);
                return true;
            default:
                // Str / Lis / Pstr — a descending tag; not decided here.
                equal = false;
                return false;
        }
    }

    /// <summary>Position key for the cycle-detection set. A cons cell is
    /// identified by its pair index; a PSTR slice by where in its buffer it
    /// starts, which advances monotonically, so a packed run cannot spin.</summary>
    private static int SpineKey(Cell c)
        => c.Tag == Tag.Lis
            ? c.AsHeapIndex
            : ~(c.AsPstrBufferIndex * Cell.PstrCodeUnitsPerBuffer + c.AsPstrOffset);

    private Cell ResolveForStructuralCompare(Cell c)
    {
        // An attributed variable is still a variable: it
        // normalizes to a REF at its home address — its payload already
        // *is* that address — so == compares it by identity, like any
        // unbound variable. This also handles a bare ATTVAR cell read
        // straight out of a structure-argument slot.
        if (c.Tag == Tag.AttVar) return Cell.Ref(c.AsHeapIndex);
        if (c.Tag == Tag.Pstr) return NormalizeEmptyPstr(c);
        if (c.Tag != Tag.Ref) return c;
        int addr = Deref(c.AsHeapIndex);
        Cell target = _heap[addr];
        if (target.Tag == Tag.Pstr) return NormalizeEmptyPstr(target);
        return target.Tag is Tag.Ref or Tag.AttVar ? Cell.Ref(addr) : target;
    }

    /// <summary>A zero-length PSTR carries no elements, so it IS its own tail —
    /// which is how <c>UnifyPstr</c> already treats it. Collapsing it here means
    /// every comparison, type test and ordering downstream sees the empty list
    /// as the atom <c>[]</c> (or as the variable it is open on), instead of as a
    /// third thing that happens to be tagged PSTR.</summary>
    private Cell NormalizeEmptyPstr(Cell c)
    {
        while (c.Tag == Tag.Pstr && c.AsPstrLength == 0)
        {
            int tailAddr = Deref(ComputePstrTailIndex(c));
            Cell tail = _heap[tailAddr];
            if (tail.Tag is Tag.Ref or Tag.AttVar) return Cell.Ref(tailAddr);
            c = tail;
        }
        return c;
    }

    /// <summary>Peels one element off a list-like cell — a cons cell or a
    /// non-empty PSTR. A PSTR is the list it represents, so both shapes have to
    /// answer the same question, and every walker that compares or orders lists
    /// goes through here rather than reading <c>_heap[idx]</c> / <c>[idx+1]</c>
    /// directly.</summary>
    public bool TryUnconsListLike(Cell c, out Cell head, out Cell tail)
    {
        if (c.Tag == Tag.Lis)
        {
            int idx = c.AsHeapIndex;
            head = _heap[idx];
            tail = _heap[idx + 1];
            return true;
        }
        if (c.Tag == Tag.Pstr && c.AsPstrLength > 0)
        {
            head = Cell.Int(GetPstrCodeUnit(c, 0));
            if (c.AsPstrLength == 1)
            {
                tail = _heap[Deref(ComputePstrTailIndex(c))];
                if (tail.Tag is Tag.Ref or Tag.AttVar)
                    tail = Cell.Ref(Deref(ComputePstrTailIndex(c)));
            }
            else
            {
                int absoluteStart = c.AsPstrOffset + 1;
                tail = Cell.Pstr(
                    c.AsPstrLength - 1,
                    c.AsPstrBufferIndex + absoluteStart / Cell.PstrCodeUnitsPerBuffer,
                    absoluteStart % Cell.PstrCodeUnitsPerBuffer);
            }
            return true;
        }
        head = default;
        tail = default;
        return false;
    }

    /// <summary>True when the cell denotes a non-empty list — a cons cell or a
    /// non-empty PSTR. The two are the same thing to every list operation.</summary>
    public static bool IsListLike(Cell c)
        => c.Tag == Tag.Lis || (c.Tag == Tag.Pstr && c.AsPstrLength > 0);

    /// <summary>Public form of <see cref="NormalizeEmptyPstr"/> for builtins
    /// that classify or order a cell: an empty PSTR is its tail.</summary>
    public Cell NormalizeListCell(Cell c) => c.Tag == Tag.Pstr ? NormalizeEmptyPstr(c) : c;

    /// <summary>Compares the leading code units of two PSTRs (<see cref="Tag.Pstr"/>)
    /// — the packed (possibly partial) char-code sequence, NOT the tail. A PSTR is
    /// a code sequence with a tail; <see cref="AppendPstrChain"/> walks the full Pstr
    /// chain (incl. lazy-concat continuation segments) and stops at the first
    /// non-Pstr tail. Two PSTRs are equal iff their materialized leading code units
    /// are equal AND their final tails are structurally equal; the caller
    /// (<see cref="StructuralCompareIterative"/>) compares the tails as an ordinary
    /// pending pair, so this returns only the code-sequence verdict. Cell-based —
    /// <see cref="AppendPstrChain"/> reads the cell, not a header index.</summary>
    private bool ArePstrCodesEqual(Cell a, Cell b)
    {
        var sbA = new System.Text.StringBuilder(a.AsPstrLength);
        var sbB = new System.Text.StringBuilder(b.AsPstrLength);
        AppendPstrChain(sbA, a);
        AppendPstrChain(sbB, b);
        if (sbA.Length != sbB.Length) return false;
        for (int i = 0; i < sbA.Length; i++)
            if (sbA[i] != sbB[i]) return false;
        return true;
    }

    /// <summary>The first non-<see cref="Tag.Pstr"/> tail cell of a PSTR chain
    /// (mirrors the tail-following loop in <see cref="AppendPstrChain"/>).</summary>
    private Cell PstrFinalTailCell(Cell header)
    {
        while (header.Tag == Tag.Pstr)
        {
            int tailIdx = ComputePstrTailIndex(header);
            Cell tail = _heap[tailIdx];
            if (tail.Tag == Tag.Ref)
                tail = _heap[Deref(tail.AsHeapIndex)];
            header = tail;
        }
        return header;
    }

    /// <summary>Sets <c>CP</c> directly. The interpreter uses this from the <c>call</c>
    /// instruction; tests use it to seed the engine state before running a fragment.
    /// public so persisted-IL assemblies (loaded into the
    /// process without InternalsVisibleTo) can call it from emitted IL.</summary>
    public void SetCp(int cp) => _cp = cp;

    // SHUMWAY_TRAP_PC=<hex> forensics: print the C# stack the moment P is set
    // to the trapped address. -1 (and JIT-eliminated checks) by default.
    public static readonly int TrapPc =
        System.Environment.GetEnvironmentVariable("SHUMWAY_TRAP_PC") is { } s
            ? System.Convert.ToInt32(s, 16) : -1;
    private void TrapPcHit(int pc)
        => System.Console.Error.WriteLine(
            $"[TRAP-PC] P set to 0x{pc:X} (from P=0x{_p:X}, gen={ProgramGeneration},"
            + $" srcByte={(CurrentProgram is { } cp && _p >= 0 && _p < cp.Length ? cp[_p] : -1):X2})"
            + $"\n{System.Environment.StackTrace}");

    /// <summary>Sets <c>PC</c> directly. Used by the interpreter for jumps
    /// (<c>execute</c>, <c>proceed</c>) and by Run for the initial entry point.
    /// public so persisted-IL assemblies (loaded into the
    /// process without InternalsVisibleTo) can call it from emitted IL.</summary>
    public void SetPc(int pc)
    {
        if (TrapPc >= 0 && pc == TrapPc) TrapPcHit(pc);
        _p = pc;
    }

    /// <summary>Advances <c>PC</c> by <paramref name="delta"/> bytes. Used by the
    /// interpreter to step past straight-line instructions.</summary>
    internal void AdvancePc(int delta)
    {
        _p += delta;
        if (TrapPc >= 0 && _p == TrapPc) TrapPcHit(_p);
    }

    /// <summary>the address a backtrackable builtin's CP
    /// resume should jump to after a successful retry. Set by the caller
    /// just before invoking <c>entry.Impl</c>:
    /// <list type="bullet">
    /// <item>Tier-0 sets it to the post-<c>call_builtin</c> address
    ///   (<c>pc + 9</c>) — the next bytecode instruction.</item>
    /// <item>Tier-1 IL sets it to a resume marker that the dispatcher
    ///   decodes back to the IL caller.</item>
    /// </list>
    /// Builtins that call <see cref="ResumeAtReturnPc"/> from inside a
    /// CP-resume delegate must capture this value at push time — don't
    /// derive it from <c>engine.P</c>: that only holds under Tier-0, where
    /// Pc happens to be the <c>call_builtin</c> opcode address; under
    /// Tier-1 Pc is stale and the resume lands mid-instruction.
    /// Public so persisted IL (loaded without InternalsVisibleTo) can
    /// set it from emitted code.</summary>
    public int BuiltinReturnPc { get; set; }

    /// <summary>Sets <c>B0</c> directly. The interpreter writes <c>_b</c> into this
    /// before any <c>call</c> or <c>execute</c> so the callee's <c>neck_cut</c> sees
    /// the right barrier. public so persisted-IL assemblies
    /// (loaded into the process without InternalsVisibleTo) can call it from
    /// emitted IL.</summary>
    public void SetB0(int b0) => _b0 = b0;

    /// <summary>ADR-035 D5+ (Set Next Statement) — restore <c>B</c> to a choice point
    /// recorded earlier, discarding every newer one, exactly as a backtrack into it would
    /// (minus taking the alternative). Only for the debugger's rewind, which has validated
    /// the target with <see cref="IsChoicePointInChain"/> first.</summary>
    public void SetB(int b) => _b = b;

    /// <summary>ADR-035 D5+ — the debugger moved the next-statement pointer DURING a stop.
    /// The interpreter's dispatch loop holds the stopped instruction in LOCALS (pc,
    /// opByte): without this flag it would execute that instruction anyway when the stop
    /// returns, clobbering the move. Every port-hook site checks it right after the hook
    /// and, when set, abandons the pending instruction and re-enters the loop at the
    /// redirected <c>P</c>. Set via <see cref="RedirectPc"/>, consumed via
    /// <see cref="TakeDebugPcRedirect"/>.</summary>
    public bool DebugPcRedirected { get; private set; }

    public void RedirectPc(int pc)
    {
        _p = pc;
        DebugPcRedirected = true;
    }

    public bool TakeDebugPcRedirect()
    {
        if (!DebugPcRedirected) return false;
        DebugPcRedirected = false;
        return true;
    }

    /// <summary>ADR-035 D5+ — restore <c>E</c> to an ancestor frame (the debugger's
    /// back-to-head rewind pops the current frame by returning to its caller's recorded
    /// goal). The frames above become dead stack, reclaimed by the next allocate.</summary>
    public void SetE(int e) => _e = e;

    /// <summary>ADR-035 D5+ — is <paramref name="b"/> still on the live choice-point
    /// chain (or the no-choice-point sentinel below it)? A rewind may only restore
    /// <c>B</c> to a choice point that still exists: one discarded by a cut since the
    /// mark was recorded is gone, and the rewind that would resurrect it is refused.</summary>
    public bool IsChoicePointInChain(int b)
    {
        int cursor = _b;
        while (cursor >= 0)
        {
            if (cursor == b) return true;
            int arity = (int)_stack[cursor + CpArityOffset].Data;
            int prev = (int)_stack[cursor + CpBOffset(arity)].Data;
            if (prev == cursor) return false;   // self-pointing corruption guard
            cursor = prev;
        }
        return b == cursor;   // the no-choice-points sentinel (-1) matches itself
    }

    /// <summary>Sets the write/read mode flag directly. The interpreter writes this
    /// from get_structure/put_structure/get_list/put_list. Exposed for tests that
    /// exercise <c>unify_*</c> opcodes without first running an open instruction.</summary>
    internal void SetWriteMode(bool writeMode) => _writeMode = writeMode;

    /// <summary>Sets the unify pointer directly. Same usage pattern as
    /// <see cref="SetWriteMode"/>.</summary>
    internal void SetUnifyPointer(int idx) => _unifyPointer = idx;

    internal ReadOnlySpan<int> BindingTrailSpan => _bindingTrail.AsSpan(0, _bindingTrailTop);
}

/// <summary>shared mutable holder for the host's dynamic-database
/// generation (the ADR-015 logical-update-view clock). The embedding layer
/// keeps ONE box per <c>PrologEngine</c>, increments <c>Value</c> wherever it
/// bumps the generation, and hands the same box to every <see cref="Activation"/>
/// it sets up — so the <c>enter_dynamic</c> opcode samples the generation with
/// a plain field read instead of invoking a <c>Func&lt;long&gt;</c> per
/// dynamic-predicate call. Single-writer (the host), read by the engine the
/// host is driving; engine access is serialized by the embedding contract.</summary>
public sealed class GenerationBox
{
    /// <summary>The current generation value.</summary>
    public long Value;
}
