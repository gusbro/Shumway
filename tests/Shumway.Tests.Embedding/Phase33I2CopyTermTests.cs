using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 I2 — the direct heap-to-heap <c>copy_term/2</c>
/// (<see cref="HeapTermCopy"/>) must reproduce the AST round-trip's semantics
/// exactly: structural equality, fresh independent variables, preserved
/// variable sharing, every value tag (atom / int / float / bigint / string),
/// long lists without stack overflow, and cyclic terms terminating.
/// </summary>
public class Phase33I2CopyTermTests
{
    private static bool Holds(string query) => new PrologEngine().Query(query).Success;

    [Fact]
    public void GroundTerm_CopiesStructurallyEqual()
    {
        Assert.True(Holds("copy_term(f(a, g(1,2,3), h(b,c), [x,y,z], 12345), C), " +
                          "C == f(a, g(1,2,3), h(b,c), [x,y,z], 12345)."));
    }

    [Fact]
    public void UnboundVar_CopyIsFreshAndIndependent()
    {
        // The copy is a NEW variable, not the original.
        Assert.True(Holds("copy_term(X, Y), X \\== Y, var(X), var(Y)."));
        // Binding the copy does not bind the original.
        Assert.True(Holds("copy_term(X, Y), Y = 1, var(X)."));
    }

    [Fact]
    public void SharedVariable_StaysShared()
    {
        // Both occurrences of X map to ONE fresh var — so the two copy slots unify.
        Assert.True(Holds("copy_term(f(X, X), f(A, B)), A == B."));
        Assert.True(Holds("copy_term(f(X, X), C), C = f(1, V), V == 1."));
    }

    [Fact]
    public void DistinctVariables_StayDistinct()
    {
        Assert.True(Holds("copy_term(f(X, Y), f(A, B)), A \\== B."));
        // Independent: binding one copy var leaves the other free.
        Assert.True(Holds("copy_term(f(X, Y), f(A, B)), A = 1, var(B)."));
    }

    [Fact]
    public void ListWithSharedTail_PreservesSharing()
    {
        // p([X,Y|Z], Z): the partial-list tail Z is shared with the last arg.
        Assert.True(Holds("copy_term(p([X,Y|Z], Z), p(L, T)), L = [1,2,3], T == [3]."));
    }

    [Fact]
    public void FloatAndBigInt_CopyByValue()
    {
        Assert.True(Holds("copy_term(pt(3.14, -2.5), C), C == pt(3.14, -2.5)."));
        // A value well past the 60-bit inline range → BIGINT side table.
        Assert.True(Holds("copy_term(big(123456789012345678901234567890), C), " +
                          "C == big(123456789012345678901234567890)."));
    }

    [Fact]
    public void Strings_CopyByValue()
    {
        Assert.True(Holds("copy_term(s(\"hello world\"), C), C == s(\"hello world\")."));
    }

    [Fact]
    public void DeepList_NoStackOverflow()
    {
        // The spine walk is iterative — a 100k-element list must not overflow.
        Assert.True(Holds("numlist(1, 100000, L), copy_term(L, C), " +
                          "last(C, X), X == 100000, length(C, N), N == 100000."));
    }

    [Fact]
    public void NestedCompoundInList_CopiesEqual()
    {
        Assert.True(Holds("copy_term([a-1, b-2, foo(bar(baz))], C), " +
                          "C == [a-1, b-2, foo(bar(baz))]."));
    }

    [Fact]
    public void CyclicTerm_Terminates()
    {
        // X = f(X) is a rational (cyclic) tree via occurs-check-off '='.
        // copy_term must TERMINATE — the heap-to-heap copy preserves the cycle
        // (register-before-recurse), and it did. Probe ONLY non-recursively:
        // '==' on a cyclic term overflows AreStructurallyEqual, a pre-existing
        // engine limitation unrelated to the copy. functor/3 + arg/3 read one
        // level: the copy is f/1 and its argument is again f/1 (cycle intact).
        Assert.True(Holds(
            "X = f(X), copy_term(X, C), functor(C, f, 1), arg(1, C, A), functor(A, f, 1)."));
    }

    [Fact]
    public void CopyIndependence_MutatingCopyLeavesOriginalFree()
    {
        // After copying, the two share no variables: unifying the copy fully
        // leaves the original's variables unbound.
        Assert.True(Holds("T = t(A, B, A), copy_term(T, C), C = t(1, 2, 1), " +
                          "var(A), var(B)."));
    }
}
