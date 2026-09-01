using System.Collections.Generic;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Regression for the Tier-1 IL backtrackable-builtin bug: cursor
/// builtins added in the alloc sweep ($clause_enum, $current_predicate_enum,
/// nth0/1, recorded, keys, string_search, directory) PushBuiltinChoicePoint at
/// runtime but were not flagged IsBacktrackable, so the IL emit skipped the
/// chunk-218 resume-marker / BuiltinReturnPc setup — the resume jumped to PC 0
/// and lost solutions. Each case wraps the builtin in a user predicate and
/// asserts the full solution set is identical WAM (Threshold 0) vs IL
/// (Threshold 1, which runs the instrumented-IL phase).</summary>
public sealed class IlBacktrackReproTests
{
    private static List<string> Solutions(string setup, string query, int threshold)
    {
        var e = new PrologEngine();
        if (threshold > 0) e.IlPromotion.Threshold = threshold;
        e.ConsultString(setup);
        return e.QueryAll(query + ".")
            .Select(s => string.Join(",", s.Bindings.OrderBy(b => b.Key).Select(b => b.Key + "=" + b.Value)))
            .ToList();
    }

    [Theory]
    [InlineData(":- public f1/1.\nf1(1). f1(2). f1(3).\nmc(H, B) :- '$clause_enum'(H, H-B).", "mc(f1(X), true)")]
    [InlineData("p(_). q(_, _).\ncp(I) :- '$current_predicate_enum'(I).", "cp(p/N)")]
    [InlineData("sa(B, L, S) :- sub_atom(banana, B, L, _, S).", "sa(B, 2, S)")]
    // Preceding choice point before the backtrackable builtin (the sf1 / maplist
    // shape): member/2 leaves CPs, then sub_atom enumerates.
    [InlineData("mp(M, P) :- member(M, [a, b]), sub_atom(shumway, 0, 1, _, P).", "mp(M, P)")]
    [InlineData("mb(M, X) :- member(M, [a, b]), between(1, 2, X).", "mb(M, X)")]
    [InlineData("nt(I, X) :- nth1(I, [a, b, c], X).", "nt(I, X)")]
    [InlineData("rc(K, V) :- recorded(K, V, _).", "(recordz(k, va, _), recordz(k, vb, _), rc(k, V))")]
    // SEPARATE bug (not the cursor fix): retract on a dynamic predicate from an
    // IL region — expected to still fail until the dynamic-under-IL issue is
    // fixed. Kept here as the documented reproduction.
    [InlineData(":- dynamic t/1.\nt(1).\nt(2).\nclr :- retractall(t(_)), \\+ t(_).", "clr")]
    public void WamEqualsIl_ForCursorBuiltins(string setup, string query)
    {
        var wam = Solutions(setup, query, threshold: 0);
        var il = Solutions(setup, query, threshold: 1);
        Assert.Equal(wam, il);
        Assert.NotEmpty(wam);   // guard: the query must actually enumerate
    }
}
