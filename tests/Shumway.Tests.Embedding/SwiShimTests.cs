using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-040 — the SWI compatibility shim (SWI system predicates that are
/// not standard/ISO: nb_setarg, copy_term_nat, duplicate_term, same_term,
/// current_arithmetic_function). It loads automatically when an SWI-dialect module
/// is loaded, and explicitly via <c>use_module(library(swi))</c>. A pure engine
/// never sees these predicates.</summary>
public sealed class SwiShimTests
{
    [Fact]
    public void ShimPredicates_NotAvailableByDefault()
    {
        var e = new PrologEngine();
        // No SWI loaded → nb_setarg is an undefined procedure.
        Assert.True(e.Query(
            "catch(nb_setarg(1, f(a), x), error(existence_error(procedure, _), _), true).").Success);
        Assert.True(e.Query(
            "catch(same_term(a, a), error(existence_error(procedure, _), _), true).").Success);
    }

    [Fact]
    public void ManualLoad_MakesShimPredicatesAvailable()
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(swi)).");
        // nb_setarg destructively (non-backtrackably) sets an argument.
        Assert.True(e.Query("T = f(a, b), nb_setarg(1, T, z), T == f(z, b).").Success);
        // copy_term_nat / duplicate_term / same_term.
        Assert.True(e.Query("copy_term_nat(g(X, X), C), C = g(1, 1).").Success);
        Assert.True(e.Query("duplicate_term(h(a), C), C == h(a).").Success);
        Assert.True(e.Query("T = k(a), same_term(T, T).").Success);
        Assert.False(e.Query("same_term(k(a), k(a)).").Success);   // distinct storage
        Assert.True(e.Query("same_term(a, a).").Success);          // equal atomics
        // current_arithmetic_function reports the built-in evaluables.
        Assert.True(e.Query("current_arithmetic_function(sin(_)).").Success);
        Assert.True(e.Query("current_arithmetic_function(_ + _).").Success);
        Assert.False(e.Query("current_arithmetic_function(no_such_fn(_)).").Success);
    }

    [Fact]
    public void AutoLoad_WhenAnSwiModuleIsLoaded()
    {
        // An SWI-dialect module uses nb_setarg internally; loading it must
        // auto-load the shim so that system-predicate call resolves.
        string tmp = Path.Combine(Path.GetTempPath(), "swishim-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "swimod.pl"),
                ":- module(swimod, [bump/2]).\n"
                + "bump(T, Out) :- copy_term(T, Out), nb_setarg(1, Out, 99).\n");
            var e = new PrologEngine();
            e.AddLibraryDirectory(tmp, "swi");
            e.ConsultString(":- use_module(library(swimod)).");
            Assert.True(e.Query("bump(f(a, b), Out), Out == f(99, b).").Success);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void ErrorKernelTypeChecks_AreAvailable()
    {
        var e = new PrologEngine();
        // '$is_char*' are bare-global kernel internals (library(error) uses them).
        Assert.True(e.Query("'$is_char'(a).").Success);
        Assert.False(e.Query("'$is_char'(ab).").Success);
        Assert.False(e.Query("'$is_char'(1).").Success);
        Assert.True(e.Query("'$is_char_code'(65).").Success);
        Assert.False(e.Query("'$is_char_code'(-1).").Success);
        Assert.True(e.Query("'$is_char_list'([a,b,c], N), N == 3.").Success);
        Assert.False(e.Query("'$is_char_list'([a,bb], _).").Success);
        Assert.True(e.Query("'$is_code_list'([65,66], N), N == 2.").Success);
        Assert.False(e.Query("'$is_code_list'([65,x], _).").Success);
    }

    [Fact]
    public void SubAtomICaseChk_IsInTheShim()
    {
        var e = new PrologEngine();
        // Not available without the shim (SWI-specific).
        Assert.True(e.Query(
            "catch(sub_atom_icasechk(abc, _, b), error(existence_error(procedure, _), _), true).").Success);
        e.ConsultString(":- use_module(library(swi)).");
        // Case-insensitive first-match offset.
        Assert.True(e.Query("sub_atom_icasechk('Hello World', B, world), B == 6.").Success);
        Assert.True(e.Query("sub_atom_icasechk(abcABC, B, 'BC'), B == 1.").Success);
        Assert.False(e.Query("sub_atom_icasechk(abc, _, z).").Success);
    }

    [Fact]
    public void SkipList_CountsProperAndPartialLists()
    {
        var e = new PrologEngine();
        // '$skip_list' is a bare-global SWI system primitive.
        Assert.True(e.Query("'$skip_list'(N, [a,b,c], T), N == 3, T == [].").Success);
        Assert.True(e.Query("'$skip_list'(N, [a,b|_], T), N == 2, var(T).").Success);   // partial
        Assert.True(e.Query("'$skip_list'(N, foo, T), N == 0, T == foo.").Success);      // not a list
        Assert.True(e.Query("'$skip_list'(N, [], T), N == 0, T == [].").Success);
    }

    [Fact]
    public void CodeType_IsInTheShim_AndClassifies()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "catch(code_type(0'a, alpha), error(existence_error(procedure, _), _), true).").Success);
        e.ConsultString(":- use_module(library(swi)).");
        Assert.True(e.Query("code_type(0'a, alpha).").Success);
        Assert.True(e.Query("code_type(0'5, digit(W)), W == 5.").Success);
        Assert.True(e.Query("code_type(0' , space).").Success);
        Assert.False(e.Query("code_type(0'a, digit(_)).").Success);
        // Case conversions yield CODES (not chars, as char_type does).
        Assert.True(e.Query("code_type(0'A, to_lower(L)), L == 0'a.").Success);
        Assert.True(e.Query("code_type(0'A, upper(Lower)), Lower == 0'a.").Success);
        Assert.False(e.Query("code_type(0'A, lower(_)).").Success);
    }

    [Fact]
    public void Backtrace_IsANoOpInTheShim()
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(swi)).");
        Assert.True(e.Query("backtrace(10).").Success);
    }

    [Fact]
    public void SubString_IsAlwaysAvailable()
    {
        var e = new PrologEngine();
        // sub_string/5 is a real bare-global builtin (not part of the shim).
        Assert.True(e.Query(
            "sub_string(hello, 0, 2, _, Sub), atom_string(A, Sub), A == he.").Success);
        // backtracks over decompositions, like sub_atom.
        Assert.Equal(3, e.QueryAll("sub_string(abc, B, 1, _, _).").Count());
    }
}
