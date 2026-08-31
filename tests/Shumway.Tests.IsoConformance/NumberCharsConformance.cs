using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.IsoConformance;

/// <summary>Neumerkel's number_chars/2 battery
/// (complang.tuwien.ac.at/ulrich/iso-prolog/number_chars): the undisputed
/// ISO cases plus the Cor.2:2012 reversible semantics for a partially
/// instantiated character list. The out-of-scope cyclic case answers a
/// catchable type_error(list, _) here — the page accepts even halting.</summary>
public sealed class NumberCharsConformance
{
    private static void True(string q) =>
        Assert.True(new PrologEngine().Query(q).Success, q);
    private static void False(string q) =>
        Assert.False(new PrologEngine().Query(q).Success, q);
    private static void Raises(string goal, string pattern) =>
        True($"catch(({goal}), error({pattern}, _), true), "
           + $"\\+ catch(({goal}), _, fail).");

    // ---- Section 0: undisputed (1..30, 47..53) ----

    [Fact] public void N01() => True("number_chars(1.2, ['1','.','2']).");
    [Fact] public void N02() => True("number_chars(1.0e9, ['1','.','0','E','9']).");
    [Fact] public void N03() => True("number_chars(1, ['0','1']).");
    [Fact] public void N04() => Raises("number_chars(1, [a])", "syntax_error(_)");
    [Fact] public void N05() => Raises("number_chars(1, [])", "syntax_error(_)");
    [Fact] public void N06() => Raises("number_chars(1, [[]])", "type_error(character, [])");
    [Fact] public void N07() => Raises("number_chars(1, [' ',[]])", "type_error(character, [])");
    [Fact] public void N08() => Raises("number_chars(1, [0])", "type_error(character, 0)");
    [Fact] public void N09() => Raises("number_chars(1, [_,[]])", "type_error(character, [])");
    [Fact] public void N10() => Raises("number_chars(_, [_])", "instantiation_error");
    [Fact] public void N11() => Raises("number_chars(_, ['0'|_])", "instantiation_error");
    [Fact] public void N12() => Raises("number_chars(_, '1')", "type_error(list, '1')");
    [Fact] public void N13() => Raises("number_chars(_, [a|a])", "type_error(list, [a|a])");
    [Fact] public void N14() => Raises("number_chars(_, [49])", "type_error(character, 49)");
    [Fact] public void N15() => Raises("number_chars(_, [])", "syntax_error(_)");
    [Fact] public void N16() => Raises("number_chars(_, ['3',' '])", "syntax_error(_)");
    [Fact] public void N17() => Raises("number_chars(_, ['3','.'])", "syntax_error(_)");
    [Fact] public void N18() => True("number_chars(N, [' ','1']), N == 1.");
    [Fact] public void N19() => True("number_chars(N, ['\\n','1']), N == 1.");
    [Fact] public void N20() => True("number_chars(N, [' ','0','''',a]), N == 97.");
    [Fact] public void N21() => True("number_chars(N, [-,' ','1']), N == -1.");
    [Fact] public void N22() => True("number_chars(N, [/,*,*,/,'1']), N == 1.");
    [Fact] public void N23() => True("number_chars(N, ['%','\\n','1']), N == 1.");
    [Fact] public void N24() => Raises("number_chars(_, [-,/,*,*,/,'1'])", "syntax_error(_)");
    [Fact] public void N25() => Raises("number_chars(_, ['1',e,'1'])", "syntax_error(_)");
    [Fact] public void N26() => Raises("number_chars(_, ['1','.','0',e])", "syntax_error(_)");
    [Fact] public void N27() => Raises("number_chars(_, ['1','.','0',e,e])", "syntax_error(_)");
    [Fact] public void N28() => True("number_chars(N, ['0',x,'1']), N == 1.");
    [Fact] public void N29() => Raises("number_chars(_, ['0','X','1'])", "syntax_error(_)");
    [Fact] public void N47() => False("number_chars(1, ['.'|_]).");
    [Fact] public void N48() => Raises("number_chars(_, [+,'1'])", "syntax_error(_)");
    [Fact] public void N49() => Raises("number_chars(_, [+,' ','1'])", "syntax_error(_)");
    [Fact] public void N50() => Raises("number_chars(_, ['''',+,'''','1'])", "syntax_error(_)");
    [Fact] public void N51() => Raises("number_chars(_, ['11'])", "type_error(character, '11')");
    [Fact] public void N52() => Raises("number_chars(_, ['1.1'])", "type_error(character, '1.1')");
    [Fact] public void N53() => Raises("number_chars(1+1, ['2'])", "type_error(number, 1+1)");

    // ---- Section 2: Cor.2:2012 reversible semantics (31..46) ----

    [Fact] public void M31() => True("number_chars(1, [C]), C == '1'.");
    [Fact] public void M32() => False("number_chars(1, [_,_]).");
    [Fact] public void M33() => False("number_chars(1, [C,C]).");
    [Fact] public void M34() => False("number_chars(0, [C,C]).");
    [Fact] public void M35() => True("number_chars(10, [C,D]), C == '1', D == '0'.");
    [Fact] public void M36() => False("number_chars(100, [_,_]).");
    [Fact] public void M37() => Raises("number_chars(_, [_|2])", "type_error(list, [_|2])");
    [Fact] public void M38() => Raises("number_chars(_, [1|_])", "instantiation_error");
    [Fact] public void M39() => Raises("number_chars(_, [1|2])", "type_error(character, 1)");
    [Fact] public void M40() => Raises("number_chars([], 1)", "type_error(number, [])");
    [Fact] public void M41() => Raises("number_chars(1, 1)", "type_error(list, 1)");
    [Fact] public void M42() => Raises("number_chars(1, [a|2])", "type_error(list, [a|2])");
    [Fact] public void M43() => Raises("number_chars(1, [_|2])", "type_error(list, [_|2])");
    [Fact] public void M44() => Raises("number_chars(1, [[]|_])", "type_error(character, [])");
    [Fact] public void M45() => Raises("number_chars(1, [[]|2])", "type_error(character, [])");
    [Fact] public void M46_Cyclic() =>
        Raises("(L = ['1'|L], number_chars(_, L))", "type_error(list, _)");
}
