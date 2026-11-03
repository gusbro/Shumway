using System;
using System.IO;
using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-003: compiled code claims the atoms it names, and the atoms of
/// the images it loads, while it is installed and, once evicted, while an
/// activation can still resume into it: through a resume marker of its
/// predicate, a compiled choice point, or a method of it on the stack around a
/// builtin that may change code. A new install for the predicate waits for the
/// retired code to go. Dead ids are poisoned: an atom the code still names and
/// no claim kept fails where it is read.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class Tier1ArtifactAtomTests
{
    static Tier1ArtifactAtomTests()
    {
        BuiltinsRegistry.Register("$art_collect", 0, _ =>
        {
            AtomTable.RequestCollection();
            return true;
        });
        BuiltinsRegistry.Register("$art_collected", 0, _ => !AtomTable.CollectionPending);
        // The retired artifacts of the engine running the probe, as seen from
        // inside compiled code.
        BuiltinsRegistry.Register("$art_retired", 0, a =>
        {
            Retired.Add(((PrologEngine)a.Host!).IlPromotion.RetiredArtifacts);
            return true;
        });
    }

    private static readonly System.Collections.Generic.List<int> Retired = new();

    private const string Countdown = """
        countdown(0) :- !.
        countdown(N) :- M is N - 1, countdown(M).
        """;

    private static void Run(Action<PrologEngine> body)
    {
        bool enabled = AtomTable.CollectionEnabled, poisoned = AtomTable.PoisonDeadIds;
        AtomTable.CollectionEnabled = true;
        AtomTable.PoisonDeadIds = true;
        try
        {
            var e = new PrologEngine { Out = new StringWriter() };
            e.IlPromotion.Threshold = 1;
            e.IlPromotion.BackgroundCompilation = false;
            e.ConsultString(Countdown);
            body(e);
        }
        finally
        {
            AtomTable.PoisonDeadIds = poisoned;
            AtomTable.CollectionEnabled = enabled;
        }
    }

    // A suffix no other code interns: the atoms named with it are transient.
    private static string Suffix() => Guid.NewGuid().ToString("N");

    private static int IdOf(string name) => AtomTable.Intern(name).Id;

    private static bool Alive(int id, string name) => AtomTable.ProbeById(id)?.Name == name;

    private static int Fid(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    private static string Name(Solution solution, string variable)
        => Assert.IsType<AtomTerm>(solution[variable]).Name;

    [Fact]
    public void RetiredCodeKeepsItsAtomsWhileAChoicePointResumesIntoIt()
    {
        string s = Suffix();
        Run(e =>
        {
            e.ConsultString($":- public(q/1).\nq(a_{s}). q(b_{s}). q(c_{s}).");
            int fid = Fid("q", 1);
            Assert.True(e.Query("q(_).").Success);
            Assert.True(e.IlPromotion.IsPromoted(fid));
            int b = IdOf("b_" + s), c = IdOf("c_" + s);
            Assert.Contains(b, e.IlPromotion.ClaimedAtoms(fid));
            Assert.Contains(c, e.IlPromotion.ClaimedAtoms(fid));

            var solutions = e.QueryAll("q(X).").GetEnumerator();
            Assert.True(solutions.MoveNext());
            Assert.Equal("a_" + s, Name(solutions.Current, "X"));
            // The version with b and c is replaced: its compiled code is retired,
            // and the choice point of the query above is all that reaches it.
            e.ReconsultString($":- public(q/1).\nq(z_{s}).");
            Assert.False(e.IlPromotion.IsPromoted(fid));
            Assert.Equal(1, e.IlPromotion.RetiredArtifacts);

            AtomTable.RequestCollection();
            Assert.True(Alive(b, "b_" + s));
            Assert.True(Alive(c, "c_" + s));
            Assert.True(solutions.MoveNext());
            Assert.Equal("b_" + s, Name(solutions.Current, "X"));
            Assert.True(solutions.MoveNext());
            Assert.Equal("c_" + s, Name(solutions.Current, "X"));
            Assert.False(solutions.MoveNext());
            solutions.Dispose();

            Assert.Equal(0, e.IlPromotion.RetiredArtifacts);
            AtomTable.RequestCollection();
            Assert.False(Alive(b, "b_" + s));
            Assert.False(Alive(c, "c_" + s));
        });
    }

    [Fact]
    public void ANewInstallWaitsWhileTheRetiredCodeCanBeResumedInto()
    {
        string s = Suffix();
        Run(e =>
        {
            e.ConsultString($":- public(q/1).\nq(a_{s}). q(b_{s}).");
            int fid = Fid("q", 1);
            Assert.True(e.Query("q(_).").Success);
            Assert.True(e.IlPromotion.IsPromoted(fid));

            var solutions = e.QueryAll("q(X).").GetEnumerator();
            Assert.True(solutions.MoveNext());
            e.ReconsultString($":- public(q/1).\nq(z_{s}).");
            Assert.False(e.IlPromotion.IsPromoted(fid));
            // A resume marker names the predicate and a cursor, not a version:
            // while the old code can be resumed into, no new code is installed.
            for (int i = 0; i < 3; i++) Assert.True(e.Query($"q(z_{s}).").Success);
            Assert.False(e.IlPromotion.IsPromoted(fid));
            Assert.True(e.IlPromotion.DeferredInstalls >= 1);
            Assert.True(e.IlPromotion.DeferredCalls >= 1);

            Assert.True(solutions.MoveNext());
            Assert.Equal("b_" + s, Name(solutions.Current, "X"));
            Assert.False(solutions.MoveNext());
            solutions.Dispose();
            Assert.Equal(0, e.IlPromotion.RetiredArtifacts);

            Assert.True(e.Query($"q(z_{s}).").Success);
            Assert.True(e.IlPromotion.IsPromoted(fid));
        });
    }

    [Fact]
    public void TheAtomsOfACalleeTheCodeCopiedAreClaimedWithIt()
    {
        string s = Suffix();
        Run(e =>
        {
            // p's code copies q's clauses (a region, or an inlined callee): q's
            // atoms belong to p's artifact, and q itself is never dispatched.
            e.ConsultString($":- public(p/1).\np(X) :- q(X), X \\== none.\nq(a_{s}). q(b_{s}). q(c_{s}).");
            int p = Fid("p", 1), q = Fid("q", 1);
            Assert.True(e.Query("p(_).").Success);
            Assert.True(e.IlPromotion.IsPromoted(p));
            Assert.False(e.IlPromotion.IsPromoted(q));
            int b = IdOf("b_" + s), c = IdOf("c_" + s);
            Assert.Contains(b, e.IlPromotion.ClaimedAtoms(p));
            Assert.Contains(c, e.IlPromotion.ClaimedAtoms(p));

            var solutions = e.QueryAll("p(X).").GetEnumerator();
            Assert.True(solutions.MoveNext());
            Assert.Equal("a_" + s, Name(solutions.Current, "X"));
            e.ReconsultString($":- public(p/1).\np(X) :- q(X), X \\== none.\nq(z_{s}).");
            Assert.False(e.IlPromotion.IsPromoted(p));
            // The choice point on q's alternatives is p's: a resume marker of p.
            Assert.Equal(1, e.IlPromotion.RetiredArtifacts);

            AtomTable.RequestCollection();
            Assert.True(Alive(b, "b_" + s));
            Assert.True(Alive(c, "c_" + s));
            Assert.True(solutions.MoveNext());
            Assert.Equal("b_" + s, Name(solutions.Current, "X"));
            Assert.True(solutions.MoveNext());
            Assert.Equal("c_" + s, Name(solutions.Current, "X"));
            Assert.False(solutions.MoveNext());
            solutions.Dispose();

            AtomTable.RequestCollection();
            Assert.False(Alive(b, "b_" + s));
        });
    }

    [Fact]
    public void AMethodOnTheStackKeepsItsCodeClaimedWhileItsBuiltinRunsAnotherQuery()
    {
        string s = Suffix();
        Run(e =>
        {
            // The clause retracts itself and has its chunk reclaimed (the chunk's
            // claim goes) while its method runs, which evicts its snapshot; the
            // collection the nested query completes must leave the atom the
            // method still puts.
            e.ConsultString($":- dynamic(r/1).\nr(X) :- retract((r(_) :- _)), garbage_collect_clauses, "
                + "'$art_retired', '$tbl_solve_complete'(('$art_collect', countdown(10), '$art_collected')), "
                + $"X = k_{s}.");
            int fid = Fid("r", 1);
            int k = IdOf("k_" + s);
            Retired.Clear();

            var solution = e.Query("r(X).");
            Assert.True(solution.Success);
            Assert.Equal("k_" + s, Name(solution, "X"));
            Assert.True(Alive(k, "k_" + s));
            Assert.False(e.IlPromotion.IsPromoted(fid));
            // The retired code was still held while its method ran.
            Assert.Equal(new[] { 1 }, Retired);

            // The query is over: nothing names the atom any more.
            Assert.Equal(0, e.IlPromotion.RetiredArtifacts);
            AtomTable.RequestCollection();
            Assert.False(Alive(k, "k_" + s));
        });
    }

    [Fact]
    public void AMethodOnTheStackKeepsItsCodeHeldAfterItsBuiltinEvictedIt()
    {
        string s = Suffix();
        Run(e =>
        {
            // A deterministic builtin leaves no choice point and no marker: the
            // store the method made before calling it is all that names the code.
            e.ConsultString($":- dynamic(r/1).\nr(X) :- assertz(r(z_{s})), '$art_retired', X = k_{s}.");
            int fid = Fid("r", 1);
            Retired.Clear();

            var solution = e.Query("r(X).");
            Assert.True(solution.Success);
            Assert.Equal("k_" + s, Name(solution, "X"));
            Assert.False(e.IlPromotion.IsPromoted(fid));
            Assert.Equal(new[] { 1 }, Retired);
            Assert.Equal(0, e.IlPromotion.RetiredArtifacts);
        });
    }

    [Fact]
    public void TheImagesTheCodeLoadsKeepTheirAtomsWithIt()
    {
        string s = Suffix();
        Run(e =>
        {
            // A ground call argument past the image threshold (an inline =/2
            // builds its term): the code loads the image by reference, and the
            // pool entry the bytecode named goes with the replaced version.
            e.ConsultString($":- public(img/1).\nimg(X) :- hold(f(g_{s}, [h_{s}, 1, 2, 3], t(u, v, w)), X).\n"
                + "img(none).\nhold(T, T).");
            int fid = Fid("img", 1);
            Assert.True(e.Query("img(_).").Success);
            Assert.True(e.IlPromotion.IsPromoted(fid));
            int g = IdOf("g_" + s), h = IdOf("h_" + s);
            Assert.Contains(g, e.IlPromotion.ClaimedAtoms(fid));
            Assert.Contains(h, e.IlPromotion.ClaimedAtoms(fid));

            var solutions = e.QueryAll("img(X).").GetEnumerator();
            Assert.True(solutions.MoveNext());
            e.ReconsultString(":- public(img/1).\nimg(other).");
            Assert.False(e.IlPromotion.IsPromoted(fid));

            AtomTable.RequestCollection();
            Assert.True(Alive(g, "g_" + s));
            Assert.True(Alive(h, "h_" + s));
            Assert.True(solutions.MoveNext());
            Assert.Equal("none", Name(solutions.Current, "X"));
            Assert.False(solutions.MoveNext());
            solutions.Dispose();
            // The atoms stay: the static link pins its literals for the pools'
            // life. The code's own claim is gone.
            Assert.Equal(0, e.IlPromotion.RetiredArtifacts);
        });
    }

    [Fact]
    public void RetiredCodeIsReleasedOnceNothingResumesIntoIt()
    {
        string s = Suffix();
        Run(e =>
        {
            e.ConsultString($":- dynamic(s/1).\ns(a_{s}).");
            e.IlPromotion.EvictionChurnLimit = 100;
            int fid = Fid("s", 1);
            for (int i = 0; i < 12; i++)
            {
                Assert.True(e.Query($"s(a_{s}).").Success);
                Assert.True(e.IlPromotion.IsPromoted(fid));
                Assert.True(e.Query($"assertz(s(b{i}_{s})).").Success);
                Assert.False(e.IlPromotion.IsPromoted(fid));
            }
            Assert.Equal(0, e.IlPromotion.RetiredArtifacts);
            Assert.True(e.IlPromotion.ReleasedArtifacts >= 12);
            Assert.Equal(0, e.IlPromotion.DeferredInstalls);
        });
    }

    // The store that names the running code is emitted before a builtin that
    // may change code, and before no other: the classification is the
    // registry's, not a guess per method.
    [Fact]
    public void TheRunningCodeIsStoredOnlyBeforeABuiltinThatMayChangeCode()
    {
        string dump = Path.Combine(Path.GetTempPath(), $"shumway-artifact-{Guid.NewGuid():N}.txt");
        string? prev = IlPredicateCompiler.IlDumpPath;
        try
        {
            IlPredicateCompiler.IlDumpPath = dump;
            var e = new PrologEngine { Out = new StringWriter() };
            e.IlPromotion.Threshold = 1;
            e.IlPromotion.BackgroundCompilation = false;
            e.ConsultString(":- public(pure/2).\n:- public(mut/1).\n:- dynamic(fact/1).\n"
                + "pure(X, Y) :- atom_length(X, Y).\nmut(X) :- assertz(X).");
            Assert.True(e.Query("pure(abc, 3).").Success);
            Assert.True(e.Query("mut(fact(1)).").Success);
            Assert.True(e.IlPromotion.IsPromoted(Fid("pure", 2)));
            Assert.True(e.IlPromotion.IsPromoted(Fid("mut", 1)));
        }
        finally
        {
            IlPredicateCompiler.IlDumpPath = prev;
        }
        string text = File.ReadAllText(dump);
        File.Delete(dump);
        int pure = text.IndexOf("pure/2", StringComparison.Ordinal);
        int mut = text.IndexOf("mut/1", StringComparison.Ordinal);
        Assert.True(pure >= 0 && mut >= 0, "both methods are in the dump");
        string pureMethod = pure < mut ? text.Substring(pure, mut - pure) : text.Substring(pure);
        string mutMethod = mut < pure ? text.Substring(mut, pure - mut) : text.Substring(mut);
        Assert.DoesNotContain("_ilArtifact", pureMethod);
        Assert.Contains("_ilArtifact", mutMethod);
    }
}
