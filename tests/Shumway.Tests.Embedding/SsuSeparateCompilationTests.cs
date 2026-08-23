using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-037 single-sided unification (<c>Head =&gt; Body</c>) through the
/// SEPARATE-COMPILATION path. The consult path lowers <c>SsuRule</c> clauses in
/// <c>ClausePipeline</c>, but <c>ShmoCompiler.CompileFromParts</c> (used by both
/// <c>shumway-compile</c> and <c>ShmoViaConsult</c>) has its own hand-rolled
/// clause sub-pipeline that must ALSO run <c>SsuTransform</c> — otherwise a raw
/// <c>SsuRule</c> reaches <c>ClauseCompiler</c> and throws
/// <c>Unknown clause kind: SsuRule</c>. Regression for that gap.</summary>
public sealed class SsuSeparateCompilationTests
{
    private static PrologEngine LinkAndLoad(string module, string source, PredicateRef entry)
    {
        var r = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(source, module) },
            EntryPoints = new[] { entry },
        });
        Assert.True(r.Success, string.Join(", ", r.Diagnostics.Select(d => d.Message)));
        var e = new PrologEngine();
        e.LoadBundle(BundleReader.FromBytes(r.Bytes!));
        return e;
    }

    [Fact]
    public void SsuRule_CompilesAndRuns_ThroughSeparateCompilation()
    {
        // p/1 defined with `=>`; the head commits before the body, and a
        // non-matching head fails (does NOT fall through to a later clause).
        var e = LinkAndLoad("m",
            ":- public classify/2.\n"
            + "classify(0, R) => R = zero.\n"
            + "classify(N, R) => N > 0, R = pos.\n",
            new PredicateRef("classify", 2));

        Assert.True(e.Query("classify(0, R), R == zero.").Success);
        Assert.True(e.Query("classify(5, R), R == pos.").Success);
        // Single-sided: a bound-but-non-unifying first arg does not backtrack
        // into a different clause's head via ordinary unification.
        Assert.Single(e.QueryAll("classify(0, R)."));
    }

    [Fact]
    public void SsuRule_WithGuard_CommitsAfterGuard()
    {
        // `(Head, Guard) => Body` — SWI style: the OUTPUT binding lives in
        // the body (a pattern in an output position would not match the
        // caller's unbound variable under single-sided unification).
        var e = LinkAndLoad("g",
            ":- public sign/2.\n"
            + "sign(N, S), N < 0 => S = s(neg).\n"
            + "sign(N, S), N =:= 0 => S = s(zero).\n"
            + "sign(N, S), N > 0 => S = s(pos).\n",
            new PredicateRef("sign", 2));

        Assert.True(e.Query("sign(-3, S), S == s(neg).").Success);
        Assert.True(e.Query("sign(0, S), S == s(zero).").Success);
        Assert.True(e.Query("sign(7, S), S == s(pos).").Success);
    }
}
