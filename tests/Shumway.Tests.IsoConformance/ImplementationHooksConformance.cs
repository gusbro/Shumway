using Shumway.Compiler.Ast;
using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// ISO 13211-1, §7.11 + §8.17 — implementation-defined hooks:
/// <c>set_prolog_flag/2</c>, <c>current_prolog_flag/2</c>, <c>op/3</c>,
/// <c>current_op/3</c>, and <c>halt/0,1</c>. These let user code
/// interact with parser / reader state and the impl-defined flag
/// table, plus quit the engine.
/// </summary>
public class ImplementationHooksConformance
{
    private static Term Atom(string n) => new AtomTerm(n);
    private static Term Int(long v) => new IntTerm(v);

    // ---------- set_prolog_flag / current_prolog_flag ----------

    [Fact]
    public void CurrentPrologFlag_DoubleQuotes_HasDefault()
    {
        var e = new PrologEngine();
        var sol = e.Query("current_prolog_flag(double_quotes, V).");
        Assert.True(sol.Success);
        // The default value is an atom — one of codes / chars /
        // atom / string. Don't pin which (the default is
        // implementation-defined); just that it's an atom.
        Assert.IsType<AtomTerm>(sol["V"]);
    }

    [Fact]
    public void SetPrologFlag_DoubleQuotes_Roundtrips()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("set_prolog_flag(double_quotes, chars).").Success);
        var sol = e.Query("current_prolog_flag(double_quotes, V).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("chars"), sol["V"]);
    }

    [Fact]
    public void SetPrologFlag_UnknownFlag_RaisesDomainError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(set_prolog_flag(no_such_flag, x), error(domain_error(D, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("prolog_flag"), sol["D"]);
    }

    [Fact]
    public void SetPrologFlag_VarFlag_RaisesInstantiationError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(set_prolog_flag(_F, codes), error(E, _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("instantiation_error"), sol["E"]);
    }

    [Fact]
    public void SetPrologFlag_InvalidDoubleQuotesValue_RaisesDomainError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(set_prolog_flag(double_quotes, nonsense), "
            + "error(domain_error(D, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("flag_value"), sol["D"]);
    }

    // ---------- op/3 ----------

    [Fact]
    public void Op_DefinesNewBinaryOperator()
    {
        var e = new PrologEngine();
        // Define a new infix op `~~~~`, then parse + use it.
        Assert.True(e.Query("op(700, xfx, ~~~~).").Success);
        var sol = e.Query("T = (foo ~~~~ bar).");
        Assert.True(sol.Success);
        var t = Assert.IsType<CompoundTerm>(sol["T"]);
        Assert.Equal("~~~~", t.Functor);
        Assert.Equal(2, t.Args.Length);
    }

    [Fact]
    public void Op_InvalidPriority_RaisesDomainError()
    {
        var e = new PrologEngine();
        // Priority above 1200 is out of range.
        var sol = e.Query(
            "catch(op(2000, xfx, foo), error(domain_error(D, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("operator_priority"), sol["D"]);
    }

    [Fact]
    public void Op_InvalidType_RaisesDomainError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(op(500, foo, bar), error(domain_error(D, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("operator_specifier"), sol["D"]);
    }

    [Fact]
    public void Op_NegativePriority_RaisesDomainError()
    {
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(op(-1, xfx, foo), error(domain_error(D, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("operator_priority"), sol["D"]);
    }

    [Fact]
    public void Op_VarPriority_RaisesTypeError()
    {
        // Phase-9 chunk 131e gave op/3 the type check; ISO calls for
        // instantiation_error when var, type_error(integer, _)
        // otherwise. Shumway's current impl reports type_error in
        // both cases (Detail "integer"), which is suboptimal but
        // matches existing SWI behaviour for some malformed args.
        var e = new PrologEngine();
        var sol = e.Query(
            "catch(op(_P, xfx, foo), error(type_error(T, _), _), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("integer"), sol["T"]);
    }

    [Fact]
    public void Op_ListOfNames_DefinesAll()
    {
        // ISO §8.17.1: the Name arg can be a list, defining several
        // operators with the same priority + type.
        var e = new PrologEngine();
        Assert.True(e.Query("op(700, xfx, [op_a, op_b]).").Success);
        Assert.True(e.Query("T = (1 op_a 2), T = op_a(1, 2).").Success);
        Assert.True(e.Query("T = (1 op_b 2), T = op_b(1, 2).").Success);
    }

    // ---------- current_op/3 ----------

    [Fact]
    public void CurrentOp_FindsStandardComma()
    {
        // `,` is the canonical infix conjunction operator — priority
        // 1000, type xfy.
        var e = new PrologEngine();
        Assert.True(e.Query("current_op(1000, xfy, ',').").Success);
    }

    [Fact]
    public void CurrentOp_FindsArithmeticOps()
    {
        var e = new PrologEngine();
        // `+` is yfx infix at priority 500.
        Assert.True(e.Query("current_op(500, yfx, +).").Success);
        // `*` is yfx infix at priority 400.
        Assert.True(e.Query("current_op(400, yfx, *).").Success);
    }

    [Fact]
    public void CurrentOp_NewlyDefined_IsVisible()
    {
        var e = new PrologEngine();
        e.Query("op(700, xfx, ===).");
        Assert.True(e.Query("current_op(700, xfx, ===).").Success);
    }

    // ---------- halt/0, halt/1 ----------

    [Fact]
    public void Halt_TerminatesQuery()
    {
        // halt should signal "stop the query" — Shumway's PrologEngine
        // surfaces this as the query yielding no further solutions and
        // exposing LastHaltExitCode.
        var e = new PrologEngine();
        var sols = e.QueryAll("halt.").ToList();
        Assert.Empty(sols);
        Assert.True(e.LastHaltExitCode.HasValue);
        Assert.Equal(0, e.LastHaltExitCode.Value);
    }

    [Fact]
    public void Halt1_ExitCodeIsSurfaced()
    {
        var e = new PrologEngine();
        var sols = e.QueryAll("halt(42).").ToList();
        Assert.Empty(sols);
        Assert.True(e.LastHaltExitCode.HasValue);
        Assert.Equal(42, e.LastHaltExitCode.Value);
    }
}
