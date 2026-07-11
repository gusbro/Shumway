using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 130 (Phase 9 Stage A, step 2): the ISO <c>error/2</c> Context
/// slot now carries the offending builtin's <c>Name/Arity</c> indicator
/// instead of the Phase-1 fresh anonymous variable. The identity flows
/// through two channels:
/// <list type="bullet">
/// <item>For a <see cref="PrologRuntimeException"/> raised from inside a
/// builtin Impl, the interpreter dispatch stamps the exception with
/// <c>entry.Name</c> / <c>entry.Arity</c> as it unwinds out of the
/// impl, and <c>TranslateRuntimeError</c> reads from there. This works
/// even when the throw originates in a sub-engine query whose
/// <see cref="Activation"/> instance is gone by the time the parent's
/// <c>catch/3</c> handler runs.</item>
/// <item>For a direct <c>throw new ShumwayPrologException(IsoError.X(...,
/// engine))</c> from inside an impl, the <see cref="IsoError"/> factory
/// reads <see cref="Activation.CurrentBuiltinName"/> (set by the same
/// dispatch right before calling the impl) and emits the indicator
/// inline.</item>
/// </list>
/// </summary>
public class Chunk130Tests
{
    private static AtomTerm Atom(string n) => new(n);
    private static IntTerm Int(long v) => new(v);

    // ---------- The PrologRuntimeException promotion path ----------

    [Fact]
    public void EvaluationError_ContextIsBuiltinIndicator()
    {
        // is/2 raises evaluation_error(zero_divisor) via PrologRuntimeException;
        // chunk 130 stamps "is/2" onto the exception so the Context slot
        // becomes (is)/2 — what a catcher matching on the indicator wants.
        var engine = new PrologEngine();
        var sol = engine.Query(
            "catch(_X is 1 // 0, error(_, Ctx), true).");
        Assert.True(sol.Success);
        var ctx = Assert.IsType<CompoundTerm>(sol["Ctx"]);
        Assert.Equal("/", ctx.Functor);
        Assert.Equal(Atom("is"), ctx.Args[0]);
        Assert.Equal(Int(2), ctx.Args[1]);
    }

    [Fact]
    public void UndefinedProcedure_NoBuiltinFrame_ContextIsFreeVar()
    {
        // The interpreter's own undefined-procedure resolver raises a
        // PrologRuntimeException that never touches a builtin dispatch
        // site, so no stamp is added. The Context slot stays a fresh
        // anonymous variable — pinned here as "unifies with anything".
        var engine = new PrologEngine();
        var sol = engine.Query(
            "catch(undefined_foo(1), error(existence_error(procedure, _), Ctx), Ctx = anything).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("anything"), sol["Ctx"]);
    }

    [Fact]
    public void TypeError_ContextIsBuiltinIndicator()
    {
        // is/2 raises type_error("evaluable", ...) via PrologRuntimeException
        // when its right-hand side contains a non-evaluable atom; the
        // Context slot carries is/2 (the impl-defined indicator).
        var engine = new PrologEngine();
        // `\(F)` for a float F goes through BitwiseNot which raises
        // PrologRuntimeException("type_error","integer"); we use that
        // because it's a clean PrologRuntimeException path (the audit
        // for InvalidOperationException leak in other arithmetic paths
        // is chunk 131's territory).
        var sol = engine.Query(
            "catch(_X is \\(1.5), error(type_error(_, _), Ctx), true).");
        Assert.True(sol.Success);
        var ctx = Assert.IsType<CompoundTerm>(sol["Ctx"]);
        Assert.Equal("/", ctx.Functor);
        Assert.Equal(Atom("is"), ctx.Args[0]);
        Assert.Equal(Int(2), ctx.Args[1]);
    }

    // ---------- The IsoError direct-construction path ----------

    [Fact]
    public void IsoError_WithEngine_BuildsIndicatorContext()
    {
        // The factory direct path — exercised by builtins that throw
        // ShumwayPrologException(IsoError.X(..., engine)). Simulate by
        // priming the engine fields manually.
        var pe = new PrologEngine();
        // Run any query that dispatches a builtin so CurrentBuiltinName
        // is set, then read the IsoError construction outside dispatch.
        pe.Query("X = 1.");  // unification — no builtin dispatch.
        // Build a synthetic engine with CurrentBuiltinName primed to
        // exercise the factory directly.
        var raw = new Activation
        {
            CurrentBuiltinName = "frobnicate",
            CurrentBuiltinArity = 3,
        };
        var err = IsoError.TypeError("integer", Atom("nope"), raw);
        var ct = Assert.IsType<CompoundTerm>(err);
        Assert.Equal("error", ct.Functor);
        var ctx = Assert.IsType<CompoundTerm>(ct.Args[1]);
        Assert.Equal("/", ctx.Functor);
        Assert.Equal(Atom("frobnicate"), ctx.Args[0]);
        Assert.Equal(Int(3), ctx.Args[1]);
    }

    [Fact]
    public void IsoError_WithoutEngine_KeepsAnonymousContext()
    {
        // Backwards-compatible: no engine, no stamped context. Phase-1
        // call sites that don't pass engine keep their fresh-var slot.
        var err = IsoError.TypeError("integer", Atom("nope"));
        var ct = Assert.IsType<CompoundTerm>(err);
        Assert.IsType<VarTerm>(ct.Args[1]);
    }

    [Fact]
    public void IsoError_EngineWithoutCurrentBuiltin_KeepsAnonymousContext()
    {
        // Activation instance in hand but no builtin currently active — the
        // factory still gets to fall through to the var. The
        // current-builtin field is null on a fresh engine.
        var raw = new Activation();
        Assert.Null(raw.CurrentBuiltinName);
        var err = IsoError.InstantiationError(raw);
        var ct = Assert.IsType<CompoundTerm>(err);
        Assert.IsType<VarTerm>(ct.Args[1]);
    }

    // ---------- Catcher patterns that depend on the indicator ----------

    [Fact]
    public void Catch_OnBuiltinIndicator_Matches()
    {
        // The classic idiom — destructuring on the Name/Arity indicator
        // — now works for any PrologRuntimeException raised from a
        // builtin impl.
        var engine = new PrologEngine();
        var sol = engine.Query(
            "catch(_X is 1 // 0, error(evaluation_error(zero_divisor), Name/Arity), "
            + "(Caught_Name = Name, Caught_Arity = Arity)).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("is"), sol["Caught_Name"]);
        Assert.Equal(Int(2), sol["Caught_Arity"]);
    }

    [Fact]
    public void Catch_NestedMetaCall_PreservesInnermostIndicator()
    {
        // The StampBuiltin idempotency rule: an outer meta-call dispatch
        // (call/1) doesn't overwrite the inner builtin's stamp. So even
        // inside catch(call(...), ...), the Context still names the
        // innermost throwing builtin, not "call".
        var engine = new PrologEngine();
        var sol = engine.Query(
            "catch(call((_X is 1 // 0)), error(_, Name/_), true).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("is"), sol["Name"]);
    }
}
