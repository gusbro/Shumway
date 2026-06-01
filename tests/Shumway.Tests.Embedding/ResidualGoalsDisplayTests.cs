using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Pieces the REPL relies on to display CLP(FD) / CLP(R) residual
/// constraints (e.g. <c>A in 6..9</c>) instead of leaving an unground
/// answer as a bare unbound variable. Exercises the public surface:
/// <see cref="PrologEngine.ParseGoal"/> (so the REPL can wrap with
/// <c>copy_term/3</c>) and <see cref="AstTermRenderer.Render(Term, int, Shumway.Compiler.Parsing.OperatorTable)"/>
/// (so library-defined operators like <c>in</c> and <c>..</c> render in
/// operator form once the library has been loaded).
/// </summary>
public class ResidualGoalsDisplayTests
{
    [Fact]
    public void ParseGoal_ReturnsTermAndVariables()
    {
        var engine = new PrologEngine();
        var (goal, vars) = engine.ParseGoal("foo(A, B, A).");
        Assert.IsType<CompoundTerm>(goal);
        Assert.Equal(new[] { "A", "B" }, vars);
    }

    [Fact]
    public void ParseGoal_AnonymousVarsExcluded()
    {
        var engine = new PrologEngine();
        var (_, vars) = engine.ParseGoal("foo(_, X).");
        Assert.Equal(new[] { "X" }, vars);
    }

    [Fact]
    public void Render_WithEngineOperators_UsesLibraryInfixForms()
    {
        // Before CLP(FD) is loaded, `in` and `..` are unknown operators
        // so they render canonically.
        var engine = new PrologEngine();
        Term term = new CompoundTerm("in", new Term[]
        {
            new VarTerm("A"),
            new CompoundTerm("..", new Term[] { new IntTerm(6), new IntTerm(9) }),
        });
        Assert.Equal("in(A, ..(6, 9))",
            AstTermRenderer.Render(term, 1200, engine.Operators));

        // After CLP(FD), `in` is xfx 700 and `..` is xfx 450, so the
        // same term renders in operator form — matching what the
        // REPL prints for `?- A #> 5, A #< 10.`.
        engine.UseClpfd();
        Assert.Equal("A in 6..9",
            AstTermRenderer.Render(term, 1200, engine.Operators));
    }

    [Fact]
    public void CopyTerm3_OverClpfdVariable_ProducesInDomainGoal()
    {
        // The same wrap the REPL applies (`UserGoal, copy_term([A], _, R).`)
        // pulls out the residuals: `R = [A in 6..9]` (over copy variables,
        // which the REPL then renames back to the originals for display).
        var engine = new PrologEngine();
        engine.UseClpfd();
        var sol = engine.Query("A #> 5, A #< 10, copy_term([A], _, R).");
        Assert.True(sol.Success);
        var residuals = sol["R"];
        Assert.NotNull(residuals);
        // R is a one-element list: [in(Copy, 6..9)].
        Assert.IsType<CompoundTerm>(residuals);
        var cons = (CompoundTerm)residuals!;
        Assert.Equal(".", cons.Functor);
        Assert.IsType<CompoundTerm>(cons.Args[0]);
        var goal = (CompoundTerm)cons.Args[0];
        Assert.Equal("in", goal.Functor);
        Assert.IsType<CompoundTerm>(goal.Args[1]);
        var range = (CompoundTerm)goal.Args[1];
        Assert.Equal("..", range.Functor);
        Assert.Equal(6L, ((IntTerm)range.Args[0]).Value);
        Assert.Equal(9L, ((IntTerm)range.Args[1]).Value);
    }

    [Fact]
    public void CopyTerm3_BinaryCmpAgainstConstant_NoPropagatorResidual()
    {
        // `A #> 5, A #< 10` posts $fd_lt(5, A) and $fd_lt(A, 10). Both
        // are fully captured by A's resulting domain, so the projection
        // emits only `A in 6..9` — no `5 #< A` / `A #< 10` residue
        // (matches SWI: `?- A #> 5, A #< 10.` prints `A in 6..9.`).
        var engine = new PrologEngine();
        engine.UseClpfd();
        var sol = engine.Query("A #> 5, A #< 10, copy_term([A], _, R), length(R, N).");
        Assert.True(sol.Success);
        Assert.Equal(1L, ((IntTerm)sol["N"]!).Value);
    }

    [Fact]
    public void CopyTerm3_BinaryCmpBetweenVars_PropagatorProjects()
    {
        // X #< Y between two variables can't be captured by domains, so
        // the propagator projects as `X #< Y` — once, not twice (it's
        // stored on both X and Y).
        var engine = new PrologEngine();
        engine.UseClpfd();
        var sol = engine.Query(
            "X in 1..5, Y in 3..7, X #< Y, copy_term([X, Y], _, R), length(R, N).");
        Assert.True(sol.Success);
        // Expected: [X in 1..5, X #< Y, Y in 3..7] (3 goals).
        Assert.Equal(3L, ((IntTerm)sol["N"]!).Value);
    }

    [Fact]
    public void CopyTerm3_PlusPropagator_ProjectsAsSourceForm()
    {
        var engine = new PrologEngine();
        engine.UseClpfd();
        var sol = engine.Query(
            "X + Y #= 10, X in 1..5, Y in 1..9, copy_term([X, Y], _, R).");
        Assert.True(sol.Success);
        // R contains `X + Y #= 10` somewhere — render it and look.
        string rendered = AstTermRenderer.Render(sol["R"]!, 1200, engine.Operators);
        Assert.Contains("#=10", rendered);   // `+` and `#=` are symbolic ops, rendered without spaces
    }

    [Fact]
    public void CopyTerm3_AllDistinct_ProjectsAsAllDistinct()
    {
        var engine = new PrologEngine();
        engine.UseClpfd();
        var sol = engine.Query(
            "all_distinct([X, Y, Z]), X in 1..3, Y in 1..3, Z in 1..3, " +
            "copy_term([X, Y, Z], _, R).");
        Assert.True(sol.Success);
        string rendered = AstTermRenderer.Render(sol["R"]!, 1200, engine.Operators);
        Assert.Contains("all_distinct", rendered);
    }

    [Fact]
    public void CopyTerm3_UnboundedAbove_ProducesInSupGoal()
    {
        var engine = new PrologEngine();
        engine.UseClpfd();
        var sol = engine.Query("A #> 5, copy_term([A], _, R).");
        Assert.True(sol.Success);
        var residuals = sol["R"];
        var cons = Assert.IsType<CompoundTerm>(residuals);
        var goal = Assert.IsType<CompoundTerm>(cons.Args[0]);
        Assert.Equal("in", goal.Functor);
        var range = Assert.IsType<CompoundTerm>(goal.Args[1]);
        Assert.Equal("..", range.Functor);
        Assert.Equal(6L, ((IntTerm)range.Args[0]).Value);
        Assert.Equal("sup", ((AtomTerm)range.Args[1]).Name);
    }
}
