using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.IsoConformance;

/// <summary>Neumerkel's variable_names/1 battery
/// (complang.tuwien.ac.at/ulrich/iso-prolog/variable_names, post-N246 /
/// Cor.3): the write_term/2 option — names written verbatim, first
/// matching pair wins, bound pairs ignored, the option list validated up
/// front (instantiation vs domain errors only) — plus the read_term/2,3
/// side, where the same option name is an OUTPUT argument whose value
/// unifies after the read (a mismatch fails, it never errs), and the
/// error conventions for open/4's option list. Where the battery lists
/// alternatives, the pin records the one this engine takes.</summary>
public sealed class VariableNamesConformance
{
    private static void True(string q) =>
        Assert.True(new PrologEngine().Query(q).Success, q);
    private static void False(string q) =>
        Assert.False(new PrologEngine().Query(q).Success, q);
    private static void Raises(string goal, string pattern) =>
        True($"catch(({goal}), error({pattern}, _), true), "
           + $"\\+ catch(({goal}), _, fail).");
    private static void Out(string goal, string expected) =>
        True($"with_output_to(atom(A), ({goal})), A == '{expected}'.");

    /// <summary>Runs the query against a data file whose path replaces
    /// <c>{F}</c>; used for the read_term and open/4 cases.</summary>
    private static void WithData(string data, string q, bool expect = true)
    {
        string dir = Path.Combine(Path.GetTempPath(), "shumway-vn-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "d.pl").Replace('\\', '/');
        File.WriteAllText(f, data);
        try
        {
            Assert.Equal(expect, new PrologEngine().Query(q.Replace("{F}", f)).Success);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private const string WT = "write_term(T,[quoted(true),variable_names([N=T])])";

    // ---- write_term(T, [quoted(true), variable_names([N=T])]) ----

    [Fact] public void V01_UnboundName() => Raises(WT, "instantiation_error");
    [Fact] public void V02_NamedX() => Out($"N='X', {WT}", "X");
    [Fact] public void V03_NameIsTheVariable() =>
        Raises($"N=T, {WT}", "instantiation_error");
    [Fact] public void V04_Underscore() => Out($"N='_', {WT}", "_");
    [Fact] public void V65_NameWithComment() => Out($"N='_/*.*/', {WT}", "_/*.*/");
    [Fact] public void V05_LowercaseName() => Out($"N=x, {WT}", "x");
    [Fact] public void V06_OperatorName() => Out($"N='x+y', {WT}", "x+y");
    [Fact] public void V50_ParensName() => Out($"N='))', {WT}", "))");
    [Fact] public void V07_IntegerName() =>
        Raises($"N=7, {WT}", "domain_error(write_option, variable_names(_))");
    [Fact] public void V08_CompoundName() =>
        Raises($"N=1+2, {WT}", "domain_error(write_option, variable_names(_))");
    [Fact] public void V09_DollarVarName() =>
        Raises($"N='$VAR'(9), {WT}", "domain_error(write_option, variable_names(_))");
    [Fact] public void V10_BoundPairUnboundName() =>
        // The pair a=N still has an unbound name: checked before use.
        Raises($"T=a, {WT}", "instantiation_error");
    [Fact] public void V11_BoundPairIsIgnored() => Out($"T=a, N='Any', {WT}", "a");
    [Fact] public void V12_DollarVarTermIsNotAVariable() =>
        True($"T='$VAR'(9), N='_', with_output_to(atom(A), {WT}), "
           + "A == '\\'$VAR\\'(9)'.");
    [Fact] public void V74_InnerFreshVariable() =>
        True($"T=f(_), N='Bad', with_output_to(atom(A), {WT}), "
           + "sub_atom(A, 0, 3, _, 'f(_').");
    [Fact]
    public void V28_WritingDoesNotWakeFrozenGoal()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("use_module(library(freeze)).").Success);
        Assert.True(e.Query(
            $"freeze(T, throw(g(T))), N='X', with_output_to(atom(A), {WT}), "
          + "A == 'X'.").Success);
    }

    // ---- variable_names(['X'=X,'Y'=Y,'Z'=Z]) ----

    private const string XYZ = "variable_names(['X'=X,'Y'=Y,'Z'=Z])";

    [Fact] public void V13_FreshVarNotInList() =>
        True($"with_output_to(atom(A), write_term(T,[quoted(true), "
           + "variable_names(['X'=_,'Y'=_,'Z'=_])])), sub_atom(A, 0, 1, _, '_'), T == T.");
    [Fact] public void V14_ThreeNames() => Out($"T=(X,Y,Z), write_term(T,[{XYZ}])", "X,Y,Z");
    [Fact] public void V15_AliasedPicksFirstPair() =>
        Out($"Z=Y, T=(X,Y,Z), write_term(T,[{XYZ}])", "X,Y,Y");
    [Fact] public void V16_AllAliased() =>
        Out($"Z=Y, Y=X, T=(X,Y,Z), write_term(T,[{XYZ}])", "X,X,X");
    [Fact] public void V17_SubsetYZ() => Out($"T=(Y,Z), write_term(T,[{XYZ}])", "Y,Z");
    [Fact] public void V18_SubsetZY() => Out($"T=(Z,Y), write_term(T,[{XYZ}])", "Z,Y");

    private const string ZYX = "variable_names(['Z'=Z,'Y'=Y,'X'=X])";

    [Fact] public void V19_FreshVarReversedList() =>
        True("with_output_to(atom(A), write_term(T,[quoted(true), "
           + "variable_names(['Z'=_,'Y'=_,'X'=_])])), sub_atom(A, 0, 1, _, '_'), T == T.");
    [Fact] public void V20_ReversedThreeNames() =>
        Out($"T=(X,Y,Z), write_term(T,[{ZYX}])", "X,Y,Z");
    [Fact] public void V21_ReversedAliasedPicksFirstPair() =>
        Out($"Z=Y, T=(X,Y,Z), write_term(T,[{ZYX}])", "X,Z,Z");
    [Fact] public void V22_ReversedAllAliased() =>
        Out($"Z=Y, Y=X, T=(X,Y,Z), write_term(T,[{ZYX}])", "Z,Z,Z");
    [Fact] public void V23_ReversedSubsetYZ() => Out($"T=(Y,Z), write_term(T,[{ZYX}])", "Y,Z");
    [Fact] public void V24_ReversedSubsetZY() => Out($"T=(Z,Y), write_term(T,[{ZYX}])", "Z,Y");

    [Fact] public void V25_DuplicateNamesFreshVar() =>
        True("with_output_to(atom(A), write_term(T,[quoted(true), "
           + "variable_names(['X'=_,'X'=_,'X'=_])])), sub_atom(A, 0, 1, _, '_'), T == T.");
    [Fact] public void V26_DuplicateNames() =>
        Out("T=(X,Y,Z), write_term(T,[variable_names(['X'=Z,'X'=Y,'X'=X])])", "X,X,X");
    [Fact] public void V27_AllPairsBound() =>
        Out("T=(1,2,3), T=(X,Y,Z), write_term(T,[variable_names(['X'=Z,'X'=Y,'X'=X])])",
            "1,2,3");

    // ---- option-list validation (write side) ----

    [Fact] public void V30_UnboundList() =>
        Raises("write_term(_,[variable_names(_)])", "instantiation_error");
    [Fact] public void V31_IntegerList() =>
        Raises("write_term(_,[variable_names(1)])",
               "domain_error(write_option, variable_names(1))");
    [Fact] public void V33_NilElement() =>
        Raises("write_term(_,[variable_names([[]])])",
               "domain_error(write_option, variable_names(_))");
    [Fact] public void V34_NonList() =>
        Raises("write_term(_,[variable_names(non_list)])",
               "domain_error(write_option, variable_names(non_list))");
    [Fact] public void V35_ImproperTailBadElement() =>
        // The battery allows domain_error or instantiation_error.
        Raises("write_term(T,[variable_names([T='T'|non_list])])",
               "domain_error(write_option, variable_names(_))");
    [Fact] public void V52_PartialList() =>
        Raises("write_term(T,[variable_names(['T'=T|_])])", "instantiation_error");
    [Fact] public void V51_ImproperTail() =>
        Raises("write_term(T,[variable_names(['T'=T|non_list])])",
               "domain_error(write_option, variable_names(_))");
    [Fact] public void V36_PairNotEquals() =>
        Raises("write_term(T,[variable_names([T-'T'])])",
               "domain_error(write_option, variable_names(_))");
    [Fact] public void V63_VarThenAtomElement() =>
        // Either error allowed; this engine reports the invalid option whole.
        Raises("write_term(_,[variable_names([_,a])])",
               "domain_error(write_option, variable_names(_))");
    [Fact] public void V64_AtomThenVarElement() =>
        Raises("write_term(_,[variable_names([a,_])])",
               "domain_error(write_option, variable_names(_))");
    [Fact] public void V66_AtomElementPartialTail() =>
        Raises("write_term(_,[variable_names([a|_])])",
               "domain_error(write_option, variable_names(_))");
    [Fact] public void V67_NonVariablePairs() =>
        Raises("write_term(_,[variable_names([i=i,7=i])])",
               "domain_error(write_option, variable_names(_))");
    [Fact] public void V68_TwoUnboundElements() =>
        Raises("write_term(_,[variable_names([_,_])])", "instantiation_error");

    // ---- writer syntax around named variables ----

    [Fact] public void V43_PrefixMinusOverCaret() =>
        Out("write_term(-X^2,[variable_names(['X'=X])])", "- (X^2)");
    [Fact] public void V44_PrefixMinusOverBoundCaret() =>
        Out("X=1, write_term(-X^2,[variable_names(['X'=X])])", "- (1^2)");
    [Fact] public void V53_OuterTermUnboundName() =>
        Raises("write_term(_S,[quoted(true),variable_names([_N=_T])])",
               "instantiation_error");
    [Fact] public void V54_NameThatIsAComment() =>
        // Names are written verbatim, never quoted; the battery also
        // allows a separating space.
        Out("S=1+T, N='/*r*/V', write_term(S,[quoted(true),variable_names([N=T])])",
            "1+/*r*/V");
    [Fact] public void V55_NameWithLeadingSpace() =>
        Out("S=1+T, N=' /*r*/V', write_term(S,[quoted(true),variable_names([N=T])])",
            "1+ /*r*/V");
    [Fact] public void V58_PlusNamedVariableOnRight() =>
        // '1++' — the battery also allows '1+ +'.
        Out("S=1+T, N=(+), write_term(S,[quoted(true),variable_names([N=T])])", "1++");
    [Fact] public void V59_PlusNamedVariableOnLeft() =>
        Out("S=T+1, N=(+), write_term(S,[quoted(true),variable_names([N=T])])", "++1");
    [Fact] public void V73_SpaceAfterAlphabeticOperator() =>
        Out("S=(1 is T), N='X', write_term(S,[quoted(true),variable_names([N=T])])",
            "1 is X");
    [Fact] public void V71_LastOptionWins() =>
        Out("write_term(T,[variable_names(['Bad'=T]),variable_names(['Good'=T])])",
            "Good");

    // ---- open/4 option-list errors (same conventions) ----

    [Fact] public void V37_UnboundOption() => WithData("",
        "catch(open('{F}',write,_,[_]), error(instantiation_error,_), true).");
    [Fact] public void V38_IntegerOption() => WithData("",
        "catch(open('{F}',write,_,[1]), error(domain_error(stream_option,1),_), true).");
    [Fact] public void V56_UnknownOptionVarArg() => WithData("",
        "catch(open('{F}',write,_,[typex(_)]), "
      + "error(domain_error(stream_option,typex(_)),_), true).");
    [Fact] public void V57_UnknownOptionIntArg() => WithData("",
        "catch(open('{F}',write,_,[typex(1)]), "
      + "error(domain_error(stream_option,typex(1)),_), true).");
    [Fact] public void V62_UnknownOptionCompoundArg() => WithData("",
        "catch(open('{F}',write,_,[typex(s(_))]), "
      + "error(domain_error(stream_option,typex(_)),_), true).");
    [Fact] public void V39_TypeText() => WithData("",
        "open('{F}',write,S,[type(text)]), close(S).");
    [Fact] public void V40_TypeInteger() => WithData("",
        "catch(open('{F}',write,_,[type(1)]), "
      + "error(domain_error(stream_option,type(1)),_), true).");
    [Fact] public void V41_TypeVarArg() => WithData("",
        "catch(open('{F}',write,_,[type(_)]), error(instantiation_error,_), true).");
    [Fact] public void V60_AliasVarArg() => WithData("",
        "catch(open('{F}',write,_,[alias(_)]), error(instantiation_error,_), true).");
    [Fact] public void V42_TypeBadAtom() => WithData("",
        "catch(open('{F}',write,_,[type(nontype)]), "
      + "error(domain_error(stream_option,type(nontype)),_), true).");
    [Fact] public void V61_AliasInteger() => WithData("",
        "catch(open('{F}',write,_,[alias(1)]), "
      + "error(domain_error(stream_option,alias(1)),_), true).");

    // ---- read_term: variable_names/singletons are OUTPUT options ----

    [Fact] public void V45_46_ReadAtomNoVariables() => WithData("a.\n",
        // The quad also pins peeks("\n"): the newline after the end dot
        // stays unconsumed.
        "open('{F}',read,S), read_term(S,T,[variable_names(VN)]), "
      + "peek_char(S, C), close(S), T == a, VN == [], C == '\\n'.");
    [Fact] public void V29_32_FirstAppearanceOrder() => WithData("B+C+A+B+C+A.\n",
        "open('{F}',read,S), read_term(S,T,[variable_names(VN)]), "
      + "peek_char(S, C), close(S), "
      + "VN = [_=1,_=2,_=3], T == 1+2+3+1+2+3, C == '\\n', "
      + "with_output_to(atom(A), writeq(VN)), "
      + "A == '[\\'B\\'=1,\\'C\\'=2,\\'A\\'=3]'.");
    [Fact] public void V47_48_ValueMismatchFails() => WithData("a.",
        "open('{F}',read,S), read_term(S,_,[variable_names(42)]).", expect: false);
    [Fact] public void V49_SyntaxErrorBeatsMismatch() => WithData("a b.",
        "catch((open('{F}',read,S), read_term(S,_,[variable_names(42)])), "
      + "error(syntax_error(_),_), true).");
    [Fact] public void V69_70_SingletonsMismatchFails() => WithData("a.",
        "open('{F}',read,S), read_term(S,_,[singletons(1)]).", expect: false);
    [Fact] public void V72_SingletonsNil() => WithData("a.",
        "open('{F}',read,S), read_term(S,T,[singletons([])]), close(S), T == a.");
}
