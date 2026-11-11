using System.Collections.Immutable;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Interpreter;

namespace Shumway.Embedding;

/// <summary>
/// The dynamic-code patcher (extracted component): the per-buffer chain
/// tables, the live-engine registry, buffer-ownership / sync-after-mutation
/// logic, the corruption tripwire, and the in-place mutation of
/// extensible-indexed dynamic predicates (append / prepend / retract across
/// every chain level, sub-switch extension, free-chunk reuse). First-stage
/// extraction: still back-references the owning engine (E) for the linked
/// buffers, generation clock, flags, clause store and the incremental-assert
/// helpers — the seam narrows in a later pass.
/// </summary>
internal sealed class DynamicCodePatcher
{
    private readonly PrologEngine E;

    /// <summary>The table describing the host's current persistent buffer.</summary>
    public DynChainTable Chains => _dynChainTable;

    /// <summary>Replaces the chain table outright — the persistent buffer was
    /// rebuilt or invalidated, so every recorded position is stale. The old
    /// table lives on for the activations still on its buffer; with none,
    /// its code is dead and its literals go.</summary>
    public void ResetChains()
    {
        var old = _dynChainTable;
        old.Current = false;
        _dynChainTable = new DynChainTable();
        if (old.Attached == 0) ReleaseTableUnits(old);
    }

    /// <summary>Associates <paramref name="engine"/> with the current chain
    /// table — every in-place mutation that engine performs resolves chain
    /// state through this association.</summary>
    public void AssociateEngineWithCurrentChains(Activation engine)
    {
        if (engine.HostChainTable is DynChainTable prev)
        {
            if (ReferenceEquals(prev, _dynChainTable)) return;
            engine.HostChainTable = null;
            Detach(prev);
        }
        _dynChainTable.Attached++;
        engine.HostChainTable = _dynChainTable;
    }

    // ----- the literals and atoms a buffer's code names -----
    //
    // A code unit appended to a buffer (the dynamic region linked at setup, a
    // clause chunk, an assert helper, a shadow snapshot) adds a reference per
    // literal operand, and claims the atoms it names (ADR-003), when it lands,
    // and gives both back when it dies: a clause chunk when the chain reclaim
    // bypasses it, the rest with the buffer, which dies when the host has
    // replaced its table and no activation runs on it.

    // An activation leaves the buffer: the code dropped from it that only this
    // activation reached goes, or all of it with the buffer.
    private void Detach(DynChainTable table)
    {
        if (--table.Attached == 0 && !table.Current) ReleaseTableUnits(table);
        else ReleaseUnreached(table);
    }

    private void ReleaseTableUnits(DynChainTable table)
    {
        if (table.LiteralUnits is { } units)
        {
            foreach (var code in units) E._literalPools.Release(code);
            table.LiteralUnits = null;
        }
        if (table.LiteralChunks is { } chunks)
        {
            foreach (var code in chunks.Values) E._literalPools.Release(code);
            table.LiteralChunks = null;
        }
        if (table.AtomUnits is { } atomUnits)
        {
            foreach (var atoms in atomUnits) E.DropCode(atoms);
            table.AtomUnits = null;
        }
        if (table.AtomChunks is { } atomChunks)
        {
            foreach (var chunk in atomChunks.Values) E.DropCode(chunk.Atoms);
            table.AtomChunks = null;
        }
        if (table.Versions is { } versions)
        {
            foreach (var version in versions.Values)
            {
                if (version.Literals is { } code) E._literalPools.Release(code);
                E.DropCode(version.Atoms);
            }
            table.Versions = null;
        }
        if (table.Dropped is { } dropped)
        {
            foreach (var code in dropped) Release(table, code, recycle: false);
            table.Dropped = null;
        }
    }

    // ----- code dropped from a buffer -----
    //
    // A clause chunk the chain bypasses, or a version a later link replaces, has
    // no way in: the chain no longer offers it, and the old entry redirects. Only
    // an activation already running in it comes back to it, through a word of its
    // control state copied there (a continuation, a choice point's alternative, a
    // catch's recovery, the return of the builtin in progress). Once no activation
    // on the buffer holds one, nothing will run it again: its literals and its
    // atoms go, and a chunk's bytes are free for the next assert. Checked when code
    // is dropped and when an activation leaves the buffer.

    // The caller checks once after a batch: a pass walks every live
    // activation's stack.
    private static void Drop(DynChainTable table, DroppedCode code)
        => (table.Dropped ??= new()).Add(code);

    /// <summary>Releases the code dropped from <paramref name="table"/>'s buffer
    /// that no activation on it reaches.</summary>
    internal void ReleaseUnreached(DynChainTable table)
    {
        E.IlPromotion.ReleaseRetiredUnreached();
        if (table.Dropped is not { Count: > 0 } dropped) return;
        // A wasm module bakes pcs of the code it was compiled from and may return
        // into it; until the tier says which, dropped code stays.
        if (E.IlPromotion.Wasm is not null) return;
        dropped.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        int n = dropped.Count;
        if (_reachStarts.Length < n)
        {
            int size = System.Math.Max(n, _reachStarts.Length * 2);
            _reachStarts = new int[size];
            _reachEnds = new int[size];
            _reachFlags = new bool[size];
        }
        var starts = _reachStarts.AsSpan(0, n);
        var ends = _reachEnds.AsSpan(0, n);
        var reached = _reachFlags.AsSpan(0, n);
        reached.Clear();
        for (int i = 0; i < n; i++)
        {
            starts[i] = dropped[i].Start;
            ends[i] = dropped[i].End;
        }
        byte[]? program = null;
        for (int i = _liveEngines.Count - 1; i >= 0; i--)
            if (_liveEngines[i].TryGetTarget(out var engine)
                && ReferenceEquals(engine.HostChainTable, table))
            {
                engine.MarkReachedCode(starts, ends, reached);
                program ??= engine.CurrentProgram;
            }
        if (program is not null) ReachThroughCode(program, starts, ends, reached);
        int kept = 0;
        for (int i = 0; i < n; i++)
        {
            if (reached[i]) dropped[kept++] = dropped[i];
            else Release(table, dropped[i], recycle: true, program ?? E._persistentProgram);
        }
        dropped.RemoveRange(kept, n - kept);
    }

    // Dropped code an activation reaches reaches what it points into: a dead
    // chunk's <next> leads to the dead chunk after it, which a choice point
    // resuming into the first runs next. Followed to a fixpoint over the address
    // operands of the reached code.
    private static void ReachThroughCode(byte[] program, ReadOnlySpan<int> starts, ReadOnlySpan<int> ends,
        Span<bool> reached)
    {
        Stack<int>? work = null;
        for (int i = 0; i < reached.Length; i++)
            if (reached[i]) (work ??= new()).Push(i);
        while (work is { Count: > 0 })
        {
            int r = work.Pop();
            int end = System.Math.Min(ends[r], program.Length);
            for (int pc = starts[r]; pc < end;)
            {
                var info = Shumway.Core.OpcodeTable.Get(program[pc]);
                if (!info.IsDefined || info.Size == 0) break;
                if (info.OperandKinds is { } kinds)
                {
                    int at = pc + 1;
                    foreach (var kind in kinds)
                    {
                        if (kind == Shumway.Core.OperandKind.Address)
                        {
                            int target = Shumway.Core.BytecodeIO.ReadInt32(program, at);
                            int t = RangeAtOrBefore(starts, target);
                            if (t >= 0 && target < ends[t] && !reached[t])
                            {
                                reached[t] = true;
                                work.Push(t);
                            }
                        }
                        at += kind == Shumway.Core.OperandKind.LongValue ? 8 : 4;
                    }
                }
                pc += info.Size;
            }
        }
    }

    // The last range starting at or before target, -1 when none does.
    private static int RangeAtOrBefore(ReadOnlySpan<int> starts, int target)
    {
        int lo = 0, hi = starts.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (starts[mid] <= target) lo = mid + 1;
            else hi = mid - 1;
        }
        return hi;
    }

    // Reused across checks: a chain reclaim checks every few retracts.
    private int[] _reachStarts = System.Array.Empty<int>();
    private int[] _reachEnds = System.Array.Empty<int>();
    private bool[] _reachFlags = System.Array.Empty<bool>();

    // A breakpoint patched into recycled bytes would restore its old byte over
    // new code: with a debug session, the bytes stay.
    private void Release(DynChainTable table, in DroppedCode code, bool recycle, byte[]? program = null)
    {
        if (code.Literals is { } literals) E._literalPools.Release(literals);
        E.DropCode(code.Atoms);
        code.Forget?.Invoke();
        if (recycle && code.Recycle && E.DebugSession is null && !NoRecycle)
        {
            // Under the trace, freed bytes hold the invalid opcode: whatever
            // still runs them stops there, with the PC ring saying where from.
            if (TraceFree && program is not null && code.End <= program.Length)
                System.Array.Clear(program, code.Start, code.End - code.Start);
            FreeBytes(table.FreeChunks, code.Start, code.End - code.Start);
        }
    }

    // Triage of recycled code. SHUMWAY_FREE_TRACE=1 reports every free and
    // reuse of bytes with the frames that made it, a range freed twice with the
    // stack that frees it again, the free list's growth, and fills freed bytes
    // with the invalid opcode so that whatever still runs them stops there (the
    // PC ring, SHUMWAY_PC_RING=1, says where from). SHUMWAY_NO_RECYCLE=1 never
    // hands dropped bytes out again: a corruption that goes with it is one.
    private static readonly bool TraceFree = System.Environment.GetEnvironmentVariable("SHUMWAY_FREE_TRACE") == "1";
    private static readonly bool NoRecycle = System.Environment.GetEnvironmentVariable("SHUMWAY_NO_RECYCLE") == "1";

    /// <summary>Puts [<paramref name="addr"/>, + <paramref name="length"/>) on
    /// the free list, merged with the free ranges next to it: kept ascending, so
    /// the bytes of versions dropped together make one hole a whole link fits.</summary>
    internal static void FreeBytes(List<(int Addr, int Length)> freeChunks, int addr, int length)
    {
        if (TraceFree)
        {
            foreach (var (fa, fl) in freeChunks)
                if (addr < fa + fl && fa < addr + length)
                    System.Console.Error.WriteLine($"[FREE-OVERLAP] freeing {addr}+{length} overlaps free {fa}+{fl}\n{System.Environment.StackTrace}");
            System.Console.Error.WriteLine($"[FREE] 0x{addr:X}+{length}{TraceFrames()}");
        }
        int i = 0;
        while (i < freeChunks.Count && freeChunks[i].Addr < addr) i++;
        int end = addr + length;
        if (i < freeChunks.Count && freeChunks[i].Addr == end)
        {
            end += freeChunks[i].Length;
            freeChunks.RemoveAt(i);
        }
        if (i > 0 && freeChunks[i - 1].Addr + freeChunks[i - 1].Length == addr)
        {
            addr = freeChunks[i - 1].Addr;
            freeChunks.RemoveAt(--i);
        }
        freeChunks.Insert(i, (addr, end - addr));
        if (TraceFree && (freeChunks.Count & (freeChunks.Count - 1)) == 0 && freeChunks.Count >= 1024)
            System.Console.Error.WriteLine($"[FREE-LIST] {freeChunks.Count} fragments");
    }

    /// <summary>A code unit appended to <paramref name="table"/>'s buffer for
    /// the buffer's life, naming <paramref name="atoms"/>.</summary>
    internal void NoteCodeUnit(DynChainTable table, byte[] code, int[] atoms)
    {
        if (E._literalPools.AddRefs(code)) (table.LiteralUnits ??= new()).Add(code);
        if (atoms.Length == 0) return;
        E.RetainCode(atoms);
        (table.AtomUnits ??= new()).Add(atoms);
    }

    /// <summary>The predicates of a link laid into <paramref name="table"/>'s
    /// buffer whole (the static region at setup, a live link): each version claims
    /// its atoms while it is the one the buffer runs, and a live link's version
    /// its literals too (the static region's are pinned). A version a later link
    /// replaces is dropped, but for the trampoline its entry became.</summary>
    internal void NoteLinkedVersions(DynChainTable table, Shumway.Compiler.Wam.Linker.LinkResult link,
        bool literals = false, List<Shumway.Core.SwitchTable>? tables = null, int tableBase = 0,
        PrologEngine.LinkedProgram? host = null)
    {
        var versions = table.Versions ??= new();
        var preds = new List<KeyValuePair<int, Shumway.Compiler.Wam.CompiledPredicate>>(link.PredicatesByAddress);
        preds.Sort(static (a, b) => a.Key.CompareTo(b.Key));
        int tableAt = tableBase;
        foreach (var (addr, pred) in preds)
        {
            int[] atoms = Shumway.Compiler.Wam.CodeAtoms.Of(pred).Ids;
            E.RetainCode(atoms);
            byte[]? code = literals && E._literalPools.AddRefs(pred.Bytecode) ? pred.Bytecode : null;
            if (versions.TryGetValue(pred.FunctorId, out var old))
            {
                bool trampoline = table.StableEntries is { } stable
                    && stable.TryGetValue(pred.FunctorId, out int stub) && stub == old.Start;
                // The version's forward references, pending by position in the
                // buffer's list: patched once their callee links, they would write
                // into whatever the bytes hold by then.
                if (table.LiveConsultUnresolved is { Count: > 0 } pending)
                    pending.RemoveAll(site => site.AbsPos >= old.Start && site.AbsPos < old.End);
                Drop(table, new DroppedCode(trampoline ? old.Start + TrampolineSize : old.Start,
                    old.End, old.Literals, old.Atoms, recycle: true, Forget(old, trampoline)));
            }
            versions[pred.FunctorId] = new LinkedVersion(addr, addr + pred.Bytecode.Length, atoms, code,
                tables, tableAt, pred.SwitchTables.Count, host);
            tableAt += pred.SwitchTables.Count;
        }
        ReleaseUnreached(table);
    }

    // What a dropped version leaves to forget once nothing runs it: its switch
    // tables' slots, free for a later link, and its entry in the address map,
    // unless the entry is the trampoline, which names the version it leads to.
    private static Action? Forget(LinkedVersion old, bool trampoline)
    {
        if (old.Tables is null && (old.Host is null || trampoline)) return null;
        return () =>
        {
            if (old.Tables is { } list)
                for (int i = old.TableStart; i < old.TableStart + old.TableCount && i < list.Count; i++)
                    list[i] = null!;
            if (!trampoline) old.Host?.ForgetLinked(old.Start);
        };
    }

    // `execute <address>`: what a reloaded predicate's first entry becomes.
    private const int TrampolineSize = 5;

    /// <summary>A clause's chunk the buffer was built with: its atoms, released
    /// as an appended chunk's are.</summary>
    internal void NoteChunkAtoms(DynChainTable table, int chunkAddr, int end, int[] atoms)
    {
        if (atoms.Length == 0) return;
        E.RetainCode(atoms);
        (table.AtomChunks ??= new())[chunkAddr] = (end, atoms);
    }

    /// <summary>Code of the buffer that claims its atoms for the buffer's life.</summary>
    internal void NoteAtomUnit(DynChainTable table, int[] atoms)
    {
        if (atoms.Length == 0) return;
        E.RetainCode(atoms);
        (table.AtomUnits ??= new()).Add(atoms);
    }

    /// <summary>A clause's chunk of <paramref name="length"/> bytes at
    /// <paramref name="chunkAddr"/>, whose body is <paramref name="body"/>:
    /// released when the chain bypasses it and nothing runs it
    /// (<see cref="DropChunk"/>), else with the buffer.</summary>
    internal void NoteClauseChunk(DynChainTable table, int chunkAddr, int length, byte[] body, int[] atoms)
    {
        if (E._literalPools.AddRefs(body)) (table.LiteralChunks ??= new())[chunkAddr] = body;
        if (atoms.Length == 0) return;
        E.RetainCode(atoms);
        (table.AtomChunks ??= new())[chunkAddr] = (chunkAddr + length, atoms);
    }

    /// <summary>The chain bypasses the chunk of <paramref name="length"/> bytes at
    /// <paramref name="chunkAddr"/>. Its bytes are recycled once nothing runs it,
    /// unless <paramref name="recycle"/> is false: the chain's head, which the
    /// trampoline enters. Released by the <see cref="ReleaseUnreached"/> that
    /// follows the batch.</summary>
    internal void DropChunk(DynChainTable table, int chunkAddr, int length, bool recycle)
    {
        byte[]? body = null;
        if (table.LiteralChunks is { } chunks) chunks.Remove(chunkAddr, out body);
        int[] atoms = System.Array.Empty<int>();
        if (table.AtomChunks is { } atomChunks && atomChunks.Remove(chunkAddr, out var chunk))
            atoms = chunk.Atoms;
        Drop(table, new DroppedCode(chunkAddr, chunkAddr + length, body, atoms, recycle));
    }

    /// <summary>The code of an open query (its transient region), released
    /// when the query ends; kept on its activation.</summary>
    private static QueryUnits UnitsOf(Activation engine)
    {
        if (engine.HostQueryUnits is QueryUnits units) return units;
        var fresh = new QueryUnits();
        engine.HostQueryUnits = fresh;
        return fresh;
    }

    private sealed class QueryUnits
    {
        public readonly List<byte[]> Literals = new();
        public readonly List<int[]> Atoms = new();
    }

    internal void NoteQueryUnit(Activation engine, byte[] code, int[] atoms)
    {
        if (E._literalPools.AddRefs(code)) UnitsOf(engine).Literals.Add(code);
        if (atoms.Length == 0) return;
        E.RetainCode(atoms);
        UnitsOf(engine).Atoms.Add(atoms);
    }
    public DynamicCodePatcher(PrologEngine engine) => E = engine;

    // ====================================================================
    // runtime in-place extension of extensible-indexed
    // dynamic predicates.
    // ====================================================================

    /// <summary>returns <c>true</c> iff the predicate
    /// <paramref name="functorId"/>'s live dispatch is the
    /// extensible-indexed layout (<c>enter_dynamic</c> +
    /// <c>switch_on_term</c> + <c>try_me_else</c>-headed bucket
    /// chains). Walks the predicate's entry to distinguish:
    /// chains begin with <c>try_me_else</c> (patchable
    /// <c>&lt;next&gt;</c> operand) whereas contiguous
    /// indexed layout begins with <c>try</c>.</summary>
    /// <summary>walks through the multi-level cascade
    /// from the predicate's <c>switch_on_term</c> var label,
    /// descending through any number of <c>switch_on_arg</c> level
    /// switches, until it reaches the final chain head (the chain
    /// that enumerates every clause regardless of indexable args).
    /// Returns -1 if the layout doesn't match.</summary>
    internal int FindFinalVarChainHead(Activation engine, int predAddr)
        => FindFinalVarChainHead(engine.CurrentProgram, predAddr);

    internal static int FindFinalVarChainHead(byte[]? prog, int predAddr)
    {
        if (prog is null || predAddr + 18 > prog.Length) return -1;
        int p = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 2);
        while (p > 0 && p + 1 <= prog.Length)
        {
            var op = (Shumway.Core.Opcode)prog[p];
            if (op == Shumway.Core.Opcode.TryMeElse) return p;
            if (op == Shumway.Core.Opcode.RetryMeElse
                && p + 6 <= prog.Length
                && prog[p + 5] == (byte)Shumway.Core.Opcode.Nop) return p;
            if (op != Shumway.Core.Opcode.SwitchOnArg) return -1;
            if (p + 9 > prog.Length) return -1;
            p = Shumway.Core.BytecodeIO.ReadInt32(prog, p + 5);
        }
        return -1;
    }

    /// <summary>Records, for each clause of a predicate just laid out
    /// indexed, the died slot of its entry in the chain of every clause: the
    /// chain lists the clauses in store order, all alive at this point. Stops
    /// at the first entry that is not of the chain's shape; a clause left out
    /// takes the walk by index when retracted.</summary>
    internal static void CollectIndexedVarEntries(
        byte[] prog, int predAddr, IReadOnlyList<Clause> clauses, Dictionary<Clause, int> into)
    {
        into.Clear();
        int cur = FindFinalVarChainHead(prog, predAddr);
        for (int i = 0; i < clauses.Count && cur > 0 && cur + 27 <= prog.Length; i++)
        {
            if (prog[cur] != (byte)Shumway.Core.Opcode.TryMeElse
                && prog[cur] != (byte)Shumway.Core.Opcode.RetryMeElse) return;
            int header = ChainEntryHeaderSize(prog, cur);
            if (cur + header + 22 > prog.Length
                || prog[cur + header] != (byte)Shumway.Core.Opcode.CheckVisible
                || prog[cur + header + 17] != (byte)Shumway.Core.Opcode.Execute) return;
            into[clauses[i]] = cur + header + 9;
            cur = Shumway.Core.BytecodeIO.ReadInt32(prog, cur + 1);
        }
    }

    /// <summary>After an in-place assert added <paramref name="clause"/> to the
    /// indexed layout: the died slot of its entry in the chain of every
    /// clause, for the retract.</summary>
    private void NoteIndexedVarEntry(Activation engine, int functorId, Clause clause, int diedAddr)
    {
        if (diedAddr <= 0 || IndexedVarRecordOff) return;
        if (GetChainTable(engine) is { } table && table.Chains.TryGetValue(functorId, out var chain))
            chain.IndexedVarEntries[clause] = diedAddr;
    }

    /// <summary>Places <paramref name="bytes"/> in a free chunk of the buffer
    /// when one fits, else at its end; the address.</summary>
    private static int PlaceChunk(Activation engine, DynChainTable table, byte[] bytes)
    {
        int at = TryReuseFreeChunk(table.FreeChunks, bytes.Length);
        if (at < 0) return engine.AppendCode(bytes);
        System.Array.Copy(bytes, 0, engine.CurrentProgram!, at, bytes.Length);
        return at;
    }

    // A body chunk of the indexed layout placed in the buffer: its literals and
    // atoms are the chunk's (NoteClauseChunk), released with it when the sweep
    // finds no entry naming it.
    private void NoteIndexedBody(DynChainTable table, int functorId, int bodyAddr, int length,
        Shumway.Compiler.Wam.CompiledClause compiledClause)
    {
        NoteClauseChunk(table, bodyAddr, length, compiledClause.Bytecode,
            Shumway.Compiler.Wam.CodeAtoms.Of(compiledClause).Ids);
        if (table.Chains.TryGetValue(functorId, out var chain))
            (chain.IndexedBodies ??= new())[bodyAddr] = length;
    }

    // A chain entry of the indexed layout placed in the buffer, for the sweep
    // that unlinks it to drop.
    private static void NoteIndexedEntry(DynChainTable table, int functorId, int addr, int length)
    {
        if (table.Chains.TryGetValue(functorId, out var chain))
            (chain.IndexedEntries ??= new())[addr] = length;
    }

    internal bool IsExtensibleIndexedLayout(Activation engine, int functorId)
    {
        var addrMap = engine.CurrentFunctorAddresses;
        if (addrMap is null) return false;
        if (!addrMap.TryGetValue(functorId, out int predAddr)) return false;
        var prog = engine.CurrentProgram;
        if (prog is null || predAddr + 18 > prog.Length) return false;
        if (prog[predAddr] != (byte)Shumway.Core.Opcode.EnterDynamic) return false;
        if (prog[predAddr + 1] != (byte)Shumway.Core.Opcode.SwitchOnTerm) return false;
        // for single-arg the var label points at a
        // try_me_else chain head; for multi-arg it points at the
        // next level's switch_on_arg (or, after layers of cascade,
        // eventually at a chain head). Walk through switch_on_arg
        // nodes until we reach a try_me_else chain head; that's
        // the signature of every indexed layout.
        int varLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 2);
        while (varLbl > 0 && varLbl + 1 <= prog.Length)
        {
            var op = (Shumway.Core.Opcode)prog[varLbl];
            if (op == Shumway.Core.Opcode.TryMeElse) return true;
            if (op == Shumway.Core.Opcode.RetryMeElse
                && varLbl + 6 <= prog.Length
                && prog[varLbl + 5] == (byte)Shumway.Core.Opcode.Nop) return true;
            if (op != Shumway.Core.Opcode.SwitchOnArg) return false;
            // switch_on_arg's var label is at +5 (skip the opcode
            // byte and the 4-byte arg_idx).
            if (varLbl + 9 > prog.Length) return false;
            varLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, varLbl + 5);
        }
        return false;
    }

    /// <summary>walks the chain starting at
    /// <paramref name="chainHead"/>, following <c>&lt;next&gt;</c>
    /// operands until the entry whose <c>&lt;next&gt;</c> is the
    /// absolute <see cref="Activation.DynamicFailStubAddr"/>. Returns the
    /// absolute byte offset of that tail entry's <c>&lt;next&gt;</c>
    /// operand (where the assertz extension patches), or <c>-1</c>
    /// on any malformed chain.</summary>
    private static int WalkChainToTailNextOperand(
        byte[] prog, int chainHead, int failStubAddr)
    {
        int cur = chainHead;
        bool isHead = true;
        // A chain instruction is ≥5 bytes, so a well-formed chain has at
        // most prog.Length/5 distinct entries — more steps than that means
        // the <next> operands form a cycle (corrupted buffer). Returning -1
        // sends the caller to the rebuild fallback instead of spinning.
        int stepsLeft = prog.Length / 5 + 1;
        while (true)
        {
            if (cur < 0 || cur + 5 > prog.Length || --stepsLeft < 0) return -1;
            var op = (Shumway.Core.Opcode)prog[cur];
            if (isHead)
            {
                if (op != Shumway.Core.Opcode.TryMeElse) return -1;
            }
            else
            {
                if (op != Shumway.Core.Opcode.RetryMeElse) return -1;
            }
            int next = Shumway.Core.BytecodeIO.ReadInt32(prog, cur + 1);
            if (next == failStubAddr) return cur + 1;
            cur = next;
            isHead = false;
        }
    }

    /// <summary>returns <c>true</c> when the chain
    /// entry at <paramref name="entryAddr"/> sits in a 9-byte
    /// chain-instruction slot (the original try_me_else footprint,
    /// possibly demoted to retry_me_else + 4 nops by an asserta).
    /// In that case the entry's check_visible / execute live at the
    /// head offsets (+9 / +26); for a native non-head 5-byte slot
    /// they live at +5 / +22. The distinguisher is the byte at
    /// entry+5: <c>Nop</c> for a demoted-head slot, anything else
    /// (start of check_visible) for a native non-head.</summary>
    private static int ChainEntryHeaderSize(byte[] prog, int entryAddr)
    {
        // try_me_else (9-byte) head: opcode TryMeElse.
        if (entryAddr + 1 <= prog.Length
            && prog[entryAddr] == (byte)Shumway.Core.Opcode.TryMeElse)
            return 9;
        // retry_me_else: 5-byte native or 9-byte demoted-from-head.
        // Demoted has Nop at offset +5; native has CheckVisible.
        if (entryAddr + 6 <= prog.Length
            && prog[entryAddr + 5] == (byte)Shumway.Core.Opcode.Nop)
            return 9;
        return 5;
    }

    /// <summary>A clause's code placed at <paramref name="codeStart"/>: its
    /// address operands (<see cref="Shumway.Compiler.Wam.CompiledClause.DispatchSites"/>,
    /// relative to the clause; a <c>get_blit</c>'s skip is one) made absolute.</summary>
    internal static void RelocateDispatchSites(byte[] prog, Shumway.Compiler.Wam.CompiledClause clause, int codeStart)
    {
        foreach (int siteRel in clause.DispatchSites)
        {
            int pos = codeStart + siteRel;
            Shumway.Core.BytecodeIO.WriteInt32(prog, pos, codeStart + Shumway.Core.BytecodeIO.ReadInt32(prog, pos));
        }
    }

    /// <summary>given the predicate entry and the new
    /// clause's arg-0 classification, locate the bucket chain head
    /// the new clause should be appended to. Returns <c>-1</c> when
    /// no bucket exists (new key) or the arg is var (every bucket
    /// would need to extend). Both cases are deferred to the
    /// persistent-rebuild fallback by the caller.</summary>
    private static int FindBucketChainHead(
        Activation engine, int predAddr, Shumway.Compiler.Ast.Clause newClause)
    {
        var prog = engine.CurrentProgram!;
        // The switch_on_term sits at predAddr + 1; its operands at
        // +2 (var), +6 (const), +10 (list), +14 (struct).
        int constLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 6);
        int listLbl  = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 10);
        int structLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 14);
        int varLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 2);

        var kind = Arg0Of(newClause, out int key);
        if (kind == Arg0Kind.None) return -1;

        // Walk the const-label cascade for level 0 only. The cascade
        // is atom → integer → structure within one level; on multi-
        // arg the cascade's last default points at the
        // next LEVEL's switch_on_arg, which marks the level boundary
        // and stops the walk — arg-0's bucket is in level 0 only,
        // higher-level buckets are routed through different chain
        // extensions.
        int CascadeLookup(int startAddr, int key, Shumway.Core.Opcode wantedOpcode)
        {
            int p = startAddr;
            while (p > 0 && p + 5 <= prog.Length)
            {
                var op = (Shumway.Core.Opcode)prog[p];
                // SwitchOnArg marks the boundary between level 0 and
                // the next indexable level — stop here.
                if (op == Shumway.Core.Opcode.SwitchOnArg) return -1;
                if (op != Shumway.Core.Opcode.SwitchOnAtom
                    && op != Shumway.Core.Opcode.SwitchOnInteger
                    && op != Shumway.Core.Opcode.SwitchOnStructure) return -1;
                int tableId = Shumway.Core.BytecodeIO.ReadInt32(prog, p + 1);
                var table = engine.GetSwitchTable(tableId);
                if (table is null) return -1;
                if (op == wantedOpcode)
                {
                    int target = table.Lookup(key);
                    return target == table.DefaultAddress ? -1 : target;
                }
                p = table.DefaultAddress;
                if (p == varLbl) return -1;
            }
            return -1;
        }

        switch (kind)
        {
            case Arg0Kind.Atom:
                return CascadeLookup(constLbl, key, Shumway.Core.Opcode.SwitchOnAtom);
            case Arg0Kind.Int:
                return CascadeLookup(constLbl, key, Shumway.Core.Opcode.SwitchOnInteger);
            case Arg0Kind.List:
                return listLbl == varLbl ? -1 : listLbl;
            case Arg0Kind.Struct:
                // struct cascade: structLbl points at switch_on_structure.
                if (structLbl <= 0 || structLbl == varLbl) return -1;
                if (prog[structLbl] != (byte)Shumway.Core.Opcode.SwitchOnStructure) return -1;
                int sTableId = Shumway.Core.BytecodeIO.ReadInt32(prog, structLbl + 1);
                var sTable = engine.GetSwitchTable(sTableId);
                if (sTable is null) return -1;
                int sTarget = sTable.Lookup(key);
                return sTarget == sTable.DefaultAddress ? -1 : sTarget;
            default:
                return -1;
        }
    }

    /// <summary>In-place extension of an extensible-
    /// indexed dynamic predicate's chains for an <c>assertz</c>.
    /// Compiles the new clause body, appends it to the buffer, then
    /// either (155b) extends the bucket chain for the new clause's
    /// arg-0 key when that key has an existing bucket, or (155c)
    /// creates a brand-new bucket chain at the end of the buffer
    /// and adds the new (key → chain-head) entry to the appropriate
    /// sub-switch table when the key is new. The var-fallthrough
    /// chain is always extended.
    ///
    /// Returns <c>false</c> if the predicate isn't using the chunk-
    /// 155a layout, the new clause is var-arg-at-0 (would need to
    /// extend every bucket — a concern), or the new key
    /// needs a sub-switch that doesn't exist yet (e.g. first int
    /// assertz to an atom-only predicate). In any of those cases
    /// the caller falls back to rebuild.</summary>
    internal bool TryAppendToIndexedDynamic(
        Activation engine, int functorId, Shumway.Compiler.Ast.Clause newClause)
    {
        if (!IsExtensibleIndexedLayout(engine, functorId)) return false;
        // capture buffer ownership before any AppendCode.
        bool ownsHost = EngineOwnsHostBuffer(engine);
        var addrMap = engine.CurrentFunctorAddresses!;
        var prog = engine.CurrentProgram!;
        int predAddr = addrMap[functorId];
        int failStub = engine.DynamicFailStubAddr;
        if (failStub <= 0) return false;

        // The new clause's first argument decides the path: the same key,
        // a new key, or a variable that extends every chain.
        var arg0Kind = Arg0Of(newClause, out int arg0Key);
        if (arg0Kind == Arg0Kind.None) return false;
        bool isVarArg = arg0Kind == Arg0Kind.Var;

        // Plan which chain tails the new clause's body will be
        // linked into. var-arg-at-0 extends every
        // chain — var fallthrough, list bucket if present, and
        // every bucket chain reachable through the atom / integer /
        // structure sub-switch tables. Concrete arg-0 (chunks
        // 155b / 155c) extends just (var chain + the specific
        // bucket chain, creating it for new-key).
        int bucketChainHead = -1;
        bool isNewKey = false;
        // for multi-arg layouts, the var slot at
        // predAddr+2 points at the next level's switch_on_arg, not
        // at the final var chain. Walk through the level cascade to
        // reach the actual chain head.
        int varChainHead = FindFinalVarChainHead(engine, predAddr);
        if (varChainHead < 0) return false;
        var chainTailNexts = new List<int>();
        int newKeyValue = 0;
        int subSwitchTableId = -1;

        int varTailNext = WalkChainToTailNextOperand(prog, varChainHead, failStub);
        if (varTailNext < 0) return false;
        if (isVarArg)
        {
            // Collect every chain's tail-next operand. Includes the
            // var chain itself so we don't double-extend below.
            if (!CollectAllChainTailNextOperands(engine, predAddr, chainTailNexts))
                return false;
        }
        else
        {
            chainTailNexts.Add(varTailNext);
            bucketChainHead = FindBucketChainHead(engine, predAddr, newClause);
            isNewKey = bucketChainHead < 0;
            if (!isNewKey)
            {
                int bucketTailNext = WalkChainToTailNextOperand(prog, bucketChainHead, failStub);
                if (bucketTailNext < 0) return false;
                chainTailNexts.Add(bucketTailNext);
            }
            else
            {
                // Locate the sub-switch matching the new key's type;
                // bail to rebuild if the type's sub-switch doesn't
                // exist yet (would be a layout change).
                if (!TryLocateSubSwitchForArg(
                        engine, predAddr, arg0Kind, arg0Key, out newKeyValue, out subSwitchTableId))
                    return false;
            }
        }

        // Corruption tripwire — every tail slot we're about to patch must
        // lie inside this activation's believed content length. A slot at
        // or beyond ProgramLength means the chain in the shared buffer
        // extends past this activation's append position (a stale
        // ProgramLength): AppendCode would overwrite those live entries and
        // the tail patch would then write the new entry's own address into
        // its own <next> operand — the self-pointing retry_me_else cycle.
        // Rebuild from the store instead of writing the corruption.
        foreach (int tn in chainTailNexts)
            if (tn + sizeof(int) > engine.ProgramLength)
            {
                ChainCorruptionRecover(
                    "assertz-indexed", engine, functorId,
                    $"tail slot {tn} beyond content length {engine.ProgramLength}");
                return true;   // the rebuild absorbed the store (new clause included)
            }

        // Compile the new clause (transforms identical to the chain
        // path; shared helper with a fact fast path).
        var compiledClause = E.CompileRuntimeAssertClause(engine, functorId, newClause);
        if (compiledClause is null) return false;

        // Body chunk: [meta(dbg, 0)] + body bytes. Clause-source-position
        // index is irrelevant for runtime-asserted clauses; the dbg marker is
        // gated on the compile_mode flag (release omits it — the interpreter
        // never dispatches a no-op on entry).
        var bodyEmitter = new Shumway.Compiler.Wam.BytecodeEmitter();
        if (E.Flags.EmitDebugInfo) bodyEmitter.EmitMetaDbgInfo(0);
        int bodyContentLocalStart = bodyEmitter.Position;
        bodyEmitter.AppendBytes(compiledClause.Bytecode);
        byte[] bodyChunk = bodyEmitter.ToBytes();
        var chainTable = GetOrCreateChainTable(engine);
        int bodyAddr = PlaceChunk(engine, chainTable, bodyChunk);
        NoteIndexedBody(chainTable, functorId, bodyAddr, bodyChunk.Length, compiledClause);
        prog = engine.CurrentProgram!;

        // Patch call sites inside the body to absolute targets.
        foreach (var site in compiledClause.CallSites)
        {
            int operandPos = bodyAddr + bodyContentLocalStart + site.OpcodeOffset + 1;
            int target = addrMap.TryGetValue(site.CalleeFunctorId, out int addr)
                ? addr
                : Shumway.Core.CallTarget.ForUndefined(site.CalleeFunctorId);
            Shumway.Core.BytecodeIO.WriteInt32(prog, operandPos, target);
        }
        RelocateDispatchSites(prog, compiledClause, bodyAddr + bodyContentLocalStart);

        // Helper: append a non-head chain entry (retry_me_else
        // <fail_stub>; check_visible; execute <targetBody>).
        int AppendNonHeadEntry(int targetBody)
        {
            var em = new Shumway.Compiler.Wam.BytecodeEmitter();
            em.EmitRetryMeElse(failStub);
            em.EmitCheckVisible(born: E._dbGeneration.Value, died: long.MaxValue);
            em.EmitExecute(targetBody);
            byte[] entry = em.ToBytes();
            int at = PlaceChunk(engine, chainTable, entry);
            NoteIndexedEntry(chainTable, functorId, at, entry.Length);
            return at;
        }

        // For the new-key concrete case, the new bucket itself is
        // built (and added to the sub-switch table) before we walk
        // the chain-tail list — the new bucket isn't in
        // chainTailNexts because it didn't exist when we planned.
        if (!isVarArg && isNewKey)
        {
            // Build a fresh bucket chain containing every var-arg
            // clause's body (they match every concrete key) plus the
            // new clause's body, then add (new_key → new_chain_head)
            // to the sub-switch table.
            var varArgBodies = CollectVarArgBodies(engine, varChainHead, functorId, newClause, prepended: false);
            if (varArgBodies is null)
            {
                E.InvalidatePersistent();
                E.RebuildEngineFidChainView(engine, functorId);
                return true;   // the rebuild took the store, the new clause included
            }
            int newBucketHead = BuildAndAppendNewBucketChain(
                engine, functorId, failStub, headArity: FunctorTable.Lookup(functorId).Arity,
                varArgBodies, bodyAddr);
            prog = engine.CurrentProgram!;
            var oldTable = engine.GetSwitchTable(subSwitchTableId);
            if (oldTable is null) return false;
            var newTable = oldTable.WithAdditionalEntry(newKeyValue, newBucketHead);
            engine.ReplaceSwitchTable(subSwitchTableId, newTable);
            MirrorSwitchTableIntoDynamicLink(subSwitchTableId, newTable);
        }

        // For every chain in the plan, append a new chain entry
        // pointing at the new body and patch the chain's prior tail.
        // This covers (depending on path):
        //   - bucket + var (2 entries).
        //   - var only (the new bucket already includes
        //     the new clause as its tail).
        //   - every chain (var + list + every bucket
        //     across all sub-switches).
        int varEntryDied = -1;
        foreach (int tailNext in chainTailNexts)
        {
            int newEntry = AppendNonHeadEntry(bodyAddr);
            prog = engine.CurrentProgram!;
            if (tailNext == varTailNext)
                varEntryDied = newEntry + ChainEntryHeaderSize(prog, newEntry) + 9;
            // Tripwire: the new entry, in a hole or at the end, must not hold
            // the slot being patched: the write below would splice the chain
            // into itself. (The ProgramLength check above makes a stale append
            // position unreachable; this is the last line of defense.)
            if (tailNext >= newEntry && tailNext < newEntry + 27)
            {
                ChainCorruptionRecover(
                    "assertz-indexed-patch", engine, functorId,
                    $"new entry {newEntry} holds the tail slot {tailNext}");
                return true;
            }
            Shumway.Core.BytecodeIO.WriteInt32(prog, tailNext, newEntry);
        }

        NoteIndexedVarEntry(engine, functorId, newClause, varEntryDied);

        // The new chunks may have grown the persistent buffer (chunk
        // 151b) — keep PrologEngine's cached reference current (owner),
        // or mark the host buffer for rebuild (non-owner engine: its
        // in-place extension is invisible to the newer buffer).
        SyncOrInvalidateAfterMutation(engine, ownsHost);

        // Refresh interpreter pools — the clause may have interned new
        // literals (skipped when the pools didn't grow).
        E.RefreshLiteralPoolsIfChanged(engine);
        return true;
    }

    /// <summary>locates the sub-switch
    /// (<c>switch_on_atom</c> / <c>switch_on_integer</c> /
    /// <c>switch_on_structure</c>) that handles the new clause's
    /// arg-0 type and returns its table id and the key value for the
    /// new arg. Returns <c>false</c> if the predicate doesn't yet
    /// have a sub-switch of that type — adding one would be a layout
    /// change beyond scope.</summary>
    private static bool TryLocateSubSwitchForArg(
        Activation engine, int predAddr, Arg0Kind arg0Kind, int arg0Key,
        out int keyValue, out int tableId)
    {
        keyValue = arg0Key; tableId = -1;
        var prog = engine.CurrentProgram!;
        int constLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 6);
        int varLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 2);

        Shumway.Core.Opcode wantedOp;
        switch (arg0Kind)
        {
            case Arg0Kind.Atom: wantedOp = Shumway.Core.Opcode.SwitchOnAtom; break;
            case Arg0Kind.Int: wantedOp = Shumway.Core.Opcode.SwitchOnInteger; break;
            case Arg0Kind.Struct: wantedOp = Shumway.Core.Opcode.SwitchOnStructure; break;
            default:
                return false;  // list (handled by listLbl, not a sub-switch) or unsupported
        }

        // Walk the cascade from constLbl looking for the wanted
        // sub-switch opcode. Stop at SwitchOnArg — multi-
        // arg layouts put a level boundary there, and the new key's
        // sub-switch must live within level 0.
        int p = constLbl;
        while (p > 0 && p + 5 <= prog.Length && p != varLbl)
        {
            var op = (Shumway.Core.Opcode)prog[p];
            if (op == Shumway.Core.Opcode.SwitchOnArg) return false;
            if (op != Shumway.Core.Opcode.SwitchOnAtom
                && op != Shumway.Core.Opcode.SwitchOnInteger
                && op != Shumway.Core.Opcode.SwitchOnStructure)
                return false;
            if (op == wantedOp)
            {
                tableId = Shumway.Core.BytecodeIO.ReadInt32(prog, p + 1);
                return true;
            }
            int tid = Shumway.Core.BytecodeIO.ReadInt32(prog, p + 1);
            var table = engine.GetSwitchTable(tid);
            if (table is null) return false;
            p = table.DefaultAddress;
        }
        return false;
    }

    /// <summary>The bodies of the clauses whose first argument is a
    /// variable, in order: each goes into the chain of a key first seen now.
    /// The live entries of the chain of all clauses are the store's clauses
    /// in order, the new one apart: it is in the store already, last after an
    /// assertz and first after an asserta, and has no entry yet. A retracted
    /// clause's entry stays linked until the sweep and stands for no stored
    /// clause. Null when the two do not line up: the caller rebuilds the
    /// predicate from the store.</summary>
    private List<int>? CollectVarArgBodies(Activation engine, int varChainHead, int functorId,
        Shumway.Compiler.Ast.Clause newClause, bool prepended)
    {
        var result = new List<int>();
        if (!E._dynStore.TryGetClauses(functorId, out var clauses) || clauses.Count == 0)
            return null;
        if (!ReferenceEquals(clauses[prepended ? 0 : clauses.Count - 1], newClause)) return null;
        var prog = engine.CurrentProgram!;
        int failStub = engine.DynamicFailStubAddr;
        int cur = varChainHead;
        int idx = prepended ? 1 : 0, end = prepended ? clauses.Count : clauses.Count - 1;
        // Cycle guard — same bound as WalkChainToTailNextOperand: a
        // corrupted <next> cycle must terminate the walk, not hang it.
        int stepsLeft = prog.Length / 5 + 1;
        while (true)
        {
            if (cur < 0 || cur + 27 > prog.Length || --stepsLeft < 0) return null;
            int chainHeaderSize = ChainEntryHeaderSize(prog, cur);
            int execOpPos = cur + chainHeaderSize + 17;
            if (execOpPos + 5 > prog.Length) return null;
            if (prog[execOpPos] != (byte)Shumway.Core.Opcode.Execute) return null;
            if (Shumway.Core.BytecodeIO.ReadInt64(prog, cur + chainHeaderSize + 9) == long.MaxValue)
            {
                if (idx >= end) return null;
                if (IsVarArgAt0(clauses[idx]))
                    result.Add(Shumway.Core.BytecodeIO.ReadInt32(prog, execOpPos + 1));
                idx++;
            }
            int next = Shumway.Core.BytecodeIO.ReadInt32(prog, cur + 1);
            if (next == failStub) break;
            cur = next;
        }
        return idx == end ? result : null;
    }

    private static bool IsVarArgAt0(Shumway.Compiler.Ast.Clause c)
        => Arg0Of(c, out _) == Arg0Kind.Var;

    /// <summary>What the layout keys a clause's first argument by: the cases
    /// of the compiler's indexing (PredicateCompiler.ClassifyTerm), with
    /// <see cref="Other"/> for what it keys as a variable (a float, packed
    /// text, an integer past the operand) and <see cref="None"/> for a head
    /// without arguments.</summary>
    private enum Arg0Kind : byte { None, Var, Atom, Int, List, Struct, Other }

    /// <summary>The kind of a clause's first argument and its key: an atom id,
    /// an integer, a functor id. Read off the image of a fact kept as one
    /// (ADR-063), so the in-place paths never build its term; off the term
    /// otherwise.</summary>
    private static Arg0Kind Arg0Of(Shumway.Compiler.Ast.Clause c, out int key)
    {
        key = 0;
        if (c.HeadImage is { } image)
        {
            var cells = image.Cells;
            if (cells.Length < 2 || FunctorTable.Lookup(cells[0].AsFunctorId).Arity == 0)
                return Arg0Kind.None;
            Cell a = cells[1];
            switch (a.Tag)
            {
                case Tag.Atom:
                    key = a.AsAtomId;
                    return Arg0Kind.Atom;
                case Tag.Int when a.AsInt >= int.MinValue && a.AsInt <= int.MaxValue:
                    key = (int)a.AsInt;
                    return Arg0Kind.Int;
                case Tag.Lis:
                    return Arg0Kind.List;
                case Tag.Str:
                    key = cells[a.AsHeapIndex].AsFunctorId;
                    return Arg0Kind.Struct;
                case Tag.Ref:
                    // A variable is a reference to a reference (its own cell,
                    // or that of its first occurrence); a float or packed text
                    // is reached through a reference to its cells.
                    return cells[a.AsHeapIndex].Tag == Tag.Ref ? Arg0Kind.Var : Arg0Kind.Other;
                default:
                    return Arg0Kind.Other;
            }
        }
        Shumway.Compiler.Ast.Term head = c.Kind == Shumway.Compiler.Ast.ClauseKind.Rule
            ? ((Shumway.Compiler.Ast.CompoundTerm)c.Term).Args[0]
            : c.Term;
        if (head is not Shumway.Compiler.Ast.CompoundTerm hc || hc.Args.Length == 0)
            return Arg0Kind.None;
        switch (hc.Args[0])
        {
            case VarTerm:
                return Arg0Kind.Var;
            case AtomTerm atom:
                key = AtomTable.Intern(atom.Name).Id;
                return Arg0Kind.Atom;
            case IntTerm n when n.Value >= int.MinValue && n.Value <= int.MaxValue:
                key = (int)n.Value;
                return Arg0Kind.Int;
            case CompoundTerm { Functor: ".", Args.Length: 2 }:
                return Arg0Kind.List;
            case CompoundTerm cc:
                key = FunctorTable.Intern(AtomTable.Intern(cc.Functor).Id, cc.Args.Length);
                return Arg0Kind.Struct;
            default:
                return Arg0Kind.Other;
        }
    }

    /// <summary>recursively enumerates every chain head
    /// reachable from a switch address. Walks both single- and
    /// multi-level layouts: switch_on_term and switch_on_arg cascade
    /// through 4 child labels; switch_on_atom / _integer /
    /// _structure (and their _arg variants) recurse through every
    /// table value plus the default. Chain heads — try_me_else, or
    /// a demoted retry_me_else + Nop at +5 — are added to
    /// <paramref name="heads"/>. Recursion stops at fail-stub
    /// addresses or unrecognised opcodes.</summary>
    private void EnumerateChainHeadsRecursive(
        Activation engine, int addr, HashSet<int> heads, HashSet<int> visited)
    {
        if (addr <= 0) return;
        var prog = engine.CurrentProgram;
        if (prog is null || addr + 1 > prog.Length) return;
        if (!visited.Add(addr)) return;
        var op = (Shumway.Core.Opcode)prog[addr];
        switch (op)
        {
            case Shumway.Core.Opcode.SwitchOnTerm:
            {
                int varLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 1);
                int constLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 5);
                int listLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 9);
                int structLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 13);
                EnumerateChainHeadsRecursive(engine, varLbl, heads, visited);
                EnumerateChainHeadsRecursive(engine, constLbl, heads, visited);
                EnumerateChainHeadsRecursive(engine, listLbl, heads, visited);
                EnumerateChainHeadsRecursive(engine, structLbl, heads, visited);
                break;
            }
            case Shumway.Core.Opcode.SwitchOnArg:
            {
                int varLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 5);
                int constLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 9);
                int listLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 13);
                int structLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 17);
                EnumerateChainHeadsRecursive(engine, varLbl, heads, visited);
                EnumerateChainHeadsRecursive(engine, constLbl, heads, visited);
                EnumerateChainHeadsRecursive(engine, listLbl, heads, visited);
                EnumerateChainHeadsRecursive(engine, structLbl, heads, visited);
                break;
            }
            case Shumway.Core.Opcode.SwitchOnAtom:
            case Shumway.Core.Opcode.SwitchOnInteger:
            case Shumway.Core.Opcode.SwitchOnStructure:
            {
                int tid = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 1);
                var table = engine.GetSwitchTable(tid);
                if (table is null) break;
                foreach (int v in table.Values)
                    EnumerateChainHeadsRecursive(engine, v, heads, visited);
                EnumerateChainHeadsRecursive(engine, table.DefaultAddress, heads, visited);
                break;
            }
            case Shumway.Core.Opcode.SwitchOnAtomArg:
            case Shumway.Core.Opcode.SwitchOnIntegerArg:
            case Shumway.Core.Opcode.SwitchOnStructureArg:
            {
                // arg_idx at +1, table_id at +5.
                int tid = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 5);
                var table = engine.GetSwitchTable(tid);
                if (table is null) break;
                foreach (int v in table.Values)
                    EnumerateChainHeadsRecursive(engine, v, heads, visited);
                EnumerateChainHeadsRecursive(engine, table.DefaultAddress, heads, visited);
                break;
            }
            case Shumway.Core.Opcode.TryMeElse:
                heads.Add(addr);
                break;
            case Shumway.Core.Opcode.RetryMeElse:
                // Demoted chain head (after asserta) — has Nop at +5.
                if (addr + 6 <= prog.Length
                    && prog[addr + 5] == (byte)Shumway.Core.Opcode.Nop)
                    heads.Add(addr);
                break;
            // fail_stub or anything else: stop.
        }
    }

    /// <summary>Collects every chain's tail-next
    /// operand, used by the var-arg-at-0 / multi-arg extension paths
    /// that need to append a new entry to every chain. Builds on
    /// <see cref="EnumerateChainHeadsRecursive"/> so multi-level
    /// layouts are fully covered.</summary>
    private bool CollectAllChainTailNextOperands(
        Activation engine, int predAddr, List<int> tailNextOperands)
    {
        var prog = engine.CurrentProgram;
        if (prog is null) return false;
        int failStub = engine.DynamicFailStubAddr;
        if (failStub <= 0) return false;
        var heads = new HashSet<int>();
        var visited = new HashSet<int>();
        // predAddr+1 is the top-level switch_on_term / switch_on_arg.
        EnumerateChainHeadsRecursive(engine, predAddr + 1, heads, visited);
        foreach (int head in heads)
        {
            int tailNext = WalkChainToTailNextOperand(prog, head, failStub);
            if (tailNext < 0) return false;
            tailNextOperands.Add(tailNext);
        }
        return true;
    }

    // ====================================================================
    // in-place asserta for extensible-indexed dynamic
    // predicates.
    // ====================================================================

    /// <summary>Prepends a clause to an extensible-
    /// indexed predicate in place. Asserta is harder than assertz
    /// because the chain head's address changes (the new entry
    /// becomes the head, the old head gets demoted to a non-head),
    /// and the old head was referenced from external pointer slots
    /// — switch_on_term operands, switch_on_atom/integer/structure
    /// table values, sub-switch default-cascade addresses. Each of
    /// those slots that pointed at an old head gets redirected to
    /// the new head; the demoted old head's <c>&lt;next&gt;</c>
    /// operand is unchanged (still references the second entry in
    /// the chain or fail_stub).
    ///
    /// Returns <c>false</c> when the predicate isn't using the
    /// layout, the new key has no existing sub-switch
    /// (would be a layout change), or any chain to demote isn't
    /// currently a <c>try_me_else</c> head (a chain whose only
    /// remaining live entry has died — unusual but a possibility
    /// after a retract).</summary>
    internal bool TryPrependToIndexedDynamic(
        Activation engine, int functorId, Shumway.Compiler.Ast.Clause newClause)
    {
        if (!IsExtensibleIndexedLayout(engine, functorId)) return false;
        // capture buffer ownership before any AppendCode.
        bool ownsHost = EngineOwnsHostBuffer(engine);
        var addrMap = engine.CurrentFunctorAddresses!;
        var prog = engine.CurrentProgram!;
        int predAddr = addrMap[functorId];
        int failStub = engine.DynamicFailStubAddr;
        if (failStub <= 0) return false;

        var arg0Kind = Arg0Of(newClause, out int arg0Key);
        if (arg0Kind == Arg0Kind.None) return false;
        int arity = FunctorTable.Lookup(functorId).Arity;
        bool isVarArg = arg0Kind == Arg0Kind.Var;

        // for multi-arg, the var slot at predAddr+2
        // cascades through switch_on_arg before reaching a chain
        // head. Walk the cascade to find the final var chain.
        int varChainHead = FindFinalVarChainHead(engine, predAddr);
        if (varChainHead < 0) return false;

        // Plan: which chain heads need demotion? var-arg touches
        // every chain; same-key touches (bucket + var); new-key
        // touches (var) and creates a brand-new bucket without
        // demotion.
        var chainsToDemote = new List<int>();
        bool isNewKey = false;
        int newKeyValue = 0;
        int subSwitchTableId = -1;

        if (isVarArg)
        {
            var heads = new HashSet<int>();
            if (!CollectAllChainHeadsForRedirect(engine, predAddr, heads))
                return false;
            chainsToDemote.AddRange(heads);
        }
        else
        {
            chainsToDemote.Add(varChainHead);
            int bucketChainHead = FindBucketChainHead(engine, predAddr, newClause);
            isNewKey = bucketChainHead < 0;
            if (!isNewKey)
            {
                chainsToDemote.Add(bucketChainHead);
            }
            else
            {
                if (!TryLocateSubSwitchForArg(
                        engine, predAddr, arg0Kind, arg0Key, out newKeyValue, out subSwitchTableId))
                    return false;
            }
        }

        // Validate every chain head we plan to demote currently has
        // try_me_else as its first opcode. A chain whose head was
        // demoted by a prior asserta or whose head was retracted
        // (died != MaxValue) but never compacted would fail this
        // check — let the rebuild handle those rarities.
        foreach (int h in chainsToDemote)
        {
            if (h < 0 || h + 9 > prog.Length) return false;
            if (prog[h] != (byte)Shumway.Core.Opcode.TryMeElse) return false;
        }
        // Also dedupe — a chain referenced by multiple keys (e.g.
        // every bucket reachable from a shared sub-switch default)
        // would otherwise be demoted twice. CollectAllChainHeadsForRedirect
        // dedupes, but the same-key path adds bucket + var manually.
        chainsToDemote = chainsToDemote.Distinct().ToList();

        // Compile the new clause's body (shared helper with
        // a fact fast path).
        var compiledClause = E.CompileRuntimeAssertClause(engine, functorId, newClause);
        if (compiledClause is null) return false;

        var bodyEmitter = new Shumway.Compiler.Wam.BytecodeEmitter();
        if (E.Flags.EmitDebugInfo) bodyEmitter.EmitMetaDbgInfo(0);
        int bodyContentLocalStart = bodyEmitter.Position;
        bodyEmitter.AppendBytes(compiledClause.Bytecode);
        byte[] bodyChunk = bodyEmitter.ToBytes();
        var chainTable = GetOrCreateChainTable(engine);
        int bodyAddr = PlaceChunk(engine, chainTable, bodyChunk);
        NoteIndexedBody(chainTable, functorId, bodyAddr, bodyChunk.Length, compiledClause);
        prog = engine.CurrentProgram!;

        // Patch call sites in the body to absolute targets.
        foreach (var site in compiledClause.CallSites)
        {
            int operandPos = bodyAddr + bodyContentLocalStart + site.OpcodeOffset + 1;
            int target = addrMap.TryGetValue(site.CalleeFunctorId, out int addr)
                ? addr
                : Shumway.Core.CallTarget.ForUndefined(site.CalleeFunctorId);
            Shumway.Core.BytecodeIO.WriteInt32(prog, operandPos, target);
        }
        RelocateDispatchSites(prog, compiledClause, bodyAddr + bodyContentLocalStart);

        // For new-key concrete: build a brand-new bucket chain
        // (new body first, then var-args), add to switch table.
        // No demotion needed for the new bucket.
        if (isNewKey)
        {
            var varArgBodies = CollectVarArgBodies(engine, varChainHead, functorId, newClause, prepended: true);
            if (varArgBodies is null)
            {
                E.InvalidatePersistent();
                E.RebuildEngineFidChainView(engine, functorId);
                return true;   // the rebuild took the store, the new clause included
            }
            // Asserta-flavoured layout: new body first, var-args after.
            int newBucketHead = BuildAndAppendBucketChainAsserta(
                engine, functorId, failStub, arity, bodyAddr, varArgBodies);
            prog = engine.CurrentProgram!;
            var oldTable = engine.GetSwitchTable(subSwitchTableId);
            if (oldTable is null) return false;
            var newTable = oldTable.WithAdditionalEntry(newKeyValue, newBucketHead);
            engine.ReplaceSwitchTable(subSwitchTableId, newTable);
            MirrorSwitchTableIntoDynamicLink(subSwitchTableId, newTable);
        }

        // Demote each chain to demote, build new head chunk, record
        // the old → new redirect.
        var redirectMap = new Dictionary<int, int>();
        int varEntryDied = -1;
        foreach (int oldHead in chainsToDemote)
        {
            // Demote try_me_else (9 bytes) to retry_me_else (5
            // bytes) + 4 nops (4 bytes). The <next> operand at
            // +1..+4 stays — retry_me_else uses it identically.
            prog[oldHead] = (byte)Shumway.Core.Opcode.RetryMeElse;
            prog[oldHead + 5] = (byte)Shumway.Core.Opcode.Nop;
            prog[oldHead + 6] = (byte)Shumway.Core.Opcode.Nop;
            prog[oldHead + 7] = (byte)Shumway.Core.Opcode.Nop;
            prog[oldHead + 8] = (byte)Shumway.Core.Opcode.Nop;

            // New head: try_me_else <oldHead> arity + check_visible
            // + execute <bodyAddr>.
            var em = new Shumway.Compiler.Wam.BytecodeEmitter();
            em.EmitTryMeElse(oldHead, arity);
            em.EmitCheckVisible(born: E._dbGeneration.Value, died: long.MaxValue);
            em.EmitExecute(bodyAddr);
            byte[] head = em.ToBytes();
            int newHead = PlaceChunk(engine, chainTable, head);
            NoteIndexedEntry(chainTable, functorId, newHead, head.Length);
            prog = engine.CurrentProgram!;
            redirectMap[oldHead] = newHead;
            if (oldHead == varChainHead)
                varEntryDied = newHead + ChainEntryHeaderSize(prog, newHead) + 9;
        }
        NoteIndexedVarEntry(engine, functorId, newClause, varEntryDied);

        // Walk every pointer slot that could reference a chain head
        // and redirect any that match.
        RedirectChainHeads(engine, predAddr, redirectMap);

        SyncOrInvalidateAfterMutation(engine, ownsHost);
        // refresh skipped when the pools didn't grow.
        E.RefreshLiteralPoolsIfChanged(engine);
        return true;
    }

    /// <summary>like
    /// <see cref="BuildAndAppendNewBucketChain"/> but with the new
    /// clause's body first (the asserta order) followed by the var-
    /// arg bodies in source order. Returns the new bucket chain
    /// head address.</summary>
    private int BuildAndAppendBucketChainAsserta(
        Activation engine, int functorId, int failStub, int headArity,
        int newBodyAddr, IReadOnlyList<int> varArgBodies)
    {
        var bodies = new List<int> { newBodyAddr };
        bodies.AddRange(varArgBodies);
        return PlaceBucketChain(engine, functorId, failStub, headArity, bodies);
    }

    /// <summary>Collects every chain head reachable
    /// from the predicate's entry into <paramref name="heads"/>,
    /// across every level of multi-arg switch dispatch.
    /// Delegates to <see cref="EnumerateChainHeadsRecursive"/>.</summary>
    private bool CollectAllChainHeadsForRedirect(
        Activation engine, int predAddr, HashSet<int> heads)
    {
        if (engine.CurrentProgram is null) return false;
        var visited = new HashSet<int>();
        EnumerateChainHeadsRecursive(engine, predAddr + 1, heads, visited);
        return true;
    }

    /// <summary>walks every pointer slot that can
    /// reference a chain head and replaces any value present in
    /// <paramref name="redirect"/> with the new address. Touches:
    /// <c>switch_on_term</c>'s var / list / struct operands at
    /// <c>predAddr + 2 / 10 / 14</c> (const_lbl is excluded —
    /// it points at a sub-switch, never a chain head directly);
    /// every sub-switch table's keys-and-values plus its default
    /// address. Each modified switch table is replaced with a new
    /// instance and mirrored into the cached <c>E._dynamicLink</c>.</summary>
    private void RedirectChainHeads(
        Activation engine, int predAddr, IReadOnlyDictionary<int, int> redirect)
    {
        if (redirect.Count == 0) return;
        // walk the predicate's dispatch graph recursively,
        // patching every switch operand and switch-table value/default
        // that matches an entry in redirect.
        var visitedSwitches = new HashSet<int>();
        RedirectChainHeadsRecursive(engine, predAddr + 1, redirect, visitedSwitches);
    }

    private void RedirectChainHeadsRecursive(
        Activation engine, int addr, IReadOnlyDictionary<int, int> redirect,
        HashSet<int> visited)
    {
        if (addr <= 0) return;
        var prog = engine.CurrentProgram!;
        if (addr + 1 > prog.Length) return;
        if (!visited.Add(addr)) return;
        var op = (Shumway.Core.Opcode)prog[addr];

        void PatchOperand(int operandPos)
        {
            int cur = Shumway.Core.BytecodeIO.ReadInt32(prog, operandPos);
            if (redirect.TryGetValue(cur, out int repl))
                Shumway.Core.BytecodeIO.WriteInt32(prog, operandPos, repl);
        }

        switch (op)
        {
            case Shumway.Core.Opcode.SwitchOnTerm:
                // 4 address operands at +1, +5, +9, +13.
                for (int j = 0; j < 4; j++) PatchOperand(addr + 1 + 4 * j);
                // Recurse into each label.
                for (int j = 0; j < 4; j++)
                {
                    int lbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 1 + 4 * j);
                    RedirectChainHeadsRecursive(engine, lbl, redirect, visited);
                }
                break;
            case Shumway.Core.Opcode.SwitchOnArg:
                // arg_idx at +1; 4 address operands at +5, +9, +13, +17.
                for (int j = 0; j < 4; j++) PatchOperand(addr + 5 + 4 * j);
                for (int j = 0; j < 4; j++)
                {
                    int lbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 5 + 4 * j);
                    RedirectChainHeadsRecursive(engine, lbl, redirect, visited);
                }
                break;
            case Shumway.Core.Opcode.SwitchOnAtom:
            case Shumway.Core.Opcode.SwitchOnInteger:
            case Shumway.Core.Opcode.SwitchOnStructure:
            {
                int tid = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 1);
                var table = engine.GetSwitchTable(tid);
                if (table is null) break;
                var newTable = RedirectSwitchTable(table, redirect);
                var live = newTable ?? table;
                if (newTable is not null)
                {
                    engine.ReplaceSwitchTable(tid, newTable);
                    MirrorSwitchTableIntoDynamicLink(tid, newTable);
                }
                foreach (int v in live.Values)
                    RedirectChainHeadsRecursive(engine, v, redirect, visited);
                RedirectChainHeadsRecursive(engine, live.DefaultAddress, redirect, visited);
                break;
            }
            case Shumway.Core.Opcode.SwitchOnAtomArg:
            case Shumway.Core.Opcode.SwitchOnIntegerArg:
            case Shumway.Core.Opcode.SwitchOnStructureArg:
            {
                int tid = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 5);
                var table = engine.GetSwitchTable(tid);
                if (table is null) break;
                var newTable = RedirectSwitchTable(table, redirect);
                var live = newTable ?? table;
                if (newTable is not null)
                {
                    engine.ReplaceSwitchTable(tid, newTable);
                    MirrorSwitchTableIntoDynamicLink(tid, newTable);
                }
                foreach (int v in live.Values)
                    RedirectChainHeadsRecursive(engine, v, redirect, visited);
                RedirectChainHeadsRecursive(engine, live.DefaultAddress, redirect, visited);
                break;
            }
            // Other opcodes (chain heads, fail_stub, anything else):
            // stop. Chain heads aren't switch nodes — they don't have
            // outgoing pointers that need redirecting beyond their
            // <next>, which is internal to the chain.
        }
    }

    /// <summary>returns a new
    /// <see cref="Shumway.Core.SwitchTable"/> with every value
    /// (including the default address) replaced if present in
    /// <paramref name="redirect"/>. Returns <c>null</c> when no
    /// entry was redirected — caller skips the replacement.</summary>
    private static Shumway.Core.SwitchTable? RedirectSwitchTable(
        Shumway.Core.SwitchTable old, IReadOnlyDictionary<int, int> redirect)
    {
        bool changed = false;
        int[] keys = old.Keys.ToArray();
        int[] values = old.Values.ToArray();
        for (int i = 0; i < values.Length; i++)
        {
            if (redirect.TryGetValue(values[i], out int repl))
            {
                values[i] = repl;
                changed = true;
            }
        }
        int newDefault = old.DefaultAddress;
        if (redirect.TryGetValue(newDefault, out int defRepl))
        {
            newDefault = defRepl;
            changed = true;
        }
        return changed ? new Shumway.Core.SwitchTable(keys, values, newDefault) : null;
    }

    // ====================================================================
    // in-place retract for extensible-indexed dynamic
    // predicates.
    // ====================================================================

    /// <summary>returns the body address of the
    /// <paramref name="clauseIndex"/>'th still-alive clause in the
    /// var-fallthrough chain, where "alive" is defined as
    /// <c>died == long.MaxValue</c> in the entry's
    /// <c>check_visible</c>. Previously-retracted entries (died set
    /// to some generation by a prior retract) are
    /// skipped, so the index aligns with the post-removal
    /// <c>_dynamicClauses</c> ordering — except that this lookup
    /// runs before the current <c>RemoveAt</c>, so
    /// <paramref name="clauseIndex"/> is the position in the
    /// pre-removal list. Returns <c>-1</c> on layout mismatch or
    /// when the index runs off the chain.</summary>
    internal int FindBodyAddrForClauseIndex(
        Activation engine, int functorId, int clauseIndex, out int varEntryDiedAddr)
    {
        varEntryDiedAddr = -1;
        if (!IsExtensibleIndexedLayout(engine, functorId)) return -1;
        var addrMap = engine.CurrentFunctorAddresses;
        if (addrMap is null || !addrMap.TryGetValue(functorId, out int predAddr)) return -1;
        var prog = engine.CurrentProgram!;
        int failStub = engine.DynamicFailStubAddr;
        int varChainHead = FindFinalVarChainHead(engine, predAddr);
        if (varChainHead < 0) return -1;
        int cur = varChainHead;
        int aliveIdx = 0;
        while (true)
        {
            if (cur < 0 || cur + 27 > prog.Length) return -1;
            int chainHeaderSize = ChainEntryHeaderSize(prog, cur);
            int diedAddr = cur + chainHeaderSize + 9;
            int execOpPos = cur + chainHeaderSize + 17;
            if (execOpPos + 5 > prog.Length) return -1;
            if (prog[execOpPos] != (byte)Shumway.Core.Opcode.Execute) return -1;
            long died = Shumway.Core.BytecodeIO.ReadInt64(prog, diedAddr);
            if (died == long.MaxValue)
            {
                if (aliveIdx == clauseIndex)
                {
                    // The entry itself, so the retract need not find it again.
                    varEntryDiedAddr = diedAddr;
                    return Shumway.Core.BytecodeIO.ReadInt32(prog, execOpPos + 1);
                }
                aliveIdx++;
            }
            int next = Shumway.Core.BytecodeIO.ReadInt32(prog, cur + 1);
            if (next == failStub) return -1;
            cur = next;
        }
    }

    /// <summary>Patches the died slot of every entry of the extensible-indexed
    /// predicate whose <c>execute</c> targets <paramref name="bodyAddr"/>, the
    /// retired clause's body. The died slot is the second 8-byte field of the
    /// entry's <c>check_visible</c>, at chain-header-size + 1 + 8 from the
    /// entry's start. A clause with a concrete first argument is in two chains,
    /// the one of every clause (its entry is <paramref name="varEntryDiedAddr"/>,
    /// found by the index walk) and its key's, reached through the switch
    /// table: those two are patched without enumerating the heads, which made a
    /// drain O(clauses) per retract. A clause with a variable first argument is
    /// in every chain, and a layout the bucket lookup does not know (a cascade
    /// over several arguments) is walked whole. Returns <c>true</c> when at
    /// least one entry was patched; <c>false</c> when the predicate isn't using
    /// the layout or no chain referenced the retired body.</summary>
    internal bool TryPatchDiedInAllIndexedChains(
        Activation engine, int functorId, int bodyAddr, Clause clause, int varEntryDiedAddr)
    {
        if (!IsExtensibleIndexedLayout(engine, functorId)) return false;
        var addrMap = engine.CurrentFunctorAddresses!;
        var prog = engine.CurrentProgram!;
        int predAddr = addrMap[functorId];
        int failStub = engine.DynamicFailStubAddr;
        long gen = E._dbGeneration.Value;

        if (varEntryDiedAddr > 0)
        {
            int bucketHead = FindBucketChainHead(engine, predAddr, clause);
            if (bucketHead > 0 && PatchDiedInChain(prog, bucketHead, failStub, bodyAddr, gen))
            {
                Shumway.Core.BytecodeIO.WriteInt64(prog, varEntryDiedAddr, gen);
                return true;
            }
        }

        // enumerate every chain head reachable from the
        // top-level switch, including multi-level cascades through
        // switch_on_arg.
        var heads = new HashSet<int>();
        var visited = new HashSet<int>();
        EnumerateChainHeadsRecursive(engine, predAddr + 1, heads, visited);

        bool anyPatched = false;
        foreach (int head in heads)
            if (PatchDiedInChain(prog, head, failStub, bodyAddr, gen)) anyPatched = true;
        return anyPatched;
    }

    /// <summary>Walks one chain and patches the died slot of the entry whose
    /// <c>execute</c> targets <paramref name="bodyAddr"/>; a chain holds a
    /// clause at most once, so the walk ends there. False when the chain has no
    /// such entry.</summary>
    private bool PatchDiedInChain(byte[] prog, int head, int failStub, int bodyAddr, long gen)
    {
        int cur = head;
        while (cur > 0 && cur + 27 <= prog.Length)
        {
            IndexedPatchSteps++;
            int chainHeaderSize = ChainEntryHeaderSize(prog, cur);
            int execOpPos = cur + chainHeaderSize + 17;
            if (execOpPos + 5 > prog.Length) break;
            if (prog[execOpPos] != (byte)Shumway.Core.Opcode.Execute) break;
            if (Shumway.Core.BytecodeIO.ReadInt32(prog, execOpPos + 1) == bodyAddr)
            {
                Shumway.Core.BytecodeIO.WriteInt64(prog, cur + chainHeaderSize + 9, gen);
                return true;
            }
            int next = Shumway.Core.BytecodeIO.ReadInt32(prog, cur + 1);
            if (next == failStub) break;
            cur = next;
        }
        return false;
    }

    // A retract kills the clause's entry in each chain that holds it and
    // unlinks none: every later call walks the dead entries of its key. Once
    // they are as many as the live clauses they are unlinked, so that a
    // predicate drained and filled again inside one query does not grow its
    // chains by a generation each time. The sweep costs a walk of every
    // chain; at that count the retracts that caused it pay for it.
    private const int IndexedSweepThreshold = 4;

    /// <summary>Retracts from a predicate in the indexed layout, and the
    /// sweeps that unlinked entries.</summary>
    internal long IndexedRetracts, IndexedSweeps;

    /// <summary>Chain entries visited while those retracts patched their
    /// clause's entries: a test pins that a drain visits O(clauses) of them.</summary>
    internal long IndexedPatchSteps;

    /// <summary>Retracts whose clause had no entry on record
    /// (<see cref="DynChainState.IndexedVarEntries"/>) and walked the chain of
    /// every clause by index instead; a test pins it at zero.</summary>
    internal long IndexedVarEntryMisses;

    /// <summary>For a test: keeps the record empty, so every retract takes the
    /// walk by index and the test can pin what that fallback costs.</summary>
    internal bool IndexedVarRecordOff;

    /// <summary>After a retract killed a clause's entries in the indexed layout.</summary>
    internal void NoteIndexedRetract(Activation engine, int functorId)
    {
        if (GetChainTable(engine) is not { } table
            || !table.Chains.TryGetValue(functorId, out var chain)) return;
        IndexedRetracts++;
        int dead = ++chain.IndexedDead;
        if (dead < IndexedSweepThreshold || dead < chain.IndexedSweepAt) return;
        int live = E._dynStore.HasClauses(functorId)
            ? E._dynStore.PhysicalClauses(functorId).Count - E._dynStore.TombstoneCount(functorId) : 0;
        if (dead < live) return;
        if (SweepIndexedDead(engine, functorId, chain))
        {
            chain.IndexedDead = 0;
            chain.IndexedSweepAt = 0;
        }
        // A goal is still walking the predicate: not again until a quarter more.
        else chain.IndexedSweepAt = dead + Math.Max(IndexedSweepThreshold, dead / 4);
    }

    /// <summary>Unlinks the dead entries of every chain of an indexed
    /// predicate, each chain's head apart (the switch points at it). Their
    /// bytes stay, and so do their own links: the sweep does not run while a
    /// choice point of an activation on this buffer resumes at one of the
    /// predicate's entries, since that goal may have to see a clause retracted
    /// after it began (the logical update view). False when it did not run.</summary>
    private bool SweepIndexedDead(Activation engine, int functorId, DynChainState chain)
    {
        if (!IsExtensibleIndexedLayout(engine, functorId)) return false;
        var prog = engine.CurrentProgram!;
        int failStub = engine.DynamicFailStubAddr;
        if (failStub <= 0) return false;
        var heads = new HashSet<int>();
        EnumerateChainHeadsRecursive(
            engine, engine.CurrentFunctorAddresses![functorId] + 1, heads, new HashSet<int>());

        // Every chain is read whole before anything is written. An entry the
        // relinked chains leave out is unlinked; a body only unlinked entries
        // name is free once nothing runs it.
        var entries = new HashSet<int>();
        var links = new List<(int NextOperand, int Target)>();
        var unlinked = new List<(int Addr, int Body)>();
        var named = new HashSet<int>();
        foreach (int head in heads)
        {
            int cur = head, keptNext = -1;
            int stepsLeft = prog.Length / 5 + 1;
            while (cur != failStub)
            {
                if (cur <= 0 || cur + 27 > prog.Length || --stepsLeft < 0) return false;
                int header = ChainEntryHeaderSize(prog, cur);
                if (cur + header + 22 > prog.Length
                    || prog[cur + header] != (byte)Shumway.Core.Opcode.CheckVisible
                    || prog[cur + header + 17] != (byte)Shumway.Core.Opcode.Execute) return false;
                entries.Add(cur);
                bool dead = Shumway.Core.BytecodeIO.ReadInt64(prog, cur + header + 9) != long.MaxValue;
                int body = Shumway.Core.BytecodeIO.ReadInt32(prog, cur + header + 18);
                if (keptNext < 0 || !dead)
                {
                    if (keptNext >= 0 && Shumway.Core.BytecodeIO.ReadInt32(prog, keptNext) != cur)
                        links.Add((keptNext, cur));
                    keptNext = cur + 1;
                    named.Add(body);
                }
                else unlinked.Add((cur, body));
                cur = Shumway.Core.BytecodeIO.ReadInt32(prog, cur + 1);
            }
            if (keptNext >= 0 && Shumway.Core.BytecodeIO.ReadInt32(prog, keptNext) != failStub)
                links.Add((keptNext, failStub));
        }
        if (links.Count == 0) return true;
        if (AnyChoicePointResumesAt(engine, entries)) return false;

        foreach (var (nextOperand, target) in links)
            Shumway.Core.BytecodeIO.WriteInt32(prog, nextOperand, target);
        // The table's tail may be an entry no chain links any more: an
        // append that falls back to it rebuilds the predicate instead.
        chain.TailNextAddr = -1;
        IndexedSweeps++;
        // The entries placed in place and the bodies they alone named are
        // dropped: their bytes go to the free list once no activation reaches
        // them, for the asserts that follow. What the layout was built with
        // goes with the buffer.
        var table = GetOrCreateChainTable(engine);
        foreach (var (addr, body) in unlinked)
        {
            if (chain.IndexedEntries is { } ents && ents.Remove(addr, out int length))
                Drop(table, new DroppedCode(addr, addr + length, null, System.Array.Empty<int>(), recycle: true));
            if (!named.Contains(body) && chain.IndexedBodies is { } bodies && bodies.Remove(body, out int bodyLength))
                DropChunk(table, body, bodyLength, recycle: true);
        }
        ReleaseUnreached(table);
        return true;
    }

    // A goal still enumerating a predicate has a choice point whose
    // alternative is one of its chain entries. The activations that share
    // this buffer count as much as the running one.
    private bool AnyChoicePointResumesAt(Activation engine, HashSet<int> addresses)
    {
        static bool Resumes(Activation a, HashSet<int> at)
        {
            foreach (var (_, savedBp, _) in a.EnumerateChoicePoints())
                if (at.Contains(savedBp)) return true;
            return false;
        }
        if (Resumes(engine, addresses)) return true;
        if (OthersOnBuffer(engine) is { } others)
            foreach (var other in others)
                if (Resumes(other, addresses)) return true;
        return false;
    }

    /// <summary>The other open activations that run on
    /// <paramref name="engine"/>'s buffer: a query nested in another starts
    /// on the buffer the suspended one is on. Null for none, the ordinary
    /// case. A goal of theirs may be walking a chain the running activation
    /// is about to relink.</summary>
    internal List<Activation>? OthersOnBuffer(Activation engine)
    {
        List<Activation>? result = null;
        for (int i = _liveEngines.Count - 1; i >= 0; i--)
            if (_liveEngines[i].TryGetTarget(out var other) && !ReferenceEquals(other, engine)
                && ReferenceEquals(other.CurrentProgram, engine.CurrentProgram))
                (result ??= new List<Activation>()).Add(other);
        return result;
    }

    /// <summary>For a test: the dead entries the chain of all clauses of an
    /// indexed predicate still links, its head apart; -1 for another layout.</summary>
    internal int IndexedDeadLinked(Activation engine, int functorId)
    {
        if (!IsExtensibleIndexedLayout(engine, functorId)) return -1;
        var prog = engine.CurrentProgram!;
        int failStub = engine.DynamicFailStubAddr;
        int head = FindFinalVarChainHead(engine, engine.CurrentFunctorAddresses![functorId]);
        if (head < 0) return -1;
        int dead = 0, stepsLeft = prog.Length / 5 + 1;
        for (int cur = Shumway.Core.BytecodeIO.ReadInt32(prog, head + 1);
             cur != failStub && cur > 0 && cur + 27 <= prog.Length && --stepsLeft >= 0;
             cur = Shumway.Core.BytecodeIO.ReadInt32(prog, cur + 1))
            if (Shumway.Core.BytecodeIO.ReadInt64(prog, cur + ChainEntryHeaderSize(prog, cur) + 9) != long.MaxValue)
                dead++;
        return dead;
    }

    /// <summary>writes <paramref name="newTable"/> into
    /// the dynamic region's slot of the cached <see cref="E._dynamicLink"/>
    /// so the next query's <see cref="SetupQueryFromTerm"/> (which
    /// rebuilds the merged engine.SwitchTables list from
    /// staticLink + E._dynamicLink + queryLink) carries the
    /// mutation forward. The merged-table id at runtime is
    /// <c>staticLink.SwitchTables.Count + dynamicLocalId</c>; we
    /// undo the offset to find the right slot in E._dynamicLink.
    /// </summary>
    internal void MirrorSwitchTableIntoDynamicLink(int mergedTableId, Shumway.Core.SwitchTable newTable)
    {
        if (E._dynamicLink is null) return;
        int staticCount = E._staticLink?.SwitchTables.Count ?? 0;
        int dynLocalId = mergedTableId - staticCount;
        if (dynLocalId < 0 || dynLocalId >= E._dynamicLink.SwitchTables.Count) return;
        if (E._dynamicLink.SwitchTables is List<Shumway.Core.SwitchTable> dynList)
            dynList[dynLocalId] = newTable;
        // If the link's IReadOnlyList isn't actually a List (some
        // alternative implementation), the mutation can't be made
        // persistent — degrades gracefully: the current
        // query holds the update via engine.SwitchTables, the next
        // query will rebuild from the unmutated link and miss it.
        // That just regresses to the rebuild fallback for
        // the affected predicate, which is correct, only slower.
    }

    /// <summary>emits a fresh bucket chain containing
    /// every var-arg clause's body (in source order) followed by
    /// the new clause's body, appends it to the buffer, and returns
    /// the chain head address. The chain head uses
    /// <c>try_me_else</c> (9 bytes); subsequent entries use
    /// <c>retry_me_else</c> (5 bytes); the last entry's
    /// <c>&lt;next&gt;</c> is the fail stub.</summary>
    private int BuildAndAppendNewBucketChain(
        Activation engine, int functorId, int failStub, int headArity,
        IReadOnlyList<int> varArgBodies, int newBodyAddr)
    {
        // Chain bodies in source order: var-arg first, then new.
        var bodies = new List<int>(varArgBodies);
        bodies.Add(newBodyAddr);
        return PlaceBucketChain(engine, functorId, failStub, headArity, bodies);
    }

    /// <summary>A bucket chain of <paramref name="bodies"/> in that order: the
    /// head a <c>try_me_else</c>, the rest <c>retry_me_else</c>, the last's next
    /// the fail stub. Placed in a free chunk when one fits, else at the end of
    /// the buffer, its entries on record for the sweep. The head's address.</summary>
    private int PlaceBucketChain(Activation engine, int functorId, int failStub, int headArity, List<int> bodies)
    {
        const int HeadEntrySize = 9 + 17 + 5;
        const int NonHeadEntrySize = 5 + 17 + 5;
        int count = bodies.Count;
        int total = HeadEntrySize + (count - 1) * NonHeadEntrySize;
        var table = GetOrCreateChainTable(engine);
        int hole = TryReuseFreeChunk(table.FreeChunks, total);
        int startAddr = hole >= 0 ? hole : engine.ProgramLength;
        // Each entry's <next> is the absolute address of the next entry, or
        // the fail stub for the last: known once the start is.
        var em = new Shumway.Compiler.Wam.BytecodeEmitter();
        for (int i = 0; i < count; i++)
        {
            int thisSize = i == 0 ? HeadEntrySize : NonHeadEntrySize;
            int nextAddr = i == count - 1 ? failStub : startAddr + em.Position + thisSize;
            if (i == 0) em.EmitTryMeElse(nextAddr, headArity);
            else em.EmitRetryMeElse(nextAddr);
            em.EmitCheckVisible(born: E._dbGeneration.Value, died: long.MaxValue);
            em.EmitExecute(bodies[i]);
        }
        byte[] chunk = em.ToBytes();
        int chunkAddr;
        if (hole >= 0)
        {
            System.Array.Copy(chunk, 0, engine.CurrentProgram!, hole, chunk.Length);
            chunkAddr = hole;
        }
        else chunkAddr = engine.AppendCode(chunk);
        System.Diagnostics.Debug.Assert(chunkAddr == startAddr,
            $"in-place assert: bucket chain address mismatch (expected {startAddr}, got {chunkAddr}).");
        for (int i = 0, at = chunkAddr; i < count; i++)
        {
            int size = i == 0 ? HeadEntrySize : NonHeadEntrySize;
            NoteIndexedEntry(table, functorId, at, size);
            at += size;
        }
        return chunkAddr;
    }

    /// <summary>Pulls the first free chunk whose
    /// length is at least <paramref name="needed"/> off the chain
    /// table's free-list and returns its address; the chunk's tail
    /// (beyond <paramref name="needed"/> bytes) goes back on the list.
    /// Returns -1 when no fit is available, meaning the caller should
    /// fall back to <c>engine.AppendCode</c>. The free-list lives on the
    /// per-buffer <see cref="DynChainTable"/> (its addresses
    /// are buffer-relative), so chunks freed in one query are reusable
    /// by the next while that buffer is reused.</summary>
    private static string TraceFrames()
    {
        var sb = new System.Text.StringBuilder();
        int n = 0;
        foreach (string line in System.Environment.StackTrace.Split('\n'))
            if (line.Contains("Shumway.") && !line.Contains("TraceFrames()") && !line.Contains("FreeBytes") && !line.Contains("TryReuseFreeChunk") && n++ < 7)
                sb.Append(" <- ").Append(line.Trim().Replace("at Shumway.Embedding.", "").Split(" in ")[0]);
        return sb.ToString();
    }

    internal static int TryReuseFreeChunk(
        List<(int Addr, int Length)> freeChunks, int needed)
    {
        for (int i = 0; i < freeChunks.Count; i++)
        {
            var (addr, length) = freeChunks[i];
            if (length < needed) continue;
            if (TraceFree) System.Console.Error.WriteLine($"[REUSE] 0x{addr:X}+{needed}{TraceFrames()}");
            int leftover = length - needed;
            if (leftover > 0) freeChunks[i] = (addr + needed, leftover);
            else freeChunks.RemoveAt(i);
            return addr;
        }
        return -1;
    }

    /// <summary>Test hook: returns the absolute byte offset of clause
    /// <paramref name="clauseIndex"/>'s died slot in the running program,
    /// or <c>null</c> when no chain state exists. Used by tests
    /// to verify <c>retract</c> patches the slot.</summary>
    internal int? PeekDiedAddr(int functorId, int clauseIndex)
    {
        if (!_dynChainTable.Chains.TryGetValue(functorId, out var chain)) return null;
        if (clauseIndex < 0 || clauseIndex >= chain.Entries.Count) return null;
        return chain.Entries[clauseIndex].DiedOperandAddr;
    }

    /// <summary>Test hook: returns the absolute byte offset of clause
    /// <paramref name="clauseIndex"/>'s chain-instruction <c>&lt;next&gt;</c>
    /// operand in the running program, or <c>null</c> when no chain state
    /// exists. -1 when the clause was emitted without a chain instruction
    /// in front of it.</summary>
    internal int? PeekNextAddr(int functorId, int clauseIndex)
    {
        if (!_dynChainTable.Chains.TryGetValue(functorId, out var chain)) return null;
        if (clauseIndex < 0 || clauseIndex >= chain.Entries.Count) return null;
        return chain.Entries[clauseIndex].NextOperandAddr;
    }


    /// <summary>The table describing the host's current persistent buffer
    /// (<see cref="E._persistentProgram"/>). Reused across queries while the
    /// buffer is; replaced whenever the buffer is rebuilt or
    /// invalidated.</summary>
    private DynChainTable _dynChainTable = new();

    /// <summary>The chain table for the buffer <paramref name="engine"/> runs
    /// on, registered at query setup and kept on the activation, so a finished
    /// query's activation (and, once unshared, its table) is collectable.</summary>
    internal DynChainTable? GetChainTable(Activation engine)
        => engine.HostChainTable as DynChainTable;

    /// <summary>ADR-041 — dispatch-time clause selection for an unindexed
    /// dynamic chain (the <c>Activation.DynChainSelect</c> hook). Inspects the
    /// call's dereferenced first argument against the chain entries'
    /// first-argument keys. Returns an absolute jump address (exactly one
    /// candidate — the caller jumps there with no choice point), -1 (zero
    /// candidates — fail without walking the chain), or -2 (no selection).
    /// Selection includes logically-dead entries as candidates: their
    /// <c>check_visible</c> still runs at the jump target, and a sole-but-dead
    /// candidate correctly fails the call.</summary>
    /// <summary>Settable so a test can ask what the selector decided. The
    /// cost this guards is C# work per call, which no Prolog-level counter
    /// (inferences, heap cells) can see -- measuring it by the clock is
    /// measuring the machine, so the verdict itself is the observable.</summary>
    internal static bool DynSelDiag =
        System.Environment.GetEnvironmentVariable("SHUMWAY_DYNSEL_DIAG") == "1";

    /// <summary>Verdict tallies, counted only while <see cref="DynSelDiag"/>
    /// is on: a sole candidate jumped to, a call ruled out without walking,
    /// and a call handed back to the chain. Not interlocked -- diagnostic
    /// only, and an activation dispatches on one thread.</summary>
    internal static long SelSole, SelNone, SelDeclined;

    /// <summary>When nonzero, only this functor is tallied. The tallies are
    /// process-wide and a query dispatches more dynamic predicates than the
    /// one under test -- whatever a previously loaded library left in the
    /// prelude counts too, which is ambient and not reproducible.</summary>
    internal static int SelDiagFid;

    internal static void ResetSelCounters(int fid = 0)
    {
        SelSole = SelNone = SelDeclined = 0;
        SelDiagFid = fid;
    }

    private static int SelDiag(int fid, int result, string reason)
    {
        if (DynSelDiag)
        {
            if (SelDiagFid == 0 || fid == SelDiagFid)
            {
                if (result == -1) SelNone++; else SelDeclined++;
            }
            string name = "?";
            if (fid != 0)
            {
                var (atomId, ar) = FunctorTable.Lookup(fid);
                name = $"{AtomTable.GetById(atomId)?.Name}/{ar}";
            }
            Console.Error.WriteLine($"[dynsel] {name} -> {result} ({reason})");
        }
        return result;
    }

    internal int SelectDynChainCandidate(
        Activation engine, int trampolinePc,
        System.Collections.Generic.IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate>? predsByAddr)
    {
        var table = GetChainTable(engine);
        if (table is null) return SelDiag(0, -2, "no-table");
        int fid;
        if (predsByAddr is not null
            && predsByAddr.TryGetValue(trampolinePc, out var pred))
            fid = pred.FunctorId;
        else if (!table.TrampolineFids.TryGetValue(trampolinePc, out fid))
            return SelDiag(0, -2, $"unknown-trampoline@{trampolinePc}");
        if (!table.Chains.TryGetValue(fid, out var state) || state.LiveCount == 0)
        {
            // ISO abolish/1: once abolished the predicate is undefined — a
            // new call (this dispatch) is an undefined-procedure call, so
            // it goes through the `unknown` flag exactly like any other
            // (error → existence_error; fail → plain failure, the
            // DEC-10/Arity behaviour abolish-then-call sources rely on).
            // Only tombstoned fids divert — declared-but-empty dynamics
            // and late helpers keep failing. Tombstone probe only on this
            // cold empty path, never on the per-call fast path. LUV holds:
            // older calls' live choice points re-check born/died against
            // their own view generation, not this entry point.
            if (E._dynStore.Abolished.Contains(fid)
                && Shumway.Core.UnknownProcedure.Fails(engine, fid))
                return SelDiag(fid, -1, "abolished");
            // Marked dynamic only by the implicit_dynamic scan: the linker
            // needed a trampoline, but nothing has declared or asserted this
            // predicate, so it is still undefined — the `unknown` flag decides
            // (error by default, fail under arity_compat). A really-declared
            // dynamic with an empty chain keeps failing, per ISO.
            if (E._dynStore.IsImplicitOnly(fid)
                && Shumway.Core.UnknownProcedure.Fails(engine, fid))
                return SelDiag(fid, -1, "implicit-only");
            return SelDiag(fid, -2, state is null ? "no-chain" : "empty-chain");
        }
        var entries = state.Entries;
        // Note: a 1-entry chain is not det by itself — its try_me_else points
        // at the fail-stub and that choice point survives a successful call
        // (Logtalk's freshly-asserted send-cache entries made every second
        // send report non-deterministic). A single entry is selected CP-free
        // below regardless of the first argument.
        var prog = engine.CurrentProgram;
        if (prog is null) return -2;

        // Flat-chain layouts only. An indexed promotion's buckets also start
        // with try_me_else, so the per-entry check below cannot tell them
        // apart — discriminate at the trampoline's `execute` target instead:
        // a flat chain's head is a chain instruction; an indexed layout's is
        // switch_on_term. Anything unexpected bails to the normal dispatch.
        if (trampolinePc + 6 > prog.Length) return SelDiag(fid, -2, "short-prog");
        // The flat-chain trampoline is exactly `enter_dynamic; execute <head>`
        // — any other successor (an inline indexed switch, execute variants)
        // is not a shape this selection understands.
        if ((Shumway.Core.Opcode)prog[trampolinePc + 1] != Shumway.Core.Opcode.Execute)
            return SelDiag(fid, -2, "not-execute");
        int chainHead = Shumway.Core.BytecodeIO.ReadInt32(prog, trampolinePc + 2);
        if (chainHead < 0 || chainHead >= prog.Length) return SelDiag(fid, -2, "bad-head");
        var headOp = (Shumway.Core.Opcode)prog[chainHead];
        if (headOp != Shumway.Core.Opcode.TryMeElse) return SelDiag(fid, -2, $"head-op={headOp}");

        DynChainEntry? match;
        if (state.LiveCount == 1)
        {
            // Sole live clause: always the sole candidate, first arg
            // irrelevant. Retired entries may still sit in the list, which
            // is why this is the live head and not entries[0].
            match = state.FirstLive!;
        }
        else
        {
            // Call's first argument, dereferenced. Unbound → every clause is
            // a candidate → no selection.
            Cell a0 = engine.GetRegister(0);
            if (a0.Tag == Tag.Ref)
            {
                a0 = engine.GetHeap(engine.Deref(a0.AsHeapIndex));
                if (a0.Tag == Tag.Ref) return SelDiag(fid, -2, "unbound-a0");
            }
            // A tag that keys nothing -- a float, a bigint -- cannot rule any
            // clause out, so every entry stays a candidate and there is more
            // than one of them (the sole-entry case returned above).
            if (DynFirstArgKey.OfCall(engine, a0) is not { } callKey)
                return SelDiag(fid, -2, "unkeyed-a0");
            if (state.NoCandidate(callKey)) return SelDiag(fid, -1, "no-candidate");
            if (!state.TrySoleCandidate(callKey, out match))
                return SelDiag(fid, -2, "multi-candidate");
        }
        // Sole candidate. Its chunk must start with a chain instruction we
        // can skip (try_me_else / retry_me_else, incl. the 155f demoted-head
        // form); anything else (indexed layout, single-emission shapes) bails.
        int addr = match!.ChunkAddr;
        if (addr < 0 || addr >= prog.Length) return SelDiag(fid, -2, "bad-addr");
        var op = (Shumway.Core.Opcode)prog[addr];
        if (op != Shumway.Core.Opcode.TryMeElse
            && op != Shumway.Core.Opcode.RetryMeElse) return SelDiag(fid, -2, $"entry-op={op}");
        if (DynSelDiag && (SelDiagFid == 0 || fid == SelDiagFid)) SelSole++;
        return addr + ChainEntryHeaderSize(prog, addr);
    }

    internal DynChainTable GetOrCreateChainTable(Activation engine)
    {
        if (engine.HostChainTable is DynChainTable table) return table;
        var fresh = new DynChainTable { Current = false, Attached = 1 };
        engine.HostChainTable = fresh;
        return fresh;
    }

    /// <summary>every engine born via SetupQueryFromTerm, weakly
    /// held (in birth order). Backs the dynamic-mutation broadcast: with
    /// nested queries (Logtalk's deferred <c>:- initialization</c> chains)
    /// several engines are suspended mid-execution at once, each on its own
    /// buffer; a mutation applied only to the mutating engine's buffer is
    /// invisible to the others when they resume (a parent never sees a
    /// child's <c>$lgt_loaded_file_</c> assert; a child baked a
    /// <c>$lgt_file_loading_stack_</c> entry the parent later retracts).
    /// SWI/GProlog get this for free from their single mutable code space;
    /// broadcasting to every live engine's buffer is the equivalent for our
    /// snapshot-per-query model. Collected engines are pruned as the list
    /// is walked; finished-but-uncollected engines receive harmless writes
    /// (and keep a to-be-reused buffer current).</summary>
    private readonly List<WeakReference<Activation>> _liveEngines = new();

    /// <summary>Activations a mutation has been broadcast to. One per
    /// mutation per suspended activation; zero when nothing is suspended,
    /// which is the ordinary case and used to be one per finished-but-
    /// uncollected query instead.</summary>
    internal long BroadcastTargets;

    internal void RegisterLiveEngine(Activation engine)
    {
        for (int i = _liveEngines.Count - 1; i >= 0; i--)
            if (!_liveEngines[i].TryGetTarget(out _)) _liveEngines.RemoveAt(i);
        _liveEngines.Add(new WeakReference<Activation>(engine));
    }

    /// <summary>The open activations of this engine: what may still resume
    /// into code the engine dropped or retired.</summary>
    internal IEnumerable<Activation> LiveActivations()
    {
        for (int i = _liveEngines.Count - 1; i >= 0; i--)
            if (_liveEngines[i].TryGetTarget(out var engine)) yield return engine;
    }

    /// <summary>Drops <paramref name="engine"/> when its query ends, so the
    /// list holds only open activations — and therefore, minus whichever one
    /// is running, exactly the suspended ones the broadcast is for.
    ///
    /// <para>Without this the list kept every finished-but-uncollected
    /// activation, and each of them took a full chain patch on every
    /// mutation: in a retract loop that was one wasted O(chain) walk per
    /// retract, for a query that had already returned and can never resume.
    /// The entries stay weak so a query whose enumerator is abandoned rather
    /// than disposed can still be collected.</para></summary>
    internal void UnregisterLiveEngine(Activation engine)
    {
        for (int i = _liveEngines.Count - 1; i >= 0; i--)
        {
            if (!_liveEngines[i].TryGetTarget(out var e) || ReferenceEquals(e, engine))
                _liveEngines.RemoveAt(i);
        }
        // Compiled code only this activation could resume into goes with it,
        // and so does the query's own code.
        E.IlPromotion.ReleaseRetiredUnreached();
        E.ReleaseQueryCode(engine);
        // Its code is dead: the query's region, and the buffer when it was
        // the last activation on a table the host has replaced.
        if (engine.HostQueryUnits is QueryUnits units)
        {
            engine.HostQueryUnits = null;
            foreach (var code in units.Literals) E._literalPools.Release(code);
            foreach (var atoms in units.Atoms) E.DropCode(atoms);
        }
        if (engine.HostChainTable is DynChainTable table)
        {
            engine.HostChainTable = null;
            Detach(table);
        }
        E._literalPools.SweepIfDue();
    }

    /// <summary>Live engines other than <paramref name="except"/>, at most
    /// one per distinct chain table (two engines sharing a reused buffer
    /// share its table — the mutation must be applied once). Newest first.
    /// Returns null instead of an empty list on the common single-engine
    /// path so callers skip broadcast work entirely.</summary>
    internal List<Activation>? OtherLiveEnginesByTable(Activation except)
    {
        List<Activation>? result = null;
        var seen = new HashSet<DynChainTable>();
        if (GetChainTable(except) is { } exceptTable) seen.Add(exceptTable);
        for (int i = _liveEngines.Count - 1; i >= 0; i--)
        {
            if (!_liveEngines[i].TryGetTarget(out var e))
            {
                _liveEngines.RemoveAt(i);
                continue;
            }
            if (ReferenceEquals(e, except)) continue;
            if (GetChainTable(e) is not { } t || !seen.Add(t)) continue;
            (result ??= new List<Activation>()).Add(e);
            BroadcastTargets++;
        }
        return result;
    }

    /// <summary>True when <paramref name="engine"/>'s program buffer is
    /// the host's current persistent buffer — i.e. no nested query rebuilt
    /// it since this engine's setup. Capture before any
    /// <c>engine.AppendCode</c> (growth reallocation changes the engine's
    /// reference; for an owner the post-growth sync keeps host and engine
    /// aligned). A mutation by a non-owner engine still patches the
    /// engine's own buffer (self-visibility — the Logtalk compiler must
    /// see its own asserts), but the host buffer misses it, so the caller
    /// must invalidate instead of syncing.</summary>
    internal bool EngineOwnsHostBuffer(Activation engine)
        => engine.CurrentProgram is not null
           && ReferenceEquals(engine.CurrentProgram, E._persistentProgram);

    /// <summary>Post-mutation buffer bookkeeping: an owner engine's growth
    /// is synced back to the host; a non-owner (stale) engine
    /// instead marks the host buffer for rebuild — the store already holds
    /// the mutation, and the next setup re-derives dispatch from it.</summary>
    internal void SyncOrInvalidateAfterMutation(Activation engine, bool ownedHostBuffer)
    {
        if (ownedHostBuffer) SyncPersistentFromEngine(engine);
        else E.InvalidatePersistent("mutation by a non-owner");
    }

    /// <summary>synchronises <see cref="E._persistentProgram"/>
    /// back from the running engine after a mid-query
    /// <see cref="Activation.AppendCode"/> may have reallocated and grown
    /// the buffer. PrologEngine holds its own reference to the buffer
    /// for the next query's two-buffer view; without this, that
    /// reference would be left pointing at the pre-grow stale buffer.
    /// Only valid for an engine that owns the host buffer — non-owner
    /// callers go through <see cref="SyncOrInvalidateAfterMutation"/>.
    /// </summary>
    internal void SyncPersistentFromEngine(Activation engine)
    {
        if (engine.CurrentProgram is null) return;
        E._persistentProgram = engine.CurrentProgram;
        E._persistentLength = engine.ProgramLength;
    }

    /// <summary>Root fix for the suspended-activation stale-append-position
    /// corruption. An activation suspended mid-enumeration while another
    /// activation extended the shared persistent buffer still believes the
    /// content length from its own setup — its next <c>AppendCode</c> would
    /// land on the newer live entries and overwrite them; the tail patch
    /// that follows then writes the new entry's own address into its own
    /// <c>&lt;next&gt;</c> operand (the self-pointing <c>retry_me_else</c>
    /// observed as an unbreakable dispatch/walk cycle). Every in-place
    /// mutation entry point brings an owner's append position forward to
    /// the host's synced persistent length first; appends then extend
    /// instead of overwriting, and the in-place fast path stays valid.
    /// Non-owners (buffer since rebuilt) are untouched — their mutations
    /// already invalidate the host buffer.</summary>
    internal void ResyncOwnerAppendPosition(Activation engine)
    {
        if (EngineOwnsHostBuffer(engine) && engine.ProgramLength < E._persistentLength)
            engine.SetInitialProgramLength(E._persistentLength);
    }

    /// <summary>Corruption tripwire — an in-place dynamic-chain patch was
    /// about to write through addresses that don't fit the live buffer's
    /// content (a stale chain record, a stale append position, or a reused
    /// chunk swallowing a live slot). Writing would splice the chain into
    /// itself — the self-pointing <c>retry_me_else</c> observed as an
    /// unbreakable dispatch/walk cycle. Report loudly (this is a
    /// should-never-happen event worth a bug report) and rebuild the
    /// predicate's dispatch from the clause store, which is authoritative
    /// — the running query then sees the correct clause set.</summary>
    internal void ChainCorruptionRecover(
        string site, Activation engine, int functorId, string detail)
    {
        var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(functorId);
        Console.Error.WriteLine(
            $"shumway: dynamic-chain tripwire at {site} for "
            + $"{Shumway.Core.AtomTable.GetById(atomId)?.Name}/{arity} ({detail}); "
            + "rebuilding the predicate's dispatch from the clause store.");
        if (E._inFidViewRebuild) return;   // already repairing this view
        E.InvalidatePersistent();
        E.RebuildEngineFidChainView(engine, functorId);
    }
}

internal sealed class DynChainState
{
    /// <summary>The chain in order. Read everywhere and indexed positionally
    /// (a clause index is a position here), so it stays a plain list; the
    /// buckets beside it are maintained by the four mutators below and must
    /// be the only way it changes.</summary>
    public readonly List<DynChainEntry> Entries = new();

    // Dispatch-time selection asks "which clauses could match this first
    // argument", once per call. Answering it by walking the chain made that
    // O(clauses) per call, so a predicate grown and queried inside one query
    // -- which never gets the indexed recompile, since that happens at query
    // setup -- was quadratic. These give the answer in a lookup.
    private readonly Dictionary<DynFirstArgKey, List<DynChainEntry>> _byKey = new();
    private readonly List<DynChainEntry> _matchAnything = new();

    // Dead-chain reclamation asks "is any choice point sitting inside this
    // chain". It answered that by building a set of every chunk address in
    // the chain, per fire -- O(clauses) of allocation and hashing to answer a
    // question that is almost always no, which was the single biggest cost of
    // a drain. These bounds, maintained as entries come and go, let the CP
    // scan reject an address without touching the chain at all; only an
    // address that falls inside the range needs the exact set. They are a
    // conservative envelope (other predicates' chunks can lie between ours),
    // never a verdict.
    public int MinChunkAddr = int.MaxValue;
    public int MaxChunkAddr = int.MinValue;

    /// <summary>Brings an address into the envelope. The live entries do this
    /// themselves; the head and the dead chunks are added by the caller that
    /// records them.</summary>
    // Reclamation refuses a chain holding any clause emitted inside a source
    // block, because such a chunk is not individually relocatable. Answering
    // that by walking every entry, on every sweep, was O(clauses) per four
    // retracts -- invisible under 32,000 clauses and 64% of a 128,000-clause
    // drain. It is a count.
    public int SourceBlockEntries;

    // The indexed layout: retracts since its dead entries were last
    // unlinked, and the count the next sweep waits for after one a goal in
    // flight blocked (DynamicCodePatcher.NoteIndexedRetract).
    public int IndexedDead, IndexedSweepAt;

    // The indexed layout: the body chunks and the chain entries placed in the
    // buffer by the in-place asserts, by address, with their lengths. The sweep
    // drops the entries it unlinks and the bodies only those named; what the
    // layout was built with goes with the buffer.
    public Dictionary<int, int>? IndexedBodies, IndexedEntries;

    public void WidenBounds(int addr)
    {
        if (addr < 0) return;
        if (addr < MinChunkAddr) MinChunkAddr = addr;
        if (addr > MaxChunkAddr) MaxChunkAddr = addr;
    }

    // Retracting patches the died slot of every entry holding the retracted
    // clause, which meant walking the whole chain per retract. Knowing how
    // many entries hold a clause is enough to skip that walk: exactly one
    // means a position hint can be trusted outright, because there is nothing
    // else to find.
    private readonly Dictionary<Clause, int> _entriesPerClause = new();

    // Bypassing a retired entry in the bytecode must wait for the sweep (a
    // live choice point may still resume into it), so each retirement records
    // the one link that will need re-making: the address of the nearest live
    // predecessor's <next> operand, captured while the live list still knows
    // it, and the address of the retired chunk's own. The sweep replays the
    // records in order, reading the retired chunk's <next> slot for the
    // continuation -- reading it at replay time is what keeps a chunk
    // appended after the retirement connected, because the append patched
    // exactly that slot.
    public DynChainEntry? FirstLive, LastLive;
    public int LiveCount;
    public int RetiredCount;
    public readonly List<(int PrevAddr, int NextAddr)> PendingBypass = new();

    /// <summary>The indexed layout: each live clause's entry in the chain of
    /// every clause, as the address of its died slot (the execute that names
    /// the body follows it). Filled when the predicate is laid out and by the
    /// in-place asserts; a retract takes its clause out. The positional
    /// <see cref="Entries"/> do not describe that layout.</summary>
    public readonly Dictionary<Clause, int> IndexedVarEntries = new(ReferenceEqualityComparer.Instance);

    /// <summary>The single live entry holding a clause, or nothing while the
    /// clause has none or has ever had more than one -- the walk decides
    /// those. It is what lets a retract retire its entry without scanning
    /// the chain for it.</summary>
    private readonly Dictionary<Clause, DynChainEntry?> _byClause =
        new(ReferenceEqualityComparer.Instance);

    public bool TryGetSoleEntry(Clause c, out DynChainEntry? sole)
    {
        sole = null;
        if (!_byClause.TryGetValue(c, out var e) || e is null || e.Retired)
            return false;
        sole = e;
        return true;
    }

    /// <summary>Marks <paramref name="e"/> retired in place. No position
    /// moves; the entry leaves the live list, the buckets, and the sole-entry
    /// map, and the link that fed it is recorded for the sweep.</summary>
    public void RetireEntry(DynChainEntry e)
    {
        if (e.Retired) return;
        e.Retired = true;
        RetiredCount++;
        CountDown(e.Clause);
        if (e.ChunkAddr < 0) SourceBlockEntries--;
        if (_byClause.TryGetValue(e.Clause, out var known)
            && ReferenceEquals(known, e))
            _byClause.Remove(e.Clause);
        if (e.Key.MatchesEverything) _matchAnything.Remove(e);
        else if (_byKey.TryGetValue(e.Key, out var bucket))
        {
            bucket.Remove(e);
            if (bucket.Count == 0) _byKey.Remove(e.Key);
        }
        int prevAddr = e.PrevLive is { } pl ? pl.NextOperandAddr
            : HeadClauseAddr >= 0 ? HeadClauseAddr + 1 : -1;
        PendingBypass.Add((prevAddr, e.NextOperandAddr));
        if (e.PrevLive is { } pv) pv.NextLive = e.NextLive; else FirstLive = e.NextLive;
        if (e.NextLive is { } nx) nx.PrevLive = e.PrevLive; else LastLive = e.PrevLive;
        e.PrevLive = e.NextLive = null;
        LiveCount--;
    }

    /// <summary>Drops the retired entries once they reach half the list --
    /// proportional, so each pass pays for the retirements that caused it.
    /// Only with no bypass pending: the records reference entries, and the
    /// verification watermark is positional.</summary>
    public void CompactEntries()
    {
        if (RetiredCount == 0 || PendingBypass.Count > 0) return;
        int w = 0, verifiedKept = 0;
        for (int r = 0; r < Entries.Count; r++)
        {
            if (Entries[r].Retired) continue;
            // Verification is per entry and position-independent, so a
            // survivor from below the watermark is still verified at its new
            // position -- resetting to zero here made every compaction force
            // a full re-verify at the next sweep.
            if (r < VerifiedCount) verifiedKept++;
            Entries[w++] = Entries[r];
        }
        Entries.RemoveRange(w, Entries.Count - w);
        RetiredCount = 0;
        VerifiedCount = verifiedKept;
    }

    // Reclamation validates every entry's cached byte offsets against the live
    // buffer before touching it. An entry's offsets cannot move while the
    // buffer is the same array object -- growing it reallocates, and an
    // in-place patch writes bytes without moving anything -- so entries
    // already validated against this buffer stay valid, and only the ones
    // added since need looking at. Checking all of them per sweep was
    // O(clauses) on every fourth retract.
    public byte[]? VerifiedProgram;
    public int VerifiedCount;

    public void ResetVerification() { VerifiedProgram = null; VerifiedCount = 0; }

    public int EntriesHolding(Clause c)
        => _entriesPerClause.TryGetValue(c, out int n) ? n : 0;

    private void CountUp(Clause c)
        => _entriesPerClause[c] = EntriesHolding(c) + 1;

    private void CountDown(Clause c)
    {
        int n = EntriesHolding(c) - 1;
        if (n <= 0) _entriesPerClause.Remove(c);
        else _entriesPerClause[c] = n;
    }

    /// <summary>Appending patches the old tail's link itself, so it leaves no
    /// stale link behind it.</summary>
    public void AppendEntry(DynChainEntry e)
    {
        Entries.Add(e); Index(e); WidenBounds(e.ChunkAddr); CountUp(e.Clause);
        if (e.ChunkAddr < 0) SourceBlockEntries++;
        if (!_byClause.TryAdd(e.Clause, e)) _byClause[e.Clause] = null;
        e.PrevLive = LastLive;
        if (LastLive is { } t) t.NextLive = e; else FirstLive = e;
        LastLive = e;
        LiveCount++;
    }

    /// <summary>Prepending shifts every position up by one, and patches the
    /// head link itself. <paramref name="oldHeadAddr"/>: the chunk the chain
    /// started at, which now follows this one; -1 for none.</summary>
    public void PrependEntry(DynChainEntry e, int oldHeadAddr)
    {
        // A head that holds no live clause (the empty stub, or a retired
        // entry: a head retires in place, its record bypasses nothing) is
        // now behind this entry, where every dispatch would walk it. The
        // record goes after the pending ones, which settle its <next> first.
        // Without it a stack kept with asserta and retract grows the chain
        // by a dead clause per pop. A first live entry with no address
        // known counts as the head: linking past a live clause loses it.
        bool oldHeadLive = FirstLive is { } first
            && (first.ChunkAddr == oldHeadAddr || first.NextOperandAddr - 1 == oldHeadAddr
                || (first.ChunkAddr < 0 && first.NextOperandAddr <= 0));
        if (oldHeadAddr >= 0 && !oldHeadLive && e.NextOperandAddr > 0)
            PendingBypass.Add((e.NextOperandAddr, oldHeadAddr + 1));
        Entries.Insert(0, e); Index(e); WidenBounds(e.ChunkAddr); CountUp(e.Clause);
        if (e.ChunkAddr < 0) SourceBlockEntries++;
        if (!_byClause.TryAdd(e.Clause, e)) _byClause[e.Clause] = null;
        e.NextLive = FirstLive;
        if (FirstLive is { } h) h.PrevLive = e; else LastLive = e;
        FirstLive = e;
        LiveCount++;
        // The new entry is at 0, so "the first N are verified" no longer
        // describes anything; re-verify from scratch.
        VerifiedCount = 0;
    }

    public void ClearEntries()
    {
        Entries.Clear();
        _entriesPerClause.Clear();
        _byClause.Clear();
        IndexedVarEntries.Clear();
        SourceBlockEntries = 0;
        FirstLive = LastLive = null;
        LiveCount = 0;
        RetiredCount = 0;
        PendingBypass.Clear();
        ResetVerification();
        MinChunkAddr = int.MaxValue;
        MaxChunkAddr = int.MinValue;
        _byKey.Clear();
        _matchAnything.Clear();
    }

    private void Index(DynChainEntry e)
    {
        if (e.Key.MatchesEverything) { _matchAnything.Add(e); return; }
        if (!_byKey.TryGetValue(e.Key, out var bucket))
            _byKey[e.Key] = bucket = new List<DynChainEntry>();
        bucket.Add(e);
    }

    /// <summary>The clauses a call with this key could match: the ones keyed
    /// the same, plus the ones that match anything. Returns false when there
    /// is not exactly one, which is all the caller needs to know -- zero means
    /// fail outright, more than one means let the chain run.</summary>
    public bool TrySoleCandidate(DynFirstArgKey callKey, out DynChainEntry? sole)
    {
        _byKey.TryGetValue(callKey, out var bucket);
        int keyed = bucket?.Count ?? 0;
        if (keyed + _matchAnything.Count != 1) { sole = null; return false; }
        sole = keyed == 1 ? bucket![0] : _matchAnything[0];
        return true;
    }

    /// <summary>True when nothing at all could match -- the call fails without
    /// touching the chain.</summary>
    public bool NoCandidate(DynFirstArgKey callKey)
        => _matchAnything.Count == 0 && !_byKey.ContainsKey(callKey);
    /// <summary>Absolute byte position of the operand at the bytecode
    /// tail of this predicate's chain — the <c>&lt;next&gt;</c> of
    /// either the latest-appended clause's <c>retry_me_else</c>, or
    /// (for never-asserted dynamic predicates) the empty-stub clause's
    /// <c>try_me_else</c>. <c>assertz</c> writes the new clause's
    /// chunk address here to link it onto the chain, then updates
    /// this to point at the new clause's own <c>&lt;next&gt;</c>
    /// operand. -1 when the predicate's last instruction is
    /// <c>trust_me</c> (paso-3 emission without a fail-stub) — those
    /// predicates fall back to the chunk-C recompile path.</summary>
    public int TailNextAddr = -1;
    /// <summary>Absolute byte position of the trampoline's
    /// <c>execute &lt;chain-head&gt;</c> operand — the 4-byte address
    /// asserta patches to install a new chain head. -1 when there is
    /// no trampoline.</summary>
    public int TrampolineExecuteOperandAddr = -1;
    /// <summary>Absolute byte position of the current chain head's
    /// chain instruction (a <c>try_me_else</c>). asserta in-place
    /// rewrites this byte from <c>try_me_else</c> (9 bytes) to
    /// <c>retry_me_else &lt;same-next&gt;</c> (5 bytes) + 4 nops,
    /// demoting the old head into a regular middle clause without
    /// shifting anything.</summary>
    public int HeadClauseAddr = -1;

    /// <summary>Chunk-150 free-list staging: the bytecode regions
    /// of clauses that have been retracted or abolished and so are
    /// candidates for the engine-wide free list. Populated by
    /// <c>retract</c> / <c>abolish</c>; drained by
    /// <c>garbage_collect_clauses</c> into the engine-wide
    /// free-list (persistent across queries) where the
    /// next <c>assertz</c> / <c>asserta</c> can reuse the bytes
    /// instead of extending the program buffer.</summary>
    public readonly List<(int Addr, int Length)> DeadChunks = new();
}
/// <summary>dynamic-chain metadata,
/// one table per persistent buffer. Chain state records absolute byte
/// offsets into one specific buffer, but nested queries (a deferred
/// <c>:- initialization</c> goal running QueryAll from inside a
/// mid-query consult — Logtalk's loader files nest these many levels
/// deep) rebuild the persistent buffer while outer engines are still
/// executing on their older buffers. A single host-level table would
/// describe only the newest buffer — a resumed outer engine's assertz
/// would then patch new-buffer offsets into its old buffer (the
/// 0xCF / ArgumentOutOfRange corruption family), or, with the
/// staleness guards, skip the patch and lose same-query visibility of
/// its own assert (the Logtalk conditional-compilation failure). So
/// each Activation is associated at query setup with the table describing
/// its buffer, and every in-place mutation resolves chain state
/// through the engine performing it. The free-chunk list rides along
/// because its addresses are equally buffer-relative. SWI / GProlog
/// sidestep all of this with a single mutable code space; the
/// per-engine table is the equivalent alignment for our
/// snapshot-per-query model, with <see cref="_dynamicClauses"/> as
/// the authoritative store bridging buffer generations.</summary>
internal sealed class DynChainTable
{
    public readonly Dictionary<int, DynChainState> Chains = new();

    /// <summary>Chunk-150 free-list of dead-clause bytecode regions
    /// in this table's buffer. <c>garbage_collect_clauses</c> moves a
    /// predicate's <see cref="DynChainState.DeadChunks"/> here; the
    /// next <c>assertz</c> / <c>asserta</c> scans for a fit
    /// (first-fit) and reuses the bytes instead of extending the
    /// program buffer via <c>engine.AppendCode</c>.</summary>
    public readonly List<(int Addr, int Length)> FreeChunks = new();

    /// <summary>live-linked static
    /// predicates' unresolved call sites (absolute operand position in
    /// this table's buffer, callee fid), accumulated across the consult
    /// batches linked into it so a forward reference (batch N calls a
    /// predicate a later batch M&gt;N defines) is re-patched once M
    /// links. Per-buffer because the positions are buffer offsets —
    /// with the consult broadcast, batches land in several engines'
    /// buffers, each with its own positions.</summary>
    public List<(int AbsPos, int FunctorId)>? LiveConsultUnresolved;

    /// <summary>ADR-041 — trampolines materialised mid-query (live consult /
    /// runtime auto-promotion): absolute <c>enter_dynamic</c> address → functor
    /// id. Setup-emitted trampolines resolve through the per-query
    /// PredicatesByAddress map; these are appended after setup, so the clause
    /// selector needs its own lookup.</summary>
    public readonly Dictionary<int, int> TrampolineFids = new();

    /// <summary>The code units of this buffer that name pool literals, the
    /// clause chunks by address (DynamicCodePatcher.NoteCodeUnit).</summary>
    public List<byte[]>? LiteralUnits;
    public Dictionary<int, byte[]>? LiteralChunks;

    /// <summary>The atoms the code units of this buffer claim (ADR-003), the
    /// clause chunks by address with their end.</summary>
    public List<int[]>? AtomUnits;
    public Dictionary<int, (int End, int[] Atoms)>? AtomChunks;

    /// <summary>The version of each predicate laid in whole (NoteLinkedVersions).</summary>
    public Dictionary<int, LinkedVersion>? Versions;

    /// <summary>The trampoline each predicate reloaded while the buffer runs
    /// enters through: its first entry, retargeted at every reload.</summary>
    public Dictionary<int, int>? StableEntries;

    /// <summary>Code dropped from this buffer that an activation on it may still
    /// run (DynamicCodePatcher.ReleaseUnreached).</summary>
    public List<DroppedCode>? Dropped;

    /// <summary>Activations running on this buffer, and whether the host
    /// still starts queries on it: with neither, the buffer is dead.</summary>
    public int Attached;
    public bool Current = true;
}

/// <summary>ADR-015 chunk C step 4: per-dynamic-functor chain state
/// — one entry per clause currently in <see cref="_dynamicClauses"/>,
/// in the same order, carrying the absolute byte position of the
/// clause's <c>check_visible</c> died-slot in
/// <see cref="Activation.CurrentProgram"/>. <c>retract</c> patches the
/// 8-byte died slot in place; the next call's
/// <c>check_visible</c> sees the new value and skips the clause.
/// Populated after every query setup / dynamic-predicate
/// recompile.</summary>
/// <summary>Code dropped from a buffer: its range, the code whose literals it
/// references, the atoms it claims, whether its bytes go to the free list, and
/// what else it leaves to forget.</summary>
internal readonly struct DroppedCode
{
    public readonly int Start, End;
    public readonly byte[]? Literals;
    public readonly int[] Atoms;
    public readonly bool Recycle;
    public readonly Action? Forget;

    public DroppedCode(int start, int end, byte[]? literals, int[] atoms, bool recycle, Action? forget = null)
    {
        Start = start;
        End = end;
        Literals = literals;
        Atoms = atoms;
        Recycle = recycle;
        Forget = forget;
    }
}

/// <summary>A version of a predicate laid into a buffer whole: its range, the
/// atoms it claims, the code whose literals it references (a live link's), and
/// the switch-table slots and address-map entry it holds in the activation that
/// linked it.</summary>
internal sealed class LinkedVersion
{
    public readonly int Start, End;
    public readonly int[] Atoms;
    public readonly byte[]? Literals;
    public readonly List<Shumway.Core.SwitchTable>? Tables;
    public readonly int TableStart, TableCount;
    public readonly PrologEngine.LinkedProgram? Host;

    public LinkedVersion(int start, int end, int[] atoms, byte[]? literals,
        List<Shumway.Core.SwitchTable>? tables, int tableStart, int tableCount, PrologEngine.LinkedProgram? host)
    {
        Start = start;
        End = end;
        Atoms = atoms;
        Literals = literals;
        Tables = tables;
        TableStart = tableStart;
        TableCount = tableCount;
        Host = host;
    }
}

internal sealed class DynChainEntry
{
    public readonly Clause Clause;
    /// <summary>Absolute byte position of this clause's
    /// <c>check_visible</c> died slot (8 bytes). <c>retract</c>
    /// patches it in place to mark the clause logically gone.</summary>
    public int DiedOperandAddr;
    /// <summary>Absolute byte position of this clause's chain
    /// instruction's <c>&lt;next&gt;</c> address operand (4 bytes) —
    /// where its <c>try_me_else</c> or <c>retry_me_else</c> says to
    /// jump on backtrack. The last clause's points at the fail-stub;
    /// <c>assertz</c> will patch the previous-last's slot in place to
    /// link a freshly compiled clause into the chain. -1 when the
    /// clause has no chain instruction in front of it (paso-3 single-
    /// clause emission without a fail-stub address — those predicates
    /// fall back to the chunk-C recompile path).</summary>
    public int NextOperandAddr;
    /// <summary>The absolute start of this clause's bytecode chunk
    /// in the program buffer (the chain-instruction address) and
    /// the chunk's total length. Tracked so the chain
    /// GC can reclaim the bytes of dead clauses into a per-engine
    /// free-list for reuse by subsequent <c>assertz</c> /
    /// <c>asserta</c>.</summary>
    public int ChunkAddr;
    public int ChunkLength;

    /// <summary>Marked instead of removed: taking an entry out of the list
    /// shifts every position above it, which made a drain quadratic a third
    /// time, in a third structure. A retired entry keeps its slot until the
    /// list compacts, and only <see cref="DynChainState.RetireEntry"/> sets
    /// this.</summary>
    public bool Retired;

    /// <summary>The doubly linked list of live entries, in chain order. It is
    /// what makes retiring O(1): the nearest live predecessor -- whose
    /// <c>next</c> operand is the one link the sweep will have to re-make --
    /// is one hop away at the moment of retirement, instead of a walk away
    /// at the moment of the sweep.</summary>
    public DynChainEntry? PrevLive, NextLive;

    public DynChainEntry(Clause c, int died, int next, int chunkAddr, int chunkLength)
    {
        Clause = c;
        DiedOperandAddr = died;
        NextOperandAddr = next;
        ChunkAddr = chunkAddr;
        ChunkLength = chunkLength;
        Key = DynFirstArgKey.Of(c);
    }

    /// <summary>The clause's first-argument key, decided once here rather
    /// than re-derived on every dispatch -- interning the head atom per entry
    /// per call was most of what the selection cost.</summary>
    public readonly DynFirstArgKey Key;
}

/// <summary>A dynamic clause's first argument, reduced to what dispatch-time
/// selection needs: enough to prove a call cannot match, and
/// <see cref="Anything"/> for every shape that cannot prove it. The kinds
/// mirror ADR-041's cases exactly; adding one that is not provable would make
/// the selection wrong, not just slower.</summary>
internal readonly record struct DynFirstArgKey(byte Kind, long Value)
{
    public const byte AnythingKind = 0, AtomKind = 1, IntKind = 2,
                      ListKind = 3, StructKind = 4;

    public static readonly DynFirstArgKey Anything = new(AnythingKind, 0);
    public bool MatchesEverything => Kind == AnythingKind;

    /// <summary>The key of a clause's head first argument.</summary>
    /// <remarks>Every shape that cannot prove a mismatch must map to
    /// <see cref="Anything"/>. Getting that wrong makes the selection wrong,
    /// not merely slower: a clause left out of a call's candidates is a
    /// solution that never runs.</remarks>
    public static DynFirstArgKey Of(Clause c)
    {
        if (c.HeadImage is { } image) return OfImage(image);
        Term head = c.Term is CompoundTerm { Functor: ":-", Args: [var h, _] }
            ? h : c.Term;
        if (head is not CompoundTerm hc || hc.Args.Length == 0) return Anything;
        switch (hc.Args[0])
        {
            case AtomTerm a:
                return new(AtomKind, AtomTable.Intern(a.Name).Id);
            case IntTerm i:
                return new(IntKind, i.Value);
            case CompoundTerm { Functor: ".", Args.Length: 2 }:
                return new(ListKind, 0);
            case CompoundTerm cc:
                // Functor and arity, not just "some compound": Logtalk's
                // per-entity `_def` tables are chains keyed by distinct goal
                // templates (precision(_), order(_), ...), and keying them
                // together would leave the whole chain multi-candidate.
                return new(StructKind,
                    ((long)AtomTable.Intern(cc.Functor).Id << 32)
                    | (uint)cc.Args.Length);
            default:
                return Anything;    // var / float / bigint / unkeyed head shapes
        }
    }

    /// <summary>The key of a fact kept as the image of its head (ADR-063), from
    /// the cell of its first argument: the cases of <see cref="OfCall"/>, a
    /// packed string keyed as the list it is.</summary>
    private static DynFirstArgKey OfImage(Shumway.Core.TermImage image)
    {
        var cells = image.Cells;
        if (cells.Length < 2 || FunctorTable.Lookup(cells[0].AsFunctorId).Arity == 0) return Anything;
        Cell a = cells[1];
        switch (a.Tag)
        {
            case Tag.Atom:
                return new(AtomKind, a.AsAtomId);
            case Tag.Int:
                return new(IntKind, a.AsInt);
            case Tag.Lis:
                return new(ListKind, 0);
            case Tag.Str:
            {
                var (aid, ar) = FunctorTable.Lookup(cells[a.AsHeapIndex].AsFunctorId);
                return new(StructKind, ((long)aid << 32) | (uint)ar);
            }
            case Tag.Ref:
                // A variable or a float keys nothing; a packed string is a list.
                return cells[a.AsHeapIndex].Tag == Tag.Pstr ? new(ListKind, 0) : Anything;
            default:
                return Anything;
        }
    }

    /// <summary>The key a CALL's first argument selects, or null when the
    /// argument's tag proves nothing -- a float or a bigint keys no clause, so
    /// every entry stays a candidate and the selection has to decline.</summary>
    public static DynFirstArgKey? OfCall(Activation engine, Cell callArg)
        => callArg.Tag switch
        {
            Tag.Atom => new DynFirstArgKey(AtomKind, callArg.AsAtomId),
            Tag.Int => new DynFirstArgKey(IntKind, callArg.AsInt),
            Tag.Lis or Tag.Pstr => new DynFirstArgKey(ListKind, 0),
            Tag.Str => StructCallKey(engine, callArg),
            _ => null,
        };

    private static DynFirstArgKey StructCallKey(Activation engine, Cell callArg)
    {
        var (aid, ar) = FunctorTable.Lookup(
            engine.GetHeap(callArg.AsHeapIndex).AsFunctorId);
        return new DynFirstArgKey(StructKind, ((long)aid << 32) | (uint)ar);
    }
}
