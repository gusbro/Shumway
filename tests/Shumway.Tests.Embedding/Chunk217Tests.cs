using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 217: full indexed dispatch (O(1) + buckets) survives
/// <see cref="PrologEngine.LoadBundle"/>. A bundle built with
/// <c>includeCompiledIl: true</c> persists the indexed-dispatch IL
/// delegate; loading the bundle in a fresh engine resolves the baked
/// functor id via the chunk-197 patch table and the resolver builds the
/// dispatch model lazily from the engine's linked code on first call —
/// so bucket backtracking, var-head clauses joining every key, and
/// O(1) lookup all work cross-process with no build-time runtime state.
/// </summary>
public class Chunk217Tests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    /// <summary>Builds a bundle from <paramref name="src"/>, writes it
    /// with persisted IL, round-trips via the reader, loads into a fresh
    /// engine, asserts the indexed predicate promoted, and returns the
    /// loaded engine for query.</summary>
    private static PrologEngine LoadWithPersistedIl(string src, string predName, int arity)
    {
        var bundle = new Bundle(new[] { new BundleEntry("idx", src) });
        byte[] bytes = BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: true, includeCompiledIl: true);
        var roundtripped = BundleReader.FromBytes(bytes);
        Assert.NotNull(roundtripped.Entries[0].CompiledIl);
        Assert.True(roundtripped.Entries[0].CompiledIl!.Length > 0);

        var engine = new PrologEngine();
        engine.LoadBundle(roundtripped);
        Assert.True(engine.IlPromotion.IsPromoted(Fid(predName, arity)),
            $"{predName}/{arity} expected to be IL-promoted from the persisted bundle");
        return engine;
    }

    private static List<string> QueryAll(PrologEngine engine, string query, string var) =>
        engine.QueryAll(query).Select(s => s.Bindings[var].ToString()!).ToList();

    [Fact]
    public void CanPersist_AcceptsIndexedDispatch()
    {
        // Direct check: the recognised shape passes the persistence gate.
        var bundle = new Bundle(new[]
        {
            new BundleEntry("idx", ":- public q/2.\nq(a,1).\nq(b,2).\nq(c,3).\n"),
        });
        byte[] bytes = BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: true, includeCompiledIl: true);
        var rt = BundleReader.FromBytes(bytes);
        // The .dll exists and the patch table is populated (functor id
        // sentinels) — empty patches would mean nothing was persisted.
        Assert.NotNull(rt.Entries[0].CompiledIl);
        Assert.NotNull(rt.Entries[0].CompiledIlPatches);
        Assert.True(rt.Entries[0].CompiledIlPatches!.Length > 0);
    }

    [Fact]
    public void Persisted_PureDiscrimination_RuntimeAnswersCorrect()
    {
        var engine = LoadWithPersistedIl(
            ":- public q/2.\nq(a, 1).\nq(b, 2).\nq(c, 3).\n",
            "q", 2);
        Assert.Equal(new[] { "2" }, QueryAll(engine, "q(b, X).", "X"));
        Assert.Equal(new[] { "1", "2", "3" }, QueryAll(engine, "q(_, X).", "X"));
        Assert.Empty(QueryAll(engine, "q(z, X).", "X"));
    }

    [Fact]
    public void Persisted_Bucket_VarHeadClause_JoinsEveryKey()
    {
        // Key correctness test: a var-head clause must appear in every
        // bucket. The persisted IL's lazy model build has to reconstruct
        // the bucket chains from the linked code's switch tables.
        var engine = LoadWithPersistedIl(
            ":- public p/2.\np(a, 1).\np(X, 2).\np(b, 3).\n",
            "p", 2);
        Assert.Equal(new[] { "1", "2" }, QueryAll(engine, "p(a, R).", "R"));
        Assert.Equal(new[] { "2", "3" }, QueryAll(engine, "p(b, R).", "R"));
        Assert.Equal(new[] { "2" }, QueryAll(engine, "p(z, R).", "R"));
        Assert.Equal(new[] { "1", "2", "3" }, QueryAll(engine, "p(_, R).", "R"));
    }

    [Fact]
    public void Persisted_SharedKey_MultipleClausesSameKey()
    {
        var engine = LoadWithPersistedIl(
            ":- public r/2.\nr(a, 1).\nr(a, 2).\nr(b, 3).\n",
            "r", 2);
        Assert.Equal(new[] { "1", "2" }, QueryAll(engine, "r(a, X).", "X"));
        Assert.Equal(new[] { "3" }, QueryAll(engine, "r(b, X).", "X"));
        Assert.Empty(QueryAll(engine, "r(c, X).", "X"));
    }

    [Fact]
    public void Persisted_IntegerKeys()
    {
        var engine = LoadWithPersistedIl(
            ":- public f/2.\nf(1, one).\nf(2, two).\nf(3, three).\n",
            "f", 2);
        Assert.Equal(new[] { "two" }, QueryAll(engine, "f(2, X).", "X"));
        Assert.Equal(new[] { "one", "two", "three" }, QueryAll(engine, "f(_, X).", "X"));
    }

    [Fact]
    public void Persisted_StructureKeys()
    {
        var engine = LoadWithPersistedIl(
            ":- public g/2.\n" +
            "g(foo(_), 1).\n" +
            "g(bar(_), 2).\n" +
            "g(_, other).\n",
            "g", 2);
        Assert.Equal(new[] { "1", "other" }, QueryAll(engine, "g(foo(x), R).", "R"));
        Assert.Equal(new[] { "2", "other" }, QueryAll(engine, "g(bar(y), R).", "R"));
        Assert.Equal(new[] { "other" }, QueryAll(engine, "g(baz(z), R).", "R"));
    }
}
