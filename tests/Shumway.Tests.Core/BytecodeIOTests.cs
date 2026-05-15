using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class BytecodeIOTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(42)]
    [InlineData(-42)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(0x12345678)]
    public void WriteAndRead_RoundTrips(int value)
    {
        var buffer = new byte[16];
        BytecodeIO.WriteInt32(buffer, offset: 4, value);
        Assert.Equal(value, BytecodeIO.ReadInt32(buffer, 4));
    }

    [Fact]
    public void Write_IsLittleEndianRegardlessOfPlatform()
    {
        // 0x12345678 little-endian = 78 56 34 12
        var buffer = new byte[4];
        BytecodeIO.WriteInt32(buffer, 0, 0x12345678);
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, buffer);
    }

    [Fact]
    public void Read_DecodesLittleEndianBytes()
    {
        var buffer = new byte[] { 0x78, 0x56, 0x34, 0x12 };
        Assert.Equal(0x12345678, BytecodeIO.ReadInt32(buffer, 0));
    }

    [Fact]
    public void WriteAtOffset_DoesNotDisturbAdjacentBytes()
    {
        var buffer = new byte[8];
        for (int i = 0; i < buffer.Length; i++) buffer[i] = 0xFF;

        BytecodeIO.WriteInt32(buffer, 2, 0x00);
        Assert.Equal(0xFF, buffer[0]);
        Assert.Equal(0xFF, buffer[1]);
        Assert.Equal(0x00, buffer[2]);
        Assert.Equal(0x00, buffer[3]);
        Assert.Equal(0x00, buffer[4]);
        Assert.Equal(0x00, buffer[5]);
        Assert.Equal(0xFF, buffer[6]);
        Assert.Equal(0xFF, buffer[7]);
    }

    [Fact]
    public void SpanOverloads_BehaveIdenticallyToArrayOverloads()
    {
        var bufferArr = new byte[8];
        var bufferSpan = new byte[8];
        BytecodeIO.WriteInt32(bufferArr, 0, -7);
        BytecodeIO.WriteInt32(bufferSpan.AsSpan(), 0, -7);
        Assert.Equal(bufferArr, bufferSpan);

        Assert.Equal(-7, BytecodeIO.ReadInt32(bufferArr, 0));
        Assert.Equal(-7, BytecodeIO.ReadInt32((ReadOnlySpan<byte>)bufferSpan, 0));
    }
}
