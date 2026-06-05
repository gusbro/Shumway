using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Phase 27 — <c>--strip-wam</c>: an IL bundle drops the (redundant)
/// WAM bodies of every IL-promoted predicate. The predicate stays registered
/// (its <c>Defined</c> metadata) and its IL delegate carries the body; callers
/// reach it by functor id (CallIl / the chunk-316 marker), never a WAM address.
/// JIT-only — the IL must load (non-AOT) for the stripped predicates to run.</summary>
public class Phase27StripWamTests
{
    // top/1 calls copy/2; both are IL-eligible. After stripping, both have no
    // WAM body and top→copy is an IL-to-IL call by functor id.
    private const string Src =
        ":- public top/1.\n" +
        "top(R) :- copy([a, b, c], R).\n" +
        "copy([], []).\n" +
        "copy([H|T], [H|R]) :- copy(T, R).\n";

    private static byte[] Build(bool stripWam) =>
        BundleWriter.ToBytes(new Bundle(new[] { new BundleEntry("m", Src) }),
            includeCompiledBytecode: true, includeCompiledIl: true, stripWam: stripWam);

    [Fact]
    public void StrippedBundle_IsSmaller()
    {
        int full = Build(stripWam: false).Length;
        int stripped = Build(stripWam: true).Length;
        Assert.True(stripped < full,
            $"stripped bundle ({stripped}) should be smaller than full ({full})");
    }

    [Fact]
    public void StrippedBundle_RunsViaIl()
    {
        var bundle = BundleReader.FromBytes(Build(stripWam: true));
        var engine = new PrologEngine();
        engine.LoadBundle(bundle);
        // top and copy have no WAM body; they run from their IL delegates, and
        // top→copy dispatches by functor id (chunk 316). Result must be correct.
        var sol = engine.Query("top(R), R = [a, b, c].");
        Assert.True(sol.Success);
    }

    [Fact]
    public void StrippedBundle_RequiresIl()
    {
        Assert.Throws<System.ArgumentException>(() =>
            BundleWriter.ToBytes(new Bundle(new[] { new BundleEntry("m", Src) }),
                includeCompiledBytecode: true, includeCompiledIl: false, stripWam: true));
    }
}
