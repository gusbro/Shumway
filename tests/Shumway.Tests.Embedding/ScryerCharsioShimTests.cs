using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The Scryer shim's charsio reader/writer natives, exercised
/// self-contained: a minimal scryer-dialect stand-in library drives
/// <c>'$read_from_chars'</c>, <c>'$write_term_to_chars'</c>,
/// <c>'$read_term_from_chars'</c> and the <c>builtins:</c> option parsers
/// exactly the way Scryer's charsio.pl does (the qualified
/// <c>builtins:parse_write_options</c> call included — no builtins module
/// exists, so it falls through to the shim's bare global). The real-tree
/// suite is the opt-in ScryerEndToEndValidation; this is the gate's own
/// coverage.</summary>
public sealed class ScryerCharsioShimTests : IDisposable
{
    private readonly string _dir;
    private readonly PrologEngine _e;

    public ScryerCharsioShimTests()
    {
        _dir = Path.Combine(Path.GetTempPath(),
            "shumway-scryshim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "minicharsio.pl"), """
            :- module(minicharsio, [rfc/2, wtc/3, rtfc/3]).
            rfc(Cs, T) :- '$read_from_chars'(Cs, T).
            wtc(T, Options, Cs) :-
                builtins:parse_write_options(Options,
                    [DQ, IO, MD, NV, Q, VN], wtc/3),
                '$write_term_to_chars'(Cs, T, IO, NV, Q, VN, MD, DQ).
            rtfc(Cs, T, Options) :-
                builtins:parse_read_term_options(Options, [S, VN, Vs], rtfc/3),
                '$read_term_from_chars'(Cs, T, S, Vs, VN).
            """);
        _e = new PrologEngine();
        _e.AddLibraryDirectorySpec("scryer:" + _dir.Replace('\\', '/'));
        _e.ConsultString(":- use_module(library(minicharsio)).");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ReadFromChars_ParsesATerm()
    {
        Assert.True(_e.Query(
            "atom_chars('f(x, [1|Y]).', Cs), rfc(Cs, T), T = f(x, [1|_]).").Success);
    }

    [Fact]
    public void ReadFromChars_SyntaxError_IsCatchable()
    {
        // The message is OUR reader's (not Scryer's error vocabulary), but the
        // syntax_error(_) shape is, so a generic catcher works.
        Assert.True(_e.Query(
            "atom_chars('f(x,', Cs), catch(rfc(Cs, _), error(syntax_error(_), _), true).")
            .Success);
    }

    [Fact]
    public void WriteTermToChars_QuotedRoundTrip()
    {
        // quoted(true) output reads back as the same term.
        Assert.True(_e.Query(
            "wtc(f('A b'), [quoted(true)], Cs), "
            + "append(Cs, ['.'], Cs1), rfc(Cs1, T), T == f('A b').").Success);
    }

    [Fact]
    public void WriteTermToChars_Options()
    {
        // variable_names names the variable; numbervars renders '$VAR'(0).
        Assert.True(_e.Query(
            "wtc(f(X), [variable_names(['Alpha'=X])], Cs), atom_chars(A, Cs), "
            + "A == 'f(Alpha)'.").Success);
        Assert.True(_e.Query(
            "wtc('$VAR'(0), [numbervars(true)], Cs), Cs == ['A'].").Success);
        // Defaults: unquoted.
        Assert.True(_e.Query(
            "wtc('A b', [], Cs), atom_chars(A, Cs), A == 'A b'.").Success);
    }

    [Fact]
    public void ParseWriteOptions_RejectsBogusOptions_ScryerShape()
    {
        Assert.True(_e.Query(
            "catch(wtc(x, [bogus(true)], _), "
            + "error(domain_error(write_option, bogus(true)), _), true).").Success);
        Assert.True(_e.Query(
            "catch(wtc(x, Options, _), error(instantiation_error, _), true).").Success);
    }

    [Fact]
    public void ReadTermFromChars_FillsTheOptionSlots()
    {
        Assert.True(_e.Query(
            "atom_chars('f(X, Y, X).', Cs), "
            + "rtfc(Cs, T, [variable_names(VN), variables(Vs), singletons(S)]), "
            + "T = f(A, B, A2), A == A2, "
            + "VN = ['X'=_, 'Y'=_], Vs = [_, _], S = ['Y'=SV], SV == B.").Success);
    }
}
