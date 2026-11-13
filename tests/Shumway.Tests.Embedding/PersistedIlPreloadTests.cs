using System.Diagnostics;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A bundle's compiled code on offer has its image loaded on a worker
/// while the engine starts, not when the first predicate earns its code: the
/// load is off the run, and nothing is bound before it is earned.</summary>
public sealed class PersistedIlPreloadTests
{
    [Fact]
    public void TheImageOfCodeOnOfferLoadsBeforeAnyQuery()
    {
        // A name of its own: an image no other test loaded in this process.
        string p = "ppl_" + Guid.NewGuid().ToString("N")[..12];
        string program = $"""
            :- public {p}/2.
            {p}([], 0).
            {p}([_|T], N) :- {p}(T, M), N is M + 1.
            """;
        var r = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(program, p) },
            EntryPoints = new[] { new PredicateRef(p, 2) },
            IncludeCompiledIl = true,
        });
        Assert.True(r.Success, string.Join(", ", r.Diagnostics.Select(d => d.Message)));
        var bundle = BundleReader.FromBytes(r.Bytes!);
        var entry = bundle.Entries.Single();
        Assert.False(BundleLoader.IsPersistedIlCached(entry));

        var e = new PrologEngine();
        // The product's threshold: the code waits on offer until earned (this
        // suite binds bundle code at load).
        e.IlPromotion.PersistedThreshold = BundleCodeAtLoad.ProductDefault;
        e.LoadBundle(bundle);
        int fid = FunctorTable.Intern(AtomTable.Intern(p).Id, 2);
        Assert.True(e.IlPromotion.HasOffer(fid));
        var clock = Stopwatch.StartNew();
        while (!BundleLoader.IsPersistedIlCached(entry))
        {
            Assert.True(clock.ElapsedMilliseconds < 30_000,
                "the image did not load ahead: " + IlCompileWorker.Describe());
            Thread.Sleep(5);
        }
        // Loaded, not bound: no query has run.
        Assert.True(e.IlPromotion.HasOffer(fid));
        Assert.False(e.IlPromotion.IsPromoted(fid));
        Assert.True(e.Query($"{p}([a, b, c], N), N == 3.").Success);
    }
}
