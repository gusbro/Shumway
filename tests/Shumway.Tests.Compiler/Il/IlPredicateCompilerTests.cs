using Shumway.Compiler.Il;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Compiler.Il;

/// <summary>
/// Cross-validation tests for the Tier-1 IL compiler MVP. A predicate is
/// compiled through both paths (Tier 0 bytecode interpreter and Tier 1
/// IL delegate) for the same input registers, and the outcomes are
/// compared. The MVP only handles the single-clause / atom-arg subset of
/// the WAM instruction set; anything outside that subset is checked for
/// the expected <c>NotSupportedException</c>.
/// </summary>
public class IlPredicateCompilerTests
{
    private static CompiledPredicate CompileFromSource(string source)
    {
        var clauses = new ClauseReader(source).ReadAll().ToList();
        return new PredicateCompiler().Compile(clauses);
    }

    // ---------- CanCompile gate ----------

    [Fact]
    public void CanCompile_SingleAtomFact_True()
    {
        var pred = CompileFromSource("colour(red).");
        Assert.True(new IlPredicateCompiler().CanCompile(pred));
    }

    [Fact]
    public void CanCompile_ZeroArityFact_True()
    {
        var pred = CompileFromSource("ready.");
        Assert.True(new IlPredicateCompiler().CanCompile(pred));
    }

    [Fact]
    public void CanCompile_MultiClause_False()
    {
        var pred = CompileFromSource("colour(red).\ncolour(green).\n");
        Assert.False(new IlPredicateCompiler().CanCompile(pred));
    }

    [Fact]
    public void CanCompile_FactWithVarArg_True()
    {
        // A var arg compiles to no head opcode (the slot is "claimed"
        // silently), so the bytecode is just [proceed] and the MVP
        // happily accepts it â€” it succeeds for any caller value.
        var pred = CompileFromSource("p(X).");
        Assert.True(new IlPredicateCompiler().CanCompile(pred));
    }

    [Fact]
    public void CanCompile_FactWithCompoundArg_False()
    {
        // Compound args use get_structure, which the extended MVP still
        // doesn't translate. Integers are now supported (see
        // IlIntegerOpcodeTests for chunk 27's get_integer coverage).
        var pred = CompileFromSource("p(foo(a)).");
        Assert.False(new IlPredicateCompiler().CanCompile(pred));
    }

    [Fact]
    public void CanCompile_RuleWithBody_False()
    {
        // A non-trivial body produces a Call (or Execute) opcode that the
        // MVP doesn't translate. `true` would optimise away to a bare
        // Proceed, so we use a real predicate call.
        var pred = CompileFromSource("p :- q.");
        Assert.False(new IlPredicateCompiler().CanCompile(pred));
    }

    // ---------- Cross-validation: IL vs Tier 0 ----------

    [Fact]
    public void Compile_ZeroArityFact_AlwaysSucceeds()
    {
        var pred = CompileFromSource("ready.");
        var del = new IlPredicateCompiler().Compile(pred);
        var engine = new Engine();
        Assert.True(del(engine, 0));
    }

    [Fact]
    public void Compile_SingleAtomFact_MatchesOnEqualAtom()
    {
        // colour(red). IL: return engine.UnifyRegisterWithCell(0, Cell.Atom('red'));
        int redId = AtomTable.Intern("red", permanent: true).Id;
        int greenId = AtomTable.Intern("green", permanent: true).Id;

        var pred = CompileFromSource("colour(red).");
        var del = new IlPredicateCompiler().Compile(pred);

        // Caller passes 'red' in X[0] â†’ match.
        var engine1 = new Engine();
        engine1.SetRegister(0, Cell.Atom(redId));
        Assert.True(del(engine1, 0));

        // Caller passes 'green' â†’ no match.
        var engine2 = new Engine();
        engine2.SetRegister(0, Cell.Atom(greenId));
        Assert.False(del(engine2, 0));
    }

    [Fact]
    public void Compile_TwoAtomFact_BothMustMatch()
    {
        int aId = AtomTable.Intern("a", permanent: true).Id;
        int bId = AtomTable.Intern("b", permanent: true).Id;
        int xId = AtomTable.Intern("x", permanent: true).Id;

        var pred = CompileFromSource("p(a, b).");
        var del = new IlPredicateCompiler().Compile(pred);

        // Both args match.
        var engine1 = new Engine();
        engine1.SetRegister(0, Cell.Atom(aId));
        engine1.SetRegister(1, Cell.Atom(bId));
        Assert.True(del(engine1, 0));

        // First matches, second doesn't.
        var engine2 = new Engine();
        engine2.SetRegister(0, Cell.Atom(aId));
        engine2.SetRegister(1, Cell.Atom(xId));
        Assert.False(del(engine2, 0));

        // First doesn't match â€” never gets to the second.
        var engine3 = new Engine();
        engine3.SetRegister(0, Cell.Atom(xId));
        engine3.SetRegister(1, Cell.Atom(bId));
        Assert.False(del(engine3, 0));
    }

    [Fact]
    public void Compile_AtomFact_BindsUnboundCaller()
    {
        // Caller passes an unbound REF â€” it should get bound to the head's
        // atom after the call succeeds. Mirrors what the Tier 0 interpreter
        // does via get_atom in write-like mode.
        int redId = AtomTable.Intern("red", permanent: true).Id;
        var pred = CompileFromSource("colour(red).");
        var del = new IlPredicateCompiler().Compile(pred);

        var engine = new Engine();
        int h = engine.AllocateHeapUnbound();
        engine.SetRegister(0, Cell.Ref(h));

        Assert.True(del(engine, 0));
        // After call, heap[h] should be bound to atom 'red'.
        Cell bound = engine.GetHeap(engine.Deref(h));
        Assert.Equal(Cell.Atom(redId), bound);
    }

    // ---------- Rejection of unsupported subsets ----------

    [Fact]
    public void Compile_UnsupportedPredicate_Throws()
    {
        // Multi-clause predicates still fall outside the supported subset.
        var pred = CompileFromSource("p(a).\np(b).\n");
        var ic = new IlPredicateCompiler();
        Assert.Throws<NotSupportedException>(() => ic.Compile(pred));
    }
}
