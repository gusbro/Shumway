using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>append/3's deterministic mode, built inside the module.
///
/// <para>It was the largest single source of builtin exits: 2,000 of clpr's
/// 5,000, twice the next one. What the exit costs is the point -- measured in
/// a browser, the builtins of a whole run do 63 ms of work while getting to
/// them and back costs 271 ms.</para>
///
/// <para>Unlike the type tests or get_attr, this one BUILDS heap structure,
/// so being wrong has more ways to look right: a list can come out with the
/// correct elements and a broken spine, or share cells it should have copied.
/// The tests compare answers against the interpreter and then ask the result
/// questions a shape-level mistake would fail.</para></summary>
public sealed class InlineAppendTests(ITestOutputHelper o)
{
    private const string Corpus = """
        app(A, B, C) :- append(A, B, C).
        len([], 0).
        len([_|T], N) :- len(T, M), N is M + 1.
        """;

    [Theory]
    [InlineData("app([], [], R)", "[]")]
    [InlineData("app([], [a, b], R)", "[a,b]")]
    [InlineData("app([a], [], R)", "[a]")]
    [InlineData("app([a], [b], R)", "[a,b]")]
    [InlineData("app([a, b, c], [d, e], R)", "[a,b,c,d,e]")]
    [InlineData("app([1, 2, 3], [4], R)", "[1,2,3,4]")]
    // Nested structure in the elements: the cells are COPIED as they are.
    [InlineData("app([f(1), g(2)], [h(3)], R)", "[f(1),g(2),h(3)]")]
    // A partial list as the second argument stays partial.
    [InlineData("app([a], [b|_], R)", "partial")]
    public void TheResultIsWhatTheInterpreterBuilds(string goal, string _)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        var (tiered, _2) = TieredEngine.Build(Corpus);

        // Written, not inspected structurally: writeq renders the SPINE, so a
        // list whose elements are right and whose tail is wrong reads
        // differently. Comparing to the interpreter keeps the expectation
        // from being mine.
        string want = Render(plain, goal);
        string got = Render(tiered, goal);
        o.WriteLine($"{goal} -> {want}");
        Assert.Equal(want, got);
    }

    private static string Render(PrologEngine e, string goal)
    {
        var r = e.Query($"( {goal} -> with_output_to(atom(A), writeq(R)) ; A = '<fail>' ).");
        return r.Success ? r.Bindings["A"].ToString()! : "<error>";
    }

    /// <summary>The spine has to be a real list, not just something that
    /// prints like one: length/2 walks it cell by cell.</summary>
    [Fact]
    public void TheSpineIsWalkable()
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query("app([a,b,c], [d,e], R), len(R, N), N == 5.").Success,
            "the built list does not walk to length 5");
        Assert.True(tiered.Query("app([a,b,c], [d,e], R), R = [_,_,_|T], T == [d,e].").Success,
            "the tail after three elements is not L2");
    }

    /// <summary>The second argument is SHARED, not copied: appending to a
    /// partial list and then binding its tail has to show through.</summary>
    [Fact]
    public void TheSecondArgumentIsShared()
    {
        const string P = """
            app(A, B, C) :- append(A, B, C).
            probe(R, T) :- app([a], [b|T], R).
            """;
        var plain = new PrologEngine();
        plain.ConsultString(P);
        Assert.True(plain.Query("probe(R, T), T = [c], R == [a,b,c].").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(P);
        Assert.True(tiered.Query("probe(R, T), T = [c], R == [a,b,c].").Success,
            "the built list did not share L2's tail");
    }

    /// <summary>Backtracking undoes the build: the cells claimed for a result
    /// that was abandoned must not survive into the next solution.</summary>
    [Fact]
    public void BacktrackingOverTheBuildIsClean()
    {
        const string P = """
            app(A, B, C) :- append(A, B, C).
            pick([a]).
            pick([a,b]).
            pick([a,b,c]).
            all(L) :- findall(R, (pick(X), app(X, [z], R)), L).
            """;
        var plain = new PrologEngine();
        plain.ConsultString(P);
        Assert.True(plain.Query("all(L), L == [[a,z],[a,b,z],[a,b,c,z]].").Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(P);
        Assert.True(tiered.Query("all(L), L == [[a,z],[a,b,z],[a,b,c,z]].").Success,
            "backtracking over the inline build lost or corrupted a solution");
    }

    /// <summary>The modes it declines still answer the way the builtin does:
    /// the split enumeration, and a string, which is a list this walk cannot
    /// follow.</summary>
    [Fact]
    public void TheModesItDeclinesStillAnswer()
    {
        const string P = """
            app(A, B, C) :- append(A, B, C).
            splits(L) :- findall(X-Y, app(X, Y, [a,b]), L).
            """;
        var plain = new PrologEngine();
        plain.ConsultString(P);
        string want = plain.Query(
            "splits(L), with_output_to(atom(A), writeq(L)).").Bindings["A"].ToString()!;

        var (tiered, _) = TieredEngine.Build(P);
        string got = tiered.Query(
            "splits(L), with_output_to(atom(A), writeq(L)).").Bindings["A"].ToString()!;
        o.WriteLine($"splits -> {want}");
        Assert.Equal(want, got);
    }

    /// <summary>The counter: appending stops leaving the module. atom_length
    /// shares the clause, so a zero cannot be a program that never ran on the
    /// tier.</summary>
    [DiagFact]
    public void AppendingStopsLeavingTheModule()
    {
        var (engine, _) = TieredEngine.Build("""
            build(0, _, []).
            build(N, X, R) :- N > 0, atom_length(ab, _), append(X, [N], R0),
                              N1 is N - 1, build(N1, X, R1), R = [R0|R1].
            """);
        WasmTierDelegate.ResetDiag();
        Assert.True(engine.Query("build(30, [p,q], L), length(L, 30).").Success);

        long appends = 0, lens = 0;
        foreach (var (n, a, hits) in WasmTierDelegate.BuiltinRanking())
        {
            if (n == "append" && a == 3) appends = hits;
            if (n == "atom_length" && a == 2) lens = hits;
        }
        o.WriteLine($"append/3 exits={appends} atom_length/2={lens} "
            + $"deopts={WasmTierDelegate.DiagDeopts}");
        Assert.True(lens >= 30, $"the clause never ran on the tier ({lens} exits)");
        Assert.Equal(0L, appends);
    }
}
