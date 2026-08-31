using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.IsoConformance;

/// <summary>Regression pins for the engine fixes the 2026-08/09 conformance
/// arc produced. Every query here is OUR OWN formulation of the fixed
/// behavior — the conformity batteries themselves (Neumerkel's length,
/// phrase, number_chars, setup_call_cleanup and variable_names sets) run
/// from tests/conformity, which fetches the author's data from the live
/// site and never redistributes it.</summary>
public sealed class ConformanceArcRegressionTests
{
    private static void True(string q) =>
        Assert.True(new PrologEngine().Query(q).Success, q);
    private static void False(string q) =>
        Assert.False(new PrologEngine().Query(q).Success, q);
    private static void Raises(string goal, string pattern) =>
        True($"catch(({goal}), error({pattern}, _), true), "
           + $"\\+ catch(({goal}), _, fail).");

    /// <summary>Runs the query against a data file whose path replaces
    /// <c>{F}</c> — for the read_term option pins.</summary>
    private static void WithData(string data, string q, bool expect = true)
    {
        string dir = Path.Combine(Path.GetTempPath(), "shumway-arc-" + Guid.NewGuid());
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

    // ---- length/2: tortoise-hare spine walk, no type_error on non-lists --

    [Fact] public void Length_CyclicSpine_FailsInsteadOfHanging() =>
        False("X = [x,y|X], length(X, _).");
    [Fact] public void Length_ShortProperList_NoFalseCyclePositive() =>
        // the first hare compared while resting on nil — [x] read as cyclic.
        True("length([x], N), N == 1, length([x,y,z], M), M == 3.");
    [Fact] public void Length_NegativeIsDomainError() =>
        Raises("length([q,w], -7)", "domain_error(not_less_than_zero, -7)");
    [Fact] public void Length_NonListFails() =>
        True("\\+ length(foo, _), \\+ length([p|q], _).");
    [Fact] public void Length_SelfAliasedEnumerates() =>
        // length(L, L) must ENUMERATE toward a genuine resource_error —
        // the ISO outcome — not short-circuit to false (no fail-fast
        // special case), and the enumeration stays interruptible at safe
        // points. time_out bounds the pin without exhausting the heap.
        True("time_out(length(L, L), 300, R), R == time_out, "
           + "time_out(length([x,y|T], T), 300, S), S == time_out.");
    [Fact] public void Length_HugeCountFailsWithoutTruncation() =>
        // 2^64: a silent (int) cast once made this allocate a wrong-sized list.
        False("length(_, 18446744073709551616).");

    // ---- phrase/2,3: the TS 13211-3 translation model ----

    [Fact] public void Phrase_BraceCutIsTransparent() =>
        // {!, fail} inlines, so the cut's extent is the whole translated
        // body: the ; [] branch is cut away.
        False("phrase(({!, fail} ; []), []).");
    [Fact] public void Phrase_TerminalValidatedBeforeBraceGoalRuns() =>
        Raises("phrase(({T = []}, [z|T]), [z])", "instantiation_error");
    [Fact] public void Phrase_NegationTranslatesLazily() =>
        // ([q], \+ 0) on []: [q] fails first, the bad \+ argument never
        // translates — and \+ still errs when it actually runs.
        True("\\+ phrase(([q], \\+ 0), []), "
           + "catch(phrase(\\+ 0, _), error(type_error(callable, 0), _), true).");
    [Fact] public void Phrase_NumberBodyErrsAtTranslation() =>
        Raises("phrase(({true}, 7), _)", "type_error(callable, 7)");
    [Fact] public void Phrase_BraceNumberCulpritIsTranslatedGoal() =>
        Raises("phrase({0}, _)", "type_error(callable, (_,_))");
    [Fact] public void Phrase_ImproperTerminalIsTypeError() =>
        Raises("phrase([q|w], _)", "type_error(list, [q|w])");
    [Fact] public void Phrase_CutBodyConsumesNothing() =>
        True("phrase(!, R), R == [].");
    [Fact] public void Phrase_StaticExpansionSkipsCut() =>
        // phrase(!, L) once compiled to a call of a nonexistent !/2.
        True("phrase((!, [k]), L), L == [k].");

    // ---- read_term/2,3: output options unify after the read ----

    [Fact] public void ReadTerm_OutputOptionMismatchFails() => WithData("z.",
        "open('{F}', read, S), read_term(S, _, [variable_names(0)]).",
        expect: false);
    [Fact] public void ReadTerm_SyntaxErrorBeatsOptionMismatch() => WithData("z w.",
        "catch((open('{F}', read, S), read_term(S, _, [variable_names(0)])), "
      + "error(syntax_error(_), _), true).");
    [Fact] public void ReadTerm_SingletonsMismatchFails() => WithData("z.",
        "open('{F}', read, S), read_term(S, _, [singletons(7)]).",
        expect: false);
    [Fact] public void ReadTerm_UnknownOptionStillErrs() => WithData("z.",
        "catch((open('{F}', read, S), read_term(S, _, [gibberish(1)])), "
      + "error(domain_error(read_option, gibberish(1)), _), true).");

    // ---- the number text reader (number_chars/number_codes) ----

    [Fact] public void NumberChars_ParenthesizedIsNotANumber() =>
        // the term-reader fallback once read "(1)" through to 1.
        Raises("number_chars(_, ['(','1',')'])", "syntax_error(_)");
    [Fact] public void NumberChars_FloatOverflowIsSyntaxError() =>
        // Also pins the lexer's TryParse path: .NET Framework's double.Parse
        // throws where Core returns Infinity — both must reject here.
        Raises("number_chars(_, ['5','.','0','e','9','9','9'])", "syntax_error(_)");
    [Fact] public void NumberChars_FloatUnderflowIsZero() =>
        True("number_chars(N, ['5','.','0','e','-','9','9','9']), N == 0.0.");
    [Fact] public void NumberChars_QuotedMinusStillReads() =>
        // the term-reader fallback exists FOR this shape.
        True("number_chars(N, ['''','-','''','3']), N == -3.");
    [Fact] public void NumberChars_ReversibleOnPartialList() =>
        True("number_chars(74, [C, D]), C == '7', D == '4', "
           + "\\+ number_chars(74, [_]).");

    // ---- setup_call_cleanup/3 ----

    [Fact] public void Scc_CleanupValidatedBeforeGoalRuns() =>
        Raises("setup_call_cleanup(true, throw(never_thrown), _)",
               "instantiation_error");
    [Fact] public void Scc_CleanupRunsExactlyOnce() =>
        True("setup_call_cleanup(true, true, (true ; throw(ran_twice))).");
    [Fact] public void Scc_CleanupCannotRebindTheAnswer() =>
        True("setup_call_cleanup(true, V = won, V = lost), V == won.");

    // ---- Prolog prologue additions: countall, nth0/4, maplist/5..8 ----

    [Fact] public void Countall_CountsAnswers() =>
        True("countall(member(_, [q,w,e]), N), N == 3, "
           + "countall((Q = 1 ; Q = 1), M), M == 2.");
    [Fact] public void Countall_ChecksArgumentsBeforeRunning() =>
        // countall(fail, -3) is the domain error, never a mere failure.
        True("catch(countall(_, _), error(instantiation_error, _), true), "
           + "catch(countall(9, _), error(type_error(callable, 9), _), true), "
           + "catch(countall(fail, -3), error(domain_error(not_less_than_zero, -3), _), true), "
           + "catch(countall(fail, w), error(type_error(integer, w), _), true), "
           + "\\+ countall(fail, 5).");
    [Fact] public void Nth4_SelectsElementAndRest() =>
        True("nth0(1, [q,w,e], E, R), E == w, R == [q,e], "
           + "nth1(3, [q,w,e], F, S), F == e, S == [q,w], "
           + "\\+ nth1(0, [q], _, _).");
    [Fact] public void Nth4_ErrorsAreEager() =>
        True("catch(nth0(x, _, _, _), error(type_error(integer, x), _), true), "
           + "catch(nth1(-2, _, _, _), error(domain_error(not_less_than_zero, -2), _), true).");
    [Fact] public void Nth3_GeneratesPartialLists() =>
        // An integer index against an unbound list EXTENDS it, and a
        // variable index enumerates past any partial prefix ad infinitum.
        True("nth0(2, L, z), L = [_,_,z|_], "
           + "nth1(2, K, y), K = [_,y|_], "
           + "call_nth(nth0(N, _M, _E), 4), N == 3.");
    [Fact] public void Nth3_NegativeIndexIsDomainError() =>
        Raises("nth0(-1, [q], _)", "domain_error(not_less_than_zero, -1)");
    [Fact]
    public void MaplistAndFoldl_HighArities()
    {
        var e = new PrologEngine();
        e.ConsultString(
            "j4(A, B, C, A-B-C).\n"
          + "j5(A, B, C, D, A-B-C-D).\n"
          + "j6(A, B, C, D, E, A-B-C-D-E).\n"
          + "j7(A, B, C, D, E, F, A-B-C-D-E-F).\n"
          + "acc3(X, Y, Z, S0, S) :- S is S0 + X*Y*Z.\n");
        Assert.True(e.Query(
            "maplist(j4, [1], [2], [3], [1-2-3]), "
          + "maplist(j5, [1], [2], [3], [4], [1-2-3-4]), "
          + "maplist(j6, [1], [2], [3], [4], [5], [1-2-3-4-5]), "
          + "maplist(j7, [1,9], [2,8], [3,7], [4,6], [5,5], [6,4], R), "
          + "R == [1-2-3-4-5-6, 9-8-7-6-5-4], "
          + "foldl(acc3, [1,2], [3,4], [5,6], 0, S), S == 63.").Success);
    }

    // ---- write_term/2 variable_names ----

    [Fact] public void WriteTerm_VariableNameWrittenVerbatim() =>
        True("with_output_to(atom(A), "
           + "write_term(T, [quoted(true), variable_names(['Q9'=T])])), "
           + "A == 'Q9'.");
    [Fact] public void WriteTerm_FirstMatchingPairWins() =>
        True("V = W, with_output_to(atom(A), "
           + "write_term(f(V, W), [variable_names(['P'=V, 'R'=W])])), "
           + "A == 'f(P,P)'.");
    [Fact] public void WriteTerm_BadOptionValueIsDomainError() =>
        Raises("write_term(x, [variable_names(gibberish)])",
               "domain_error(write_option, variable_names(gibberish))");
    [Fact] public void WriteTerm_PartialOptionListIsInstantiationError() =>
        Raises("write_term(x, [variable_names([_|_])])", "instantiation_error");
}
