using System.IO;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-063: the image of a structure on the heap, for a fact asserted
/// at run time: its cells as the heap holds them, a shared sub-term laid once,
/// a variable a cell of its own, an attributed variable a plain one; none for
/// a cyclic term or a value whose cell names a table of the activation.</summary>
public sealed class TermImageCopierTests
{
    private static PrologEngine Engine()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        Probe.Register(e);
        return e;
    }

    private static bool Holds(string query) => Engine().Query(query).Success;

    /// <summary>What the tests ask from inside a query. Registered once: a
    /// foreign predicate's name is taken process-wide.</summary>
    public static class Probe
    {
        private static bool _registered;

        public static void Register(PrologEngine engine)
        {
            lock (typeof(Probe))
            {
                if (_registered) return;
                engine.RegisterPredicates(typeof(Probe));
                _registered = true;
            }
        }

        /// <summary><c>image_copy(+Term, -Copy, -Cells)</c>: the term through
        /// its image and back, and the image's cell count; fails for a term an
        /// image cannot carry.</summary>
        [PrologPredicate("image_copy/3")]
        public static bool Copy(Activation engine)
        {
            var image = TermImageCopier.FromHeap(engine, engine.GetRegister(0));
            if (image is null) return false;
            return engine.UnifyRegisterWithCell(1, engine.BuildImage(image))
                && engine.UnifyRegisterWithCell(2, Cell.Int(image.Length));
        }
    }

    [Fact]
    public void EveryKindOfCellRoundTrips()
    {
        Assert.True(Holds(@"T = f(a, [], 1, -5, 5000000000, 2.5, ""txt"", [1, 2|R], g(h(R))),
            image_copy(T, C, _), C = f(a, [], 1, -5, 5000000000, F, S, [1, 2|R2], g(h(R3))),
            F =:= 2.5, S == ""txt"", var(R2), R2 == R3, R2 \== R."));
        // Three cells for the head: the functor and two arguments.
        Assert.True(Holds("image_copy(f(a, b), _, N), N == 3."));
        Assert.True(Holds("image_copy(f(X, g(X, Y), Y), C, N), N == 7, C = f(A, g(B, D), E), A == B, D == E, A \\== D, A \\== X."));
    }

    [Fact]
    public void ASharedSubTermIsLaidOnceAndStaysShared()
    {
        Assert.True(Holds("S = g(1, 2), image_copy(f(S, S), C, N), N == 6, C = f(A, B), A == B, A == g(1, 2)."));
    }

    [Fact]
    public void AnAttributedVariableIsAPlainVariableOfTheCopy()
    {
        Assert.True(Holds("put_attr(V, m, 1), image_copy(f(V, V), f(A, B), _), var(A), A == B, \\+ get_attr(A, m, _), A \\== V."));
    }

    [Fact]
    public void WhatAnImageCannotCarryMakesNone()
    {
        Assert.True(Holds("X = f(X), \\+ image_copy(X, _, _)."));
        Assert.True(Holds("X = f(g(Y)), Y = h(X), \\+ image_copy(X, _, _)."));
        Assert.True(Holds("\\+ image_copy(f(123456789012345678901234567890), _, _)."));
        Assert.True(Holds("R is 1 rdiv 3, \\+ image_copy(f(R), _, _)."));
        Assert.True(Holds("\\+ image_copy(a, _, _), \\+ image_copy([a], _, _), \\+ image_copy(_, _, _)."));
        // A term an image can carry, after one it cannot: the scratch is clean.
        Assert.True(Holds("X = f(X), \\+ image_copy(X, _, _), image_copy(f(a), C, _), C == f(a)."));
    }
}
