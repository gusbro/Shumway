using System.Collections.Generic;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>One flag value. Flags hold an ATOMIC value (SWI: a number or an
/// atom). Stored as a managed value — a number as a <see cref="Number"/>, an atom
/// as its id — rather than a heap <see cref="Cell"/>, so it survives across
/// queries: a Cell holding a heap index (Float / BigInt) would dangle once the
/// per-query heap unwinds. The counter/accumulator use that motivates flags is
/// integer-dominated, but this keeps floats and bigints correct too.</summary>
public readonly struct FlagValue
{
    public readonly bool IsAtom;
    public readonly int AtomId;
    public readonly Number Num;

    private FlagValue(bool isAtom, int atomId, Number num)
    {
        IsAtom = isAtom;
        AtomId = atomId;
        Num = num;
    }

    public static FlagValue OfAtom(int atomId) => new(true, atomId, default);
    public static FlagValue OfNumber(Number n) => new(false, 0, n);

    /// <summary>The default value of an unset flag — the integer 0 (SWI).</summary>
    public static FlagValue Zero => OfNumber(new Number(0L));

    /// <summary>Materialises the value as a fresh heap <see cref="Cell"/> in the
    /// current activation (numbers allocate on the live heap; an atom is inline).</summary>
    public Cell ToCell(Activation engine) => IsAtom ? Cell.Atom(AtomId) : Num.ToCell(engine);
}

/// <summary>The per-engine <c>flag/3</c> store: a global, non-backtrackable
/// key → value map. Its own namespace, distinct from the
/// <see cref="GlobalVarStore"/> (SWI keeps flags and global variables separate).
/// A key is an atom OR a ground compound (SWI's <c>library(gensym)</c> keys on
/// <c>gensym(Base)</c>), canonicalised to a string by <see cref="FlagBuiltins"/>;
/// an unset key reads as 0.</summary>
public sealed class FlagStore
{
    private readonly Dictionary<string, FlagValue> _flags = new();

    public FlagValue Get(string key) =>
        _flags.TryGetValue(key, out var v) ? v : FlagValue.Zero;

    public void Set(string key, FlagValue value) => _flags[key] = value;
}

/// <summary>Host-side interface so <see cref="FlagBuiltins"/> can reach the
/// per-engine <see cref="FlagStore"/> without the Builtins project depending on
/// Embedding. Implemented by <c>PrologEngine</c>.</summary>
public interface IFlagHost
{
    FlagStore FlagStore { get; }
}
