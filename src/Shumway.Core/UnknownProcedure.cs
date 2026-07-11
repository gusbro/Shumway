namespace Shumway.Core;

/// <summary>Chunk 417 — the ISO <c>unknown</c> prolog flag's runtime action,
/// mirrored onto <see cref="Activation.OnUnknown"/> by the embedding layer (at
/// query setup from the engine flags, and live when
/// <c>set_prolog_flag(unknown, _)</c> runs mid-query).</summary>
public enum UnknownAction : byte
{
    /// <summary>ISO default: raise <c>existence_error(procedure, Name/Arity)</c>.</summary>
    Error = 0,

    /// <summary>Fail silently (classic DEC-10 / Arity behaviour).</summary>
    Fail = 1,

    /// <summary>Print a warning to standard error, then fail.</summary>
    Warning = 2,
}

/// <summary>Chunk 417 — the single decision point for a call to an undefined
/// procedure. Every dispatch path that used to throw
/// <see cref="PrologRuntimeException.UndefinedProcedure"/> unconditionally
/// now asks <see cref="Fails"/>: under <c>unknown=error</c> it throws exactly
/// as before; under <c>fail</c>/<c>warning</c> it returns true and the caller
/// fails through its normal backtrack path.</summary>
public static class UnknownProcedure
{
    /// <summary>Returns true when the caller should FAIL (flag is
    /// <c>fail</c>, or <c>warning</c> after printing one); throws the ISO
    /// <c>existence_error</c> when the flag is <c>error</c>.</summary>
    public static bool Fails(Activation engine, int functorId)
    {
        switch (engine.OnUnknown)
        {
            case UnknownAction.Fail:
                return true;
            case UnknownAction.Warning:
                var (atomId, arity) = FunctorTable.Lookup(functorId);
                System.Console.Error.WriteLine(
                    $"warning: unknown procedure {AtomTable.GetById(atomId)?.Name ?? "?"}/{arity}");
                return true;
            default:
                if (System.Environment.GetEnvironmentVariable("SHUMWAY_UNDEF_DIAG") == "1")
                {
                    var (aid2, ar2) = FunctorTable.Lookup(functorId);
                    string inMap = "no-map";
                    if (engine.CurrentFunctorAddresses is { } m2)
                        inMap = m2.TryGetValue(functorId, out int a3)
                            ? $"map={a3} progByte=0x{(engine.CurrentProgram is { } p2 && a3 >= 0 && a3 < p2.Length ? p2[a3] : 0xEE):X2}"
                            : "not-in-map";
                    bool vis = engine.LiveConsultVisibleFids?.Contains(functorId) ?? false;
                    System.Console.Error.WriteLine(
                        $"[UNDEF] {AtomTable.GetById(aid2)?.Name}/{ar2} pc={engine.P}"
                        + $" caller={engine.ResolveAddressToLabel?.Invoke(engine.P) ?? "?"}"
                        + $" {inMap} visible={vis}");
                }
                throw PrologRuntimeException.UndefinedProcedure(functorId);
        }
    }
}
