using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A browser takes no function over 7,654,321 bytes, and refuses the
/// whole module that has one. A predicate is one function at the least, so
/// one whose function would be that large stays on Tier-0, and the rest of
/// its group compiles without it. The limits are lowered here: a predicate
/// of the real size takes seconds to compile.</summary>
public sealed class OversizePredicateTests : IDisposable
{
    private readonly int _bytes = WasmPredicateCompiler.MaxFunctionBytes;
    private readonly int _instructions = WasmPredicateCompiler.MaxMemberInstructions;

    public void Dispose()
    {
        WasmPredicateCompiler.MaxFunctionBytes = _bytes;
        WasmPredicateCompiler.MaxMemberInstructions = _instructions;
    }

    private const string Corpus = """
        small(a).
        other(X) :- small(X).
        large(1, a). large(2, b). large(3, c). large(4, d). large(5, e). large(6, f).
        large(7, g). large(8, h). large(9, i). large(10, j). large(11, k). large(12, l).
        """;

    private static List<WasmGroupMember> Members(out WasmGroupMember large)
    {
        var engine = new PrologEngine();
        engine.ConsultString(Corpus);
        engine.Query("true.");
        var members = new List<WasmGroupMember>();
        WasmGroupMember? found = null;
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
        {
            string name = NameOf(pred.FunctorId);
            if (!name.EndsWith("small") && !name.EndsWith("other") && !name.EndsWith("large")) continue;
            var member = new WasmGroupMember(pred, addr, null);
            members.Add(member);
            if (name.EndsWith("large")) found = member;
        }
        Assert.Equal(3, members.Count);
        large = found!;
        return members;
    }

    private static string NameOf(int functorId)
    {
        var (atom, _) = Shumway.Core.FunctorTable.Lookup(functorId);
        return Shumway.Core.AtomTable.GetById(atom)?.Name ?? "";
    }

    private static List<string> Baked(List<WasmGroupMember> members, List<string> refused)
    {
        var module = WasmRelocatableModule.BakeCompilable(members, new EngineWasmCompileEnv(),
            (m, why) => refused.Add($"{NameOf(m.Predicate.FunctorId)}: {why}"));
        return module.Members.Select(m => m.Name).ToList();
    }

    [Fact]
    public void WithTheLimitsABrowserHas_TheGroupCompilesWhole()
    {
        var refused = new List<string>();
        List<string> baked = Baked(Members(out _), refused);

        Assert.Equal(3, baked.Count);
        Assert.Empty(refused);
    }

    [Fact]
    public void APredicateWithTooManyInstructions_IsRefusedBeforeAnythingIsEmitted()
    {
        var members = Members(out WasmGroupMember large);
        WasmPredicateCompiler.MaxMemberInstructions = 30;
        var env = new EngineWasmCompileEnv();

        var alone = Assert.Throws<WasmOversizeException>(
            () => WasmPredicateCompiler.CompileGroup(new[] { large }, env));
        string? before = WasmPredicateCompiler.RefusalBeforeEmit(large, env);
        var refused = new List<string>();
        List<string> baked = Baked(members, refused);

        Assert.Equal(large.Predicate.FunctorId, alone.FunctorId);
        Assert.Contains("larger than a browser takes", before);
        Assert.Equal(2, baked.Count);
        Assert.DoesNotContain(baked, name => name.EndsWith("large"));
        string only = Assert.Single(refused);
        Assert.Contains("large", only);
        Assert.Contains("instructions", only);
    }

    /// <summary>The instruction count is an estimate: what decides is the
    /// function as written.</summary>
    [Fact]
    public void APredicateWhoseFunctionComesOutTooLarge_IsLeftOutAndTheRestCompiled()
    {
        var members = Members(out WasmGroupMember large);
        var env = new EngineWasmCompileEnv();
        // Between what the small ones come to and what the large one does.
        int smallest = members.Where(m => m != large)
            .Max(m => WasmPredicateCompiler.CompileGroup(new[] { m }, env).Module.Length);
        int whole = WasmPredicateCompiler.CompileGroup(new[] { large }, env).Module.Length;
        Assert.True(whole > 2 * smallest, $"large {whole} bytes, the others at most {smallest}");
        WasmPredicateCompiler.MaxFunctionBytes = (whole + smallest) / 2;

        var alone = Assert.Throws<WasmOversizeException>(
            () => WasmPredicateCompiler.CompileGroup(new[] { large }, env));
        Assert.Null(WasmPredicateCompiler.RefusalBeforeEmit(large, env));
        var refused = new List<string>();
        List<string> baked = Baked(members, refused);

        Assert.Equal(large.Predicate.FunctorId, alone.FunctorId);
        Assert.Equal(2, baked.Count);
        Assert.DoesNotContain(baked, name => name.EndsWith("large"));
        string only = Assert.Single(refused);
        Assert.Contains("large", only);
        Assert.Contains("bytes", only);
    }
}
