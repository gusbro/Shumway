using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Conditional compilation — <c>:- if(Cond) / :- elif(Cond) / :- else /
/// :- endif</c>. A first-class load-time feature (SWI, GProlog, SICStus): clauses
/// in an inactive branch are dropped. Conditions are evaluated against the current
/// engine state.</summary>
public sealed class ConditionalCompilationTests
{
    [Fact]
    public void IfTrue_IncludesBranch_ElseSkipped()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- if(current_predicate(atom_length/2)).\n"   // a builtin — true
            + "p(taken).\n"
            + ":- else.\n"
            + "p(skipped).\n"
            + ":- endif.\n");
        Assert.True(e.Query("p(taken).").Success);
        Assert.False(e.Query("p(skipped).").Success);
    }

    [Fact]
    public void IfFalse_SkipsBranch_ElseIncluded()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- if(current_predicate(no_such_predicate_xyz/9)).
            q(taken).
            :- else.
            q(else).
            :- endif.
            """);
        Assert.False(e.Query("q(taken).").Success);
        Assert.True(e.Query("q(else).").Success);
    }

    [Fact]
    public void Elif_FirstTrueBranchWins()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- if(fail).\n"
            + "r(a).\n"
            + ":- elif(current_predicate(atom_length/2)).\n"   // true
            + "r(b).\n"
            + ":- elif(true).\n"
            + "r(c).\n"
            + ":- else.\n"
            + "r(d).\n"
            + ":- endif.\n");
        Assert.True(e.Query("findall(X, r(X), [b]).").Success);   // only the elif branch
        Assert.False(e.Query("r(a).").Success);
        Assert.False(e.Query("r(c).").Success);
        Assert.False(e.Query("r(d).").Success);
    }

    [Fact]
    public void Nested_InnerSkipped_WhenOuterSkipped()
    {
        var e = new PrologEngine();
        // Outer is false → the whole block (including the inner if's TRUE branch)
        // is skipped.
        e.ConsultString("""
            :- if(fail).
            :- if(true).
            s(inner).
            :- endif.
            s(outer).
            :- else.
            s(elsebranch).
            :- endif.
            """);
        Assert.False(e.Query("s(inner).").Success);
        Assert.False(e.Query("s(outer).").Success);
        Assert.True(e.Query("s(elsebranch).").Success);
    }

    [Fact]
    public void ConditionBooleanCombinators()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- if((current_predicate(atom_length/2), \+ current_predicate(nope_zzz/1))).
            t(yes).
            :- endif.
            """);
        Assert.True(e.Query("t(yes).").Success);
    }
}
