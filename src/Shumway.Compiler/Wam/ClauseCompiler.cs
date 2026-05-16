using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Compiler.Wam;

/// <summary>
/// Compiles a single Prolog clause to WAM bytecode. Chunk 8a's scope is the
/// minimum useful slice that closes the parse → compile → run loop end-to-end:
///
/// <list type="bullet">
/// <item>Facts (and rules whose body is just <c>true</c> — i.e. the empty
///   body). Non-trivial bodies will land in 8c.</item>
/// <item>Head arguments restricted to atoms, integers, named variables, and
///   the anonymous variable. Compound and list patterns come in 8b; floats and
///   strings later when bigger numeric / string handling lands.</item>
/// </list>
///
/// <para>Head-compilation rules:</para>
/// <list type="bullet">
/// <item>An <see cref="AtomTerm"/> at position <c>i</c> emits
///   <c>get_atom A, X[i]</c>.</item>
/// <item>An <see cref="IntTerm"/> at position <c>i</c> emits
///   <c>get_integer N, X[i]</c>.</item>
/// <item>A first-occurrence <see cref="VarTerm"/> at position <c>i</c> claims
///   <c>X[i]</c> as the variable's home and emits NO opcode — the value is
///   already in the right register thanks to the WAM calling convention.</item>
/// <item>A subsequent occurrence emits <c>get_value_x X[home], X[i]</c>.</item>
/// <item>The anonymous variable <c>_</c> emits no opcode — it imposes no
///   constraint on the caller-supplied value.</item>
/// </list>
///
/// <para>After the head, the compiler emits <c>proceed</c>. For 8a that's the
/// only body opcode supported.</para>
/// </summary>
public sealed class ClauseCompiler
{
    /// <summary>Compiles a <see cref="Clause"/> to a <see cref="CompiledClause"/>.</summary>
    public CompiledClause Compile(Clause clause)
    {
        ArgumentNullException.ThrowIfNull(clause);

        switch (clause.Kind)
        {
            case ClauseKind.Fact:
                return CompileFact(clause.Term);
            case ClauseKind.Rule:
                return CompileRule((CompoundTerm)clause.Term);
            case ClauseKind.Directive:
                throw new NotSupportedException(
                    "Directives are handled by ClauseReader, not by the clause compiler.");
            case ClauseKind.DcgRule:
                throw new NotSupportedException(
                    "DCG rules require a separate translation pass — not yet implemented.");
            default:
                throw new InvalidOperationException($"Unknown clause kind: {clause.Kind}.");
        }
    }

    private CompiledClause CompileFact(Term headTerm)
    {
        (string name, Term[] args) = DecomposeHead(headTerm);

        var emitter = new BytecodeEmitter();
        var vars = new VariableMap(args.Length);

        for (int i = 0; i < args.Length; i++)
            CompileHeadArg(emitter, vars, args[i], i);

        emitter.EmitProceed();

        int functorId = InternFunctor(name, args.Length);
        return new CompiledClause(emitter.ToBytes(), functorId, args.Length, vars.RegisterCount);
    }

    private CompiledClause CompileRule(CompoundTerm ruleTerm)
    {
        // The clause is :-/2 with [head, body].
        Term headTerm = ruleTerm.Args[0];
        Term bodyTerm = ruleTerm.Args[1];

        // 8a only handles the trivial body `true`.
        if (bodyTerm is not AtomTerm { Name: "true" })
            throw new NotSupportedException(
                "Chunk 8a only handles facts and rules whose body is 'true'. "
                + "Bodies with goals land with chunk 8c.");

        return CompileFact(headTerm);
    }

    private void CompileHeadArg(BytecodeEmitter emitter, VariableMap vars, Term arg, int argSlot)
    {
        switch (arg)
        {
            case AtomTerm a:
                emitter.EmitGetAtom(InternAtom(a.Name), argSlot);
                break;

            case IntTerm n:
                if (n.Value < int.MinValue || n.Value > int.MaxValue)
                    throw new NotSupportedException(
                        $"Integer literal {n.Value} doesn't fit in a 32-bit operand. "
                        + "BigInt support lands later.");
                emitter.EmitGetInteger((int)n.Value, argSlot);
                break;

            case VarTerm v:
                if (v.Name == "_")
                {
                    // Anonymous variable — no constraint, no opcode.
                    return;
                }
                if (vars.IsNewName(v.Name))
                {
                    // First occurrence: claim X[argSlot] as this variable's home.
                    // No opcode needed — the value is already there.
                    vars.Bind(v.Name, argSlot);
                }
                else
                {
                    // Subsequent occurrence: unify with the variable's stored slot.
                    emitter.EmitGetValueX(vars.GetSlot(v.Name), argSlot);
                }
                break;

            case FloatTerm:
                throw new NotSupportedException(
                    "Float head arguments are not in scope for chunk 8a.");

            case StringTerm:
                throw new NotSupportedException(
                    "String head arguments are not in scope for chunk 8a.");

            case CompoundTerm:
                throw new NotSupportedException(
                    "Compound head arguments require get_structure / get_list, which "
                    + "land with chunk 8b.");

            default:
                throw new NotSupportedException(
                    $"Head argument type {arg.GetType().Name} is not supported.");
        }
    }

    /// <summary>Splits a head term into its functor name and argument list. An
    /// <see cref="AtomTerm"/> is treated as a zero-arity predicate; a
    /// <see cref="CompoundTerm"/> exposes its functor and args directly.</summary>
    private static (string name, Term[] args) DecomposeHead(Term head)
    {
        return head switch
        {
            AtomTerm a => (a.Name, Array.Empty<Term>()),
            CompoundTerm c => (c.Functor, c.Args),
            _ => throw new NotSupportedException(
                $"Clause head must be an atom or compound, got {head.GetType().Name}."),
        };
    }

    private static int InternAtom(string name) =>
        AtomTable.Intern(name, permanent: true).Id;

    private static int InternFunctor(string name, int arity) =>
        FunctorTable.Intern(InternAtom(name), arity);
}
