using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The fused cons helpers for the Tier-1 IL emit (GetListVarXVarX /
/// GetListValXVarX + the IlPredicateCompiler peephole): the `get_list; unify_*_x;
/// unify_variable_x` window becomes one call. Every test runs the SAME program at
/// Tier-0 and as a persisted region-IL bundle and demands identical answers — the
/// fused path must be semantics-invisible, including on the shapes that take the
/// generic fallback (attvars, PSTR, bound non-list, failure + backtracking).</summary>
public class FusedConsIlTests
{
    private static List<Solution> RunT0(string program, string query)
    {
        var e = new PrologEngine();
        e.ConsultString(program);
        return e.QueryAll(query).ToList();
    }

    private static List<Solution> RunT1(string program, string query)
    {
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new List<ShmoObject> { ShmoCompiler.CompileSource(program, "m") },
            EntryPoints = ExtractEntries(program),
            IncludeCompiledIl = true,
            BakePrelude = true,
        });
        Assert.True(result.Success, string.Join("; ",
            result.Diagnostics.Select(d => d.Message)));
        var e = new PrologEngine();
        e.LoadBundle(BundleReader.FromBytes(result.Bytes!));
        return e.QueryAll(query).ToList();
    }

    private static List<PredicateRef> ExtractEntries(string program)
    {
        // Every defined name/arity becomes an entry so nothing is pruned and
        // every predicate keeps a callable form for the query.
        var entries = new HashSet<PredicateRef>();
        foreach (var line in program.Split('\n'))
        {
            string t = line.Trim();
            int open = t.IndexOf('(');
            int neck = t.IndexOf(":-", StringComparison.Ordinal);
            if (t.Length == 0 || t.StartsWith("%") || t.StartsWith(":-")) continue;
            string head = neck > 0 ? t[..neck] : t.TrimEnd('.');
            head = head.Trim();
            open = head.IndexOf('(');
            if (open < 0)
            {
                entries.Add(new PredicateRef(head.TrimEnd('.'), 0));
                continue;
            }
            string name = head[..open];
            int depth = 0, commas = 0;
            for (int i = open; i < head.Length; i++)
            {
                char c = head[i];
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                else if (c == ',' && depth == 1) commas++;
                if (depth == 0) break;
            }
            entries.Add(new PredicateRef(name, commas + 1));
        }
        return entries.ToList();
    }

    private static void AssertSameAnswers(string program, string query, string var)
    {
        var t0 = RunT0(program, query).Select(s => s[var]!.ToString()).ToList();
        var t1 = RunT1(program, query).Select(s => s[var]!.ToString()).ToList();
        Assert.Equal(t0, t1);
        Assert.NotEmpty(t0);
    }

    private const string Conc =
        "conc([], L, L).\n" +
        "conc([H|T], L, [H|R]) :- conc(T, L, R).\n";

    [Fact]
    public void ReadMode_Destructure_BothPatterns()
    {
        // conc's second clause head is exactly VarXVarX (arg0) + ValXVarX (arg2).
        AssertSameAnswers(Conc, "conc([1,2,3], [4,5], R).", "R");
    }

    [Fact]
    public void WriteMode_Build_ProducesTheSameList()
    {
        // Splitting an unbound: arg2 bound, arg0/arg1 built in WRITE mode.
        var t0 = RunT0(Conc, "conc(A, B, [1,2,3]).")
            .Select(s => s["A"] + "/" + s["B"]).ToList();
        var t1 = RunT1(Conc, "conc(A, B, [1,2,3]).")
            .Select(s => s["A"] + "/" + s["B"]).ToList();
        Assert.Equal(t0, t1);
        Assert.Equal(4, t1.Count);   // all 4 splits, in the same order
    }

    [Fact]
    public void Failure_MidList_BacktracksCleanly()
    {
        // The head value unify FAILS mid-cons on the second element; the trail
        // must restore, and the enumeration must keep yielding the later answers.
        AssertSameAnswers(
            Conc + "pick([1,2], x).\npick([1,3], y).\n",
            "pick(L, W), conc(L, [9], [1,3,9]).", "W");
    }

    [Fact]
    public void DeepRecursion_LongList_NoStateDrift()
    {
        AssertSameAnswers(
            Conc +
            "mk(0, []) :- !.\nmk(N, [N|T]) :- N1 is N - 1, mk(N1, T).\n" +
            "go(Len) :- mk(400, L), conc(L, L, LL), length_(LL, Len).\n" +
            "length_([], 0).\nlength_([_|T], N) :- length_(T, M), N is M + 1.\n",
            "go(Len).", "Len");
    }

    [Fact]
    public void PartialList_TailVariable_StaysLogical()
    {
        // conc into an open list: the fused write path must leave a proper
        // unbound tail the next goal can bind.
        AssertSameAnswers(Conc,
            "conc([1,2], T, L), T = [3|T2], T2 = [].",
            "L");
    }

    [Fact]
    public void Attvar_InListPosition_TakesTheGenericRoute()
    {
        // A CLP(FD) variable inside the list: the fused fast paths must bail to
        // the generic helpers so attvar semantics (hooks, domains) survive.
        var program = Conc;
        string query = "X in 1..3, conc([X], [2], R), X = 2.";
        var e0 = new PrologEngine(); e0.UseClpfd(); e0.ConsultString(program);
        var t0 = e0.QueryAll(query).Select(s => s["R"]!.ToString()).ToList();

        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new List<ShmoObject> { ShmoCompiler.CompileSource(program, "m") },
            EntryPoints = new List<PredicateRef> { new("conc", 3) },
            IncludeCompiledIl = true,
            BakePrelude = true,
        });
        Assert.True(result.Success);
        var e1 = new PrologEngine(); e1.UseClpfd();
        e1.LoadBundle(BundleReader.FromBytes(result.Bytes!));
        var t1 = e1.QueryAll(query).Select(s => s["R"]!.ToString()).ToList();
        Assert.Equal(t0, t1);
        Assert.Single(t1);
    }

    [Fact]
    public void BoundNonList_FailsIdentically()
    {
        // get_list on an integer / atom argument: the fused path sees a non-Lis
        // tag, delegates, and must FAIL exactly like the generic route — while
        // the enumeration continues to later answers.
        AssertSameAnswers(
            Conc + "cand(7, no1).\ncand([1], yes).\ncand(foo, no2).\n",
            "cand(C, W), conc(C, [], [1]).", "W");
    }

    // ----- the get_structure/2 twins -----

    private const string Pairs =
        "swap(pair(A, B), pair(B, A)).\n" +
        "mktree(0, leaf) :- !.\n" +
        "mktree(N, node(L, R)) :- N1 is N - 1, mktree(N1, L), mktree(N1, R).\n" +
        "count(leaf, 1).\n" +
        "count(node(L, R), N) :- count(L, NL), count(R, NR), N is NL + NR.\n";

    [Fact]
    public void Struct2_ReadAndWrite_BothDirections()
    {
        // swap's head is get_structure pair/2 with VarVar (arg0 read) and the
        // second arg builds in WRITE mode (ValVal or Var depending on codegen).
        AssertSameAnswers(Pairs, "swap(pair(1, dos), S).", "S");
        // Reverse mode: destructure the OUTPUT, build the input.
        AssertSameAnswers(Pairs, "swap(P, pair(x, y)).", "P");
    }

    [Fact]
    public void Struct2_DeepTree_BuildAndConsume()
    {
        // node/2 built write-mode down, consumed read-mode up — thousands of
        // fused windows in both modes plus arithmetic between them.
        AssertSameAnswers(Pairs, "mktree(8, T), count(T, N).", "N");
    }

    [Fact]
    public void Struct2_WrongFunctor_FailsAndBacktracks()
    {
        AssertSameAnswers(
            Pairs + "c(pair(1,2), yes).\nc(other(1,2), no).\nc(pair(3,4), also).\n",
            "c(P, W), swap(P, _).", "W");
    }

    [Fact]
    public void HighRegisterSlots_GrowTheBank()
    {
        // A wide clause pushes unify slots past the default bank; the fused
        // guard must fall through to the growing path, not throw.
        var args = string.Join(", ", Enumerable.Range(1, 40).Select(i => $"[A{i}|B{i}]"));
        var argHeads = string.Join(", ", Enumerable.Range(1, 40).Select(i => $"A{i}"));
        string program =
            $"wide({args}, Out) :- Out = [{argHeads}].\n";
        var lists = string.Join(", ", Enumerable.Range(1, 40).Select(i => $"[{i}]"));
        AssertSameAnswers(program, $"wide({lists}, Out).", "Out");
    }
}
