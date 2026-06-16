using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Tabling through the compile → link → load pipeline. The
/// <c>:- table</c> semi-naive transform now runs at COMPILE time
/// (ShmoCompiler), so the transformed predicates are baked into the .shmo
/// bytecode — which makes tabling work in a SOURCE-STRIPPED (release) bundle
/// and under <c>--with-compiled-il</c>, where it previously looped or failed
/// (the transform used to run only at load time off the entry's source).
///
/// <para>A left-recursive transitive-closure (<c>reach/2</c>) is the canonical
/// case: it terminates only because tabling memoises and drives a fixpoint —
/// under plain SLD the second clause loops.</para></summary>
public sealed class TablingBundleTests
{
    // CYCLIC graph (a -> b -> c -> a, plus c -> d): reach(a, Y) must detect the
    // tabled subgoal recurring in-progress or it loops. reach(a, _) = {a,b,c,d}.
    private const string Program =
        ":- public ans/1.\n" +
        ":- table reach/2.\n" +
        "edge(a, b).\n" +
        "edge(b, c).\n" +
        "edge(c, a).\n" +
        "edge(c, d).\n" +
        "reach(X, Y) :- edge(X, Y).\n" +
        "reach(X, Y) :- edge(X, Z), reach(Z, Y).\n" +
        "ans(L) :- findall(Y, reach(a, Y), L0), sort(L0, L).\n";

    // Tabled NEGATION (well-founded semantics). `\+ win(Y)` over the tabled
    // win/1 is rewritten to '$tbl_negate'; the transform adds a '$wfs_mode'
    // marker so a DIRECT top-level tabled call (game/1 -> win/1, routed through
    // '$tbl_dispatch') runs the alternating fixpoint. c wins (moves to dead-end
    // d), d loses, a<->b is a draw (undefined -> a direct call fails, since
    // undefined is not true). game/1 calls win/1 directly — exercising the
    // '$wfs_mode' path (well_founded/2 would bypass it by running the fixpoint
    // itself). status/1 also checks the three-valued report directly.
    private const string WfsProgram =
        ":- public game/1.\n" +
        ":- public status/2.\n" +
        ":- table win/1.\n" +
        "move(a, b).  move(b, a).  move(c, d).\n" +
        "win(X) :- move(X, Y), \\+ win(Y).\n" +
        "game(P) :- win(P).\n" +
        "status(P, S) :- well_founded(win(P), S).\n";

    private static byte[] LinkWfs(ShmoBuildMode mode, bool il) =>
        ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(WfsProgram, "wfs", mode) },
            EntryPoints = new[] { new PredicateRef("game", 1), new PredicateRef("status", 2) },
            BakePrelude = true,
            IncludeCompiledIl = il,
            StripWam = false,
        }).Bytes!;

    [Theory]
    [InlineData(ShmoBuildMode.Release)]
    [InlineData(ShmoBuildMode.Debug)]
    public void Tier0Bundle_WellFounded(ShmoBuildMode mode)
    {
        var engine = PrologEngine.FromBundle(BundleReader.FromBytes(LinkWfs(mode, il: false)));
        // Direct tabled call through the '$wfs_mode' fixpoint path.
        Assert.True(engine.Query("game(c).").Success);    // c wins
        Assert.False(engine.Query("game(d).").Success);   // d loses
        Assert.False(engine.Query("game(a).").Success);   // a is a draw (undefined, not true)
        // Three-valued report (via well_founded/2 directly).
        Assert.True(engine.Query("status(c, true).").Success);
        Assert.True(engine.Query("status(d, false).").Success);
        Assert.True(engine.Query("status(a, undefined).").Success);
    }

    // WFS under --with-compiled-il, cross-process: the '$wfs_mode' marker rides
    // the dynamic-seed path (same as Tier-0 release), so the fixpoint activates.
    [Fact]
    public void IlBundle_WellFounded_CrossProcess()
    {
        byte[] bytes = LinkWfs(ShmoBuildMode.Release, il: true);

        string replDll = Path.Combine(
            Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(typeof(TablingBundleTests).Assembly.Location)!,
                "..", "..", "..", "..", "..")),
            "src", "Shumway.Repl", "bin", "Release", "net10.0", "shumway.dll");
        if (!File.Exists(replDll)) return;   // dev convenience: needs a Release REPL build

        string tmp = Path.Combine(Path.GetTempPath(), "shumway-wfs-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        try
        {
            string bundlePath = Path.Combine(tmp, "wfs.shum");
            File.WriteAllBytes(bundlePath, bytes);

            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(replDll);
            psi.ArgumentList.Add(bundlePath);

            using var proc = Process.Start(psi)!;
            proc.StandardInput.WriteLine("status(c, Sc).");   // Sc = true
            proc.StandardInput.WriteLine("status(a, Sa).");   // Sa = undefined
            proc.StandardInput.WriteLine("halt.");
            proc.StandardInput.Close();
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);

            Assert.True(proc.ExitCode == 0, $"exit {proc.ExitCode}\n{stdout}\n{stderr}");
            Assert.Contains("Sc = true", stdout);
            Assert.Contains("Sa = undefined", stdout);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    // MUTUAL recursion through a p<->q cycle (two tabled predicates). p(a) is
    // the only fact; p(b) terminates only via in-progress detection across both
    // subgoals. who(L) = sorted truths reachable as p — just [a].
    private const string MutualProgram =
        ":- public who/1.\n" +
        ":- table p/1.\n" +
        ":- table q/1.\n" +
        "p(a).\n" +
        "p(X) :- q(X).\n" +
        "q(X) :- p(X).\n" +
        "who(L) :- findall(X, ( member(X, [a,b]), p(X) ), L0), sort(L0, L).\n";

    private static byte[] Link(ShmoBuildMode mode, bool il, bool stripWam) =>
        ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(Program, "tab", mode) },
            EntryPoints = new[] { new PredicateRef("ans", 1) },
            BakePrelude = true,
            IncludeCompiledIl = il,
            StripWam = stripWam,
        }).Bytes!;

    [Theory]
    [InlineData(ShmoBuildMode.Release)]
    [InlineData(ShmoBuildMode.Debug)]
    public void Tier0Bundle_MutualRecursion(ShmoBuildMode mode)
    {
        var bytes = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(MutualProgram, "mut", mode) },
            EntryPoints = new[] { new PredicateRef("who", 1) },
            BakePrelude = true,
            IncludeCompiledIl = false,
            StripWam = false,
        }).Bytes!;
        var engine = PrologEngine.FromBundle(BundleReader.FromBytes(bytes));
        var sols = engine.QueryAll("who(L).").ToList();
        Assert.Single(sols);
        Assert.Equal(".(a, [])", sols[0].Bindings["L"].ToString());
    }

    // Tier-0 (no IL) loads in-process via FromBundle. Release is the
    // previously-broken case (source stripped → the load-time transform had no
    // source); Debug is the regression guard (re-consults source as before).
    [Theory]
    [InlineData(ShmoBuildMode.Release)]
    [InlineData(ShmoBuildMode.Debug)]
    public void Tier0Bundle_Tables(ShmoBuildMode mode)
    {
        var engine = PrologEngine.FromBundle(BundleReader.FromBytes(Link(mode, il: false, stripWam: false)));
        var sols = engine.QueryAll("ans(L).").ToList();
        Assert.Single(sols);
        // [b, c, d] — left-recursive reach/2 terminates only via tabling.
        Assert.Equal(".(a, .(b, .(c, .(d, []))))", sols[0].Bindings["L"].ToString());
    }

    // IL bundles need the persisted-IL load path, which is only faithful in a
    // fresh process (build-time vs load-time functor tables); run the REPL on
    // the bundle. Both --with-compiled-il and --strip-wam.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // --strip-wam
    public void IlBundle_Tables_CrossProcess(bool stripWam)
    {
        byte[] bytes = Link(ShmoBuildMode.Release, il: true, stripWam: stripWam);

        string replDll = Path.Combine(
            Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(typeof(TablingBundleTests).Assembly.Location)!,
                "..", "..", "..", "..", "..")),
            "src", "Shumway.Repl", "bin", "Release", "net10.0", "shumway.dll");
        if (!File.Exists(replDll)) return;   // dev convenience: needs a Release REPL build

        string tmp = Path.Combine(Path.GetTempPath(), "shumway-tbl-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        try
        {
            string bundlePath = Path.Combine(tmp, "tab.shum");
            File.WriteAllBytes(bundlePath, bytes);

            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(replDll);
            psi.ArgumentList.Add(bundlePath);

            using var proc = Process.Start(psi)!;
            proc.StandardInput.WriteLine("ans(L).");
            proc.StandardInput.WriteLine("halt.");
            proc.StandardInput.Close();
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);

            Assert.True(proc.ExitCode == 0, $"exit {proc.ExitCode}\n{stdout}\n{stderr}");
            Assert.Contains("[a, b, c, d]", stdout);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

}
