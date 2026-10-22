using System.Linq;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>The pools are released with the code: a counted entry lives
/// while a code unit names it, a pinned one for the pool's life, and the
/// sweep frees what nothing names, its id reused by the next intern.</summary>
public sealed class LiteralPoolReleaseTests
{
    private static int IdOf(LiteralPool<TextLiteral> pool, string text)
    {
        for (int i = 0; i < pool.Count; i++)
            if (pool.Items[i].Text == text) return i;
        throw new Xunit.Sdk.XunitException($"no literal '{text}'");
    }

    private static byte[] Compile(LiteralPools pools, string src)
        => new PredicateCompiler().Compile(
            new ClauseReader(src).ReadAll().ToList(),
            pools.Strings, pools.Floats, pools.BigInts, imageLiterals: pools.Images).Bytecode;

    [Fact]
    public void ACountedEntryIsFreedAtZeroAndItsIdReused()
    {
        var pool = new LiteralPool<double>();
        int v0 = pool.Version;
        int a = pool.Intern(1.5);
        Assert.Equal(a, pool.Intern(1.5));
        Assert.NotEqual(v0, pool.Version);
        Assert.Equal(0, pool.RefCount(a));
        Assert.Equal(1, pool.PendingCount);

        int v1 = pool.Version;
        pool.AddRef(a);
        pool.AddRef(a);
        Assert.Equal(2, pool.RefCount(a));
        Assert.Equal(v1, pool.Version);           // a reference changes no value
        Assert.Equal(0, pool.Sweep());             // referenced: not freed
        Assert.Equal(0, pool.PendingCount);
        pool.Release(a);
        Assert.Equal(0, pool.PendingCount);        // still referenced once
        pool.Release(a);
        Assert.Equal(1, pool.PendingCount);
        Assert.Equal(v1, pool.Version);

        Assert.Equal(1, pool.Sweep());
        Assert.True(pool.IsFree(a));
        Assert.Equal(1, pool.FreeCount);
        Assert.NotEqual(v1, pool.Version);
        Assert.False(pool.TryGetId(1.5, out _));
        Assert.Equal(default, pool.Items[a]);

        int b = pool.Intern(2.5);
        Assert.Equal(a, b);                        // the freed id first
        Assert.Equal(0, pool.FreeCount);
        Assert.Equal(2.5, pool.Items[b]);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void APinnedEntryIsNeverFreed()
    {
        var pool = new LiteralPool<double>();
        int a = pool.InternPinned(1.5);
        Assert.True(pool.IsPinned(a));
        pool.AddRef(a);
        pool.Release(a);
        pool.Release(a);                           // a release never counts against a pin
        Assert.Equal(0, pool.RefCount(a));
        Assert.Equal(0, pool.Sweep());
        Assert.False(pool.IsFree(a));
        Assert.Equal(a, pool.Intern(1.5));
        // Pinned later: a counted entry at zero that static code then names.
        int b = pool.Intern(2.5);
        pool.Pin(b);
        Assert.Equal(0, pool.Sweep());
        Assert.True(pool.IsPinned(b));
    }

    [Fact]
    public void AReleaseBelowZeroAndAReferenceToAFreedIdAreBugs()
    {
        var pool = new LiteralPool<double>();
        int a = pool.Intern(1.5);
        Assert.Throws<System.InvalidOperationException>(() => pool.Release(a));
        pool.Sweep();
        Assert.Throws<System.InvalidOperationException>(() => pool.AddRef(a));
        Assert.Throws<System.InvalidOperationException>(() => pool.Pin(a));
    }

    [Fact]
    public void ASweepIsDueForAShareOfThePool()
    {
        var pool = new LiteralPool<double>();
        for (int i = 0; i < 31; i++) pool.Intern(i);
        Assert.False(pool.SweepDue);               // under the floor
        pool.Intern(31);
        Assert.True(pool.SweepDue);                // 32 of 32
        Assert.Equal(32, pool.Sweep());

        var big = new LiteralPool<double>();
        for (int i = 0; i < 1000; i++) { int id = big.Intern(i); big.AddRef(id); }
        for (int i = 0; i < 100; i++) big.Release(i);
        Assert.False(big.SweepDue);                // 100 of 1000: under an eighth
        for (int i = 100; i < 125; i++) big.Release(i);
        Assert.True(big.SweepDue);
        Assert.Equal(125, big.Sweep());
        Assert.Equal(125, big.FreeCount);
    }

    [Fact]
    public void TheWalkCountsEveryLiteralOperandOfACodeUnit()
    {
        var pools = new LiteralPools();
        byte[] code = Compile(pools, """
            p("abc", 2.5, 100000000000000000000, X, R, T) :- R is X + 3.5, T = f(a, b, c, d, e, f, g, h, i).
            """);
        Assert.True(pools.NamesAny(code));
        int s = IdOf(pools.Strings, "abc");
        Assert.True(pools.Floats.TryGetId(2.5, out int f));
        Assert.True(pools.Floats.TryGetId(3.5, out int g));
        Assert.True(pools.BigInts.TryGetId(System.Numerics.BigInteger.Parse("100000000000000000000"), out int b));
        Assert.Equal(1, pools.Images.Count);
        Assert.Equal(0, pools.Strings.RefCount(s));

        Assert.True(pools.AddRefs(code));
        Assert.Equal(1, pools.Strings.RefCount(s));
        Assert.Equal(1, pools.Floats.RefCount(f));
        Assert.Equal(1, pools.Floats.RefCount(g));          // the a_eval_push float
        Assert.Equal(1, pools.BigInts.RefCount(b));
        Assert.Equal(1, pools.Images.RefCount(0));
        Assert.Equal(0, pools.Sweep());

        // A second unit naming the same literal, and the first one leaving.
        byte[] again = Compile(pools, """q("abc").""");
        pools.AddRefs(again);
        Assert.Equal(2, pools.Strings.RefCount(s));
        Assert.True(pools.Release(code));
        Assert.Equal(1, pools.Strings.RefCount(s));
        Assert.Equal(0, pools.Floats.RefCount(f));
        Assert.Equal(4, pools.Sweep());                      // 2.5, 3.5, the big integer, the image
        Assert.False(pools.Strings.IsFree(s));
        Assert.True(pools.Images.IsFree(0));

        var view = pools.ViewsFor(again);
        Assert.Equal("abc", view.StringView[s].Text);
        Assert.Equal(pools.Strings.Count, view.StringView.Count);
        Assert.Throws<System.InvalidOperationException>(() => view.FloatView[f]);

        pools.Pin(again);
        Assert.True(pools.Strings.IsPinned(s));
        Assert.False(pools.NamesAny(Compile(pools, "r(1, a, [x]).")));
    }
}
