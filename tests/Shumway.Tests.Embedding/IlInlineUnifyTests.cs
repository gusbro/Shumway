using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A =/2 in compiled code unifies its two cells inline (the mirror
/// of the wasm tier's open-coded =/2): the same cell, a plain variable against
/// a value or another variable, two distinct immediates; the rest through the
/// unifier. The clause compiler compiles most of the cases below to
/// get_value_y / get_value_x (a permanent through a scratch register), which
/// take the same code; two non-variables and a variable without a cell
/// against `_` stay a call of the builtin. The answers must be Tier-0's.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class IlInlineUnifyTests : IDisposable
{
    private readonly bool _savedCpsMode = IlPredicateCompiler.CpsMode;

    public IlInlineUnifyTests() => IlPredicateCompiler.CpsMode = false;

    public void Dispose() => IlPredicateCompiler.CpsMode = _savedCpsMode;

    // p/1 and q/1 are calls, so the variables they take are permanent.
    private static readonly (string Name, string Body)[] Cases =
    {
        ("varvar_old_young", "p(X), q(Y), X = Y, X == Y, R = ok"),
        ("varvar_young_old", "p(X), q(Y), Y = X, X == Y, R = ok"),
        ("var_int", "p(X), q(_), X = 7, R = X"),
        ("int_var", "p(X), q(_), 7 = X, R = X"),
        ("var_atom", "p(X), q(_), X = foo, R = X"),
        ("int_int_eq", "p(_), q(_), A = 3, B = 3, A = B, R = ok"),
        ("int_int_ne", "p(_), q(_), A = 3, B = 4, ( A = B -> R = bad ; R = ok )"),
        ("atom_int_ne", "p(_), q(_), A = a, B = 1, ( A = B -> R = bad ; R = ok )"),
        ("var_struct", "p(X), q(_), X = f(1, g(Z)), Z = 2, R = X"),
        ("struct_var_ref", "p(X), q(Y), Y = f(a), X = Y, X == Y, R = X"),
        ("struct_struct", "p(_), q(_), A = f(P, b), B = f(a, Q), A = B, R = P-Q"),
        ("struct_struct_ne", "p(_), q(_), A = f(1), B = g(1), ( A = B -> R = bad ; R = ok )"),
        ("list", "p(X), q(_), X = [1,2|T], T = [3], R = X"),
        // Boxed numbers from text: a float or a bigint literal in arithmetic
        // keeps a clause on Tier-0.
        ("float_eq", "p(_), q(_), atom_number('1.5', A), atom_number('1.50', B), ( A = B -> R = ok ; R = bad )"),
        ("float_ne", "p(_), q(_), atom_number('1.5', A), atom_number('2.5', B), ( A = B -> R = bad ; R = ok )"),
        ("bigint_eq", "p(_), q(_), atom_number('1180591620717411303424', A), atom_number('1180591620717411303424', B), ( A = B -> R = ok ; R = bad )"),
        ("bigint_ne", "p(_), q(_), atom_number('1180591620717411303424', A), atom_number('2361183241434822606848', B), ( A = B -> R = bad ; R = ok )"),
        ("attvar", "p(X), q(_), freeze(X, W = woken), X = 5, R = W"),
        ("attvar_var", "p(X), q(Y), freeze(X, W = woken), Y = X, Y = 9, R = W"),
        ("chain", "p(X), q(Y), q(Z), Y = X, Z = Y, Z = 11, R = X"),
        ("backtrack", "p(X), q(_), ( X = 1, fail ; X = 2 ), R = X"),
        ("trail_undo", "p(X), q(_), ( X = 1, fail ; true ), ( var(X) -> R = ok ; R = bad )"),
        ("occurs", "p(X), q(_), set_prolog_flag(occurs_check, true), ( X = f(X) -> R = bad ; R = ok ), set_prolog_flag(occurs_check, false)"),
        // The shapes of a permanent the clause compiler compiles inline: its
        // first cell from a built term, from the other side's cell, shared
        // with another variable without one; a bound one matched.
        ("perm_first_built", "p(_), X = f(A, [1,2]), q(X), A = 3, R = X"),
        ("perm_first_cyclic", "p(_), X = f(X), q(X), X = f(Y), ( Y == X -> R = ok ; R = bad )"),
        ("perm_first_from_perm", "p(X), Y = X, q(Y), X = 4, R = Y"),
        ("perm_first_two", "p(_), X = Y, q(X), q(Y), X = 5, R = Y"),
        ("perm_first_from_temp", "p(_), B = 7, X = B, q(X), R = X"),
        ("perm_first_to_temp", "p(_), X = B, B = 8, q(X), R = X"),
        ("perm_seen_from_temp", "p(X), q(_), B = 9, X = B, R = X"),
        ("perm_seen_to_temp", "p(X), q(_), X = B, B = 10, R = X"),
        ("perm_seen_struct_fail", "p(X), q(_), X = f(1), ( X = g(_) -> R = bad ; R = ok )"),
        ("perm_anon_no_cell", "p(_), q(_), Z = _, Z = 3, R = Z"),
        ("perm_anon", "p(X), q(_), X = _, X = 11, R = X"),
        ("both_nonvar", "p(_), q(_), f(P, b) = f(a, Q), R = P-Q"),
    };

    private static string Program(string tag)
    {
        var sb = new System.Text.StringBuilder(":- use_module(library(coroutining)).\n");
        foreach (var (name, body) in Cases)
            sb.Append(tag).Append('_').Append(name).Append("(R) :- ").Append(body).Append(".\n");
        sb.Append("p(_).\nq(_).\n");
        return sb.ToString();
    }

    private static List<string> Answers(PrologEngine e, string tag)
    {
        var answers = new List<string>();
        foreach (var (name, _) in Cases)
        {
            var r = e.Query($"{tag}_{name}(R).");
            answers.Add(name + "=" + (r.Success ? r.Bindings["R"].ToString() : "failed"));
        }
        return answers;
    }

    [Fact]
    public void EveryCaseAnswersAsTierZero()
    {
        var plain = new PrologEngine { Out = new StringWriter() };
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Program("iu"));
        var expected = Answers(plain, "iu");
        Assert.Contains("attvar=woken", expected);
        Assert.Contains("trail_undo=ok", expected);
        Assert.Contains("occurs=ok", expected);

        int sites = IlPredicateCompiler.InlineUnifySites;
        int getValueSites = IlPredicateCompiler.InlineGetValueSites;
        var tiered = new PrologEngine { Out = new StringWriter() };
        tiered.IlPromotion.Threshold = 1;
        tiered.ConsultString(Program("iu"));
        for (int i = 0; i < 3; i++)
        {
            Answers(tiered, "iu");
            Assert.True(tiered.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        }
        // ANTI-VACUITY: every case compiled; the two paths emitted.
        var promoted = new HashSet<string>(tiered.IlPromotion.PromotedFunctorIds().Select(fid =>
        {
            var (atom, arity) = FunctorTable.Lookup(fid);
            string n = AtomTable.GetById(atom)?.Name ?? "";
            return $"{n[(n.LastIndexOf('$') + 1)..]}/{arity}";
        }));
        foreach (var (name, _) in Cases)
            Assert.True(promoted.Contains($"iu_{name}/1"), $"iu_{name}/1 did not promote");
        Assert.True(IlPredicateCompiler.InlineUnifySites > sites, "no =/2 site was emitted inline");
        Assert.True(IlPredicateCompiler.InlineGetValueSites > getValueSites, "no get_value site was emitted inline");

        Assert.Equal(expected, Answers(tiered, "iu"));
    }
}
