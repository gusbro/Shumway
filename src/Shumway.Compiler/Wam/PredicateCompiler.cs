using Shumway.Compiler.Ast;

namespace Shumway.Compiler.Wam;

/// <summary>
/// Compiles all clauses of a single predicate into one <see cref="CompiledPredicate"/>.
/// The clauses must share a functor name and arity; callers (typically
/// <see cref="ModuleCompiler"/>) are responsible for grouping a source file's
/// flat clause stream by functor before invoking this.
///
/// <para><b>Single-clause case.</b> If there's exactly one clause the predicate's
/// bytecode is just that clause's bytes — no dispatch wrapping is needed because
/// no alternative exists to backtrack to.</para>
///
/// <para><b>Multi-clause case.</b> Each clause's body is inlined between the
/// standard try-me-else / retry-me-else / trust-me sequence:</para>
/// <code>
///   try_me_else  BP_1, arity      ; address of retry/trust before clause 2
///   &lt;clause 1 body&gt;
///   retry_me_else BP_2             ; address of retry/trust before clause 3
///   &lt;clause 2 body&gt;
///   ...
///   trust_me                       ; final alternative — discards the CP
///   &lt;clause N body&gt;
/// </code>
/// Dispatch-instruction sizes are fixed (try_me_else = 9, retry_me_else = 5,
/// trust_me = 1), so we can compute every BP and clause start in one pass over
/// the precompiled clauses before emitting anything.
///
/// <para>Each clause's <c>CallSites</c> are translated from clause-local offsets
/// to predicate-local offsets and copied to the aggregate
/// <see cref="CompiledPredicate.CallSites"/> list — the <see cref="Linker"/>
/// shifts them again to module-absolute addresses.</para>
/// </summary>
public sealed class PredicateCompiler
{
    public CompiledPredicate Compile(IReadOnlyList<Clause> clauses)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        if (clauses.Count == 0)
            throw new ArgumentException("At least one clause is required.", nameof(clauses));

        // Compile each clause independently.
        var compiledClauses = new List<CompiledClause>(clauses.Count);
        var compiler = new ClauseCompiler();
        foreach (var c in clauses) compiledClauses.Add(compiler.Compile(c));

        // Verify all clauses share the same functor signature.
        int functorId = compiledClauses[0].FunctorId;
        int arity = compiledClauses[0].Arity;
        for (int i = 1; i < compiledClauses.Count; i++)
        {
            if (compiledClauses[i].FunctorId != functorId)
                throw new ArgumentException(
                    "All clauses passed to PredicateCompiler must share the same functor "
                    + $"(clause 0 = id {functorId}, clause {i} = id {compiledClauses[i].FunctorId}).");
        }

        // Single-clause shortcut.
        if (compiledClauses.Count == 1)
        {
            return new CompiledPredicate(
                compiledClauses[0].Bytecode,
                functorId,
                arity,
                clauseCount: 1,
                callSites: compiledClauses[0].CallSites,
                dispatchSites: Array.Empty<int>());
        }

        // Multi-clause layout: pass 1 computes the offset of every dispatch
        // instruction and every clause body so the BP operand for each
        // try/retry can be written out in pass 2 without back-patching.
        int n = compiledClauses.Count;
        int[] clauseBodyOffsets = new int[n];
        int pos = 0;
        for (int i = 0; i < n; i++)
        {
            int dispatchSize = i == 0
                ? 9                          // try_me_else
                : i == n - 1
                    ? 1                      // trust_me
                    : 5;                     // retry_me_else
            pos += dispatchSize;
            clauseBodyOffsets[i] = pos;
            pos += compiledClauses[i].Bytecode.Length;
        }
        // Each clause's dispatch starts immediately before its body.
        // For clause i, dispatch lives at clauseBodyOffsets[i] - dispatchSize(i).
        // The BP that the previous clause's retry/try points at is the start of
        // the next clause's dispatch — which is clauseBodyOffsets[i+1] minus
        // dispatch_size(i+1). Simpler: dispatch start = clauseBodyOffsets[i] - dispatchSize(i).
        // We just need clauseBodyOffsets — dispatch starts are derived as needed.

        // Pass 2: emit.
        var emitter = new BytecodeEmitter();
        var callSites = new List<CallSite>();
        var dispatchSites = new List<int>();
        for (int i = 0; i < n; i++)
        {
            // Emit this clause's dispatch instruction.
            if (i == 0)
            {
                int nextDispatch = clauseBodyOffsets[1] - DispatchSizeFor(1, n);
                int opPos = emitter.Position;
                emitter.EmitTryMeElse(nextDispatch, arity);
                dispatchSites.Add(opPos + 1);   // BP operand is the 4 bytes after the opcode
            }
            else if (i == n - 1)
            {
                emitter.EmitTrustMe();
                // trust_me has no BP operand — nothing to track.
            }
            else
            {
                int nextDispatch = clauseBodyOffsets[i + 1] - DispatchSizeFor(i + 1, n);
                int opPos = emitter.Position;
                emitter.EmitRetryMeElse(nextDispatch);
                dispatchSites.Add(opPos + 1);
            }

            // Append the clause body and translate its call sites.
            int clauseStart = emitter.Position;
            emitter.AppendBytes(compiledClauses[i].Bytecode);
            foreach (var site in compiledClauses[i].CallSites)
                callSites.Add(new CallSite(
                    clauseStart + site.OpcodeOffset,
                    site.CalleeFunctorId,
                    site.IsExecute));
        }

        return new CompiledPredicate(
            emitter.ToBytes(),
            functorId,
            arity,
            clauseCount: n,
            callSites: callSites,
            dispatchSites: dispatchSites);
    }

    private static int DispatchSizeFor(int clauseIndex, int totalClauses) =>
        clauseIndex == 0
            ? 9
            : clauseIndex == totalClauses - 1
                ? 1
                : 5;
}
