using Shumway.Compiler.Wasm;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>Float arithmetic inside the module, against the interpreter.
///
/// <para>The a_eval RPN stack lived in i64 locals, so a float literal was an
/// unconditional deopt and a float CELL operand deopted on its tag. Measured
/// on a 2,000-iteration loop over `N * 1.5`: 2,000 deopts, one per
/// iteration, and 8x the time of the same loop in integers -- the tier made
/// float code SLOWER than not promoting at all, and clpr (whose store is
/// floats) ran 12x slower than Tier-0.</para>
///
/// <para>Each slot now carries a KIND beside its value: the 60-bit int lane
/// as before, or the IEEE bits of a double. Delivering a float result still
/// escalates -- that needs its two heap cells -- so what stays in wasm here
/// is everything that CONSUMES floats: literals, operand reads, arithmetic
/// and comparisons.</para>
///
/// <para>These are differential: the tier must answer exactly what the
/// interpreter answers. Arithmetic is where "fast and almost right" is
/// worst, so the assertions are equality of ANSWERS, not of speed.</para></summary>
public sealed class WasmFloatArithmeticTests(ITestOutputHelper o)
{
    private const string Corpus = """
        fcmp(A, B, lt) :- A < B, !.
        fcmp(A, B, gt) :- A > B, !.
        fcmp(_, _, eq).
        cmps(X, Y, R) :- ( X * 2.0 >= Y -> R = ge ; R = lt ).
        chain(0, A, A) :- !.
        chain(N, A, R) :- ( A * 1.5 > 10.0 -> A1 is A - 1 ; A1 is A + 1 ),
                          M is N - 1, chain(M, A1, R).
        negcmp(X, R) :- ( -X < 0.0 -> R = neg ; R = nonneg ).
        abscmp(X, R) :- ( abs(X) > 2.5 -> R = big ; R = small ).
        mixed(I, F, R) :- ( I + F > 3.0 -> R = over ; R = under ).
        divcmp(A, B, R) :- ( A / B > 1.0 -> R = over ; R = under ).
        zerodiv(A, B, R) :- catch(( A / B > 1.0 -> R = over ; R = under ),
                                  error(E, _), R = err(E)).
        add(A, B, R) :- R is A + B.
        sub(A, B, R) :- R is A - B.
        mul(A, B, R) :- R is A * B.
        divi(A, B, R) :- R is A / B.
        neg(A, R) :- R is -A.
        absv(A, R) :- R is abs(A).
        sgn(A, R) :- R is sign(A).
        rpn(A, B, C, R) :- R is (A + B) * C - A / B.
        fsum(0, A, A) :- !.
        fsum(N, A, S) :- A1 is A + N * 1.5, M is N - 1, fsum(M, A1, S).
        intdiv(A, B, R) :- R is A // B.
        modv(A, B, R) :- R is A mod B.
        errdiv(A, B, R) :- catch(X is A / B, error(E, _), true),
                           ( var(E) -> R = X ; R = err(E) ).
        """;

    private static PrologEngine Plain()
    {
        var e = new PrologEngine();
        e.ConsultString(Corpus);
        return e;
    }

    private static (PrologEngine Engine, WasmPromotionStore Wasm) Tiered()
    {
        var engine = new PrologEngine();
        engine.ConsultString(Corpus);
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
        engine.Query("true.");
        Assert.True(wasm.PromoteAllStatics(engine) > 0);
        return (engine, wasm);
    }

    public static TheoryData<string> Goals() => new()
    {
        // comparison against a literal, both ways
        "fcmp(1.5, 2.5, R), R == lt.",
        "fcmp(2.5, 1.5, R), R == gt.",
        "fcmp(2.5, 2.5, R), R == eq.",
        // int against float: the standard promotes
        "fcmp(2, 2.5, R), R == lt.",
        "fcmp(3, 2.5, R), R == gt.",
        "fcmp(2, 2.0, R), R == eq.",
        "mixed(1, 2.5, R), R == over.",
        "mixed(1, 1.5, R), R == under.",
        // arithmetic feeding a comparison
        "cmps(2.0, 3.0, R), R == ge.",
        "cmps(1.0, 3.0, R), R == lt.",
        "divcmp(3.0, 2.0, R), R == over.",
        "divcmp(1.0, 2.0, R), R == under.",
        // unary
        "negcmp(1.5, R), R == neg.",
        "negcmp(-1.5, R), R == nonneg.",
        "abscmp(-3.5, R), R == big.",
        "abscmp(-1.5, R), R == small.",
        // the single zero: -0.0 and 0.0 are one value
        "fcmp(-0.0, 0.0, R), R == eq.",
        "negcmp(0.0, R), R == nonneg.",
        // a loop that stays in the module
        "chain(50, 1, R), R == 7.",
        // division by zero is the host's error, not an infinity
        "zerodiv(1.0, 0.0, R), R = err(evaluation_error(zero_divisor)).",
        // integers are untouched
        "fcmp(1, 2, R), R == lt.",
        "cmps(2, 3, R), R == ge.",

        // ---- results, which is what the float lane had to learn to build ----
        "add(1.5, 2.25, R), R == 3.75.",
        "sub(1.5, 2.25, R), R == -0.75.",
        "mul(1.5, 2.0, R), R == 3.0.",
        "divi(3.0, 2.0, R), R == 1.5.",
        // int and float mixed: the result is a float
        "add(1, 2.5, R), R == 3.5.",
        "mul(2, 1.5, R), R == 3.0.",
        // / on two integers that do not divide gives a float
        "divi(3, 2, R), R == 1.5.",
        // `/` on two integers yields a float here even when it divides.
        "divi(4, 2, R), R == 2.0.",
        "neg(1.5, R), R == -1.5.",
        "absv(-1.5, R), R == 1.5.",
        "sgn(-2.5, R), R == -1.0.",
        "sgn(2.5, R), R == 1.0.",
        "sgn(0.0, R), R == 0.0.",
        // the single zero, now on a COMPUTED value
        "mul(0.0, -1.0, R), R == 0.0.",
        "neg(0.0, R), R == 0.0.",
        "mul(0.0, -1.0, R), R =:= 0.0.",
        // a nested expression through the RPN path
        "rpn(2.0, 4.0, 3.0, R), R == 17.5.",
        // an accumulator that stays a float across 200 iterations
        "fsum(200, 0.0, S), S == 30150.0.",
        // the delivered float is a real term: unifiable, comparable, printable
        "add(1.5, 2.25, 3.75).",
        "add(1.5, 2.25, R), float(R).",
        "add(1.5, 2.25, R), R > 3.7, R < 3.8.",
        "add(1.5, 2.25, R), Y is R + 0.25, Y == 4.0.",
        "add(1.5, 2.25, R), copy_term(R, C), C == 3.75.",
        // integer-only operators keep their integer meaning
        "intdiv(9, 2, R), R == 4.",
        "modv(7, 3, R), R == 1.",
        // and their type errors on a float
        "catch(intdiv(9.0, 2, _), error(type_error(integer, 9.0), _), true).",
        // division by zero: the error, not an infinity
        "errdiv(1.0, 0.0, R), R = err(evaluation_error(zero_divisor)).",
        "errdiv(1, 0, R), R = err(evaluation_error(zero_divisor)).",
    };

    [Theory]
    [MemberData(nameof(Goals))]
    public void TheTierAnswersWhatTheInterpreterAnswers(string goal)
    {
        bool plain = Plain().Query(goal).Success;
        var (e, _) = Tiered();
        bool tiered = e.Query(goal).Success;
        Assert.True(plain == tiered,
            $"tier {tiered} != interpreter {plain} for: {goal}");
        Assert.True(plain, $"the goal itself is wrong: {goal}");
    }

    /// <summary>The point of the change: a float comparison no longer leaves
    /// the module. Counted, not timed.</summary>
    [DiagFact]
    public void AFloatComparisonLoopStaysInTheModule()
    {
        var (e, _) = Tiered();
        WasmTierDelegate.ResetDiag();
        Assert.True(e.Query("chain(2000, 1, R), R == 7.").Success);
        long entries = WasmTierDelegate.DiagEntries;
        long deopts = WasmTierDelegate.DiagDeopts;
        o.WriteLine($"chain(2000): entries={entries} deopts={deopts}");
        // Before: one deopt per iteration. The `is` in the loop still
        // delivers integers, so nothing here needs a float result cell.
        Assert.True(deopts == 0,
            $"{deopts} deopts in a float comparison loop: the float lane is "
            + "not carrying the comparisons");
        Assert.True(entries <= 2,
            $"{entries} chain entries for one goal: the chain is breaking");
    }

    /// <summary>And a loop that PRODUCES a float every iteration: this is the
    /// one clpr is made of, and it used to deopt on every `is`.</summary>
    [DiagFact]
    public void AFloatProducingLoopStaysInTheModule()
    {
        var (e, _) = Tiered();
        WasmTierDelegate.ResetDiag();
        Assert.True(e.Query("fsum(2000, 0.0, S), S > 0.0.").Success);
        long entries = WasmTierDelegate.DiagEntries;
        long deopts = WasmTierDelegate.DiagDeopts;
        o.WriteLine($"fsum(2000): entries={entries} deopts={deopts}");
        Assert.True(deopts == 0,
            $"{deopts} deopts building 2000 float results");
        Assert.True(entries <= 2,
            $"{entries} chain entries for one goal: the chain is breaking");
    }
}
