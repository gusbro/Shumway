using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Baking the prelude as Tier-1 IL (--stdlib --with-compiled-il):
/// the $prelude bundle entry carries compiled IL, and a bundle whose prelude is
/// already IL runs prelude predicates correctly (same answers as a normal WAM
/// engine) — the fast-startup deployment (no parse, no compile at load).
///
/// <para>Two validation tiers. Direct-dispatch predicates are checked
/// IN-PROCESS. Meta-call-heavy predicates (aggregate_all, foldl — they run their
/// goal via findall, resolved through the runtime functor table) are checked
/// CROSS-PROCESS by spawning the REPL on the bundle, because building AND loading
/// a persisted-IL bundle in the SAME process leaves the meta-call functor
/// resolution in a build-time state that a deployed app (link, then load in a
/// fresh process) never sees.</para></summary>
public sealed class PreludeIlBakeTests
{
    private const string App = ":- public dummy/0.\ndummy.\n";

    private static byte[] LinkIlBaked(bool stripWam) =>
        ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(App, "app") },
            EntryPoints = new[] { new PredicateRef("dummy", 0) },
            BakePrelude = true,
            IncludeCompiledIl = true,
            StripWam = stripWam,
        }).Bytes!;

    [Fact]
    public void BakedPreludeEntry_CarriesIl()
    {
        var bundle = BundleReader.FromBytes(LinkIlBaked(stripWam: false));
        var prelude = bundle.Entries.Single(e => e.ModuleName == "$prelude");
        Assert.NotNull(prelude.CompiledIl);
    }

    private static readonly Lazy<PrologEngine> IlEngine =
        new(() => PrologEngine.FromBundle(BundleReader.FromBytes(LinkIlBaked(stripWam: false))));

    // Direct-dispatch prelude predicates: a top-level query resolves them via the
    // link, so in-process FromBundle is faithful.
    [Theory]
    [InlineData("findall(X, member(X, [a,b,c]), L)")]
    [InlineData("msort([3,1,2,1], L)")]
    [InlineData("sub_atom(banana, B, 2, _, an)")]           // cursor builtin
    [InlineData("(G = throw(boom), catch(G, E, true))")]    // prelude catch/3 (variable goal)
    [InlineData("(forall(member(X,[2,4,6]), 0 is X mod 2) -> R = yes ; R = no)")]
    [InlineData("(numlist(1,5,L), sum_list(L, S))")]
    [InlineData("call((member(Z,[x,y]), Z==y))")]
    public void IlBakedPrelude_InProcess(string query)
    {
        var expected = new PrologEngine().QueryAll(query + ".").Select(Canon).ToList();
        var got = IlEngine.Value.QueryAll(query + ".").Select(Canon).ToList();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, got);
    }

    // Comprehensive scenario including the meta-call-heavy predicates, run
    // cross-process on a real (and a --strip-wam) IL-baked bundle.
    private const string Scenario =
        ":- public scenario/0.\n" +
        "even(X) :- 0 is X mod 2.\n" +
        "add(E, A, B) :- B is A + E.\n" +
        "scenario :-\n" +
        "    findall(X, member(X, [a,b,c]), [a,b,c]),\n" +
        "    msort([3,1,2,1], [1,1,2,3]),\n" +
        "    sub_atom(banana, 1, 2, _, an),\n" +
        "    forall(member(X, [2,4,6]), even(X)),\n" +
        "    aggregate_all(sum(Y), member(Y, [1,2,3,4]), 10),\n" +
        "    aggregate_all(count, member(_, [a,b,c]), 3),\n" +
        "    foldl(add, [1,2,3,4], 0, 10),\n" +
        "    (G = throw(boom), catch(G, boom, true)),\n" +
        "    call((member(Z, [x,y]), Z == y)),\n" +
        "    (numlist(1,5,NL), sum_list(NL, 15)).\n";

    // The premise BundleRaceDiag stands on: two serial builds of the same
    // input agree on every section except the persisted-IL blob, which
    // embeds a fresh MVID / PE timestamp per emit — in place, at constant
    // length. Everything a load consumes structurally is deterministic, so a
    // rebuild-diff in a failure report indicts the concurrent build.
    [Fact]
    public void BundleBuild_BackToBack_IsStructurallyDeterministic()
    {
        byte[] Build() => ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(Scenario, "app") },
            EntryPoints = new[] { new PredicateRef("scenario", 0) },
            BakePrelude = true,
            IncludeCompiledIl = true,
            StripWam = false,
        }).Bytes!;
        string table = BundleRaceDiag.Structural(
            BundleReader.FromBytes(Build()), BundleReader.FromBytes(Build()));
        Assert.DoesNotContain("DIFFER", table);
        Assert.DoesNotContain("NULL-MISMATCH", table);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // --strip-wam
    public void IlBakedPrelude_CrossProcess(bool stripWam)
    {
        var r = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(Scenario, "app") },
            EntryPoints = new[] { new PredicateRef("scenario", 0) },
            BakePrelude = true,
            IncludeCompiledIl = true,
            StripWam = stripWam,
        });
        Assert.True(r.Success, string.Join(", ", r.Diagnostics.Select(d => d.Message)));

        string replDll = Path.Combine(
            Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(typeof(PreludeIlBakeTests).Assembly.Location)!,
                "..", "..", "..", "..", "..")),
            "src", "Shumway.Repl", "bin", "Release", "net10.0", "shumway.dll");
        if (!File.Exists(replDll)) return;   // dev convenience: needs a Release REPL build

        string tmp = Path.Combine(Path.GetTempPath(), "shumway-ilprelude-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        try
        {
            string bundlePath = Path.Combine(tmp, "scenario.shum");
            File.WriteAllBytes(bundlePath, r.Bytes!);

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
            proc.StandardInput.WriteLine("scenario.");
            proc.StandardInput.WriteLine("halt.");
            proc.StandardInput.Close();
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);

            Assert.True(proc.ExitCode == 0, $"exit {proc.ExitCode}\n{stdout}\n{stderr}");
            // Full transcript on failure: Assert.Contains truncates its
            // display, which once hid the child's actual error.
            Assert.True(stdout.Contains("true."),
                $"child transcript lacks \"true.\"\nstdout:\n{stdout}\nstderr:\n{stderr}\n"
                + BundleRaceDiag.CompareWithRebuild(r.Bytes!, () => ShmoLinker.Link(new LinkConfig
                {
                    Objects = new[] { ShmoCompiler.CompileSource(Scenario, "app") },
                    EntryPoints = new[] { new PredicateRef("scenario", 0) },
                    BakePrelude = true,
                    IncludeCompiledIl = true,
                    StripWam = stripWam,
                }).Bytes!));   // scenario succeeded
            Assert.DoesNotContain("false.", stdout);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    private static string Canon(Solution s) =>
        s.Bindings.Count == 0 ? "<true>"
        : string.Join(", ", s.Bindings.OrderBy(b => b.Key).Select(b => b.Key + "=" + b.Value));
}
