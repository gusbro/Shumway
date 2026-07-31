using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-040 — builtins whose behaviour differs by source dialect. A
/// builtin whose strict ISO behaviour would raise consults the caller's module
/// dialect (chain-walking the call-return addresses) and, for an SWI module,
/// applies SWI's more permissive rule. Only the would-raise path pays the cost;
/// the strict case is unchanged. First case: <c>atom_concat/3</c> coercing a
/// numeric argument.</summary>
public sealed class DialectAwareBuiltinsTests
{
    // A throwaway SWI-dialect library whose predicate calls atom_concat with a
    // number — self-authored (no third-party source), machine-path-free.
    private static PrologEngine EngineWithSwiModule(string moduleSource, out string tmp)
    {
        tmp = Path.Combine(Path.GetTempPath(), "swidialect-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "swimod.pl"), moduleSource);
        var e = new PrologEngine();
        e.AddLibraryDirectory(tmp, "swi");
        e.ConsultString(":- use_module(library(swimod)).");
        return e;
    }

    [Fact]
    public void AtomConcat_NumericArg_StrictByDefault()
    {
        var e = new PrologEngine();
        // Default (Shumway/ISO) module: a numeric argument is a type_error,
        // exactly as GNU Prolog does.
        Assert.True(e.Query(
            "catch(atom_concat(foo, 42, _), error(type_error(atom, 42), _), true).").Success);
        Assert.True(e.Query(
            "catch(atom_concat(7, bar, _), error(type_error(atom, 7), _), true).").Success);
    }

    [Fact]
    public void AtomConcat_NumericArg_CoercedInSwiModule()
    {
        string tmp = "";
        try
        {
            var e = EngineWithSwiModule(
                ":- module(swimod, [cat/2, cat2/3]).\n"
                + "cat(X, R) :- atom_concat(foo, X, R).\n"
                + "cat2(A, B, R) :- atom_concat(A, B, R).\n", out tmp);
            // cat/2 lives in an SWI module; atom_concat(foo, 42, R) coerces.
            Assert.True(e.Query("cat(42, R), R == foo42.").Success);
            // both a number prefix and suffix.
            Assert.True(e.Query("cat2(3, 14, R), R == '314'.").Success);
            Assert.True(e.Query("cat2(pre, 9, R), R == pre9.").Success);
            // a float coerces to its Prolog text.
            Assert.True(e.Query("cat(1.5, R), R == 'foo1.5'.").Success);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void AtomConcat_StrictUserCall_UnaffectedByLoadedSwiModule()
    {
        string tmp = "";
        try
        {
            // An SWI module is loaded (so the dialect machinery is armed), but a
            // direct user-level atom_concat call is NOT in an SWI module — it
            // stays strict. This checks the caller-module resolution actually
            // distinguishes callers rather than flipping a global switch.
            var e = EngineWithSwiModule(
                ":- module(swimod, [cat/2]).\ncat(X, R) :- atom_concat(foo, X, R).\n", out tmp);
            Assert.True(e.Query("cat(1, R), R == foo1.").Success);            // SWI path works
            Assert.True(e.Query(
                "catch(atom_concat(foo, 2, _), error(type_error(atom, 2), _), true).").Success); // user strict
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void AtomConcat_AtomAtom_UnchangedInBothDialects()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("atom_concat(ab, cd, abcd).").Success);
        // split mode still works too.
        Assert.Equal(4, e.QueryAll("atom_concat(X, Y, abc).").Count());  // ""+abc … abc+""
    }
}
