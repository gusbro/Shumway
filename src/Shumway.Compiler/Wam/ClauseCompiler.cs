using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Compiler.Wam;

/// <summary>
/// Compiles a single Prolog clause to WAM bytecode. Current scope (8a + 8b):
///
/// <list type="bullet">
/// <item>Facts and rules whose body is the trivial atom <c>true</c>. Non-trivial
///   bodies land in 8c.</item>
/// <item>Head arguments may be atoms, integers, named variables, the anonymous
///   variable, or compound terms (including lists, which the parser desugars
///   into nested <c>.</c>/2 compounds). Float and string head arguments are
///   still deferred.</item>
/// </list>
///
/// <para>Head-compilation proceeds in two passes:</para>
///
/// <para><b>Pass 1.</b> Top-level head arguments at positions <c>X[0..arity-1]</c>:</para>
/// <list type="bullet">
/// <item><see cref="AtomTerm"/> → <c>get_atom A, X[i]</c>.</item>
/// <item><see cref="IntTerm"/> → <c>get_integer N, X[i]</c>.</item>
/// <item><see cref="VarTerm"/> first occurrence — claim <c>X[i]</c> as the
///   variable's home, emit no opcode.</item>
/// <item><see cref="VarTerm"/> subsequent — <c>get_value_x X[home], X[i]</c>.</item>
/// <item>Anonymous <c>_</c> — emit no opcode (no constraint).</item>
/// <item><see cref="CompoundTerm"/> — defer onto a worklist; pass 2 will expand
///   it. No opcode is emitted yet because <c>X[i]</c> already holds the term.</item>
/// </list>
///
/// <para><b>Pass 2.</b> Drain the worklist FIFO. Each entry is a pair
/// <c>(slot, compound)</c> describing a compound that lives at <c>X[slot]</c>:</para>
/// <list type="bullet">
/// <item>If the compound's functor is <c>.</c>/2 emit <c>get_list X[slot]</c>;
///   otherwise emit <c>get_structure F/N, X[slot]</c>.</item>
/// <item>For each sub-argument, emit a <c>unify_*</c> opcode mirroring the
///   pass-1 dispatch above. Nested compounds get a fresh anonymous slot, an
///   <c>unify_variable_x</c> capture, and a worklist entry to expand them
///   later. This keeps the unify cursor aligned with the current compound
///   while still recursing depth-by-depth.</item>
/// </list>
///
/// <para>After both passes: <c>proceed</c> (since 8b still has empty bodies).</para>
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
        var pending = new Queue<(int Slot, CompoundTerm Compound)>();

        // Pass 1: emit head opcodes for top-level args; defer compound args.
        for (int i = 0; i < args.Length; i++)
            CompileHeadArg(emitter, vars, args[i], i, pending);

        // Pass 2: BFS-expand deferred compounds. Each iteration consumes one
        // (slot, compound) pair, emits its open instruction, and emits unify_*
        // for each sub-arg — re-enqueuing for any sub-arg that's itself a
        // compound.
        while (pending.Count > 0)
        {
            var (slot, comp) = pending.Dequeue();
            CompileExpandedCompound(emitter, vars, slot, comp, pending);
        }

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

    private void CompileHeadArg(
        BytecodeEmitter emitter,
        VariableMap vars,
        Term arg,
        int argSlot,
        Queue<(int Slot, CompoundTerm Compound)> pending)
    {
        switch (arg)
        {
            case AtomTerm a:
                emitter.EmitGetAtom(InternAtom(a.Name), argSlot);
                break;

            case IntTerm n:
                CheckInt32(n);
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

            case CompoundTerm c:
                // Defer expansion to pass 2. X[argSlot] already holds the term;
                // the get_structure / get_list opcode is emitted when the
                // worklist drains.
                pending.Enqueue((argSlot, c));
                break;

            case FloatTerm:
                throw new NotSupportedException(
                    "Float head arguments are not yet supported.");

            case StringTerm:
                throw new NotSupportedException(
                    "String head arguments are not yet supported.");

            default:
                throw new NotSupportedException(
                    $"Head argument type {arg.GetType().Name} is not supported.");
        }
    }

    private void CompileExpandedCompound(
        BytecodeEmitter emitter,
        VariableMap vars,
        int slot,
        CompoundTerm comp,
        Queue<(int Slot, CompoundTerm Compound)> pending)
    {
        bool isList = comp.Functor == "." && comp.Args.Length == 2;
        if (isList)
            emitter.EmitGetList(slot);
        else
            emitter.EmitGetStructure(InternFunctor(comp.Functor, comp.Args.Length), slot);

        foreach (Term subArg in comp.Args)
            CompileUnifyArg(emitter, vars, subArg, pending);
    }

    private void CompileUnifyArg(
        BytecodeEmitter emitter,
        VariableMap vars,
        Term arg,
        Queue<(int Slot, CompoundTerm Compound)> pending)
    {
        switch (arg)
        {
            case AtomTerm a:
                // [] gets the compact unify_nil; other atoms use unify_atom.
                if (a.Name == "[]")
                    emitter.EmitUnifyNil();
                else
                    emitter.EmitUnifyAtom(InternAtom(a.Name));
                break;

            case IntTerm n:
                CheckInt32(n);
                emitter.EmitUnifyInteger((int)n.Value);
                break;

            case VarTerm v:
                if (v.Name == "_")
                {
                    emitter.EmitUnifyVoid(1);
                    return;
                }
                if (vars.IsNewName(v.Name))
                {
                    int s = vars.AllocateFresh(v.Name);
                    emitter.EmitUnifyVariableX(s);
                }
                else
                {
                    emitter.EmitUnifyValueX(vars.GetSlot(v.Name));
                }
                break;

            case CompoundTerm c:
                // Capture the nested compound's heap reference into a fresh
                // anonymous slot and defer its expansion to the next worklist
                // iteration.
                int temp = vars.AllocateAnonymousSlot();
                emitter.EmitUnifyVariableX(temp);
                pending.Enqueue((temp, c));
                break;

            case FloatTerm:
                throw new NotSupportedException(
                    "Float arguments inside compound head terms are not yet supported.");

            case StringTerm:
                throw new NotSupportedException(
                    "String arguments inside compound head terms are not yet supported.");

            default:
                throw new NotSupportedException(
                    $"Unsupported sub-argument type {arg.GetType().Name} inside a compound.");
        }
    }

    private static void CheckInt32(IntTerm n)
    {
        if (n.Value < int.MinValue || n.Value > int.MaxValue)
            throw new NotSupportedException(
                $"Integer literal {n.Value} doesn't fit in a 32-bit operand. "
                + "BigInt support lands later.");
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
