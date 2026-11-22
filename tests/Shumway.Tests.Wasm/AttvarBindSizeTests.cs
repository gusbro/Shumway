using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>Every head argument that can meet an attributed variable binds
/// it by a call to one function of the module. Inline, the binding was some
/// 420 instructions at each such argument, a fifth of a large module, in
/// code a program with no attributed variable never runs.</summary>
public sealed class AttvarBindSizeTests(ITestOutputHelper o)
{
    private const string Corpus = """
        narrow(a).
        wide(a, b, c, d, e, f, g, h, i, j, k, l, m, n, o, p, q).
        """;

    private static int InstructionsOf(PrologEngine engine, string name, int arity)
    {
        var env = new EngineWasmCompileEnv();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
        {
            var (atom, ar) = Shumway.Core.FunctorTable.Lookup(pred.FunctorId);
            if (ar != arity || !(Shumway.Core.AtomTable.GetById(atom)?.Name ?? "").EndsWith(name))
                continue;
            byte[] bytes = WasmPredicateCompiler.CompileGroup(
                new[] { new WasmGroupMember(pred, addr, null) }, env, cps: false).Module;
            var module = WebAssembly.Module.ReadFromBinary(new MemoryStream(bytes));
            return module.Codes.Sum(body => body.Code.Count);
        }
        throw new InvalidOperationException($"{name}/{arity} is not in the program");
    }

    [Fact]
    public void AnArgumentMore_CostsFarLessThanTheBindingItself()
    {
        var engine = new PrologEngine();
        engine.ConsultString(Corpus);
        engine.Query("true.");

        int narrow = InstructionsOf(engine, "narrow", 1);
        int wide = InstructionsOf(engine, "wide", 17);
        int each = (wide - narrow) / 16;
        o.WriteLine($"narrow/1 {narrow}, wide/17 {wide}: {each} instructions for an argument more");

        // The function is in both modules, so the difference is the sites.
        Assert.True(each < 250, $"{each} instructions for an argument more");
        Assert.True(each > 50, $"{each} instructions for an argument more: is the argument unified at all?");
    }
}
