namespace Shumway.Core;

/// <summary>
/// Encoding of a <c>call</c> / <c>execute</c> target operand for a callee
/// the linker could not resolve.
///
/// <para>A resolved target is the callee's byte address inside the linked
/// program — always non-negative. An <em>unresolved</em> target (the
/// callee predicate has no clauses anywhere in the program) is instead a
/// negative sentinel that carries the callee's functor id. The linker
/// patches one in rather than failing the link, so an undefined predicate
/// raises <c>existence_error(procedure, Name/Arity)</c> only when the call
/// is actually reached — the ISO behaviour — and an undefined predicate in
/// one part of a program no longer breaks unrelated queries.</para>
/// </summary>
public static class CallTarget
{
    /// <summary>The sentinel target for a call to undefined functor
    /// <paramref name="functorId"/>. Always negative: functor ids are
    /// non-negative and so are real addresses, so the sign bit cleanly
    /// distinguishes the two.</summary>
    public static int ForUndefined(int functorId) => -1 - functorId;

    /// <summary>True when <paramref name="target"/> is an unresolved-call
    /// sentinel rather than a real program address.</summary>
    public static bool IsUnresolved(int target) => target < 0;

    /// <summary>Recovers the undefined callee's functor id from a sentinel
    /// produced by <see cref="ForUndefined"/>.</summary>
    public static int FunctorIdOf(int target) => -1 - target;
}
