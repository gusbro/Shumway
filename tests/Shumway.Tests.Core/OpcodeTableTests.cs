using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class OpcodeTableTests
{
    [Theory]
    [InlineData(Opcode.ReservedInvalid, 1, 0, "reserved_invalid")]
    [InlineData(Opcode.GetVariableX, 9, 2, "get_variable_x")]
    [InlineData(Opcode.GetConstant, 9, 2, "get_constant")]
    [InlineData(Opcode.GetNil, 5, 1, "get_nil")]
    [InlineData(Opcode.GetList, 5, 1, "get_list")]
    [InlineData(Opcode.PutVariableY, 9, 2, "put_variable_y")]
    [InlineData(Opcode.UnifyVariableX, 5, 1, "unify_variable_x")]
    [InlineData(Opcode.UnifyNil, 1, 0, "unify_nil")]
    [InlineData(Opcode.UnifyVoid, 5, 1, "unify_void")]
    [InlineData(Opcode.Allocate, 5, 1, "allocate")]
    [InlineData(Opcode.Deallocate, 1, 0, "deallocate")]
    [InlineData(Opcode.Call, 9, 2, "call")]
    [InlineData(Opcode.Execute, 5, 1, "execute")]
    [InlineData(Opcode.Proceed, 1, 0, "proceed")]
    [InlineData(Opcode.TryMeElse, 9, 2, "try_me_else")]
    [InlineData(Opcode.TrustMe, 1, 0, "trust_me")]
    [InlineData(Opcode.SwitchOnTerm, 17, 4, "switch_on_term")]
    [InlineData(Opcode.NeckCut, 1, 0, "neck_cut")]
    [InlineData(Opcode.Cut, 5, 1, "cut")]
    [InlineData(Opcode.CallBuiltin, 9, 2, "call_builtin")]
    [InlineData(Opcode.IsOp, 1, 0, "is_op")]
    [InlineData(Opcode.GetConstantA1, 5, 1, "get_constant_a1")]
    [InlineData(Opcode.GetListA1, 1, 0, "get_list_a1")]
    [InlineData(Opcode.GetPstr, 9, 2, "get_pstr")]
    [InlineData(Opcode.UnifyPstrHead, 5, 1, "unify_pstr_head")]
    [InlineData(Opcode.Meta, 6, 0, "meta")]
    [InlineData(Opcode.ReservedExtension, 1, 0, "reserved_extension")]
    public void Get_ReturnsDocumentedEntry(Opcode op, byte size, byte numOperands, string mnemonic)
    {
        var info = OpcodeTable.Get(op);
        Assert.True(info.IsDefined);
        Assert.Equal(op, info.Op);
        Assert.Equal(size, info.Size);
        Assert.Equal(numOperands, info.NumOperands);
        Assert.Equal(mnemonic, info.Mnemonic);
    }

    [Fact]
    public void OperandKinds_AreSpecifiedForOpcodesWithOperands()
    {
        var info = OpcodeTable.Get(Opcode.GetConstant);
        Assert.NotNull(info.OperandKinds);
        Assert.Equal(2, info.OperandKinds!.Length);
        Assert.Equal(OperandKind.Atom, info.OperandKinds[0]);
        Assert.Equal(OperandKind.Reg, info.OperandKinds[1]);
    }

    [Fact]
    public void OperandKinds_AreNullForZeroOperandOpcodes()
    {
        Assert.Null(OpcodeTable.Get(Opcode.Proceed).OperandKinds);
        Assert.Null(OpcodeTable.Get(Opcode.NeckCut).OperandKinds);
    }

    [Fact]
    public void SwitchOnTerm_HasFourAddressOperands()
    {
        var info = OpcodeTable.Get(Opcode.SwitchOnTerm);
        Assert.Equal(4, info.OperandKinds!.Length);
        Assert.All(info.OperandKinds, k => Assert.Equal(OperandKind.Address, k));
    }

    [Fact]
    public void Get_AcceptsBothByteAndOpcodeOverloads()
    {
        var a = OpcodeTable.Get(Opcode.Call);
        var b = OpcodeTable.Get((byte)Opcode.Call);
        Assert.Equal(a.Mnemonic, b.Mnemonic);
        Assert.Equal(a.Size, b.Size);
    }

    [Theory]
    [InlineData(0xB0)]   // unused in 0xA0..0xBF range
    [InlineData(0xC5)]   // unused PSTR range
    [InlineData(0xD0)]   // future-reserved
    [InlineData(0xFD)]   // future-reserved
    public void IsDefined_ReturnsFalseForUnassignedBytes(int b)
    {
        Assert.False(OpcodeTable.IsDefined((byte)b));
        Assert.False(OpcodeTable.Get((byte)b).IsDefined);
    }

    [Fact]
    public void IsDefined_ReturnsTrueForReservedInvalid()
    {
        // 0x00 is explicitly defined as ReservedInvalid so disassembly can advance past it.
        Assert.True(OpcodeTable.IsDefined(0x00));
        Assert.Equal("reserved_invalid", OpcodeTable.Get((byte)0x00).Mnemonic);
    }

    [Fact]
    public void TwoOperandOpcodes_Are9BytesTotal()
    {
        // All opcodes with two int operands should be 1 + 2*4 = 9 bytes.
        for (int b = 0; b < 256; b++)
        {
            var info = OpcodeTable.Get((byte)b);
            if (info.NumOperands == 2 && info.Op != Opcode.Meta)
                Assert.Equal(9, info.Size);
        }
    }

    [Fact]
    public void OneOperandOpcodes_Are5BytesTotal()
    {
        for (int b = 0; b < 256; b++)
        {
            var info = OpcodeTable.Get((byte)b);
            if (info.NumOperands == 1 && info.Op != Opcode.Meta)
                Assert.Equal(5, info.Size);
        }
    }

    [Fact]
    public void ZeroOperandOpcodes_Are1ByteExceptMeta()
    {
        for (int b = 0; b < 256; b++)
        {
            var info = OpcodeTable.Get((byte)b);
            if (info.IsDefined && info.NumOperands == 0 && info.Op != Opcode.Meta)
                Assert.Equal(1, info.Size);
        }
    }
}
