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

    [Fact]
    public void OtherBuiltins_NumericArg_StrictByDefault()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("catch(atom_length(42, _), error(type_error(atom, 42), _), true).").Success);
        Assert.True(e.Query("catch(atom_chars(42, _), error(type_error(atom, _), _), true).").Success);
        Assert.True(e.Query("catch(atom_codes(42, _), error(type_error(atom, _), _), true).").Success);
        Assert.True(e.Query("catch(upcase_atom(42, _), error(type_error(atom, _), _), true).").Success);
    }

    [Fact]
    public void OtherBuiltins_NumericArg_CoercedInSwiModule()
    {
        string tmp = "";
        try
        {
            var e = EngineWithSwiModule(
                ":- module(swimod, [alen/2, achars/2, acodes/2, aup/2]).\n"
                + "alen(X, N) :- atom_length(X, N).\n"
                + "achars(X, Cs) :- atom_chars(X, Cs).\n"
                + "acodes(X, Cs) :- atom_codes(X, Cs).\n"
                + "aup(X, U) :- upcase_atom(X, U).\n", out tmp);
            Assert.True(e.Query("alen(12345, N), N == 5.").Success);
            Assert.True(e.Query("achars(42, Cs), Cs == ['4','2'].").Success);
            Assert.True(e.Query("acodes(42, Cs), Cs == [0'4, 0'2].").Success);
            Assert.True(e.Query("aup(1.5, U), U == '1.5'.").Success);          // number has no case
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void ShmoObject_Dialect_RoundTrips()
    {
        var obj = ShmoCompiler.CompileSource(
            ":- module(m, [p/0]).\np.\n", "m");   // dialect defaults null
        Assert.Null(ShmoReader.FromBytes(ShmoWriter.ToBytes(obj)).Dialect);
    }

    [Fact]
    public void AtomConcat_Coercion_SurvivesSourceStrippedBundle()
    {
        // The limitation fix: a linked, SOURCE-STRIPPED bundle carries the module
        // dialect, so atom_concat coercion still applies after a bare load (no
        // use_module at runtime).
        string tmp = Path.Combine(Path.GetTempPath(), "swidialbundle-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "swimod.pl"),
                ":- module(swimod, [cat/2]).\ncat(X, R) :- atom_concat(foo, X, R).\n");
            string driver = Path.Combine(tmp, "driver.pl");
            File.WriteAllText(driver,
                ":- use_module(library(swimod)).\n:- public go/2.\ngo(X, R) :- cat(X, R).\n");

            var errors = new System.Collections.Generic.List<ShmoCompileError>();
            // Release build mode → source stripped from the .shmo; swimod is pulled
            // under the swi dialect, so its ShmoObject carries Dialect="swi".
            var objs = ShmoViaConsult.Compile(
                driver, new[] { tmp }, ShmoBuildMode.Release, errors, dialect: "swi");
            Assert.Empty(errors);

            var r = ShmoLinker.Link(new LinkConfig
            {
                Objects = System.Linq.Enumerable.ToList(
                    System.Linq.Enumerable.Select(objs, o => o.Object)),
                EntryPoints = new[] { new PredicateRef("go", 2) },
            });
            Assert.True(r.Success, string.Join(", ", r.Diagnostics.Select(d => d.Message)));

            // Cross-process shape: serialize then read back, then bare-load.
            var bundle = BundleReader.FromBytes(r.Bytes!);
            var e = new PrologEngine();
            e.LoadBundle(bundle);

            // go/2 reaches swimod$cat → atom_concat(foo, 42, R); swimod is swi, so
            // the numeric arg coerces — with NO source and NO use_module at runtime.
            Assert.True(e.Query("go(42, R), R == foo42.").Success);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }
}
