using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 439 (Phase 30) — four Arity-corpus parser/transform fixes:
/// <list type="number">
/// <item>A QUOTED <c>'!'</c> after <c>[</c> is an ordinary list element,
/// never the chunk-263 snip opener — <c>['!', Token]</c> is a
/// two-element list. Bare <c>[! G !]</c> snips unchanged.</item>
/// <item>Backquote char-code literals under arity_compat take the next
/// character LITERALLY (no 0'-style escape processing — revises chunk
/// 437): <c>`\</c> is 92, <c>`)</c> is 41, backquote + space is 32.
/// A backquote at end of input / before a line break is an error
/// diagnostic.</item>
/// <item>A trailing comma before <c>)</c> in a compound argument list
/// is tolerated under arity_compat ONLY (subviews.pl writes
/// <c>ifthenelse(..., save_old_mod,  % comment
/// )</c>). Flag off: still a syntax error.</item>
/// <item>DCG double-quoted string terminals (standard DCG, NOT
/// arity-gated): a <c>"ab"</c> body terminal consumes the equivalent
/// element list per the active double_quotes mode (codes / chars expand
/// at parse time; the default `string` mode expands in DcgTransform to
/// character codes, PSTR's native representation); <c>""</c> consumes
/// nothing.</item>
/// </list>
/// </summary>
public class Chunk439Tests
{
    // ------------------------------------------------------------------
    // 1. Quoted '!' is a list element, not a snip opener
    // ------------------------------------------------------------------

    [Fact]
    public void QuotedBang_AsFirstListElement_IsPlainList_FlagOn()
    {
        var e = new PrologEngine();
        // The arity.pl / arv2.pl corpus shape: concat(['!', Token], N).
        e.ConsultString("""
            :- set_prolog_flag(arity_compat, true).
            l(['!', x]).
            """);
        var s = e.Query("l([A, B]).");
        Assert.True(s.Success);
        Assert.True(e.Query("l(['!', x]).").Success);
        Assert.True(e.Query("l(L), L = [F | _], F == '!'.").Success);
        Assert.True(e.Query("l(L), length(L, 2).").Success);
    }

    [Fact]
    public void QuotedBang_UsedInAtomConcatShape_FlagOn()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- set_prolog_flag(arity_compat, true).
            bang(N, T) :- atomic_list_concat(['!', T], N).
            """);
        var s = e.Query("bang(N, foo).");
        Assert.True(s.Success);
        Assert.Equal("!foo", s["N"]!.ToString());
    }

    [Fact]
    public void BareSnip_StillDesugarsToOnce_FlagOn()
    {
        var e = new PrologEngine();
        // Snip semantics unchanged: internal backtracking allowed, choice
        // points pruned on exit — so findall sees exactly one solution.
        e.ConsultString("""
            :- set_prolog_flag(arity_compat, true).
            s(X) :- [! member(X, [1, 2, 3]), X > 1 !].
            """);
        Assert.True(e.Query("s(2).").Success);
        Assert.True(e.Query("findall(X, s(X), [2]).").Success);
    }

    [Fact]
    public void QuotedBang_ListElement_FlagOff()
    {
        var e = new PrologEngine();
        // Not arity-gated: a quoted '!' after '[' is a list element in
        // every mode (the snip parse itself is what chunk 263 added
        // unconditionally; the WasQuoted guard applies equally).
        e.ConsultString("l(['!', y]).");
        Assert.True(e.Query("l(['!', y]).").Success);
    }

    // ------------------------------------------------------------------
    // 2. Backquote literals take the next char literally (flag on)
    // ------------------------------------------------------------------

    [Fact]
    public void Backquote_Backslash_Is92_FlagOn()
    {
        var e = new PrologEngine();
        // Source: c1(`\).  — the char after ` is taken literally, so the
        // backslash IS the literal (no escape sequence starts).
        e.ConsultString("""
            :- set_prolog_flag(arity_compat, true).
            c1(`\).
            """);
        Assert.True(e.Query("c1(92).").Success);
    }

    [Fact]
    public void Backquote_CloseParen_Is41_FlagOn()
    {
        var e = new PrologEngine();
        // Source: c2(`)).  — the first `)` is the literal (41), the
        // second closes the argument list.
        e.ConsultString("""
            :- set_prolog_flag(arity_compat, true).
            c2(`)).
            """);
        Assert.True(e.Query("c2(41).").Success);
    }

    [Fact]
    public void Backquote_Space_Is32_FlagOn()
    {
        var e = new PrologEngine();
        // Source: c3(` ).  — backquote followed by a space is code 32.
        e.ConsultString("""
            :- set_prolog_flag(arity_compat, true).
            c3(` ).
            """);
        Assert.True(e.Query("c3(32).").Success);
    }

    [Fact]
    public void Backquote_DoubleQuote_Is34_FlagOn()
    {
        var e = new PrologEngine();
        // The prospec3.pl shape: edit_format1(..., `", ...) — a
        // backquoted double-quote character, code 34.
        e.ConsultString("""
            :- set_prolog_flag(arity_compat, true).
            c4(`").
            """);
        Assert.True(e.Query("c4(34).").Success);
    }

    [Fact]
    public void Backquote_BeforeLineBreak_ErrorDiagnosticNotCrash()
    {
        var r = ShmoCompiler.TryCompileSource(
            ":- set_prolog_flag(arity_compat, true).\nc(`\n).\n",
            arityCompat: true);
        Assert.False(r.Success);
        Assert.Contains(r.Errors,
            err => err.Message.Contains("line break"));
    }

    [Fact]
    public void Backquote_AtEndOfInput_ErrorDiagnosticNotCrash()
    {
        var r = ShmoCompiler.TryCompileSource(
            "c(`",
            arityCompat: true);
        Assert.False(r.Success);
        Assert.Contains(r.Errors,
            err => err.Message.Contains("Unterminated `"));
    }

    // ------------------------------------------------------------------
    // 3. Trailing comma before `)` (arity_compat only)
    // ------------------------------------------------------------------

    [Fact]
    public void TrailingCommaBeforeRParen_SubviewsShape_FlagOn()
    {
        var e = new PrologEngine();
        // The subviews.pl construct (line 2069): the third argument of
        // ifthenelse/3 is followed by a comma, a % comment, and then the
        // closing paren on the next line.
        e.ConsultString(":- set_prolog_flag(arity_compat, true).\n" +
            "ifthenelse(C, T, _) :- C, !, T.\n" +
            "ifthenelse(_, _, E) :- E.\n" +
            "save_old_mod.\n" +
            "go(Type) :-\n" +
            "\tifthenelse(Type = filtered,\n" +
            "\t\ttrue,\n" +
            "\t\tsave_old_mod,%comment text\n" +
            "\t),\n" +
            "\t!.\n");
        Assert.True(e.Query("go(filtered).").Success);
        Assert.True(e.Query("go(other).").Success);
    }

    [Fact]
    public void TrailingCommaBeforeRParen_SimpleFact_FlagOn()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- set_prolog_flag(arity_compat, true).
            f(a, b, ).
            """);
        // The dangling comma is dropped: f/2, not f/3.
        Assert.True(e.Query("f(a, b).").Success);
        Assert.False(e.Query("current_predicate(f/3).").Success);
    }

    [Fact]
    public void TrailingCommaBeforeRParen_StillSyntaxError_FlagOff()
    {
        var r = ShmoCompiler.TryCompileSource(
            "f(a, b, ).\n",
            arityCompat: false);
        Assert.False(r.Success);
        Assert.NotEmpty(r.Errors);
    }

    [Fact]
    public void TrailingCommaInList_StillSyntaxError_FlagOn()
    {
        // The tolerance is the narrowest the corpus needs: argument
        // lists only — a trailing comma in a [list] stays an error even
        // under the flag.
        var r = ShmoCompiler.TryCompileSource(
            ":- set_prolog_flag(arity_compat, true).\ng([a, b, ]).\n",
            arityCompat: true);
        Assert.False(r.Success);
        Assert.NotEmpty(r.Errors);
    }

    // ------------------------------------------------------------------
    // 4. DCG double-quoted string terminals (standard DCG, no flag)
    // ------------------------------------------------------------------

    [Fact]
    public void DcgStringTerminal_CodesMode()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- set_prolog_flag(double_quotes, codes).
            ab --> "ab".
            """);
        Assert.True(e.Query("phrase(ab, [97, 98]).").Success);
        Assert.False(e.Query("phrase(ab, [97]).").Success);
    }

    [Fact]
    public void DcgStringTerminal_CharsMode()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- set_prolog_flag(double_quotes, chars).
            ab --> "ab".
            """);
        Assert.True(e.Query("phrase(ab, [a, b]).").Success);
        Assert.False(e.Query("phrase(ab, [a, c]).").Success);
    }

    [Fact]
    public void DcgStringTerminal_DefaultMode_MatchesChars()
    {
        var e = new PrologEngine();
        // A terminal expands to the literal's OWN presentation, which under the
        // default is chars (ADR-047). It used to expand to codes whatever the
        // literal was, so a grammar written under `chars` matched nothing.
        e.ConsultString("ab --> \"ab\".");
        Assert.True(e.Query("phrase(ab, [a, b]).").Success);
        Assert.False(e.Query("phrase(ab, [b, a]).").Success);
        Assert.False(e.Query("phrase(ab, [97, 98]).").Success);
    }

    [Fact]
    public void DcgStringTerminal_EmptyString_ConsumesNothing()
    {
        var e = new PrologEngine();
        // "" is the empty terminal: S0 = S. Default (string) mode.
        e.ConsultString("""
            e --> "".
            wrap --> e, "x", e.
            """);
        Assert.True(e.Query("phrase(e, []).").Success);
        Assert.True(e.Query("phrase(e, [a], [a]).").Success);
        Assert.True(e.Query("phrase(wrap, [120]).").Success);
    }

    [Fact]
    public void DcgStringTerminal_MidBody_ThreadsDiffList()
    {
        var e = new PrologEngine();
        // The prospec3.pl shape: a string terminal followed by more body.
        e.ConsultString("""
            :- set_prolog_flag(double_quotes, codes).
            pct(P) --> "%", [P].
            """);
        var s = e.Query("phrase(pct(P), [37, 100]).");
        Assert.True(s.Success);
        Assert.Equal("100", s["P"]!.ToString());
    }
}
