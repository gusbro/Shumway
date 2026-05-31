using System.IO;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 236: <c>reconsult/1</c> follows classical GProlog / SICStus
/// edit-reload semantics — abolish every predicate whose indicator
/// appears in the file before loading it, leave everything else
/// untouched. Tests cover the .pl path (dynamic, static, multi-pred,
/// negative — predicates not in the file survive), the .shum path,
/// and the consult/1 contrast.
/// </summary>
public class Chunk236Tests
{
    [Fact]
    public void ReconsultFile_ReplacesClausesOfPredicatesInFile()
    {
        var v1 = TempFile(".pl",
            ":- public greet/1.\n"
            + "greet(hello).\n"
            + "greet(world).\n");
        var v2 = TempFile(".pl",
            ":- public greet/1.\n"
            + "greet(bonjour).\n");
        try
        {
            var engine = new PrologEngine();
            engine.ConsultFile(v1);
            engine.ReconsultFile(v2);
            // After reconsult, only the v2 clause should remain.
            var sols = engine.QueryAll("greet(X).")
                .Select(s => s.Bindings["X"].ToString()!).ToList();
            Assert.Equal(new[] { "bonjour" }, sols);
        }
        finally { File.Delete(v1); File.Delete(v2); }
    }

    [Fact]
    public void ReconsultFile_LeavesUnmentionedPredicatesAlone()
    {
        // Two files defining two different predicates. Reconsulting
        // the first file again must NOT touch the second predicate.
        var fileA = TempFile(".pl",
            ":- public foo/1.\n"
            + "foo(1).\n");
        var fileB = TempFile(".pl",
            ":- public bar/1.\n"
            + "bar(99).\n");
        var fileAv2 = TempFile(".pl",
            ":- public foo/1.\n"
            + "foo(2).\n");
        try
        {
            var engine = new PrologEngine();
            engine.ConsultFile(fileA);
            engine.ConsultFile(fileB);
            engine.ReconsultFile(fileAv2);
            // foo replaced, bar survives.
            Assert.Equal(new[] { "2" }, Results(engine, "foo(X).", "X"));
            Assert.Equal(new[] { "99" }, Results(engine, "bar(X).", "X"));
        }
        finally { File.Delete(fileA); File.Delete(fileB); File.Delete(fileAv2); }
    }

    [Fact]
    public void ConsultFile_DuplicatesClauses_ContrastWithReconsult()
    {
        // Pure consult/1 of the same file twice should accumulate
        // clauses — sanity check that reconsult's de-dup behavior is
        // actually a difference, not a coincidence.
        var f = TempFile(".pl",
            "fact(1).\n"
            + "fact(2).\n");
        try
        {
            var engine = new PrologEngine();
            engine.ConsultFile(f);
            engine.ConsultFile(f);
            // Each consult appends, so we should see four solutions.
            Assert.Equal(new[] { "1", "2", "1", "2" },
                Results(engine, "fact(X).", "X"));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void ReconsultBuiltin_ReplacesViaQuery()
    {
        var v1 = TempFile(".pl",
            ":- public p/1.\n"
            + "p(a).\n"
            + "p(b).\n");
        var v2 = TempFile(".pl",
            ":- public p/1.\n"
            + "p(c).\n");
        try
        {
            var engine = new PrologEngine();
            Assert.True(engine.QueryAll(
                $"consult('{v1.Replace("\\", "\\\\")}').").Any());
            Assert.True(engine.QueryAll(
                $"reconsult('{v2.Replace("\\", "\\\\")}').").Any());
            Assert.Equal(new[] { "c" }, Results(engine, "p(X).", "X"));
        }
        finally { File.Delete(v1); File.Delete(v2); }
    }

    [Fact]
    public void ReconsultFile_ReplacesDynamicPredicateClauses()
    {
        var v1 = TempFile(".pl",
            ":- dynamic(counter/1).\n"
            + "counter(1).\n");
        var v2 = TempFile(".pl",
            ":- dynamic(counter/1).\n"
            + "counter(42).\n");
        try
        {
            var engine = new PrologEngine();
            engine.ConsultFile(v1);
            // assertz a runtime clause too — reconsult should also wipe it,
            // since counter/1 appears in the file.
            engine.QueryAll("assertz(counter(7)).").ToList();
            var before = Results(engine, "counter(X).", "X");
            Assert.Equal(new[] { "1", "7" }, before);

            engine.ReconsultFile(v2);
            Assert.Equal(new[] { "42" }, Results(engine, "counter(X).", "X"));
        }
        finally { File.Delete(v1); File.Delete(v2); }
    }

    [Fact]
    public void ReconsultFile_ShumBundle_ReplacesPredicates()
    {
        // .shum path: write a bundle defining hi/1 with one answer,
        // load it via consult, then reconsult a second bundle that
        // defines hi/1 differently and verify it replaced.
        var b1 = MakeBundleFile(":- public hi/1.\nhi(one).\nhi(two).\n");
        var b2 = MakeBundleFile(":- public hi/1.\nhi(three).\n");
        try
        {
            var engine = new PrologEngine();
            engine.ConsultFile(b1);
            Assert.Equal(new[] { "one", "two" },
                Results(engine, "hi(X).", "X"));
            engine.ReconsultFile(b2);
            Assert.Equal(new[] { "three" },
                Results(engine, "hi(X).", "X"));
        }
        finally { File.Delete(b1); File.Delete(b2); }
    }

    private static System.Collections.Generic.List<string> Results(
        PrologEngine engine, string query, string var) =>
        engine.QueryAll(query).Select(s => s.Bindings[var].ToString()!).ToList();

    private static string TempFile(string suffix, string content)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"shumway-chunk236-{System.Guid.NewGuid():N}{suffix}");
        File.WriteAllText(path, content);
        return path;
    }

    private static string MakeBundleFile(string source)
    {
        var bundle = new Bundle(new[] { new BundleEntry("user", source) });
        var path = Path.Combine(Path.GetTempPath(),
            $"shumway-chunk236-{System.Guid.NewGuid():N}.shum");
        BundleWriter.WriteToFile(bundle, path);
        return path;
    }
}
