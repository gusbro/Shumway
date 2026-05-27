using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 19 — IL meta-call dispatch correctness. Each test exercises a
/// distinct shape the bytecode interpreter's DispatchCall handles, and
/// verifies that the IL replica behaves the same when the predicate is
/// IL-promoted via the persisted-bundle path.
/// </summary>
public class Phase19MetaCallTests
{
    [Fact]
    public void Call_VariableGoal_BoundToAtom()
    {
        Run(":- public test/0.\n"
            + "test :- G = foo, call(G).\n"
            + "foo.\n",
            "test.");
    }

    [Fact]
    public void Call_VariableGoal_BoundToCompound()
    {
        Run(":- public test/0.\n"
            + "test :- G = foo(1), call(G).\n"
            + "foo(1).\n",
            "test.");
    }

    [Fact]
    public void Call_WithExtraArgs()
    {
        Run(":- public test/0.\n"
            + "test :- G = foo, call(G, 1, 2).\n"
            + "foo(1, 2).\n",
            "test.");
    }

    [Fact]
    public void Call_TrueGoal()
    {
        Run(":- public test/0.\n"
            + "test :- G = true, call(G).\n",
            "test.");
    }

    [Fact]
    public void Call_FailGoal()
    {
        // Wraps in negation so the predicate succeeds.
        Run(":- public test/0.\n"
            + "test :- G = fail, ( call(G) -> X = leaked ; X = ok ),\n"
            + "    X = ok.\n",
            "test.");
    }

    [Fact]
    public void Call_ConjunctionGoal()
    {
        Run(":- public test/0.\n"
            + "test :- G = (a, b), call(G).\n"
            + "a. b.\n",
            "test.");
    }

    [Fact]
    public void Call_DisjunctionGoal()
    {
        Run(":- public test/0.\n"
            + "test :- G = (a ; b), call(G).\n"
            + "a. b.\n",
            "test.");
    }

    [Fact]
    public void Call_NegationGoal()
    {
        // \+ fail → succeeds; the inner `fail` is a known builtin so
        // the linker accepts the reference and runtime negation-as-
        // failure routes through the chunk-88 $call_neg helper.
        Run(":- public test/0.\n"
            + "test :- G = (\\+ fail), call(G).\n",
            "test.");
    }

    [Fact]
    public void Call_NestedCall()
    {
        Run(":- public test/0.\n"
            + "test :- G = call(foo), call(G).\n"
            + "foo.\n",
            "test.");
    }

    [Fact]
    public void Call_ComparatorPattern()
    {
        // Mimics predsort_ins's `call(P, Ord, X, Y)` shape.
        Run(":- public test/0.\n"
            + "compare_at_test(<, X, Y) :- X @< Y.\n"
            + "compare_at_test(=, X, X).\n"
            + "compare_at_test(>, X, Y) :- X @> Y.\n"
            + "test :- P = compare_at_test, call(P, Ord, 1, 2), Ord = (<).\n",
            "test.");
    }

    private static void Run(string src, string query)
    {
        var bundle = new Bundle(new[] { new BundleEntry("p19", src) });
        byte[] bytes = BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: true, includeCompiledIl: true);
        var rt = BundleReader.FromBytes(bytes);
        var engine = new PrologEngine();
        engine.LoadBundle(rt);
        Assert.True(engine.Query(query).Success);
    }
}
