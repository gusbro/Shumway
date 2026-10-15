using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A packed string is a list (ADR-047), so a clause body that
/// unifies one with a list cell must succeed where the elements agree. The
/// module's unifier took two cells of different tags for different terms, and
/// a PSTR against a LIS has different tags: on the wasm tier the predicate
/// failed where the interpreter succeeded. Scryer's nth0/3 unifies that way,
/// and crypto's hex_bytes/2 failed through it.</summary>
public class PackedStringUnifyTests
{
    private const string Corpus = """
        after_check(L, T) :- nonvar(L), L = [_|T].
        committed(L, T) :- nonvar(L), L = [_|T], !.
        built_first(X) :- Y = [a,b,c], X = Y.
        built_second(X) :- Y = [a,b,c], Y = X.
        open_list(X, T) :- Y = [a|T], X = Y.
        mismatch(X) :- Y = [a,b,d], X = Y.
        skipped(L, N, S, R) :- '$skip_max_list'(S, N, L, R).
        """;

    private static string Run(PrologEngine e, string goal)
    {
        var r = e.Query(goal);
        if (!r.Success) return "fail";
        return string.Join(",", r.Bindings.Select(kv => kv.Key + "=" + kv.Value).OrderBy(s => s));
    }

    // A member's name is module-qualified: user$after_check/2.
    private static bool Names(WasmGroupMember m, string indicator)
    {
        var (atomId, arity) = FunctorTable.Lookup(m.Predicate.FunctorId);
        string name = AtomTable.GetById(atomId)!.Name + "/" + arity;
        return name == indicator || name.EndsWith("$" + indicator, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"after_check(""abc"", T).", "after_check/2", "T=.(b, .(c, []))")]
    [InlineData(@"committed(""abc"", T).", "committed/2", "T=.(b, .(c, []))")]
    [InlineData(@"built_first(""abc"").", "built_first/1", "")]
    [InlineData(@"built_second(""abc"").", "built_second/1", "")]
    [InlineData(@"open_list(""abc"", T).", "open_list/2", "T=.(b, .(c, []))")]
    [InlineData(@"mismatch(""abc"").", "mismatch/1", "fail")]
    [InlineData(@"skipped(""abc"", 1, S, R).", "$skip_max_list_/5", "R=.(b, .(c, [])),S=1")]
    [InlineData(@"skipped(""abc"", 5, S, R).", "$skip_max_list_/5", "R=[],S=3")]
    public void TheTierAgreesWithTheInterpreter(string goal, string runsOnTheTier, string answer)
    {
        var interpreted = new PrologEngine();
        interpreted.ConsultString(Corpus);
        Assert.Equal(answer, Run(interpreted, goal));

        var (tier, members, _) = TieredEngine.BuildWithWorld(Corpus);
        // The first call promotes; the second runs the module.
        Run(tier, goal);
        Assert.Contains(members, m => Names(m, runsOnTheTier));
        Assert.Equal(answer, Run(tier, goal));
    }
}
