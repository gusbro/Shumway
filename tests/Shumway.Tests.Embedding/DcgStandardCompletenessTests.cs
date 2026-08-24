using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Standard-DCG completeness surfaced by the Djota (Scryer/Trealla) corpus:
/// variable non-terminal bodies, semicontext / pushback heads, <c>|/2</c>
/// disjunction, the runtime <c>phrase/2,3</c> interpreter, runtime module
/// qualification <c>:/2</c>, and the fail-fast lowering that defers a
/// terminal-led rule's output construction so failed alternatives don't build
/// their output structure.
/// </summary>
public class DcgStandardCompletenessTests
{
    // ---- variable non-terminal body (`--> V`) ----

    [Fact]
    public void Dcg_VariableBody_CallsPhraseAtRuntime()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public emit/3.
            emit(X) --> X.
            """);   // X is a variable non-terminal (a terminal list here)
        // emit([a,b], S0, S) means: phrase([a,b], S0, S) → S0 = [a,b|S].
        Assert.True(engine.Query("emit([a, b], [a, b, c], [c]).").Success);
        Assert.False(engine.Query("emit([a, b], [a, x, c], [c]).").Success);
    }

    // ---- semicontext / pushback head (`H, PB --> B`) ----

    [Fact]
    public void Dcg_SemicontextHead_PushesTokenBack()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public peek/3.
            peek(T), [T] --> [T].
            """);   // lookahead: consume T then push it back
        // peek(X, [a,b], R): X = a and R = [a,b] (nothing actually consumed).
        Assert.True(engine.Query("peek(X, [a, b], _), X == a.").Success);
        Assert.True(engine.Query("peek(_, [a, b], R), R == [a, b].").Success);
    }

    // ---- `|/2` as DCG disjunction ----

    [Fact]
    public void Dcg_PipeDisjunction_ParsesEitherBranch()
    {
        var engine = new PrologEngine();
        // Default chars mode (ADR-047): "\n" is a DCG terminal for the
        // CHAR list ['\n'] — the literal's own presentation, like every
        // other terminal position.
        engine.ConsultString("""
            :- public nl_seq/2.
            nl_seq --> "\n" | "\r" | "\r\n".
            """);
        Assert.True(engine.Query("nl_seq(['\\n'], []).").Success);
        Assert.True(engine.Query("nl_seq(['\\r'], []).").Success);
        Assert.True(engine.Query("nl_seq(['\\r', '\\n'], []).").Success);
        Assert.False(engine.Query("nl_seq([' '], []).").Success);
    }

    // ---- runtime phrase/2,3 over a variable / control-construct body ----

    [Fact]
    public void Phrase_RuntimeInterpreter_HandlesVariableAndControlBody()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public ab/2.
            ab --> [a], [b].
            """);
        // phrase/2 with a statically-unknown (variable) body.
        Assert.True(engine.Query("G = ab, phrase(G, [a, b]).").Success);
        // phrase/3 over a conjunction body built at runtime.
        Assert.True(engine.Query("G = (ab, ab), phrase(G, [a, b, a, b], []).").Success);
        // phrase over a list-terminal body.
        Assert.True(engine.Query("phrase([a, b], [a, b]).").Success);
    }

    // ---- runtime module-qualified call `:/2` ----

    [Fact]
    public void ModuleQualifiedCall_IsTransparentOverPublicPredicate()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public greet/1.
            greet(hello).
            """);
        Assert.True(engine.Query("user:greet(hello).").Success);
        Assert.False(engine.Query("user:greet(bye).").Success);
    }

    // ---- char_type(_, decimal_digit) ----

    [Fact]
    public void CharType_DecimalDigit_Recognised()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query("char_type('7', decimal_digit).").Success);
        Assert.False(engine.Query("char_type(a, decimal_digit).").Success);
    }

    // ---- fail-fast lowering: terminal-led rule defers output construction ----

    [Fact]
    public void FailFast_TerminalLedRule_StillParsesAndBuildsOutput()
    {
        var engine = new PrologEngine();
        engine.ConsultString(
            ":- public node/3.\n" +
            "node(open(X)) --> [o], [X].\n" +   // terminal-led, compound output
            "node(close) --> [c].\n");
        // Output built correctly after the input matches.
        Assert.True(engine.Query("node(N, [o, hi], []), N == open(hi).").Success);
        Assert.True(engine.Query("node(close, [c], []).").Success);
        // A non-matching input fails (and, per the lowering, without building
        // the open(_) structure — observable only as correctness here).
        Assert.False(engine.Query("node(_, [x], []).").Success);
    }

    [Fact]
    public void FailFast_RenderRule_WithBraceLeadingGoal_StillWorks()
    {
        // A `{ }`-led (render-direction) rule must be unaffected by the
        // fail-fast lowering — its head arg stays in place.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public render/3.
            render(n(V)) --> { W is V + 1 }, [W].
            """);
        Assert.True(engine.Query("render(n(41), [42], []).").Success);
        Assert.False(engine.Query("render(n(41), [43], []).").Success);
    }

    [Fact]
    public void NonLeadingStringTerminal_MatchesTheLiteralsOwnPresentation()
    {
        // Since ADR-047 a "..." literal survives as a StringTerm in every
        // double_quotes mode, carrying its presentation kind. A NON-leading
        // terminal (after a nonterminal — the leading one is hoisted into
        // the head by the fail-fast lowering) must consume elements of that
        // kind: hardcoding codes made Trealla json's `"{", ws, "}"` fail
        // silently against chars input.
        var engine = new PrologEngine();
        engine.ConsultString("""
            ws --> [].
            brk([]) --> "[", ws, "]".
            """);
        Assert.True(engine.Query("phrase(brk([]), \"[]\").").Success);
        Assert.True(engine.Query("phrase(brk([]), ['[', ']']).").Success);
        // And under codes the same rule consumes codes.
        var e2 = new PrologEngine();
        e2.ConsultString("""
            :- set_prolog_flag(double_quotes, codes).
            ws2 --> [].
            brk2([]) --> "[", ws2, "]".
            """);
        Assert.True(e2.Query("phrase(brk2([]), [0'[, 0']]).").Success);
    }
}
