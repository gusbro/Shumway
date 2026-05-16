namespace Shumway.Compiler.Wam;

/// <summary>
/// The compiled bytecode for one predicate — all clauses with the same functor
/// and arity, wrapped (if there are two or more) by the WAM choice-point
/// dispatch instructions <c>try_me_else</c>, <c>retry_me_else</c>, and
/// <c>trust_me</c>. A single-clause predicate has no wrapping; its bytecode is
/// exactly its sole <see cref="CompiledClause"/>'s bytes.
///
/// <para><see cref="CallSites"/> contains every <c>call</c> / <c>execute</c>
/// instruction from any of the predicate's clauses, with offsets translated
/// from clause-local to predicate-local. A <see cref="Linker"/> then shifts
/// those once more when concatenating predicates into a program.</para>
/// </summary>
public sealed class CompiledPredicate
{
    public byte[] Bytecode { get; }
    public int FunctorId { get; }
    public int Arity { get; }
    public int ClauseCount { get; }
    public IReadOnlyList<CallSite> CallSites { get; }

    /// <summary>Byte offsets (inside <see cref="Bytecode"/>) of the four-byte BP
    /// operands embedded in <c>try_me_else</c> and <c>retry_me_else</c> opcodes.
    /// Each site currently holds a predicate-local address; the linker shifts
    /// them by the predicate's base position so the runtime sees absolute
    /// program addresses.</summary>
    public IReadOnlyList<int> DispatchSites { get; }

    public CompiledPredicate(
        byte[] bytecode,
        int functorId,
        int arity,
        int clauseCount,
        IReadOnlyList<CallSite> callSites,
        IReadOnlyList<int> dispatchSites)
    {
        Bytecode = bytecode;
        FunctorId = functorId;
        Arity = arity;
        ClauseCount = clauseCount;
        CallSites = callSites;
        DispatchSites = dispatchSites;
    }
}
