using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A typed <c>[PrologPredicate]</c>'s arguments and results go
/// straight between the heap and C# values for the shapes with a direct form
/// (the scalars, strings, lists and arrays of them, <c>[PrologTerm]</c>
/// types), and through Term trees otherwise. The two ways give the same
/// answers and raise the same errors: every goal runs on an engine that
/// converts directly and on one whose registered converter sends every
/// conversion the Term way.</summary>
public sealed partial class DirectMarshallingTests
{
    [PrologTerm("pt")]
    public sealed partial record Pt(long X, double Y, string Name);

    [PrologTerm("box")]
    public sealed partial record Box(Pt Corner, List<long> Ids, bool Open);

    [PrologTerm("nil_box")]
    public sealed partial class NilBox { }

    public sealed record Unused(int V);

    public static partial class Fx
    {
        [PrologPredicate("dm_sum/2")]
        public static long Sum(List<long> xs) { long s = 0; foreach (long x in xs) s += x; return s; }

        [PrologPredicate("dm_isum/2")]
        public static long ISum(int[] xs) { long s = 0; foreach (int x in xs) s += x; return s; }

        [PrologPredicate("dm_dsum/2")]
        public static double DSum(IEnumerable<double> xs) => xs.Sum();

        [PrologPredicate("dm_count_true/2")]
        public static int CountTrue(IReadOnlyList<bool> xs) => xs.Count(x => x);

        [PrologPredicate("dm_join/2")]
        public static string Join(List<string> xs) => string.Join(",", xs);

        [PrologPredicate("dm_len/2")]
        public static int Len(string s) => s.Length;

        [PrologPredicate("dm_range/2")]
        public static List<long> Range(int n) => Enumerable.Range(0, n).Select(i => (long)i).ToList();

        [PrologPredicate("dm_big/1")]
        public static List<long> Big() => new() { long.MaxValue, long.MinValue, 1 };

        [PrologPredicate("dm_floats/1")]
        public static double[] Floats() => new[] { 0.5, -2.0 };

        [PrologPredicate("dm_words/1")]
        public static List<string> Words() => new() { "a", "B c", "" };

        [PrologPredicate("dm_flags/1")]
        public static bool[] Flags() => new[] { true, false };

        [PrologPredicate("dm_pt_x/2")]
        public static long PtX(Pt p) => p.X;

        [PrologPredicate("dm_box/2")]
        public static Box MakeBox(long id) => new(new Pt(id, 1.5, "c"), new() { id, id + 1 }, true);

        [PrologPredicate("dm_box_ids/2")]
        public static List<long> BoxIds(Box b) => b.Ids;

        [PrologPredicate("dm_nil/1")]
        public static NilBox Nil() => new();

        [PrologPredicate("dm_is_nil/1")]
        public static bool IsNil(NilBox n) => n is not null;

        [PrologPredicate("dm_out/2")]
        public static void Out(int n, out List<string> words) => words = Enumerable.Repeat("w", n).ToList();

        // The host side: a goal solved from inside a foreign, its result read
        // back as a C# value (SolveOnce<T>), the type named by an atom.
        [PrologPredicate("dm_solve/3")]
        public static bool Solve(Shumway.Core.Activation engine)
        {
            var host = (PrologEngine)engine.Host!;
            var goalTerm = Shumway.Embedding.TermReader.Materialize(engine, Slot(engine, 0));
            string type = Shumway.Embedding.TermReader.Materialize(engine, Slot(engine, 1)).ToString()!;
            var goal = new Shumway.Compiler.Ast.CompoundTerm("=", new Shumway.Compiler.Ast.Term[]
            {
                new Shumway.Compiler.Ast.VarTerm("R"), goalTerm,
            });
            string text = type switch
            {
                "long" => host.SolveOnce<long>(engine, goal, "R", out long l) ? l.ToString() : "no",
                "double" => host.SolveOnce<double>(engine, goal, "R", out double d) ? d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "no",
                "string" => host.SolveOnce<string>(engine, goal, "R", out string str) ? "<" + str + ">" : "no",
                "longs" => host.SolveOnce<List<long>>(engine, goal, "R", out List<long> ls) ? string.Join(",", ls) : "no",
                "strings" => host.SolveOnce<string[]>(engine, goal, "R", out string[] ss) ? string.Join(",", ss) : "no",
                _ => "?",
            };
            return engine.UnifyRegisterWithCell(2, Shumway.Core.Cell.Atom(Shumway.Core.AtomTable.Intern(text).Id));
        }

        private static int Slot(Shumway.Core.Activation engine, int reg)
        {
            int slot = engine.AllocateHeap(1);
            engine.SetHeap(slot, engine.GetRegister(reg));
            return slot;
        }

        [PrologPredicate("dm_each/1", NonDeterministic = true)]
        public static IEnumerable<Pt> Each()
        {
            yield return new Pt(1, 0.5, "a");
            yield return new Pt(2, 1.0, "b");
        }
    }

    private static readonly string[] Goals =
    {
        "dm_sum([1,2,3], S)",
        "dm_sum([], S)",
        "dm_sum([1,a], S)",
        "dm_sum([1|_], S)",
        "dm_sum(_, S)",
        "dm_sum([1.5], S)",
        "dm_sum(foo, S)",
        "dm_sum([1, 99999999999999999999], S)",
        "L = [1|L], dm_sum(L, S)",
        "dm_isum([1, 99999999999], S)",
        "dm_isum([1, 2], S)",
        "dm_dsum([1, 2.5], S)",
        "dm_count_true([true, false, true], N)",
        "dm_count_true([true, yes], N)",
        "dm_join([a, 'B', \"cd\", []], J)",
        "dm_join(\"ab\", J)",
        "dm_join([a, f(x)], J)",
        "dm_len(hello, N)",
        "dm_len(\"hello\", N)",
        "dm_len([0'h, 0'i], N)",
        "dm_len([], N)",
        "dm_len(12, N)",
        "dm_range(3, L)",
        "dm_big(L)",
        "dm_floats(L)",
        "dm_words(L)",
        "dm_flags(L)",
        "dm_pt_x(pt(7, 2.5, n), X)",
        "dm_pt_x(pt(7, 2, n), X)",
        "dm_pt_x(pt(7, 2.5), X)",
        "dm_pt_x(pt(7, x, n), X)",
        "dm_pt_x(pq(7, 2.5, n), X)",
        "dm_pt_x(pt(_, 2.5, n), X)",
        "dm_box(4, B)",
        "dm_box(4, B), dm_box_ids(B, Ids)",
        "dm_box_ids(box(pt(1, 0.5, a), [9, 8], false), Ids)",
        "dm_box_ids(box(pt(1, 0.5, a), [9, x], false), Ids)",
        "dm_nil(N)",
        "dm_is_nil(nil_box)",
        "dm_is_nil(other)",
        "dm_out(2, W)",
        "findall(P, dm_each(P), Ps)",
        "dm_solve(42, long, T)",
        "dm_solve(4.5, double, T)",
        "dm_solve(7, double, T)",
        "dm_solve(abc, string, T)",
        "dm_solve(\"xy\", string, T)",
        "dm_solve([1, 2, 3], longs, T)",
        "dm_solve([a, b], strings, T)",
        "dm_solve([1, a], longs, T)",
        "dm_solve(f(x), long, T)",
    };

    private static PrologEngine Engine(bool viaTerms)
    {
        var e = new PrologEngine();
        e.RegisterPredicates(typeof(Fx));
        if (viaTerms)
            e.RegisterConverter<Unused>(
                (_, u) => new Shumway.Compiler.Ast.IntTerm(u.V),
                t => new Unused((int)((Shumway.Compiler.Ast.IntTerm)t).Value));
        return e;
    }

    // Generated variable names carry heap addresses, which may differ.
    private static readonly Regex VarNames = new(@"_[A-Z]?\d+");

    private static string Outcome(PrologEngine e, string goal)
    {
        try
        {
            var sol = e.Query(goal + ".");
            if (!sol.Success) return "false";
            return string.Join(" ", sol.Bindings.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + "=" + VarNames.Replace(kv.Value.ToString() ?? "", "_")));
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + VarNames.Replace(ex.Message, "_");
        }
    }

    [Fact]
    public void TheDirectWayAndTheTermWayAgree()
    {
        var direct = Engine(viaTerms: false);
        var viaTerms = Engine(viaTerms: true);
        Assert.False(direct.HasUserConverters);
        Assert.True(viaTerms.HasUserConverters);
        foreach (string goal in Goals)
            Assert.Equal(goal + " -> " + Outcome(viaTerms, goal), goal + " -> " + Outcome(direct, goal));
    }

    [Fact]
    public void TheDirectWayBuildsNoTermTree()
    {
        // The same loop on both engines: the direct way allocates the list it
        // hands the method and nothing per element besides, the Term way a
        // tree node per element as well.
        const string Program = @"
            dm_loop(0, _) :- !.
            dm_loop(K, L) :- dm_sum(L, _), dm_range(20, _), K1 is K - 1, dm_loop(K1, L).
            dm_bench(K) :- numlist(1, 100, L), dm_loop(K, L).
        ";
        var direct = Engine(viaTerms: false);
        var viaTerms = Engine(viaTerms: true);
        direct.ConsultString(Program);
        viaTerms.ConsultString(Program);
        Assert.True(direct.Query("dm_bench(10).").Success);
        Assert.True(viaTerms.Query("dm_bench(10).").Success);
        long d = AllocatedBy(direct, "dm_bench(2000).");
        long t = AllocatedBy(viaTerms, "dm_bench(2000).");
        Assert.True(d * 5 < t, $"the direct way should allocate under a fifth of the Term way: direct={d} terms={t}");
    }

    private static long AllocatedBy(PrologEngine e, string goal)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(e.Query(goal).Success);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
