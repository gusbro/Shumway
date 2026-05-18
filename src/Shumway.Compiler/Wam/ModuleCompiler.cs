using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Compiler.Wam;

/// <summary>
/// Compiles a Prolog source's clause stream into a <see cref="CompiledModule"/>.
/// Clauses are grouped by functor (name + arity), and each group is handed to
/// <see cref="PredicateCompiler"/>. Source order is preserved across the whole
/// module: a predicate's slot in <see cref="CompiledModule.Predicates"/> matches
/// the position of its first clause in the source, and its clauses are tried in
/// the order they were written.
///
/// <para>Directives encountered in the stream are <em>not</em> emitted as
/// bytecode — they were already processed by <see cref="ClauseReader"/> (the
/// <c>:- op</c> case mutates the operator table in place) and are simply
/// skipped here.</para>
/// </summary>
public sealed class ModuleCompiler
{
    public CompiledModule Compile(IEnumerable<Clause> clauses)
        => Compile(clauses, cache: null);

    /// <summary><paramref name="cache"/> short-circuits compilation: any
    /// predicate-group whose functor id is in the cache <em>and</em> whose
    /// cached <see cref="CompiledPredicate"/> doesn't reference per-module
    /// literal-pool indices reuses the cached bytecode verbatim instead of
    /// running <see cref="PredicateCompiler"/> over the source clauses.
    /// This is the Tier-0 half of the bundle pipeline's skip-compile path
    /// (chunk 55) — a loaded bundle's <c>CompiledPredicate</c>s can be
    /// re-served at query setup without the consulted-source round trip.
    /// Cache misses (functor not in the cache, or the cached predicate
    /// uses literals whose indices wouldn't survive a fresh pool) fall
    /// through to normal compilation.</summary>
    public CompiledModule Compile(
        IEnumerable<Clause> clauses,
        IReadOnlyDictionary<int, CompiledPredicate>? cache)
    {
        ArgumentNullException.ThrowIfNull(clauses);

        // Group by functor in first-occurrence order. The order matters: when
        // we emit the program, predicates appear in the order the source
        // introduced them, which is the natural debugging-friendly order.
        var groups = new Dictionary<int, List<Clause>>();
        var order = new List<int>();

        foreach (var clause in clauses)
        {
            if (clause.Kind == ClauseKind.Directive)
                continue;   // already executed by ClauseReader

            int functorId = GetFunctorId(clause);
            if (!groups.TryGetValue(functorId, out var list))
            {
                list = new List<Clause>();
                groups[functorId] = list;
                order.Add(functorId);
            }
            list.Add(clause);
        }

        var stringLiterals = new LiteralPool<string>();
        var floatLiterals = new LiteralPool<double>();
        var bigIntLiterals = new LiteralPool<System.Numerics.BigInteger>();

        var predicates = new List<CompiledPredicate>(order.Count);
        var predicateCompiler = new PredicateCompiler();
        foreach (int fid in order)
        {
            if (cache is not null
                && cache.TryGetValue(fid, out var cached)
                && IsCachedPredicateReusable(cached))
            {
                predicates.Add(cached);
                continue;
            }
            predicates.Add(predicateCompiler.Compile(
                groups[fid], stringLiterals, floatLiterals, bigIntLiterals));
        }

        return new CompiledModule(
            predicates,
            stringLiterals.Snapshot(),
            floatLiterals.Snapshot(),
            bigIntLiterals.Snapshot());
    }

    /// <summary>True iff <paramref name="pred"/>'s bytecode references no
    /// per-module literal pool (string / float / big-integer) — i.e. it
    /// can be lifted into a freshly-compiled module without its
    /// <see cref="OperandKind.LiteralId"/> operands needing to be
    /// re-keyed to the new pools. Atom ids, functor ids, and builtin
    /// ids are all globally interned so they don't need this guard.</summary>
    private static bool IsCachedPredicateReusable(CompiledPredicate pred)
    {
        byte[] code = pred.Bytecode;
        int pc = 0;
        while (pc < code.Length)
        {
            byte opByte = code[pc];
            var info = OpcodeTable.Get(opByte);
            if (!info.IsDefined || info.Size == 0) return false;
            if (info.OperandKinds is not null)
            {
                for (int i = 0; i < info.OperandKinds.Length; i++)
                {
                    if (info.OperandKinds[i] == OperandKind.LiteralId)
                        return false;
                }
            }
            pc += info.Size;
        }
        return true;
    }

    private static int GetFunctorId(Clause clause)
    {
        // For facts and rules the head is the term (or the :-/2's first arg).
        Term head = clause.Kind == ClauseKind.Rule
            ? ((CompoundTerm)clause.Term).Args[0]
            : clause.Term;

        switch (head)
        {
            case AtomTerm a:
                return FunctorTable.Intern(AtomTable.Intern(a.Name, permanent: true).Id, 0);
            case CompoundTerm c:
                return FunctorTable.Intern(AtomTable.Intern(c.Functor, permanent: true).Id, c.Args.Length);
            default:
                throw new InvalidOperationException(
                    $"Clause head must be an atom or compound, got {head.GetType().Name}.");
        }
    }
}
