using System.Runtime.CompilerServices;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class SharedByteArraysTests
{
    private static byte[] Blob(int length, byte seed)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(seed + i);
        return bytes;
    }

    [Fact]
    public void EqualContents_AreOneArray()
    {
        var shared = new SharedByteArrays();
        byte[] first = Blob(1000, 1);
        Assert.Same(first, shared.Share(first));
        Assert.Same(first, shared.Share(Blob(1000, 1)));
        Assert.Equal(1, shared.Count);
    }

    [Fact]
    public void DifferentContents_StayApart_EvenAtOneLength()
    {
        var shared = new SharedByteArrays();
        byte[] a = shared.Share(Blob(1000, 1));
        byte[] b = shared.Share(Blob(1000, 2));
        byte[] c = shared.Share(Blob(999, 1));
        Assert.NotSame(a, b);
        Assert.NotSame(a, c);
        Assert.Equal(3, shared.Count);
        Assert.Same(b, shared.Share(Blob(1000, 2)));
    }

    [Fact]
    public void Keep_MakesTheArrayThatIsHeld_AndRunsOncePerContent()
    {
        var shared = new SharedByteArrays();
        int made = 0;
        byte[] Copy(byte[] bytes) { made++; return (byte[])bytes.Clone(); }

        byte[] offered = Blob(64, 7);
        byte[] held = shared.Share(offered, Copy);
        Assert.NotSame(offered, held);
        Assert.Equal(offered, held);
        Assert.Same(held, shared.Share(Blob(64, 7), Copy));
        Assert.Equal(1, made);
    }

    [Fact]
    public void AnArrayNobodyHolds_IsForgotten()
    {
        var shared = new SharedByteArrays();
        byte[] kept = shared.Share(Blob(500, 3));
        ShareAndDrop(shared);
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Assert.Equal(1, shared.Count);
        GC.KeepAlive(kept);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ShareAndDrop(SharedByteArrays shared)
    {
        for (int i = 0; i < 300; i++) shared.Share(Blob(100 + i, 9));
    }
}
