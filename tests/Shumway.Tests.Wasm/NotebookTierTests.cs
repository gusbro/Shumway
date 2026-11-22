using System.Text.Json;
using System.Text.Json.Nodes;
using Shumway.Compiler.Wam;
using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Shumway.Jupyter;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>A notebook's server is one query for the session's life, and
/// what a cell consults is linked into that query. The tier's batch runs
/// between queries and compiles against the static link, so a session under
/// it never compiled the program of its cells: a predicate that crossed the
/// threshold waited on Tier-0 for a batch that had no moment to run. A
/// session compiles each predicate where it runs, when it is called enough.</summary>
public sealed class NotebookTierTests : IDisposable
{
    private readonly NotebookHarness _notebooks = new();

    public void Dispose() => _notebooks.Dispose();

    // The line ends a kernel sends, whatever those of this file.
    private static readonly string Program = """
        %%user
        len([], 0).
        len([_|T], N) :- len(T, M), N is M + 1.
        dbl([], []).
        dbl([X|Xs], [Y|Ys]) :- Y is X * 2, dbl(Xs, Ys).
        work(N, S) :- numlist(1, N, L), dbl(L, D), len(D, S).
        pick(X) :- member(X, [1, 2, 3]).
        opt(a). opt(b). opt(c).
        """.Replace("\r\n", "\n");

    private static string Call(int id, string code)
        => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "call", @params = new { code } });

    private static JsonNode First(NotebookServer server, string code)
    {
        string? line = NotebookHarness.Ask(server, Call(1, code));
        Assert.NotNull(line);
        return JsonNode.Parse(line)!["result"]!["1"]!;
    }

    /// <summary>A tier on the desktop's wasm world. It names every predicate
    /// it was asked to compile, one at a time or in a batch.</summary>
    private sealed class Tier(bool batch)
    {
        public WasmPromotionStore Wasm = null!;
        private readonly List<string> _offered = new();

        public List<string> Offered()
        {
            lock (_offered) return new List<string>(_offered);
        }

        public void Attach(PrologEngine engine)
        {
            var store = engine.IlPromotion;
            store.Threshold = 0;
            var world = new DesktopWasmWorld();
            var env = new EngineWasmCompileEnv();
            bool Install(CompiledPredicate pred, int addr)
            {
                var (name, arity) = Name(pred.FunctorId);
                lock (_offered) _offered.Add($"{name}/{arity}");
                var member = new WasmGroupMember(pred, addr,
                    store.FloatPoolProvider?.Invoke(pred.FunctorId),
                    store.ImagePoolProvider?.Invoke(pred.FunctorId));
                try
                {
                    TieredEngine.InstallOne(world, member, env, store);
                    return true;
                }
                catch (WasmCompileException e)
                {
                    store.Wasm!.MarkUnpromotable(pred.FunctorId, e.Message);
                    return false;
                }
            }
            Wasm = new WasmPromotionStore(store)
            {
                Threshold = 2,
                CompileAllOnConsult = batch,
                Promoter = (pred, addr) => Install(pred, addr)
                    ? new WasmTierDelegate(pred.FunctorId, world).Invoke : null,
                BatchPromoter = !batch ? null : candidates =>
                {
                    int installed = 0;
                    foreach (var (pred, addr) in candidates)
                    {
                        if (!Install(pred, addr)) continue;
                        store.RegisterBoundDelegate(pred.FunctorId,
                            new WasmTierDelegate(pred.FunctorId, world).Invoke);
                        store.Wasm!.NoteInstalled(pred.FunctorId, addr, pred);
                        installed++;
                    }
                    return installed;
                },
            };
            Wasm.StaleEvicted = world.Evict;
            Wasm.ShadowRetired = world.Retire;
            Wasm.LiveRefreshed = world.RefreshLiveAddresses;
            store.Wasm = Wasm;
        }
    }

    private static (string Name, int Arity) Name(int functorId)
    {
        var (atomId, arity) = FunctorTable.Lookup(functorId);
        return (AtomTable.GetById(atomId)?.Name ?? "?", arity);
    }

    // A predicate of a %%user cell is local to user: user$name.
    private static bool OnTheTier(PrologEngine engine, string name, int arity)
    {
        int functor = FunctorTable.Intern(AtomTable.Intern("user$" + name).Id, arity);
        return engine.IlPromotion.PromotedFunctorIds().Contains(functor);
    }

    [Fact]
    public void TheProgramOfACell_ReachesTheTierAsItRuns()
    {
        var tier = new Tier(batch: false);
        var (server, session) = _notebooks.Open("a", prepare: tier.Attach);

        First(server, Program);
        JsonNode first = First(server, "work(20, S).");
        JsonNode second = First(server, "work(30, S).");

        Assert.Equal("20", (string)first["bindings"]!["S"]!);
        Assert.Equal("30", (string)second["bindings"]!["S"]!);
        Assert.True(OnTheTier(session.Engine, "len", 2), "len/2 is not on the tier");
        Assert.True(OnTheTier(session.Engine, "dbl", 2), "dbl/2 is not on the tier");
        Assert.True(OnTheTier(session.Engine, "work", 2), "work/2 is not on the tier");
    }

    /// <summary>Every session would compile the server again as its cells
    /// warmed it up.</summary>
    [Fact]
    public void TheServersOwnCode_StaysOffTheTier()
    {
        var tier = new Tier(batch: false);
        NotebookServer server = _notebooks.Open("a", prepare: tier.Attach).Server;

        First(server, Program);
        for (int i = 0; i < 4; i++) First(server, "work(20, S).");

        List<string> offered = tier.Offered();
        Assert.Contains("user$len/2", offered);     // the tier was asked
        Assert.All(offered, name => Assert.StartsWith("user$", name));
    }

    /// <summary>A query that left choice points stays alive between cells:
    /// its next solution comes out of the compiled predicate.</summary>
    [Fact]
    public void AQueryAliveInACompiledPredicate_GivesItsNextSolution()
    {
        var tier = new Tier(batch: false);
        var (server, session) = _notebooks.Open("a", prepare: tier.Attach);

        First(server, Program);
        First(server, "pick(X), pick(Y), X + Y > 5.");
        JsonNode first = First(server, "pick(X).");
        JsonNode second = First(server, "retry.");

        Assert.True(OnTheTier(session.Engine, "pick", 1), "pick/1 is not on the tier");
        Assert.Equal("1", (string)first["bindings"]!["X"]!);
        Assert.Equal("2", (string)second["bindings"]!["X"]!);
    }

    [Fact]
    public void APredicateDefinedAgain_RunsItsNewClausesAndIsCompiledAgain()
    {
        var tier = new Tier(batch: false);
        var (server, session) = _notebooks.Open("a", prepare: tier.Attach);

        First(server, Program);
        First(server, "pick(X), pick(Y), X + Y > 5.");
        JsonNode before = First(server, "pick(X).");
        First(server, "%%user\npick(X) :- member(X, [a, b, c]).");
        JsonNode after = First(server, "pick(X).");
        First(server, "pick(X), pick(Y), X == c, Y == c.");
        JsonNode last = First(server, "pick(X).");

        Assert.Equal("1", (string)before["bindings"]!["X"]!);
        Assert.Equal("a", (string)after["bindings"]!["X"]!);
        Assert.Equal("a", (string)last["bindings"]!["X"]!);
        Assert.True(OnTheTier(session.Engine, "pick", 1), "pick/1 is not on the tier");
        Assert.Equal(2, tier.Offered().Count(name => name == "user$pick/1"));
    }

    /// <summary>A compiled caller reaches the new clauses of a predicate a
    /// later cell defines again, whether the caller's own text came with
    /// them or not.</summary>
    [Theory]
    [InlineData("%%user\nboth(X) :- pick(X).\npick(X) :- member(X, [7, 8, 9]).", "[7,8,9]")]
    [InlineData("%%user+\npick(4).", "[1,2,3,4]")]
    public void ACompiledCaller_ReachesTheNewClausesOfItsCallee(string redefinition, string expected)
    {
        var tier = new Tier(batch: false);
        var (server, session) = _notebooks.Open("a", prepare: tier.Attach);

        First(server, "%%user\nboth(X) :- pick(X).\npick(X) :- member(X, [1, 2, 3]).");
        for (int i = 0; i < 4; i++) First(server, "both(X).");
        Assert.True(OnTheTier(session.Engine, "both", 1), "both/1 is not on the tier");
        Assert.True(OnTheTier(session.Engine, "pick", 1), "pick/1 is not on the tier");
        NotebookHarness.Ask(server, Call(1, redefinition));
        string? line = NotebookHarness.Ask(server, Call(1, "findall(X, both(X), L)."));

        Assert.True(line is not null, "the server stopped: " + server.Failure);
        JsonNode? bindings = JsonNode.Parse(line)!["result"]?["1"]?["bindings"];
        Assert.True(bindings is not null, line);
        Assert.Equal(expected, ((string)bindings!["L"]!).Replace(" ", ""));
    }

    /// <summary>jit_compile(off) left what was compiled for the setup of the
    /// next query to send back to its bytecode, and a session has no next
    /// query: its program stayed compiled. It leaves the tier at the goal,
    /// and a query alive inside it gives its next solution.</summary>
    [Fact]
    public void JitCompileOff_SendsTheProgramBackToItsBytecode_AndACountCompilesItAgain()
    {
        var tier = new Tier(batch: false);
        var (server, session) = _notebooks.Open("a", prepare: tier.Attach);
        bool Compiled(string name, int arity) => OnTheTier(session.Engine, name, arity);

        First(server, Program);
        for (int i = 0; i < 3; i++) First(server, "work(20, S).");
        First(server, "opt(X), opt(Y), X == c, Y == c.");
        Assert.True(Compiled("opt", 1) && Compiled("work", 2) && Compiled("len", 2), "the program is not on the tier");

        // The choice point of opt/1 is one of its compiled code.
        JsonNode first = First(server, "opt(X), jit_compile(off).");
        bool leftAtTheGoal = !Compiled("opt", 1) && !Compiled("work", 2) && !Compiled("len", 2);
        JsonNode second = First(server, "retry.");
        JsonNode interpreted = First(server, "work(30, S).");
        for (int i = 0; i < 3; i++) First(server, "work(20, S).");
        bool stayedOff = !Compiled("work", 2) && !Compiled("len", 2);
        First(server, "jit_compile(2).");
        for (int i = 0; i < 3; i++) First(server, "work(20, S).");

        Assert.True(leftAtTheGoal, "the program stayed on the tier after jit_compile(off)");
        Assert.Equal("a", (string)first["bindings"]!["X"]!);
        Assert.True(second["bindings"] is not null, second.ToJsonString());
        Assert.Equal("b", (string)second["bindings"]!["X"]!);
        Assert.Equal("30", (string)interpreted["bindings"]!["S"]!);
        Assert.True(stayedOff, "the program went back on the tier while it was off");
        Assert.True(Compiled("work", 2) && Compiled("len", 2), "the program is not on the tier again");
    }

    /// <summary>Counter-proof, and what a notebook did: under the batch the
    /// same cells leave the program on Tier-0, however often it runs.</summary>
    [Fact]
    public void UnderTheBatch_TheProgramOfACellStaysOnTier0()
    {
        var tier = new Tier(batch: true);
        var (server, session) = _notebooks.Open("a", prepare: tier.Attach);

        First(server, Program);
        for (int i = 0; i < 4; i++) First(server, "work(20, S).");

        Assert.False(OnTheTier(session.Engine, "len", 2));
        Assert.DoesNotContain("user$len/2", tier.Offered());
    }
}
