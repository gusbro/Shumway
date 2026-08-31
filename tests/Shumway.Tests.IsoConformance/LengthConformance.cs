using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.IsoConformance;

/// <summary>Neumerkel's length/2 and atom_length/2 batteries
/// (complang.tuwien.ac.at/ulrich/iso-prolog/length), transcribed. Where the
/// battery allows "loops | resource_error" this engine does better where
/// soundness permits: a second argument that would have to be both a list and
/// an integer has no solution, so length(L,L) and length([a,b|X],X) FAIL
/// outright; a cyclic list has no finite length and fails likewise (it used
/// to spin uninterruptibly). The genuinely open enumeration (case 30) loops
/// at safe points, which time_out/3 can interrupt.</summary>
public sealed class LengthConformance
{
    private static PrologEngine Fresh() => new();

    private static void True(string query)
        => Assert.True(Fresh().Query(query).Success, query);

    private static void False(string query)
        => Assert.False(Fresh().Query(query).Success, query);

    private static void Raises(string goal, string errorPattern)
        => True($"catch(({goal}), error({errorPattern}, _), true), "
              + $"\\+ catch(({goal}), _, fail).");

    // ---- atom_length/2 (cases a1..a5) ----

    [Fact] public void A1_BothVar() =>
        Raises("atom_length(_, _)", "instantiation_error");
    [Fact] public void A2_AtomLength() =>
        Raises("atom_length(a, a)", "type_error(integer, a)");
    [Fact] public void A3_FloatLength() =>
        Raises("atom_length(a, 1.5)", "type_error(integer, 1.5)");
    [Fact] public void A4_NegativeLength() =>
        Raises("atom_length(a, -1)", "domain_error(not_less_than_zero, -1)");
    [Fact] public void A5_IntegerAtom() =>
        Raises("atom_length(1, _)", "type_error(atom, 1)");

    // ---- length/2 (cases 1..31) ----

    [Fact]
    public void C01_BothVar_EnumeratesFromEmpty()
    {
        var sols = Fresh().QueryAll("length(L, N).").Take(3).ToList();
        Assert.Equal(3, sols.Count);
        Assert.Equal("0", sols[0]["N"]!.ToString());
        Assert.Equal("1", sols[1]["N"]!.ToString());
        Assert.Equal("2", sols[2]["N"]!.ToString());
        Assert.Equal("[]", sols[0]["L"]!.ToString());
    }

    [Fact] public void C02_ZeroGivesEmpty() => True("length(L, 0), L == [].");
    [Fact] public void C03_ConsAtZero() => False("length([_|_], 0).");
    [Fact] public void C04_NonListFails() => False("length(2, 0).");
    [Fact] public void C05_ImproperTailAtZero() => False("length([_|2], 0).");
    [Fact] public void C06_ImproperTailVarN() => False("length([_|2], _).");
    [Fact] public void C07_ImproperTailAtTwo() => False("length([_|2], 2).");
    [Fact] public void C08_NegativeVarList() =>
        Raises("length(_, -1)", "domain_error(not_less_than_zero, -1)");
    [Fact] public void C09_NegativeEmpty() =>
        Raises("length([], -1)", "domain_error(not_less_than_zero, -1)");
    [Fact] public void C10_NegativeNonList() =>
        Raises("length(a, -1)", "domain_error(not_less_than_zero, -1)");
    [Fact] public void C11_NegativeFloatEmpty() =>
        Raises("length([], -0.5)", "type_error(integer, -0.5)");
    [Fact] public void C12_NegativeFloatVar() =>
        Raises("length(_, -0.5)", "type_error(integer, -0.5)");
    [Fact] public void C13_FloatOnProperList() =>
        Raises("length([a], 1.0)", "type_error(integer, 1.0)");
    [Fact] public void C14_FloatOnVar() =>
        Raises("length(_, 1.0)", "type_error(integer, 1.0)");
    [Fact] public void C15_FractionOnVar() =>
        Raises("length(_, 1.5)", "type_error(integer, 1.5)");
    [Fact] public void C16_HugeFloat() =>
        Raises("length(_, 1.0e99)", "type_error(integer, _)");
    [Fact] public void C17_BigIntOnEmpty() => False("N is 2^52, length([], N).");
    [Fact] public void C18_CompoundLength() =>
        Raises("length([], 0+0)", "type_error(integer, 0+0)");
    [Fact] public void C19_MinusVarOnEmpty() =>
        Raises("length([], -_)", "type_error(integer, -_)");
    [Fact] public void C20_MinusVarOnList() =>
        Raises("length([a], -_)", "type_error(integer, -_)");

    // 21/22: the battery allows loops | resource_error; no solution exists
    // (the tail/N would have to be a list AND an integer), so this engine
    // fails, soundly and finitely.
    [Fact] public void C21_TailIsOwnLength_Fails() => False("length([a,b|X], X).");
    [Fact] public void C22_ListIsOwnLength_Fails() => False("length(L, L).");

    [Fact] public void C23_BoundListAsLength() =>
        Raises("L = [_|_], length(L, L)", "type_error(integer, [_|_])");
    [Fact] public void C24_SingletonAsLength() =>
        Raises("L = [_], length(L, L)", "type_error(integer, [_])");
    [Fact] public void C25_GroundListAsLength() =>
        Raises("L = [1], length(L, L)", "type_error(integer, [1])");

    [Fact] public void C26_CyclicBehindDisjunction_FirstAnswer() =>
        True("L = [a|L], ( true ; length(L, _) ), !.");
    [Fact] public void C27_CyclicAtZero_Fails() => False("L = [a|L], length(L, 0).");
    [Fact] public void C28_CyclicAtSeven_Fails() => False("L = [a|L], length(L, 7).");

    private static PrologEngine WithFreeze()
    {
        var e = Fresh();
        Assert.True(e.Query("use_module(library(freeze)).").Success);
        return e;
    }

    [Fact] public void C29_FreezeToNil_Fails() =>
        Assert.False(WithFreeze().Query("freeze(L, L = []), length(L, L).").Success);

    [Fact]
    public void C30_FreezeGrowsForever_LoopsInterruptibly()
    {
        // The battery allows loops | resource_error. The loop must at least
        // be interruptible — it once could not be — which time_out/3 proves.
        Assert.True(WithFreeze().Query(
            "freeze(L, L = [_|L]), time_out(length(L, _), 300, R), R == time_out.")
            .Success);
    }

    [Fact] public void C31_FreezeWithBigIntLength_Fails() =>
        Assert.False(WithFreeze().Query(
            "freeze(L, L = [_|L]), N is 2^64, length(L, N).").Success);
}
