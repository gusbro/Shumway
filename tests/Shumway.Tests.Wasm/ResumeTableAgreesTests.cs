using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>The resume table has to say exactly what the host says. Until now a
/// marker was resolved twice over, by two separately-derived encodings of one
/// fact: the host walked a dictionary, the module walked a chain of comparisons
/// baked into its own code. Nothing compared them, so a disagreement had
/// nowhere to show — it would surface as a wrong jump, far from its cause.
///
/// <para>This is the test that makes the single source of truth real rather
/// than intended.</para></summary>
public sealed class ResumeTableAgreesTests(ITestOutputHelper o)
{
    private const string Corpus = """
        :- public app/3.
        :- public rev/2.
        :- public pick/2.
        :- public between3/3.
        app([], L, L).
        app([H|T], L, [H|R]) :- app(T, L, R).
        rev([], []).
        rev([H|T], R) :- rev(T, S), app(S, [H], R).
        pick([H|_], H).
        pick([_|T], X) :- pick(T, X).
        between3(L, H, X) :- L =< H, (X = L ; L1 is L + 1, between3(L1, H, X)).
        """;

    [Fact]
    public void EveryRowMatchesWhatTheHostResolves()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 0;
        engine.ConsultString(Corpus);
        engine.Query("true.");

        var env = new EngineWasmCompileEnv();
        var world = new DesktopWasmWorld();
        var members = new List<WasmGroupMember>();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
        {
            var (aid, _) = FunctorTable.Lookup(pred.FunctorId);
            string n = AtomTable.GetById(aid)?.Name ?? "";
            if (n.EndsWith("app") || n.EndsWith("rev") || n.EndsWith("pick")
                || n.EndsWith("between3"))
                members.Add(new WasmGroupMember(pred, addr, null));
        }
        Assert.True(members.Count >= 4, $"only {members.Count} members");

        var entry = WasmPredicateCompiler.CompileGroup(members, env);
        var addrMap = new Dictionary<int, int>(members.Count);
        foreach (var m in members) addrMap[m.Predicate.FunctorId] = m.Bias;
        world.InstallGroup(entry.Module, entry.EntryCursorByFid,
            entry.CursorByAddress, addrMap, entry.RegisterDemand);

        var index = new WasmBuildAddressIndex(addrMap);
        int checkedRows = 0, freshEntries = 0;

        // Every fresh entry: marker (fid, 0).
        foreach (var (fid, cursor) in entry.EntryCursorByFid)
        {
            int marker = Activation.EncodeResumeMarker(fid, 0);
            Assert.True(world.ResumeTable.TryGet(marker, out int mod, out int cur),
                $"no row for the fresh entry of functor {fid}");
            Assert.Equal(world.ModuleId, mod);
            Assert.True(world.TryResolve(fid, 0, out int hostCur),
                $"the host cannot resolve the fresh entry of functor {fid}");
            Assert.Equal(hostCur, cur);
            freshEntries++;
        }

        // Every other re-entry point, under the functor that owns its address.
        foreach (var (address, cursor) in entry.CursorByAddress)
        {
            int fid = index.OwnerFunctorOf(address);
            if (fid < 0) continue;
            int marker = Activation.EncodeResumeMarker(fid, address);
            Assert.True(world.ResumeTable.TryGet(marker, out int mod, out int cur),
                $"no row for ({fid}, 0x{address:X})");
            Assert.Equal(world.ModuleId, mod);
            Assert.True(world.TryResolve(fid, address, out int hostCur),
                $"the host cannot resolve ({fid}, 0x{address:X})");
            Assert.Equal(hostCur, cur);
            Assert.Equal(cursor, cur);
            checkedRows++;
        }

        o.WriteLine($"{freshEntries} fresh entries + {checkedRows} re-entry points agree");
        // ANTI-VACUITY: a build with no re-entry points would pass the loop
        // above without comparing anything.
        Assert.True(checkedRows > 20, $"only {checkedRows} rows compared");
    }

    /// <summary>The other direction: a marker the build never minted must not
    /// resolve. Otherwise "not mine" would be indistinguishable from a row that
    /// happens to be zero, and the module would jump somewhere.</summary>
    [Fact]
    public void AForeignMarkerDoesNotResolve()
    {
        var world = new DesktopWasmWorld();
        int stranger = Activation.EncodeResumeMarker(999_001, 0x7777);
        Assert.False(world.ResumeTable.TryGet(stranger, out _, out _));
    }
}
