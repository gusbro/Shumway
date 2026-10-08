using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-035: code compiled debuggable stays on the interpreter, where
/// a breakpoint (a byte patched into the bytecode) can fire. A fact, or a rule
/// ending in a builtin with no inline goal, carries no debug opcode for the IL
/// compiler to refuse: promoted, a breakpoint in it never fired. Compiled
/// release, the same predicates promote, so a pass is not a shape the tier
/// refuses anyway.</summary>
public sealed class DebuggableCodeStaysInterpretedTests
{
    private static int Fid(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name).Id, arity);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheIlTierLeavesDebuggableCodeAlone(bool debug)
    {
        var e = new PrologEngine();
        e.IlPromotion.Threshold = 1;
        e.ConsultString($":- set_prolog_flag(compile_mode, {(debug ? "debug" : "release")}).");
        e.ConsultString("""
            :- public fact/1, tail/1.
            fact(a).
            fact(b).
            tail(X) :- atom_length(X, _).
            """);
        for (int i = 0; i < 5; i++)
            Assert.True(e.Query("fact(_), tail(abc).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        foreach (var (name, what) in new[] { ("fact", "fact"), ("tail", "rule ending in a builtin") })
            Assert.True(e.IlPromotion.IsPromoted(Fid(name, 1)) != debug,
                debug ? $"a debuggable {what} promoted"
                      : $"the {what} did not promote compiled release: nothing here is measured");
    }
}
