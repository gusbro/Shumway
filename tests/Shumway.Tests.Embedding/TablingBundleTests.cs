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
    private const string Program =
        ":- public ans/1.\n" +
        ":- table reach/2.\n" +
        "edge(a, b).\n" +
        "edge(b, c).\n" +
        "edge(c, d).\n" +
        "reach(X, Y) :- edge(X, Y).\n" +
        "reach(X, Y) :- edge(X, Z), reach(Z, Y).\n" +
        "ans(L) :- findall(Y, reach(a, Y), L0), msort(L0, L).\n";

    private static byte[] Link(ShmoBuildMode mode, bool il, bool stripWam) =>
        ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(Program, "tab", mode) },
            EntryPoints = new[] { new PredicateRef("ans", 1) },
            BakePrelude = true,
            IncludeCompiledIl = il,
            StripWam = stripWam,
        }).Bytes!;

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
        Assert.Equal(".(b, .(c, .(d, [])))", sols[0].Bindings["L"].ToString());
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
            Assert.Contains("[b, c, d]", stdout);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }
}
