using System.Linq;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>findall/3, findall/4, bagof/3 and setof/3 copy each solution off the
/// heap into a solution buffer as the goal finds it, and back when the call
/// collects; global variables keep their value the same way. Every kind of
/// term goes through the same copy: floats, packed strings, big integers,
/// rationals and foreign objects included, and a term of any depth. A throw out
/// of a call drops its buffer at the catch/3 that takes the ball.</summary>
public sealed class SolutionBufferTests
{
    private static PrologEngine Engine()
    {
        var e = new PrologEngine { Out = new System.IO.StringWriter() };
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

        [PrologPredicate("solutions_open/1")]
        public static bool Open(Activation engine)
            => engine.UnifyRegisterWithCell(0, Cell.Int(engine.SolutionsInProgress));

        [PrologPredicate("make_object/1")]
        public static bool MakeObject(Activation engine)
            => engine.UnifyRegisterWithCell(0, engine.MakeForeign(new object()));

        [PrologPredicate("same_object/2")]
        public static bool SameObject(Activation engine)
        {
            Cell a = engine.GetHeap(engine.Deref(engine.GetRegister(0).AsHeapIndex));
            Cell b = engine.GetHeap(engine.Deref(engine.GetRegister(1).AsHeapIndex));
            return a.Tag == Tag.Foreign && b.Tag == Tag.Foreign
                && ReferenceEquals(engine.AsForeign(a), engine.AsForeign(b));
        }
    }

    [Fact]
    public void AThrowCaughtInsideACall_LeavesTheEnclosingCallItsOwnSolutions()
    {
        const string inner =
            @"catch(findall(Y, (member(Y, [a,b]), (Y == b -> throw(oops) ; true)), _), oops, true)";
        Assert.True(Holds($@"findall(X, (member(X, [1,2]), {inner}), L), L == [1,2]."));
        Assert.True(Holds($@"bagof(X, (member(X, [1,2]), {inner}), L), L == [1,2]."));
        Assert.True(Holds($@"setof(X, (member(X, [2,1]), {inner}), L), L == [1,2]."));
        Assert.True(Holds($@"G = (member(X, [1,2]), {inner}), findall(X, G, L), L == [1,2]."));
    }

    [Fact]
    public void ACallAThrowLeaves_DoesNotStayOpen()
    {
        Assert.True(Holds(
            @"( between(1, 100, _), catch(findall(X, (X = 1, throw(e)), _), e, true), fail
              ; solutions_open(N), N == 0 )."));
        Assert.True(Holds(@"findall(N, solutions_open(N), L), L == [1]."));
        Assert.True(Holds(@"solutions_open(0)."));
    }

    [Fact]
    public void FloatsAreCopied()
    {
        Assert.True(Holds(@"findall(X, member(X, [1.5, -0.25, 2.0e10]), L), L == [1.5, -0.25, 2.0e10]."));
        Assert.True(Holds(@"F = 1.5, findall(g(F, F), true, [g(A, B)]), A == 1.5, B == 1.5."));
    }

    [Fact]
    public void PackedStringsAreCopied()
    {
        Assert.True(Holds(@"findall(X, member(X, [""abc"", ""de""]), L), L == [""abc"", ""de""]."));
        // The tails of a packed string start inside its buffer.
        Assert.True(Holds(@"findall(T, append(_, T, ""abcdef""), L),
            L == [""abcdef"", ""bcdef"", ""cdef"", ""def"", ""ef"", ""f"", []]."));
        Assert.True(Holds(@"append(""ab"", ""cd"", S), findall(S, true, [T]), T == ""abcd""."));
        // The flag applies to the text read after it.
        var e = Engine();
        Assert.True(e.Query("set_prolog_flag(double_quotes, codes).").Success);
        Assert.True(e.Query(@"X = ""ab"", findall(X, true, [Y]), Y == [97, 98].").Success);
    }

    [Fact]
    public void BigIntegersAndRationalsAreCopied()
    {
        Assert.True(Holds(@"X is 2**100, Y is -X, findall(Z, member(Z, [X, Y]), L), L == [X, Y]."));
        Assert.True(Holds(@"R is 1 rdiv 3, findall(k(R, [R]), true, [k(A, [B])]), A == R, B == R."));
        Assert.True(Holds(
            @"findall(f(X, ""s"", B), (member(X, [1.5, 2.5]), B is 2**70), L),
              B70 is 2**70, L == [f(1.5, ""s"", B70), f(2.5, ""s"", B70)]."));
    }

    [Fact]
    public void ForeignObjectsAreCopied()
    {
        Assert.True(Holds(@"make_object(O), findall(O, true, [P]), same_object(O, P)."));
    }

    [Fact]
    public void SharingInsideASolutionSurvives_AndAttributesDoNot()
    {
        Assert.True(Holds(@"findall(f(X, X, Y), true, [f(A, B, C)]), A == B, A \== C."));
        Assert.True(Holds(@"put_attr(X, m, 1), findall(X, true, [Y]), var(Y), \+ get_attr(Y, m, _)."));
    }

    /// <summary>The copy is iterative: a term of any depth.</summary>
    [Fact]
    public void ADeepTermIsCopied()
    {
        var e = Engine();
        e.ConsultString("nest(0, z) :- !.\nnest(N, s(X)) :- N1 is N - 1, nest(N1, X).\n");
        Assert.True(e.Query("nest(300000, T), findall(T, true, [U]), U == T.").Success);
        Assert.True(e.Query("nest(300000, T), nb_setval(deep, T), nb_getval(deep, U), U == T.").Success);
    }

    /// <summary>The buffer grows by chunks of 4,096 cells: solutions, and single
    /// solutions and blocks, larger than one.</summary>
    [Fact]
    public void SolutionsLargerThanAChunkAreCopied()
    {
        Assert.True(Holds(
            @"findall(f(X, g(X), [X, a]), between(1, 2000, X), L), length(L, 2000),
              nth1(1500, L, E), E == f(1500, g(1500), [1500, a])."));
        // A findall solution of an integer is two cells: 2,048 fill the first chunk
        // exactly, and the next one starts a chunk.
        Assert.True(Holds(@"findall(X, between(1, 2048, X), L), length(L, 2048), last(L, 2048)."));
        Assert.True(Holds(@"findall(X, between(1, 2049, X), L), nth1(2048, L, 2048), last(L, 2049)."));
        Assert.True(Holds(
            @"findall(T, (between(1, 6000, I), (I mod 3 =:= 0 -> T = f(I) ; T = I)), L),
              nth1(4500, L, E), E == f(4500), nth1(4501, L, 4501), last(L, f(6000))."));
        Assert.True(Holds(@"numlist(1, 5000, N), findall(N, true, [M]), M == N."));
        Assert.True(Holds(
            @"numlist(1, 5000, N), bagof(N-K, member(K, [a, b]), L), L == [N-a, N-b]."));
        Assert.True(Holds(
            @"length(Cs, 20000), maplist(=(a), Cs), atom_chars(A, Cs), atom_codes(A, S),
              findall(S, true, [T]), T == S."));
    }

    [Fact]
    public void TheResultUnifiesWithWhatTheCallerGave()
    {
        Assert.True(Holds(@"findall(X, member(X, [1,2]), [1,2])."));
        Assert.False(Holds(@"findall(X, member(X, [1,2]), [2,1])."));
        Assert.True(Holds(@"findall(X, member(X, [1,2]), [A|T]), A == 1, T == [2]."));
        // The template's variable is the caller's too; the solutions are copies.
        Assert.True(Holds(@"findall(X, member(X, [1,2]), [X|_]), X == 1."));
        Assert.True(Holds(@"catch(findall(X, true, foo), error(type_error(list, foo), _), true)."));
    }

    [Fact]
    public void FindallFourEndsInItsTail()
    {
        Assert.True(Holds(@"findall(X, member(X, [1,2]), L, [3]), L == [1,2,3]."));
        Assert.True(Holds(@"findall(X, fail, L, T), L == T."));
        Assert.True(Holds(@"findall(X, member(X, [1,2]), L, T), T = [], L == [1,2]."));
        Assert.True(Holds(@"findall(X, member(X, [1.5, ""a""]), L, []), L == [1.5, ""a""]."));
    }

    [Fact]
    public void BagofAndSetofGroupSolutionsWithEveryKindOfTerm()
    {
        Assert.True(Holds(
            @"findall(K-L, bagof(X, member(X-K, [1.5-a, 2.5-b, 3.5-a]), L), R),
              R == [a-[1.5, 3.5], b-[2.5]]."));
        Assert.True(Holds(
            @"findall(K-L, bagof(X, member(X-K, [1-1.5, 2-2.5, 3-1.5]), L), R),
              R == [1.5-[1, 3], 2.5-[2]]."));
        Assert.True(Holds(@"setof(S, member(S, [""b"", ""a"", ""b""]), L), L == [""a"", ""b""]."));
        Assert.True(Holds(@"G = member(X, [2.5, 1.5]), setof(X, G, L), L == [1.5, 2.5]."));
    }

    /// <summary>A global variable outlives the query, and with it the activation
    /// whose side tables held its big integer.</summary>
    [Fact]
    public void AGlobalVariableKeepsEveryKindOfTermAcrossQueries()
    {
        var e = Engine();
        Assert.True(e.Query(@"B is 2**80, R is 1 rdiv 7, nb_setval(k, f(1.5, ""ab"", B, R))."
            ).Success);
        Assert.True(e.Query(@"nb_getval(k, V), B is 2**80, R is 1 rdiv 7,
            V == f(1.5, ""ab"", B, R)."
            ).Success);
    }
}
