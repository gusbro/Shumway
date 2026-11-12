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

    // The linker's case: a helper the bundle ships no bytecode for (a \+ of
    // the engine's own), whose guard inlines a callee with a wake point, then a
    // predicate with wake points in its body and no guard.
    private const string LinkSource = """
        esh(X, W) :- esc(X, W), !, fail.
        esh(_, _).
        esc(a, W) :- W > 1.
        esz(A, B, C) :- esf(A, B), C > 0, A > 0, B > 0.
        esf(1, 1).
        esf(2, 2).
        """;

    [Fact]
    public void APersistedCompileRefusedInAnInlinedGuardLeavesTheNextOneClean()
    {
        int Fid(string name, int arity) => FunctorTable.Intern(AtomTable.Intern(name).Id, arity);
        var module = new ModuleCompiler().Compile(new ClauseReader(LinkSource).ReadAll().ToList());
        var map = module.Predicates.ToDictionary(p => p.FunctorId);
        var shipped = new HashSet<int>(map.Keys);
        shipped.Remove(Fid("esh", 2));
        var compiler = new IlPredicateCompiler();
        compiler.BeginPersistEmit(shipped);
        try
        {
            var refused = Assert.Throws<NotSupportedException>(() => compiler.Compile(map[Fid("esh", 2)], map));
            Assert.Contains("no bytecode", refused.Message);
        }
        finally { compiler.EndPersistEmit(); }
        // Refused as inside the helper's guard, which it is not in.
        Assert.NotNull(compiler.Compile(map[Fid("esz", 3)], map));
    }
}
