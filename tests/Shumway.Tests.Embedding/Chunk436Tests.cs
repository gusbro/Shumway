using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 436 (Phase 30) — three Arity-compat compiler robustness pieces,
/// driven by the real Arity corpus (anstring.pl / arity.pl):
/// <list type="number">
/// <item>Unknown directives (e.g. <c>:- extrn foo/3:far.</c>) are a WARNING
/// under <c>arity_compat</c> — the compile continues and succeeds; the
/// <see cref="ShmoCompiler.SilentlyIgnoredDirectives"/> set suppresses
/// named ones entirely. Without the flag behaviour is unchanged.</item>
/// <item><c>:- c.</c> switches to a native-code (C) section skipped RAW
/// until a line with <c>:- prolog.</c> (whitespace-tolerant) or EOF;
/// <c>:- prolog.</c> in normal mode is a silent no-op.</item>
/// <item>Crash fix (unconditional): a LexerException (e.g. Arity's
/// backquote char literals) escaping <c>ReadAllCollectingErrors</c> —
/// including from the resync itself and from the at-end peek — is now a
/// captured error diagnostic, never an unhandled exception.</item>
/// </list>
/// </summary>
public class Chunk436Tests
{
    // ------------------------------------------------------------------
    // 1. Unknown directives → warning under arity_compat
    // ------------------------------------------------------------------

    [Fact]
    public void UnknownDirective_WarnsUnderFlag_CompileSucceeds()
    {
        // Chunk 437 seeds 'extrn' into SilentlyIgnoredDirectives; pull it
        // out for the duration so this test still exercises the warning
        // path with the operator-form directive.
        bool removed = ShmoCompiler.SilentlyIgnoredDirectives.Remove("extrn");
        try
        {
            var r = ShmoCompiler.TryCompileSource(
                ":- extrn concat_l/3:far,\n" +
                "         on/2:far.\n" +
                "p(1).\n",
                arityCompat: true);
            Assert.True(r.Success);
            Assert.NotNull(r.Object);
            var warning = Assert.Single(r.Warnings);
            Assert.Equal("unknown directive 'extrn' ignored (arity_compat)",
                warning.Message);
            Assert.Equal(1, warning.Line);
        }
        finally
        {
            if (removed) ShmoCompiler.SilentlyIgnoredDirectives.Add("extrn");
        }
    }

    [Fact]
    public void UnknownDirective_AtomForm_WarnsToo()
    {
        var r = ShmoCompiler.TryCompileSource(
            ":- disable_debug.\n:- disable_functor_map(is/2).\np(1).\n",
            arityCompat: true);
        Assert.True(r.Success);
        Assert.Equal(2, r.Warnings.Count);
        Assert.Contains(r.Warnings, w => w.Message.Contains("'disable_debug'"));
        Assert.Contains(r.Warnings, w => w.Message.Contains("'disable_functor_map'"));
    }

    [Fact]
    public void UnknownDirective_ErrorWithoutFlag()
    {
        // `extrn` is not an operator outside arity_compat, so the
        // directive doesn't even parse — current behaviour unchanged.
        var r = ShmoCompiler.TryCompileSource(
            ":- extrn concat_l/3:far.\np(1).\n",
            arityCompat: false);
        Assert.False(r.Success);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void RecognizedDirectives_DoNotWarn()
    {
        var r = ShmoCompiler.TryCompileSource(
            ":- public p/1:far.\n" +
            ":- visible v/1.\n" +
            ":- mode p(+).\n" +
            ":- discontiguous q/1.\n" +
            "p(1).\nq(a).\n",
            arityCompat: true);
        Assert.True(r.Success);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void SilentlyIgnoredDirectives_SuppressesTheWarning()
    {
        // Chunk 437: 'extrn' ships in the set out of the box, so the
        // suppression needs no caller setup.
        Assert.Contains("extrn", ShmoCompiler.SilentlyIgnoredDirectives);
        var r = ShmoCompiler.TryCompileSource(
            ":- extrn foo/1:far.\np(1).\n",
            arityCompat: true);
        Assert.True(r.Success);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void SilentlyIgnoredDirectives_CallerAddedNameSuppressesToo()
    {
        Assert.True(ShmoCompiler.SilentlyIgnoredDirectives.Add("disable_debug"));
        try
        {
            var r = ShmoCompiler.TryCompileSource(
                ":- disable_debug.\np(1).\n",
                arityCompat: true);
            Assert.True(r.Success);
            Assert.Empty(r.Warnings);
        }
        finally
        {
            ShmoCompiler.SilentlyIgnoredDirectives.Remove("disable_debug");
        }
    }

    // ------------------------------------------------------------------
    // 2. :- c. / :- prolog. native-code sections
    // ------------------------------------------------------------------

    [Fact]
    public void CSection_SkippedRaw_CodeAfterPrologCompiles()
    {
        // The C body is deliberately un-lexable as Prolog (backquotes,
        // unbalanced quotes) — it must be skipped raw, not tokenized.
        var r = ShmoCompiler.TryCompileSource(
            "a(1).\n" +
            ":- c.\n" +
            "typedef char *pchar;\n" +
            "void f(int x) { char c = `x'; /* don't */ }\n" +
            ":- prolog.\n" +
            "b(2).\n",
            arityCompat: true);
        Assert.True(r.Success);
        Assert.Empty(r.Warnings);
        var defined = r.Object!.Defined.Select(d => d.Indicator.ToString()).ToList();
        Assert.Contains("a/1", defined);
        Assert.Contains("b/1", defined);
    }

    [Fact]
    public void CSection_ToEof_EndsModuleNormally()
    {
        var r = ShmoCompiler.TryCompileSource(
            "a(1).\n" +
            ":- c.\n" +
            "extern unsigned long ulNRlips;\n" +
            "int main() { return `q'; }\n",   // no :- prolog. — runs to EOF
            arityCompat: true);
        Assert.True(r.Success);
        var defined = r.Object!.Defined.Select(d => d.Indicator.ToString()).ToList();
        Assert.Contains("a/1", defined);
        Assert.Single(defined);
    }

    [Fact]
    public void CSection_MultipleAlternatingSections()
    {
        var r = ShmoCompiler.TryCompileSource(
            ":- c.\nvoid one(void);\n:- prolog.\n" +
            "a(1).\n" +
            ":-c.\nint two; `\n:-prolog.\n" +        // Arity's no-space form
            "b(2).\n" +
            ":- c.\nchar three;\n  :-  prolog .\n" + // whitespace variations
            "c(3).\n",
            arityCompat: true);
        Assert.True(r.Success);
        var defined = r.Object!.Defined.Select(d => d.Indicator.ToString()).ToList();
        Assert.Contains("a/1", defined);
        Assert.Contains("b/1", defined);
        Assert.Contains("c/1", defined);
    }

    [Fact]
    public void PrologDirective_NoOpInNormalMode()
    {
        var r = ShmoCompiler.TryCompileSource(
            "a(1).\n:- prolog.\nb(2).\n",
            arityCompat: true);
        Assert.True(r.Success);
        Assert.Empty(r.Warnings);   // not reported as unknown
        var defined = r.Object!.Defined.Select(d => d.Indicator.ToString()).ToList();
        Assert.Contains("a/1", defined);
        Assert.Contains("b/1", defined);
    }

    [Fact]
    public void CSection_OffWithoutFlag_ErrorsNotCrash()
    {
        // Without arity_compat the section markers mean nothing; the C
        // text hits the parser and produces diagnostics — but never an
        // unhandled exception.
        var r = ShmoCompiler.TryCompileSource(
            ":- c.\nvoid f(int x) { char c = `x'; }\n:- prolog.\nb(2).\n",
            arityCompat: false);
        Assert.False(r.Success);
        Assert.NotEmpty(r.Errors);
    }

    [Fact]
    public void CSection_WorksThroughEngineConsult()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            :- set_prolog_flag(arity_compat, true).
            a(1).
            :- c.
            unsigned long strlen(const char* s);
            :- prolog.
            b(2).
            """);
        Assert.True(e.Query("a(1), b(2).").Success);
    }

    // ------------------------------------------------------------------
    // 3. Crash regression — lexer errors are diagnostics, not crashes
    // ------------------------------------------------------------------

    [Fact]
    public void LexerError_Backquote_IsErrorDiagnosticNotCrash()
    {
        // The arity.pl crash shape: Arity's backquote char-code literal
        // (`x) threw a LexerException that escaped the error-recovery
        // resync entirely. Without the flag it is still an unlexable
        // character — but recovered as a diagnostic, never a crash.
        // (Chunk 437: WITH the flag `x now lexes as a char-code integer
        // and the source compiles — see Chunk437Tests.)
        var r = ShmoCompiler.TryCompileSource(
            "p(L) :- not(L = [_, `x|_]).\n" +
            "q(1).\n",
            arityCompat: false);
        Assert.False(r.Success);
        Assert.Contains(r.Errors, err => err.Message.Contains("Unexpected character '`'"));

        var on = ShmoCompiler.TryCompileSource(
            "p(L) :- not(L = [_, `x|_]).\n" +
            "q(1).\n",
            arityCompat: true);
        Assert.True(on.Success);
    }

    [Fact]
    public void LexerError_AfterClauseEnd_IsErrorNotCrash()
    {
        // The bad character sits where the reader's at-end peek (not a
        // clause parse) hits it — the second escape path.
        var r = ShmoCompiler.TryCompileSource("a(1). ` \nb(2).\n");
        Assert.False(r.Success);
        Assert.Contains(r.Errors, err => err.Message.Contains("Unexpected character '`'"));
    }

    [Fact]
    public void LexerError_ConsecutiveBadChars_RecoveryStillProgresses()
    {
        // A run of unlexable characters inside one clause must not hang
        // the resync; the next clause is still reached.
        var r = ShmoCompiler.TryCompileSource(
            "p :- x = ``` .\nq(ok).\n");
        Assert.False(r.Success);
        Assert.NotEmpty(r.Errors);
        // Bounded — far fewer than maxErrors, and the call returned.
        Assert.True(r.Errors.Count < 100);
    }

    [Fact]
    public void LexerError_UnterminatedQuoteAtEof_Terminates()
    {
        var r = ShmoCompiler.TryCompileSource("p('never closed.\n");
        Assert.False(r.Success);
        Assert.NotEmpty(r.Errors);
    }
}
