using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.IsoConformance;

/// <summary>Neumerkel's phrase/2-3 battery
/// (complang.tuwien.ac.at/ulrich/iso-prolog/phrase), 48 cases plus its call/1
/// and functor/3 preliminaries. Where the battery lists alternatives, the pin
/// records the one this engine takes. The runtime driver follows the
/// TS 13211-3 TRANSLATION model — the whole body becomes one goal before
/// anything executes — which is what cases 11 and 17 exercise: the cut in
/// {!,fail} is a real cut with the translated call's extent, and a terminal
/// is validated before the brace goal that would have bound its tail runs.</summary>
public sealed class PhraseConformance
{
    private static void True(string q) =>
        Assert.True(new PrologEngine().Query(q).Success, q);
    private static void False(string q) =>
        Assert.False(new PrologEngine().Query(q).Success, q);
    private static void Raises(string goal, string pattern) =>
        True($"catch(({goal}), error({pattern}, _), true), "
           + $"\\+ catch(({goal}), _, fail).");

    // ---- call/1 preliminaries (c1..c7) ----

    [Fact] public void C1() => Raises("call(1)", "type_error(callable, 1)");
    [Fact] public void C2() => Raises("call((1,fail))", "type_error(callable, (1,fail))");
    [Fact] public void C3() => Raises("call((fail,1))", "type_error(callable, (fail,1))");
    [Fact] public void C4() => Raises("call((!;1))", "type_error(callable, (!;1))");
    [Fact] public void C5() => True("call((!;\\+1)).");
    [Fact] public void C6() => True("call((!;call(1))).");
    [Fact] public void C7() => True("call((\\+!;X=a)), X == a.");

    // ---- functor/3 preliminaries (f1..f3) ----

    [Fact] public void F1() => Raises("functor(_,_,_)", "instantiation_error");
    [Fact] public void F2() => Raises("functor(_,1,2)", "type_error(atom, 1)");
    [Fact] public void F3() => True("functor([_],'.',2).");

    // ---- phrase (1..48) ----

    [Fact] public void P01_EqualsAsBody() => True("phrase(=,L), L == [].");
    [Fact] public void P02_NumberBody() => Raises("phrase(1,_)", "type_error(callable, 1)");
    [Fact] public void P03_CutBody() => True("phrase(!,L), L == [].");
    [Fact] public void P04_Terminal() => True("phrase([a],L), L == [a].");
    [Fact] public void P05_ImproperTerminal() =>
        Raises("phrase([a|b],_)", "type_error(list, [a|b])");
    [Fact] public void P06_OpenTerminalOpenList() =>
        Raises("phrase([a|_],_)", "instantiation_error");
    [Fact] public void P07_OpenTerminalClosedList() =>
        Raises("phrase([a|_],[a,b])", "instantiation_error");
    [Fact] public void P08_OpenTerminalEmpty() =>
        Raises("phrase([a|_],[])", "instantiation_error");
    [Fact] public void P09_TerminalThenNil() => True("phrase(([a],[]),[a]).");
    [Fact] public void P10_BraceNumberAfterFailingTerminal() =>
        // A number INSIDE braces surfaces from call/1 with the whole
        // translated goal as culprit (quad: type_error(callable,(...,...))).
        Raises("phrase(([a],{1}),[])", "type_error(callable, (_,_))");
    [Fact] public void P11_CutFailInBraces_IsTransparent() =>
        // Tr({G}) inlines G, so the cut's extent is the translated call —
        // the whole phrase — and the ;[] branch is cut away (Scryer agrees;
        // SWI keeps the cut local and answers []).
        False("phrase(({!,fail};[]),_).");
    [Fact] public void P12_BarAlternation() => True("phrase('|'([],[a]),[a]).");
    [Fact] public void P13_NumberAfterBraces() =>
        Raises("phrase(({fail},1),_)", "type_error(callable, 1)");
    [Fact] public void P14_DisjunctionFirstSolution() =>
        True("phrase(([a];[]),L), L == [a].");
    [Fact] public void P15_NonCallableConjInBraces() =>
        // quad: type_error(callable, ((fail,1),[]=_)) — the culprit is the
        // translated goal, so the (,)/2 shape is what the pin holds.
        Raises("phrase({fail,1},_)", "type_error(callable, (_,_))");
    [Fact] public void P16_ThrowInBraces() =>
        True("catch(phrase({throw(h)},[a]), h, true).");
    [Fact] public void P17_TerminalCheckedBeforeBraceRuns() =>
        // Translation precedes execution: [a|L] is validated while L is
        // still unbound — {L=[]} never runs (Scryer agrees; SWI runs the
        // brace goal first and succeeds).
        Raises("phrase(({L=[]},[a|L]),[a])", "instantiation_error");
    [Fact] public void P18_OpenTerminalThenBind() =>
        Raises("(phrase([a|L],_), L=[b])", "instantiation_error");
    [Fact] public void P19_OpenTerminalThenNumber() =>
        // The battery allows either error; translation reads left to right,
        // so the open terminal is found before the number.
        Raises("phrase(([a|_],1),[])", "instantiation_error");
    [Fact] public void P20_NumberThenOpenTerminal() =>
        Raises("phrase((1,[a|_]),[])", "type_error(callable, 1)");
    [Fact] public void P21_NumberThenImproper() =>
        Raises("phrase((1,[a|b]),[])", "type_error(callable, 1)");
    [Fact] public void P22_IfThenInBar() =>
        True("phrase('|'(([x]->[y]),[z]),L), L == [x,y].");
    [Fact] public void P23_IfThenInSemicolon() =>
        True("phrase(;(([x]->[y]),[z]),L), L == [x,y].");
    [Fact] public void P24_AssertaDcgRule() =>
        Raises("asserta((a-->b))",
               "permission_error(modify, static_procedure, (-->)/2)");
    [Fact] public void P25_ClauseOfDcgRule() =>
        Raises("clause((a-->b),_)",
               "permission_error(access, private_procedure, (-->)/2)");
    [Fact] public void P26_CallingArrow() =>
        Raises("(_-->_)", "existence_error(procedure, (-->)/2)");
    [Fact] public void P27_NegatedTerminal() => True("phrase(\\+[a],[]).");
    [Fact] public void P28_NegatedNumber() =>
        Raises("phrase(\\+1,_)", "type_error(callable, 1)");
    [Fact] public void P29_NegatedNumberAfterTerminal() =>
        // \+ is an extension (TS has no negation; Scryer rejects it with
        // representation_error(dcg_body)). Its argument translates INSIDE
        // the negation at call time, so [a] failing against [] means the
        // \+1 never runs — the quad sanctions exactly this "false".
        False("phrase(([a],\\+1),[]).");
    [Fact] public void P30_NegationInDisjunction() =>
        // Same lazy reading: the ([a],\+1) branch fails, ;[] succeeds.
        True("phrase(([a],\\+1;[]),[]).");
    [Fact] public void P31_PhraseOfPhrase() =>
        Raises("phrase(phrase(phrase,[]),_)", "existence_error(procedure, phrase/4)");
    [Fact] public void P32_CallNilBody() =>
        Raises("phrase(call([]),[])", "existence_error(procedure, []/2)");
    [Fact] public void P33_ClosedTailMismatch() => False("L=[], phrase([a|L],[b]).");
    [Fact] public void P34_TerminalMismatch() => False("phrase([a],[b]).");
    [Fact] public void P35_CutBodyDoesNotConsume() =>
        True("(phrase(!,[_]) ; L=1), L == 1.");
    [Fact] public void P36_ClosedTailMatch() => True("L=[], phrase([a|L],[a]).");
    [Fact] public void P37_CutTerminalBraceNumber() =>
        Raises("phrase((!,[a],{1}),[])", "type_error(callable, (_,_))");
    [Fact] public void P38_TerminalTailIsRest() =>
        Raises("phrase([a|L],L)", "instantiation_error");
    [Fact] public void P39_VarBody() => Raises("phrase(_,_)", "instantiation_error");
    [Fact] public void P40_NilBody() => True("K = [], phrase(K,L), L == [].");
    [Fact] public void P41_NonListInput() => False("phrase([], non_list).");
    [Fact] public void P42_ImproperInput() => False("phrase([], [a|non_list]).");
    [Fact] public void P43_NonListRest() =>
        True("phrase([], L, non_list), L == non_list.");
    [Fact] public void P44_ImproperRest() =>
        True("phrase([], L, [a|non_list]), L == [a|non_list].");
    [Fact] public void P45_PhraseTwoAfterTerminal() =>
        False("phrase(([a],phrase(2)),[]).");
    [Fact] public void P46_NumberThenBraceNumber() =>
        Raises("phrase((1,{2}),[])", "type_error(callable, 1)");
    [Fact] public void P47_BraceNumberThenNumber() =>
        // The brace goal translates without inspection, so the culprit is
        // the BODY number 1 — the quad's only sanctioned answer.
        Raises("phrase(({2},1),[])", "type_error(callable, 1)");
    [Fact] public void P48_ManyNonCallables() =>
        Raises("phrase((1,(2,[_|_],3),4),[])", "type_error(callable, _)");
}
