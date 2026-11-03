using System.IO;
using Shumway.Builtins;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Code dropped from a buffer (a clause chunk the chain bypasses, a
/// version a reconsult replaces while a query runs) keeps its bytes and its
/// literals while an activation can still run it, a goal returning into the body
/// of a clause that retracted itself or a choice point resuming into it, and gives
/// them up once none can: the next asserts and links reuse its bytes. A reloaded
/// predicate is entered through a trampoline at its first entry.</summary>
public sealed class DroppedCodeTests
{
    static DroppedCodeTests()
    {
        BuiltinsRegistry.Register("$dropped_code_length", 1, a =>
            a.UnifyRegisterWithCell(0, Shumway.Core.Cell.Int(a.ProgramLength)));
        // The bytes, the switch tables and the address-map entries the query holds.
        BuiltinsRegistry.Register("$dropped_code_size", 3, a =>
            a.UnifyRegisterWithCell(0, Shumway.Core.Cell.Int(a.ProgramLength))
            && a.UnifyRegisterWithCell(1, Shumway.Core.Cell.Int(a.SwitchTables?.Count ?? 0))
            && a.UnifyRegisterWithCell(2, Shumway.Core.Cell.Int(
                (a.HostProgram as PrologEngine.LinkedProgram)?.Map?.Count ?? 0)));
        // t/1's chain after an abolish: the head chunk's <next> operand, the
        // tail slot's offset from the head, the free list's length, the stub.
        BuiltinsRegistry.Register("$abolished_chain", 4, a =>
        {
            var host = (PrologEngine)a.Host!;
            int fid = Shumway.Core.FunctorTable.Intern(Shumway.Core.AtomTable.Intern("t", permanent: true).Id, 1);
            var table = host.GetChainTable(a)!;
            var chain = table.Chains[fid];
            int next = Shumway.Core.BytecodeIO.ReadInt32(a.CurrentProgram!, chain.HeadClauseAddr + 1);
            return a.UnifyRegisterWithCell(0, Shumway.Core.Cell.Int(next))
                && a.UnifyRegisterWithCell(1, Shumway.Core.Cell.Int(chain.TailNextAddr - chain.HeadClauseAddr))
                && a.UnifyRegisterWithCell(2, Shumway.Core.Cell.Int(table.FreeChunks.Count))
                && a.UnifyRegisterWithCell(3, Shumway.Core.Cell.Int(a.DynamicFailStubAddr));
        });
    }

    private static PrologEngine Engine(string program)
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString(program);
        return e;
    }

    // A file of Prolog text in a directory the test removes, by a path Prolog reads.
    private static string WriteFile(string dir, string name, string text)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return path.Replace('\\', '/');
    }

    private static void InTempDir(System.Action<string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "shumway-dropped-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AQueryThatReconsultsFileAfterFileKeepsItsBufferBounded() => InTempDir(dir =>
    {
        var e = Engine(":- dynamic(junk/1). " + Churn);
        // Three predicates a file, keyed by atoms and of a size that changes from
        // file to file: a reload drops versions laid side by side, their switch
        // tables and their entries, and a later link lays its own elsewhere. The
        // churn sweeps the literal pools, so a version's literals must hold. The
        // sizes cycle every seven files: measured from the tenth on.
        for (int round = 1; round <= 30; round++)
        {
            int n = 40 + round % 7;
            var facts = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++) facts.Append($"fact({round}, k{i}, {i}.5).\n");
            for (int i = 0; i < n; i++) facts.Append($"key(k{i}, {round}).\n");
            for (int i = 0; i < n; i++) facts.Append($"label(k{i}, \"t{round}\").\n");
            WriteFile(dir, $"facts{round}.pl", facts.ToString());
        }
        string files = dir.Replace('\\', '/');
        var sizes = e.Query($"""
            findall(S, ( between(1, 30, R), number_codes(R, Cs), atom_codes(N, Cs),
                         atom_concat('{files}/facts', N, F0), atom_concat(F0, '.pl', F),
                         reconsult(F), churn,
                         findall(X, fact(R, _, X), Xs), length(Xs, Len), Len >= 40,
                         fact(R, k3, V), V =:= 3.5,
                         label(k3, L), atom_chars(A, L), atom_concat(t, N, A),
                         key(k7, R), \+ ( key(_, T), T \== R ),
                         '$dropped_code_size'(B, Ts, M), S = s(B, Ts, M) ), Ss),
            length(Ss, 30), nth1(10, Ss, s(B0, T0, M0)), last(Ss, s(B1, T1, M1)).
            """);

        Assert.True(sizes.Success);
        long Get(string name) => long.Parse(sizes.Bindings[name].ToString()!);
        Assert.True(Get("B1") <= Get("B0") + 4096, $"the buffer grew from {Get("B0")} to {Get("B1")} bytes");
        Assert.True(Get("T1") <= Get("T0") + 8, $"the switch tables grew from {Get("T0")} to {Get("T1")}");
        Assert.True(Get("M1") <= Get("M0") + 8, $"the address map grew from {Get("M0")} to {Get("M1")}");
    });

    [Fact]
    public void AQueryThatAbolishesAndConsultsFileAfterFileKeepsItsBufferBounded() => InTempDir(dir =>
    {
        var e = Engine(":- dynamic(junk/1). " + Churn);
        // The same loop over dynamic facts: abolish drops the clauses, consult
        // asserts the next file's.
        for (int round = 1; round <= 30; round++)
        {
            int n = 40 + round % 7;
            var facts = new System.Text.StringBuilder(":- dynamic(fact/3). :- dynamic(key/2). :- dynamic(label/2).\n");
            for (int i = 0; i < n; i++) facts.Append($"fact({round}, k{i}, {i}.5).\n");
            for (int i = 0; i < n; i++) facts.Append($"key(k{i}, {round}).\n");
            for (int i = 0; i < n; i++) facts.Append($"label(k{i}, \"t{round}\").\n");
            WriteFile(dir, $"facts{round}.pl", facts.ToString());
        }
        string files = dir.Replace('\\', '/');
        var sizes = e.Query($"""
            findall(S, ( between(1, 30, R), number_codes(R, Cs), atom_codes(N, Cs),
                         atom_concat('{files}/facts', N, F0), atom_concat(F0, '.pl', F),
                         abolish(fact/3), abolish(key/2), abolish(label/2), consult(F), churn,
                         findall(X, fact(R, _, X), Xs), length(Xs, Len), Len >= 40,
                         \+ ( fact(R2, _, _), R2 \== R ),
                         fact(R, k3, V), V =:= 3.5,
                         label(k3, L), atom_chars(A, L), atom_concat(t, N, A),
                         key(k7, R), \+ ( key(_, T), T \== R ),
                         '$dropped_code_size'(B, Ts, M), S = s(B, Ts, M) ), Ss),
            length(Ss, 30), nth1(10, Ss, s(B0, T0, M0)), last(Ss, s(B1, T1, M1)).
            """);

        Assert.True(sizes.Success);
        long Get(string name) => long.Parse(sizes.Bindings[name].ToString()!);
        Assert.True(Get("B1") <= Get("B0") + 4096, $"the buffer grew from {Get("B0")} to {Get("B1")} bytes");
        Assert.True(Get("T1") <= Get("T0") + 8, $"the switch tables grew from {Get("T0")} to {Get("T1")}");
        Assert.True(Get("M1") <= Get("M0") + 8, $"the address map grew from {Get("M0")} to {Get("M1")}");
    });

    [Fact]
    public void AVersionLinkedInsideAQueryKeepsItsLiteralsForTheNextQuery() => InTempDir(dir =>
    {
        string d = dir.Replace('\\', '/');
        for (int k = 1; k <= 3; k++) WriteFile(dir, $"v{k}.pl", $"label(\"text{k}\"). weight({k}.25).");
        var e = Engine(":- dynamic(junk/1). " + Churn);
        // The buffer the query ran on dies with it, and with it the clauses
        // asserted last: enough literals for the pools to sweep.
        Assert.True(e.Query($"""
            reconsult('{d}/v1.pl'), churn, reconsult('{d}/v2.pl'), churn,
            reconsult('{d}/v3.pl'), churn, label(L), atom_chars(text3, L),
            ( between(1, 300, I), F is I + 0.125, assertz(junk(F)), fail ; true ).
            """).Success);

        // The next query links the last version again.
        var answer = e.Query("churn, label(L), atom_chars(A, L), weight(W).");

        Assert.True(answer.Success);
        Assert.Equal("text3", answer.Bindings["A"].ToString());
        Assert.Equal("3.25", answer.Bindings["W"].ToString());
    });

    [Fact]
    public void ACallerReachesTheLatestVersionThroughTheTrampoline() => InTempDir(dir =>
    {
        for (int k = 1; k <= 5; k++) WriteFile(dir, $"v{k}.pl", $"version({k}).");
        var e = Engine("current(X) :- version(X).");
        string d = dir.Replace('\\', '/');
        Assert.True(e.Query($"""
            reconsult('{d}/v1.pl'), current(1), reconsult('{d}/v2.pl'), current(2),
            reconsult('{d}/v3.pl'), current(3), reconsult('{d}/v4.pl'), current(4),
            reconsult('{d}/v5.pl'), current(5).
            """).Success);
    });

    [Fact]
    public void ACallLinkedWithItsCalleeFollowsALaterReloadOfTheCallee() => InTempDir(dir =>
    {
        string d = dir.Replace('\\', '/');
        WriteFile(dir, "a.pl", "p(1).");
        WriteFile(dir, "b.pl", "p(2).");
        // Linked in one batch with p, which already enters through a trampoline.
        WriteFile(dir, "c.pl", "p(3). q(X) :- p(X).");
        WriteFile(dir, "d.pl", "p(4).");
        var e = Engine("");
        Assert.True(e.Query($"""
            reconsult('{d}/a.pl'), reconsult('{d}/b.pl'), reconsult('{d}/c.pl'), q(3),
            reconsult('{d}/d.pl'), q(X), X == 4.
            """).Success);
    });

    // Floats asserted and retracted one by one: the chain drops their chunks and
    // releases their literals, and the pool sweeps and hands their ids out again.
    private const string Churn = """
        churn :-
            ( between(1, 300, I), F is I + 0.5, assertz(junk(F)), fail ; true ),
            ( between(1, 300, _), once(retract(junk(_))), fail ; true ),
            ( between(1, 300, I), F is I + 0.75, assertz(junk(F)), fail ; true ),
            ( between(1, 300, _), once(retract(junk(_))), fail ; true ).
        """;

    [Fact]
    public void AClauseThatRetractsItselfKeepsItsLiteralsWhileItRuns()
    {
        var e = Engine(":- dynamic(d/2). :- dynamic(junk/1). " + Churn);
        // Heads the chain cannot index, the rule last so no choice point is left
        // in the chain while it runs; its fourth retract lets the chain drop the
        // dead chunks, its own among them.
        Assert.True(e.Query("assertz((d(_, _) :- fail)), assertz((d(_, _) :- fail)), "
            + "assertz((d(_, _) :- fail)), assertz((d(_, _) :- fail)), "
            + "assertz((d(G, V) :- G == go, \\+ \\+ retract((d(H, _) :- H == go, _)), "
            + "once(retract((d(_, _) :- fail))), once(retract((d(_, _) :- fail))), "
            + "once(retract((d(_, _) :- fail))), churn, V = 3.0625)).").Success);

        var answer = e.Query("d(go, V).");

        Assert.True(answer.Success);
        Assert.Equal("3.0625", answer.Bindings["V"].ToString());
    }

    [Fact]
    public void AQueryThatAssertsAndRetractsForEverKeepsItsBufferBounded()
    {
        var e = Engine(":- dynamic(item/1).");
        var lengths = e.Query("""
            findall(L, ( between(1, 60, R),
                         ( between(1, 40, I), J is R * 100 + I, assertz(item(J)), fail ; true ),
                         ( between(1, 40, _), once(retract(item(_))), fail ; true ),
                         '$dropped_code_length'(L) ), Ls),
            nth1(10, Ls, Early), last(Ls, Late).
            """);

        Assert.True(lengths.Success);
        long early = long.Parse(lengths.Bindings["Early"].ToString()!);
        long late = long.Parse(lengths.Bindings["Late"].ToString()!);
        Assert.True(late <= early + 4096, $"the buffer grew from {early} to {late} bytes");
    }

    [Fact]
    public void TheHeadOfAChainIsNotRecycled()
    {
        var e = Engine(":- dynamic(q/1).");
        // The asserta'd clause becomes the head the trampoline enters: dead and
        // collected, the only free chunk, its bytes must not take the next clause.
        Assert.True(e.Query("""
            asserta(q(1)), assertz(q(2)), once(retract(q(1))),
            garbage_collect_clauses, assertz(q(6)),
            findall(X, q(X), L), L == [2, 6].
            """).Success);
    }

    [Fact]
    public void CollectingClausesDoesNotRecycleACodeAChoicePointResumesInto()
    {
        var e = Engine(":- dynamic(p/1).");
        // The logical update view: the call saw five clauses when it began.
        Assert.True(e.Query("""
            assertz(p(1)), assertz(p(2)), assertz(p(3)), assertz(p(4)), assertz(p(5)),
            findall(X, ( p(X),
                         ( X == 1
                         -> retract(p(2)), retract(p(3)), retract(p(4)), retract(p(5)),
                            garbage_collect_clauses, assertz(p(6)), assertz(p(7))
                         ;  true ) ), L),
            L == [1, 2, 3, 4, 5].
            """).Success);
    }

    // The live link re-patches the forward references of every batch linked so
    // far once their callee links: those of a version a later consult replaced
    // go with it, or the patch would land in whatever its bytes hold by then.
    [Fact]
    public void AReplacedVersionsForwardReferencesGoWithIt() => InTempDir(dir =>
    {
        string a1 = WriteFile(dir, "a1.pl", "p :- q, q, q, q, q, q, q, q, q, q, q, q.");
        string a2 = WriteFile(dir, "a2.pl", "p :- r.\nr.");
        string b = WriteFile(dir, "b.pl", "q.");
        var e = Engine(":- dynamic(d/2).");
        // The asserted facts take the replaced version's bytes; q's definition
        // patches the sites still listed.
        Assert.True(e.Query($"""
            consult('{a1}'), consult('{a2}'),
            assertz(d(1, one)), assertz(d(2, two)), assertz(d(3, three)),
            consult('{b}'),
            d(1, X), X == one, d(2, Y), Y == two, d(3, Z), Z == three, p, q.
            """).Success);
    });

    // An abolish stages the chunks of the live entries for reclamation. A
    // retired entry's chunk is dead already, staged or freed by a retract's
    // sweep: staged again, the free list would hand its bytes out twice.
    [Fact]
    public void AnAbolishAfterRetractsFreesEachChunkOnce()
    {
        var e = Engine(":- dynamic(t/2).\n:- dynamic(u/2).");
        Assert.True(e.Query("""
            ( between(1, 12, I), assertz(t(I, x)), fail ; true ),
            ( between(1, 4, I), retract(t(I, _)), fail ; true ),
            abolish(t/2),
            ( between(1, 8, I), assertz(u(I, y)), fail ; true ),
            findall(X, u(X, y), L), L == [1, 2, 3, 4, 5, 6, 7, 8].
            """).Success);
    }

    // After an abolish the head chunk stays as the chain's anchor, and its link
    // led to the dead chunks the sweep frees: cut, so that no path runs them
    // (a prepend links its clause to the head, whatever the head links to).
    [Fact]
    public void AnAbolishCutsTheChainsHeadFromTheChunksItFrees()
    {
        var e = Engine(":- dynamic(t/1).");
        Assert.True(e.Query("""
            ( between(1, 8, I), assertz(t(I)), fail ; true ),
            abolish(t/1),
            '$abolished_chain'(Next, TailOffset, Free, Stub),
            Next =:= Stub, TailOffset =:= 1, Free > 0.
            """).Success);
    }
}
