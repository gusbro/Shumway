using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>The one-argument type tests, answered inside the module.
///
/// <para>They are a tag comparison on a dereferenced argument, and stepping
/// out to the host to run one cost a chain exit each time. Measured as a
/// share of all builtin exits: 60% of clpr's (var/1 32%, number/1 22%) and
/// 39% of clpfd's (integer/1 36%) -- the cheapest entries in that ranking
/// and the largest share of it.</para>
///
/// <para>The trap these tests exist for is the tag SETS. A variable is Ref
/// or ATTVAR: an attributed variable has attributes and no value, so var/1
/// accepts it and nonvar/1 rejects it -- and the libraries that lean on
/// var/1 are precisely the ones that create attributed variables, so
/// getting this wrong would answer wrongly exactly where it matters.
/// integer/1 takes BigInt beside Int, number/1 takes Rational too, and
/// compound/1 takes a packed string (ADR-047: a PSTR is a list).</para></summary>
/// <summary>The corpus, built ONCE for the whole class.
///
/// <para>Every case here asks a different goal of the SAME program, and
/// building it is what the time went to: two engines, each loading clpfd,
/// and the tiered one compiling every reached predicate to its own wasm
/// module and then to IL. Measured, 38 cases cost 200 s that way and the
/// work they share is all of it.</para>
///
/// <para>Safe to share because these cases only ASK: no case asserts,
/// retracts or consults, and each Query gets its own activation. A case
/// that mutated the database would make its neighbours order-dependent,
/// so one that needs to must build its own engine -- as the counter below
/// does, for the counters rather than the database.</para></summary>
public sealed class WasmTypeTestCorpus
{
    public PrologEngine Plain { get; } = WasmTypeTestTests.BuildPlain();
    public PrologEngine Tiered { get; } = WasmTypeTestTests.BuildTiered().Engine;
}

public sealed class WasmTypeTestTests(ITestOutputHelper o, WasmTypeTestCorpus shared)
    : IClassFixture<WasmTypeTestCorpus>
{
    private const string Corpus = """
        t(G, yes) :- call(G), !.
        t(_, no).
        isvar(X, R) :- t(var(X), R).
        isnonvar(X, R) :- t(nonvar(X), R).
        isint(X, R) :- t(integer(X), R).
        isfloat(X, R) :- t(float(X), R).
        isnum(X, R) :- t(number(X), R).
        isatom(X, R) :- t(atom(X), R).
        isatomic(X, R) :- t(atomic(X), R).
        iscomp(X, R) :- t(compound(X), R).
        tailvar(X) :- var(X).
        tailint(X) :- integer(X).
        countvars([], N, N).
        countvars([X|Xs], A, N) :- ( var(X) -> A1 is A + 1 ; A1 = A ),
                                   countvars(Xs, A1, N).
        """;

    internal static PrologEngine BuildPlain()
    {
        var e = new PrologEngine();
        e.ConsultString(":- use_module(library(clpfd)).");
        e.ConsultString(Corpus);
        return e;
    }

    internal static (PrologEngine Engine, WasmPromotionStore Wasm) BuildTiered()
    {
        var engine = new PrologEngine();
        engine.ConsultString(":- use_module(library(clpfd)).");
        engine.ConsultString(Corpus);
        engine.Query("true.");
        var store = engine.IlPromotion;
        store.Threshold = 0;
        var world = new DesktopWasmWorld();
        var env = new EngineWasmCompileEnv();
        var wasm = new WasmPromotionStore(store)
        {
            Threshold = 1,
            BatchPromoter = candidates =>
            {
                var good = new List<WasmGroupMember>();
                foreach (var (pred, addr) in candidates)
                {
                    var m = new WasmGroupMember(pred, addr,
                        store.FloatPoolProvider?.Invoke(pred.FunctorId));
                    try { WasmPredicateCompiler.CompileGroup(new[] { m }, env); good.Add(m); }
                    catch (WasmCompileException) { }
                }
                if (good.Count == 0) return 0;
                TieredEngine.Install(world, good, env, store);
                foreach (var m in good)
                {
                    store.RegisterBoundDelegate(m.Predicate.FunctorId,
                        new WasmTierDelegate(m.Predicate.FunctorId, world).Invoke);
                    store.Wasm?.NoteInstalled(m.Predicate.FunctorId, m.Bias, m.Predicate);
                }
                return good.Count;
            },
        };
        wasm.LiveRefreshed = world.RefreshLiveAddresses;
        wasm.StaleEvicted = world.Evict;
        store.Wasm = wasm;
        Assert.True(wasm.PromoteAllStatics(engine) > 0);
        return (engine, wasm);
    }

    public static TheoryData<string> Goals() => new()
    {
        // plain variables and bound terms
        "isvar(_, R), R == yes.",
        "isvar(foo, R), R == no.",
        "isnonvar(_, R), R == no.",
        "isnonvar(foo, R), R == yes.",
        // AN ATTRIBUTED VARIABLE IS A VARIABLE
        "X in 1..3, isvar(X, R), R == yes.",
        "X in 1..3, isnonvar(X, R), R == no.",
        "X in 1..3, isatomic(X, R), R == no.",
        "X in 1..3, iscomp(X, R), R == no.",
        // a bound attributed variable is its value
        "X in 1..3, X = 2, isvar(X, R), R == no.",
        "X in 1..3, X = 2, isint(X, R), R == yes.",
        // integers, including a bignum
        "isint(42, R), R == yes.",
        "isint(123456789012345678901234567890, R), R == yes.",
        "isint(1.5, R), R == no.",
        "isint(foo, R), R == no.",
        // floats and numbers
        "isfloat(1.5, R), R == yes.",
        "isfloat(42, R), R == no.",
        "isnum(42, R), R == yes.",
        "isnum(1.5, R), R == yes.",
        "isnum(123456789012345678901234567890, R), R == yes.",
        "isnum(foo, R), R == no.",
        "isnum(f(1), R), R == no.",
        // atoms and atomic
        "isatom(foo, R), R == yes.",
        "isatom([], R), R == yes.",
        "isatom(42, R), R == no.",
        "isatom(f(x), R), R == no.",
        "isatomic(42, R), R == yes.",
        "isatomic(1.5, R), R == yes.",
        "isatomic(foo, R), R == yes.",
        "isatomic(f(x), R), R == no.",
        "isatomic(_, R), R == no.",
        // compound: structures and lists, and a packed string is a list
        "iscomp(f(x), R), R == yes.",
        "iscomp([a], R), R == yes.",
        "iscomp([], R), R == no.",
        "iscomp(foo, R), R == no.",
        "iscomp(42, R), R == no.",
        // tail position
        "tailvar(_).",
        "tailint(42).",
        // and a loop that calls one per element
        "countvars([a, _, b, _, _], 0, N), N == 3.",
    };

    [Theory]
    [MemberData(nameof(Goals))]
    public void TheTierAnswersWhatTheInterpreterAnswers(string goal)
    {
        bool plain = shared.Plain.Query(goal).Success;
        bool tiered = shared.Tiered.Query(goal).Success;
        Assert.True(plain == tiered,
            $"tier {tiered} != interpreter {plain} for: {goal}");
        Assert.True(plain, $"the goal itself is wrong: {goal}");
    }

    /// <summary>Counted: a loop of type tests no longer leaves the module.</summary>
    [DiagFact]
    public void ALoopOfTypeTestsMakesNoBuiltinExit()
    {
        var (e, _) = BuildTiered();
        e.Query("numlist(1, 50, L), countvars(L, 0, _).");     // warm
        WasmTierDelegate.ResetDiag();
        Assert.True(e.Query("numlist(1, 400, L), countvars(L, 0, N), N == 0.").Success);
        o.WriteLine($"countvars over 400: builtinExits={WasmTierDelegate.DiagBuiltins} "
            + $"entries={WasmTierDelegate.DiagEntries} deopts={WasmTierDelegate.DiagDeopts}");
        // numlist and the comparison are the host's; the 400 var/1 are not.
        Assert.True(WasmTierDelegate.DiagBuiltins < 50,
            $"{WasmTierDelegate.DiagBuiltins} builtin exits: the type tests are "
            + "still leaving the module");
    }
}
