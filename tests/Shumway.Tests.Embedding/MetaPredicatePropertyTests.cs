using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary><c>predicate_property/2</c> reporting
/// <c>meta_predicate(Template)</c> from recorded <c>:- meta_predicate</c>
/// directives — including for a MODULE-QUALIFIED query (<c>user:freeze</c>),
/// which is the exact shape Logtalk's compiler asks to decide whether a goal
/// argument must be wrapped for its calling context. Without this, a goal
/// handed through a qualified forwarding call woke up unwrapped in
/// <c>user</c>, unable to see the predicates it was written against.</summary>
public sealed class MetaPredicatePropertyTests
{
    private static string TemplateOf(PrologEngine e, string headExpr)
    {
        var sol = e.Query($"predicate_property({headExpr}, meta_predicate(T)).");
        Assert.True(sol.Success);
        return AstTermRenderer.Render(sol["T"]!);
    }

    [Fact]
    public void LibraryDeclaredTemplate_IsReported()
    {
        var e = new PrologEngine();
        e.Query("use_module(library(coroutining)).");
        Assert.Equal("freeze(*, 0)", TemplateOf(e, "freeze(_, _)"));
        Assert.Equal("when(*, 0)", TemplateOf(e, "when(_, _)"));
    }

    [Fact]
    public void ModuleQualifiedQuery_AnswersForTheBareName()
    {
        var e = new PrologEngine();
        e.Query("use_module(library(coroutining)).");
        Assert.Equal("freeze(*, 0)", TemplateOf(e, "user:freeze(_, _)"));
    }

    [Fact]
    public void UserDeclaredTemplate_IsReported()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- meta_predicate(my_call(0, *)).\n"
            + "my_call(G, _) :- call(G).\n");
        Assert.Equal("my_call(0, *)", TemplateOf(e, "my_call(_, _)"));
    }

    [Fact]
    public void CommaConjunctionOfTemplates_RecordsEach()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- meta_predicate((mc_a(0), mc_b(*, 0))).\n"
            + "mc_a(G) :- call(G).\n"
            + "mc_b(_, G) :- call(G).\n");
        Assert.Equal("mc_a(0)", TemplateOf(e, "mc_a(_)"));
        Assert.Equal("mc_b(*, 0)", TemplateOf(e, "mc_b(_, _)"));
    }

    [Fact]
    public void APredicateWithoutATemplate_HasNoSuchProperty()
    {
        var e = new PrologEngine();
        e.ConsultString("plain_p(1).\n");
        Assert.False(
            e.Query("predicate_property(plain_p(_), meta_predicate(_)).").Success);
        // The atom properties still answer.
        Assert.True(
            e.Query("predicate_property(plain_p(_), defined).").Success);
    }
}
