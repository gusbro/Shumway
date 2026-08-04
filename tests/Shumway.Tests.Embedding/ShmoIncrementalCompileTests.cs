using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// The <c>--consult</c> separate-compilation invariant: every module's
/// <c>.shmo</c> is a self-contained function of its own source. A dependency
/// shared by several roots compiles to the SAME object whether the roots are
/// compiled in one batch or separately, and each object carries its OWN
/// dynamic seeds and operators — so compiling <c>a.pl</c> then <c>b.pl</c>
/// separately yields the same object set as one batch, with no double-seeding,
/// and any reachability-complete subset links correctly.
/// </summary>
public class ShmoIncrementalCompileTests
{
    private static string WriteTemp(string dir, string name, string content)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class Fixture : System.IDisposable
    {
        public readonly string Root = Path.Combine(
            Path.GetTempPath(), "shmo_inc_" + Path.GetRandomFileName());
        public readonly string LibDir;
        public readonly string A;
        public readonly string B;

        public Fixture()
        {
            LibDir = Path.Combine(Root, "lib");
            Directory.CreateDirectory(LibDir);
            WriteTemp(LibDir, "sharedlib.pl", """
                :- module(sharedlib, [p/1]).
                :- dynamic(d/1).
                :- op(700, xfx, ~~).
                d(1).
                d(2).
                p(X) :- d(X).
                """);
            A = WriteTemp(Root, "a.pl", """
                :- module(a, [ga/1]).
                :- use_module(library(sharedlib)).
                ga(X) :- p(X).
                """);
            B = WriteTemp(Root, "b.pl", """
                :- module(b, [gb/1]).
                :- use_module(library(sharedlib)).
                gb(X) :- p(X).
                """);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static ShmoObject Module(
        System.Collections.Generic.List<(string ModuleName, ShmoObject Object, System.DateTime SourceTimeUtc, bool IsRoot)> objs,
        string name)
        => objs.Single(o => o.ModuleName == name).Object;

    [Fact]
    public void SharedDependency_IsByteIdentical_SeparateVsBatch()
    {
        using var f = new Fixture();
        var libs = new[] { f.LibDir };
        var errB = new System.Collections.Generic.List<ShmoCompileError>();
        var errA = new System.Collections.Generic.List<ShmoCompileError>();
        var errS = new System.Collections.Generic.List<ShmoCompileError>();

        var batch = ShmoViaConsult.CompileMany(new[] { f.A, f.B }, libs, ShmoBuildMode.Release, errB);
        var sepA = ShmoViaConsult.CompileMany(new[] { f.A }, libs, ShmoBuildMode.Release, errA);
        var sepB = ShmoViaConsult.CompileMany(new[] { f.B }, libs, ShmoBuildMode.Release, errS);

        Assert.Empty(errB);
        Assert.Empty(errA);
        Assert.Empty(errS);

        // The shared dependency appears exactly once in the batch.
        Assert.Single(batch, o => o.ModuleName == "sharedlib");

        // ... and its object is byte-identical whether produced in the batch
        // or by either separate root — self-contained, context-independent.
        byte[] libBatch = ShmoWriter.ToBytes(Module(batch, "sharedlib"));
        byte[] libFromA = ShmoWriter.ToBytes(Module(sepA, "sharedlib"));
        byte[] libFromB = ShmoWriter.ToBytes(Module(sepB, "sharedlib"));
        Assert.Equal(libBatch, libFromA);
        Assert.Equal(libBatch, libFromB);
    }

    [Fact]
    public void Dependency_CarriesItsOwnSeedsAndOperators_RootsDoNot()
    {
        using var f = new Fixture();
        var errors = new System.Collections.Generic.List<ShmoCompileError>();
        var objs = ShmoViaConsult.CompileMany(
            new[] { f.A, f.B }, new[] { f.LibDir }, ShmoBuildMode.Release, errors);
        Assert.Empty(errors);

        ShmoObject lib = Module(objs, "sharedlib");
        ShmoObject a = Module(objs, "a");
        ShmoObject b = Module(objs, "b");

        // The dependency owns d/1's seeds — and only it does (no double-seeding
        // across the roots that pulled it in).
        Assert.Contains(lib.DynamicSeeds, s => s.Indicator.Name == "d" && s.Indicator.Arity == 1);
        Assert.DoesNotContain(a.DynamicSeeds, s => s.Indicator.Name == "d");
        Assert.DoesNotContain(b.DynamicSeeds, s => s.Indicator.Name == "d");

        // The dependency carries its own operator — so it is available even
        // when linked without the root that introduced it.
        Assert.Contains(lib.Operators, o => o.Name == "~~");
        Assert.DoesNotContain(a.Operators, o => o.Name == "~~");
        Assert.DoesNotContain(b.Operators, o => o.Name == "~~");
    }
}
