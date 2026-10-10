using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-049 in ADR-031's fail-direct chains: a callee inlined into a
/// CP-free guard wakes where the interpreter does. Its self-tail call is a
/// goal boundary (the loop walked an attributed list past the hook that
/// binds it, building cells without end), and a pending wake at its neck cut
/// runs in the interpreter (run in compiled code, the woken goals' cuts
/// compacted the trail past the marks the chain untrails to).</summary>
public sealed class GuardChainWakeTests : IDisposable
{
    // Both guards inline their callee as a fail-direct chain whose last
    // clause is a self-tail loop. walk(go) fails its first clause on the
    // argument, so the loop meets no other wake point.
    private const string Grammar = @"
walk(stop) --> [].
walk(go) --> [_], walk(go).
g --> walk(go), !.
g --> [z].
line --> ['\n'], !.
line --> [_], line.
lines(N0, N) --> line, !, { N1 is N0 + 1 }, lines(N1, N).
lines(N, N) --> [].
";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shumway-guardwake-" + Guid.NewGuid().ToString("N"));

    public GuardChainWakeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // Compiled: every predicate promoted at its first call, in the engine's
    // thread, so the first query runs the chain.
    private static PrologEngine Engine(bool compiled)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = compiled ? 1 : 0;
        e.IlPromotion.BackgroundCompilation = false;
        e.Query("use_module(library(coroutining)).");
        e.ConsultString(Grammar);
        return e;
    }

    // The hook binds the list the loop walks: [a] ends the walk, so the guard
    // and the query fail. A loop that never sees the wake answers time_out
    // instead of running the machine out of memory.
    [Theory]
    [InlineData("freeze(L, L = [a])")]
    [InlineData("'$lazy_freeze'(L, L = [a])")]
    public void TheSelfTailCallWakes(string hook)
    {
        foreach (bool compiled in new[] { false, true })
        {
            var e = Engine(compiled);
            for (int i = 0; i < 3; i++)
            {
                var s = e.Query($"{hook}, time_out((phrase(g, L) -> A = yes ; A = no), 3000, R).");
                Assert.True(s.Success, $"compiled: {compiled}, run {i}");
                Assert.Equal("success", s["R"]!.ToString());
                Assert.Equal("no", s["A"]!.ToString());
            }
        }
    }

    // The window phrase_from_file reads is unified in by a woken goal at
    // line//0's neck cut, the first character's alternatives untried. The
    // prelude runs from its bytecode, as before it has earned its code: the
    // woken goal's cut, in the interpreter, compacts the trail.
    [Fact]
    public void ALazilyReadFileParsesTheSameCompiled()
    {
        string data = Path.Combine(_dir, "data.txt").Replace('\\', '/');
        File.WriteAllText(data, string.Concat(Enumerable.Repeat(new string('x', 59) + "\n", 50)));
        foreach (bool compiled in new[] { false, true })
        {
            var e = Engine(compiled);
            e.IlPromotion.PersistedThreshold = int.MaxValue;
            var s = e.Query(
                $"time_out(phrase_from_file(lines(0, N), '{data}'), 3000, R).");
            Assert.True(s.Success, $"compiled: {compiled}");
            Assert.Equal("success", s["R"]!.ToString());
            Assert.Equal("50", s["N"]!.ToString());
        }
    }
}
