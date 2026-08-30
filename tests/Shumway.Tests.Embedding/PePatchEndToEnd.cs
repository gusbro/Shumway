using System.Diagnostics;
using System.Reflection;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// End-to-end Phase 17 validation: assertz/retract/disjunction-driven
/// scenarios that exercise every patch site shape (atom ids, functor
/// ids, resume markers). Each scenario is built in this process,
/// then loaded both in-process (where build-time and runtime ids
/// coincide) and cross-process via a spawned REPL (where they don't —
/// the PE-patch path has to remap every constant).
///
/// <para>The covered shapes:</para>
/// <list type="bullet">
/// <item>Single dynamic pred + retractall (atoms + resume markers
///   inside retractall's <c>$disj_N</c> helpers).</item>
/// <item>Two dynamic preds, one with a nested compound argument
///   (exercises functor-id patches for <c>pair/2</c> and
///   <c>fact/1</c> in a structure operand).</item>
/// <item>Two dynamic preds, retractall on the simpler one — fully
///   round-trips through Phase 17's patching.</item>
/// </list>
///
/// <para>A bug in the IL emit for 2+ assertz of <c>fact/1</c>
/// combined with another <c>:- dynamic</c> declaration triggers
/// <c>instantiation_error</c> inside <c>retract</c>; that's unrelated
/// to Phase 17 (the patches resolve and apply correctly; the IL the
/// patcher hands to the runtime is just wrong for that shape) and is
/// tracked separately.</para>
/// </summary>
public class PePatchEndToEnd
{
    /// <summary>Single dynamic pred + retractall — the canonical
    /// $disj_N / retract / atom-id flow.</summary>
    [Fact]
    public void RetractallSingleDynamic_InProcess()
        => RunInProcess(SimpleRetractallSource);

    [Fact]
    public void RetractallSingleDynamic_CrossProcess()
        => RunCrossProcess(SimpleRetractallSource);

    /// <summary>Two dynamic preds, no retractall, one with a nested
    /// compound argument. Exercises <c>PutStructure</c> patching for
    /// <c>pair/2</c> and <c>fact/1</c>.</summary>
    [Fact]
    public void TwoDynamicsNestedCompound_InProcess()
        => RunInProcess(NestedCompoundSource);

    [Fact]
    public void TwoDynamicsNestedCompound_CrossProcess()
        => RunCrossProcess(NestedCompoundSource);

    /// <summary>One dynamic pred + one nested compound assertz +
    /// retractall — the most thorough Phase-17 exercise that's still
    /// within the IL emit's currently-supported shapes.</summary>
    [Fact]
    public void NestedCompoundPlusRetractall_InProcess()
        => RunInProcess(NestedRetractallSource);

    [Fact]
    public void NestedCompoundPlusRetractall_CrossProcess()
        => RunCrossProcess(NestedRetractallSource);

    private const string SimpleRetractallSource =
        ":- public scenario/0.\n"
        + ":- dynamic fact/1.\n"
        + "scenario :-\n"
        + "    assertz(fact(a)),\n"
        + "    assertz(fact(b)),\n"
        + "    retractall(fact(_)),\n"
        + "    ( fact(_) -> X = leaked ; X = ok ),\n"
        + "    X = ok.\n";

    private const string NestedCompoundSource =
        ":- public scenario/0.\n"
        + ":- dynamic pair/2.\n"
        + "scenario :-\n"
        + "    assertz(pair(one, fact(a))),\n"
        + "    assertz(pair(two, fact(b))),\n"
        + "    pair(one, fact(a)),\n"
        + "    pair(two, fact(b)).\n";

    private const string NestedRetractallSource =
        ":- public scenario/0.\n"
        + ":- dynamic fact/1.\n"
        + ":- dynamic pair/2.\n"
        + "scenario :-\n"
        + "    assertz(fact(a)),\n"
        + "    assertz(pair(one, fact(a))),\n"
        + "    pair(one, fact(a)),\n"
        + "    retractall(fact(_)),\n"
        + "    ( fact(_) -> X = leaked ; X = ok ),\n"
        + "    X = ok.\n";

    /// <summary>Regression for the chunk-359 fact inliner's persisted-bundle atom-key
    /// bug (found via Blint's `any_lint/4` → `lint_msg_text/3`): an atom-keyed
    /// multi-clause fact inlined into a caller compared the dispatch key with a RAW
    /// build-time atom id (<c>emit.LoadConstant(keys[c])</c>) instead of the patchable
    /// <see cref="EmitAtomId"/>, so a FRESH process compared against the wrong id, the
    /// inlined lookup found no clause, and the caller silently failed. Here
    /// <c>msg/2</c> is the atom-keyed fact, inlined into <c>lookup/2</c>; cross-process
    /// the bound key <c>lnt104</c> must still resolve to <c>high</c>.</summary>
    [Fact]
    public void AtomKeyedFactInline_InProcess()
        => RunInProcess(AtomKeyedFactInlineSource);

    [Fact]
    public void AtomKeyedFactInline_CrossProcess()
        => RunCrossProcess(AtomKeyedFactInlineSource);

    private const string AtomKeyedFactInlineSource =
        ":- public scenario/0.\n"
        + "msg(lnt100, low).\n"
        + "msg(lnt104, high).\n"
        + "msg(lnt200, crit).\n"
        // msg is a NON-tail call (the trailing cut) so lookup compiles through the
        // meta-CP body that runs the fact inliner — a tail call would leaf-compile and
        // skip inlining, missing the bug.
        + "lookup(Id, Sev) :- msg(Id, Sev), !.\n"
        + "scenario :- lookup(lnt104, S), S == high.\n";

    private static void RunInProcess(string src)
    {
        byte[] bytes = BuildBundleBytes(src);
        var rt = BundleReader.FromBytes(bytes);
        var engine = new PrologEngine();
        engine.LoadBundle(rt);
        Assert.True(engine.Query("scenario.").Success);
    }

    private static void RunCrossProcess(string src)
    {
        byte[] bytes = BuildBundleBytes(src);
        string tmpDir = Path.Combine(Path.GetTempPath(), "shumway-phase17-" + Guid.NewGuid());
        Directory.CreateDirectory(tmpDir);
        try
        {
            string bundlePath = Path.Combine(tmpDir, "scenario.shum");
            File.WriteAllBytes(bundlePath, bytes);

            string testBinDir = Path.GetDirectoryName(typeof(PePatchEndToEnd).Assembly.Location)!;
            string repoRoot = Path.GetFullPath(Path.Combine(
                testBinDir, "..", "..", "..", "..", ".."));
            string replDll = Path.Combine(repoRoot,
                "src", "Shumway.Repl", "bin", "Release", "net10.0", "shumway.dll");
            if (!File.Exists(replDll)) return; // dev convenience — see class doc

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

            Assert.True(proc.ExitCode == 0,
                $"REPL exited with {proc.ExitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
            // The query succeeds → REPL prints "true." on a goal that
            // returned a single solution. A failure prints "false."
            // and an exception prints "% PrologRuntimeException: ...".
            // The "true." check is enough to distinguish all three — and on
            // failure the WHOLE transcript matters (Assert.Contains truncates
            // its display, which once hid the child's actual error).
            Assert.True(stdout.Contains("true."),
                $"child transcript lacks \"true.\"\nstdout:\n{stdout}\nstderr:\n{stderr}\n"
                + BundleRaceDiag.CompareWithRebuild(bytes, () => BuildBundleBytes(src)));
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    private static byte[] BuildBundleBytes(string src)
    {
        var bundle = new Bundle(new[] { new BundleEntry("scenario", src) });
        return BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: true, includeCompiledIl: true);
    }
}
