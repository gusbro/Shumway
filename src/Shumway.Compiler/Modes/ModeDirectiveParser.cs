using System.Collections.Generic;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Compiler.Modes;

/// <summary>
/// Parses the body of a <c>:- mode(...)</c> directive into a
/// <see cref="ModeDeclaration"/>. Because <c>mode</c> is a prefix
/// operator (priority 1150) and <c>is</c> is an infix operator
/// (priority 700), <c>:- mode foo(+, -) is det.</c> parses as
/// <c>mode(is(foo(+,-), det))</c> — the <c>mode</c> wrapper is
/// outermost and the determinism, when present, sits in an
/// <c>is/2</c> inside it.
///
/// <para>Two accepted shapes for the wrapped term:</para>
/// <list type="bullet">
/// <item><c>foo(+, -)</c> — a plain spec compound, no determinism.</item>
/// <item><c>is(foo(+, -), det)</c> — spec plus a determinism atom.</item>
/// </list>
/// </summary>
public static class ModeDirectiveParser
{
    /// <summary>Attempts to parse <paramref name="directiveBody"/> (the
    /// term after <c>:-</c>) as a mode directive. Returns false when the
    /// body isn't a <c>mode/1</c> compound at all — the caller then
    /// tries other directive readers. Returns true with
    /// <paramref name="declaration"/> set on success; returns true with
    /// <paramref name="error"/> set when the body IS a mode directive
    /// but malformed (bad indicator, bad determinism, non-compound
    /// spec) — those are hard errors the caller should surface.</summary>
    public static bool TryParse(
        Term directiveBody,
        out ModeDeclaration? declaration,
        out string? error)
    {
        declaration = null;
        error = null;

        if (directiveBody is not CompoundTerm mode
            || mode.Functor != "mode" || mode.Args.Length != 1)
        {
            return false;   // not a mode directive — caller moves on
        }

        ParseSpecCore(mode.Args[0], out declaration, out error);
        return true;
    }

    /// <summary>Chain-aware variant: the classic
    /// DEC-10 / Quintus style packs MANY specs into ONE directive as a
    /// ','-chain — <c>:- mode f(+,-), g(+), h(?,-).</c> parses as
    /// <c>mode(','(f(+,-), ','(g(+), h(?,-))))</c>. Splits the chain and
    /// parses each element with the single-spec core. Same contract as
    /// <see cref="TryParse"/>: false = not a mode directive at all; true with
    /// <paramref name="error"/> = malformed; true with
    /// <paramref name="declarations"/> = success.</summary>
    public static bool TryParseAll(
        Term directiveBody,
        out List<ModeDeclaration>? declarations,
        out string? error)
    {
        declarations = null;
        error = null;

        if (directiveBody is not CompoundTerm mode
            || mode.Functor != "mode" || mode.Args.Length != 1)
        {
            return false;   // not a mode directive — caller moves on
        }

        var decls = new List<ModeDeclaration>();
        Term rest = mode.Args[0];
        while (rest is CompoundTerm { Functor: ",", Args.Length: 2 } conj)
        {
            ParseSpecCore(conj.Args[0], out var d, out error);
            if (error is not null) return true;
            decls.Add(d!);
            rest = conj.Args[1];
        }
        ParseSpecCore(rest, out var last, out error);
        if (error is not null) return true;
        decls.Add(last!);
        declarations = decls;
        return true;
    }

    /// <summary>Single-spec core shared by <see cref="TryParse"/> and
    /// <see cref="TryParseAll"/>: parses <c>foo(+,-)</c> or
    /// <c>is(foo(+,-), det)</c>.</summary>
    private static void ParseSpecCore(Term inner,
        out ModeDeclaration? declaration, out string? error)
    {
        declaration = null;
        error = null;
        Term specTerm;
        Determinism determinism;

        // Determinism form: is(spec, detAtom).
        if (inner is CompoundTerm isExpr && isExpr.Functor == "is" && isExpr.Args.Length == 2)
        {
            specTerm = isExpr.Args[0];
            if (isExpr.Args[1] is not AtomTerm detAtom)
            {
                error = "malformed :- mode directive: the determinism after 'is' "
                    + "must be one of det, semidet, multi, nondet.";
                return;
            }
            if (!TryParseDeterminism(detAtom.Name, out determinism))
            {
                error = $"malformed :- mode directive: unknown determinism '{detAtom.Name}' "
                    + "(expected det, semidet, multi, or nondet).";
                return;
            }
        }
        else
        {
            specTerm = inner;
            determinism = Determinism.NoneDeclared;
        }

        if (specTerm is not CompoundTerm spec || spec.Args.Length == 0)
        {
            error = "malformed :- mode directive: the predicate spec must be a "
                + "compound term like foo(+, -).";
            return;
        }

        var argModes = new ModeIndicator[spec.Args.Length];
        for (int i = 0; i < spec.Args.Length; i++)
        {
            if (spec.Args[i] is not AtomTerm indicator
                || !TryParseIndicator(indicator.Name, out argModes[i]))
            {
                error = $"malformed :- mode directive: argument {i + 1} must be "
                    + "one of +, -, ?.";
                return;
            }
        }

        int functorId = FunctorTable.Intern(
            AtomTable.Intern(spec.Functor, permanent: true).Id, spec.Args.Length);
        declaration = new ModeDeclaration(functorId, argModes, determinism);
    }

    private static bool TryParseIndicator(string atom, out ModeIndicator indicator)
    {
        switch (atom)
        {
            case "+": indicator = ModeIndicator.Input; return true;
            case "-": indicator = ModeIndicator.Output; return true;
            case "?": indicator = ModeIndicator.Either; return true;
            default: indicator = ModeIndicator.Either; return false;
        }
    }

    private static bool TryParseDeterminism(string atom, out Determinism determinism)
    {
        switch (atom)
        {
            case "det": determinism = Determinism.Det; return true;
            case "semidet": determinism = Determinism.Semidet; return true;
            case "multi": determinism = Determinism.Multi; return true;
            case "nondet": determinism = Determinism.Nondet; return true;
            default: determinism = Determinism.NoneDeclared; return false;
        }
    }
}
