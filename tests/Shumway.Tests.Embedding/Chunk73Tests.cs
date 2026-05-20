using Shumway.Compiler.Modes;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 73 — mode-analysis foundation (Phase 3 starts). ADR-012's
/// <c>:- mode</c> directive was parsed and stored as raw strings since
/// chunk 28; chunk 73 builds the real data model: a typed
/// <see cref="ModeDeclaration"/> (arg indicators + determinism),
/// support for multiple declarations per predicate, the
/// <c>is det</c> / <c>is semidet</c> / ... determinism annotation, and
/// a queryable <see cref="ModeTable"/> with a semantic validation
/// pass.
///
/// <para>Chunk 73 is foundation only — the specialised code
/// generation that exploits these modes is a follow-up. These tests
/// pin the parse / store / validate contract.</para>
/// </summary>
public class Chunk73Tests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    [Fact]
    public void Mode_ParsesBasicIndicators()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- mode classify(+, -, ?).\n" +
            "classify(_, out, _).\n");
        var decl = Assert.Single(engine.Modes.ModesFor(Fid("classify", 3)));
        Assert.Equal(
            new[] { ModeIndicator.Input, ModeIndicator.Output, ModeIndicator.Either },
            decl.ArgModes);
        // No `is ...` annotation → NoneDeclared, effective Nondet.
        Assert.Equal(Determinism.NoneDeclared, decl.Determinism);
        Assert.Equal(Determinism.Nondet, decl.EffectiveDeterminism);
    }

    [Fact]
    public void Mode_ParsesDeterminismAnnotation()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- mode add(+, +, -) is det.\n" +
            "add(X, Y, Z) :- Z is X + Y.\n");
        var decl = Assert.Single(engine.Modes.ModesFor(Fid("add", 3)));
        Assert.Equal(Determinism.Det, decl.Determinism);
        Assert.True(decl.IsDeterministic);
    }

    [Theory]
    [InlineData("det", Determinism.Det, true)]
    [InlineData("semidet", Determinism.Semidet, true)]
    [InlineData("multi", Determinism.Multi, false)]
    [InlineData("nondet", Determinism.Nondet, false)]
    public void Mode_ParsesEachDeterminismCategory(
        string keyword, Determinism expected, bool isDeterministic)
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            $":- mode p(+) is {keyword}.\n" +
            "p(_).\n");
        var decl = Assert.Single(engine.Modes.ModesFor(Fid("p", 1)));
        Assert.Equal(expected, decl.Determinism);
        Assert.Equal(isDeterministic, decl.IsDeterministic);
    }

    [Fact]
    public void Mode_MultipleDeclarationsPerPredicate()
    {
        // ADR-012's append/3 example — three callable modes.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- mode append(+, +, -) is det.\n" +
            ":- mode append(+, -, +) is semidet.\n" +
            ":- mode append(-, -, +) is nondet.\n" +
            "append([], L, L).\n" +
            "append([H|T], L, [H|R]) :- append(T, L, R).\n");
        var modes = engine.Modes.ModesFor(Fid("append", 3));
        Assert.Equal(3, modes.Count);
        Assert.Equal(Determinism.Det, modes[0].Determinism);
        Assert.Equal(Determinism.Semidet, modes[1].Determinism);
        Assert.Equal(Determinism.Nondet, modes[2].Determinism);
    }

    [Fact]
    public void Mode_HasDeterministicMode()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- mode lookup(+, -) is semidet.\n" +
            ":- mode lookup(-, -) is nondet.\n" +
            "lookup(k, v).\n");
        // One semidet mode → the predicate has a deterministic mode.
        Assert.True(engine.Modes.HasDeterministicMode(Fid("lookup", 2)));
    }

    [Fact]
    public void Mode_NoDeterministicMode_WhenAllNondet()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- mode gen(-) is multi.\n" +
            "gen(a).\ngen(b).\n");
        Assert.False(engine.Modes.HasDeterministicMode(Fid("gen", 1)));
    }

    [Fact]
    public void Mode_MalformedIndicator_Throws()
    {
        var engine = new PrologEngine();
        var ex = Assert.ThrowsAny<Exception>(() =>
            engine.ConsultString(":- mode bad(*).\nbad(_).\n"));
        Assert.Contains("mode", ex.Message);
    }

    [Fact]
    public void Mode_MalformedDeterminism_Throws()
    {
        var engine = new PrologEngine();
        var ex = Assert.ThrowsAny<Exception>(() =>
            engine.ConsultString(":- mode p(+) is fast.\np(_).\n"));
        Assert.Contains("determinism", ex.Message);
    }

    [Fact]
    public void Validate_WarnsOnModeForUndefinedPredicate()
    {
        var engine = new PrologEngine();
        // ghost/2 has a mode declaration but no clauses.
        engine.ConsultString(
            ":- mode ghost(+, -) is det.\n" +
            ":- mode real(+) is det.\n" +
            "real(x).\n");
        var issues = engine.Modes.Validate(engine.DefinedFunctors());
        var issue = Assert.Single(issues);
        Assert.Equal(Fid("ghost", 2), issue.FunctorId);
        Assert.Equal(ModeValidationSeverity.Warning, issue.Severity);
        Assert.Contains("no clauses", issue.Message);
    }

    [Fact]
    public void Validate_WarnsOnConflictingDeterminism()
    {
        var engine = new PrologEngine();
        // Same arg pattern (+, -), different determinism.
        engine.ConsultString(
            ":- mode q(+, -) is det.\n" +
            ":- mode q(+, -) is semidet.\n" +
            "q(a, b).\n");
        var issues = engine.Modes.Validate(engine.DefinedFunctors());
        Assert.Contains(issues,
            i => i.Message.Contains("different determinism"));
    }

    [Fact]
    public void Validate_CleanProgram_NoIssues()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- mode add(+, +, -) is det.\n" +
            "add(X, Y, Z) :- Z is X + Y.\n");
        var issues = engine.Modes.Validate(engine.DefinedFunctors());
        Assert.Empty(issues);
    }

    [Fact]
    public void Mode_NoDeclaration_EmptyModesFor()
    {
        var engine = new PrologEngine();
        engine.ConsultString("plain(x).\n");
        Assert.Empty(engine.Modes.ModesFor(Fid("plain", 1)));
        Assert.False(engine.Modes.HasModes(Fid("plain", 1)));
    }

    [Fact]
    public void Mode_DynamicPredicate_ValidatesCleanly()
    {
        // A :- dynamic predicate counts as "defined" for validation
        // even before anything is asserted.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- dynamic counter/1.\n" +
            ":- mode counter(?) is nondet.\n");
        var issues = engine.Modes.Validate(engine.DefinedFunctors());
        Assert.Empty(issues);
    }

    [Fact]
    public void Mode_DirectivesStillDontBreakQueries()
    {
        // The chunk-28 guarantee holds: mode directives are metadata,
        // the program still computes.
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- mode sum(+, +, -) is det.\n" +
            "sum(X, Y, Z) :- Z is X + Y.\n");
        Assert.Equal(
            new Shumway.Compiler.Ast.IntTerm(7),
            engine.Query("sum(3, 4, R).")["R"]);
    }
}
