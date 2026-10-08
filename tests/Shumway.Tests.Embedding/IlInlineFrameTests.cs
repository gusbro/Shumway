using Shumway.Builtins;
using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-058 item 6: a region pushes and restores its choice points
/// inline. The frame it writes and the state it restores are those of
/// PushChoicePoint, RetryMeElse and TrustMe, cell by cell, in a region compiled
/// at run time and in one loaded from a bundle; and with a hook on, the region
/// calls the methods, which carry it.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class IlInlineFrameTests : IDisposable
{
    // These measure regions, which continuation methods turn off (ADR-061).
    private readonly bool _savedCpsMode = IlPredicateCompiler.CpsMode;

    public IlInlineFrameTests() => IlPredicateCompiler.CpsMode = false;

    public void Dispose() => IlPredicateCompiler.CpsMode = _savedCpsMode;

    // Three alternatives: the push, a retry (the middle one) and a trust (the
    // last). The probe runs after each answer, with the choice point on top.
    private const string Program = """
        alt(X, Y) :- X = 1, Y = a.
        alt(X, Y) :- X = 2, Y = f(X).
        alt(X, Y) :- X = 3, Y = [X].
        walk(X-Y) :- alt(X, Y), '$frame_probe'.
        """;

    // walk/1 calls alt/2 directly, so the linker absorbs it into walk's region
    // as the runtime promotion does.
    private const string Goal = "findall(P, walk(P), L), L == [1-a, 2-f(2), 3-[3]].";

    private static readonly List<long[]> Probes = new();

    private static void RegisterProbe()
        => BuiltinsRegistry.Register("$frame_probe", 0, a =>
        {
            int b = a._b;
            int arity = (int)a.GetStack(b + Activation.CpArityOffset).Data;
            int size = Activation.CpSize(arity);
            var snap = new long[10 + size];
            snap[0] = b; snap[1] = a._e; snap[2] = a._cp; snap[3] = a._b0; snap[4] = a._hb;
            snap[5] = a._heapTop; snap[6] = a._stackTop; snap[7] = a._bindingTrailTop;
            snap[8] = a._extraTrailTop; snap[9] = a._currentViewGen;
            for (int i = 0; i < size; i++) snap[10 + i] = a.GetStack(b + i).Data;
            Probes.Add(snap);
            return true;
        });

    private static PrologEngine Runtime()
    {
        var e = new PrologEngine { Out = new StringWriter() };
        e.IlPromotion.Threshold = 1;
        e.ConsultString(Program);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(e.Query(Goal).Success);
            e.IlPromotion.WaitForPendingPromotions();
        }
        return e;
    }

    // Through the linker: it is the path that region-compiles persisted IL.
    private static PrologEngine FromBundle()
    {
        var r = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(":- public walk/1.\n" + Program, "frames") },
            EntryPoints = new[] { new PredicateRef("walk", 1) },
            IncludeCompiledIl = true,
        });
        Assert.True(r.Success, string.Join(", ", r.Diagnostics.Select(d => d.Message)));
        var e = new PrologEngine { Out = new StringWriter() };
        e.LoadBundle(BundleReader.FromBytes(r.Bytes!));
        Assert.True(e.Query(Goal).Success);
        return e;
    }

    private static List<long[]> Run(PrologEngine e)
    {
        Probes.Clear();
        Assert.True(e.Query(Goal).Success);
        return Probes.Select(p => (long[])p.Clone()).ToList();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheInlineFrameIsTheMethodsFrame(bool bundle)
    {
        RegisterProbe();
        int sitesBefore = IlPredicateCompiler.InlineFrameSites;
        var inline = Run(bundle ? FromBundle() : Runtime());
        // ANTI-VACUITY: the regions of this program were emitted inline.
        Assert.True(IlPredicateCompiler.InlineFrameSites > sitesBefore,
            "no choice point was emitted inline");

        var hooked = bundle ? FromBundle() : Runtime();
        List<long[]> calls;
        var stderr = new StringWriter();
        var savedErr = Console.Error;
        try
        {
            Console.SetError(stderr);
            Activation.TraceCpStack = true;
            calls = Run(hooked);
        }
        finally
        {
            Activation.TraceCpStack = false;
            Console.SetError(savedErr);
        }

        Assert.Equal(3, inline.Count);
        Assert.Equal(3, calls.Count);
        for (int i = 0; i < 3; i++)
            Assert.True(inline[i].SequenceEqual(calls[i]),
                $"probe {i}: inline [{string.Join(",", inline[i].Select(x => x.ToString("X")))}]"
                + $" methods [{string.Join(",", calls[i].Select(x => x.ToString("X")))}]");

        // The first probe sees alt/2's choice point, its BP a region marker.
        int arity = (int)inline[0][10 + Activation.CpArityOffset];
        Assert.Equal(2, arity);
        int bp = (int)inline[0][10 + Activation.CpBpOffset(arity)];
        Assert.True(Activation.IsResumeMarker(bp), $"BP 0x{bp:X} is not a marker");
        // With the hook on, that push went through the method, which traced it.
        Assert.Contains($"bp=0x{bp:X} arity=2", stderr.ToString());
    }

    // The first clause calls b/0 and fails; the second, resumed through the
    // region's own restore, cuts at its neck, which must remove a/1's choice
    // point: the third clause must not answer. b/0 is not a fact, which the
    // region would inline instead of calling.
    private const string CutProgram = """
        b :- d(_).
        d(1).
        d(2).
        a(_) :- b, fail.
        a(X) :- !, X = 1.
        a(2).
        t(X) :- a(X), X > 0.
        """;

    [Fact]
    public void AResumedClauseCutsToTheRestoredBarrier()
    {
        var plain = new PrologEngine();
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(CutProgram);
        Assert.True(plain.Query("findall(X, t(X), L), L == [1].").Success);

        var tiered = new PrologEngine();
        tiered.IlPromotion.Threshold = 1;
        tiered.ConsultString(CutProgram);
        int sitesBefore = IlPredicateCompiler.InlineFrameSites;
        for (int i = 0; i < 4; i++)
        {
            tiered.Query("findall(X, t(X), L).");
            tiered.IlPromotion.WaitForPendingPromotions();
        }
        // ANTI-VACUITY: t/1 promoted, and its region pushed its own frames.
        var promoted = tiered.IlPromotion.PromotedFunctorIds().Select(fid =>
        {
            var (atom, arity) = FunctorTable.Lookup(fid);
            string n = AtomTable.GetById(atom)?.Name ?? "";
            return $"{n[(n.LastIndexOf('$') + 1)..]}/{arity}";
        }).ToList();
        Assert.True(promoted.Contains("a/1"), "a/1 did not promote: " + string.Join(" ", promoted));
        Assert.True(IlPredicateCompiler.InlineFrameSites > sitesBefore,
            "no choice point was emitted over the region's locals");
        Assert.True(tiered.Query("findall(X, t(X), L), L == [1].").Success,
            "the neck cut left a/1's choice point alive");
    }
}
