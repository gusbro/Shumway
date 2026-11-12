using Shumway.Compiler.Il;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Compiler.Il;

/// <summary>What one emission leaves in its thread's state stays out of the
/// next: a compile refused inside a guard does not make the next compile on the
/// same thread see a guard that is not there.</summary>
public class IlEmissionStateTests
{
    // A guard (head, comparison, cut) with a wake point in front of the
    // comparison: the head binds the first argument.
    private const string Source = "esg(a, W) :- W > 1, !.\nesg(_, no).\n";

    private static (CompiledPredicate Esg, Dictionary<int, CompiledPredicate> Map) Esg()
    {
        int fid = FunctorTable.Intern(AtomTable.Intern("esg").Id, 2);
        var module = new ModuleCompiler().Compile(new ClauseReader(Source).ReadAll().ToList());
        var map = module.Predicates.ToDictionary(p => p.FunctorId);
        return (map[fid], map);
    }

    [Fact]
    public void ACompileRefusedInAGuardLeavesTheNextOneClean()
    {
        var compiler = new IlPredicateCompiler();
        var (snapshot, snapshotMap) = Esg();
        snapshot.IsDynamicSnapshot = true;
        // A snapshot has no bytecode: the wake point in its guard is refused,
        // from inside the guard.
        var refused = Assert.Throws<NotSupportedException>(() => compiler.Compile(snapshot, snapshotMap));
        Assert.Contains("no bytecode", refused.Message);
        // The same predicate with its bytecode compiles, on the same thread.
        var (esg, map) = Esg();
        Assert.NotNull(compiler.Compile(esg, map));
        Assert.NotNull(compiler.Compile(esg, map));
    }
}
