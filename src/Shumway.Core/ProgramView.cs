namespace Shumway.Core;

/// <summary>
/// The bytecode buffer an activation runs: one array, whose offsets are the
/// code addresses. The prefix, the static link, the dynamic region, the code
/// appended while queries run (live links, clause chunks) and each query's own
/// code all lie in it; the query's code is appended at setup and freed when
/// the query ends (ADR-015). A single array keeps every read a plain index.
/// </summary>
public readonly struct ProgramView
{
    public readonly byte[] Primary;

    public ProgramView(byte[] primary)
    {
        // A null primary is allowed through — the caller validates
        // before reading. This mirrors the prior `byte[]?` parameter
        // shape so implicit conversion from a null byte[] doesn't
        // NRE in the ctor.
        Primary = primary;
    }

    public byte this[int idx] => Primary[idx];

    /// <summary>The array's capacity: the slack past the live code is zero
    /// bytes (the Invalid opcode), so a stray pc there fails loudly.</summary>
    public int Length => Primary.Length;

    /// <summary>Implicit conversion from a bare <c>byte[]</c>.</summary>
    public static implicit operator ProgramView(byte[] primary) =>
        new ProgramView(primary);
}
