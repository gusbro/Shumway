using System.Text;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Shumway.Core;

namespace Shumway.Compiler.Wam;

/// <summary>
/// Compiles the static predicates in a Prolog source and renders their WAM
/// bytecode as human-readable disassembly — the post-indexing layout
/// (<c>switch_on_term</c> / <c>try</c> / <c>retry</c> / <c>trust</c> chains and
/// per-clause bodies) the Tier-0 interpreter actually runs. Intended for
/// inspecting code generation while optimising; backs the <c>shumway-disasm</c>
/// CLI and is directly callable from tests / tooling.
/// </summary>
public static class PredicateDisassembler
{
    /// <summary>One predicate's compiled form: its <c>Name/Arity</c> label and
    /// the disassembled text, or a compile error in <see cref="Error"/>.</summary>
    public sealed record Entry(string Name, int Arity, string Text, string? Error);

    /// <summary>Parses <paramref name="source"/>, groups its facts / rules (DCG
    /// rules expanded; directives skipped) by predicate in first-seen order, and
    /// compiles each with the indexing <see cref="PredicateCompiler"/>.
    /// <paramref name="filter"/>, when non-null, restricts the result to the
    /// named <c>Name/Arity</c> indicators.</summary>
    public static IReadOnlyList<Entry> Disassemble(
        string source,
        IReadOnlyCollection<(string Name, int Arity)>? filter = null,
        bool emitDebugInfo = false,
        bool arityCompat = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Phase 30 Arity/Prolog32 sources ($...$ atoms, #line markers, the
        // `extrn` declaration operator) lex only with arity_compat on — the
        // corpus files under C:\temp\test / testGen start with a `#line`
        // directive, so the flag must be set before the first token.
        ClauseReader reader = arityCompat
            ? new ClauseReader(
                new global::Shumway.Compiler.Lexer.Lexer(source),
                OperatorTable.Default(),
                new Parsing.PrologFlags { ArityCompat = true })
            : new ClauseReader(source);
        // Same transform pipeline the engine runs (DCG + meta-call lowering +
        // phrase + mode specialization), so the disassembly is exactly what the
        // interpreter executes — including the synthesised if-then-else / `\+`
        // helper predicates. A mode-free table makes specialization a no-op.
        var clauses = ClausePipeline.Apply(
            reader.ReadAll(), new Modes.ModeTable());

        // Group by (head functor, arity), preserving first-seen order.
        var order = new List<(string Name, int Arity)>();
        var groups = new Dictionary<(string, int), List<Clause>>();
        foreach (Clause clause in clauses)
        {
            if (clause.Kind == ClauseKind.Directive) continue;
            (string name, int arity) = HeadIndicator(clause);
            var key = (name, arity);
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = new List<Clause>();
                order.Add(key);
            }
            list.Add(clause);
        }

        var result = new List<Entry>();
        foreach ((string name, int arity) in order)
        {
            if (filter is not null && !filter.Contains((name, arity))) continue;
            string label = $"{name}/{arity}";
            try
            {
                CompiledPredicate pred = new PredicateCompiler { EmitDebugInfo = emitDebugInfo }
                    .Compile(groups[(name, arity)]);
                result.Add(new Entry(name, arity, Format(label, pred.Bytecode), Error: null));
            }
            catch (Exception ex)
            {
                result.Add(new Entry(name, arity, Text: "", Error: ex.Message));
            }
        }
        return result;
    }

    /// <summary>Renders a single predicate's bytecode region as a header line
    /// plus one line per decoded instruction (<c>offset: mnemonic [operands]</c>).</summary>
    public static string Format(string label, byte[] bytecode)
    {
        ArgumentNullException.ThrowIfNull(bytecode);
        var sb = new StringBuilder();
        sb.AppendLine($"=== {label}  ({bytecode.Length} bytes) ===");
        foreach (DisassembledInstruction ins in Disassembler.Iterate(bytecode, 0, bytecode.Length))
        {
            // The Meta mnemonic already names its sub-opcode (e.g.
            // "meta dbg_info"), so MetaSubOpcode is not re-appended here.
            sb.Append($"  {ins.Address,4}: {ins.Mnemonic}");
            if (ins.Operands is { Length: > 0 })
                sb.Append("  [" + string.Join(", ", ins.Operands) + "]");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static (string Name, int Arity) HeadIndicator(Clause clause)
    {
        // A Rule is `:-/2` with the head at Args[0]; a Fact is the term itself.
        Term head = clause.Kind == ClauseKind.Rule && clause.Term is CompoundTerm r
            ? r.Args[0]
            : clause.Term;
        return head switch
        {
            CompoundTerm c => (c.Functor, c.Args.Length),
            AtomTerm a => (a.Name, 0),
            _ => (head.ToString() ?? "?", 0),
        };
    }
}
