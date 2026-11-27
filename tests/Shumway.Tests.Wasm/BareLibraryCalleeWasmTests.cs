using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-056, amendment, on the wasm tier: a predicate that calls a
/// predicate of a library without a module directive by its bare name
/// promotes to wasm, as it does to IL, and follows the name when the program
/// defines it later.</summary>
public sealed class BareLibraryCalleeWasmTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway-bare-callee-wasm-" + Guid.NewGuid().ToString("N"));

    public BareLibraryCalleeWasmTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void WriteLibrary(string library)
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(library, "steps", ShmoBuildMode.Release) },
            EntryPoints = Array.Empty<PredicateRef>(),
            Library = true,
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        File.WriteAllBytes(Path.Combine(_dir, "steps.shum"), BundleWriter.ToBytes(link.Bundle!, includeCompiledBytecode: true));
    }

    private static HashSet<string> Promoted(IEnumerable<WasmGroupMember> members)
        => members.Select(m => FunctorTable.Lookup(m.Predicate.FunctorId))
            .Select(f => $"{AtomTable.GetById(f.AtomId)?.Name}/{f.Arity}").ToHashSet();

    [Theory]
    [InlineData("leaf", @"
step(X, Y) :- Y is X + 1.
")]
    [InlineData("rule", @"
step(X, Y) :- succ(X, Y), check(Y).
check(Y) :- Y > 0.
")]
    public void ACallerOfALibraryPredicate_PromotesToWasm(string tag, string library)
    {
        WriteLibrary(library);
        var (e, members, _) = TieredEngine.BuildWithWorld("", wasmThreshold: 2);
        e.AddLibraryDirectory(_dir);
        e.ConsultString($@":- use_module(library(steps)).
count_{tag}(N, N) :- !.
count_{tag}(I, N) :- step(I, J), count_{tag}(J, N).
");
        for (int i = 0; i < 4; i++) Assert.True(e.Query($"count_{tag}(0, 2000).").Success);

        var promoted = Promoted(members);
        Assert.True(promoted.Contains($"user$count_{tag}/2"),
            $"count_{tag}/2 did not promote; promoted: {string.Join(", ", promoted)}");
        Assert.True(e.Query($"count_{tag}(0, 3000).").Success, "the promoted caller answers");
    }

    [Fact]
    public void APromotedCaller_FollowsTheNameWhenTheProgramDefinesIt()
    {
        WriteLibrary(@"
step(X, Y) :- Y is X + 1.
");
        var (e, members, _) = TieredEngine.BuildWithWorld("", wasmThreshold: 2);
        e.AddLibraryDirectory(_dir);
        e.ConsultString(@":- use_module(library(steps)).
last(I, N, I) :- I >= N, !.
last(I, N, L) :- step(I, J), last(J, N, L).
");
        for (int i = 0; i < 4; i++) Assert.True(e.Query("last(0, 2001, _).").Success);
        var promoted = Promoted(members);
        Assert.True(promoted.Contains("user$last/3"),
            $"last/3 did not promote; promoted: {string.Join(", ", promoted)}");
        Assert.Equal("2001", e.Query("last(0, 2001, L).").Bindings["L"].ToString());

        e.ConsultString(@"
step(X, Y) :- Y is X + 2.
");
        Assert.Equal("2002", e.Query("last(0, 2001, L).").Bindings["L"].ToString());
    }
}
