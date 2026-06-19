using System.Collections.Generic;
using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Xunit;

namespace Shumway.Tests.Compiler.Parsing;

/// <summary>ADR-022 step 1 — the parser now CAPTURES the raw spans of Arity
/// embedded native code instead of discarding them:
/// <list type="bullet">
/// <item>a <c>{ …C… }</c> body block becomes <c>'$native_goal'(RawText)</c>
/// (raw C statements as a non-interned <see cref="StringTerm"/>), instead of the
/// previous <c>true</c> no-op substitution;</item>
/// <item>a <c>:- c.</c> … <c>:- prolog.</c> region becomes a synthetic
/// <c>:- '$native_decls'(RawText)</c> directive instead of being skipped to
/// nothing.</item>
/// </list>
/// (No C is parsed yet — that is step 2. <c>'$native_goal'/1</c> is a no-op
/// builtin so runtime behaviour is unchanged.)</summary>
public sealed class NativeBlockCaptureTests
{
    private static List<Clause> ReadArity(string src) =>
        new ClauseReader(new Shumway.Compiler.Lexer.Lexer(src), OperatorTable.Default(),
            new PrologFlags { ArityCompat = true }).ReadAll().ToList();

    private static IEnumerable<CompoundTerm> Find(Term t, string functor, int arity)
    {
        if (t is CompoundTerm c)
        {
            if (c.Functor == functor && c.Args.Length == arity) yield return c;
            foreach (var a in c.Args)
                foreach (var m in Find(a, functor, arity)) yield return m;
        }
    }

    [Fact]
    public void NativeGoalBlock_CapturedAsStringTerm()
    {
        var clauses = ReadArity("p(X) :- q(X), { X is 'strlen'(X) }, r(X).\n");
        var rule = clauses.Single(c => c.Kind == ClauseKind.Rule);

        var goals = Find(rule.Term, "$native_goal", 1).ToList();
        Assert.Single(goals);
        var str = Assert.IsType<StringTerm>(goals[0].Args[0]);
        Assert.Contains("strlen", str.Content);     // the raw C text is preserved
        Assert.DoesNotContain("{", str.Content);     // braces are NOT part of the span
        Assert.DoesNotContain("}", str.Content);

        // The goals before and after the block survive as normal Prolog.
        Assert.NotEmpty(Find(rule.Term, "q", 1));
        Assert.NotEmpty(Find(rule.Term, "r", 1));
    }

    [Fact]
    public void CRegion_CapturedAsNativeDeclsDirective()
    {
        var clauses = ReadArity(
            ":- c.\nchar buf[255];\nint strcmp(const char*, const char*);\n:- prolog.\nq.\n");

        var decls = clauses
            .Where(c => c.Kind == ClauseKind.Directive
                        && c.Term is CompoundTerm d && d.Functor == ":-" && d.Args.Length == 1
                        && d.Args[0] is CompoundTerm dd
                        && dd.Functor == "$native_decls" && dd.Args.Length == 1)
            .ToList();
        Assert.Single(decls);

        var inner = (CompoundTerm)((CompoundTerm)decls[0].Term).Args[0];
        var str = Assert.IsType<StringTerm>(inner.Args[0]);
        Assert.Contains("strcmp", str.Content);      // the C declarations are preserved
        Assert.Contains("char buf[255]", str.Content);
        Assert.DoesNotContain("prolog", str.Content); // the `:- prolog.` line is excluded

        // The Prolog clause AFTER the region is not swallowed (the old bug for
        // `#prolog`; here `:- prolog.` correctly resumes Prolog).
        Assert.Contains(clauses, c => c.Kind == ClauseKind.Fact
            && c.Term is AtomTerm { Name: "q" });
    }

    [Fact]
    public void NonArity_BraceKeepsCurlyMeaning()
    {
        // Without arity_compat, `{X}` is the ISO {}/1 term, not a native goal.
        var clauses = new ClauseReader(new Shumway.Compiler.Lexer.Lexer("p :- {a}.\n"),
            OperatorTable.Default(), new PrologFlags { ArityCompat = false })
            .ReadAll().ToList();
        var rule = clauses.Single(c => c.Kind == ClauseKind.Rule);
        Assert.Empty(Find(rule.Term, "$native_goal", 1));
        Assert.NotEmpty(Find(rule.Term, "{}", 1));
    }
}
