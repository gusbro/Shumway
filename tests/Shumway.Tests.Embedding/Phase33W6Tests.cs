using Shumway.Core;
using Shumway.Embedding;
using Shumway.Compiler.Il;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 W6 (deferred-item review) — the IL compiler accepts the chunk-248
/// <c>ExecuteBuiltin</c> opcode in clause bodies, lifting the stated
/// prerequisite for the WAM-side tail-builtin fusion. No in-engine flow puts
/// ExecuteBuiltin into a <see cref="CompiledPredicate"/> today (the linker's
/// Execute→ExecuteBuiltin rewrite happens on the linked program buffer), so
/// these tests hand-assemble the bytecode the future fusion would emit and
/// load it through a bundle — exercising the interpreter, the eligibility
/// check, the Warm-path IL compile, and promoted execution end-to-end.
/// </summary>
public class Phase33W6Tests
{
    /// <summary>Builds an engine whose bundle defines
    /// <c>predName/arity</c> as ONE clause of exactly
    /// <c>execute_builtin &lt;builtin&gt;</c> — arguments pass through the
    /// X registers untouched, the builtin is the whole body.</summary>
    private static (PrologEngine Activation, int PredFid) EngineWith(
        string predName, int arity, string builtinName, int builtinArity,
        int ilThreshold)
    {
        Shumway.Builtins.StandardBuiltins.EnsureRegistered();
        MetaBuiltins.EnsureRegistered();
        int predFid = FunctorTable.Intern(
            AtomTable.Intern(predName, permanent: true).Id, arity);
        int bFid = FunctorTable.Intern(
            AtomTable.Intern(builtinName, permanent: true).Id, builtinArity);
        Assert.True(Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(bFid, out int builtinId),
            $"{builtinName}/{builtinArity} is not a registered builtin");

        var code = new byte[5];
        code[0] = (byte)Opcode.ExecuteBuiltin;
        BytecodeIO.WriteInt32(code, 1, builtinId);
        var pred = new CompiledPredicate(code, predFid, arity, 1,
            Array.Empty<CallSite>(), Array.Empty<int>());
        var module = new CompiledModule(new[] { pred },
            Array.Empty<string>(), Array.Empty<double>());
        var entry = new BundleEntry("w6mod_" + predName, source: "",
            compiledBytecode: CompiledModuleCodec.Encode(module),
            compiledIl: null,
            defined: new[] { new ShmoDefinedPredicate(
                new PredicateRef(predName, arity), PredicateVisibility.Public) });

        var e = new PrologEngine();
        e.IlPromotion.Threshold = ilThreshold;
        e.LoadBundle(new Bundle(new[] { entry }));
        // Bundle load is lazy; front-load the IL explicitly so these tests
        // exercise the Tier-1 path (the opt-in that replaced load-time warm).
        if (ilThreshold > 0) e.WarmAllCompilable();
        return (e, predFid);
    }

    [Fact]
    public void ExecuteBuiltin_Deterministic_Interpreter_AndIl()
    {
        // Tier-0 first (threshold 0): the interpreter path.
        var (e0, _) = EngineWith("w6ebatom0", 1, "atom", 1, ilThreshold: 0);
        Assert.True(e0.Query("w6ebatom0(foo).").Success);
        Assert.False(e0.Query("w6ebatom0(42).").Success);

        // Tier-1: the Warm path IL-compiles the predicate at LoadBundle —
        // failing eligibility would leave it unpromoted.
        var (e, fid) = EngineWith("w6ebatom1", 1, "atom", 1, ilThreshold: 1);
        Assert.True(e.IlPromotion.IsPromoted(fid),
            "ExecuteBuiltin predicate must IL-promote");
        for (int i = 0; i < 4; i++)
        {
            Assert.True(e.Query("w6ebatom1(foo).").Success);
            Assert.False(e.Query("w6ebatom1(42).").Success);
            Assert.False(e.Query("w6ebatom1(f(x)).").Success);
        }
    }

    [Fact]
    public void ExecuteBuiltin_Backtrackable_ResumesAtCallerContinuation()
    {
        // Tail between/3: on re-satisfaction the builtin's CP resumes at the
        // CALLER's continuation (BuiltinReturnPc = Cp), not inside the IL
        // method — the contract the emit must mirror from the interpreter.
        var (e, fid) = EngineWith("w6ebbet", 3, "between", 3, ilThreshold: 1);
        Assert.True(e.IlPromotion.IsPromoted(fid),
            "backtrackable ExecuteBuiltin predicate must IL-promote");
        for (int i = 0; i < 4; i++)
        {
            var xs = e.QueryAll("w6ebbet(1, 4, X).").Select(s => s.Get<int>("X")).ToList();
            Assert.Equal(new[] { 1, 2, 3, 4 }, xs);
            // A following goal exercises the caller-continuation resume.
            Assert.Equal(2, e.QueryAll("w6ebbet(1, 4, X), 0 =:= X mod 2.").Count());
            Assert.True(e.Query("w6ebbet(1, 4, 3).").Success);
            Assert.False(e.Query("w6ebbet(1, 4, 9).").Success);
        }
    }

    [Fact]
    public void ExecuteBuiltin_Meta_IsRejected_WithHonestReason()
    {
        // A META tail builtin (call/1) is deliberately outside the IL subset,
        // and the rejection names the real blocker (pre-fix it was invisible).
        // NOTE: the form is unreachable from the toolchain — the compiler
        // emits CallBuiltin for compile-time-known meta builtins and the
        // linker's Execute→ExecuteBuiltin rewrite only fires for tails that
        // were UNRESOLVED at compile (call/N never is). The interpreter's
        // ExecuteBuiltin case accordingly does not route meta dispatch either
        // (entry.Impl throws its loud dead-fallback guard if ever reached).
        var (e, fid) = EngineWith("w6ebcall", 1, "call", 1, ilThreshold: 1);
        Assert.False(e.IlPromotion.IsPromoted(fid));
        var pred = e.PrecompiledStaticPredicates[fid];
        var compiler = new IlPredicateCompiler();
        var map = e.PrecompiledStaticPredicates.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.False(compiler.CanCompile(pred, map));
        Assert.Equal("ExecuteBuiltin(meta)", compiler.DescribeRejection(pred, map));
    }
}
