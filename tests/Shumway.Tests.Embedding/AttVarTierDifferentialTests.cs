using Shumway.Builtins;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>Tier-0 is the oracle: every attributed-variable shape below must
/// answer the same with the IL tier promoted, and must leave no ORPHAN attvar
/// behind — a cell tagged AttVar with no attr-table record, which is what a
/// tier that COPIES an attvar cell produces (Deref does not follow AttVar, so
/// a copy elsewhere is a variable the attribute machinery cannot see).
///
/// <para>The corpus walks the instructions an attvar can reach a tier
/// through: get_value (head unification against a bound arg), unify_value
/// and unify_variable (inside a structure), argument passing, and the
/// wakeup path a binding triggers.</para></summary>
public sealed class AttVarTierDifferentialTests(ITestOutputHelper o)
{
    // '$orphan_attvar'(-Addr): the first heap cell tagged AttVar with no
    // record, or -1. Registered once; the registry deduplicates by functor.
    private static readonly object _regLock = new();
    private static bool _registered;

    private static void EnsureProbe()
    {
        lock (_regLock)
        {
            if (_registered) return;
            StandardBuiltins.EnsureRegistered();
            BuiltinsRegistry.Register("$orphan_attvar", 1,
                engine => engine.UnifyRegisterWithCell(
                    0, Cell.Int(engine.FindOrphanAttVar())));
            _registered = true;
        }
    }

    private const string Corpus = """
        :- use_module(library(coroutining)).
        :- public samep/2.
        :- public wrap/2.
        :- public unwrap/2.
        :- public through/3.
        :- public twice/2.
        samep(X, X).
        wrap(X, w(X)).
        unwrap(w(X), X).
        through(X, Y, p(X, Y)).
        twice(X, pair(X, X)).
        """;

    /// <summary>(query, what it exercises). Each runs on both tiers.</summary>
    public static TheoryData<string, string> Shapes() => new()
    {
        { "put_attr(X, m, hello), samep(X, Y), get_attr(Y, m, V), V == hello.",
          "get_value: attvar as the bound side" },
        { "put_attr(X, m, hello), samep(Y, X), get_attr(Y, m, V), V == hello.",
          "get_value: attvar as the free side" },
        { "put_attr(X, m, hello), wrap(X, W), unwrap(W, Y), get_attr(Y, m, V), V == hello.",
          "unify_variable / unify_value through a structure" },
        { "put_attr(X, m, a), twice(X, P), P = pair(A, B), get_attr(A, m, V), get_attr(B, m, W), V == W.",
          "one attvar reaching two argument positions" },
        { "put_attr(X, m, a), through(X, X, T), T = p(L, R), L == R.",
          "the same attvar twice in one build" },
        { "put_attr(X, m, a), samep(X, Y), Y = bound, X == bound.",
          "binding through the alias" },
        { "put_attr(X, m, a), samep(X, Y), put_attr(Y, m, b), get_attr(X, m, V), V == b.",
          "writing the attribute through the alias" },
        { "put_attr(X, m, a), del_attr(X, m), samep(X, Y), ( get_attr(Y, m, _) -> fail ; true ).",
          "del_attr then alias" },
        { "put_attr(X, m, a), copy_term(X, Y), ( get_attr(Y, m, _) -> fail ; true ).",
          "a copy drops attributes (engine rule)" },
        { "put_attr(X, m, a), copy_term(X, Y, Gs), Gs == [].",
          "copy_term/3 with a hookless module" },
        { "dif(X, a), samep(X, Y), Y = b.",
          "dif through get_value, satisfied" },
        { "dif(X, a), samep(X, Y), ( Y = a -> fail ; true ).",
          "dif through get_value, violated" },
        { "freeze(X, true), samep(X, Y), Y = 1, X == 1.",
          "freeze woken through the alias" },
        { "freeze(X, fail), samep(X, Y), ( Y = 1 -> fail ; true ).",
          "a woken goal that fails undoes the binding" },
        { "put_attr(X, m, a), findall(Z, samep(X, Z), [_]).",
          "attvar through findall's copy" },
        { "put_attr(X, m, a), samep(X, Y), '$orphan_attvar'(O), O == -1.",
          "no orphan after aliasing" },
    };

    private static PrologEngine Tier0()
    {
        EnsureProbe();
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 0;          // the oracle: no promotion at all
        e.ConsultString(Corpus);
        return e;
    }

    private static PrologEngine Il()
    {
        EnsureProbe();
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;          // promote on the first dispatch
        e.ConsultString(Corpus);
        return e;
    }

    private static (bool Ok, string Detail) Run(PrologEngine e, string goal)
    {
        try
        {
            var r = e.Query(goal);
            if (!r.Success) return (false, "fail");
            var parts = new List<string>();
            foreach (var (name, value) in r.Bindings)
                parts.Add($"{name}={value}");
            parts.Sort(System.StringComparer.Ordinal);
            return (true, string.Join(",", parts));
        }
        catch (System.Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Tier0AndIlAgree(string goal, string what)
    {
        var t0 = Tier0();
        var il = Il();

        // Warm the IL engine so the predicates the goal touches are promoted,
        // then run the comparison on a promoted tier.
        _ = Run(il, goal);
        var a = Run(t0, goal);
        var b = Run(il, goal);

        // ANTI-VACUITY: without a promotion this compares Tier-0 with
        // Tier-0 and proves nothing. Every goal here calls one of the
        // corpus predicates, so at least one must have promoted.
        var promoted = new List<string>();
        foreach (var (name, arity) in new[]
                 { ("samep", 2), ("wrap", 2), ("unwrap", 2), ("through", 3), ("twice", 2) })
        {
            int fid = FunctorTable.Intern(AtomTable.Intern(name).Id, arity);
            if (il.IlPromotion.IsPromoted(fid)) promoted.Add($"{name}/{arity}");
        }
        o.WriteLine($"  promoted: [{string.Join(" ", promoted)}]");
        bool callsCorpus = goal.Contains("samep(") || goal.Contains("wrap(")
            || goal.Contains("unwrap(") || goal.Contains("through(")
            || goal.Contains("twice(");
        if (callsCorpus)
            Assert.True(promoted.Count > 0,
                "nothing promoted: this compares Tier-0 with Tier-0");
        // A goal that RAISES on both tiers agrees vacuously — the corpus is
        // meant to run, so an error is a broken shape, not a result.
        Assert.False(a.Detail.Contains("Exception"), $"tier0 raised: {a.Detail}");
        o.WriteLine($"{what}\n  tier0: {a.Ok} {a.Detail}\n  il   : {b.Ok} {b.Detail}");
        Assert.Equal(a.Ok, b.Ok);
        Assert.Equal(a.Detail, b.Detail);

        // And neither tier may leave an orphan behind.
        foreach (var (label, engine) in new[] { ("tier0", t0), ("il", il) })
        {
            var probe = engine.Query("'$orphan_attvar'(O).");
            Assert.True(probe.Success, $"{label}: the probe must answer");
            Assert.Equal("-1", probe.Bindings["O"].ToString());
        }
    }
}
