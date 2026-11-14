using Shumway.Compiler.Ast;

namespace Shumway.Compiler.Parsing;

/// <summary>Splits a large predicate at the changes of its head constants: each
/// run of consecutive clauses that carry the same constants in the same
/// argument positions becomes a predicate of its own, and the original keeps
/// one clause per run, <c>p(.., c, ..) :- '$keyN_p'(.., c, ..)</c>, whose head
/// carries the run's constants. The clauses are tried in the order written and
/// each still unifies its whole head, so the answers and their order do not
/// change; what changes is that each run is small enough to be compiled as a
/// Tier-1 region with its callees.
///
/// <para>Only a predicate none of whose clauses reads its cut barrier: a
/// <c>!</c> in a run's clause would commit the run and leave the alternatives
/// of the original predicate behind it. And only one whose compound head
/// arguments are the same structure in every clause at a position: the
/// dispatching clause holds a variable there, and where the structures differ
/// the original's index tells clauses apart that the dispatcher could not,
/// leaving a choice point the original would not.</para></summary>
public static class KeySplit
{
    /// <summary>The weight (term nodes of its clauses) from which a predicate is
    /// split; 0 = never.</summary>
    public static int MinWeight { get; set; } = ParseEnv("SHUMWAY_KEY_SPLIT");

    private static readonly bool Trace =
        Environment.GetEnvironmentVariable("SHUMWAY_KEY_SPLIT_TRACE") == "1";

    private static int ParseEnv(string name)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int v) && v > 0 ? v : 0;

    /// <summary>The clauses with every eligible predicate split: a run's
    /// dispatching clause where the run's first clause was, and the runs'
    /// predicates after everything else. The list itself when nothing was
    /// split.</summary>
    public static List<Clause> Apply(List<Clause> clauses, Func<string, int, bool>? eligible = null)
    {
        if (MinWeight <= 0 && !Trace) return clauses;

        var groups = new Dictionary<(string, int), List<int>>();
        for (int i = 0; i < clauses.Count; i++)
        {
            if (HeadOf(clauses[i]) is not { } head) continue;
            var key = (head.Functor, head.Args.Length);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<int>();
            list.Add(i);
        }

        // By clause index: the clause that replaces it (null = dropped, it
        // moved to a run's predicate).
        Dictionary<int, Clause?>? replaced = null;
        List<Clause>? runs = null;
        foreach (var group in groups)
        {
            var (name, arity) = group.Key;
            var members = group.Value;
            if (members.Count < 3 || name.Length == 0 || name[0] == '$' || name == ":") continue;
            if (eligible is not null && !eligible(name, arity)) continue;
            if (PlanRuns(clauses, members, arity, out int weight) is not { } plan) continue;
            if (Trace)
                Console.Error.WriteLine(
                    $"[key-split] {name}/{arity} clauses={members.Count} weight={weight} runs="
                    + string.Join(",", plan.Select(r => r.Count)));
            if (MinWeight <= 0 || weight < MinWeight) continue;

            replaced ??= new Dictionary<int, Clause?>();
            runs ??= new List<Clause>();
            int serial = 0;
            foreach (var run in plan)
            {
                if (run.Count == 1) continue;   // stays a clause of the predicate
                string runName = $"$key{++serial}_{name}";
                var first = clauses[run[0]];
                replaced[run[0]] = Dispatching(first, name, runName, arity);
                for (int k = 0; k < run.Count; k++)
                {
                    if (k > 0) replaced[run[k]] = null;
                    runs.Add(Renamed(clauses[run[k]], runName));
                }
            }
        }
        if (replaced is null) return clauses;

        var result = new List<Clause>(clauses.Count + runs!.Count);
        for (int i = 0; i < clauses.Count; i++)
        {
            if (!replaced.TryGetValue(i, out var by)) result.Add(clauses[i]);
            else if (by is not null) result.Add(by);
        }
        result.AddRange(runs);
        return result;
    }

    private static CompoundTerm? HeadOf(Clause clause)
    {
        if (!clause.HasTerm) return null;
        return clause.Kind switch
        {
            ClauseKind.Fact => clause.Term as CompoundTerm,
            ClauseKind.Rule => clause.Term is CompoundTerm { Functor: ":-", Args.Length: 2 } rule
                ? rule.Args[0] as CompoundTerm
                : null,
            _ => null,
        };
    }

    /// <summary>The predicate's clauses in runs of equal head constants, or null
    /// when it is not to be split: a clause reads the cut barrier, a head holds
    /// something the dispatching clause cannot stand for, or no two clauses
    /// make a run.</summary>
    private static List<List<int>>? PlanRuns(
        List<Clause> clauses, List<int> members, int arity, out int weight)
    {
        weight = 0;
        var shape = new (string? Functor, int Arity)[arity];   // of the compound at a position
        var seen = new bool[arity];
        var keys = new object?[members.Count][];
        for (int m = 0; m < members.Count; m++)
        {
            var clause = clauses[members[m]];
            var head = HeadOf(clause)!;
            if (clause.Kind == ClauseKind.Rule)
            {
                var body = ((CompoundTerm)clause.Term).Args[1];
                if (ReadsCutBarrier(body)) return null;
                weight += Weight(body);
            }
            var key = keys[m] = new object?[arity];
            for (int a = 0; a < arity; a++)
            {
                var arg = head.Args[a];
                weight += Weight(arg);
                (string?, int) here = default;
                switch (arg)
                {
                    case AtomTerm atom: key[a] = atom.Name; break;
                    case IntTerm n: key[a] = n.Value; break;
                    case VarTerm: break;
                    case CompoundTerm c when c.CycleId is null: here = (c.Functor, c.Args.Length); break;
                    default: return null;
                }
                if (seen[a] && shape[a] != here) return null;
                seen[a] = true;
                shape[a] = here;
            }
        }

        var plan = new List<List<int>>();
        bool any = false;
        for (int m = 0; m < members.Count; m++)
        {
            if (m > 0 && SameKeys(keys[m - 1], keys[m]))
            {
                plan[plan.Count - 1].Add(members[m]);
                any = true;
            }
            else plan.Add(new List<int> { members[m] });
        }
        return any && plan.Count >= 2 ? plan : null;
    }

    private static bool SameKeys(object?[] a, object?[] b)
    {
        for (int i = 0; i < a.Length; i++)
            if (!Equals(a[i], b[i])) return false;
        return true;
    }

    /// <summary>True when a body commits to its clause: a <c>!</c> where the
    /// clause sees it, or the barrier MetaTransform captures for the ones in
    /// the arms of a control construct.</summary>
    private static bool ReadsCutBarrier(Term body)
    {
        var pending = new Stack<Term>();
        pending.Push(body);
        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case AtomTerm { Name: "!" }:
                    return true;
                case CompoundTerm { Functor: "$get_cut_barrier" or "$choice_level" }:
                    return true;
                case CompoundTerm { Functor: "$call", Args.Length: 2 } call
                    when call.Args[0] is AtomTerm { Name: "!" }:
                    return true;
                case CompoundTerm { Functor: "," or ";" or "->" or "*->", Args.Length: 2 } c:
                    pending.Push(c.Args[1]);
                    pending.Push(c.Args[0]);
                    break;
            }
        }
        return false;
    }

    private static int Weight(Term term)
    {
        int n = 0;
        var pending = new Stack<Term>();
        pending.Push(term);
        while (pending.Count > 0)
        {
            n++;
            if (pending.Pop() is CompoundTerm c)
                foreach (var arg in c.Args) pending.Push(arg);
        }
        return n;
    }

    // p(A1, c, A3) :- run(A1, c, A3): a constant every clause of the run
    // carries, a variable elsewhere.
    private static Clause Dispatching(Clause first, string name, string runName, int arity)
    {
        var head = HeadOf(first)!;
        var args = new Term[arity];
        for (int a = 0; a < arity; a++)
            args[a] = head.Args[a] is AtomTerm or IntTerm
                ? head.Args[a]
                : new VarTerm($"A{a + 1}");
        var position = first.Term.Position;
        var term = new CompoundTerm(":-", new Term[]
        {
            new CompoundTerm(name, args) { Position = head.Position },
            new CompoundTerm(runName, args) { Position = head.Position },
        }) { Position = position };
        return new Clause(ClauseKind.Rule, term, first.Position);
    }

    private static Clause Renamed(Clause clause, string runName)
    {
        var head = HeadOf(clause)!;
        var newHead = new CompoundTerm(runName, head.Args) { Position = head.Position };
        if (clause.Kind == ClauseKind.Fact)
            return new Clause(ClauseKind.Fact, newHead, clause.Position);
        var rule = (CompoundTerm)clause.Term;
        var term = new CompoundTerm(":-", new[] { newHead, rule.Args[1] }) { Position = rule.Position };
        return new Clause(ClauseKind.Rule, term, clause.Position);
    }
}
