using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Unification and copy_term/2 walk a term whose depth the program
/// chose. A .NET stack overflow cannot be caught -- it takes the process down
/// with no goal to unwind and nothing to report -- so neither may spend a C#
/// frame per level without a ceiling.
///
/// <para>Both are mixed: recursive while the depth is known to be safe, and
/// past that the remaining work goes on a list the entry point drains at depth
/// zero. Unify could take that shape because unification is confluent (which
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
            chain(0, S, S) :- !.
            chain(N, S0, S) :- string_concat("a", S0, S1), M is N - 1, chain(M, S1, S).
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

    /// <summary>Anti-vacuity: it is still deciding, not just surviving. One
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

    /// <summary>Anti-vacuity for the copy: a copy is fresh, so its variables
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

    /// <summary>The occurs-checked unifier could not take the deferral the
    /// plain one takes: its pair set is a path, removed on the way out, so a
    /// pair put off until later would no longer see the ancestors that make a
    /// cyclic term terminate. It walks an explicit stack with leave items
    /// instead, which keeps enter and leave nested the way a `finally`
    /// did.</summary>
    [Fact]
    public void OccursCheckedUnificationHandlesAnyDepth()
    {
        var e = Engine();
        Assert.True(e.Query(
            $"nest({Deep}, X), nest({Deep}, Y), unify_with_occurs_check(X, Y).")
            .Success);
        // Anti-vacuity: still deciding, not just surviving.
        Assert.True(e.Query(
            $"nest({Deep}, X), nest({Deep - 1}, Y), "
            + $"\\+ unify_with_occurs_check(X, Y).").Success);
    }

    /// <summary>What the path set is for, unchanged: two cyclic terms unify
    /// coinductively, two that cannot do not, and the check still refuses to
    /// build a new cycle.</summary>
    [Fact]
    public void TheOccursCheckedCyclicRulesAreUnchanged()
    {
        var e = Engine();
        Assert.True(e.Query("X = f(X), Y = f(Y), unify_with_occurs_check(X, Y).")
                     .Success);
        Assert.True(e.Query("P = [a|P], Q = [a|Q], unify_with_occurs_check(P, Q).")
                     .Success);
        Assert.True(e.Query(
            $"\\+ (U = f(U, a), V = f(V, b), unify_with_occurs_check(U, V)).")
            .Success);
        Assert.True(e.Query($"\\+ unify_with_occurs_check(V, f(V)).").Success);
    }

    /// <summary>The flag routes every unification through that path, including
    /// a deep one, and mode 2 still raises rather than failing.</summary>
    [Fact]
    public void TheOccursCheckFlagHandlesAnyDepthAndStillRaises()
    {
        var e = Engine();
        Assert.True(e.Query(
            "set_prolog_flag(occurs_check, true), "
            + $"nest({Deep}, X), nest({Deep}, Y), X = Y.").Success);
        Assert.True(e.Query(
            "set_prolog_flag(occurs_check, error), "
            + "catch(W = f(W), error(E, _), true), E == representation_error(term).")
            .Success);
    }

    /// <summary>A packed list against a cons list of the same text walks the
    /// two spines in lockstep: no C# frame and no heap cell per element. It
    /// used to re-enter the public entry point once per element, four frames
    /// that no depth guard saw, and a hundred thousand characters took the
    /// process down.</summary>
    [Fact]
    public void APackedListAgainstAConsListFarLongerThanTheStack_Unifies()
    {
        var e = Engine();
        Assert.True(e.Query(
            $"length(L, {Deep}), maplist(=(a), L), atom_chars(A, L), "
            + "atom_chars(A, P), P = L, L = P.").Success);
    }

    /// <summary>Anti-vacuity: the last element still decides.</summary>
    [Fact]
    public void APackedListThatDiffersAtItsLastElement_StillFails()
    {
        var e = Engine();
        Assert.True(e.Query(
            $"length(L0, {Deep - 1}), maplist(=(a), L0), append(L0, [b], L), "
            + "atom_chars(A0, L0), atom_concat(A0, a, A), atom_chars(A, P), "
            + "\\+ P = L, \\+ L = P.").Success);
    }

    /// <summary>It binds through, and the trail undoes it: a long list of
    /// variables takes the characters, and is a list of variables again
    /// after the double negation.</summary>
    [Fact]
    public void APackedListBindsALongListOfVariables_AndTheTrailUndoesIt()
    {
        var e = Engine();
        Assert.True(e.Query(
            $"length(L0, {Deep}), maplist(=(a), L0), atom_chars(A, L0), atom_chars(A, P), "
            + $"length(L, {Deep}), \\+ \\+ (P = L, last(L, X), X == a), "
            + "L = [V|_], var(V), last(L, W), var(W).").Success);
    }

    /// <summary>The open ends: a cons list that runs out first binds its tail
    /// to the rest of the text, a packed list that runs out first binds its
    /// tail to the rest of the cons list.</summary>
    [Fact]
    public void AnOpenTailOnEitherSideTakesTheRest()
    {
        var e = Engine();
        Assert.True(e.Query(
            $"length(L0, {Deep}), maplist(=(a), L0), atom_chars(A0, L0), "
            + "atom_concat(A0, b, A), atom_chars(A, P), "
            + "append(L0, T, Open), P = Open, T == [b].").Success);
        Assert.True(e.Query(
            $"length(L0, {Deep}), maplist(=(a), L0), partial_string(L0, P, T), "
            + "append(L0, [b], L), P = L, T == [b].").Success);
    }

    /// <summary>An attributed variable among the elements takes the general
    /// path for that one element: its hook still fires, in either direction,
    /// and backtracking still undoes the binding.</summary>
    [Fact]
    public void AnAttributedElementStillWakesItsHook()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString(":- use_module(library(coroutining)).");
        Assert.True(e.Query(
            "length(Pre, 1000), length(Post, 1000), maplist(=(a), Pre), maplist(=(a), Post), "
            + "append(Pre, [X|Post], L), freeze(X, F = fired), "
            + "length(All, 2001), maplist(=(a), All), atom_chars(A, All), atom_chars(A, P), "
            + "\\+ \\+ (P = L, X == a, F == fired), var(X), var(F), "
            + "L = P, X == a, F == fired.").Success);
    }

    /// <summary>A lazy concatenation is a chain of packed segments. The walk
    /// continues into the next segment in place, against a cons list and
    /// against a flat packed list alike; it used to recurse once per link.</summary>
    [Fact]
    public void ALongLazyConcatenationChain_UnifiesWithEitherShape()
    {
        var e = Engine();
        Assert.True(e.Query(
            $"chain({Links}, \"z\", C), length(L, {Links}), maplist(=(a), L), "
            + "append(L, [z], Lz), C = Lz, Lz = C, "
            + "atom_chars(Az, Lz), atom_chars(Az, Fz), C = Fz, Fz = C, "
            + "append(L, [y], Ly), \\+ C = Ly, atom_chars(Ay, Ly), atom_chars(Ay, Fy), \\+ Fy = C.")
            .Success);
    }

    /// <summary>The cyclic case, as for two cons lists: a packed list that is
    /// its own tail against a cons list that is its own tail is one equation
    /// repeated, rational-tree true, and a mismatch still fails. Under double
    /// negation so no cyclic answer is exported.</summary>
    [Fact]
    public void ACyclicPackedListAgainstACyclicConsList_Terminates()
    {
        var e = Engine();
        Assert.True(e.Query(
            "\\+ \\+ (partial_string([a,b], P, P), L = [a,b|L], P = L, L = P).").Success);
        Assert.True(e.Query(
            "\\+ \\+ (partial_string([a,b], P, P), partial_string([a,b], Q, Q), P = Q).").Success);
        Assert.True(e.Query(
            "\\+ \\+ (partial_string([a,b], P, P), L = [a,c|L], \\+ P = L).").Success);
    }

    /// <summary>Links in the lazy chain: string_concat/3 measures the chain it
    /// extends, so building one is quadratic, and this is enough to have
    /// overflowed a test thread's stack at a frame per link.</summary>
    private const int Links = 20_000;
}
