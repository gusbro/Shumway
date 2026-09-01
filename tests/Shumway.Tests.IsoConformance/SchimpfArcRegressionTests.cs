using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.IsoConformance;

/// <summary>Regression pins for the engine fixes the 2026-09 Schimpf
/// conformance arc produced (error shapes, context indicators, meta-call
/// conversion, stream eof discipline). Every query is OUR OWN formulation
/// of the fixed behavior; the suite itself stays in its author's tree.</summary>
public sealed class SchimpfArcRegressionTests
{
    private static void True(string q) =>
        Assert.True(new PrologEngine().Query(q).Success, q);
    private static void False(string q) =>
        Assert.False(new PrologEngine().Query(q).Success, q);
    private static void Raises(string goal, string pattern) =>
        True($"catch(({goal}), error({pattern}, _), true), "
           + $"\\+ catch(({goal}), _, fail).");

    /// <summary>Runs the query with <c>{F}</c> replaced by the path of a
    /// fresh data file holding <paramref name="data"/>.</summary>
    private static void WithData(string data, string q, bool expect = true)
    {
        string dir = Path.Combine(Path.GetTempPath(), "shumway-schimpf-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "d.txt").Replace('\\', '/');
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

    // ---- §7.12.2: the Context slot names the PUBLIC builtin ----

    [Fact] public void Asserta_NonCallable_ContextIsAsserta1() =>
        True("catch(asserta(4), error(type_error(callable, 4), asserta/1), true).");
    [Fact] public void Assertz_RuleWithNonCallableBody_ContextIsAssertz1() =>
        True("catch(assertz((foo :- 4)), error(type_error(callable, 4), assertz/1), true).");
    [Fact] public void Findall_BadResultArg_ContextIsFindall3() =>
        True("catch(findall(X, X = 1, 12), error(type_error(list, 12), findall/3), true).");
    [Fact] public void Bagof_BadResultArg_ContextIsBagof3() =>
        True("catch(bagof(X, X = 1, 12), error(type_error(list, 12), bagof/3), true).");
    [Fact] public void Setof_VariableGoal_BadResultArg_ContextIsSetof3() =>
        // The variable-goal path runs the prelude fallback — the internal
        // '$check_partial_list' helper must not leak into the context.
        True("catch((G = (X = 1), setof(X, G, 12)), "
           + "error(type_error(list, 12), setof/3), true).");

    // ---- §7.8.3: call/N culprits ----

    [Fact] public void CallN_NonCallableClosure_CulpritIsTheClosure() =>
        Raises("call(7, _)", "type_error(callable, 7)");
    [Fact] public void CallN_NonCallableClosure_ManyExtras() =>
        Raises("call(7, _, _, _, _)", "type_error(callable, 7)");

    // ---- §7.8.3.1: conversion happens AT the call boundary ----

    [Fact] public void MetacallCut_BoundBeforeCall_IsARealCut() =>
        // X already names ! when call/1 converts its goal: the cut is
        // literal and commits within the call — one solution.
        True("X = !, findall(Y, call(((Y=1 ; Y=2), X)), L), L == [1].");
    [Fact] public void MetacallCut_BoundMidBody_CutsNothing() =>
        // Z is a VARIABLE at conversion time, so it converts to call(Z)
        // and the ! it is later bound to is local — both solutions.
        True("findall(Y, call((Z = !, (Y=1 ; Y=2), Z)), L), L == [1, 2].");
    [Fact] public void MetacallCut_AssembledConjunction_ConvertsToo() =>
        // call(',', G1, G2) assembles the construct at dispatch; its
        // variable subgoals must convert exactly like call((G1, G2))'s.
        True("findall(Y, call(',', W = !, (Y=1, W ; Y=2)), L), L == [1, 2].");
    [Fact] public void MetacallCut_LiteralInGoal_Commits() =>
        True("findall(Y, call(((Y=1 ; Y=2), !)), L), L == [1].");

    // ---- §8.8.2.3: predicate-indicator validation ----

    [Fact] public void CurrentPredicate_NegativeArity_IsTypeErrorPredicateIndicator() =>
        Raises("current_predicate(f / -1)", "type_error(predicate_indicator, f / -1)");

    // ---- §8.16.2.3: atom_concat result argument ----

    [Fact] public void AtomConcat_BoundNonAtomResult_IsTypeErrorAtom() =>
        Raises("atom_concat(a, b, 3)", "type_error(atom, 3)");

    // ---- §8.14.9.3 / §8.14.10.3: the char_conversion asymmetry ----

    [Fact] public void CurrentCharConversion_NonCharacter_IsTypeErrorWithCulprit() =>
        Raises("current_char_conversion(1, _)", "type_error(character, 1)");
    [Fact] public void CurrentCharConversion_MultiCharAtom_IsTypeError() =>
        Raises("current_char_conversion(_, ab)", "type_error(character, ab)");

    // ---- §8.14.2.3: write options ----

    [Fact] public void WriteTerm_UnknownOption_IsDomainError() =>
        Raises("write_term(x, [attributes(none)])",
            "domain_error(write_option, attributes(none))");
    [Fact] public void WriteTerm_ToInputStream_CulpritIsTheStream() =>
        Raises("write_term(user_input, x, [])",
            "permission_error(output, stream, user_input)");

    // ---- §8.11.6.3: close/2 option-list culprit ----

    [Fact] public void Close_ImproperOptionsList_CulpritIsWholeList() =>
        Raises("close(user_output, [force(true)|b])",
            "type_error(list, [force(true)|b])");

    // ---- §8.11.8: at_end_of_stream is property-based ----

    [Fact] public void AtEndOfStream_OutputStream_Fails() =>
        True("\\+ at_end_of_stream(user_output).");

    // ---- §8.11.5.3: eof_action defaults to error ----

    [Fact]
    public void ReadPastEof_DefaultEofAction_RaisesPermissionError() =>
        WithData("hello.\n",
            "open('{F}', read, S), read(S, T), T == hello, "
            + "read(S, E), E == end_of_file, "
            + "catch(read(S, _), error(permission_error(input, past_end_of_stream, _), _), R = caught), "
            + "R == caught, close(S).");

    [Fact]
    public void ReadPastEof_EofCodeAction_KeepsAnsweringEndOfFile() =>
        WithData("hello.\n",
            "open('{F}', read, S, [eof_action(eof_code)]), read(S, _), "
            + "read(S, E1), E1 == end_of_file, "
            + "read(S, E2), E2 == end_of_file, close(S).");

    // ---- user_input has a stream position (chars consumed) ----

    [Fact] public void UserInput_ReportsPositionProperty() =>
        True("stream_property(S, alias(user_input)), "
           + "stream_property(S, position(P)), integer(P).");

    // ---- (**)/2 negative base: integral float exponent stays defined ----

    [Fact] public void PowFloat_NegativeBase_IntegralFloatExponent_Succeeds() =>
        True("X is -2 ** 3.0, X =:= -8.0, Y is -2.0 ** 3.0, Y =:= -8.0.");
    [Fact] public void PowFloat_NegativeBase_FractionalExponent_IsUndefined() =>
        Raises("_ is -2 ** 3.5", "evaluation_error(undefined)");
}
