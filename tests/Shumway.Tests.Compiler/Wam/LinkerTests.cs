using System.Linq;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

/// <summary>
/// Linker tests. The external-symbols path (ADR-015 chunk B) lets a
/// transient query chunk be linked against a persistent code region that
/// was linked earlier — a call to a predicate outside the chunk is
/// patched to its address in that region instead of the
/// undefined-predicate sentinel.
/// </summary>
public class LinkerTests
{
    // Reads back the target operand of a predicate's first call site
    // from a linked byte buffer that was linked at loadOffset 0.
    private static int FirstCallTarget(Linker.LinkResult link, int functorId)
    {
        int addr = link.Addresses[functorId];
        var pred = link.PredicatesByAddress[addr];
        int opcodeOffset = pred.CallSites[0].OpcodeOffset;
        return BytecodeIO.ReadInt32(link.Bytecode, addr + opcodeOffset + 1);
    }

    [Fact]
    public void Call_ToAPredicateOutsideTheSet_IsUndefinedWithoutExternalSymbols()
    {
        var module = new ModuleCompiler().Compile(
            new ClauseReader("uses(X) :- lib(X).\n").ReadAll().ToList());
        int usesFid = FunctorTable.Intern(
            AtomTable.Intern("uses", permanent: true).Id, 1);

        var link = new Linker().Link(module);

        Assert.True(CallTarget.IsUnresolved(FirstCallTarget(link, usesFid)));
    }

    [Fact]
    public void Call_ToAnExternalSymbol_IsPatchedToTheGivenAddress()
    {
        var module = new ModuleCompiler().Compile(
            new ClauseReader("uses(X) :- lib(X).\n").ReadAll().ToList());
        int usesFid = FunctorTable.Intern(
            AtomTable.Intern("uses", permanent: true).Id, 1);
        int libFid = FunctorTable.Intern(
            AtomTable.Intern("lib", permanent: true).Id, 1);

        const int libAddress = 4242;
        var link = new Linker().Link(
            module, loadOffset: 0,
            externalSymbols: new Dictionary<int, int> { [libFid] = libAddress });

        Assert.Equal(libAddress, FirstCallTarget(link, usesFid));
    }

    [Fact]
    public void ExternalSymbols_DoNotOverrideALocalDefinition()
    {
        // 'lib' is defined in the set; the external entry must be ignored.
        var module = new ModuleCompiler().Compile(
            new ClauseReader("uses(X) :- lib(X).\nlib(ok).\n").ReadAll().ToList());
        int usesFid = FunctorTable.Intern(
            AtomTable.Intern("uses", permanent: true).Id, 1);
        int libFid = FunctorTable.Intern(
            AtomTable.Intern("lib", permanent: true).Id, 1);

        var link = new Linker().Link(
            module, loadOffset: 0,
            externalSymbols: new Dictionary<int, int> { [libFid] = 4242 });

        Assert.Equal(link.Addresses[libFid], FirstCallTarget(link, usesFid));
    }
}
