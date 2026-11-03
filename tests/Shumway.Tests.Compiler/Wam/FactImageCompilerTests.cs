using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>ADR-063: the code of a fact asserted at run time comes from the
/// image of its head. Atoms, integers the operand holds and variables match
/// in place; a compound through its stream, with an image of its own before
/// it from the head threshold's cells; a head the streams cannot express has
/// the whole head copied from the image, interned in the pool, and unified
/// argument by argument.</summary>
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
    public void AVariableThatIsAPlainArgumentIsMatchedFromItsRegisterInsideACompound()
    {
        // p(f(X), X): the call's second argument is X wherever X sits.
        var nested = new TermImage(new[]
        {
            Cell.Functor(Fid("p", 2)), Cell.Str(3), Cell.Ref(4), Cell.Functor(Fid("f", 1)), Cell.Ref(4),
        }, Tag.Str);
        var rows = Disassemble(FactImageCompiler.Compile(nested, Fid("p", 2), new LiteralPools()).Bytecode);
        Assert.Equal(3, rows.Count);
        Expect(rows[0], Opcode.GetStructure, Fid("f", 1), 0);
        Expect(rows[1], Opcode.UnifyValueX, 1);
        Expect(rows[2], Opcode.Proceed);
    }

    [Fact]
    public void AVariableFirstSeenInsideACompoundIsReadIntoATemporary()
    {
        // p(f(X), g(X))
        var nested = new TermImage(new[]
        {
            Cell.Functor(Fid("p", 2)), Cell.Str(3), Cell.Str(5),
            Cell.Functor(Fid("f", 1)), Cell.Ref(4), Cell.Functor(Fid("g", 1)), Cell.Ref(4),
        }, Tag.Str);
        var rows = Disassemble(FactImageCompiler.Compile(nested, Fid("p", 2), new LiteralPools()).Bytecode);
        Assert.Equal(5, rows.Count);
        Expect(rows[0], Opcode.GetStructure, Fid("f", 1), 0);
        Expect(rows[1], Opcode.UnifyVariableX, 2);
        Expect(rows[2], Opcode.GetStructure, Fid("g", 1), 1);
        Expect(rows[3], Opcode.UnifyValueX, 2);
        Expect(rows[4], Opcode.Proceed);
    }

    // The head threshold is the body's, 8 cells, unless the environment says
    // otherwise: these heads are built at it.
    private static int Threshold => ClauseCompiler.HeadThreshold;

    [Fact]
    public void ACompoundArgumentAtTheThresholdGetsItsImageBeforeItsStream()
    {
        Assert.Equal(8, Threshold);
        // p(f(a, b, c, d, e, g, h), x): f/7 is eight cells.
        var atoms = new[] { "a", "b", "c", "d", "e", "g", "h" };
        var cells = new List<Cell> { Cell.Functor(Fid("p", 2)), Cell.Str(3), Cell.Atom(AtomId("x")), Cell.Functor(Fid("f", 7)) };
        foreach (string a in atoms) cells.Add(Cell.Atom(AtomId(a)));
        var pools = new LiteralPools();
        var rows = Disassemble(FactImageCompiler.Compile(new TermImage(cells.ToArray(), Tag.Str), Fid("p", 2), pools).Bytecode);
        var argument = new TermImage(cells.GetRange(3, 8).ToArray(), Tag.Str);
        Assert.True(pools.Images.TryGetId(argument, out int id), "the argument's image is not in the pool");
        Assert.Equal(11, rows.Count);
        int streamEnd = OpcodeTable.Get(Opcode.GetBlit).Size + OpcodeTable.Get(Opcode.GetStructure).Size
            + 7 * OpcodeTable.Get(Opcode.UnifyAtom).Size;
        Expect(rows[0], Opcode.GetBlit, id, 0, streamEnd);
        Expect(rows[1], Opcode.GetStructure, Fid("f", 7), 0);
        for (int i = 0; i < 7; i++) Expect(rows[2 + i], Opcode.UnifyAtom, AtomId(atoms[i]));
        Expect(rows[9], Opcode.GetAtom, AtomId("x"), 1);
        Expect(rows[10], Opcode.Proceed);
        // One cell short of the threshold: the stream alone.
        var seven = new List<Cell> { Cell.Functor(Fid("p", 2)), Cell.Str(3), Cell.Atom(AtomId("x")), Cell.Functor(Fid("f", 6)) };
        for (int i = 0; i < 6; i++) seven.Add(Cell.Atom(AtomId(atoms[i])));
        rows = Disassemble(FactImageCompiler.Compile(new TermImage(seven.ToArray(), Tag.Str), Fid("p", 2), pools).Bytecode);
        Assert.Equal(Opcode.GetStructure, rows[0].Opcode);
    }

    [Fact]
    public void TheArgumentsImageHasAHoleForEachVariableReadOrWrittenAsTheStreamWould()
    {
        // p(X, f(X, W, W, a, b, c, d), Y): X comes from the call's first
        // argument, W is this stream's own temporary, Y is no part of it.
        var cells = new[]
        {
            Cell.Functor(Fid("p", 3)), Cell.Ref(1), Cell.Str(4), Cell.Ref(3),
            Cell.Functor(Fid("f", 7)), Cell.Ref(1), Cell.Ref(6), Cell.Ref(6),
            Cell.Atom(AtomId("a")), Cell.Atom(AtomId("b")), Cell.Atom(AtomId("c")), Cell.Atom(AtomId("d")),
        };
        var pools = new LiteralPools();
        var rows = Disassemble(FactImageCompiler.Compile(new TermImage(cells, Tag.Str), Fid("p", 3), pools).Bytecode);
        var argument = new TermImage(new[]
            {
                Cell.Functor(Fid("f", 7)), Cell.Ref(1), Cell.Ref(2), Cell.Ref(2),
                Cell.Atom(AtomId("a")), Cell.Atom(AtomId("b")), Cell.Atom(AtomId("c")), Cell.Atom(AtomId("d")),
            }, Tag.Str,
            new[] { new ImageHole(1, ImageHole.In, 0), new ImageHole(2, ImageHole.Out, 3) });
        Assert.True(pools.Images.TryGetId(argument, out int id), "the argument's image, holes included, is not in the pool");
        Assert.Equal(10, rows.Count);
        int streamEnd = OpcodeTable.Get(Opcode.GetBlit).Size + OpcodeTable.Get(Opcode.GetStructure).Size
            + 2 * OpcodeTable.Get(Opcode.UnifyValueX).Size + OpcodeTable.Get(Opcode.UnifyVariableX).Size
            + 4 * OpcodeTable.Get(Opcode.UnifyAtom).Size;
        Expect(rows[0], Opcode.GetBlit, id, 1, streamEnd);
        Expect(rows[1], Opcode.GetStructure, Fid("f", 7), 1);
        Expect(rows[2], Opcode.UnifyValueX, 0);
        Expect(rows[3], Opcode.UnifyVariableX, 3);
        Expect(rows[4], Opcode.UnifyValueX, 3);
        Expect(rows[9], Opcode.Proceed);
    }

    [Fact]
    public void AnArgumentThatNamesABlockLaidElsewhereKeepsItsStream()
    {
        // p(f(a, b, c, d, e, g, h), g(f(...), b, c, d, e, g, h)) with one f
        // block for both: the second argument is eight cells of its own, but
        // its first cell refers into the first argument's run.
        var atoms = new[] { "a", "b", "c", "d", "e", "g", "h" };
        var cells = new List<Cell> { Cell.Functor(Fid("p", 2)), Cell.Str(3), Cell.Str(11), Cell.Functor(Fid("f", 7)) };
        foreach (string a in atoms) cells.Add(Cell.Atom(AtomId(a)));
        cells.Add(Cell.Functor(Fid("g", 7)));
        cells.Add(Cell.Str(3));
        for (int i = 1; i < 7; i++) cells.Add(Cell.Atom(AtomId(atoms[i])));
        var pools = new LiteralPools();
        var rows = Disassemble(FactImageCompiler.Compile(new TermImage(cells.ToArray(), Tag.Str), Fid("p", 2), pools).Bytecode);
        Assert.Equal(Opcode.GetBlit, rows[0].Opcode);
        Assert.Equal(1, pools.Images.Count);
        // The second argument: get_structure g/7 straight away, after the
        // first's prefix and stream.
        Expect(rows[9], Opcode.GetStructure, Fid("g", 7), 1);
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
