using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>Which builtins actually cost a chain exit — the tally that
/// decides what earns open-coding in the wasm compiler. Runs the Van Roy
/// corpus through the engine wasm tier on the desktop world and prints
/// requests per builtin. Diagnostic: it asserts nothing beyond the programs
/// succeeding; read its output.</summary>
public class WasmBuiltinTallyDiagnostic
{
    private readonly ITestOutputHelper _out;
    public WasmBuiltinTallyDiagnostic(ITestOutputHelper output) => _out = output;

    private static string RepoRoot()
    {
        string current = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && current is not null; i++)
        {
            if (File.Exists(Path.Combine(current, "Shumway.slnx"))) return current;
            current = Path.GetDirectoryName(current)!;
        }
        throw new InvalidOperationException("no repo root");
    }

    private static PrologEngine WasmEngine(string source,
        List<(string Pred, string Why)>? rejections = null)
    {
        var engine = new PrologEngine();
        engine.ConsultString(source);
        var store = engine.IlPromotion;
        store.Threshold = 0;
        var world = new DesktopWasmWorld();
        var members = new List<WasmGroupMember>();
        var env = new EngineWasmCompileEnv();
        store.Wasm = new WasmPromotionStore(store)
        {
            Threshold = 1,
            Promoter = (pred, linkedBase) =>
            {
                var candidate = new WasmGroupMember(pred, linkedBase,
                    store.FloatPoolProvider?.Invoke(pred.FunctorId));
                members.Add(candidate);
                try
                {
                    var entry = WasmPredicateCompiler.CompileGroup(members, env);
                    var entryAddr = new Dictionary<int, int>(members.Count);
                    foreach (var m in members)
                        entryAddr[m.Predicate.FunctorId] = m.Bias;
                    world.InstallGroup(entry.Module, entry.EntryCursorByFid,
                        entry.CursorByAddress, entryAddr, entry.RegisterDemand);
                    return new WasmTierDelegate(pred.FunctorId, world).Invoke;
                }
                catch (WasmCompileException ex)
                {
                    rejections?.Add((Shumway.Core.FunctorTable.Lookup(pred.FunctorId).ToString(),
                        ex.Message));
                    members.Remove(candidate);
                    if (members.Count > 0)
                    {
                        var entry = WasmPredicateCompiler.CompileGroup(members, env);
                        var entryAddr = new Dictionary<int, int>(members.Count);
                        foreach (var m in members)
                            entryAddr[m.Predicate.FunctorId] = m.Bias;
                        world.InstallGroup(entry.Module, entry.EntryCursorByFid,
                            entry.CursorByAddress, entryAddr, entry.RegisterDemand);
                    }
                    return null;
                }
            },
        };
        return engine;
    }

    [Fact]
    public void TallyOverTheVanRoyCorpus()
    {
        string dir = Path.Combine(RepoRoot(), "benchmarks", "vanroy");
        // The whole corpus; a program whose predicates the compiler rejects
        // simply runs Tier-0 and contributes nothing, which is also data.
        string[] programs =
        {
            "nreverse", "qsort", "queens", "tak", "serialize",
            "flatten", "sendmore", "zebra", "crypt", "boyer",
        };
        var grand = new Dictionary<string, long>();
        foreach (string name in programs)
        {
            string source = File.ReadAllText(Path.Combine(dir, $"{name}.pl"));
            var rejections = new List<(string Pred, string Why)>();
            var e = WasmEngine(source, rejections);
            WasmTierDelegate.ResetDiag();
            // Warm once (promotion), then the measured pass.
            Assert.True(e.Query("bench.").Success, name);
            Assert.True(e.Query("bench.").Success, name);
            long deopts = WasmTierDelegate.DiagDeopts;
            long entries = WasmTierDelegate.DiagEntries;
            foreach (var (pred, why) in rejections.DistinctBy(r => r.Pred))
                _out.WriteLine($"   [{name}] REJECT {pred,-18} {why}");
            string PredName(int f)
            {
                var (aid, ar) = Shumway.Core.FunctorTable.Lookup(f);
                return $"{Shumway.Core.AtomTable.GetById(aid)?.Name}/{ar}";
            }
            _out.WriteLine($"   [{name}] promoted: "
                + string.Join(" ", e.IlPromotion.PromotedFunctorIds().Select(PredName)));
            if (e.IlPromotion.Wasm is { } ws)
                _out.WriteLine($"   [{name}] unpromotable: "
                    + string.Join(" ", ws.UnpromotableFunctorIds().Select(PredName)));
            if (deopts > 0)
            {
                _out.WriteLine($"   [{name}] deopt pcs: " + string.Join(", ",
                    WasmTierDelegate.DiagDeoptPcs.Where(p => p != -1)));
                if (WasmTierDelegate.DiagFirstDeoptSlots is { } s)
                    _out.WriteLine($"   [{name}] first-deopt slots: flags={s[0]} "
                        + $"TR={s[1]}/{s[2]} H={s[3]} watermark={s[4]} ST={s[5]}/{s[6]}");
            }
            var rows = WasmTierDelegate.DiagBuiltinTally
                .OrderByDescending(kv => kv.Value)
                .Select(kv =>
                {
                    var b = Shumway.Builtins.BuiltinsRegistry.GetById(kv.Key);
                    return ($"{b.Name}/{b.Arity}", kv.Value);
                })
                .ToList();
            _out.WriteLine($"== {name}: entries={entries} deopts={deopts} "
                + $"builtinRequests={rows.Sum(r => r.Item2)}");
            foreach (var (pred, n) in rows.Take(12))
            {
                _out.WriteLine($"   {pred,-22} {n}");
                grand[pred] = grand.GetValueOrDefault(pred) + n;
            }
        }
        _out.WriteLine("== GRAND TOTAL");
        foreach (var (pred, n) in grand.OrderByDescending(kv => kv.Value).Take(20))
            _out.WriteLine($"   {pred,-22} {n}");
    }
}
