using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>ADR-063: the code of a fact asserted at run time comes from the
/// image of its head. Atoms, integers the operand holds and variables match
/// in place; any other argument has the whole head copied from the image,
/// interned in the pool, and unified argument by argument.</summary>
public sealed class FactImageCompilerTests
{
    private static int Fid(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    private static int AtomId(string name) => AtomTable.Intern(name, permanent: true).Id;

    [Fact]
    public void AtomsIntegersAndVariablesMatchInPlace()
    {
        // p(a, 1, X, X)
        var image = new TermImage(new[]
        {
            Cell.Functor(Fid("p", 4)), Cell.Atom(AtomId("a")), Cell.Int(1), Cell.Ref(3), Cell.Ref(3),
        }, Tag.Str);
        var pools = new LiteralPools();
        var cc = FactImageCompiler.Compile(image, Fid("p", 4), pools);

        var rows = Disassemble(cc.Bytecode);
        Assert.Equal(4, rows.Count);
        Expect(rows[0], Opcode.GetAtom, AtomId("a"), 0);
        Expect(rows[1], Opcode.GetInteger, 1, 1);
        Expect(rows[2], Opcode.GetValueX, 2, 3);
        Expect(rows[3], Opcode.Proceed);
        Assert.Equal(4, cc.Arity);
        Assert.Equal(4, cc.RegisterCount);
        Assert.Empty(cc.CallSites);
        Assert.Equal(0, pools.Images.Count);
    }

    [Fact]
    public void ACompoundArgumentMatchesThroughItsStream()
    {
        // p(f(b), c)
        var image = new TermImage(new[]
        {
            Cell.Functor(Fid("p", 2)), Cell.Str(3), Cell.Atom(AtomId("c")),
            Cell.Functor(Fid("f", 1)), Cell.Atom(AtomId("b")),
        }, Tag.Str);
        var pools = new LiteralPools();
        var cc = FactImageCompiler.Compile(image, Fid("p", 2), pools);

        var rows = Disassemble(cc.Bytecode);
        Assert.Equal(4, rows.Count);
        Expect(rows[0], Opcode.GetStructure, Fid("f", 1), 0);
        Expect(rows[1], Opcode.UnifyAtom, AtomId("b"));
        Expect(rows[2], Opcode.GetAtom, AtomId("c"), 1);
        Expect(rows[3], Opcode.Proceed);
        Assert.Equal(2, cc.RegisterCount);
        Assert.Equal(0, pools.Images.Count);
    }

    [Fact]
    public void NestedCompoundsBoxedLiteralsAndSharedVariablesTakeTemporaries()
    {
        // p(g(h(i), 2.5), Y, [Y|[]]): the nested compound and the float come after
        // g's stream through temporaries; Y's first occurrence is register 1.
        var (header, bits) = Cell.MakeFloat(2.5, 12);
        var image = new TermImage(new[]
        {
            Cell.Functor(Fid("p", 3)), Cell.Str(4), Cell.Ref(2), Cell.Lis(9),
            Cell.Functor(Fid("g", 2)), Cell.Str(7), Cell.Ref(11),
            Cell.Functor(Fid("h", 1)), Cell.Atom(AtomId("i")),
            Cell.Ref(2), Cell.Atom(AtomTable.EmptyListId),
            header, bits,
        }, Tag.Str);
        var pools = new LiteralPools();
        var cc = FactImageCompiler.Compile(image, Fid("p", 3), pools);

        Assert.True(pools.Floats.TryGetId(2.5, out int value));
        var rows = Disassemble(cc.Bytecode);
        Assert.Equal(10, rows.Count);
        Expect(rows[0], Opcode.GetStructure, Fid("g", 2), 0);
        Expect(rows[1], Opcode.UnifyVariableX, 3);
        Expect(rows[2], Opcode.UnifyVariableX, 4);
        Expect(rows[3], Opcode.GetStructure, Fid("h", 1), 3);
        Expect(rows[4], Opcode.UnifyAtom, AtomId("i"));
        Expect(rows[5], Opcode.GetFloat, value, 4);
        Expect(rows[6], Opcode.GetList, 2);
        Expect(rows[7], Opcode.UnifyValueX, 1);
        Expect(rows[8], Opcode.UnifyNil);
        Expect(rows[9], Opcode.Proceed);
        Assert.Equal(5, cc.RegisterCount);
        Assert.Equal(0, pools.Images.Count);
    }

    [Fact]
    public void ClosedTextAndAFloatMatchInPlaceThroughTheirPools()
    {
        // p("ab", 2.5), the text in two segments, as a concatenation leaves it.
        var (header, bits) = Cell.MakeFloat(2.5, 10);
        var image = new TermImage(new[]
        {
            Cell.Functor(Fid("p", 2)), Cell.Ref(3), Cell.Ref(9),
            Cell.Pstr(1, 4, 0, TextKind.Chars, false), Cell.PstrBuffer('a', 0, 0), Cell.Ref(6),
            Cell.Pstr(1, 7, 0, TextKind.Chars, false), Cell.PstrBuffer('b', 0, 0), Cell.Atom(AtomTable.EmptyListId),
            header, bits,
        }, Tag.Str);
        var pools = new LiteralPools();
        var cc = FactImageCompiler.Compile(image, Fid("p", 2), pools);

        Assert.True(pools.Strings.TryGetId(new TextLiteral("ab", TextKind.Chars), out int text));
        Assert.True(pools.Floats.TryGetId(2.5, out int value));
        var rows = Disassemble(cc.Bytecode);
        Assert.Equal(3, rows.Count);
        Expect(rows[0], Opcode.GetPstr, text, 0);
        Expect(rows[1], Opcode.GetFloat, value, 1);
        Expect(rows[2], Opcode.Proceed);
        Assert.Equal(0, pools.Images.Count);

        // Text that ends in a variable is no literal.
        var open = new TermImage(new[]
        {
            Cell.Functor(Fid("p", 1)), Cell.Ref(2),
            Cell.Pstr(1, 3, 0, TextKind.Chars, false), Cell.PstrBuffer('a', 0, 0), Cell.Ref(4),
        }, Tag.Str);
        Assert.Equal(Opcode.PutBlit, Disassemble(FactImageCompiler.Compile(open, Fid("p", 1), pools).Bytecode)[0].Opcode);
    }

    [Fact]
    public void AnIntegerPastTheOperandTakesTheWholeHeadFromThePool()
    {
        var wide = new TermImage(new[] { Cell.Functor(Fid("n", 1)), Cell.Int(5_000_000_000L) }, Tag.Str);
        var pools = new LiteralPools();
        var rows = Disassemble(FactImageCompiler.Compile(wide, Fid("n", 1), pools).Bytecode);
        Assert.True(pools.Images.TryGetId(wide, out int id));
        Assert.Equal(4, rows.Count);
        Expect(rows[0], Opcode.PutBlit, id, 1);
        Expect(rows[1], Opcode.GetStructure, Fid("n", 1), 1);
        Expect(rows[2], Opcode.UnifyValueX, 0);
        Expect(rows[3], Opcode.Proceed);
    }

    [Fact]
    public void AVariableFirstSeenInsideACompoundIsReadIntoATemporary()
    {
        // p(f(X), X)
        var nested = new TermImage(new[]
        {
            Cell.Functor(Fid("p", 2)), Cell.Str(3), Cell.Ref(4), Cell.Functor(Fid("f", 1)), Cell.Ref(4),
        }, Tag.Str);
        var rows = Disassemble(FactImageCompiler.Compile(nested, Fid("p", 2), new LiteralPools()).Bytecode);
        Assert.Equal(4, rows.Count);
        Expect(rows[0], Opcode.GetStructure, Fid("f", 1), 0);
        Expect(rows[1], Opcode.UnifyVariableX, 2);
        Expect(rows[2], Opcode.GetValueX, 2, 1);
        Expect(rows[3], Opcode.Proceed);
    }

    private record struct Row(Opcode Opcode, int[] Operands);

    private static void Expect(Row row, Opcode opcode, params int[] operands)
    {
        Assert.Equal(opcode, row.Opcode);
        Assert.Equal(operands, row.Operands);
    }

    private static List<Row> Disassemble(byte[] code)
    {
        var rows = new List<Row>();
        for (int pc = 0; pc < code.Length;)
        {
            var op = (Opcode)code[pc];
            var info = OpcodeTable.Get(op);
            var operands = new List<int>();
            int at = pc + 1;
            if (info.OperandKinds is { } kinds)
                foreach (var kind in kinds)
                {
                    operands.Add(BytecodeIO.ReadInt32(code, at));
                    at += kind == OperandKind.LongValue ? 8 : 4;
                }
            rows.Add(new Row(op, operands.ToArray()));
            pc += info.Size;
        }
        return rows;
    }
}
