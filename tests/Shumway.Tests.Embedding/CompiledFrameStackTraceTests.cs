using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A predicate running compiled is a frame of the reported call
/// stack. Its environment holds a resume marker where a bytecode frame holds
/// a return address, and the marker names the predicate.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class CompiledFrameStackTraceTests : IDisposable
{
    private readonly int _savedThreshold = IlPromotionStore.DefaultPersistedThreshold;

    public CompiledFrameStackTraceTests() => IlPromotionStore.DefaultPersistedThreshold = 0;

    public void Dispose() => IlPromotionStore.DefaultPersistedThreshold = _savedThreshold;

    private const string Program = """
        :- module(frames, [checked/2]).
        checked(X, L) :- must_be_atom(X), atom_length(X, L).
        must_be_atom(X) :- atom_length(X, _).
        """;

    private static int Fid(string n, int a) => FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, a);

    [Fact]
    public void ACompiledCaller_IsAFrameOfTheStack()
    {
        var e = PrologEngine.FromBundle(BundleReader.FromBytes(ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(Program, "frames", ShmoBuildMode.Release) },
            // Every predicate a root, as an engine library links: no region
            // absorbs must_be_atom/1 into checked/2.
            Library = true,
            StripSource = true,
            IncludeCompiledIl = true,
        }).Bytes!));
        // ANTI-VACUITY: checked/2 runs compiled, and the error is its callee's.
        Assert.True(e.IlPromotion.IsPromoted(Fid("frames$checked", 2)), "checked/2 is not bound");
        var ex = Record.Exception(() => e.Query("frames:checked(_, _)."));
        Assert.Contains("instantiation_error", ex?.Message ?? "");
        Assert.Contains(e.LastErrorStackTraceWithPositions, f => f.Name == "frames$checked" && f.Arity == 2);
    }
}
