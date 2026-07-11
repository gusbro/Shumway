namespace Shumway.Core;

/// <summary>
/// A virtual view of one or two physically-distinct bytecode buffers,
/// laid out contiguously in a single address space. Used by chunk 151
/// (Phase 10) to give the interpreter a uniform <c>code[pc]</c>
/// interface while letting the embedding layer split the bytecode
/// across a persistent program region (prefix + static + dynamic)
/// and a per-query overlay (the synthetic query clause and its
/// helpers).
///
/// <para>The split offset <see cref="Split"/> tells the indexer
/// where the boundary is: <c>code[pc]</c> reads from
/// <see cref="Primary"/> when <c>pc &lt; Split</c>, from
/// <see cref="Overflow"/> at offset <c>pc - Split</c> otherwise. With
/// <c>Overflow == null</c> the view is just a thin wrapper over
/// <c>Primary</c> — the chunk-151a starting state, behaviour-
/// preserving so the per-query rebuild keeps working while we land
/// the interpreter / Activation rewiring.</para>
///
/// <para>The struct is a <c>readonly ref struct</c>-friendly value
/// (just three fields) — the JIT inlines the indexer and hoists
/// <see cref="Split"/> into a register on the hot dispatch loop, so
/// the dispatch cost over the original direct <c>byte[]</c> access
/// is a single bounds-style branch.</para>
/// </summary>
public readonly struct ProgramView
{
    public readonly byte[] Primary;
    public readonly byte[]? Overflow;
    public readonly int Split;

    /// <summary>Single-buffer view — the common case during chunk 151a
    /// rollout, before the persistent / per-query split is in place.</summary>
    public ProgramView(byte[] primary)
    {
        Primary = primary;
        Overflow = null;
        // A null primary is allowed through — the caller validates
        // before reading. This mirrors the prior `byte[]?` parameter
        // shape so implicit conversion from a null byte[] doesn't
        // NRE in the ctor.
        Split = primary?.Length ?? 0;
    }

    /// <summary>Two-buffer view; <paramref name="split"/> is the
    /// address at which <paramref name="overflow"/> begins.</summary>
    public ProgramView(byte[] primary, byte[] overflow, int split)
    {
        Primary = primary;
        Overflow = overflow;
        Split = split;
    }

    public byte this[int idx] =>
        idx < Split ? Primary[idx] : Overflow![idx - Split];

    public int Length =>
        Overflow is null ? Primary.Length : Split + Overflow.Length;

    /// <summary>True when the view is a single-buffer wrapper. Hot-path
    /// helpers can take the fast direct-array branch when this is true,
    /// which is the steady state until a query is in flight.</summary>
    public bool IsSingleBuffer => Overflow is null;

    /// <summary>Implicit conversion from a bare <c>byte[]</c> — keeps
    /// the chunk-151a transition trivial: any caller still passing a
    /// raw program array just works.</summary>
    public static implicit operator ProgramView(byte[] primary) =>
        new ProgramView(primary);
}
