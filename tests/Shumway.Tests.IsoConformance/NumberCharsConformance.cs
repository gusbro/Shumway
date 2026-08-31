using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.IsoConformance;

/// <summary>Neumerkel's number_chars/2 battery
/// (complang.tuwien.ac.at/ulrich/iso-prolog/number_chars): the undisputed
/// ISO cases plus the Cor.2:2012 reversible semantics for a partially
/// instantiated character list. The out-of-scope cyclic case answers a
/// catchable type_error(list, _) here — the page accepts even halting.
/// The C-numbered section is the newer number_chars_cont comparison
/// (cases 54..83, run there under double_quotes=chars — this engine's
/// default): quoted/escaped signs, 0'-literal edges, radix forms, and
/// the float-range cases, where a literal past double's range is a
/// syntax error, never an infinity.</summary>
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

    // ---- number_chars_cont: contemporary comparison (54..83) ----

    [Fact] public void C54_QuotedMinus() =>
        True("number_chars(N, \"'-'1\"), N == -1.");
    [Fact] public void C55_TrailingZeroFraction() => True("number_chars(1.2, \"1.20\").");
    [Fact] public void C56_LowercaseExponent() => True("number_chars(1.0e9, \"1.0e9\").");
    [Fact] public void C57_SignSpaceComment() =>
        True("number_chars(N, \"- /**/1\"), N == -1.");
    [Fact] public void C58_BareCharQuote() =>
        Raises("number_chars(_, \"0'\")", "syntax_error(_)");
    [Fact] public void C59_RawNewlineCharLiteral() =>
        Raises("number_chars(_, \"0'\\n\")", "syntax_error(_)");
    [Fact] public void C60_EscapedNewlineCharLiteral() =>
        True("number_chars(N, \"0'\\\\n\"), N == 10.");
    [Fact] public void C61_OctalEscapeCharLiteral() =>
        True("number_chars(N, \"0'\\\\7\\\\\"), N == 7.");
    [Fact] public void C62_DotCharLiteral() =>
        True("number_chars(N, \"0'.\"), N == 46.");
    [Fact] public void C63_ContinuationEscapeInQuotedMinus() =>
        True("number_chars(N, \"'\\\\\\n-' 3\"), N == -3.");
    [Fact] public void C64_NegativeZeroFloat() => True("number_chars(0.0, \"-0.0\").");
    [Fact] public void C65_LeadingZeroInteger() => True("number_chars(10, \"010\").");
    [Fact] public void C66_LeadingZeroSolves() =>
        True("number_chars(N, \"010\"), N == 10.");
    [Fact] public void C67_LeadingZeroEightIsDecimal() =>
        True("number_chars(N, \"08\"), N == 8.");
    [Fact] public void C68_Binary() => True("number_chars(N, \"0b11\"), N == 3.");
    [Fact] public void C69_Octal() => True("number_chars(N, \"0o11\"), N == 9.");
    [Fact] public void C70_Hex() => True("number_chars(N, \"0x11\"), N == 17.");
    [Fact] public void C71_UnterminatedOctalEscape() =>
        Raises("number_chars(_, \"0'\\\\7\")", "syntax_error(_)");
    [Fact] public void C72_MixedBadList() =>
        // The battery sanctions four errors; this engine reports the first
        // non-character element.
        Raises("number_chars(_, [1,[],_|2])", "type_error(character, 1)");
    [Fact] public void C73_ParenthesizedIsNotANumber() =>
        Raises("number_chars(_, \"(0)\")", "syntax_error(_)");
    [Fact] public void C74_SignCommentZero() =>
        True("number_chars(N, \"-%\\n0\"), N == 0.");
    [Fact] public void C75_QuotedQuoteCharLiteral() =>
        True("number_chars(N, \"0'''\"), N == 39.");
    [Fact] public void C76_EscapedQuoteCharLiteral() =>
        True("number_chars(N, \"0'\\\\'\"), N == 39.");
    [Fact] public void C77_SpaceCharLiteral() =>
        True("number_chars(N, \"0' \"), N == 32.");
    [Fact] public void C78_RoundTripStability() =>
        True("number_chars(N, \"1.0e-8\"), number_chars(N, L), number_chars(N, L).");
    [Fact] public void C79_ArithmeticUnderflowIsZero() =>
        // The battery also sanctions evaluation_error(underflow).
        True("N is 0.1*10** -999, N == 0.0.");
    [Fact] public void C80_LiteralUnderflowIsZero() =>
        // The battery also sanctions representation_error(number).
        True("number_chars(N, \"0.1e-999\"), N == 0.0.");
    [Fact] public void C81_ArithmeticOverflowErrs() =>
        Raises("_ is 9.9*10**999", "evaluation_error(float_overflow)");
    [Fact] public void C82_LiteralOverflowErrs() =>
        // Of the sanctioned errors this engine picks syntax_error, with
        // SICStus, Scryer and Trealla; an infinity is non-conforming.
        Raises("number_chars(_, \"9.9e999\")", "syntax_error(_)");
    [Fact] public void C83_AllCodesList() =>
        Raises("number_chars(_, [1,2,3])", "type_error(character, 1)");
}
