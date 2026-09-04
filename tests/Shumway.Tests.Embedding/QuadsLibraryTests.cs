using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>library(quads) — Neumerkel's machine-readable test transcripts
/// (issue #69): importing the library activates `?-` (xfx) and `|` (xfy)
/// for the importer, a stateful term_expansion turns each quad into inert
/// facts (a transcript is not a program — its expected blocks would
/// otherwise define ;/2), and run_quads/0 checks every loaded quad's goal
/// outcome against its sanctioned classes. The quad text here is SYNTHETIC,
/// mirroring the published format — the real files live outside the
/// repo.</summary>
public sealed class QuadsLibraryTests
{
    private static (PrologEngine Engine, System.IO.StringWriter Out) Loaded()
    {
        var w = new System.IO.StringWriter();
        var e = new PrologEngine { Out = w };
        Assert.True(e.Query("use_module(library(quads)).").Success);
        return (e, w);
    }

    private static string QuadFile(string content)
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"quads_pin_{System.Guid.NewGuid():N}.pl");
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void TheWorkflowOfTheIssue()
    {
        // use_module, consult the transcript, run_quads — the whole UX.
        var (e, w) = Loaded();
        string f = QuadFile(
            "% synthetic quads\n" +
            "1 ?- atom_length(abc, L).\n" +
            "      L = 3.\n" +
            "2 ?- atom_length(A, N).\n" +
            "      instantiation_error.\n" +
            "a3 ?- atom(abc).\n" +
            "      true.\n" +
            "4 ?- atom(1).\n" +
            "      false.\n");
        try
        {
            Assert.True(e.Query($"consult('{f.Replace('\\', '/')}').").Success);
            Assert.True(e.Query("run_quads.").Success);
            Assert.Contains("quads: 4/4", w.ToString());
        }
        finally { System.IO.File.Delete(f); }
    }

    [Fact]
    public void AlternativesAndFailuresReport()
    {
        var (e, w) = Loaded();
        string f = QuadFile(
            "1 ?- atom_length(abc, L).\n" +
            "      type_error(oops, x)\n" +
            "   |  false.\n" +          // neither matches a plain success
            "2 ?- atom(1).\n" +
            "      false\n" +
            "   |  type_error(atom, 1). % lenient alternative set\n");
        try
        {
            Assert.True(e.Query($"consult('{f.Replace('\\', '/')}').").Success);
            Assert.True(e.Query("run_quads.").Success);
            string s = w.ToString();
            Assert.Contains("quads: 1/2", s);
            Assert.Contains("failing (1): [1]", s);
        }
        finally { System.IO.File.Delete(f); }
    }

    [Fact]
    public void ATranscriptIsNotAProgram()
    {
        // The expected block `L = 3.` must never define =/2 — and a normal
        // consult AFTER a quad file stays untouched (the pending slot is
        // keyed by file, so nothing leaks).
        var (e, _) = Loaded();
        string f = QuadFile("1 ?- atom_length(abc, L).\n      L = 3.\n");
        try
        {
            Assert.True(e.Query($"consult('{f.Replace('\\', '/')}').").Success);
            Assert.True(e.Query("X = 1, X == 1.").Success);   // =/2 intact
            e.ConsultString("normal_fact(after).\n");
            Assert.True(e.Query("normal_fact(after).").Success);
        }
        finally { System.IO.File.Delete(f); }
    }

    [Fact]
    public void RunQuadsById_AndClearQuads()
    {
        var (e, w) = Loaded();
        string f = QuadFile(
            "1 ?- atom(a).\n      true.\n" +
            "2 ?- atom(1).\n      true.\n");   // wrong on purpose
        try
        {
            Assert.True(e.Query($"consult('{f.Replace('\\', '/')}').").Success);
            Assert.True(e.Query("run_quads(1).").Success);
            Assert.Contains("quads: 1/1", w.ToString());
            Assert.True(e.Query("clear_quads.").Success);
            Assert.True(e.Query("run_quads.").Success);
            Assert.Contains("quads: 0/0", w.ToString());
        }
        finally { System.IO.File.Delete(f); }
    }

    [Fact]
    public void AnIdentifierIsAnyGroundTerm()
    {
        // Issue #84: the published suites key a test by whatever identifies
        // it, up to `16, "7.8.3.4#9"` — a comma term naming the clause of the
        // standard under test. Rejecting those did not skip the test: the
        // transcript reached the compiler, which read the line as a clause
        // for ,/2 and refused it, and the file scored 0/0.
        var (e, w) = Loaded();
        string f = QuadFile(
            "16, \"7.8.3.4#9\" ?- atom_length(_A, _).\n      instantiation_error.\n" +
            "f(1)-g ?- atom(a).\n      true.\n" +
            "[1,2] ?- atom(1).\n      false.\n");
        try
        {
            Assert.True(e.Query($"consult('{f.Replace('\\', '/')}').").Success);
            Assert.True(e.Query("run_quads.").Success);
            Assert.Contains("quads: 3/3", w.ToString());
            // A compound id selects its own test.
            Assert.True(e.Query("run_quads(f(1)-g).").Success);
            Assert.Contains("quads: 1/1", w.ToString());
        }
        finally { System.IO.File.Delete(f); }
    }

    [Fact]
    public void AnIdentifierSpanningLinesIsStillOneTerm()
    {
        // The id and its `?-` need not share a line: a transcript is read
        // as terms, not as lines. This is how the published file writes it.
        var (e, w) = Loaded();
        string f = QuadFile(
            "16, \"7.8.3.4#9\"\n?- atom_length(_A, _).\n   instantiation_error.\n");
        try
        {
            Assert.True(e.Query($"consult('{f.Replace('\\', '/')}').").Success);
            Assert.True(e.Query("run_quads.").Success);
            Assert.Contains("quads: 1/1", w.ToString());
        }
        finally { System.IO.File.Delete(f); }
    }

    [Fact]
    public void AnOutputsExpectationClassifiesByItsOutcome()
    {
        // `outputs(Text), Outcome` says the goal writes Text and THEN
        // behaves as Outcome. Unclassified, such an alternative fell through
        // to the lenient catch-all and the test passed whatever happened.
        // Both halves are checked now, so both goals here really do write
        // the text and only the OUTCOME tells the two quads apart.
        var (e, w) = Loaded();
        string f = QuadFile(
            "1 ?- put_char(x), atom_length(_A, _).\n"
            + "      outputs(\"x\"), instantiation_error.\n"
            + "2 ?- put_char(x), atom(a).\n"
            + "      outputs(\"x\"), type_error(evaluable, foo/0).\n");
        try
        {
            Assert.True(e.Query($"consult('{f.Replace('\\', '/')}').").Success);
            Assert.True(e.Query("run_quads.").Success);
            // The first matches its error, the second does not — where a
            // lenient classification passed both.
            Assert.Contains("quads: 1/2", w.ToString());
        }
        finally { System.IO.File.Delete(f); }
    }

    [Fact]
    public void CoroutiningComesAlong()
    {
        // The published suites lean on freeze/2 (length 29-31); the library
        // must bring library(coroutining) itself — the browser top level has
        // no --quad flag to do it, and without it those goals raise
        // existence_error instead of running.
        var (e, w) = Loaded();
        string f = QuadFile(
            "1 ?- freeze(L, L = []), length(L, L).\n      false.\n" +
            "2 ?- dif(X, a), X = a.\n      false.\n");
        try
        {
            Assert.True(e.Query($"consult('{f.Replace('\\', '/')}').").Success);
            Assert.True(e.Query("run_quads.").Success);
            Assert.Contains("quads: 2/2", w.ToString());
        }
        finally { System.IO.File.Delete(f); }
    }

    [Fact]
    public void TheOperatorsAreImporterScoped()
    {
        var (e, _) = Loaded();
        Assert.True(e.Query("current_op(1200, xfx, ?-).").Success);
        Assert.True(e.Query("current_op(1100, xfy, '|').").Success);
        // A fresh engine without the import keeps the strict default.
        var bare = new PrologEngine { Out = new System.IO.StringWriter() };
        Assert.False(bare.Query("current_op(_, xfx, ?-).").Success);
        Assert.False(bare.Query("current_op(_, _, '|').").Success);
    }
}
