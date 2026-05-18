using Shumway.Compiler.Il;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Compiler.Il;

/// <summary>
/// Chunk 29: the IL compiler grows two more head-matching opcodes â€”
/// <c>get_nil</c> and <c>get_value_x</c> â€” extending the supported MVP
/// subset to single-clause facts with a nil literal arg and to
/// facts with shared variables in the head (the <c>p(X, X)</c>
/// pattern). Cross-validated against Tier 0 results.
/// </summary>
public class IlExtendedOpcodeTests
{
    private static CompiledPredicate CompileFromSource(string source)
    {
        var clauses = new ClauseReader(source).ReadAll().ToList();
        return new PredicateCompiler().Compile(clauses);
    }

    // ---------- CanCompile gate ----------

    [Fact]
    public void CanCompile_NilArgFact_True()
    {
        var pred = CompileFromSource("empty([]).");
        Assert.True(new IlPredicateCompiler().CanCompile(pred));
    }

    [Fact]
    public void CanCompile_SharedVarFact_True()
    {
        // p(X, X). compiles to get_value_x X[0], X[1].
        var pred = CompileFromSource("p(X, X).");
        Assert.True(new IlPredicateCompiler().CanCompile(pred));
    }

    // ---------- get_nil ----------

    [Fact]
    public void Compile_NilFact_MatchesEmptyList()
    {
        var pred = CompileFromSource("empty([]).");
        var del = new IlPredicateCompiler().Compile(pred);

        // Caller passes [] â€” match.
        var engine1 = new Engine();
        engine1.SetRegister(0, Cell.Atom(AtomTable.EmptyListId));
        Assert.True(del(engine1, 0));

        // Caller passes any other atom â€” fail.
        int otherId = AtomTable.Intern("nope", permanent: true).Id;
        var engine2 = new Engine();
        engine2.SetRegister(0, Cell.Atom(otherId));
        Assert.False(del(engine2, 0));
    }

    // ---------- get_value_x ----------

    [Fact]
    public void Compile_SharedVarFact_RequiresArgsToUnify()
    {
        // p(X, X). The compiled IL claims X[0] for X then emits
        // get_value_x X[0], X[1] to check the two args unify.
        int aId = AtomTable.Intern("a", permanent: true).Id;
        int bId = AtomTable.Intern("b", permanent: true).Id;

        var pred = CompileFromSource("p(X, X).");
        var del = new IlPredicateCompiler().Compile(pred);

        // Same atom in both slots â†’ match.
        var engine1 = new Engine();
        engine1.SetRegister(0, Cell.Atom(aId));
        engine1.SetRegister(1, Cell.Atom(aId));
        Assert.True(del(engine1, 0));

        // Different atoms â†’ no match.
        var engine2 = new Engine();
        engine2.SetRegister(0, Cell.Atom(aId));
        engine2.SetRegister(1, Cell.Atom(bId));
        Assert.False(del(engine2, 0));
    }

    [Fact]
    public void Compile_SharedVarFact_BindsTwoUnbounds()
    {
        // Both args unbound; they should become bound to each other.
        var pred = CompileFromSource("p(X, X).");
        var del = new IlPredicateCompiler().Compile(pred);

        var engine = new Engine();
        int h0 = engine.AllocateHeapUnbound();
        int h1 = engine.AllocateHeapUnbound();
        engine.SetRegister(0, Cell.Ref(h0));
        engine.SetRegister(1, Cell.Ref(h1));

        Assert.True(del(engine, 0));
        // After unification, both heap cells deref to the same target.
        Assert.Equal(engine.Deref(h0), engine.Deref(h1));
    }

    // ---------- Combined ----------

    [Fact]
    public void Compile_AtomNilFact_BothMustMatch()
    {
        int markerId = AtomTable.Intern("end", permanent: true).Id;
        var pred = CompileFromSource("p(end, []).");
        var del = new IlPredicateCompiler().Compile(pred);

        var engine1 = new Engine();
        engine1.SetRegister(0, Cell.Atom(markerId));
        engine1.SetRegister(1, Cell.Atom(AtomTable.EmptyListId));
        Assert.True(del(engine1, 0));

        // [] in arg 0 doesn't match atom 'end'.
        var engine2 = new Engine();
        engine2.SetRegister(0, Cell.Atom(AtomTable.EmptyListId));
        engine2.SetRegister(1, Cell.Atom(AtomTable.EmptyListId));
        Assert.False(del(engine2, 0));
    }

    // ---------- Tier1Promoter convenience surface ----------

    [Fact]
    public void Tier1Promoter_AcceptsSupportedPredicate()
    {
        var del = Tier1Promoter.TryCompile("foo(bar).");
        Assert.NotNull(del);

        int barId = AtomTable.Intern("bar", permanent: true).Id;
        var engine = new Engine();
        engine.SetRegister(0, Cell.Atom(barId));
        Assert.True(del!(engine, 0));
    }

    [Fact]
    public void Tier1Promoter_RejectsUnsupportedPredicate_ReturnsNull()
    {
        // Tier1Promoter.TryCompile invokes CanCompile without a callee
        // map — non-tail Call opcodes need the map to verify the
        // leaf-callee restriction (chunk 50), so multi-clause bodies
        // that include a Call still surface as null here.
        Assert.Null(Tier1Promoter.TryCompile("p(X) :- q(X), r(X).\np(X) :- s(X).\n"));
    }
}
