using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

public static partial class MetaBuiltins
{
    // ============================================================================
    // Arity-Prolog recorded database.
    // See RecordedDatabase.cs for the storage layer.
    // ============================================================================

    public static bool Recorda3(Activation engine) => RecordImpl(engine, atFront: true);
    public static bool Recordz3(Activation engine) => RecordImpl(engine, atFront: false);

    private static bool RecordImpl(Activation engine, bool atFront)
    {
        PrologEngine host = RequireHost(engine, atFront ? "recorda/3" : "recordz/3");
        var (atomId, keyTerm) = ReadRecordedKey(engine, 0, atFront ? "recorda/3" : "recordz/3");
        // Atom key: materialise the AtomTerm once, here on the (cooler) write
        // path, so the entry carries a Key for introspection (keys/1, KeyOf).
        keyTerm ??= MaterializeRegister(engine, 0);
        Term term = MaterializeRegister(engine, 1);
        long @ref = atFront
            ? host.Records.Recorda(atomId, keyTerm, term)
            : host.Records.Recordz(atomId, keyTerm, term);
        return engine.UnifyRegisterWithCell(2, Cell.Int(@ref));
    }

    // recorded/3 is written around the retract lessons: the naive
    // shape ToList-copied the key's WHOLE chain into
    // (Ref, Term) tuples per call and materialised + unified EVERY candidate
    // through a full CP cycle. The classic Edinburgh drain
    // (`recorded(K, V, R), erase(R), …, !` once per item — the PrologToC
    // assembler) made that O(n²) tuples over tens of thousands of records:
    // dotnet-trace showed the enumerator's MoveNext alone at 38% exclusive
    // plus the induced finalizer/GC storm. Now:
    //   1. LAZY-FIRST — scan the LIVE chain and yield the first match with no
    //      snapshot at all; the remaining entries are snapshotted only if a
    //      RESUME actually happens (a cut after the first solution — the
    //      drain — never pays it, making the drain O(1) per call).
    //   2. PREFILTER — a candidate that DefiniteMismatch proves incompatible
    //      with the V pattern is skipped with zero allocation; a non-refuted
    //      candidate pays a rolled-back trial unify, and only the ACCEPTED
    //      one is materialised for real. An unbound V (the drain) skips the
    //      trial entirely.
    // Semantics note: mutations between the first solution and the first
    // resume are visible to the continuation (the lazy snapshot reads the
    // live chain then) — the drain idiom RELIES on seeing its own erasures;
    // the old eager snapshot hid them until the next fresh call.
    public static bool Recorded3(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "recorded/3");
        var (atomId, keyTerm) = ReadRecordedKey(engine, 0, "recorded/3");
        int returnPc = engine.BuiltinReturnPc;
        return new RecordedCursor(host, atomId, keyTerm, returnPc).Start(engine);
    }

    private sealed class RecordedCursor
    {
        private readonly PrologEngine _host;
        private readonly int _atomId;   // >= 0 => atom key (integer store); -1 => compound
        private readonly Term? _key;    // set only for compound keys
        private readonly int _returnPc;
        // The key's chain, looked up ONCE (lazily) and cached for the cursor's
        // whole backtracking life — no repeated structural dictionary probe.
        private LinkedList<RecordedDatabase.RecordEntry>? _chain;
        private bool _chainResolved;
        // Built on the FIRST resume (lazy tail snapshot); null before that.
        private List<(long Ref, Term Term)>? _snapshot;
        private int _snapIdx;
        private long _lastYieldedRef = -1;
        public readonly Func<Activation, int, bool> Resume;

        public RecordedCursor(PrologEngine host, int atomId, Term? key, int returnPc)
        {
            _host = host;
            _atomId = atomId;
            _key = key;
            _returnPc = returnPc;
            Resume = (e, _) => Attempt(e, isResume: true);
        }

        private LinkedList<RecordedDatabase.RecordEntry>? Chain()
        {
            if (!_chainResolved)
            {
                _chain = _atomId >= 0
                    ? _host.Records.GetAtomChain(_atomId)
                    : _host.Records.GetChain(_key!);
                _chainResolved = true;
            }
            return _chain;
        }

        public bool Start(Activation engine) => Attempt(engine, isResume: false);

        private bool Attempt(Activation engine, bool isResume)
        {
            if (isResume && _snapshot is null)
            {
                // Lazy tail snapshot: exactly one entry has been yielded so
                // far. Capture the live chain AFTER it — or the whole current
                // chain when the consumer erased it (the drain): the entries
                // before the yielded one were already rejected against this
                // same (CP-restored) pattern, so re-offering them is at worst
                // a cheap re-rejection, never a wrong answer. Walk the node
                // spine directly (no iterator allocation).
                _snapshot = new List<(long, Term)>();
                var chain = Chain();
                if (chain is not null)
                {
                    var start = chain.First;
                    for (var n = chain.First; n is not null; n = n.Next)
                        if (n.Value.Ref == _lastYieldedRef) { start = n.Next; break; }
                    for (var n = start; n is not null; n = n.Next)
                        _snapshot.Add((n.Value.Ref, n.Value.Term));
                }
                _snapIdx = 0;
            }

            // The V pattern: park register 1's cell in a heap slot so the
            // prefilter/trial can walk it. An unbound V matches everything —
            // skip the trial and go straight to the real unify.
            int patSlot = engine.AllocateHeap(1);
            engine.SetHeap(patSlot, engine.GetRegister(1));
            int patDeref = engine.Deref(patSlot);
            Cell patCell = engine.GetHeap(patDeref);
            bool patIsVar = patCell.Tag == Tag.Ref || patCell.Tag == Tag.AttVar;

            if (_snapshot is null)
            {
                var chain = Chain();
                for (var n = chain?.First; n is not null; n = n.Next)
                {
                    var cand = n.Value;
                    if (!patIsVar && !TrialUnifies(engine, patSlot, cand.Term)) continue;
                    _lastYieldedRef = cand.Ref;
                    return YieldCandidate(engine, (cand.Ref, cand.Term), isResume);
                }
                return false;
            }

            while (_snapIdx < _snapshot.Count)
            {
                var cand = _snapshot[_snapIdx++];
                if (!patIsVar && !TrialUnifies(engine, patSlot, cand.Term)) continue;
                return YieldCandidate(engine, cand, isResume);
            }
            return false;
        }

        /// <summary>Prefilter + rolled-back trial unify of the V pattern
        /// against a stored term — the FindRetractMatch shape.</summary>
        private static bool TrialUnifies(Activation engine, int patSlot, Term stored)
        {
            if (DefiniteMismatch(engine, patSlot, stored, depth: 6)) return false;
            int savedHeapTop = engine.HeapTop;
            int savedBindingTrail = engine.BindingTrailTop;
            int savedExtraTrail = engine.ExtraTrailTop;
            int savedHb = engine.Hb;
            engine.SetHb(engine.HeapTop);
            Cell candCell = Materializer.MaterializeAsCell(engine, stored);
            int candSlot = engine.AllocateHeap(1);
            engine.SetHeap(candSlot, candCell);
            bool ok = engine.Unify(patSlot, candSlot);
            engine.UnwindTrails(savedBindingTrail, savedExtraTrail);
            engine.SetHeapTop(savedHeapTop);
            engine.SetHb(savedHb);
            return ok;
        }

        private bool YieldCandidate(
            Activation engine, (long Ref, Term Term) cand, bool isResume)
        {
            // Push the re-satisfaction CP FIRST so the real bindings roll
            // back cleanly on backtrack (arity 3 — the registers must be
            // restored for the next attempt's pattern; the CP-arity lesson).
            engine.PushBuiltinChoicePoint(Resume, arity: 3);
            Cell termCell = Materializer.MaterializeAsCell(engine, cand.Term);
            if (!engine.UnifyRegisterWithCell(1, termCell)) return false;
            if (!engine.UnifyRegisterWithCell(2, Cell.Int(cand.Ref))) return false;
            if (isResume) engine.ResumeAtReturnPc(_returnPc);
            return true;
        }
    }

    public static bool Erase1(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "erase/1");
        long @ref = RequireIntRef(engine, register: 0, builtin: "erase/1");
        return host.Records.Erase(@ref);
    }

    public static bool EraseAll1(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "eraseall/1");
        var (atomId, keyTerm) = ReadRecordedKey(engine, 0, "eraseall/1");
        if (atomId >= 0) host.Records.EraseAllAtom(atomId);
        else host.Records.EraseAll(keyTerm!);
        return true;
    }

    public static bool Instance2(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "instance/2");
        long @ref = RequireIntRef(engine, register: 0, builtin: "instance/2");
        Term? stored = host.Records.Instance(@ref);
        if (stored is null) return false;
        Cell c = Materializer.MaterializeAsCell(engine, stored);
        return engine.UnifyRegisterWithCell(1, c);
    }

    public static bool KeyCount2(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "key_count/2");
        var (atomId, keyTerm) = ReadRecordedKey(engine, 0, "key_count/2");
        int count = atomId >= 0
            ? host.Records.KeyCountAtom(atomId)
            : host.Records.KeyCount(keyTerm!);
        return engine.UnifyRegisterWithCell(1, Cell.Int(count));
    }

    public static bool Keys1(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "keys/1");
        Cell keyCell = MaterializeRegisterAsCell(engine, 0);
        if (keyCell.Tag != Tag.Ref && keyCell.Tag != Tag.AttVar)
        {
            // Ground (or partially bound): treat as membership test.
            if (keyCell.Tag == Tag.Atom)
                return host.Records.KeyCountAtom(keyCell.AsAtomId) > 0;
            Term k = MaterializeRegister(engine, 0);
            return host.Records.KeyCount(k) > 0;
        }
        // Unbound: enumerate every key on backtracking.
        var keys = host.Records.AllKeys().ToList();
        if (keys.Count == 0) return false;
        int returnPc = engine.BuiltinReturnPc;
        return IndexEnumCursor.Start(engine, keys.Count, 1, returnPc,  // arity 1 (keys/1)
            (e, i) => KeysUnify(e, keys, i));
    }

    private static bool KeysUnify(Activation engine, List<Term> keys, int index)
    {
        Cell c = Materializer.MaterializeAsCell(engine, keys[index]);
        return engine.UnifyRegisterWithCell(0, c);
    }

    public static bool Ref1(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "ref/1");
        Cell cell = MaterializeRegisterAsCell(engine, 0);
        return cell.Tag == Tag.Int && host.Records.ContainsRef(cell.AsInt);
    }

    public static bool Replace2(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "replace/2");
        long @ref = RequireIntRef(engine, register: 0, builtin: "replace/2");
        Term newTerm = MaterializeRegister(engine, 1);
        return host.Records.Replace(@ref, newTerm);
    }

    public static bool Nref2(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "nref/2");
        long @ref = RequireIntRef(engine, register: 0, builtin: "nref/2");
        long? next = host.Records.NextRef(@ref);
        if (next is null) return false;
        return engine.UnifyRegisterWithCell(1, Cell.Int(next.Value));
    }

    public static bool Pref2(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "pref/2");
        long @ref = RequireIntRef(engine, register: 0, builtin: "pref/2");
        long? prev = host.Records.PrevRef(@ref);
        if (prev is null) return false;
        return engine.UnifyRegisterWithCell(1, Cell.Int(prev.Value));
    }

    public static bool RecordAfter3(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "record_after/3");
        long @ref = RequireIntRef(engine, register: 0, builtin: "record_after/3");
        Term term = MaterializeRegister(engine, 1);
        long? newRef = host.Records.RecordAfter(@ref, term);
        if (newRef is null) return false;
        return engine.UnifyRegisterWithCell(2, Cell.Int(newRef.Value));
    }

    public static bool RecordBefore3(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "record_before/3");
        long @ref = RequireIntRef(engine, register: 0, builtin: "record_before/3");
        Term term = MaterializeRegister(engine, 1);
        long? newRef = host.Records.RecordBefore(@ref, term);
        if (newRef is null) return false;
        return engine.UnifyRegisterWithCell(2, Cell.Int(newRef.Value));
    }

    // ---- shared validation helpers ----

    private static PrologEngine RequireHost(Activation engine, string builtin)
        => engine.Host as PrologEngine
            ?? throw new InvalidOperationException(
                $"{builtin} requires the engine to be hosted by a PrologEngine.");

    /// <summary>Reads a recorded-DB key from register <paramref name="register"/>.
    /// For an ATOM key returns its integer id (AtomId >= 0) and a null Term —
    /// the hot read path (recorded/3) then keys the integer-indexed store with
    /// NO key-AST materialisation and no structural string hashing (the two
    /// costs the PrologToC self-compile profile was dominated by). For a ground
    /// COMPOUND key returns AtomId = -1 and the materialised, validated Term.
    /// A var / inner-var key raises <c>instantiation_error</c> — the recorded
    /// DB keys on structural equality, and a VarTerm compares by its generated
    /// name, so a non-ground key would store under a never-again-equal key and
    /// silently fail every lookup.</summary>
    private static (int AtomId, Term? Term) ReadRecordedKey(Activation engine, int register, string builtin)
    {
        Cell cell = MaterializeRegisterAsCell(engine, register);
        if (cell.Tag == Tag.Ref || cell.Tag == Tag.AttVar)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (cell.Tag == Tag.Atom)
            return (cell.AsAtomId, null);
        Term key = MaterializeRegister(engine, register);
        if (!IsDeepGround(key))
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        return (-1, key);
    }

    // Iterative deep-groundness walk (no recursion — keys can be long lists).
    private static bool IsDeepGround(Term t)
    {
        var stack = new Stack<Term>();
        stack.Push(t);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case VarTerm:
                    return false;
                case CompoundTerm ct:
                    foreach (var a in ct.Args) stack.Push(a);
                    break;
            }
        }
        return true;
    }

    private static long RequireIntRef(Activation engine, int register, string builtin)
    {
        Cell cell = MaterializeRegisterAsCell(engine, register);
        if (cell.Tag == Tag.Ref || cell.Tag == Tag.AttVar)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (cell.Tag != Tag.Int)
            throw new Shumway.Core.PrologRuntimeException("type_error", "db_reference");
        return cell.AsInt;
    }

}
