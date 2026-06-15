using System.IO;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Differential harness for baking the prelude as Tier-1 IL. Every public
/// prelude predicate is exercised with the SAME query two ways — Tier-0 WAM
/// (promotion off) and Tier-1 IL (promotion forced on the first call) — and
/// BOTH the full solution set AND any captured output must be byte-identical.
///
/// <para>Crucially, each case also asserts the predicate-under-test actually
/// PROMOTED to IL (<see cref="IlPromotionStore.IsPromoted"/>), so a silent
/// WAM-only run can never mask a divergence — the whole point is to catch a
/// sub_atom-class latent IL miscompile BEFORE <c>--strip-wam</c> drops the WAM
/// fallback. A predicate that legitimately cannot be IL (none of the public
/// surface today) would be listed in <see cref="WamOnly"/> with a reason.</para>
///
/// <para>IL promotion is synchronous (IlPromotionStore compiles inline once the
/// call count crosses Threshold), so the IL run warms up once (promoting the
/// whole call tree) and then re-runs the measured query under IL. Pure cases
/// reuse the query as their warm-up; side-effecting cases supply a benign
/// warm-up so the measured run starts from clean state.</para>
/// </summary>
public sealed class PreludeIlDifferentialTests
{
    // Shared helper predicates every engine gets, so cases can name a goal
    // (maplist/foldl/include/… all take a callable).
    private const string Helpers =
        "even(X) :- 0 is X mod 2.\n" +
        "dbl(X, Y) :- Y is X * 2.\n" +
        "add3(A, B, C) :- C is A + B.\n" +
        "foldadd(E, A, B) :- B is A + E.\n" +
        "foldadd2(E1, E2, A, B) :- B is A + E1 + E2.\n" +
        "cmp3(O, A, B) :- compare(O, A, B).\n" +
        "inc(A, B) :- B is A + 1.\n" +
        "f1(1). f1(2). f1(3).\n";

    public sealed record Case(
        string Pred, string Query, string Setup = "", string? Warmup = null, bool Output = false);

    public static readonly Case[] AllCases =
    {
        // ---- list utilities ----
        new("member/2", "member(X, [1, 2, 3])"),
        new("memberchk/2", "memberchk(2, [1, 2, 3])"),
        new("select/3", "select(X, [a, b, c], R)"),
        new("permutation/2", "permutation([1, 2, 3], P)"),
        new("subtract/3", "subtract([1, 2, 3, 4], [2, 4], R)"),
        new("intersection/3", "intersection([1, 2, 3], [2, 3, 4], R)"),
        new("union/3", "union([1, 2], [2, 3, 4], R)"),
        new("delete/3", "delete([1, 2, 1, 3, 1], 1, R)"),
        new("numlist/3", "numlist(1, 6, L)"),
        new("sum_list/2", "sum_list([1, 2, 3, 4], S)"),
        new("sumlist/2", "sumlist([10, 20, 30], S)"),
        new("max_list/2", "max_list([3, 1, 4, 1, 5], M)"),
        new("min_list/2", "min_list([3, 1, 4, 1, 5], M)"),
        new("max_member/2", "max_member(M, [3, 1, 4, 1, 5, 9, 2])"),
        new("min_member/2", "min_member(M, [3, 1, 4, 1, 5, 9, 2])"),
        new("length/2", "length([a, b, c, d], N)"),
        new("length/2", "length(L, 3)"),                       // generative mode
        new("sort/4", "sort(0, @>=, [3, 1, 2, 1, 4], L)"),
        new("predsort/3", "predsort(cmp3, [3, 1, 2, 1, 4], L)"),
        new("include/3", "include(even, [1, 2, 3, 4, 5, 6], L)"),
        new("exclude/3", "exclude(even, [1, 2, 3, 4, 5, 6], L)"),
        new("partition/4", "partition(even, [1, 2, 3, 4, 5], I, E)"),
        new("maplist/2", "maplist(even, [2, 4, 6])"),
        new("maplist/3", "maplist(dbl, [1, 2, 3], L)"),
        new("maplist/4", "maplist(add3, [1, 2, 3], [10, 20, 30], L)"),
        new("foldl/4", "foldl(foldadd, [1, 2, 3, 4], 0, S)"),
        new("foldl/5", "foldl(foldadd2, [1, 2], [10, 20], 0, S)"),
        new("pairs_keys_values/3", "pairs_keys_values([a-1, b-2, c-3], K, V)"),

        // ---- aggregation / all-solutions ----
        new("aggregate_all/3", "aggregate_all(sum(X), member(X, [1, 2, 3, 4]), S)"),
        new("aggregate_all/3", "aggregate_all(count, member(_, [a, b, c]), N)"),
        new("findall/4", "findall(X, member(X, [1, 2]), L, [99])"),
        new("copy_term/3", "copy_term(f(X, g(Y)), C, Attrs)"),

        // ---- atoms / strings ----
        new("sub_atom/5", "sub_atom(banana, B, 2, _, an)"),
        new("sub_atom/5", "sub_atom(abc, B, L, A, S)"),        // full enumeration
        new("atomic_list_concat/2", "atomic_list_concat([a, b, c], R)"),
        new("atomic_list_concat/3", "atomic_list_concat([a, b, c], '-', R)"),
        new("atomic_list_concat/3", "atomic_list_concat(L, '-', 'x-y-z')"),  // split
        new("char_type/2", "char_type(a, alnum)"),

        // ---- control ----
        new("once/1", "once(member(X, [a, b, c]))"),
        new("ignore/1", "ignore(fail)"),
        new("apply/2", "apply(inc, [5, X])"),
        // Variable goal so MetaTransform does NOT rewrite inline — exercises
        // the prelude forall/2 + catch/3 predicates (the runtime fallback path).
        new("forall/2", "C = member(X, [2, 4, 6]), forall(C, even(X))"),
        new("forall/2", "C = member(X, [2, 3, 4]), forall(C, even(X))"),   // false case
        new("catch/3", "G = throw(boom), catch(G, E, true)"),
        new("catch/3", "G = member(X, [a, b]), catch(G, _, true)"),        // no throw

        // ---- database / introspection ----
        new("clause/2", "clause(f1(X), true)", Warmup: "clause(f1(1), _)"),
        new("current_predicate/1", "current_predicate(f1/N)", Warmup: "current_predicate(even/1)"),
        new("retractall/1", "(retractall(t(_)), \\+ t(_))",
            Setup: ":- dynamic t/1.\nt(1).\nt(2).\n", Warmup: "retractall(zzz_warmup(_))"),

        // ---- output (captured via engine.Out) ----
        new("format_to_atom/3", "format_to_atom(A, '~w+~w=~w', [1, 2, 3])"),
        new("format/1", "format('hello ~w~n', [world])", Warmup: "format('warm~n')", Output: true),
        new("tab/1", "tab(3)", Warmup: "tab(1)", Output: true),
        new("listing/1", "listing(f1/1)", Warmup: "listing(nope/0)", Output: true),

        // ---- tabling ----
        new("abolish_all_tables/0", "abolish_all_tables"),
        new("abolish_table/1", "abolish_table(foo/1)"),
    };

    // Predicates that genuinely cannot run as Tier-1 IL (none today). A name
    // here means: still assert WAM==IL, but don't require promotion.
    private static readonly System.Collections.Generic.HashSet<string> WamOnly = new();

    // Predicates with a KNOWN, tracked Tier-1 IL divergence not yet fixed. Each
    // is verified to STILL diverge (a tripwire — when the underlying bug is
    // fixed the case here fails, prompting removal from this set).
    private static readonly System.Collections.Generic.Dictionary<string, string> KnownIlUnsafe = new()
    {
        // retract on a (pre-declared) dynamic predicate, reached from an
        // IL-compiled TOP-LEVEL query, throws permission_error(modify,
        // static_procedure). retractall/1 from a NAMED predicate is fine
        // (IlBacktrackReproTests.clr passes) — this is a dynamic-mutation-under-
        // IL issue in the query-wrapper path, SEPARATE from the cursor-builtin
        // IsBacktrackable fix. Tracked for a dedicated investigation.
        ["retractall/1"] = "retract on a dynamic predicate from an IL top-level query throws permission_error",
    };

    public static System.Collections.Generic.IEnumerable<object[]> Cases()
    {
        foreach (var c in AllCases)
            yield return new object[] { c };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void WamEqualsIl(Case c)
    {
        var (wamSols, wamOut, _) = Run(c, ilThreshold: 0);

        if (KnownIlUnsafe.TryGetValue(c.Pred, out var reason))
        {
            bool agrees;
            try
            {
                var (ilS, ilO, _) = Run(c, ilThreshold: 1);
                agrees = wamSols.SequenceEqual(ilS) && (!c.Output || wamOut == ilO);
            }
            catch (Shumway.Core.PrologRuntimeException) { agrees = false; }
            Assert.False(agrees,
                $"{c.Pred} now agrees under WAM/IL — the known issue ({reason}) " +
                "appears fixed; remove it from KnownIlUnsafe.");
            return;
        }

        var (ilSols, ilOut, promoted) = Run(c, ilThreshold: 1);
        Assert.Equal(wamSols, ilSols);
        if (c.Output)
            Assert.Equal(wamOut, ilOut);

        if (!WamOnly.Contains(c.Pred))
            Assert.True(promoted,
                $"{c.Pred} did not promote to Tier-1 IL for query `{c.Query}` — " +
                "the differential ran WAM-only and would mask an IL divergence.");
    }

    private static (System.Collections.Generic.List<string> sols, string output, bool promoted)
        Run(Case c, int ilThreshold)
    {
        // Set the output sink ONCE before any query — write/tab/format resolve
        // through the stream registry, which syncs to engine.Out at query time,
        // so reassigning Out mid-life would silently misroute output.
        var sw = new StringWriter();
        var e = new PrologEngine { Out = sw };
        if (ilThreshold > 0) e.IlPromotion.Threshold = ilThreshold;
        e.ConsultString(Helpers);
        if (!string.IsNullOrEmpty(c.Setup)) e.ConsultString(c.Setup);

        // IL: warm up once (synchronous promotion of the whole call tree).
        if (ilThreshold > 0)
            foreach (var _ in e.QueryAll((c.Warmup ?? c.Query) + ".")) { }

        // Discard everything emitted by consult + warm-up; measure only the
        // query's own output.
        sw.GetStringBuilder().Clear();
        var sols = new System.Collections.Generic.List<string>();
        foreach (var s in e.QueryAll(c.Query + ".")) sols.Add(Canon(s));

        bool promoted = IsPromoted(e, c.Pred);
        return (sols, sw.ToString(), promoted);
    }

    private static string Canon(Solution s)
    {
        if (s.Bindings.Count == 0) return "<success>";
        var keys = new System.Collections.Generic.List<string>(s.Bindings.Keys);
        keys.Sort(System.StringComparer.Ordinal);
        return string.Join(", ", keys.ConvertAll(k => k + "=" + s.Bindings[k]));
    }

    private static bool IsPromoted(PrologEngine e, string pred)
    {
        int slash = pred.LastIndexOf('/');
        string name = pred.Substring(0, slash);
        int arity = int.Parse(pred.Substring(slash + 1));
        int fid = FunctorTable.Intern(AtomTable.Intern(name).Id, arity);
        return e.IlPromotion.IsPromoted(fid);
    }
}
