using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 131e (Phase 9 Stage A, step 3, files 7-8): the user-reachable
/// runtime sites in <c>MetaBuiltins.cs</c> and <c>PrologEngine.cs</c>
/// now raise catchable ISO-shaped errors. The remaining ~30
/// InvalidOperationException sites across these two files are
/// engine-invariant assertions ("requires PrologEngine host",
/// "literal pool out of sync", "fail/0 builtin must be registered"…)
/// or consult-time directive errors — both are real bugs / setup
/// problems, not query-time contract violations, so they stay
/// uncatchable.
///
/// <para>This chunk also widens
/// <see cref="MetaBuiltins.TranslateRuntimeError"/> to handle the
/// three-argument <c>permission_error(Op, ObjType, Obj)</c> compound
/// — encoded as <c>"Op,ObjType"</c> in the
/// <see cref="Shumway.Core.PrologRuntimeException"/> Detail. The Obj
/// slot is a fresh anonymous variable; a Term-valued payload that
/// could carry the offending object is queued behind the rest of the
/// audit's wider rework of the exception's argument shape.</para>
/// </summary>
public class Chunk131eTests
{
    private static AtomTerm Atom(string n) => new(n);
    private static IntTerm Int(long v) => new(v);

    // ---------- assertz/retract on static predicate ----------

    [Fact]
    public void AssertzOnStatic_RaisesPermissionError()
    {
        var e = new PrologEngine();
        e.ConsultString("foo(1).");  // foo/1 is static
        var sol = e.Query(
            "catch(assertz(foo(2)), error(permission_error(Op, ObjType, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("modify"), sol["Op"]);
        Assert.Equal(Atom("static_procedure"), sol["ObjType"]);
    }

    [Fact]
    public void RetractOnStatic_RaisesPermissionError()
    {
        var e = new PrologEngine();
        e.ConsultString("foo(1).");
        var sol = e.Query(
            "catch(retract(foo(1)), error(permission_error(Op, _, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("modify"), sol["Op"]);
    }

    // ---------- assertz with non-callable head ----------

    [Fact]
    public void AssertzNumericHead_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(assertz(123), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("callable"), sol["T"]);
    }

    [Fact]
    public void AssertzVarHead_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(assertz(_X), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    // catch/3 with a non-callable recovery: the compiler rejects a
    // non-callable goal at compile time (NotSupportedException out of
    // the body emitter), so the runtime path my chunk-131e edit added
    // is unreachable from user code. The change is kept as defensive
    // — if a future compiler change moves this check to runtime, the
    // ISO-shaped throw is ready.

    // ---------- numbervars/3 ----------

    [Fact]
    public void NumberVars_VarStart_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(numbervars(foo(_A, _B), _Start, _End), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void NumberVars_NonIntStart_RaisesTypeError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(numbervars(foo(_A), foo, _End), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("integer"), sol["T"]);
    }

    // ---------- call/N with non-callable goal ----------

    [Fact]
    public void CallN_NumericGoal_RaisesTypeError()
    {
        var e = new PrologEngine();
        // call(123, X) builds 123(X) which is non-callable. The
        // AppendArgs path lands in the type_error(callable) case.
        var sol = e.Query(
            "catch(call(123, _X), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("callable"), sol["T"]);
    }

    // ---------- Happy paths still work ----------

    [Fact]
    public void DynamicAssertz_StillWorks()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic foo/1. foo(1).");
        Assert.True(e.Query("assertz(foo(2)), foo(2).").Success);
    }

    [Fact]
    public void Catch_CallableRecovery_StillWorks()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("catch(throw(oops), oops, true).").Success);
    }

    [Fact]
    public void NumberVars_GroundStart_StillWorks()
    {
        var e = new PrologEngine();
        var sol = e.Query("numbervars(foo(_A, _B), 0, End).");
        Assert.True(sol.Success);
        Assert.Equal(Int(2), sol["End"]);
    }

    // ---------- Context indicator ----------

    [Fact]
    public void PermissionError_ContextSlot_CarriesAssertzIndicator()
    {
        var e = new PrologEngine();
        e.ConsultString("foo(1).");
        var sol = e.Query(
            "catch(assertz(foo(2)), error(_, Name/Arity), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("assertz"), sol["Name"]);
        Assert.Equal(Int(1), sol["Arity"]);
    }
}
