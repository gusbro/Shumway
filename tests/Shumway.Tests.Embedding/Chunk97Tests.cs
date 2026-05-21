using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 97 (Phase 7): the atom/number conversion predicates —
/// <c>atom_number/2</c>, <c>number_string/2</c>,
/// <c>atomic_list_concat/2</c> and <c>/3</c>, and <c>char_type/2</c>.
/// </summary>
public class Chunk97Tests
{
    private static bool Holds(string query) => new PrologEngine().Query(query).Success;

    // ---- atom_number ----

    [Fact]
    public void AtomNumber_ParsesAnAtomIntoANumber()
    {
        Assert.True(Holds("atom_number('42', N), N == 42."));
        // == is not yet defined on floats, so compare arithmetically.
        Assert.True(Holds("atom_number('3.5', N), N =:= 3.5."));
    }

    [Fact]
    public void AtomNumber_FormatsANumberIntoAnAtom()
    {
        Assert.True(Holds("atom_number(A, 42), A == '42'."));
    }

    [Fact]
    public void AtomNumber_FailsOnANonNumericAtom()
    {
        Assert.False(Holds("atom_number(hello, _)."));
    }

    // ---- number_string ----

    [Fact]
    public void NumberString_ParsesAStringIntoANumber()
    {
        Assert.True(Holds("atom_string('42', S), number_string(N, S), N == 42."));
    }

    [Fact]
    public void NumberString_FormatsANumberIntoAString()
    {
        Assert.True(Holds("number_string(42, S), atom_string(A, S), A == '42'."));
    }

    // ---- atomic_list_concat/2 ----

    [Fact]
    public void AtomicListConcat2_JoinsAtoms()
    {
        Assert.True(Holds("atomic_list_concat([foo, bar, baz], A), A == foobarbaz."));
    }

    [Fact]
    public void AtomicListConcat2_RendersNumbers()
    {
        Assert.True(Holds("atomic_list_concat([a, 1, b, 2], A), A == 'a1b2'."));
    }

    [Fact]
    public void AtomicListConcat2_EmptyListIsEmptyAtom()
    {
        Assert.True(Holds("atomic_list_concat([], A), A == ''."));
    }

    // ---- atomic_list_concat/3 — join ----

    [Fact]
    public void AtomicListConcat3_JoinsWithASeparator()
    {
        Assert.True(Holds("atomic_list_concat([a,b,c], '-', A), A == 'a-b-c'."));
    }

    [Fact]
    public void AtomicListConcat3_JoinsASingleton()
    {
        Assert.True(Holds("atomic_list_concat([x], '-', A), A == x."));
    }

    // ---- atomic_list_concat/3 — split ----

    [Fact]
    public void AtomicListConcat3_SplitsOnTheSeparator()
    {
        Assert.True(Holds("atomic_list_concat(L, '-', 'a-b-c'), L == [a,b,c]."));
    }

    [Fact]
    public void AtomicListConcat3_SplitsOnAMultiCharSeparator()
    {
        Assert.True(Holds(
            "atomic_list_concat(L, ', ', 'one, two, three'), L == [one, two, three]."));
    }

    [Fact]
    public void AtomicListConcat3_SplitKeepsLeadingEmptyField()
    {
        Assert.True(Holds("atomic_list_concat(L, '-', '-a'), L == ['', a]."));
    }

    // ---- char_type ----

    [Fact]
    public void CharType_ClassifiesLettersAndDigits()
    {
        Assert.True(Holds("char_type(a, alpha)."));
        Assert.False(Holds("char_type('5', alpha)."));
        Assert.True(Holds("char_type('5', alnum)."));
        Assert.True(Holds("char_type(' ', space)."));
    }

    [Fact]
    public void CharType_DigitYieldsTheWeight()
    {
        Assert.True(Holds("char_type('7', digit(W)), W == 7."));
    }

    [Fact]
    public void CharType_ConvertsCase()
    {
        Assert.True(Holds("char_type('A', to_lower(L)), L == a."));
        Assert.True(Holds("char_type(a, to_upper(U)), U == 'A'."));
    }

    [Fact]
    public void CharType_UpperAndLowerGateOnCase()
    {
        Assert.True(Holds("char_type('A', upper(L)), L == a."));
        Assert.False(Holds("char_type(a, upper(_))."));
        Assert.True(Holds("char_type(a, lower(U)), U == 'A'."));
    }

    [Fact]
    public void CharType_CsymAndPunct()
    {
        Assert.True(Holds("char_type('_', csym)."));
        Assert.True(Holds("char_type('+', punct)."));
        Assert.False(Holds("char_type('+', csym)."));
    }
}
