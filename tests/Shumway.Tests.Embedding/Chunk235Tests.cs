using System.IO;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 235: <c>consult/1</c> and <c>reconsult/1</c> builtins (plus the
/// underlying <see cref="PrologEngine.ConsultFile"/> public method). A
/// thin shim over <see cref="PrologEngine.ConsultString"/> and
/// <see cref="PrologEngine.LoadBundle"/>; tests cover the routing, the
/// reconsult-as-synonym contract and the ISO error shapes.
/// </summary>
public class Chunk235Tests
{
    [Fact]
    public void ConsultFile_LoadsPrologSource_AndPredicateIsCallable()
    {
        var tmp = Path.GetTempFileName() + ".pl";
        File.WriteAllText(tmp,
            ":- public greet/1.\n"
            + "greet(hello).\n"
            + "greet(world).\n");
        try
        {
            var engine = new PrologEngine();
            engine.ConsultFile(tmp);
            var sols = engine.QueryAll("greet(X).")
                .Select(s => s.Bindings["X"].ToString()!).ToList();
            Assert.Equal(new[] { "hello", "world" }, sols);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Consult_WithoutExtension_TriesAddingPl()
    {
        // SWI-style: consult(algo) resolves to algo.pl when `algo` itself does
        // not exist. Covers the builtin, the API and reconsult/1.
        string dir = Path.Combine(Path.GetTempPath(), "shumway_plext_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string plPath = Path.Combine(dir, "algo.pl");
        string bare = Path.Combine(dir, "algo").Replace('\\', '/');
        File.WriteAllText(plPath, ":- public saludo/1.\nsaludo(hola).\n");
        try
        {
            var engine = new PrologEngine();
            Assert.Contains(engine.QueryAll($"consult('{bare}'), saludo(X), X == hola."), s => s.Success);

            var engine2 = new PrologEngine();
            engine2.ConsultFile(Path.Combine(dir, "algo"));
            Assert.Contains(engine2.QueryAll("saludo(hola)."), s => s.Success);

            // An extensionless file that EXISTS still wins over the .pl probe.
            string exact = Path.Combine(dir, "exacto");
            File.WriteAllText(exact, ":- public pino/1.\npino(si).\n");
            File.WriteAllText(exact + ".pl", ":- public pino/1.\npino(no).\n");
            var engine3 = new PrologEngine();
            engine3.ConsultFile(exact);
            Assert.Contains(engine3.QueryAll("pino(si)."), s => s.Success);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ConsultBuiltin_LoadsFileFromInsideQuery()
    {
        var tmp = Path.GetTempFileName() + ".pl";
        File.WriteAllText(tmp,
            ":- public ping/1.\n"
            + "ping(pong).\n");
        try
        {
            var engine = new PrologEngine();
            // consult/1 as a query: load, then query the new predicate.
            Assert.True(engine.QueryAll($"consult('{tmp.Replace("\\", "\\\\")}').").Any());
            var sols = engine.QueryAll("ping(X).")
                .Select(s => s.Bindings["X"].ToString()!).ToList();
            Assert.Equal(new[] { "pong" }, sols);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void ReconsultBuiltin_IsSynonymForConsult()
    {
        var tmp = Path.GetTempFileName() + ".pl";
        File.WriteAllText(tmp,
            ":- public q/1.\n"
            + "q(42).\n");
        try
        {
            var engine = new PrologEngine();
            Assert.True(engine.QueryAll($"reconsult('{tmp.Replace("\\", "\\\\")}').").Any());
            var sols = engine.QueryAll("q(X).")
                .Select(s => s.Bindings["X"].ToString()!).ToList();
            Assert.Equal(new[] { "42" }, sols);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Consult_UnboundArg_ThrowsInstantiationError()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<PrologRuntimeException>(
            () => engine.QueryAll("consult(X).").ToList());
        Assert.Contains("instantiation_error", ex.Message);
    }

    [Fact]
    public void Consult_NonAtomArg_ThrowsTypeError()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<PrologRuntimeException>(
            () => engine.QueryAll("consult(42).").ToList());
        Assert.Contains("type_error(atom", ex.Message);
    }

    [Fact]
    public void Consult_MissingFile_ThrowsExistenceError()
    {
        var engine = new PrologEngine();
        // Path almost certainly doesn't exist.
        var missing = Path.Combine(Path.GetTempPath(), "shumway_chunk235_no_such.pl");
        if (File.Exists(missing)) File.Delete(missing);
        var ex = Assert.Throws<PrologRuntimeException>(
            () => engine.QueryAll($"consult('{missing.Replace("\\", "\\\\")}').").ToList());
        Assert.Contains("existence_error", ex.Message);
        Assert.Contains("source_sink", ex.Message);
    }

    [Fact]
    public void ConsultFile_RoutesShumExtensionThroughLoadBundle()
    {
        // Tiny .shum bundle: a one-predicate source written via the
        // bundler API, then ConsultFile-d. If routing is correct, the
        // predicate is callable on the receiving engine.
        var bundle = new Bundle(new[]
        {
            new BundleEntry("user",
                ":- public hi/1.\n"
                + "hi(there).\n"),
        });
        var bundlePath = Path.Combine(Path.GetTempPath(),
            $"shumway-chunk235-{System.Guid.NewGuid():N}.shum");
        try
        {
            BundleWriter.WriteToFile(bundle, bundlePath);

            var consumer = new PrologEngine();
            consumer.ConsultFile(bundlePath);

            var sols = consumer.QueryAll("hi(X).")
                .Select(s => s.Bindings["X"].ToString()!).ToList();
            Assert.Equal(new[] { "there" }, sols);
        }
        finally { if (File.Exists(bundlePath)) File.Delete(bundlePath); }
    }
}
