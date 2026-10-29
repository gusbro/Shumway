using System.Collections.Generic;
using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary><c>predicate_property/2</c> reporting
/// <c>meta_predicate(Template)</c> from recorded <c>:- meta_predicate</c>
/// directives — including for a module-qualified query (<c>user:freeze</c>),
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
        e.ConsultString("""
            :- meta_predicate(my_call(0, *)).
            my_call(G, _) :- call(G).
            """);
        Assert.Equal("my_call(0, *)", TemplateOf(e, "my_call(_, _)"));
    }

    [Fact]
    public void CommaConjunctionOfTemplates_RecordsEach()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- meta_predicate((mc_a(0), mc_b(*, 0))).
            mc_a(G) :- call(G).
            mc_b(_, G) :- call(G).
            """);
        Assert.Equal("mc_a(0)", TemplateOf(e, "mc_a(_)"));
        Assert.Equal("mc_b(*, 0)", TemplateOf(e, "mc_b(_, _)"));
    }

    // Every documented predicate whose reference template marks an argument
    // ':' (a goal, a closure, a grammar body), with its library loaded. M:Goal
    // qualifies a goal and is no predicate with properties.
    public static IEnumerable<object[]> DocumentedMetaPredicates()
        => PredicateDoc.Entries()
            .Where(d => d.Name != ":" && DocumentedArgs(d.Template).Any(a => a.StartsWith(':')))
            .Select(d => new object[] { d.Name, d.Arity, d.Template });

    // The template's arguments as written, up to a "..." (call/N), past which
    // the positions are not spelled out.
    private static List<string> DocumentedArgs(string template)
    {
        int open = template.IndexOf('(');
        if (open < 0 || !template.EndsWith(")")) return new List<string>();
        var args = new List<string>();
        foreach (string a in template.Substring(open + 1, template.Length - open - 2).Split(','))
        {
            string arg = a.Trim();
            if (arg == "...") break;
            args.Add(arg);
        }
        return args;
    }

    // A meta-argument specifier: a goal or closure (an integer), a goal under
    // ^, a grammar body (//), a module-sensitive argument (:).
    private static bool IsMetaSpecifier(Term spec)
        => spec is IntTerm || spec is AtomTerm { Name: "^" or "//" or ":" };

    [Theory]
    [MemberData(nameof(DocumentedMetaPredicates))]
    public void APredicateDocumentedWithAGoalArgumentDeclaresIt(string name, int arity, string template)
    {
        var e = new PrologEngine();
        e.Query("use_module(library(coroutining)), use_module(library(reif)).");
        // The reference writes a name that needs quotes quoted: ','/3.
        string quoted = name.StartsWith("'") ? name
            : "'" + name.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

        var sol = e.Query($"functor(G, {quoted}, {arity}), predicate_property(G, meta_predicate(T)).");

        Assert.True(sol.Success, $"{template} reports no meta_predicate");
        var specs = ((CompoundTerm)sol["T"]!).Args;
        var documented = DocumentedArgs(template);
        for (int i = 0; i < documented.Count; i++)
            Assert.True(documented[i].StartsWith(':') == IsMetaSpecifier(specs[i]),
                $"{template}: argument {i + 1} is documented {documented[i]}, declared {AstTermRenderer.Render(specs[i])}");
    }

    [Theory]
    [InlineData(@"','(_, _)", @"(0, 0)")]
    [InlineData(@";(_, _)", @"(0 ; 0)")]
    [InlineData(@"->(_, _)", @"(0 -> 0)")]
    [InlineData(@"*->(_, _)", @"(0 *-> 0)")]
    [InlineData(@"\+(_)", @"(\+ 0)")]
    public void AControlConstructIsABuiltInMetaPredicate(string head, string expected)
    {
        var e = new PrologEngine();

        Assert.True(e.Query($@"G = {head}, predicate_property(G, built_in),
            predicate_property(G, meta_predicate(T)), T == {expected}.").Success);
    }

    [Fact]
    public void AModulesPrivateNonTerminalReachesPhrase()
    {
        // phrase/2 declares its body a grammar body (//): a body the module's
        // clause hands it in a variable takes the module along, so phrase/2's
        // clauses find the module's private rule. A body written in the call is
        // translated where it stands and never needs the declaration.
        var e = new PrologEngine();
        e.ConsultString("""
            :- module(greet, [hello/2]).
            hello(Body, L) :- phrase(Body, L).
            salute --> [hi], who.
            who --> [there].
            """);

        Assert.True(e.Query("greet:hello(salute, [hi, there]).").Success);
    }

    [Fact]
    public void APredicateWithoutATemplate_HasNoSuchProperty()
    {
        var e = new PrologEngine();
        e.ConsultString("plain_p(1).");
        Assert.False(
            e.Query("predicate_property(plain_p(_), meta_predicate(_)).").Success);
        // The atom properties still answer.
        Assert.True(
            e.Query("predicate_property(plain_p(_), defined).").Success);
    }
}
