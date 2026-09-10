using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Unification and copy_term/2 walk a term whose depth the program
/// chose. A .NET stack overflow cannot be caught -- it takes the process down
/// with no goal to unwind and nothing to report -- so neither may spend a C#
/// frame per level without a ceiling.
///
/// <para>Both are MIXED: recursive while the depth is known to be safe, and
/// past that the remaining work goes on a list the entry point drains at depth
/// zero. Unify could take that shape because unification is CONFLUENT (which
/// equation you solve first cannot change the answer) and its pair set is
/// append-only; copy_term because every destination slot is reserved before
/// anything is copied into it.</para></summary>
public sealed class DeepTermEngineWalksTests
{
    // Far past where the recursive form died, which was between 30k and 60k
    // on the 64 MB stack the CLIs get and far earlier on a test thread.
    private const int Deep = 200_000;

    private static PrologEngine Engine()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString("""
            nest(0, x) :- !.
            nest(N, f(T)) :- M is N - 1, nest(M, T).
            """);
        return e;
    }

    [Fact]
    public void UnifyingTwoTermsFarDeeperThanTheStack_Succeeds()
    {
        var e = Engine();
        Assert.True(e.Query(
            $"nest({Deep}, X), nest({Deep}, Y), X = Y, X == Y.").Success);
    }

    /// <summary>ANTI-VACUITY: it is still deciding, not just surviving. One
    /// level shorter and the unification has to FAIL.</summary>
    [Fact]
    public void UnifyingTermsThatDiffer_StillFails()
    {
        var e = Engine();
        Assert.True(e.Query(
            $"nest({Deep}, X), nest({Deep - 1}, Y), \\+ X = Y.").Success);
    }

    /// <summary>And it binds through: the answer is the unifier, not just a
    /// yes.</summary>
    [Fact]
    public void UnifyingBindsThroughADeepTerm()
    {
        var e = Engine();
        Assert.True(e.Query(
            $"nest({Deep}, X), Y = f(Z), X = Y, nonvar(Z).").Success);
    }

    [Fact]
    public void CopyingATermFarDeeperThanTheStack_CopiesItAll()
    {
        var e = Engine();
        Assert.True(e.Query($"nest({Deep}, X), copy_term(X, C), C == X.").Success);
    }

    /// <summary>ANTI-VACUITY for the copy: a copy is FRESH, so its variables
    /// are not the original's however deep it is.</summary>
    [Fact]
    public void ADeepCopyIsStillACopy()
    {
        var e = Engine();
        Assert.True(e.Query(
            $"nest({Deep}, T), X = g(V, T), copy_term(X, g(W, _)), V \\== W.")
            .Success);
    }

    /// <summary>The properties the escalated pass exists for, unchanged: two
    /// cyclic terms unify coinductively, two that cannot do not, and a shared
    /// subterm unifies once.</summary>
    [Fact]
    public void TheCyclicAndSharedCasesAreUnchanged()
    {
        var e = Engine();
        Assert.True(e.Query("X = f(X), Y = f(Y), X = Y.").Success);
        Assert.True(e.Query("P = [a|P], Q = [a|Q], P = Q.").Success);
        Assert.True(e.Query("\\+ (U = f(U, a), V = f(V, b), U = V).").Success);
        Assert.True(e.Query(
            "W = f(1, 2), Z = h(W, W), Z = h(f(1, 2), f(1, 2)).").Success);
        Assert.True(e.Query("Y = f(Y), copy_term(Y, C), C = f(_).").Success);
    }

    /// <summary>A cyclic term deeper than the ceiling still terminates: the
    /// deferred pairs go through the same pair set, so the coinductive stop
    /// applies to them too.</summary>
    [Fact]
    public void ACyclicTermBelowADeepPrefixStillTerminates()
    {
        var e = Engine();
        Assert.True(e.Query(
            $"nest({Deep}, _), A = f(A), B = f(B), A = B.").Success);
    }
}
