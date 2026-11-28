using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The text a module carries and the code it was compiled to are
/// independent. A release object compiled with its text (<c>shumway-compile
/// --with-source</c>) carries the text into the bundle, and the bundle loads
/// from the compiled code, in a debug session too: the text is there to be
/// read. Debuggable code runs in a debug session; outside one it cannot be
/// debugged, so the text its debug build compiled is consulted, to run as
/// code that can be promoted. The tests give an object a text that says
/// something else than its code, so the answer shows which of the two ran.</summary>
public sealed class WithSourceBundleTests : IDisposable
{
    private const string Alpha = @":- module(alpha, [a/1, b/1]).
% the comment stays
a(1).
b(X) :- a(X).
";

    // main/1 is local: an entry point the link has to promote.
    private const string Prog = @"main(X) :- aux(X).
aux(1).
";

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_with_source_" + Guid.NewGuid().ToString("N"));

    public WithSourceBundleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static ShmoObject Compiled(string text, string module, bool withSource = true,
        ShmoBuildMode mode = ShmoBuildMode.Release)
        => ShmoCompiler.CompileSource(text, module, mode, withSource: withSource);

    // The same object with another text.
    private static ShmoObject Retexted(ShmoObject o, string text) => new(
        o.ModuleName, text, o.Bytecode, o.Defined, o.EnsureLinked, o.CallGraph, o.QualifiedRefs,
        o.BuildMode, o.DynamicSeeds, o.ClauseTerms, o.ArityCompat, o.NativeBlocks,
        o.NativeFunctions, o.NativeDecls, o.Operators, o.IsExportQualified, o.Exports,
        o.Imports, o.LibraryDeps, o.Dialect, o.GeneratorVersion, o.Flags, o.InitializationGoals);

    private static Bundle Linked(ShmoObject obj, bool strip = false, PredicateRef? entry = null)
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            EntryPoints = entry is { } e ? new[] { e } : Array.Empty<PredicateRef>(),
            Library = entry is null,
            AllowUndefined = entry is null,
            StripSource = strip,
            BakePrelude = entry is not null,
            IncludeCompiledIl = false,
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        return BundleReader.FromBytes(link.Bytes!);
    }

    private static BundleEntry Entry(Bundle bundle, string module)
        => bundle.Entries.Single(e => e.ModuleName == module);

    // A promoted entry point is public in the entry, as its recompiled head is.
    private static PredicateVisibility VisibilityOf(Bundle bundle, string module, string name, int arity)
        => Entry(bundle, module).Defined.Single(d => d.Indicator == new PredicateRef(name, arity)).Visibility;

    private static bool Debuggable(Bundle bundle, string module)
        => CompiledModuleCodec.Decode(Entry(bundle, module).CompiledBytecode!)
            .Predicates.Any(p => p.IsDebuggable);

    private static string? Answer(PrologEngine engine, string query)
    {
        var r = engine.Query(query);
        return r.Success ? r.Bindings["X"].ToString() : null;
    }

    private static PrologEngine Loaded(Bundle bundle, bool debug = false)
    {
        var engine = new PrologEngine();
        if (debug)
        {
            engine.Flags.EmitDebugInfo = true;
            engine.Flags.DebugCodegen = true;
        }
        engine.LoadBundle(bundle);
        return engine;
    }

    [Fact]
    public void TheObjectKeepsItsTextAndTheReleaseCode()
    {
        var with = Compiled(Alpha, "alpha");
        var without = Compiled(Alpha, "alpha", withSource: false);
        Assert.Equal(ShmoBuildMode.Release, with.BuildMode);
        Assert.Equal(Alpha, with.Source);
        Assert.Equal("", without.Source);
        Assert.Equal(without.Bytecode, with.Bytecode);
        // And the link carries it, unless it strips the bundle.
        Assert.Equal(Alpha, Entry(Linked(with), "alpha").Source);
        Assert.Equal("", Entry(Linked(with, strip: true), "alpha").Source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ItsBundleLoadsFromTheCompiledCode(bool session)
    {
        var bundle = Linked(Retexted(Compiled(Alpha, "alpha"), Alpha.Replace("a(1).", "a(2).")));
        Assert.Contains("a(2).", Entry(bundle, "alpha").Source);
        Assert.False(Debuggable(bundle, "alpha"));
        var engine = Loaded(bundle, debug: session);
        Assert.Equal("1", Answer(engine, "alpha:b(X)."));
        // clause/2 reads the module's clauses, as of one loaded without text.
        Assert.Equal("1", Answer(engine, "clause(alpha:a(X), true)."));
    }

    [Theory]
    [InlineData(true, "1")]     // a debug session runs the debug code
    [InlineData(false, "2")]    // outside one, the text is consulted
    public void ADebuggableBundleRunsItsDebugCodeOnlyInADebugSession(bool session, string expected)
    {
        var obj = Compiled(Alpha, "alpha", mode: ShmoBuildMode.Debuggable);
        Assert.Equal(Alpha, obj.Source);
        var bundle = Linked(Retexted(obj, Alpha.Replace("a(1).", "a(2).")));
        Assert.True(Debuggable(bundle, "alpha"));
        Assert.Equal(expected, Answer(Loaded(bundle, debug: session), "alpha:b(X)."));
    }

    [Fact]
    public void ADebuggableBundleWithoutItsTextRunsItsDebugCode()
    {
        var bundle = Linked(Retexted(Compiled(Alpha, "alpha", mode: ShmoBuildMode.Debuggable), ""));
        Assert.Equal("", Entry(bundle, "alpha").Source);
        Assert.True(Debuggable(bundle, "alpha"));
        Assert.Equal("1", Answer(Loaded(bundle), "alpha:b(X)."));
    }

    [Fact]
    public void StrippedItKeepsItsCompiledCode()
    {
        var bundle = Linked(Retexted(Compiled(Alpha, "alpha"), Alpha.Replace("a(1).", "a(2).")),
                            strip: true);
        Assert.Equal("", Entry(bundle, "alpha").Source);
        Assert.Equal("1", Answer(Loaded(bundle), "alpha:b(X)."));
    }

    [Fact]
    public void AStrippedDebugBuildIsReleaseCode()
    {
        // No source information stays in a bundle without text (Chunk179Tests).
        var bundle = Linked(Compiled(Alpha, "alpha", mode: ShmoBuildMode.Debuggable), strip: true);
        Assert.Equal("", Entry(bundle, "alpha").Source);
        Assert.False(Debuggable(bundle, "alpha"));
        Assert.Equal("1", Answer(Loaded(bundle), "alpha:b(X)."));
    }

    [Fact]
    public void ALocalEntryPointIsPromotedFromTheClauses()
    {
        string other = Prog.Replace("aux(1).", "aux(2).");
        var bundle = Linked(Retexted(Compiled(Prog, "prog"), other),
                            entry: new PredicateRef("main", 1));
        // The text is as written: the link does not edit it.
        Assert.Equal(other, Entry(bundle, "prog").Source);
        Assert.Equal(PredicateVisibility.Public, VisibilityOf(bundle, "prog", "main", 1));
        Assert.Equal("1", Answer(PrologEngine.FromBundle(bundle), "main(X)."));
        Assert.Equal("1", Answer(Loaded(bundle, debug: true), "main(X)."));
    }

    [Fact]
    public void ADebuggableLocalEntryPointHasItsDeclarationAppended()
    {
        var bundle = Linked(Compiled(Prog, "prog", mode: ShmoBuildMode.Debuggable),
                            entry: new PredicateRef("main", 1));
        // Outside a debug session the text is consulted, and main/1 has to be
        // public there too; appended, the line moves no other.
        Assert.Equal(Prog + ":- public main/1.\n", Entry(bundle, "prog").Source);
        Assert.Equal(PredicateVisibility.Public, VisibilityOf(bundle, "prog", "main", 1));
        Assert.True(Debuggable(bundle, "prog"));
        Assert.Equal("1", Answer(PrologEngine.FromBundle(bundle), "main(X)."));
        Assert.Equal("1", Answer(Loaded(bundle, debug: true), "main(X)."));
    }

    [Theory]
    [InlineData(ShmoBuildMode.Debuggable, ":- dynamic q/0.\n")]
    [InlineData(ShmoBuildMode.Release, "")]     // --with-source: the text as written
    public void AnImplicitDynamicIsDeclaredAtTheEndOfADebuggableText(ShmoBuildMode mode, string appended)
    {
        // Arity semantics: a goal only meta-called and defined nowhere is an
        // empty dynamic, which the link declares in a text a load consults.
        const string text = ":- public p/0.\np :- call(q).\n";
        var compiled = ShmoCompiler.TryCompileSource(text, "ar", mode,
            arityCompat: true, withSource: true);
        Assert.NotNull(compiled.Object);
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { compiled.Object! },
            EntryPoints = new[] { new PredicateRef("p", 0) },
            BakePrelude = true,
            IncludeCompiledIl = false,
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        var bundle = BundleReader.FromBytes(link.Bytes!);
        Assert.Equal(PredicateVisibility.Dynamic, VisibilityOf(bundle, "ar", "q", 0));
        Assert.Equal(text + appended, Entry(bundle, "ar").Source);
        // p fails, as q/0 has no clauses, from the consulted text or the code.
        var engine = new PrologEngine();
        engine.LoadBundle(bundle);
        Assert.False(engine.Query("p.").Success);
    }

#if !NETFRAMEWORK  // ExecutableEmitter is net10 toolchain, excluded from the net48 build.
    [Fact]
    public void ADebugExecutableNeedsDebuggableCode()
    {
        // Release code with its text has nothing to stop in: the precondition
        // refuses it, before anything is published.
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { Compiled(Prog, "prog") },
            EntryPoints = new[] { new PredicateRef("main", 1) },
            BakePrelude = true,
            IncludeCompiledIl = false,
        });
        Assert.True(link.Success);
        Assert.Equal(Prog, Entry(BundleReader.FromBytes(link.Bytes!), "prog").Source);
        string exePath = Path.Combine(_dir, "debug.exe");
        var res = ExecutableEmitter.Emit(link.Bytes!, goal: "main(_)", outputPath: exePath, debug: true);
        Assert.False(res.Success);
        Assert.Contains(res.Diagnostics, d => d.Code == "debug_no_source");
        Assert.False(File.Exists(exePath));
    }
#endif

    [Fact]
    public void AConsultCompileKeepsEachModulesFile()
    {
        string dep = ":- module(dep, [d/1]).\n% dep's own comment\nd(7).\n";
        string app = ":- module(app, [r/1]).\n:- use_module(dep).\nr(X) :- d(X).\n";
        File.WriteAllText(Path.Combine(_dir, "dep.pl"), dep);
        string appPath = Path.Combine(_dir, "app.pl");
        File.WriteAllText(appPath, app);

        var errors = new List<ShmoCompileError>();
        var objects = ShmoViaConsult.CompileMany(new[] { appPath }, Array.Empty<string>(),
            ShmoBuildMode.Release, errors, withSource: true);
        Assert.Empty(errors);
        var byName = objects.ToDictionary(o => o.ModuleName, o => o.Object);
        Assert.Equal(app, byName["app"].Source.Replace("\r\n", "\n"));
        Assert.Equal(dep, byName["dep"].Source.Replace("\r\n", "\n"));
        Assert.All(byName.Values, o => Assert.Equal(ShmoBuildMode.Release, o.BuildMode));

        var plain = ShmoViaConsult.CompileMany(new[] { appPath }, Array.Empty<string>(),
            ShmoBuildMode.Release, new List<ShmoCompileError>());
        Assert.All(plain, o => Assert.Equal("", o.Object.Source));
    }

#if !NETFRAMEWORK  // runs the net10 shumway-compile
    private static string CompileExe()
    {
        string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Shumway.slnx")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        foreach (string config in new[] { "Debug", "Release" })
        {
            string path = Path.Combine(root, "src", "Shumway.Compile", "bin", config,
                "net10.0", "shumway-compile" + suffix);
            if (File.Exists(path)) return path;
        }
        throw new InvalidOperationException("shumway-compile is not built");
    }

    private string Compile(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(CompileExe())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _dir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        string stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(120_000), "shumway-compile did not finish");
        Assert.True(proc.ExitCode == 0, stderr + stdout.Result);
        return stderr;
    }

    [Fact]
    public void TheToolKeepsTheTextAndRebuildsWhatWasBuiltWithout()
    {
        string dep = ":- module(dep, [d/1]).\nd(7).\n";
        File.WriteAllText(Path.Combine(_dir, "dep.pl"), dep);
        File.WriteAllText(Path.Combine(_dir, "app.pl"), ":- module(app, [r/1]).\n:- use_module(dep).\nr(X) :- d(X).\n");
        string objs = Path.Combine(_dir, "objs");
        string depShmo = Path.Combine(objs, "dep.shmo");
        string Text() => ShmoReader.ReadFromFile(depShmo).Source.Replace("\r\n", "\n");

        Assert.Contains("[release, with source]", Compile("--consult", "--with-source", "-o", objs, "app.pl"));
        Assert.Equal(dep, Text());
        // The dependency is up to date only for a build of the same kind.
        Assert.Contains("dep.shmo up to date", Compile("--consult", "--with-source", "-o", objs, "app.pl"));
        Assert.DoesNotContain("dep.shmo up to date", Compile("--consult", "-o", objs, "app.pl"));
        Assert.Equal("", Text());
        Assert.Contains("dep.shmo up to date", Compile("--consult", "-o", objs, "app.pl"));
        Assert.DoesNotContain("dep.shmo up to date", Compile("--consult", "--with-source", "-o", objs, "app.pl"));
        Assert.Equal(dep, Text());

        // A file compiled alone keeps it too.
        Compile("--with-source", "-o", Path.Combine(_dir, "alone.shmo"), "dep.pl");
        var alone = ShmoReader.ReadFromFile(Path.Combine(_dir, "alone.shmo"));
        Assert.Equal(ShmoBuildMode.Release, alone.BuildMode);
        Assert.Equal(dep, alone.Source.Replace("\r\n", "\n"));
    }
#endif
}
