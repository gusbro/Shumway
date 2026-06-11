namespace Shumway.Core;

/// <summary>Chunk 417 — the ISO <c>unknown</c> prolog flag's runtime action,
/// mirrored onto <see cref="Engine.OnUnknown"/> by the embedding layer (at
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
    public static bool Fails(Engine engine, int functorId)
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
                throw PrologRuntimeException.UndefinedProcedure(functorId);
        }
    }
}
