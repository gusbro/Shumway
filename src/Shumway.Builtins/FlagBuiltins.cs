using System;
using Shumway.Core;

namespace Shumway.Builtins;

/// <summary>SWI-style <c>flag/3</c> — a global integer/atom flag with an atomic
/// read-modify-write. <c>flag(Key, Old, New)</c> unifies <c>Old</c> with the
/// current value (0 if never set), then stores <c>New</c> (an arithmetic
/// expression is evaluated — <c>flag(c, X, X+1)</c> — so <c>Old</c> must bind
/// before <c>New</c> is read). Changes are NOT backtracked, so a flag survives
/// as a counter across a failure-driven loop (which is exactly how
/// <c>library(gensym)</c> generates fresh atoms).</summary>
public static class FlagBuiltins
{
    /// <summary><c>flag(+Key, ?Old, +New)</c>.</summary>
    public static bool Flag3(Activation engine)
    {
        string key = KeyString(engine, engine.GetRegister(0));
        var store = Flags(engine);
        FlagValue current = store.Get(key);
        // Bind Old to the current value BEFORE evaluating New, so an expression
        // over Old (`flag(K, X, X+1)`) sees the bound value.
        if (!engine.UnifyRegisterWithCell(1, current.ToCell(engine))) return false;
        store.Set(key, EvalNewValue(engine, engine.GetRegister(2)));
        return true;
    }

    /// <summary><c>set_flag(+Key, +Value)</c> — set a flag, discarding the old
    /// value.</summary>
    public static bool SetFlag2(Activation engine)
    {
        string key = KeyString(engine, engine.GetRegister(0));
        Flags(engine).Set(key, EvalNewValue(engine, engine.GetRegister(1)));
        return true;
    }

    /// <summary><c>get_flag(+Key, -Value)</c> — read a flag (0 if never set).</summary>
    public static bool GetFlag2(Activation engine)
    {
        string key = KeyString(engine, engine.GetRegister(0));
        return engine.UnifyRegisterWithCell(1, Flags(engine).Get(key).ToCell(engine));
    }

    // ---------- helpers ----------

    /// <summary>The new value: a bare (non-arithmetic-constant) atom is stored
    /// verbatim; anything else is evaluated as an arithmetic expression.</summary>
    private static FlagValue EvalNewValue(Activation engine, Cell newCell)
    {
        Cell d = Resolve(engine, newCell);
        if (d.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        if (d.Tag == Tag.Atom)
            return FlagValue.OfAtom(d.AsAtomId);
        return FlagValue.OfNumber(ArithmeticEvaluator.Evaluate(engine, d));
    }

    private static FlagStore Flags(Activation engine)
    {
        if (engine.Host is not IFlagHost host)
            throw new InvalidOperationException(
                "flag/3 requires the engine to be hosted by a type exposing a FlagStore.");
        return host.FlagStore;
    }

    /// <summary>Canonicalises a flag key (an atom or a ground compound — SWI's
    /// gensym keys on <c>gensym(Base)</c>) to a stable string. An arity marker
    /// distinguishes a compound from a same-named atom.</summary>
    private static string KeyString(Activation engine, Cell cell)
    {
        var sb = new System.Text.StringBuilder();
        BuildKey(engine, cell, sb);
        return sb.ToString();
    }

    private static void BuildKey(Activation engine, Cell cell, System.Text.StringBuilder sb)
    {
        Cell d = Resolve(engine, cell);
        switch (d.Tag)
        {
            case Tag.Ref:
                throw new PrologRuntimeException("instantiation_error");
            case Tag.Atom:
                sb.Append(AtomTable.GetById(d.AsAtomId)?.Name ?? "").Append("/0");
                return;
            case Tag.Int:
                sb.Append('#').Append(d.AsInt);
                return;
            case Tag.Str:
            {
                int functorIdx = d.AsHeapIndex;
                var (atomId, arity) = FunctorTable.Lookup(engine.GetHeap(functorIdx).AsFunctorId);
                sb.Append(AtomTable.GetById(atomId)?.Name ?? "").Append('/').Append(arity).Append('(');
                for (int i = 0; i < arity; i++)
                {
                    if (i > 0) sb.Append(',');
                    BuildKey(engine, engine.GetHeap(functorIdx + 1 + i), sb);
                }
                sb.Append(')');
                return;
            }
            default:
                // A number/string flag key is unusual; reject cleanly.
                throw new PrologRuntimeException("type_error", "atom_or_compound");
        }
    }

    private static Cell Resolve(Activation engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        return engine.GetHeap(engine.Deref(c.AsHeapIndex));
    }
}
