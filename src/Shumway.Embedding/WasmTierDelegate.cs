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
    /// <summary>The first few distinct deopt PCs, for attribution: a deopt
    /// storm names its instruction. -1 = unused slot.</summary>
    public static readonly long[] DiagDeoptPcs = new long[8];
    /// <summary>Key slots at the FIRST deopt: flags, TR, trail limit, H,
    /// watermark, ST, stack limit. Null until one fires.</summary>
    public static long[]? DiagFirstDeoptSlots;

    public static void ResetDiag()
    {
        DiagEntries = DiagSwitches = DiagDeopts = DiagBuiltins = DiagTailExits = 0;
        for (int i = 0; i < DiagDeoptPcs.Length; i++) DiagDeoptPcs[i] = -1;
        DiagFirstDeoptSlots = null;
    }

    private static void NoteDeoptPc(long pc)
    {
        for (int i = 0; i < DiagDeoptPcs.Length; i++)
        {
            if (DiagDeoptPcs[i] == pc) return;
            if (DiagDeoptPcs[i] == -1) { DiagDeoptPcs[i] = pc; return; }
        }
    }

    /// <summary>The delegate entry. <paramref name="address"/> is a marker
    /// payload: 0 for a fresh call, a biased bytecode address for a resume
    /// or a retry.</summary>
    public bool Invoke(Activation engine, int address)
    {
        if (!engine.WasmModeCompatible || engine.HasPendingWakeups)
        {
            engine.SetPc(address == 0 ? _world.EntryAddressOf(_functorId) : address);
            engine.IlTailCallPending = true;
            return true;
        }
        DiagEntries++;
        int currentFid = _functorId;
        bool result;
        int pendingPc = int.MinValue;
        using (var cx = _world.BeginChain(engine))
        {
            if (!cx.TryResolve(currentFid, address, out int cursor))
            {
                // The captured build predates this functor (a nested rebuild
                // raced the entry): run its bytecode this once.
                engine.SetPc(address == 0 ? _world.EntryAddressOf(_functorId) : address);
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
                    pendingPc = pc;     // non-wasm callee: the interpreter dispatches
                    result = true;
                    break;
                }
                if (v == WasmVerdict.Deopt)
                {
                    DiagDeopts++;
                    pendingPc = (int)cx.ReadSlot(WasmAbi.Pc);
                    NoteDeoptPc(pendingPc);
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
