using System.Collections.Concurrent;
using System.Text;
using Shumway.Compiler.Il;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Compiler.Il;

/// <summary>One compiler compiles regions on several threads at once, as the
/// promotion workers do: against the same view of the program, each region
/// gets the members it gets alone, and no compile throws.</summary>
public class IlConcurrentCompileTests
{
    private const int Roots = 16, Threads = 8, Rounds = 10;

    // root_i(X, Y) :- three facts only root_i calls: a region of four.
    private static string Program()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Roots; i++)
        {
            sb.Append($"croot{i}(X, Y) :- cm{i}a(X, A), cm{i}b(A, B), cm{i}c(B, Y).\n");
            sb.Append($"cm{i}a(1, 2). cm{i}a(2, 3). cm{i}a(3, 4).\n");
            sb.Append($"cm{i}b(2, b). cm{i}b(3, c). cm{i}b(4, d).\n");
            sb.Append($"cm{i}c(b, x). cm{i}c(c, y). cm{i}c(d, z).\n");
        }
        return sb.ToString();
    }

    [Fact]
    public void RegionsCompiledOnSeveralThreadsGetTheirMembers()
    {
        var module = new ModuleCompiler().Compile(new ClauseReader(Program()).ReadAll().ToList());
        var baseMap = module.Predicates.ToDictionary(p => p.FunctorId);
        var rootFids = new HashSet<int>();
        for (int i = 0; i < Roots; i++)
            rootFids.Add(FunctorTable.Intern(AtomTable.Intern($"croot{i}").Id, 2));
        var roots = module.Predicates.Where(p => rootFids.Contains(p.FunctorId)).ToList();
        Assert.Equal(Roots, roots.Count);
        // ANTI-VACUITY: alone, each root is a region of four.
        var alone = new IlPredicateCompiler();
        foreach (var root in roots) Assert.Equal(4, alone.RegionMemberFids(root, baseMap).Count);

        // A new view per round, shared by every thread: each round the
        // compiler's caches are replaced and filled by all of them at once.
        var maps = new IReadOnlyDictionary<int, CompiledPredicate>[Rounds];
        for (int r = 0; r < Rounds; r++) maps[r] = new Dictionary<int, CompiledPredicate>(baseMap);
        var compiler = new IlPredicateCompiler();
        var errors = new ConcurrentQueue<string>();
        using var round = new Barrier(Threads);
        var threads = new List<Thread>();
        for (int t = 0; t < Threads; t++)
        {
            int offset = t;
            var thread = new Thread(() =>
            {
                for (int r = 0; r < Rounds; r++)
                {
                    round.SignalAndWait();
                    for (int k = 0; k < roots.Count; k++)
                    {
                        var root = roots[(k + offset) % roots.Count];
                        try
                        {
                            int members = compiler.RegionMemberFids(root, maps[r]).Count;
                            if (members != 4) errors.Enqueue($"round {r}: {members} members");
                            compiler.Compile(root, maps[r]);
                        }
                        catch (Exception ex) { errors.Enqueue($"round {r}: {ex.GetType().Name}: {ex.Message}"); }
                    }
                }
            }, 16 * 1024 * 1024);
            threads.Add(thread);
            thread.Start();
        }
        foreach (var thread in threads) Assert.True(thread.Join(300_000), "a compiling thread did not finish");
        Assert.True(errors.IsEmpty, string.Join("\n", errors.Take(10)));
    }
}
