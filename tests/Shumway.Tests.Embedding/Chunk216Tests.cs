using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 216 stage 2: end-to-end correctness of indexed dispatch in IL.
/// Each test runs the same program + query under Tier-0 (interpreter) and
/// Tier-1 (promotion forced at Threshold=1) and asserts identical answers,
/// plus that the indexed predicate really promoted (so the indexed IL path
/// is exercised, not a fallback). Covers pure discrimination, buckets with
/// a var-head clause joining every key, shared keys, integer / structure
/// keys, deep backtracking, and an indexed predicate with non-fact bodies.
/// </summary>
public class Chunk216Tests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    /// <summary>Runs <paramref name="query"/> under Tier-0 and Tier-1,
    /// asserts identical solution lists, and (when <paramref name="promoted"/>
    /// is given) that the predicate promoted under Tier-1.</summary>
    private static List<string> RunBoth(string program, string query, string var,
        (string name, int arity)? promoted = null)
    {
        var t0 = new PrologEngine();
        t0.ConsultString(program);
        var r0 = t0.QueryAll(query).Select(s => s.Bindings[var].ToString()!).ToList();

        var t1 = new PrologEngine();
        t1.IlPromotion.Threshold = 1;
        t1.ConsultString(program);
        var r1 = t1.QueryAll(query).Select(s => s.Bindings[var].ToString()!).ToList();

        Assert.Equal(r0, r1);
        if (promoted is var (n, a))
            Assert.True(t1.IlPromotion.IsPromoted(Fid(n, a)),
                $"{n}/{a} expected to promote to indexed IL");
        return r1;
    }

    [Fact]
    public void PureDiscrimination_FirstArgAtom()
    {
        const string p = ":- public q/2.\nq(a, 1).\nq(b, 2).\nq(c, 3).\n";
        Assert.Equal(new[] { "2" }, RunBoth(p, "q(b, X).", "X", ("q", 2)));
        Assert.Equal(new[] { "1", "2", "3" }, RunBoth(p, "q(_, X).", "X", ("q", 2)));
        Assert.Empty(RunBoth(p, "q(z, X).", "X", ("q", 2)));
    }

    [Fact]
    public void Bucket_VarHeadClause_JoinsEveryKey()
    {
        // p(a,1). p(X,2). p(b,3).  key a -> {1,2}; key b -> {2,3}; key z -> {2}.
        const string p = ":- public p/2.\np(a, 1).\np(X, 2).\np(b, 3).\n";
        Assert.Equal(new[] { "1", "2" }, RunBoth(p, "p(a, R).", "R", ("p", 2)));
        Assert.Equal(new[] { "2", "3" }, RunBoth(p, "p(b, R).", "R", ("p", 2)));
        Assert.Equal(new[] { "2" }, RunBoth(p, "p(z, R).", "R", ("p", 2)));
        // Unbound key -> every clause in source order.
        Assert.Equal(new[] { "1", "2", "3" }, RunBoth(p, "p(_, R).", "R", ("p", 2)));
    }

    [Fact]
    public void SharedKey_MultipleClausesSameKey()
    {
        const string p = ":- public r/2.\nr(a, 1).\nr(a, 2).\nr(b, 3).\n";
        Assert.Equal(new[] { "1", "2" }, RunBoth(p, "r(a, X).", "X", ("r", 2)));
        Assert.Equal(new[] { "3" }, RunBoth(p, "r(b, X).", "X", ("r", 2)));
        Assert.Empty(RunBoth(p, "r(c, X).", "X", ("r", 2)));
    }

    [Fact]
    public void IntegerKeys()
    {
        const string p = ":- public f/2.\nf(1, one).\nf(2, two).\nf(3, three).\n";
        Assert.Equal(new[] { "two" }, RunBoth(p, "f(2, X).", "X", ("f", 2)));
        Assert.Equal(new[] { "one", "two", "three" }, RunBoth(p, "f(_, X).", "X", ("f", 2)));
    }

    [Fact]
    public void StructureKeys()
    {
        const string p =
            ":- public g/2.\n" +
            "g(foo(_), 1).\n" +
            "g(bar(_), 2).\n" +
            "g(_, other).\n";
        Assert.Equal(new[] { "1", "other" }, RunBoth(p, "g(foo(x), R).", "R", ("g", 2)));
        Assert.Equal(new[] { "2", "other" }, RunBoth(p, "g(bar(y), R).", "R", ("g", 2)));
        Assert.Equal(new[] { "other" }, RunBoth(p, "g(baz(z), R).", "R", ("g", 2)));
    }

    [Fact]
    public void IndexedPredicate_WithBodies_AndBacktracking()
    {
        // Indexed on arg0, but clauses have real bodies (guards + a call).
        const string p =
            ":- public classify/2.\n" +
            "classify(n(X), R) :- X < 0, R = neg.\n" +
            "classify(n(X), R) :- X >= 0, R = nonneg.\n" +
            "classify(s(_), str).\n";
        Assert.Equal(new[] { "neg" }, RunBoth(p, "classify(n(-3), R).", "R", ("classify", 2)));
        Assert.Equal(new[] { "nonneg" }, RunBoth(p, "classify(n(5), R).", "R", ("classify", 2)));
        Assert.Equal(new[] { "str" }, RunBoth(p, "classify(s(x), R).", "R", ("classify", 2)));
    }

    [Fact]
    public void DeepBacktracking_AcrossBuckets()
    {
        // findall over a bucketed predicate exercises full enumeration.
        const string p =
            ":- public color/2.\n" +
            "color(red, warm).\n" +
            "color(_, any).\n" +
            "color(blue, cool).\n";
        // key red -> {warm, any}; the var-head 'any' clause is in every bucket.
        Assert.Equal(new[] { "warm", "any" }, RunBoth(p, "color(red, T).", "T", ("color", 2)));
        Assert.Equal(new[] { "any", "cool" }, RunBoth(p, "color(blue, T).", "T", ("color", 2)));
    }
}
