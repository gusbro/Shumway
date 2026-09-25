using Shumway.Builtins;
using Shumway.Compiler.Wasm;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>call/1 has had an inline form since the tier shipped. Every
/// wider arity stepped aside, and a meta-call builtin cannot be requested
/// directly either, so each one DEOPTED.
///
/// <para>That is not a corner. <c>maplist/3</c> is <c>call(G, X, Y)</c> in a
/// loop, so a library built on maplist deopts once per element: measured on
/// clp(Z) in a browser, 1,336,036 of 1,336,496 deopts in a single goal --
/// 100% -- at one site, <c>lists$maplist/3</c>.</para>
///
/// <para>This pins the question the emitter asks. Which arities it then
/// serves is the emitter's business, but the ANSWER has to name call/N and
/// say how many arguments it appends, or the two halves drift.</para>
/// </summary>
public sealed class InlineMetaCallNTests
{
    // The builtin registry is populated when an engine is built, so one
    // has to exist before call/N has an id at all.
    static InlineMetaCallNTests() => _ = new Shumway.Embedding.PrologEngine();

    // The registry is keyed by FUNCTOR, so the id comes the way the
    // emitter's own call sites get it.
    private static int BuiltinId(string name, int arity)
    {
        int fid = Shumway.Core.FunctorTable.Intern(
            Shumway.Core.AtomTable.Intern(name, permanent: true).Id, arity);
        Assert.True(BuiltinsRegistry.TryGetByFunctor(fid, out int id),
            $"{name}/{arity} is not a builtin");
        return id;
    }

    private static readonly EngineWasmCompileEnv Env = new();

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(8, 7)]
    public void CallNIsInlineAndSaysHowManyItAppends(int arity, int appended)
    {
        Assert.True(Env.IsInlineMetaCallN(BuiltinId("call", arity), out int n),
            $"call/{arity} is not recognised");
        Assert.Equal(appended, n);
    }

    /// <summary>call/1 keeps its OWN form, which takes the goal as it
    /// stands. Answering yes here would send it down the appending path
    /// with nothing to append.</summary>
    [Fact]
    public void CallOneIsNotTheAppendingForm()
    {
        Assert.False(Env.IsInlineMetaCallN(BuiltinId("call", 1), out _));
        Assert.True(Env.IsInlineMetaCall(BuiltinId("call", 1)));
    }

    /// <summary>And the two questions do not overlap: a builtin is at most
    /// one of them, so the emitter's if-chain cannot take both branches.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TheTwoFormsAreDisjoint(int arity)
    {
        int id = BuiltinId("call", arity);
        Assert.False(Env.IsInlineMetaCall(id) && Env.IsInlineMetaCallN(id, out _),
            $"call/{arity} answers to both inline forms");
    }

    /// <summary>ANTI-VACUITY: something that is NOT call/N is rejected, so
    /// the predicate means "call/N" and not "any builtin".</summary>
    [Theory]
    [InlineData("=", 2)]
    [InlineData("atom", 1)]
    [InlineData("put_attr", 3)]
    public void OtherBuiltinsAreNotTheAppendingForm(string name, int arity)
        => Assert.False(Env.IsInlineMetaCallN(BuiltinId(name, arity), out _));
}
