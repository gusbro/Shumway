using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 16 follow-up: persisted-IL bundles (chunk 71) must dispatch
/// correctly under threaded Tier-1 (Phase 16). The Phase 16 chunks
/// 181-183 redesigned IL non-tail Call to set <c>Cp = resumeMarker</c>
/// and tail-call to the callee instead of synchronously recursing
/// through <c>RunSubroutine</c>. <see cref="PersistedIlBuilder"/>
/// emits the same body emitters the runtime <c>DynamicMethod</c>
/// path uses (<c>EmitSingleClauseMetaCpBody</c>,
/// <c>EmitTryMeElseChainBody</c>), so the threading semantics
/// should propagate to the persisted assembly automatically.
///
/// <para>These tests pin that invariant: a multi-clause predicate
/// with a non-tail Call to another predicate runs identically
/// regardless of whether its IL comes from the persisted-assembly
/// load path or from the runtime Sigil emitter. The chunk-71
/// existing tests only covered simple facts and head-match shapes
/// — these add the call-and-return shape that chunk-182 threading
/// is responsible for.</para>
/// </summary>
public class Chunk187Tests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    [Fact]
    public void PersistedIl_MultiClauseWithNonTailCall_RunsCorrectly()
    {
        // A multi-clause caller with a non-tail Call to a leaf callee.
        // Under the old chunk-50 design this was synchronous via
        // RunSubroutine; under Phase 16 threading it's tail-call +
        // resume marker. Persisted IL must produce the same answer.
        const string src =
            ":- public outer/1.\n"
            + "leaf(ok).\n"
            + "outer(X) :- leaf(X), check(X).\n"
            + "outer(error).\n"
            + "check(ok).\n";

        var bundle = new Bundle(new[] { new BundleEntry("threaded", src) });
        byte[] bytes = BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: true, includeCompiledIl: true);
        var rt = BundleReader.FromBytes(bytes);
        var engine = new PrologEngine();
        engine.LoadBundle(rt);

        // First clause succeeds with X=ok via the non-tail Call to
        // leaf/1 then check/1.
        var sol = engine.Query("outer(X).");
        Assert.True(sol.Success);
        Assert.Equal("ok", sol.Bindings["X"].ToString());
    }

    [Fact]
    public void PersistedIl_MultiClauseBacktracksAcrossThreadedCall()
    {
        // Backtracking through a non-tail Call: the second clause must
        // run when the first fails. Persisted IL's CP cascade must
        // honour the threaded boundary.
        const string src =
            ":- public choose/1.\n"
            + "choose(X) :- find(X), keep(X).\n"
            + "choose(fallback).\n"
            + "find(no).\n"
            + "keep(yes).\n";

        var bundle = new Bundle(new[] { new BundleEntry("bt", src) });
        byte[] bytes = BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: true, includeCompiledIl: true);
        var rt = BundleReader.FromBytes(bytes);
        var engine = new PrologEngine();
        engine.LoadBundle(rt);

        // find(no) succeeds, keep(no) fails → first clause fails →
        // backtrack to clause 2 → choose(fallback) succeeds.
        var sol = engine.Query("choose(X).");
        Assert.True(sol.Success);
        Assert.Equal("fallback", sol.Bindings["X"].ToString());
    }

    [Fact]
    public void PersistedIl_DeepCallChain_DoesNotOverflowCSharpStack()
    {
        // Architectural check: persisted IL also benefits from the
        // chunk-182 stack-flat dispatch. A deep chain that would have
        // overflowed under chunk-50's recursive RunSubroutine model
        // runs to completion via threading.
        const string src =
            ":- public chain/3.\n"
            + "chain(0, Acc, Acc) :- !.\n"
            + "chain(N, Acc, Out) :- N > 0, N1 is N - 1, "
            + "  chain(N1, Acc, Mid), Out = Mid.\n";

        var bundle = new Bundle(new[] { new BundleEntry("deep", src) });
        byte[] bytes = BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: true, includeCompiledIl: true);
        var rt = BundleReader.FromBytes(bytes);
        var engine = new PrologEngine();
        engine.LoadBundle(rt);
        var sol = engine.Query("chain(2000, ok, X).");
        Assert.True(sol.Success);
        Assert.Equal("ok", sol.Bindings["X"].ToString());
    }
}
