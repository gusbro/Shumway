using System;
using System.IO;
using Shumway.Builtins;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-003: the atoms an engine's code names are transient, and the code
/// claims them while it can run: consulted code while a buffer holds it, an
/// asserted clause until its chunk is reclaimed, a query's code until the query
/// ends, an image while its pool entry lives. Dead ids are poisoned: an atom the
/// code still names and no claim kept fails where it is read.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class EngineCodeAtomTests
{
    static EngineCodeAtomTests()
    {
        BuiltinsRegistry.Register("$code_atom_collect", 0, _ =>
        {
            AtomTable.RequestCollection();
            return true;
        });
        BuiltinsRegistry.Register("$code_atom_count", 0, _ =>
        {
            Counts.Add(AtomTable.TransientCount);
            return true;
        });
        BuiltinsRegistry.Register("$code_atom_claims", 0, _ =>
        {
            Counts.Add(AtomTable.ClaimsOf(Watched));
            return true;
        });
    }

    private static readonly System.Collections.Generic.List<int> Counts = new();
    // The atom '$code_atom_claims' reports on.
    private static int Watched;

    private static void Run(Action<PrologEngine> body)
    {
        bool enabled = AtomTable.CollectionEnabled, poisoned = AtomTable.PoisonDeadIds;
        AtomTable.CollectionEnabled = true;
        AtomTable.PoisonDeadIds = true;
        try { body(new PrologEngine { Out = new StringWriter() }); }
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

    [Fact]
    public void AnAtomOnlyConsultedCodeNamesIsTransientAndLives()
    {
        string s = Suffix();
        Run(e =>
        {
            e.ConsultString($"p(c_{s}). q(f(g_{s})).");
            Assert.True(e.Query("p(_), q(_).").Success);
            int c = IdOf("c_" + s), g = IdOf("g_" + s);
            Assert.True(AtomTable.IsTransient(c));

            AtomTable.RequestCollection();

            Assert.True(Alive(c, "c_" + s));
            Assert.True(Alive(g, "g_" + s));
            Assert.True(e.Query($"p(X), X == c_{s}, q(f(Y)), Y == g_{s}.").Success);
        });
    }

    [Fact]
    public void CodeThatNamesManyAtomsClaimsEveryOne()
    {
        string s = Suffix();
        string Args(string prefix)
        {
            var args = new string[40];
            for (int i = 0; i < args.Length; i++) args[i] = $"{prefix}{i}_{s}";
            return string.Join(", ", args);
        }
        Run(e =>
        {
            // More atoms than a walk of the code keeps inline: a consulted dynamic
            // fact, walked as a chunk of the buffer, and an asserted one.
            e.ConsultString($":- dynamic(w/40). w({Args("c")}).");
            Assert.True(e.Query($"assertz(w({Args("a")})).").Success);
            int c0 = IdOf($"c0_{s}"), c39 = IdOf($"c39_{s}"), a0 = IdOf($"a0_{s}"), a39 = IdOf($"a39_{s}");

            AtomTable.RequestCollection();

            Assert.True(Alive(c0, $"c0_{s}") && Alive(c39, $"c39_{s}"));
            Assert.True(Alive(a0, $"a0_{s}") && Alive(a39, $"a39_{s}"));
            Assert.True(e.Query($"w({Args("c")}), w({Args("a")}).").Success);
        });
    }

    [Fact]
    public void AnAssertedFactKeepsItsAtomsUntilItsChunkIsReclaimed()
    {
        string s = Suffix();
        Run(e =>
        {
            e.ConsultString(":- dynamic(fact/1).");
            Assert.True(e.Query($"between(1, 6, I), number_codes(I, Cs), atom_codes(N, Cs), "
                + $"atom_concat(a_{s}_, N, A), assertz(fact(A)), fail ; true.").Success);
            int first = IdOf($"a_{s}_1");

            AtomTable.RequestCollection();
            Assert.True(Alive(first, $"a_{s}_1"));
            Assert.True(e.Query($"fact(X), X == a_{s}_1.").Success);

            Assert.True(e.Query("retract(fact(_)), fail ; true.").Success);
            Assert.True(e.Query("\\+ fact(_).").Success);
            AtomTable.RequestCollection();

            Assert.False(Alive(first, $"a_{s}_1"));
        });
    }

    [Fact]
    public void AnAssertAndRetractLoopKeepsTheTableBounded()
    {
        string s = Suffix();
        Run(e =>
        {
            e.ConsultString(":- dynamic(item/1).");
            int before = 0;
            for (int round = 0; round < 6; round++)
            {
                Assert.True(e.Query($"between(1, 200, I), number_codes(I, Cs), atom_codes(N, Cs), "
                    + $"atom_concat(r{round}_{s}_, N, A), assertz(item(A)), fail ; true.").Success);
                Assert.True(e.Query("retract(item(_)), fail ; true.").Success);
                AtomTable.RequestCollection();
                if (round == 1) before = AtomTable.TransientCount;
            }
            Assert.True(AtomTable.TransientCount <= before + 50,
                $"transient atoms grew from {before} to {AtomTable.TransientCount}");
        });
    }

    [Fact]
    public void AQueryKeepsTheAtomsOfItsOwnCodeWhileItRuns()
    {
        string s = Suffix();
        Run(e =>
        {
            e.ConsultString("countdown(0) :- !. countdown(N) :- M is N - 1, countdown(M).");
            Assert.True(e.Query($"'$code_atom_collect', countdown(10), atom_length(lit_{s}, L), L > 0, "
                + $"X = lit_{s}, X == lit_{s}.").Success);
        });
    }

    [Fact]
    public void AReconsultedVersionGivesUpItsAtomsOnceNoQueryRunsOnIt()
    {
        string s = Suffix();
        Run(e =>
        {
            e.ReconsultString($"r(old_{s}).");
            Assert.True(e.Query($"r(old_{s}).").Success);
            int old = IdOf("old_" + s);

            e.ReconsultString($"r(new_{s}).");
            Assert.True(e.Query($"r(new_{s}).").Success);
            int fresh = IdOf("new_" + s);
            AtomTable.RequestCollection();

            Assert.False(Alive(old, "old_" + s));
            Assert.True(Alive(fresh, "new_" + s));
        });
    }

    private const string Countdown = "countdown(0) :- !. countdown(N) :- M is N - 1, countdown(M).";

    // A file of Prolog text in a directory the test removes, by a path Prolog reads.
    private static string WriteFile(string dir, string name, string text)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return path.Replace('\\', '/');
    }

    private static void InTempDir(Action<string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "shumway-atoms-" + Suffix());
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AVersionAReconsultReplacesInsideARunningQueryGivesUpItsAtoms()
    {
        string s = Suffix();
        InTempDir(dir => Run(e =>
        {
            string first = WriteFile(dir, "first.pl", $"r(old_{s}).");
            string second = WriteFile(dir, "second.pl", $"r(new_{s}).");
            Counts.Clear();
            Watched = IdOf("old_" + s);
            Assert.True(e.Query($"reconsult('{first}'), '$code_atom_claims', "
                + $"reconsult('{second}'), '$code_atom_claims'.").Success);
            Assert.Equal(new[] { 1, 0 }, Counts);
        }));
    }

    [Fact]
    public void AVersionRunningAtTheMarkKeepsItsAtomsAfterAReconsultReplacesIt()
    {
        string s = Suffix();
        InTempDir(dir => Run(e =>
        {
            string next = WriteFile(dir, "next.pl", "step.");
            e.ConsultString(Countdown);
            // The collection marks at the first call of the old body after the
            // request: the program counter is in it.
            e.ConsultString($"step :- reconsult('{next}'), '$code_atom_collect', countdown(10), "
                + $"atom_length(old_{s}, _).");
            int old = IdOf("old_" + s);
            Assert.True(e.Query("step, step.").Success);

            AtomTable.RequestCollection();
            Assert.False(Alive(old, "old_" + s));
        }));
    }

    [Fact]
    public void AVersionStillRunningKeepsItsAtomsAfterAReconsultReplacesIt()
    {
        string s = Suffix();
        InTempDir(dir => Run(e =>
        {
            string next = WriteFile(dir, "next.pl", "step.");
            e.ConsultString(Countdown);
            // The collection marks while deeper runs: the return into the old body
            // waits in deeper's frame on the stack, not in a register.
            e.ConsultString("deeper :- '$code_atom_collect', countdown(10), countdown(1).");
            e.ConsultString($"step :- reconsult('{next}'), deeper, atom_length(old_{s}, _).");
            int old = IdOf("old_" + s);
            // The query names nothing of the old version: only what it runs keeps it.
            Assert.True(e.Query("step, step.").Success);

            AtomTable.RequestCollection();
            Assert.False(Alive(old, "old_" + s));
        }));
    }

    [Fact]
    public void AClauseThatRetractsItselfKeepsItsAtomsWhileItRuns()
    {
        string s = Suffix();
        Run(e =>
        {
            e.ConsultString(Countdown + " :- dynamic(d/1).");
            // Heads the chain cannot index, the rule last of five so no choice point
            // is left in the chain while it runs: its fourth retract lets the chain
            // reclaim the dead chunks, its own among them. Its own retract runs
            // under \+ \+, so the copy of its body the retract unifies is undone.
            Assert.True(e.Query("assertz((d(_) :- fail)), assertz((d(_) :- fail)), "
                + "assertz((d(_) :- fail)), assertz((d(_) :- fail)), "
                + "assertz((d(X) :- X == go, \\+ \\+ retract((d(Y) :- Y == go, _)), "
                + "once(retract((d(_) :- fail))), once(retract((d(_) :- fail))), "
                + "once(retract((d(_) :- fail))), '$code_atom_collect', countdown(10), "
                + $"atom_length(chunk_{s}, _))).").Success);
            int chunk = IdOf("chunk_" + s);
            long reclaims = e.ChainReclaims;
            Assert.True(e.Query("d(go), \\+ d(go).").Success);
            Assert.True(e.ChainReclaims > reclaims);

            // The chain gave the chunk up: once nothing runs it, its atom goes.
            AtomTable.RequestCollection();
            Assert.False(Alive(chunk, "chunk_" + s));
        });
    }

    [Fact]
    public void AQueryThatReconsultsFileAfterFileKeepsTheTableBounded()
    {
        string s = Suffix();
        Counts.Clear();
        InTempDir(dir => Run(e =>
        {
            e.ConsultString(Countdown);
            var loop = new System.Text.StringBuilder();
            for (int round = 0; round < 8; round++)
            {
                var facts = new System.Text.StringBuilder();
                for (int i = 0; i < 100; i++) facts.Append($"fact(f{round}_{i}_{s}).\n");
                string file = WriteFile(dir, $"facts{round}.pl", facts.ToString());
                loop.Append($"reconsult('{file}'), \\+ \\+ (findall(X, fact(X), L), length(L, 100)), "
                    + "'$code_atom_collect', countdown(5), ");
                if (round == 2) loop.Append("'$code_atom_count', ");
            }
            loop.Append("'$code_atom_count'.");
            Assert.True(e.Query(loop.ToString()).Success);
            Assert.True(Counts[1] <= Counts[0] + 50,
                $"transient atoms grew from {Counts[0]} to {Counts[1]} while the query ran");
        }));
    }

    [Fact]
    public void AnImageKeepsItsAtomsWhileItsCodeLives()
    {
        string s = Suffix();
        Run(e =>
        {
            // A ground compound passed to a call is copied from an image (ADR-063).
            e.ConsultString($"id(X, X). t(X) :- id(g(img_{s}, [1, 2, 3, 4], h(k_{s})), X).");
            Assert.True(e.Query("t(_).").Success);
            int img = IdOf("img_" + s), k = IdOf("k_" + s);

            AtomTable.RequestCollection();

            Assert.True(Alive(img, "img_" + s));
            Assert.True(Alive(k, "k_" + s));
            Assert.True(e.Query($"t(g(A, _, h(B))), A == img_{s}, B == k_{s}.").Success);
        });
    }
}
