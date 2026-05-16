namespace Shumway.Compiler.Wam;

/// <summary>
/// The bytecode and metadata produced by compiling a single Prolog clause. The
/// bytecode is self-contained: it starts with the clause's head-matching
/// instructions (in the order of the head's arguments) and ends with a
/// <c>proceed</c>. A caller wires it into a program by placing the bytecode at
/// some address and emitting a <c>call</c> instruction targeting that address.
///
/// <para>Multi-clause predicates and indexing are not in scope for chunk 8a — a
/// later chunk introduces a <c>PredicateCompiler</c> that wraps several
/// <see cref="CompiledClause"/>s with the <c>try_me_else</c>/<c>retry_me_else</c>/
/// <c>trust_me</c> chain.</para>
/// </summary>
public sealed class CompiledClause
{
    public byte[] Bytecode { get; }

    /// <summary>The functor id (interned in <see cref="Shumway.Core.FunctorTable"/>)
    /// identifying the head's name and arity.</summary>
    public int FunctorId { get; }

    public int Arity { get; }

    /// <summary>The number of X registers the clause uses, including the argument
    /// registers. Useful when sizing or auditing the engine's register array.</summary>
    public int RegisterCount { get; }

    public CompiledClause(byte[] bytecode, int functorId, int arity, int registerCount)
    {
        Bytecode = bytecode;
        FunctorId = functorId;
        Arity = arity;
        RegisterCount = registerCount;
    }
}
