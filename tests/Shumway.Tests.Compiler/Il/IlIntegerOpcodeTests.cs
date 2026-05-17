using Shumway.Compiler.Il;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Compiler.Il;

/// <summary>
/// Chunk 27 coverage (Part B): the IL compiler grows
/// <c>get_integer</c> alongside the existing <c>get_atom</c>, lifting
/// the supported subset to single-clause facts whose head args mix atoms
/// and integers.
/// </summary>
public class IlIntegerOpcodeTests
{
    private static CompiledPredicate CompileFromSource(string source)
    {
        var clauses = new ClauseReader(source).ReadAll().ToList();
        return new PredicateCompiler().Compile(clauses);
    }

    [Fact]
    public void CanCompile_IntegerFact_True()
    {
        var pred = CompileFromSource("answer(42).");
        Assert.True(new IlPredicateCompiler().CanCompile(pred));
    }

    [Fact]
    public void CanCompile_MixedAtomAndIntegerFact_True()
    {
        var pred = CompileFromSource("entry(name, 7).");
        Assert.True(new IlPredicateCompiler().CanCompile(pred));
    }

    [Fact]
    public void Compile_IntegerFact_MatchesEqual()
    {
        var pred = CompileFromSource("answer(42).");
        var del = new IlPredicateCompiler().Compile(pred);

        var engine = new Engine();
        engine.SetRegister(0, Cell.Int(42));
        Assert.True(del(engine));

        var engine2 = new Engine();
        engine2.SetRegister(0, Cell.Int(99));
        Assert.False(del(engine2));
    }

    [Fact]
    public void Compile_IntegerFact_BindsUnboundCaller()
    {
        var pred = CompileFromSource("answer(42).");
        var del = new IlPredicateCompiler().Compile(pred);

        var engine = new Engine();
        int h = engine.AllocateHeapUnbound();
        engine.SetRegister(0, Cell.Ref(h));

        Assert.True(del(engine));
        Cell bound = engine.GetHeap(engine.Deref(h));
        Assert.Equal(Cell.Int(42), bound);
    }

    [Fact]
    public void Compile_MixedAtomIntegerFact_BothMustMatch()
    {
        int nameId = AtomTable.Intern("name", permanent: true).Id;
        int otherId = AtomTable.Intern("other", permanent: true).Id;

        var pred = CompileFromSource("entry(name, 7).");
        var del = new IlPredicateCompiler().Compile(pred);

        // Both match.
        var engine1 = new Engine();
        engine1.SetRegister(0, Cell.Atom(nameId));
        engine1.SetRegister(1, Cell.Int(7));
        Assert.True(del(engine1));

        // Atom mismatch.
        var engine2 = new Engine();
        engine2.SetRegister(0, Cell.Atom(otherId));
        engine2.SetRegister(1, Cell.Int(7));
        Assert.False(del(engine2));

        // Integer mismatch.
        var engine3 = new Engine();
        engine3.SetRegister(0, Cell.Atom(nameId));
        engine3.SetRegister(1, Cell.Int(99));
        Assert.False(del(engine3));
    }
}
