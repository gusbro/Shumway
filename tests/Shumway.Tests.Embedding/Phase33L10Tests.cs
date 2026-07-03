using Shumway.Core;
using Shumway.Embedding;
using Shumway.Compiler.Il;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 L10 — the audit finding "multi-arg indexed shapes
/// (switch_on_*_arg) are not IL-describable" was REFUTED: the corpus census
/// that produced it was mis-driven by a DescribeRejection classifier bug
/// (typed switch opcodes always landed in the "unsupported" set, masking the
/// true cause — 1663 of 1666 were unresolved calls from consult-failure
/// artifacts, 3 were a missing float pool). These tests pin both facts:
/// the multi-arg shape compiles, and the classifier now reports the truth.
/// </summary>
public class Phase33L10Tests
{
    private const string MultiArgSource =
        ":- public p/3.\n" +
        "p(a, x, 1).\n" +
        "p(a, y, 2).\n" +
        "p(b, x, 3).\n" +
        "p(b, y, 4).\n" +
        "p(c, z, 5).\n";

    private static (Shumway.Compiler.Wam.CompiledPredicate Pred,
        Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate> Map)
        CompileP(string source)
    {
        var e = new PrologEngine();
        e.ConsultString(source);
        Assert.True(e.Query("p(a, x, _).").Success);
        var map = new Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>();
        foreach (var (k, v) in e.StaticPredicateCache) map[k] = v;
        int fid = FunctorTable.Intern(AtomTable.Intern("p").Id, 3);
        Assert.True(map.ContainsKey(fid), "p/3 not in the static cache");
        return (map[fid], map);
    }

    private static bool HasOpcode(byte[] code, params Opcode[] wanted)
    {
        int pc = 0;
        while (pc < code.Length)
        {
            var op = (Opcode)code[pc];
            if (Array.IndexOf(wanted, op) >= 0) return true;
            var inf = OpcodeTable.Get(code[pc]);
            if (inf.Size <= 0) return false;
            pc += inf.Size;
        }
        return false;
    }

    [Fact]
    public void MultiArgIndexedShape_IsIlCompilable()
    {
        var (pred, map) = CompileP(MultiArgSource);
        // The shape under test: a switch_on_arg cascade with typed *Arg tables.
        Assert.True(HasOpcode(pred.Bytecode, Opcode.SwitchOnArg),
            "expected a multi-arg switch cascade");
        Assert.True(HasOpcode(pred.Bytecode,
            Opcode.SwitchOnAtomArg, Opcode.SwitchOnIntegerArg, Opcode.SwitchOnStructureArg),
            "expected a typed *Arg switch table");

        var compiler = new IlPredicateCompiler();
        Assert.True(compiler.CanCompile(pred, map),
            "multi-arg indexed shape must be IL-compilable: "
            + compiler.DescribeRejection(pred, map));
        Assert.NotNull(compiler.Compile(pred, map));
    }

    [Fact]
    public void MultiArgIndexedShape_RunsUnderIlPromotion()
    {
        // End-to-end: force early promotion and make sure dispatch through the
        // promoted multi-arg predicate answers correctly on every key path,
        // including bucket backtracking and the var-arg fallthrough.
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString(MultiArgSource);
        for (int i = 0; i < 4; i++)   // warm + promoted passes
        {
            Assert.True(e.Query("p(a, y, V), V == 2.").Success);
            Assert.True(e.Query("p(b, x, V), V == 3.").Success);
            Assert.True(e.Query("p(c, z, V), V == 5.").Success);
            Assert.False(e.Query("p(c, x, _).").Success);
            Assert.Equal(5, e.QueryAll("p(_, _, _).").Count());
            Assert.Equal(2, e.QueryAll("p(a, _, _).").Count());
        }
    }

    // ---- Bundle-wide calleeMap (the REAL coverage gap the user's correct
    // re-test methodology exposed): CompileEntryToIl used to warm a
    // SINGLE-entry engine, so every cross-module Call rejected its caller
    // as call->unresolved — 26% IL coverage on a real multi-module corpus
    // bundle (6.8% among cross-module callers). With the shared whole-bundle
    // warm engine + per-entry emitOnly: 76.9% / 71.0% (full testGen). ----

    [Fact]
    public void CrossModuleCalls_GetIl_InMultiEntryBundle()
    {
        // pb is called NON-last from pa (a real Call site, the shape that was
        // rejected); two clauses everywhere so no unfold erases the calls.
        var objA = ShmoCompiler.CompileSource(
            ":- public pa/2.\n"
            + "pa(0, 0).\n"
            + "pa(N, R) :- N > 0, pb(N, R0), R is R0 + 1.\n",
            moduleNameFallback: "l10moda");
        var objB = ShmoCompiler.CompileSource(
            ":- public pb/2.\n"
            + "pb(0, 0).\n"
            + "pb(N, R) :- N > 0, R is N * 2.\n",
            moduleNameFallback: "l10modb");
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { objA, objB },
            EntryPoints = new[] { new PredicateRef("pa", 2) },
            BakePrelude = true,
            IncludeCompiledIl = true,
            // Strip the WAM: execution is FORCED through the IL, cross-entry
            // (pa's IL in modA dispatches pb by fid into modB's IL).
            StripWam = true,
        });
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        var bundle = BundleReader.FromBytes(result.Bytes!);

        var aMethods = Shumway.Compiler.Il.IlPersistedEntryCodec.Decode(
            bundle.Entries.First(en => en.ModuleName == "l10moda").CompiledIlEntries!)
            .Select(pe => (pe.Name, pe.Arity)).ToHashSet();
        var bMethods = Shumway.Compiler.Il.IlPersistedEntryCodec.Decode(
            bundle.Entries.First(en => en.ModuleName == "l10modb").CompiledIlEntries!)
            .Select(pe => (pe.Name, pe.Arity)).ToHashSet();
        // The cross-module CALLER got IL (the pre-fix rejection), each
        // predicate ships exactly once, in its own entry.
        Assert.Contains(("pa", 2), aMethods);
        Assert.DoesNotContain(("pb", 2), aMethods);
        Assert.Contains(("pb", 2), bMethods);
        Assert.DoesNotContain(("pa", 2), bMethods);

        var e = PrologEngine.FromBundle(bundle);
        Assert.True(e.Query("pa(3, R), R == 7.").Success);
        Assert.True(e.Query("pa(0, R), R == 0.").Success);
        Assert.False(e.Query("pa(-1, _).").Success);
    }

    [Fact]
    public void DescribeRejection_ReportsUnresolvedCall_NotSwitchOpcodes()
    {
        // p/3 keeps the multi-arg indexed shape but every body calls a
        // predicate that is NOT in the calleeMap. Pre-fix, DescribeRejection
        // reported "SwitchOnAtomArg,…" — the typed dispatch skeleton — and
        // hid the real blocker.
        // helper needs TWO clauses: a single-clause pure rule/fact would be
        // unfolded into the callers at query setup and the Call site under
        // test would vanish (the MetaWrapperUnfold test-design lesson).
        var (pred, map) = CompileP(
            ":- public p/3.\n" +
            ":- public helper/1.\n" +
            // A trailing builtin keeps helper a NON-last goal → a real Call
            // site (a single-goal body compiles to Execute, which dispatches
            // by fid at run time and never consults the calleeMap).
            "p(a, x, R) :- helper(R), R >= 0.\n" +
            "p(a, y, R) :- helper(R), R >= 0.\n" +
            "p(b, x, R) :- helper(R), R >= 0.\n" +
            "p(b, y, R) :- helper(R), R >= 0.\n" +
            "helper(0).\n" +
            "helper(1).\n");
        Assert.True(HasOpcode(pred.Bytecode,
            Opcode.SwitchOnAtomArg, Opcode.SwitchOnIntegerArg, Opcode.SwitchOnStructureArg));
        // Remove the callee from the map to force the unresolved-call reject.
        int helperFid = FunctorTable.Intern(AtomTable.Intern("helper").Id, 1);
        map.Remove(helperFid);

        var compiler = new IlPredicateCompiler();
        Assert.False(compiler.CanCompile(pred, map));
        Assert.Equal("call->unresolved", compiler.DescribeRejection(pred, map));
    }
}
