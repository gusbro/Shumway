using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Regression: <c>retract/1</c> used to read the pattern back from
/// register 0 inside its CP-resume path. Under heavy
/// dynamic-mutation load (Blint.pl's <c>retract(next_char_i(X))</c>
/// loop linting Blint.pl) the saved arg slot in the CP frame would
/// get clobbered between push and pop, so the resume read a stale
/// REF and bound the pattern's variable to the WHOLE candidate STR
/// term instead of its argument:
///
/// <code>
///   retract(next_char_i(X))  →  X = next_char_i(a)   // WRONG (was the whole head)
///                                                     // expected X = a
/// </code>
///
/// The fix captures the pattern's heap home up-front and closes it
/// over the resume delegate, so the second + subsequent retract
/// steps don't have to trust register 0 anymore. Diagnosed via
/// <c>RetractTrace</c> + <c>PopRestoreTrace</c> (compile-time
/// instrumentation gated on <c>ShumwayRetractTrace</c>).
/// </summary>
public class RetractRegisterClobberRegression
{
    [Fact]
    public void Retract_OnBacktrack_RebindsToArgNotHead()
    {
        var e = new PrologEngine();
        e.ConsultString(@"
:- dynamic q/1.
:- public test/2.
test(X1, X2) :-
  assertz(q(a)),
  assertz(q(b)),
  retract(q(X1)),
  % Force a sub-goal that uses register 0 between the two retract
  % steps — this is what would clobber the saved arg in the pre-
  % fix code path.
  atom_codes(X1, _),
  retract(q(X2)).
");
        var sol = e.Query("test(A, B).");
        Assert.True(sol.Success);
        Assert.Equal(new Shumway.Compiler.Ast.AtomTerm("a"), sol["A"]);
        Assert.Equal(new Shumway.Compiler.Ast.AtomTerm("b"), sol["B"]);
    }

    [Fact]
    public void Retract_FailureDrivenLoop_PreservesValues()
    {
        // The Blint-shape failure-driven retract loop: assert N values,
        // retract them one by one in a `(... , fail ; true)` loop,
        // accumulate the values into a list. With the pre-fix register-
        // clobber bug, some retracts on backtracking returned the
        // wrapped head term instead of the argument.
        var e = new PrologEngine();
        e.ConsultString(@"
:- dynamic q/1.
:- public take_all/1.
take_all(L) :-
  assertz(q(1)), assertz(q(2)), assertz(q(3)),
  assertz(q(4)), assertz(q(5)),
  findall(X, retract(q(X)), L).
");
        var sol = e.Query("take_all(L).");
        Assert.True(sol.Success);
        // L should be [1,2,3,4,5] — each element an Int, not a q(N) compound.
        var l = sol["L"];
        int count = 0;
        Shumway.Compiler.Ast.Term cursor = l;
        while (cursor is Shumway.Compiler.Ast.CompoundTerm c
               && c.Functor == "." && c.Args.Length == 2)
        {
            Assert.IsType<Shumway.Compiler.Ast.IntTerm>(c.Args[0]);
            count++;
            cursor = c.Args[1];
        }
        Assert.Equal(5, count);
    }
}
