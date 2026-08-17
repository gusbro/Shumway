using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>
/// ADR-028 — sibling-argument and structure-keyed indexing INSIDE a value bucket.
/// Where ADR-027 sub-indexes a list/struct value, ADR-028 replaces any
/// ≥ 2-clause value bucket's linear chain with a nested switch on a sibling
/// argument (reusing <c>switch_on_{atom,integer,structure}_arg</c>) or on a
/// structure-functor sub-key (the new <c>switch_on_structure_sub</c>). These
/// tests check the nested opcode is emitted (or correctly NOT emitted);
/// end-to-end correctness / determinism / the ADR-027 unbound-default soundness
/// fix live in Shumway.Tests.Embedding.
/// </summary>
public class BucketIndexingTests
{
    private static CompiledPredicate Compile(string source) =>
        new PredicateCompiler().Compile(new ClauseReader(source).ReadAll().ToList());

    private static int Count(byte[] code, Opcode target)
    {
        int count = 0, pc = 0;
        while (pc < code.Length)
        {
            if ((Opcode)code[pc] == target) count++;
            var info = OpcodeTable.Get(code[pc]);
            if (info.Size == 0) break;
            pc += info.Size;
        }
        return count;
    }

    private static bool Has(byte[] code, Opcode op) => Count(code, op) > 0;

    [Fact]
    public void GroundAtomBucket_DiscriminableBySibling_NestsSwitchOnAtomArg()
    {
        // The arg0='a' bucket has 3 clauses distinguished by arg1 (x/y/z). Before
        // ADR-028 that bucket was a linear try/retry/trust; now it nests a
        // switch_on_atom_arg on arg1. There are TWO: one on the var-arg0 cascade
        // path (arg0 unbound) and one nested inside the 'a' value bucket.
        var cp = Compile("""
            h(a,x,1).
            h(a,y,2).
            h(a,z,3).
            h(b,w,9).
            """);
        Assert.True(Has(cp.Bytecode, Opcode.SwitchOnAtom));       // arg0 value table
        Assert.True(Count(cp.Bytecode, Opcode.SwitchOnAtomArg) >= 2);
    }

    [Fact]
    public void TwoClauseBucket_NotNested()
    {
        // A 2-clause bucket ends in `trust` (no leftover choice point): ADR-028
        // gates the atom/int sibling nesting at ≥ 3, so only the var-path cascade
        // switch_on_atom_arg is emitted, not a nested one.
        var cp = Compile("""
            foo(a,x):-one.
            foo(a,y):-two.
            foo(b,x):-three.
            foo(b,y):-four.
            """);
        Assert.Equal(1, Count(cp.Bytecode, Opcode.SwitchOnAtomArg));
    }

    [Fact]
    public void ListHeadFunctors_EmitStructureSub()
    {
        // arg0 is a list in every clause; the heads are distinct FUNCTORS
        // (parse/1, amp/1, lit/1) — not atoms/ints — so the new structure-keyed
        // sub fires (ADR-027 atom/int sub cannot key a functor).
        var cp = Compile("""
            r([parse(X)|T],T):- !.
            r([amp(X)|T],T):- !.
            r([lit(X)|T],T):- !.
            r([V|T],[V|T]).
            """);
        Assert.True(Has(cp.Bytecode, Opcode.SwitchOnStructureSub));
    }

    [Fact]
    public void StructSiblingFunctors_NestSwitchOnStructureArg()
    {
        // arg0='k' bucket (3 clauses) distinguished by the FUNCTOR of arg1.
        var cp = Compile("""
            s(k,f(1),a).
            s(k,g(2),b).
            s(k,h(3),c).
            s(m,z,d).
            """);
        Assert.True(Count(cp.Bytecode, Opcode.SwitchOnStructureArg) >= 1);
    }

    [Fact]
    public void NoDiscriminator_KeepsPlainChain()
    {
        // The arg0 struct t/1 bucket's clauses share every argument's shape (t's
        // sub-arg is a var; arg1 is the same integer in all): nothing partitions
        // it, so no nested structure-sub fires — the bucket stays a linear chain.
        // (A switch_on_integer_arg still appears on the var-arg0 cascade path;
        // that is the pre-existing multi-arg index, not an ADR-028 nested switch.)
        var cp = Compile("""
            u(t(A),1).
            u(t(B),1).
            u(t(C),1).
            """);
        Assert.False(Has(cp.Bytecode, Opcode.SwitchOnStructureSub));
        // Three clause chain entries remain (the bucket was not collapsed).
        Assert.True(Count(cp.Bytecode, Opcode.Trust) >= 1);
    }
}
