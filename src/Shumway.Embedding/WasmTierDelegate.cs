using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>The wasm tier's <c>PredicateDelegate</c>: a CHAIN driver over the
/// store's group module. One chain stages the engine areas once (pin or
/// copy, fill the mailbox) and runs; in-group calls never leave the module
/// (group compilation turned them into internal jumps), so what remains here
/// is the entry itself, builtins, deopt, and hops to OUT-of-group callees.
///
/// The interpreter contract is unchanged at the edges: Success leaves Cp for
/// the caller; a tail call to a non-wasm callee or a deopt sets Pc +
/// <see cref="Activation.IlTailCallPending"/> and returns true; Fail returns
/// false and backtracking re-enters wasm choice points through their marker
/// BPs. Marker payloads are (functor, ADDRESS) -- stable across group
/// rebuilds -- and the world translates them to the current build's cursors.</summary>
public sealed class WasmTierDelegate
{
    private readonly int _functorId;
    private readonly IWasmExecutionWorld _world;

    public WasmTierDelegate(int functorId, IWasmExecutionWorld world)
    {
        _functorId = functorId;
        _world = world;
    }

    /// <summary>Diagnostic tallies (all delegates, process-wide): chain
    /// entries, in-chain module switches, deopts, builtin requests, and exits
    /// to the interpreter for tail calls it must dispatch. Not on any hot
    /// path decision; plain longs.</summary>
    public static long DiagEntries, DiagSwitches, DiagDeopts, DiagBuiltins, DiagTailExits;
    /// <summary>Requests per builtin id — which builtins actually cost a
    /// chain exit, to decide what earns open-coding. Diagnostic only.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<int, long>
        DiagBuiltinTally = new();
    /// <summary>Deopt PCs with a HIT COUNT each, for attribution: knowing
    /// where a storm falls is only half of it, the ranking is what says
    /// which instruction to open-code next. Parallel arrays scanned
    /// linearly -- this runs on the deopt path, which already pays a full
    /// image staging, so a probe over a few dozen longs is free by
    /// comparison. -1 = unused slot; PCs past the table land in
    /// <see cref="DiagDeoptOverflow"/>.</summary>
    public static readonly long[] DiagDeoptPcs = FreshPcTable();
    public static readonly long[] DiagDeoptHits = new long[64];

    // -1, not 0: pc 0 is a legal address, and a table left at its default
    // would make every slot look OCCUPIED by it -- no site is ever claimed
    // and every deopt lands in the overflow. ResetDiag is not enough; a
    // browser session never calls it.
    private static long[] FreshPcTable()
    {
        var a = new long[64];
        for (int i = 0; i < a.Length; i++) a[i] = -1;
        return a;
    }
    /// <summary>Deopts whose PC did not fit the table.</summary>
    public static long DiagDeoptOverflow;
    /// <summary>Key slots at the FIRST deopt: flags, TR, trail limit, H,
    /// watermark, ST, stack limit. Null until one fires.</summary>
    public static long[]? DiagFirstDeoptSlots;

    /// <summary>Diagnostic tripwire: scan the heap for an AttVar cell with
    /// no attr-table record after every delegate return. Off by default.</summary>
    public static bool DiagOrphanScan;

    public static void ResetDiag()
    {
        DiagEntries = DiagSwitches = DiagDeopts = DiagBuiltins = DiagTailExits = 0;
        DiagBuiltinTally.Clear();
        for (int i = 0; i < DiagDeoptPcs.Length; i++) { DiagDeoptPcs[i] = -1; DiagDeoptHits[i] = 0; }
        DiagDeoptOverflow = 0;
        DiagFirstDeoptSlots = null;
        DiagFirstRestoreGuard = null;
    }


    /// <summary>The two values the restore path's extra-trail guard compares
    /// at the FIRST deopt: the top saved in the choice point (ctl[5]) and the
    /// live one. They are supposed to be equal whenever nothing attributed
    /// was bound; a run whose every Trust steps aside says they are not, and
    /// the pair below says which side is wrong. Captured once, so nothing
    /// here is on a hot path.</summary>
    public static long[]? DiagFirstRestoreGuard;
    /// <summary>Capture the guard only at this pc (0 = never). Diagnostic.</summary>
    public static int DiagGuardPc;

    private static void CaptureRestoreGuard(Activation engine, IWasmChainContext cx)
    {
        int b = engine.B;
        var stack = engine.WasmStackView;
        if (b < 0 || b >= stack.Length) return;
        // [arity | A1..Aarity | CE CP B BP bindingTop extraTop heapTop ...]
        int arity = (int)stack[b].Data;
        int ctl = b + 1 + arity;
        if ((uint)(ctl + 5) >= (uint)stack.Length) return;
        DiagFirstRestoreGuard ??= new long[8];
        var cap = new[]
        {
            b, arity,
            stack[ctl + 5].Data,                    // the raw saved cell
            (long)(int)stack[ctl + 5].Data,         // ...as the emitter reads it
            engine.ExtraTrailTop,                   // the live top, managed
            cx.ReadSlot(WasmAbi.ExtraTrailTop),     // ...as the mailbox has it
            cx.ReadSlot(WasmAbi.DiagA),             // what the GUARD saw: saved
            cx.ReadSlot(WasmAbi.DiagB),             // what the GUARD saw: live
        };
        System.Array.Copy(cap, DiagFirstRestoreGuard, 8);
    }

    private static void NoteDeoptPc(long pc)
    {
        for (int i = 0; i < DiagDeoptPcs.Length; i++)
        {
            if (DiagDeoptPcs[i] == pc) { DiagDeoptHits[i]++; return; }
            if (DiagDeoptPcs[i] == -1) { DiagDeoptPcs[i] = pc; DiagDeoptHits[i] = 1; return; }
        }
        DiagDeoptOverflow++;
    }

    /// <summary>The deopt sites, heaviest first: (pc, hits). Diagnostic.</summary>
    public static List<(long Pc, long Hits)> DeoptRanking()
    {
        var r = new List<(long, long)>();
        for (int i = 0; i < DiagDeoptPcs.Length; i++)
            if (DiagDeoptPcs[i] >= 0) r.Add((DiagDeoptPcs[i], DiagDeoptHits[i]));
        r.Sort((x, y) => y.Item2.CompareTo(x.Item2));
        return r;
    }

    /// <summary>The delegate entry. <paramref name="address"/> is a marker
    /// payload: 0 for a fresh call, a biased bytecode address for a resume
    /// or a retry.</summary>
    public bool Invoke(Activation engine, int address)
    {
        if (!engine.WasmModeCompatible || engine.HasPendingWakeups)
        {
            // A relink may have moved the code out from under the build:
            // the fallback pc must be LIVE (see IWasmExecutionWorld's
            // translation contract).
            engine.SetPc(address == 0
                ? _world.LiveEntryAddressOf(_functorId)
                : (int)_world.TranslatePcToLive(address));
            engine.IlTailCallPending = true;
            return true;
        }
        DiagEntries++;
        int currentFid = _functorId;
        bool result;
        int pendingPc = int.MinValue;
        bool growTrail = false, growStack = false;
        using (var cx = _world.BeginChain(engine))
        {
            if (!cx.TryResolve(currentFid, address, out int cursor))
            {
                // The captured build predates this functor (a nested rebuild
                // raced the entry): run its bytecode this once.
                engine.SetPc(address == 0
                    ? _world.LiveEntryAddressOf(_functorId)
                    : (int)_world.TranslatePcToLive(address));
                engine.IlTailCallPending = true;
                return true;
            }
            while (true)
            {
                WasmVerdict v = cx.Call(cursor);
                if (v == WasmVerdict.Success)
                {
                    int cp = (int)cx.ReadSlot(WasmAbi.ContinuationPc);
                    if (Activation.IsResumeMarker(cp)
                        && TryChain(cx, engine, cp, ref currentFid, ref cursor))
                        continue;
                    result = true;      // the interpreter proceeds at Cp
                    break;
                }
                if (v == WasmVerdict.SuccessTailCall)
                {
                    int pc = (int)cx.ReadSlot(WasmAbi.Pc);
                    if (Activation.IsResumeMarker(pc)
                        && TryChain(cx, engine, pc, ref currentFid, ref cursor))
                        continue;
                    DiagTailExits++;
                    // A marker passes through symbolically; a raw bytecode
                    // address is build-space and must move to live space.
                    pendingPc = Activation.IsResumeMarker(pc)
                        ? pc : (int)cx.TranslatePcToLive(pc);
                    result = true;
                    break;
                }
                if (v == WasmVerdict.Deopt)
                {
                    DiagDeopts++;
                    // The module baked this pc when the group was compiled;
                    // a consult since then relinked the program and moved
                    // the code (a stale pc here was the "bytecode
                    // corruption" crash). Translate to the live space.
                    pendingPc = (int)cx.TranslatePcToLive(cx.ReadSlot(WasmAbi.Pc));
                    NoteDeoptPc(pendingPc);
                    // A deopt AT an area's limit is a capacity signal, not a
                    // semantic one. The wasm limit sits a margin below the
                    // real array, so the interpreter completes the step
                    // INSIDE that margin and never grows the area — leaving
                    // every later chain to deopt at the same spot (measured:
                    // 108 of tak's 114 entries, all at one pc). Note it here;
                    // the growth runs after the chain closes.
                    growTrail = cx.ReadSlot(WasmAbi.TrailTop) >= cx.ReadSlot(WasmAbi.TrailLimit);
                    growStack = cx.ReadSlot(WasmAbi.StackTop) >= cx.ReadSlot(WasmAbi.StackLimit);
                    if (DiagFirstDeoptSlots is null)
                        DiagFirstDeoptSlots = new[]
                        {
                            cx.ReadSlot(WasmAbi.Flags),
                            cx.ReadSlot(WasmAbi.TrailTop),
                            cx.ReadSlot(WasmAbi.TrailLimit),
                            cx.ReadSlot(WasmAbi.HeapTop),
                            cx.ReadSlot(WasmAbi.HeapWatermark),
                            cx.ReadSlot(WasmAbi.StackTop),
                            cx.ReadSlot(WasmAbi.StackLimit),
                        };
                    if (DiagGuardPc != 0 && pendingPc == DiagGuardPc)
                        CaptureRestoreGuard(engine, cx);
                    result = true;
                    break;
                }
                if (v == WasmVerdict.Fail)
                {
                    result = false;     // backtracking re-enters via marker BPs
                    break;
                }
                if (v != WasmVerdict.BuiltinRequest)
                    throw new System.InvalidOperationException(
                        $"wasm verdict {v} for functor {currentFid}");

                DiagBuiltins++;
                long req = cx.ReadSlot(WasmAbi.BuiltinId);
                int builtinId = (int)(uint)req;
                DiagBuiltinTally.AddOrUpdate(builtinId, 1, (_, n) => n + 1);
                int trim = (int)(req >> 32);
                int ret = (int)cx.ReadSlot(WasmAbi.Cursor);
                // The builtin runs against the ENGINE: adopt the mailbox
                // first, restage after -- managed code may bind, allocate,
                // even replace an area array by growing it.
                cx.SyncEngine();
                var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
                engine.Inferences++;
                // Mirrors the interpreter's CallBuiltin: trim BEFORE the impl
                // so any choice point it pushes lands at the trimmed top
                // (execute_builtin, ret -1, never trims).
                if (ret >= 0) engine.TrimEnv(trim);
                engine.CurrentBuiltinName = entry.Name;
                engine.CurrentBuiltinArity = entry.Arity;
                engine.BuiltinReturnPc = ret >= 0
                    ? Activation.EncodeResumeMarker(currentFid, ret)
                    : engine.Cp;
                bool ok;
                Profiler.BuiltinEnter(builtinId);
                try { ok = entry.Impl(engine); }
                catch (PrologRuntimeException re)
                {
                    re.StampBuiltin(entry.Name, entry.Arity);
                    throw;
                }
                finally { Profiler.BuiltinExit(builtinId); }
                if (!ok) { result = false; break; }
                if (ret < 0)
                {
                    // Tail position: proceed. A backtrackable impl that chose
                    // its own resume left IlTailCallPending + Pc; either way
                    // the interpreter's post-delegate handling is right.
                    result = true;
                    break;
                }
                cx.RefreshFromEngine();
                if (!cx.TryResolve(currentFid, ret, out cursor))
                    throw new System.InvalidOperationException(
                        $"builtin resume address {ret} unknown to the build");
            }
        }
        // After the chain closed (the engine is authoritative again): give
        // the area the room the wasm ran out of, so the next chain stages a
        // bigger image instead of deopting at the same spot.
        if (growTrail) engine.GrowWasmBindingTrail();
        if (growStack) engine.GrowWasmStack();
        if (DiagOrphanScan && engine.FindOrphanAttVar() is int orphan and >= 0)
            throw new System.InvalidOperationException(
                $"orphan AttVar after delegate: fid={_functorId} "
                + $"entry-fid={currentFid} heap[{orphan}] result={result}");
        if (pendingPc != int.MinValue)
        {
            engine.SetPc(pendingPc);
            engine.IlTailCallPending = true;
        }
        return result;
    }

    /// <summary>Whether the marker can be followed inside the chain: the
    /// functor is in the build THIS chain captured AND the chain guards
    /// hold. The guards are the boundary work the interpreter would have
    /// done: heap watermark (collect at a return boundary), cancellation,
    /// and pending wakeups (only a builtin can queue them mid-chain; they
    /// must drain at the next goal boundary, which the interpreter owns).</summary>
    private static bool TryChain(IWasmChainContext cx, Activation engine, int marker,
                                 ref int currentFid, ref int cursor)
    {
        var (fid, address) = Activation.DecodeResumeMarker(marker);
        if (!cx.TryResolve(fid, address, out int c)) return false;
        if (cx.ReadSlot(WasmAbi.HeapTop) >= cx.ReadSlot(WasmAbi.HeapWatermark)) return false;
        if (engine.IsCancellationRequested || engine.HasPendingWakeups) return false;
        DiagSwitches++;
        currentFid = fid;
        cursor = c;
        return true;
    }
}
