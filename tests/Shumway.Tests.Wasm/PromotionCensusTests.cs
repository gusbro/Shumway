using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>The static promotion census: a member whose guaranteed host
/// crossings dominate its compilable work is refused at compile time and
/// stays on Tier-0. The motivating shape is catch/3 around a recursive call
/// (perftest1.pl's deep3): promoted, every level paid two crossings for one
/// `is`, and it ran an order of magnitude SLOWER than the interpreter. The
/// census is a by-product of the decode pass, so refusing costs nothing at
/// run time -- which is why it is static and not measured.</summary>
public sealed class PromotionCensusTests
{
    private static string NameOf(WasmGroupMember m)
    {
        var (aid, _) = FunctorTable.Lookup(m.Predicate.FunctorId);
        return AtomTable.GetById(aid)?.Name ?? "?";
    }

    // deep3's shape verbatim: the body is one `is` plus a catch, and the
    // catch rewrite plants the two $catch_begin/$catch_end crossings in a
    // $catchgoal_N helper that the caller owns.
    private const string Deep = """
        :- public deep3/2.
        deep3(0, G) :- !, call(G).
        deep3(N, G) :- M is N - 1, catch(deep3(M, G), _, true).
        """;

    [Fact]
    public void ACatchDominatedPredicateIsRefused()
    {
        var (e, members, _) = TieredEngine.BuildWithWorld(Deep);
        Assert.True(e.Query("deep3(300, true).").Success);
        // Neither deep3 nor its $catchgoal_ helper promoted: the helper
        // carries the crossings, and the caller inherits them (a call to a
        // $catchgoal_ IS the caller's catch).
        Assert.DoesNotContain(members, m => NameOf(m).Contains("deep3"));
        Assert.DoesNotContain(members, m => NameOf(m).Contains("$catchgoal_"));
        // And refusing changed no answer.
        Assert.False(e.Query("deep3(300, fail).").Success);
    }

    // The other pole: the same catch, but a body with real compilable work
    // around it. If this stopped promoting, the census got greedy -- it is
    // the red counter-proof that the bar discriminates rather than refusing
    // every predicate that ever crosses.
    private const string Worker = """
        :- public busy/2.
        busy(0, A) :- !, A = [].
        busy(N, [X|T]) :-
            M is N - 1,
            X is N * N + M // 2,
            Y is X mod 7, Z is Y + X * 3, W is Z - Y,
            W >= 0, X >= M,
            busy(M, T).
        """;

    [Fact]
    public void APredicateWithRealWorkStillPromotes()
    {
        var (e, members, _) = TieredEngine.BuildWithWorld(Worker);
        Assert.True(e.Query("busy(50, L), length(L, 50).").Success);
        Assert.Contains(members, m => NameOf(m) == "busy");
    }

    [Fact]
    public void TheRefusalNamesTheArithmetic()
    {
        // Straight to the compiler: the refusal is a WasmCompileException
        // whose message carries the counts, so a reader of the diag log can
        // see WHY a predicate stayed behind without re-deriving the census.
        var e = new PrologEngine();
        e.ConsultString("""
            :- public crosser/1.
            crosser(X) :- put_attr(X, m, a), put_attr(X, n, b).
            """);
        e.Query("true.");
        var env = new EngineWasmCompileEnv();
        WasmGroupMember? member = null;
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(e))
        {
            var (aid, _) = FunctorTable.Lookup(pred.FunctorId);
            if (AtomTable.GetById(aid)?.Name == "crosser")
                member = new WasmGroupMember(pred, addr, null);
        }
        Assert.NotNull(member);
        var ex = Assert.Throws<WasmCompileException>(
            () => WasmPredicateCompiler.CompileGroup([member!], env));
        Assert.Contains("host crossings", ex.Message);
        Assert.Contains("crosser/1", ex.Message);
    }
}
