using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// The context of an ISO error term (§7.12.2, implementation defined) names
/// the builtin that raised it, <c>Name/Arity</c>, whichever way the builtin
/// built the term: a check that had the engine in hand filled it at once, a
/// check that did not left it open, and the engine fills it where the ball
/// surfaces, before any catcher sees it. A ball a program throws itself
/// keeps its own variables.
/// </summary>
public class ErrorContextConformance
{
    private static void Succeeds(string query)
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(query).Success, $"Query failed: {query}");
    }

    [Fact]
    public void AbolishNamesItselfInEveryError()
    {
        Succeeds("catch(abolish(_), error(instantiation_error, C), true), C == abolish/1.");
        Succeeds("catch(abolish(0/a), error(type_error(atom, 0), C), true), C == abolish/1.");
        Succeeds("catch(abolish(p/(-1)), error(domain_error(not_less_than_zero, -1), C), true), "
            + "C == abolish/1.");
        Succeeds("catch(abolish(p/10000), error(representation_error(max_procedure_arity), C), true), "
            + "C == abolish/1.");
        Succeeds("catch(abolish(atom_length/2), "
            + "error(permission_error(modify, static_procedure, atom_length/2), C), true), "
            + "C == abolish/1.");
    }

    [Fact]
    public void OtherBuiltinsThatLeftTheContextOpenAreNamedToo()
    {
        Succeeds("functor(F, f, 1024), "
            + "catch(asserta(F), error(representation_error(max_procedure_arity), C), true), "
            + "C == asserta/1.");
        Succeeds("catch(see(_), error(instantiation_error, C), true), C == see/1.");
        Succeeds("catch(open(_, read, _), error(instantiation_error, C), true), C == open/3.");
        Succeeds("catch(current_op(1201, xfx, foo), error(domain_error(operator_priority, 1201), C), true), "
            + "C == current_op/3.");
    }

    [Fact]
    public void TheNameIsTheBuiltinThatRaisedNotTheOneThatCalled()
    {
        // Through a meta-call and through a solution collector the inner
        // builtin is the one named, and a catcher inside sees it filled.
        Succeeds("catch(call(abolish(_)), error(instantiation_error, C), true), C == abolish/1.");
        Succeeds("catch(findall(_, abolish(_), _), error(instantiation_error, C), true), "
            + "C == abolish/1.");
        Succeeds("catch(\\+ abolish(_), error(instantiation_error, C), true), C == abolish/1.");
    }

    [Fact]
    public void AProgramsOwnBallKeepsItsVariables()
    {
        // The catcher unifies with a COPY of the ball (7.8.10), so its
        // variable is fresh; the point is that it stays a variable.
        Succeeds("catch(throw(error(foo, _)), error(foo, C), true), var(C).");
        Succeeds("catch(throw(error(foo, bar)), error(foo, C), true), C == bar.");
        Succeeds("catch(findall(_, throw(error(foo, _)), _), error(foo, C), true), var(C).");
        Succeeds("catch(call(throw(error(foo, _))), error(foo, C), true), var(C).");
        Succeeds("catch(throw(ball), B, true), B == ball.");
    }
}
