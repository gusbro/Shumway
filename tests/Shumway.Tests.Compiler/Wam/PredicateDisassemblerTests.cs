using System.Linq;
using Shumway.Compiler.Wam;
using Xunit;

namespace Shumway.Tests.Compiler.Wam;

public class PredicateDisassemblerTests
{
    private const string NrevSource =
        "nrev([], []).\n" +
        "nrev([H|T], R) :- nrev(T, RT), conc(RT, [H], R).\n" +
        "conc([], L, L).\n" +
        "conc([H|T], L, [H|R]) :- conc(T, L, R).\n" +
        ":- dynamic foo/1.\n";

    [Fact]
    public void Disassemble_GroupsPredicates_SkipsDirectives()
    {
        var entries = PredicateDisassembler.Disassemble(NrevSource);

        // Two predicates, in first-seen order; the :- dynamic directive is skipped.
        Assert.Equal(new[] { ("nrev", 2), ("conc", 3) },
            entries.Select(e => (e.Name, e.Arity)).ToArray());
        Assert.All(entries, e => Assert.Null(e.Error));
    }

    [Fact]
    public void Disassemble_ShowsIndexingAndTailCall()
    {
        var conc = Assert.Single(
            PredicateDisassembler.Disassemble(NrevSource, new[] { ("conc", 3) }));
        Assert.Null(conc.Error);

        // Multi-clause predicate over a list-vs-nil first arg → indexed dispatch,
        // and the recursive clause is a tail call (execute, not call).
        Assert.Contains("switch_on_term", conc.Text);
        Assert.Contains("get_list", conc.Text);
        Assert.Contains("execute", conc.Text);
        Assert.StartsWith("=== conc/3 ", conc.Text);
    }

    [Fact]
    public void Disassemble_Filter_RestrictsResult()
    {
        var entries = PredicateDisassembler.Disassemble(NrevSource, new[] { ("nrev", 2) });
        var only = Assert.Single(entries);
        Assert.Equal(("nrev", 2), (only.Name, only.Arity));
    }

    [Fact]
    public void Disassemble_FusedArithmetic_Appears()
    {
        // ADR-018: a flat comparison compiles to the fused a_int_cmp opcode.
        var entries = PredicateDisassembler.Disassemble("p(X) :- X > 0.");
        Assert.Contains("a_int_cmp", Assert.Single(entries).Text);
    }

    [Fact]
    public void IfThenElse_DisassemblesToTheSynthesizedHelper()
    {
        // The disassembler runs the SAME transform pipeline as the engine
        // (ClausePipeline: DCG + MetaTransform + phrase + modes), so a control
        // construct shows the lowered helper it actually compiles to — not the
        // raw `;`/`->`. `p :- (C -> T ; E)` becomes `p :- $disj_N` plus a
        // two-clause `$disj_N` helper.
        var entries = PredicateDisassembler.Disassemble("p(R) :- ( 1 > 2 -> R = yes ; R = no ).");

        // p/1 tail-calls the helper; a $disj helper predicate is emitted.
        var p = entries.Single(e => e.Name == "p");
        Assert.Contains("execute", p.Text);
        Assert.Contains(entries, e => e.Name.StartsWith("$disj_") && e.Arity == 1);
    }

    [Fact]
    public void Release_OmitsDbgInfo_Debug_IncludesIt()
    {
        // compile_mode=release (the default) emits NO meta dbg_info markers at
        // all — not per-clause, not per-predicate; debug includes one per clause.
        const string src = "p(a).\np(b).\np([H|T]) :- p(T).";
        string release = PredicateDisassembler.Disassemble(src).Single().Text;
        string debug = PredicateDisassembler.Disassemble(src, emitDebugInfo: true).Single().Text;
        Assert.DoesNotContain("meta", release);
        Assert.Contains("meta dbg_info", debug);
    }
}
