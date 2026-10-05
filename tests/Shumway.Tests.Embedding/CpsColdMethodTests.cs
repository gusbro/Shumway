#if !NETFRAMEWORK
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Shumway.Compiler.Il;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-061: in a bundle that keeps its bytecode, a predicate with
/// continuation methods has no method of its own. Its cold method is the
/// whole predicate, entered by the dispatch loop at any cursor: an entry, a
/// continuation, a choice point's alternative, an instruction boundary.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class CpsColdMethodTests : IDisposable
{
    private readonly bool _savedCpsMode = IlPredicateCompiler.CpsMode;
    private readonly int _savedThreshold = IlPromotionStore.DefaultPersistedThreshold;

    public void Dispose()
    {
        IlPromotionStore.DefaultPersistedThreshold = _savedThreshold;
        IlPredicateCompiler.CpsMode = _savedCpsMode;
    }

    // sel/3 leaves a choice point at each element and calls after its head;
    // perm/2 calls twice, the second its last call; q/2 cuts after a call;
    // ite/2 branches; thr/1 throws through a frame with a continuation;
    // fr/2 calls a builtin after a binding that may wake a goal.
    private const string Program = """
        sel(X, [X|T], T).
        sel(X, [H|T], [H|R]) :- sel(X, T, R).
        perm([], []).
        perm(L, [X|P]) :- sel(X, L, R), perm(R, P).
        len([], 0).
        len([_|T], N) :- len(T, M), N is M + 1.
        q(L, X) :- sel(X, L, _), X > 1, !.
        q(_, none).
        ite(X, Y) :- ( X > 2 -> Y = big ; X > 1 -> Y = mid ; Y = small ).
        thr(X) :- len([a, b], N), X is N + 1, throw(got(X)).
        fr(X, L) :- X = abc, atom_length(X, L).
        """;

    private static readonly PredicateRef[] EntryPoints =
    {
        new("sel", 3), new("perm", 2), new("len", 2), new("q", 2), new("ite", 2), new("thr", 1), new("fr", 2),
    };

    private static readonly string[] Goals =
    {
        "findall(P, perm([1, 2, 3], P), R).",
        "findall(X-T, sel(X, [a, b, c], T), R).",
        "findall(X, q([1, 2, 3], X), R).",
        "findall(X, q([0, 1], X), R).",
        "findall(Y, (sel(X, [1, 2, 3], _), ite(X, Y)), R).",
        "catch(thr(_), got(R), true).",
        "findall(P-N, (perm([a, b], P), len(P, N)), R).",
        "findall(X-L, (freeze(X, atom(X)), fr(X, L)), R).",
        "findall(X-L, (freeze(X, fail), fr(X, L)), R).",
    };

    private static byte[] Bundle(bool stripWam) =>
        ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(Program, "m", ShmoBuildMode.Release) },
            EntryPoints = EntryPoints,
            StripSource = true,
            IncludeCompiledIl = true,
            StripWam = stripWam,
        }).Bytes!;

    private static string Answer(PrologEngine e, string goal)
    {
        var r = e.Query(goal);
        return r.Success ? $"R = {r["R"]}" : "false";
    }

    private static int Fid(string n, int a) => FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, a);

    private static BundleEntry UserEntry(Bundle bundle) =>
        bundle.Entries.Single(en => en.CompiledIlEntries is { Length: > 0 });

    private static List<string> MethodNames(byte[] image)
    {
        using var pe = new PEReader(new MemoryStream(image));
        var md = pe.GetMetadataReader();
        return md.MethodDefinitions.Select(h => md.GetString(md.GetMethodDefinition(h).Name)).ToList();
    }

    private static (List<IlPersistedEntry> Table, List<string> Methods) Linked(bool stripWam)
    {
        IlPredicateCompiler.CpsMode = true;
        var entry = UserEntry(BundleReader.FromBytes(Bundle(stripWam)));
        var table = IlPersistedEntryCodec.Decode(entry.CompiledIlEntries!).ToList();
        // ANTI-VACUITY: the program's predicates have continuation methods.
        int withCps = table.Count(pe => pe.Cps is not null);
        Assert.True(withCps >= EntryPoints.Length, $"{withCps} predicates with continuation methods");
        return (table, MethodNames(entry.CompiledIl!));
    }

    [Fact]
    public void APredicateWithContinuationMethods_HasNoMethodOfItsOwnInTheBundle()
    {
        var (table, methods) = Linked(stripWam: false);
        foreach (var pe in table.Where(pe => pe.Cps is not null))
        {
            Assert.Equal(pe.Cps!.ColdMethod, pe.MethodName);
            Assert.Contains(pe.MethodName, methods);
        }
        // The methods named P_ are those of the predicates with none.
        Assert.Equal(
            table.Where(pe => pe.Cps is null).Select(pe => pe.MethodName).OrderBy(n => n, StringComparer.Ordinal),
            methods.Where(n => n.StartsWith("P_", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal));
    }

    // With no bytecode the delegate compiles at its first call, on the
    // engine's thread: the predicate's own method, which has no tail call and
    // so starts unoptimized. The cold method would compile optimized there.
    [Fact]
    public void WithNoBytecode_APredicateKeepsItsOwnMethod()
    {
        var (table, methods) = Linked(stripWam: true);
        foreach (var pe in table)
        {
            Assert.StartsWith("P_", pe.MethodName, StringComparison.Ordinal);
            Assert.Contains(pe.MethodName, methods);
            if (pe.Cps is not null) Assert.Contains(pe.Cps.ColdMethod, methods);
        }
    }

    // Bound at load, the delegates are the cold methods, and the continuation
    // methods never arrive here: every call, return, retry and wake enters
    // the cold method from the dispatch loop.
    [Fact]
    public void TheColdMethod_RunsThePredicateFromEveryCursor()
    {
        var plain = new PrologEngine();
        plain.UseCoroutining();
        plain.IlPromotion.Threshold = 0;
        plain.ConsultString(Program);

        IlPredicateCompiler.CpsMode = true;
        byte[] bytes = Bundle(stripWam: false);
        IlPredicateCompiler.CpsMode = false;
        IlPromotionStore.DefaultPersistedThreshold = 0;
        var bundle = BundleReader.FromBytes(bytes);
        var bundled = PrologEngine.FromBundle(bundle);
        bundled.IlPromotion.PersistedThreshold = int.MaxValue;
        bundled.UseCoroutining();

        foreach (string g in Goals)
            Assert.Equal(Answer(plain, g), Answer(bundled, g));

        // ANTI-VACUITY: the predicates ran the bundle's code, the cold method
        // alone, and the answers are not empty.
        var table = IlPersistedEntryCodec.Decode(UserEntry(bundle).CompiledIlEntries!).ToList();
        foreach (var (n, a) in new[] { ("sel", 3), ("perm", 2), ("len", 2), ("q", 2), ("ite", 2), ("thr", 1) })
        {
            int fid = Fid(n, a);
            Assert.True(bundled.IlPromotion.IsPromoted(fid), $"{n}/{a} does not run compiled");
            Assert.Null(bundled.IlPromotion.TryGetCps(fid));
            var pe = table.Single(t => t.Name == n && t.Arity == a);
            Assert.Equal(pe.Cps!.ColdMethod, pe.MethodName);
        }
        Assert.True(bundled.Query(
            "findall(P, perm([1, 2, 3], P), R), R == [[1, 2, 3], [1, 3, 2], [2, 1, 3], [2, 3, 1], [3, 1, 2], [3, 2, 1]].").Success);
        Assert.True(bundled.Query("findall(X-L, (freeze(X, atom(X)), fr(X, L)), R), R == [abc-3].").Success);
        Assert.True(bundled.Query("findall(X-L, (freeze(X, fail), fr(X, L)), R), R == [].").Success);
    }
}
#endif
