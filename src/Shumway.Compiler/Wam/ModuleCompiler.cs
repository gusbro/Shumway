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

        var predicates = new List<CompiledPredicate>(order.Count);
        var predicateCompiler = new PredicateCompiler();
        foreach (int fid in order)
            predicates.Add(predicateCompiler.Compile(groups[fid]));

        return new CompiledModule(predicates);
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
