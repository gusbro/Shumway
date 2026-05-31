using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

// ---- (-, +, ?) example: successor_pair(-Lower, +Mid, ?Upper) ----
public partial class C246Pairs
{
    [PrologPredicate("c246_successor_pair/3")]
    public static void SuccessorPair(out int lower, int mid, ref int? upper)
    {
        lower = mid - 1;
        // Unconditional write — let the post-call unify do the
        // work. With unbound input, unify binds register to
        // mid+1. With bound-matching input, unify check
        // succeeds. With bound-wrong input, unify check fails
        // and the predicate fails.
        upper = mid + 1;
    }
}

// ---- (-, -, +) example: divmod ----
public partial class C246DivMod
{
    [PrologPredicate("c246_divmod/4")]
    public static void DivMod(int n, int d, out int q, out int r)
    {
        q = n / d;
        r = n % d;
    }
}

// ---- (+, ?, -) — single ? at a non-final position ----
public partial class C246Lookup
{
    [PrologPredicate("c246_color_code/3")]
    public static bool ColorCode(string name, ref string? canonical, out int code)
    {
        switch (name.ToLowerInvariant())
        {
            case "red":
                canonical ??= "red";
                code = 0xFF0000;
                return true;
            case "green":
                canonical ??= "green";
                code = 0x00FF00;
                return true;
            case "blue":
                canonical ??= "blue";
                code = 0x0000FF;
                return true;
            default:
                code = 0;
                return false;
        }
    }
}

// ---- Reference type in ref position ----
public partial class C246Greeter
{
    [PrologPredicate("c246_greet/2")]
    public static void Greet(string name, ref string? greeting)
    {
        // Unconditional write so the unify-after-call checks
        // bound inputs against the canonical greeting.
        greeting = $"hello, {name}";
    }
}

/// <summary>
/// Chunk 246: parameter-modifier-driven mode declarations on
/// <c>[PrologPredicate]</c>. Plain parameter = <c>+</c>, <c>out</c>
/// = <c>-</c>, <c>ref</c> = <c>?</c>. The generator emits per-mode
/// decode + unify-after-call so the user method can be written
/// declaratively (no <c>Engine</c> argument, no manual register
/// fiddling).
/// </summary>
public class Chunk246Tests
{
    // ---------- (-, +, ?) successor_pair ----------

    [Fact]
    public void SuccessorPair_OutAndUnboundRef_BindsBoth()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C246Pairs));
        var sol = engine.Query("c246_successor_pair(L, 5, U).");
        Assert.Equal(4, sol.Get<int>("L"));
        Assert.Equal(6, sol.Get<int>("U"));
    }

    [Fact]
    public void SuccessorPair_RefBoundCorrectly_StillSucceeds()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C246Pairs));
        var sol = engine.Query("c246_successor_pair(L, 5, 6).");
        Assert.True(sol.Success);
        Assert.Equal(4, sol.Get<int>("L"));
    }

    [Fact]
    public void SuccessorPair_RefBoundWrong_PredicateFails()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C246Pairs));
        // Upper bound to 99 but predicate computes 6 → unify check fails.
        Assert.False(engine.Query("c246_successor_pair(L, 5, 99).").Success);
    }

    [Fact]
    public void SuccessorPair_PlusParamUnbound_RaisesInstantiationError()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C246Pairs));
        // Mid is + — must be bound. Generator emits FromTerm<int>
        // which throws on a VarTerm via the chunk-238 path.
        Assert.Throws<Shumway.Core.PrologRuntimeException>(
            () => engine.Query("c246_successor_pair(L, M, U).").Bindings.ToList());
    }

    // ---------- (-, -, +, +) divmod ----------

    [Fact]
    public void DivMod_MultipleOutputs()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C246DivMod));
        var sol = engine.Query("c246_divmod(17, 5, Q, R).");
        Assert.Equal(3, sol.Get<int>("Q"));
        Assert.Equal(2, sol.Get<int>("R"));
    }

    [Fact]
    public void DivMod_OutBoundCorrectly_StillSucceeds()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C246DivMod));
        // Bind Q to 3 explicitly — the unify-after just checks.
        Assert.True(engine.Query("c246_divmod(17, 5, 3, R).").Success);
        Assert.False(engine.Query("c246_divmod(17, 5, 99, R).").Success);
    }

    // ---------- (+, ?, -) bool return + ref + out ----------

    [Fact]
    public void ColorCode_RefAndOut_BindsBoth()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C246Lookup));
        var sol = engine.Query("c246_color_code(red, C, X).");
        Assert.Equal("red", sol.Get<string>("C"));
        Assert.Equal(0xFF0000, sol.Get<int>("X"));
    }

    [Fact]
    public void ColorCode_UnknownColour_PredicateFails()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C246Lookup));
        Assert.False(engine.Query("c246_color_code(magenta, C, X).").Success);
    }

    // ---------- Reference-type ? ----------

    [Fact]
    public void Greet_ReferenceTypeRef_NullSignalsUnbound()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C246Greeter));
        var sol = engine.Query("c246_greet(world, G).");
        Assert.Equal("hello, world", sol.Get<string>("G"));
    }

    [Fact]
    public void Greet_BoundWrongAtom_Fails()
    {
        // Bound input that can't match the produced StringTerm —
        // the ref unify-after-call fails the predicate. We use a
        // wrong-value atom here (avoiding the string-vs-atom mapping
        // ambiguity that the corresponding "correct match" case
        // would need a custom converter to bridge).
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C246Greeter));
        Assert.False(engine.Query("c246_greet(world, hola).").Success);
    }
}
