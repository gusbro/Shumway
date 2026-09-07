using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>The wasm tier's <c>PredicateDelegate</c>: the verdict loop over a
/// compiled module, mirroring the interpreter's dispatch contract cell for
/// cell. Success leaves Cp for the caller (the interpreter proceeds there);
/// a tail call or a deopt sets Pc + <see cref="Activation.IlTailCallPending"/>
/// and returns true -- the interpreter continues at Pc, which for a deopt is
/// the bytecode address of the very instruction, so the predicate runs on as
/// if it had never been compiled. Fail returns false and the interpreter's
/// backtracking pops the top choice point; a wasm-pushed one carries a resume
/// marker BP that re-enters this delegate at its retry cursor.</summary>
public sealed class WasmTierDelegate
{
    private readonly int _functorId;
    private readonly IWasmActivationRunner _runner;
    // cursor -> linked bytecode address, for the mode-incompatible fallback
    // (trail-everything / occurs_check): run the entry on the interpreter.
    private readonly int[] _linkedAddressByCursor;

    public WasmTierDelegate(int functorId, IWasmActivationRunner runner,
                            int[] linkedAddressByCursor)
    {
        _functorId = functorId;
        _runner = runner;
        _linkedAddressByCursor = linkedAddressByCursor;
    }

    /// <summary>Diagnostic verdict tallies (all delegates, process-wide):
    /// entries, deopts, builtin requests. A probe reads these to attribute a
    /// slow case -- a high deopt count means the predicate keeps stepping
    /// aside (watermark, attvar) rather than running native. Not on any hot
    /// path decision; plain longs, last-writer-wins is fine for a counter.</summary>
    public static long DiagEntries, DiagDeopts, DiagBuiltins, DiagTailCalls;

    public static void ResetDiag() { DiagEntries = DiagDeopts = DiagBuiltins = DiagTailCalls = 0; }

    public bool Invoke(Activation engine, int cursor)
    {
        if (!engine.WasmModeCompatible)
        {
            engine.SetPc(_linkedAddressByCursor[cursor]);
            engine.IlTailCallPending = true;
            return true;
        }
        DiagEntries++;
        while (true)
        {
            WasmVerdict v = _runner.Run(engine, cursor);
            switch (v)
            {
                case WasmVerdict.Success:
                    return true;
                case WasmVerdict.SuccessTailCall:
                case WasmVerdict.Deopt:
                    if (v == WasmVerdict.Deopt) DiagDeopts++; else DiagTailCalls++;
                    engine.SetPc((int)_runner.ReadSlot(WasmAbi.Pc));
                    engine.IlTailCallPending = true;
                    return true;
                case WasmVerdict.Fail:
                    return false;
                case WasmVerdict.BuiltinRequest:
                {
                    DiagBuiltins++;
                    long req = _runner.ReadSlot(WasmAbi.BuiltinId);
                    int builtinId = (int)(uint)req;
                    int trim = (int)(req >> 32);
                    int ret = (int)_runner.ReadSlot(WasmAbi.Cursor);
                    var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
                    engine.Inferences++;
                    // Mirrors the interpreter's CallBuiltin: trim BEFORE the
                    // impl so any choice point it pushes lands at the trimmed
                    // top (execute_builtin, ret -1, never trims).
                    if (ret >= 0) engine.TrimEnv(trim);
                    engine.CurrentBuiltinName = entry.Name;
                    engine.CurrentBuiltinArity = entry.Arity;
                    engine.BuiltinReturnPc = ret >= 0
                        ? Activation.EncodeResumeMarker(_functorId, ret)
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
                    if (!ok) return false;
                    if (ret < 0)
                        // Tail position: proceed. A backtrackable impl that
                        // chose its own resume left IlTailCallPending + Pc;
                        // either way the interpreter's post-delegate handling
                        // does the right thing.
                        return true;
                    cursor = ret;
                    continue;
                }
                default:
                    throw new System.InvalidOperationException(
                        $"wasm verdict {v} for functor {_functorId}");
            }
        }
    }
}
