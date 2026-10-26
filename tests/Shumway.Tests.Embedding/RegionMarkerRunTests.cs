using System;
using System.IO;
using System.Linq;
using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A region's markers are one run of consecutive ids: a return inside
/// the region and a fail onto one of its own choice points find the cursor by
/// subtraction. The run is reserved before the region emits a marker, when it
/// compiles or, for persisted IL, when the bundle loads; a region whose marker
/// was interned alone before decodes through the pairs table and answers the
/// same.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class RegionMarkerRunTests : IDisposable
{
    // These measure regions, which continuation methods turn off (ADR-061).
    private readonly bool _savedCpsMode = IlPredicateCompiler.CpsMode;

    public RegionMarkerRunTests() => IlPredicateCompiler.CpsMode = false;

    public void Dispose() => IlPredicateCompiler.CpsMode = _savedCpsMode;

    // nrev returns to its own call site inside the region; pick's clauses give
    // the region choice points of its own, resumed by the fail handler. The tag
    // keeps each test's predicates apart: compiled IL is cached per predicate.
    private static string Program(string tag) => $$"""
        {{tag}}_nrev([], []).
        {{tag}}_nrev([H|T], R) :- {{tag}}_nrev(T, RT), {{tag}}_app(RT, [H], R).
        {{tag}}_app([], L, L).
        {{tag}}_app([H|T], L, [H|R]) :- {{tag}}_app(T, L, R).
        {{tag}}_pick(X, [X|_]).
        {{tag}}_pick(X, [_|T]) :- {{tag}}_pick(X, T).
        {{tag}}_root(L, R, X) :- {{tag}}_nrev(L, R), {{tag}}_pick(X, R), X > 3.
        """;

    private static string Goal(string tag)
        => $"findall(X, {tag}_root([1,2,3,4,5,6], _, X), Xs), Xs == [6,5,4], "
         + $"{tag}_root([1,2,3,4,5,6], R, 6), R == [6,5,4,3,2,1].";

    private static void PromoteOn(PrologEngine e, string tag)
    {
        e.IlPromotion.Threshold = 1;
        for (int i = 0; i < 3; i++)
        {
            Assert.True(e.Query(Goal(tag)).Success);
            Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        }
    }

    private static string NameOf(int fid)
    {
        var (atom, _) = FunctorTable.Lookup(fid);
        return AtomTable.GetById(atom)?.Name ?? "";
    }

    private static bool Named(int fid, string name, int arity)
    {
        if (!FunctorTable.TryLookup(fid, out var entry) || entry.Arity != arity) return false;
        string n = NameOf(fid);
        return n == name || n.EndsWith("$" + name, StringComparison.Ordinal);
    }

    // The predicate's functor is the module-qualified one; the plain name is
    // the query's term.
    private static int ConsultedFunctor(string name, int arity)
    {
        var found = Enumerable.Range(0, FunctorTable.IdLimit)
            .Where(fid => Named(fid, name, arity) && NameOf(fid).Contains('$')).ToList();
        Assert.True(found.Count == 1, $"{name}/{arity}: {found.Count} qualified functors");
        return found[0];
    }

    [Fact]
    public void ARegionsMarkersFormARun()
    {
        int runs = IlPredicateCompiler.RegionMarkerRuns;
        var e = new PrologEngine { Out = new StringWriter() };
        e.ConsultString(Program("mra"));
        PromoteOn(e, "mra");
        // ANTI-VACUITY: a region reserved its run.
        Assert.True(IlPredicateCompiler.RegionMarkerRuns > runs, "no region reserved a run");
        int fid = e.IlPromotion.PromotedFunctorIds().Single(f => Named(f, "mra_root", 3));
        Assert.True(Activation.TryGetResumeMarker(fid, 1, out int m1));
        Assert.True(Activation.TryGetResumeMarker(fid, 2, out int m2));
        Assert.Equal(m1 + 1, m2);
        Assert.Equal(m1, Activation.ReserveResumeMarkerRange(fid, 1, 2));
    }

    [Fact]
    public void AMarkerMintedAloneSendsTheRegionToTheTable()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.IlPromotion.Threshold = 0;
        e.ConsultString(Program("mrb"));
        Assert.True(e.Query(Goal("mrb")).Success);
        int fid = ConsultedFunctor("mrb_root", 3);
        int alone = Activation.EncodeResumeMarker(fid, 2);
        int misses = IlPredicateCompiler.RegionMarkerRunMisses;
        PromoteOn(e, "mrb");
        Assert.Contains(fid, e.IlPromotion.PromotedFunctorIds());
        // ANTI-VACUITY: the region compiled without a run.
        Assert.True(IlPredicateCompiler.RegionMarkerRunMisses > misses,
            "the region got a run over a marker minted alone");
        Assert.Equal(alone, Activation.EncodeResumeMarker(fid, 2));
        Assert.True(e.Query(Goal("mrb")).Success);
    }

    // Through the linker: the path that region-compiles persisted IL. The
    // run's patch site names the region's root.
    private static (Bundle Bundle, IlPatchSite Run) Link(string tag)
    {
        var r = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[]
            {
                ShmoCompiler.CompileSource($":- public {tag}_root/3.\n" + Program(tag), "runs"),
            },
            EntryPoints = new[] { new PredicateRef($"{tag}_root", 3) },
            IncludeCompiledIl = true,
        });
        Assert.True(r.Success, string.Join(", ", r.Diagnostics.Select(d => d.Message)));
        var bundle = BundleReader.FromBytes(r.Bytes!);
        var runs = bundle.Entries
            .Where(en => en.CompiledIlPatches is { Length: > 0 })
            .SelectMany(en => IlPatchSiteCodec.Decode(en.CompiledIlPatches!))
            .Where(s => s.Kind == IlPatchKind.ResumeMarkerRange)
            .ToList();
        // ANTI-VACUITY: a region was persisted, with a run to resolve at load.
        var run = runs.FirstOrDefault(s => s.Arity == 3 && s.Name.EndsWith($"{tag}_root", StringComparison.Ordinal));
        Assert.True(run is not null, "no persisted region rooted at " + tag + "_root/3");
        Assert.True(run!.Count > 1);
        return (bundle, run);
    }

    private static PrologEngine Load(Bundle bundle, string tag, int fid)
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.LoadBundle(bundle);
        PromoteOn(e, tag);
        Assert.Contains(fid, e.IlPromotion.PromotedFunctorIds());   // bound from the bundle
        return e;
    }

    [Fact]
    public void APersistedRegionGetsItsRunAtLoad()
    {
        var (bundle, run) = Link("mrc");
        int fid = FunctorTable.Intern(AtomTable.Intern(run.Name).Id, run.Arity);
        Load(bundle, "mrc", fid);
        int b = Activation.ReserveResumeMarkerRange(fid, run.Cursor, run.Count);
        Assert.True(b >= Activation.ResumeMarkerBase, "the loader did not reserve the region's run: "
            + string.Join(" ", Enumerable.Range(run.Cursor, run.Count).Select(c =>
                Activation.TryGetResumeMarker(fid, c, out int m) ? $"{c}:{m - Activation.ResumeMarkerBase}" : $"{c}:-")));
        Assert.True(Activation.TryGetResumeMarker(fid, run.Cursor + 1, out int m2));
        Assert.Equal(b + 1, m2);
    }

    [Fact]
    public void APersistedRegionOverAMarkerMintedAloneAnswersThroughTheTable()
    {
        // The marker is minted before the link: the linker's warm pass emits
        // the region in this process and would reserve the run itself. The
        // root's name comes from a probe bundle of another tag.
        var (_, probe) = Link("mrq");
        string name = probe.Name[..^"mrq_root".Length] + "mrd_root";
        int fid = FunctorTable.Intern(AtomTable.Intern(name).Id, 3);
        int alone = Activation.EncodeResumeMarker(fid, 2);
        var (bundle, run) = Link("mrd");
        Assert.Equal(name, run.Name);
        Assert.Equal(1, run.Cursor);
        var e = Load(bundle, "mrd", fid);
        Assert.Equal(-1, Activation.ReserveResumeMarkerRange(fid, run.Cursor, run.Count));
        Assert.Equal(alone, Activation.EncodeResumeMarker(fid, 2));
        Assert.True(e.Query(Goal("mrd")).Success);
    }
}
