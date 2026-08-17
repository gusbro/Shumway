using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-037 — an inline <c>( Cond *-> Then ; Else )</c> (and inline <c>-></c>) in a
/// MULTI-CLAUSE, first-arg-indexed predicate. At runtime the full indexed-dispatch
/// describer (tried first) handles it, so these promote. In a persisted bundle the
/// full indexed is disabled (allowIndexedDispatch=false), so the fallback
/// describers (IndexedAtom / SwitchedChain) decide — they used to reject any inline
/// ITE via a stale ContainsInlineIteOpcode guard even though their clause
/// boundaries come from operands (switch table / try-chain addresses), not a linear
/// me-else scan. With the guard removed, an indexed <c>*-></c> bakes as IL in the
/// bundle too.
/// </summary>
public class Adr037IndexedIlTests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    private static PrologEngine LoadWithPersistedIl(string src)
    {
        var bundle = new Bundle(new[] { new BundleEntry("ix", src) });
        byte[] bytes = BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: true, includeCompiledIl: true);
        var e = new PrologEngine();   // Threshold 0 — runs the persisted IL, no promotion
        e.LoadBundle(BundleReader.FromBytes(bytes));
        return e;
    }

    // ---- runtime (full indexed dispatch handles inline ITE) ----

    [Fact]
    public void IndexedSoftCut_Runtime_Promotes_AndIsCorrect()
    {
        var e = new PrologEngine { EnableInlineIte = true };
        e.IlPromotion.Threshold = 1;
        e.ConsultString("""
            :- public cls/2.
            cls(a, R) :- ( member(X, [1,2,3]) *-> R = t(X) ; R = none ).
            cls(b, R) :- R = bee.
            """);
        Assert.True(e.Query("cls(a, R), R == t(1).").Success);
        Assert.True(e.Query("cls(a, R), R == t(1).").Success);
        Assert.True(e.IlPromotion.IsPromoted(Fid("cls", 2)));
        Assert.True(e.Query("findall(R, cls(a, R), L), L == [t(1),t(2),t(3)].").Success);
        Assert.True(e.Query("cls(b, R), R == bee.").Success);
    }

    [Fact]
    public void IndexedInlineArrow_Runtime_Promotes_AndIsCorrect()
    {
        var e = new PrologEngine { EnableInlineIte = true };
        e.IlPromotion.Threshold = 1;
        e.ConsultString("""
            :- public sign/2.
            sign(pos, R) :- ( 1 > 0 -> R = yes ; R = no ).
            sign(neg, R) :- R = other.
            """);
        Assert.True(e.Query("sign(pos, R), R == yes.").Success);
        Assert.True(e.Query("sign(pos, R), R == yes.").Success);
        Assert.True(e.IlPromotion.IsPromoted(Fid("sign", 2)));
        Assert.True(e.Query("sign(neg, R), R == other.").Success);
    }

    // ---- persisted bundle (fallback describers; the guard-removal case) ----

    [Fact]
    public void IndexedSoftCut_PersistedBundle_BakesToIl_AndIsCorrect()
    {
        var e = LoadWithPersistedIl(
            ":- public pk/2.\n" +
            "pk(a, R) :- ( member(X, [1,2,3]) *-> R = t(X) ; R = none ).\n" +
            "pk(b, R) :- R = bee.\n");
        Assert.True(e.IlPromotion.IsPromoted(Fid("pk", 2)),
            "indexed *-> must bake to IL in a persisted bundle (fallback describer)");
        Assert.True(e.Query("pk(a, R), R == t(1).").Success);
        Assert.True(e.Query("findall(R, pk(a, R), L), L == [t(1),t(2),t(3)].").Success);
        Assert.True(e.Query("pk(b, R), R == bee.").Success);
    }

    [Fact]
    public void IndexedAtomSoftCut_PersistedBundle_BakesToIl()
    {
        // Arity-1 atom index → routes through the IndexedAtom fallback describer.
        var e = LoadWithPersistedIl(
            ":- public q/1.\n" +
            "q(a) :- ( 1 =< 1 *-> true ; fail ).\n" +
            "q(b).\n");
        Assert.True(e.IlPromotion.IsPromoted(Fid("q", 1)),
            "arity-1 indexed *-> must bake to IL in a persisted bundle");
        Assert.True(e.Query("q(a).").Success);
        Assert.True(e.Query("q(b).").Success);
        // Deterministic condition leaves no choice point.
        Assert.True(e.Query("findall(x, q(a), L), L == [x].").Success);
    }
}
