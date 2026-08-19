using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// Third round of Logtalk-battery-driven ISO conformance: §7.6.2 body
/// conversion, format/2's directive set and column engine, the
/// partial-list check on every solutions/output argument, and the
/// argument-validation sweep over open/4's option values, put_char/put_code,
/// current_input/current_output, halt/1, current_prolog_flag/2 and clause/2.
/// </summary>
public class BatteryRoundThreeConformance
{
    private static void Succeeds(string query)
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query(query).Success, $"Query failed: {query}");
    }

    private static string Formatted(string directives, string args)
    {
        var engine = new PrologEngine();
        var sol = engine.Query(
            $"format_to_atom(A, '{directives}', {args}).");
        Assert.True(sol.Success, $"format failed: {directives}");
        return ((Shumway.Compiler.Ast.AtomTerm)sol["A"]!).Name;
    }

    // ---------- §7.6.2 body conversion ----------

    [Fact]
    public void BodyConversion_NumberInGoalPosition_RaisesBeforeRunning()
    {
        // The conversion happens BEFORE the body runs, so the leading `fail`
        // never gets to decide the outcome. The culprit is the whole
        // construct (GNU agrees); the battery accepts the subterm too.
        Succeeds("catch(call((fail, 3)), "
            + "error(type_error(callable, (fail, 3)), _), true).");
        Succeeds("catch(call((fail -> 3)), "
            + "error(type_error(callable, (fail -> 3)), _), true).");
    }

    [Fact]
    public void BodyConversion_ReachedThroughCallN_Raises()
    {
        // call(',', fail, X) builds the conjunction at RUNTIME; the static
        // call/N rewrite must not inline it and skip the conversion.
        Succeeds("X = 3, catch(call(',', fail, X), "
            + "error(type_error(callable, (fail, 3)), _), true).");
        Succeeds("X = 3, catch(call(';', !, X), "
            + "error(type_error(callable, (! ; 3)), _), true).");
    }

    [Fact]
    public void CallN_BuiltConjunction_KeepsMetacalledCutLocal()
    {
        // The counterpart: a call/N-built conjunction whose arguments are all
        // callable still inlines, and a `!` reached through a VARIABLE is
        // local to its own metacall — the disjunction keeps its second answer.
        Succeeds("findall(X, call(',', C = (!), (X = 1, C ; X = 2)), L), "
            + "L == [1, 2].");
    }

    // ---------- format/2 directives ----------

    [Fact]
    public void Format_CanonicalAndWriteTermDirectives()
    {
        Assert.Equal(":-(a)", Formatted("~k", "[(:-a)]"));
        Assert.Equal("a+B", Formatted("~W", "[a+'B', []]"));
        Assert.Equal("a+'B'", Formatted("~W", "[a+'B', [quoted(true)]]"));
    }

    [Fact]
    public void Format_FreshLineOnlyWhenNotAtColumnZero()
    {
        Assert.Equal("begin\nend", Formatted("~Nbegin~N~Nend", "[]"));
    }

    [Fact]
    public void Format_StringDirectiveTakesCodesCharsOrAtom()
    {
        Assert.Equal("ABC", Formatted("~s", "[[65,66,67]]"));
        Assert.Equal("ABC", Formatted("~s", "[['A','B','C']]"));
        Assert.Equal("ABC", Formatted("~s", "['ABC']"));
    }

    [Fact]
    public void Format_StringDirectiveNumberIsAFieldWidth()
    {
        // Longer text is cut, shorter text padded out with spaces.
        Assert.Equal("ABC", Formatted("~3s", "[[65,66,67,68,69]]"));
        Assert.Equal("ABC   ", Formatted("~6s", "[[65,66,67]]"));
    }

    [Fact]
    public void Format_GroupedDecimalPutsCountDigitsAfterThePoint()
    {
        Assert.Equal("123,456,789", Formatted("~D", "[123456789]"));
        Assert.Equal("1,234,567.89", Formatted("~2D", "[123456789]"));
    }

    [Fact]
    public void Format_BestFloatUsesCPrintfShape()
    {
        // %g: lower-case marker and a two-digit exponent.
        Assert.Equal("1.23e-06", Formatted("~g", "[0.00000123]"));
        Assert.Equal("3.9e+02", Formatted("~2g", "[392.65]"));
    }

    [Fact]
    public void Format_ColumnFillSpreadsTheRemainderOverTheLastPoints()
    {
        // 7 columns of padding over 4 fill points is 1+2+2+2, not 1+1+1+4.
        Assert.Equal("^     abc  $", Formatted("^~|~t~t~tabc~t~10+$", "[]"));
        Assert.Equal("^ a  b  c  $", Formatted("^~|~ta~tb~tc~t~10+$", "[]"));
    }

    [Fact]
    public void Format_EmptyControlStringWritesNothing()
    {
        // "" is the empty code LIST under double_quotes=codes, not the
        // atom named "[]".
        Assert.Equal("", Formatted("", "[]"));
    }

    [Fact]
    public void Format_ArgumentErrorsCarryTheirCulprit()
    {
        Succeeds("catch(format(42, [42]), error(type_error(atom, 42), _), true).");
        Succeeds("catch(format('~d', 42), error(type_error(list, 42), _), true).");
        Succeeds("catch(format('~c', [a]), "
            + "error(type_error(evaluable, a/0), _), true).");
        Succeeds("catch(format('~*d', [-1, 123]), "
            + "error(domain_error(format_spec, _), _), true).");
    }

    // ---------- partial-list checks on output arguments ----------

    [Fact]
    public void SortFamily_OutputArgumentMustBeAPartialList()
    {
        Succeeds("catch(sort([], 3), error(type_error(list, 3), _), true).");
        Succeeds("catch(msort([a], [a|b]), error(type_error(list, [a|b]), _), true).");
        Succeeds("catch(keysort([a-1], [x|y]), "
            + "error(type_error(list, [x|y]), _), true).");
        Succeeds("catch(term_variables(foo, 3), "
            + "error(type_error(list, 3), _), true).");
    }

    [Fact]
    public void SolutionCollectors_ResultArgumentMustBeAPartialList()
    {
        // Checked BEFORE the goal runs, so a goal with solutions still raises.
        Succeeds("catch(findall(X, (X = 1 ; X = 2), 12), "
            + "error(type_error(list, 12), _), true).");
        Succeeds("catch(bagof(X, X = 1, 12), error(type_error(list, 12), _), true).");
        Succeeds("catch(setof(X, X = 1, 12), error(type_error(list, 12), _), true).");
        Succeeds("catch(findall(X, X = 1, _, 12), "
            + "error(type_error(list, 12), _), true).");
    }

    [Fact]
    public void SolutionCollectors_GroundResultListStillUnifies()
    {
        // A ground list IS a partial list — the check must not reject the
        // common "check the solutions" idiom.
        Succeeds("findall(X, member(X, [1,2]), [1,2]).");
    }

    // ---------- open/4 option values ----------

    [Fact]
    public void OpenOptions_BadValueCarriesTheWholeOption()
    {
        Succeeds("catch(open(foo, write, _, [type(nontype)]), "
            + "error(domain_error(stream_option, type(nontype)), _), true).");
        Succeeds("catch(open(foo, write, _, [alias(1)]), "
            + "error(domain_error(stream_option, alias(1)), _), true).");
        Succeeds("catch(open(foo, write, _, [eof_action(1)]), "
            + "error(domain_error(stream_option, eof_action(1)), _), true).");
    }

    [Fact]
    public void OpenOptions_RepositionIsValidated()
    {
        Succeeds("catch(open(foo, write, _, [reposition(_)]), "
            + "error(instantiation_error, _), true).");
        Succeeds("catch(open(foo, write, _, [reposition(1)]), "
            + "error(domain_error(stream_option, reposition(1)), _), true).");
    }

    // ---------- character output ----------

    [Fact]
    public void PutCharAndPutCode_CarryTheOffendingValue()
    {
        Succeeds("catch(put_char(ty), error(type_error(character, ty), _), true).");
        Succeeds("catch(put_char(1), error(type_error(character, 1), _), true).");
        Succeeds("catch(put_code(ty), error(type_error(integer, ty), _), true).");
        Succeeds("catch(put_code(65.0), error(type_error(integer, 65.0), _), true).");
    }

    [Fact]
    public void CurrentInputOutput_BoundNonStreamIsADomainError()
    {
        Succeeds("catch(current_input(foo), "
            + "error(domain_error(stream, foo), _), true).");
        Succeeds("catch(current_output(foo), "
            + "error(domain_error(stream, foo), _), true).");
        // A variable still just binds.
        Succeeds("current_input(S), nonvar(S).");
    }

    // ---------- halt/1, current_prolog_flag/2, clause/2 ----------

    [Fact]
    public void Halt_ValidatesItsExitCode()
    {
        Succeeds("catch(halt(a), error(type_error(integer, a), _), true).");
        Succeeds("catch(halt(_), error(instantiation_error, _), true).");
    }

    [Fact]
    public void CurrentPrologFlag_ValidatesTheFlagName()
    {
        Succeeds("catch(current_prolog_flag(5, _), "
            + "error(type_error(atom, 5), _), true).");
        Succeeds("catch(current_prolog_flag(1+2, _), "
            + "error(type_error(atom, 1+2), _), true).");
        Succeeds("catch(current_prolog_flag(warning, _), "
            + "error(domain_error(prolog_flag, warning), _), true).");
    }

    [Fact]
    public void Clause_ValidatesHeadAndRefusesBuiltins()
    {
        Succeeds("catch(clause(_, _), error(instantiation_error, _), true).");
        Succeeds("catch(clause(5, _), error(type_error(callable, 5), _), true).");
        Succeeds("catch(clause(atom(_), _), "
            + "error(permission_error(access, private_procedure, atom/1), _), true).");
    }

    // ---------- round four: the loose ends ----------

    [Fact]
    public void NegationKeepsItsGoalsCutLocal()
    {
        // §7.8.9: `\+ G` is `(call(G) -> fail ; true)`, so the cut inside G
        // commits within G only — spliced bare into the helper it used to cut
        // away the very clause that makes the negation succeed.
        Succeeds("\\+ ((!, fail)).");
        Succeeds("findall(X, ((X = 1 ; X = 2), \\+ ((!, fail))), L), L == [1, 2].");
    }

    [Fact]
    public void DeclaredButUndefinedPredicateIsKnown()
    {
        // A `:- discontiguous` / `:- multifile` declaration DEFINES the
        // predicate as far as current_predicate/1 is concerned, and calling it
        // fails instead of raising existence_error.
        var engine = new PrologEngine();
        engine.ConsultString(":- discontiguous(scat/2). :- multifile(mfe/1).");
        Assert.False(engine.Query("scat(_, _).").Success);
        Assert.True(engine.Query("current_predicate(scat/2).").Success);
        Assert.True(engine.Query("current_predicate(mfe/1).").Success);
        // An undeclared, undefined predicate still raises.
        Assert.True(engine.Query(
            "catch(nosuchpred(_), error(existence_error(procedure, _), _), true).").Success);
    }

    [Fact]
    public void ExistentialGoalIsBodyConvertedToo()
    {
        // The ^ wrappers hide the goal from the ordinary §7.6.2 check.
        Succeeds("catch(setof(_X, _A^_A^1, _L), "
            + "error(type_error(callable, 1), _), true).");
    }

    [Fact]
    public void FilesOpenWithoutLockingOtherHandlesOut()
    {
        // No other Prolog locks the files it opens, and programs that reopen a
        // file they already hold open are ordinary.
        Succeeds("atom_concat('shumway_share_', 'test.tmp', F), "
            + "open(F, write, S1), write(S1, a), flush_output(S1), "
            + "open(F, write, S2), write(S2, b), close(S2), close(S1).");
    }

    [Fact]
    public void EnsureLoadedResolvesRelativeToTheLoadingFile()
    {
        // `:- ensure_loaded(dep)` inside dir/main.pl means dir/dep.pl, the
        // same rule `:- include/1` follows.
        string dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "shumway_el_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "dep.pl"), "el_a(1).\n");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "main.pl"),
                ":- ensure_loaded(dep).\nel_c(3).\n:- ensure_loaded(dep).\n");
            var engine = new PrologEngine();
            engine.ConsultFile(System.IO.Path.Combine(dir, "main.pl"));
            Assert.True(engine.Query("findall(X, el_a(X), L), L == [1].").Success);
            Assert.True(engine.Query("el_c(3).").Success);
        }
        finally { System.IO.Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void StreamOptionsRepeatedAndRepositionFalse()
    {
        // A repeated option: the LAST one wins.
        Succeeds("atom_concat('shumway_opt_', 'test.tmp', F), "
            + "open(F, write, S, [type(binary), type(text)]), "
            + "stream_property(S, type(text)), close(S).");
        // reposition(false) refuses the seek and reports itself.
        Succeeds("atom_concat('shumway_rep_', 'test.tmp', F), "
            + "open(F, write, S0, []), close(S0), "
            + "open(F, read, S, [reposition(false)]), "
            + "stream_property(S, reposition(false)), "
            + "catch(set_stream_position(S, 0), "
            + "  error(permission_error(reposition, stream, _), _), true), close(S).");
    }

    [Fact]
    public void MoreArgumentChecks()
    {
        Succeeds("catch(keysort([], [1/a]), error(type_error(pair, 1/a), _), true).");
        Succeeds("catch(predicate_property(atom_chars(_, _), foobar), "
            + "error(domain_error(predicate_property, foobar), _), true).");
        Succeeds("catch(op(100, xfx, [a|_]), error(instantiation_error, _), true).");
        Succeeds("catch(functor(_, t, 18446744073709097979076548489551455463), "
            + "error(representation_error(max_arity), _), true).");
        Succeeds("atomic_concat(foo, 42, A), A == foo42.");
        Succeeds("atomic_list_concat([a, b], 42, X), X == a42b.");
        Succeeds("current_prolog_flag(min_integer, Lo), Lo < 0, "
            + "current_prolog_flag(max_integer, Hi), Hi > 0.");
    }

    [Fact]
    public void UnterminatedBlockCommentIsASyntaxError()
    {
        Succeeds("catch(atom_to_term('/* unterminated', _, _), "
            + "error(syntax_error(_), _), true).");
    }

    // ---------- §7.6.2 clause conversion at store time ----------

    [Fact]
    public void StoredClauseBodyIsTheConvertedOne()
    {
        // A clause enters the database converted, so a variable in goal
        // position comes back out of clause/2 as call(V). Both the asserted
        // and the source-declared route (GNU and SWI agree exactly).
        var engine = new PrologEngine();
        engine.ConsultString(":- dynamic(legs/2).\nlegs(A, 7) :- A, call(A).\n");
        Assert.True(engine.Query(
            "clause(legs(_, 7), B), B = ','(call(X), call(X)).").Success);
        Assert.True(engine.Query(
            "assertz((q(X) :- X, call(X))), clause(q(_), B), "
            + "B = ','(call(Y), call(Y)).").Success);
    }

    [Fact]
    public void ConversionDescendsTheControlSkeletonOnly()
    {
        // §7.6.2 covers ','/2, ';'/2 and '->'/2. `\+` is NOT converted —
        // GNU Prolog, the reference here, leaves it alone (SWI does convert
        // it, and *-> too).
        Succeeds("assertz((r1(X) :- (X ; true))), clause(r1(_), B), "
            + "B = ';'(call(_), true).");
        Succeeds("assertz((r2(X,Y,Z) :- (X -> Y ; Z))), clause(r2(_,_,_), B), "
            + "B = ';'('->'(call(_), call(_)), call(_)).");
        Succeeds("assertz((r3(X) :- \\+ X)), clause(r3(_), B), "
            + "B = \\+(V), var(V).");
        // A non-callable body is still rejected outright.
        Succeeds("catch(assertz((r4 :- 3)), error(type_error(callable, 3), _), true).");
    }

    [Fact]
    public void RetractMatchesTheConvertedFormAndTheWrittenOne()
    {
        // The pattern goes through the same conversion, so the text that
        // assertz stored is the text retract takes back…
        Succeeds("assertz((z1(X) :- X, call(X))), retract((z1(Y) :- Y, call(Y))).");
        // …the converted form matches too (the clause/2 round trip)…
        Succeeds("assertz((z2(X) :- X, call(X))), clause(z2(W), B), "
            + "retract((z2(W) :- B)).");
        // …and a body that is a bare VARIABLE stays the pattern's wildcard,
        // not a goal to convert: it must still match anything.
        Succeeds("assertz((z3(X) :- X, call(X))), retract((z3(_) :- Body)), "
            + "Body = ','(call(_), call(_)).");
        Succeeds("assertz((z4 :- true)), retract((z4 :- B)), B == true.");
    }

    [Fact]
    public void PrintOnAStreamExists()
    {
        Succeeds("format_to_atom(A, '~w', [ok]), A == ok.");
        var engine = new PrologEngine();
        Assert.True(engine.Query("current_output(S), print(S, hello).").Success);
    }
}
