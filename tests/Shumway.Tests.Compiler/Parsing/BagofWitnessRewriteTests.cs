using System.Collections.Generic;
using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Xunit;

namespace Shumway.Tests.Compiler.Parsing;

/// <summary>A compiled bagof/setof keeps the witness written from the variable
/// names when no name can stand for a bound variable at the call, and computes
/// it with <c>'$bagof_free'</c> otherwise. A goal that is a variable under its
/// ^ prefixes is not rewritten at all: the runtime bagof/3 strips the ^ of
/// its value.</summary>
public class BagofWitnessRewriteTests
{
    private static List<Clause> Rewrite(string src) =>
        MetaTransform.Apply(new ClauseReader(src).ReadAll().ToList());

    private static bool Calls(List<Clause> clauses, string functor, int arity)
    {
        var work = new List<Term>(clauses.Select(c => c.Term));
        while (work.Count > 0)
        {
            Term t = work[^1];
            work.RemoveAt(work.Count - 1);
            if (t is CompoundTerm c)
            {
                if (c.Functor == functor && c.Args.Length == arity) return true;
                work.AddRange(c.Args);
            }
        }
        return false;
    }

    [Theory]
    [InlineData(@"p(Y, L) :- bagof(X, foo(X, Y), L).")]
    [InlineData(@"p(L) :- bagof(X, foo(X, Y), L), q(Y).")]
    [InlineData(@"p(L) :- q(X), bagof(X, foo(X, Y), L).")]
    [InlineData(@"p(L) :- ( q ; bagof(X, foo(X, Y), L) ).")]
    [InlineData(@"p(L) :- \+ \+ setof(X, foo(X, Y), L).")]
    [InlineData(@"p(L) :- G = foo(1), bagof(X, foo(X), L), call(G).")]
    public void TheWrittenWitnessIsKeptWhenNoNameCanBeBound(string clause)
    {
        var rewritten = Rewrite(clause);

        Assert.True(Calls(rewritten, "$bagof_next", 2));
        Assert.False(Calls(rewritten, "$bagof_free", 3));
    }

    [Theory]
    [InlineData(@"p(L) :- Z = g(Y), bagof(X, Y^foo(X, Z), L).")]
    [InlineData(@"p(X, L) :- X = f(A), bagof(X, foo(A, _), L).")]
    [InlineData(@"p(L) :- G = foo(X, Y), bagof(X, Y^call(G), L).")]
    [InlineData(@"p(L) :- q(X, Y), setof(X, foo(X, Y), L).")]
    [InlineData(@"p(L) :- ( q(X, Y) ; true ), bagof(X, foo(X, Y), L).")]
    public void TheWitnessIsComputedAtTheCallWhenANameMayBeBound(string clause)
    {
        var rewritten = Rewrite(clause);

        Assert.True(Calls(rewritten, "$bagof_next", 2));
        Assert.True(Calls(rewritten, "$bagof_free", 3));
    }

    [Theory]
    [InlineData(@"p(L) :- G = foo(X, Y), bagof(X, Y^G, L).")]
    [InlineData(@"p(G, L) :- setof(X, Y^Z^G, L).")]
    public void AVariableGoalUnderItsQuantifiersIsLeftToTheRuntime(string clause)
    {
        var rewritten = Rewrite(clause);

        Assert.False(Calls(rewritten, "$bagof_next", 2));
        Assert.True(Calls(rewritten, clause.Contains("setof") ? "setof" : "bagof", 3));
    }
}
