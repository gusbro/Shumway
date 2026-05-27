using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Prototype validating the PE-patch path for persisted Tier-1 IL:
/// emit a method with <c>ldc.i4 &lt;buildId&gt;</c>, save the assembly to
/// bytes, locate the int32 constant's absolute byte offset in the PE,
/// overwrite it with the "runtime" id, then <c>Assembly.Load</c> the
/// patched bytes and verify the JIT sees the new value.
///
/// <para>If this is solid, the production version follows the same shape:
/// record patch sites per IL emit, serialise them alongside the .dll
/// bytes, and at LoadBundle intern each (name, arity) to map build-time
/// id → runtime id, then patch.</para>
/// </summary>
public class PePatchPrototype
{
    [Fact]
    public void PatchedLdcI4_IsVisibleToJit()
    {
        const int buildId = 0x12345678;
        const int runtimeId = 0x7EDCBA09;

        // 1) Emit the assembly. Always use Emit(OpCodes.Ldc_I4, ...) (the
        //    long 5-byte form) so the patch site is always 4 bytes wide
        //    regardless of the magnitude of the constant — Sigil-/JIT-
        //    style compaction would pick ldc.i4.s for small values and
        //    invalidate the offset math.
        var psab = new PersistedAssemblyBuilder(
            new AssemblyName("PePatchProto"), typeof(object).Assembly);
        var module = psab.DefineDynamicModule("PePatchProto");
        var tb = module.DefineType("Proto",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var mb = tb.DefineMethod("GetValue",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), Type.EmptyTypes);
        var il = mb.GetILGenerator();
        int ilOffsetBeforeLdc = il.ILOffset;
        il.Emit(OpCodes.Ldc_I4, buildId);
        il.Emit(OpCodes.Ret);
        tb.CreateType();

        using var saveStream = new MemoryStream();
        psab.Save(saveStream);
        byte[] bytes = saveStream.ToArray();

        // 2) Compute the int32 byte offset in the PE.
        //    - ILOffset reports the offset within the method body's IL
        //      stream (0 at start, advancing past each emitted instruction).
        //    - The patch target is the int32 operand of ldc.i4 — 1 byte
        //      after the opcode.
        //    - The method body itself lives at some RVA in the PE; the body
        //      starts with a header (tiny: 1 byte; fat: 12 bytes) and the
        //      IL stream follows.
        int absOffset = ResolveAbsoluteIlOffset(bytes,
            methodToken: 0x06000001 /* first MethodDef */,
            ilOffsetWithinIl: ilOffsetBeforeLdc + 1 /* skip opcode */);

        // 3) Sanity: the four bytes at that offset must be the build-id LE.
        int found = bytes[absOffset]
            | (bytes[absOffset + 1] << 8)
            | (bytes[absOffset + 2] << 16)
            | (bytes[absOffset + 3] << 24);
        Assert.Equal(buildId, found);

        // 4) Patch in place to the runtime id.
        bytes[absOffset] = (byte)(runtimeId & 0xFF);
        bytes[absOffset + 1] = (byte)((runtimeId >> 8) & 0xFF);
        bytes[absOffset + 2] = (byte)((runtimeId >> 16) & 0xFF);
        bytes[absOffset + 3] = (byte)((runtimeId >> 24) & 0xFF);

        // 5) Load the patched bytes and invoke. The JIT compiles the IL on
        //    first call — it reads the patched bytes, so the constant is
        //    the new runtime id.
        var asm = Assembly.Load(bytes);
        var type = asm.GetType("Proto")!;
        var method = type.GetMethod("GetValue")!;
        int result = (int)method.Invoke(null, null)!;

        Assert.Equal(runtimeId, result);
    }

    /// <summary>
    /// Maps an IL-relative offset (the position within a method's IL
    /// stream) onto an absolute PE byte offset usable for direct byte
    /// patching of the in-memory image.
    /// </summary>
    private static int ResolveAbsoluteIlOffset(byte[] peBytes,
        int methodToken, int ilOffsetWithinIl)
    {
        using var peStream = new MemoryStream(peBytes, writable: false);
        using var peReader = new PEReader(peStream);
        var mdReader = peReader.GetMetadataReader();
        var methodDef = mdReader.GetMethodDefinition(
            MetadataTokens.MethodDefinitionHandle(methodToken));
        int bodyRva = methodDef.RelativeVirtualAddress;
        if (bodyRva == 0)
            throw new InvalidOperationException(
                $"Method 0x{methodToken:X8} has no body (RVA=0).");
        int bodyFileOffset = RvaToFileOffset(peReader.PEHeaders, bodyRva);

        // Method body header: tiny (1 byte, top 2 bits = 0b10) or fat
        // (12 bytes, top 2 bits = 0b11). Tiny header's lower 6 bits give
        // the IL stream length in bytes; fat header has its own layout.
        byte headerFirst = peBytes[bodyFileOffset];
        int headerSize = (headerFirst & 0x03) switch
        {
            0x02 => 1,   // tiny
            0x03 => 12,  // fat
            _ => throw new InvalidOperationException(
                $"Unrecognised method-body header byte 0x{headerFirst:X2}."),
        };
        return bodyFileOffset + headerSize + ilOffsetWithinIl;
    }

    /// <summary>RVA → file offset using the PE section table.</summary>
    private static int RvaToFileOffset(PEHeaders headers, int rva)
    {
        foreach (var section in headers.SectionHeaders)
        {
            if (rva >= section.VirtualAddress
                && rva < section.VirtualAddress + section.VirtualSize)
            {
                return section.PointerToRawData + (rva - section.VirtualAddress);
            }
        }
        throw new InvalidOperationException($"RVA 0x{rva:X8} is not in any section.");
    }

    /// <summary>
    /// Same idea, scaled to three patch sites in the same method body
    /// plus enough locals/code to force a fat header (the 12-byte form
    /// triggers when the IL exceeds 63 bytes, references &gt;7 stack
    /// slots, has locals, or has exception handlers). Validates that
    /// ILOffset tracking + RvaToFileOffset stay correct across an
    /// arbitrary IL stream.
    /// </summary>
    [Fact]
    public void MultiplePatchSites_FatHeader_AllVisibleToJit()
    {
        // Each site is patched independently; the resulting method
        // returns site1 + site2 + site3 so the assertion fires only
        // if every patch landed correctly.
        const int build1 = 0x11111111, run1 = 100;
        const int build2 = 0x22222222, run2 = 200;
        const int build3 = 0x33333333, run3 = 300;

        var psab = new PersistedAssemblyBuilder(
            new AssemblyName("PePatchProtoFat"), typeof(object).Assembly);
        var module = psab.DefineDynamicModule("PePatchProtoFat");
        var tb = module.DefineType("ProtoFat",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var mb = tb.DefineMethod("Sum",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), Type.EmptyTypes);
        var il = mb.GetILGenerator();

        // Force fat header by declaring a local — this lifts the body
        // header from tiny (1 byte) to fat (12 bytes).
        var localAccum = il.DeclareLocal(typeof(int));

        // accum = build1
        int off1 = il.ILOffset;
        il.Emit(OpCodes.Ldc_I4, build1);
        il.Emit(OpCodes.Stloc, localAccum);
        // accum += build2
        il.Emit(OpCodes.Ldloc, localAccum);
        int off2 = il.ILOffset;
        il.Emit(OpCodes.Ldc_I4, build2);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, localAccum);
        // accum += build3
        il.Emit(OpCodes.Ldloc, localAccum);
        int off3 = il.ILOffset;
        il.Emit(OpCodes.Ldc_I4, build3);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ret);
        tb.CreateType();

        using var saveStream = new MemoryStream();
        psab.Save(saveStream);
        byte[] bytes = saveStream.ToArray();

        // Validate fat header is what we expect.
        int bodyFileOffset = RvaToFileOffset(
            new PEReader(new MemoryStream(bytes)).PEHeaders,
            new PEReader(new MemoryStream(bytes))
                .GetMetadataReader()
                .GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(0x06000001))
                .RelativeVirtualAddress);
        byte headerFirst = bytes[bodyFileOffset];
        Assert.Equal(0x03, headerFirst & 0x03); // fat

        // Patch all three sites.
        foreach (var (ilOff, build, run) in new[]
        {
            (off1, build1, run1),
            (off2, build2, run2),
            (off3, build3, run3),
        })
        {
            int abs = ResolveAbsoluteIlOffset(bytes,
                methodToken: 0x06000001, ilOffsetWithinIl: ilOff + 1);
            int found = bytes[abs]
                | (bytes[abs + 1] << 8)
                | (bytes[abs + 2] << 16)
                | (bytes[abs + 3] << 24);
            Assert.Equal(build, found);
            bytes[abs] = (byte)(run & 0xFF);
            bytes[abs + 1] = (byte)((run >> 8) & 0xFF);
            bytes[abs + 2] = (byte)((run >> 16) & 0xFF);
            bytes[abs + 3] = (byte)((run >> 24) & 0xFF);
        }

        var asm = Assembly.Load(bytes);
        int result = (int)asm.GetType("ProtoFat")!.GetMethod("Sum")!
            .Invoke(null, null)!;
        Assert.Equal(run1 + run2 + run3, result);
    }
}
