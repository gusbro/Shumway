using Shumway.Compiler.Ast;
using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// ISO 13211-1, §8.16 Atomic term processing: <c>atom_length/2</c>,
/// <c>atom_chars/2</c>, <c>atom_codes/2</c>, <c>atom_concat/3</c>,
/// <c>char_code/2</c>, <c>number_chars/2</c>, <c>number_codes/2</c>,
/// <c>sub_atom/5</c>.
/// </summary>
public class AtomsAndStringsConformance
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    [Fact]
    public void AtomLength_ReturnsCodeUnitCount()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(5), engine.Query("atom_length(hello, N).")["N"]);
        Assert.Equal(Int(0), engine.Query("atom_length('', N).")["N"]);
    }

    [Fact]
    public void AtomChars_Bidirectional()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("atom_chars(cat, Cs).");
        // Cs = [c, a, t]
        Assert.True(sol.Success);
        // Reverse direction.
        var sol2 = engine.Query("atom_chars(A, [d, o, g]).");
        Assert.Equal(Atom("dog"), sol2["A"]);
    }

    [Fact]
    public void AtomCodes_Bidirectional()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("atom_codes(A, [104, 105]).");
        Assert.Equal(Atom("hi"), sol["A"]);
    }

    [Fact]
    public void AtomConcat_JoinsTwoAtoms()
    {
        var engine = new PrologEngine();
        Assert.Equal(Atom("foobar"), engine.Query("atom_concat(foo, bar, X).")["X"]);
    }

    [Fact]
    public void CharCode_Bidirectional()
    {
        var engine = new PrologEngine();
        Assert.Equal(Int(65), engine.Query("char_code('A', X).")["X"]);
        Assert.Equal(Atom("A"), engine.Query("char_code(C, 65).")["C"]);
    }

    [Fact]
    public void NumberChars_BothDirections()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("number_chars(N, ['4', '2']).");
        Assert.Equal(Int(42), sol["N"]);
        var sol2 = engine.Query("number_chars(7, Cs).");
        Assert.True(sol2.Success);
    }

    [Fact]
    public void NumberChars_ParsesTheListWhenBothArgumentsAreBound()
    {
        // ISO §8.16.8: the char list is authoritative when instantiated, so
        // number_chars(1, ['0','1']) parses "01"→1 and succeeds (it must NOT
        // generate "1" from the 1 and fail the compare).
        var engine = new PrologEngine();
        Assert.True(engine.Query("number_chars(1, ['0','1']).").Success);
        Assert.True(engine.Query("number_chars(10, ['0','1','0']).").Success);
    }

    [Fact]
    public void NumberChars_GeneratesWhenListElementsAreUnbound()
    {
        // A list of unbound elements is the generate direction: fails on a
        // length mismatch rather than raising instantiation_error.
        var engine = new PrologEngine();
        Assert.False(engine.Query("number_chars(1, [_C, _D]).").Success);
    }

    [Fact]
    public void NumberChars_BoundNonCharElement_IsTypeError()
    {
        // A bound element that is not a character wins over an earlier unbound
        // one (§8.16.8): number_chars(1, [_, []]) → type_error(character, []).
        var engine = new PrologEngine();
        Assert.True(engine.Query(
            "catch(number_chars(1, [_, []]), error(type_error(character, _), _), true).").Success);
    }

    [Fact]
    public void NumberChars_RejectsNonIsoNumberSyntax()
    {
        var engine = new PrologEngine();
        // A float needs a fractional part: 1e1 is not a valid float.
        Assert.True(engine.Query(
            "catch(number_chars(_, ['1','e','1']), error(syntax_error(_),_), true).").Success);
        // No leading '+'.
        Assert.True(engine.Query(
            "catch(number_chars(_, ['+','1']), error(syntax_error(_),_), true).").Success);
        // …but the well-formed float and a leading comment parse.
        Assert.True(engine.Query("number_chars(N, ['1','.','0','e','1']), N =:= 10.0.").Success);
        Assert.True(engine.Query(
            "number_chars(N, ['/','*','x','*','/','1']), N =:= 1.").Success);
    }

    [Fact]
    public void SubAtom_AllDecompositions()
    {
        // Chunk 43 made sub_atom/5 multi-solution. For "ab" we get
        // (n+1)(n+2)/2 = 6 decompositions.
        var engine = new PrologEngine();
        Assert.Equal(6, engine.QueryAll("sub_atom(ab, _, _, _, _).").Count());
    }

    [Fact]
    public void SubAtom_FindSubstring()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("sub_atom(banana, 1, 3, _, S).");
        Assert.Equal(Atom("ana"), sol["S"]);
    }

    [Fact]
    public void UpcaseDowncase()
    {
        var engine = new PrologEngine();
        Assert.Equal(Atom("HELLO"), engine.Query("upcase_atom(hello, X).")["X"]);
        Assert.Equal(Atom("hello"), engine.Query("downcase_atom('HELLO', X).")["X"]);
    }
}
