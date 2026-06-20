using System.Collections.Generic;
using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Compiler.NativeC;

namespace Shumway.Embedding;

/// <summary>ADR-022 — a faulty embedded native block, carrying the source position
/// (the block's line) so the compiler / consult error points at it instead of
/// reporting line 0. The message names the predicate the block is in.</summary>
internal sealed class NativeBlockCompileException : System.InvalidOperationException
{
    public int Line { get; }
    public int Column { get; }
    public NativeBlockCompileException(string message, int line, int column) : base(message)
    {
        Line = line;
        Column = column;
    }
}

/// <summary>ADR-022 — the consult-time wiring. Rewrites each captured native
/// block <c>'$native_goal'(RawText)</c> body goal into a call to a per-block
/// synthesized foreign builtin (the block's Prolog variables as arguments),
/// using the module's <c>:- c</c> symbol table (the captured
/// <c>'$native_decls'</c> regions) for prototypes and globals.
///
/// <para>Fails LOUDLY: a block that cannot be parsed (unsupported native syntax —
/// the deferred term/reftype tier, C control flow), whose variables' type/mode
/// cannot be inferred, or that calls a function the configured interop class does
/// not provide, raises a consult error. It is never silently left inert — a
/// no-op'd block would make the program misbehave without the author noticing.
/// </para></summary>
internal static class NativeTransform
{
    /// <summary>Rewrites every captured <c>'$native_goal'(Text)</c> to a portable
    /// <c>'$native_run'('$nb$…', V1..Vk)</c> dispatch and hands the analysed block
    /// (name, variables, statements) to <paramref name="registerBlock"/> — which
    /// stores it where it will be found at run time (the live engine's block table
    /// in-process; a serialized bundle table for separate compilation). Block names
    /// are <paramref name="namePrefix"/> + a per-call index, so the same source
    /// compiled twice produces the same names (the baked bytecode references the
    /// block by name and must match the table populated at load).</summary>
    public static List<Clause> Apply(IReadOnlyList<Clause> clauses, List<CDecl> cDecls,
        Func<string, System.Reflection.MethodInfo?>? resolveInterop,
        Action<string, NativeVar[], CStmt[], string> registerBlock,
        string namePrefix)
    {
        int index = 0;
        var result = new List<Clause>(clauses.Count);
        foreach (var clause in clauses)
        {
            if (!Mentions(clause.Term, "$native_goal")) { result.Add(clause); continue; }
            result.Add(Clause.From(Rewrite(clause.Term, clause.Term, PredLabel(clause.Term),
                clause.Position, cDecls, resolveInterop, registerBlock, namePrefix, ref index)));
        }
        return result;
    }

    /// <summary>The predicate indicator (<c>name/arity</c>) of a clause's head, for
    /// error messages that point at the predicate carrying a faulty native block.</summary>
    private static string PredLabel(Term clauseTerm)
    {
        Term head = clauseTerm is CompoundTerm { Functor: ":-", Args.Length: 2 } rule
            ? rule.Args[0] : clauseTerm;
        return head switch
        {
            CompoundTerm h => $"{h.Functor}/{h.Args.Length}",
            AtomTerm a => $"{a.Name}/0",
            _ => "?",
        };
    }

    /// <summary>Returns true if any clause carries a native block — so the caller
    /// only pays the C-symbol-table parse when there is work to do.</summary>
    public static bool HasNativeBlock(IEnumerable<Clause> clauses)
        => clauses.Any(c => Mentions(c.Term, "$native_goal"));

    // Rewrite occurrences of $native_goal(StringTerm) in `t`. `clauseTerm` is the
    // WHOLE clause (kept intact for guard-based inference); `predLabel` / `clausePos`
    // locate it for error messages.
    private static Term Rewrite(Term t, Term clauseTerm, string predLabel,
        Shumway.Compiler.Lexer.SourcePosition clausePos, List<CDecl> cDecls,
        Func<string, System.Reflection.MethodInfo?>? resolve,
        Action<string, NativeVar[], CStmt[], string> registerBlock, string namePrefix, ref int index)
    {
        if (t is not CompoundTerm c) return t;
        if (c.Functor == "$native_goal" && c.Args.Length == 1 && c.Args[0] is StringTerm s)
        {
            // Prefer the block's own captured position; fall back to the clause head.
            var pos = c.Position.Line > 0 ? c.Position : clausePos;
            return TransformBlock(s.Content, clauseTerm, predLabel, pos, cDecls, resolve,
                registerBlock, namePrefix + (index++));
        }
        var args = new Term[c.Args.Length];
        for (int i = 0; i < c.Args.Length; i++)
            args[i] = Rewrite(c.Args[i], clauseTerm, predLabel, clausePos, cDecls, resolve,
                registerBlock, namePrefix, ref index);
        return new CompoundTerm(c.Functor, args) { Position = c.Position };
    }

    private static Term TransformBlock(string text, Term clauseTerm, string predLabel,
        Shumway.Compiler.Lexer.SourcePosition pos, List<CDecl> cDecls,
        Func<string, System.Reflection.MethodInfo?>? resolve,
        Action<string, NativeVar[], CStmt[], string> registerBlock, string name)
    {
        NativeBlockCompileException Error(string detail) => new(
            $"embedded native block in {predLabel} (line {pos.Line}): {detail}", pos.Line, pos.Column);

        List<CStmt> stmts;
        try
        {
            stmts = CParser.ParseStatements(text);
        }
        catch (CParseException ex)
        {
            throw Error($"unsupported native syntax: {ex.Message} (offset {ex.Offset}). "
                + "C control flow and the term/reftype tier are not compilable yet.");
        }

        var info = NativeInference.Analyze(clauseTerm, stmts, cDecls);
        if (info.Diagnostics.Count > 0)
            throw Error(info.Diagnostics[0]);

        // Every interop function the block calls must be a public static method of
        // the configured interop class — otherwise the program would misbehave
        // silently. Fail at consult instead. (Compile-time / bundle path passes
        // resolve == null: the interop class is unknown then, so resolution is
        // enforced at run time when the block executes, and at link by
        // --foreign-dll. Either way an unresolved call is a hard error, never a
        // silent no-op.)
        if (resolve is not null)
            foreach (var fn in CollectCallNames(stmts))
                if (resolve(fn) is null)
                    throw Error($"calls '{fn}', which is not a public static method of the interop "
                        + "class. Register the class with PrologEngine.UseNativeInterop(typeof(...)) "
                        + "(or name it Shumway.Native.Interop for auto-discovery) and implement the method.");

        // Hand the analysed block to the sink (engine table or bundle table), and
        // emit the portable dispatch `'$native_run'('$nb$…', V1..Vk)`.
        var vars = info.PrologVars.ToArray();
        registerBlock(name, vars, stmts.ToArray(), text);
        var callArgs = new Term[vars.Length + 1];
        callArgs[0] = new AtomTerm(name);
        for (int i = 0; i < vars.Length; i++)
            callArgs[i + 1] = new VarTerm(vars[i].Name);
        return new CompoundTerm("$native_run", callArgs);
    }


    /// <summary>The names of the non-intrinsic C functions a block calls — the
    /// ones that must resolve to Shumway.Native.Interop methods (MakeCString /
    /// MakePrologString are intrinsics, lowered inline, not interop methods).</summary>
    private static HashSet<string> CollectCallNames(IReadOnlyList<CStmt> stmts)
    {
        var names = new HashSet<string>();
        foreach (var st in stmts)
            switch (st)
            {
                case CBindStmt b: WalkCalls(b.Value, names); break;
                case CAssignStmt a: WalkCalls(a.Target, names); WalkCalls(a.Value, names); break;
                case CCallStmt c: WalkCalls(c.Call, names); break;
            }
        return names;
    }

    private static void WalkCalls(CExpr e, HashSet<string> names)
    {
        switch (e)
        {
            case CCallExpr c:
                if (c.Name is not ("MakeCString" or "MakePrologString" or "MakePrologStringEx"))
                    names.Add(c.Name);
                foreach (var a in c.Args) WalkCalls(a, names);
                break;
            case CAddrOfExpr a: WalkCalls(a.Operand, names); break;
            case CDerefExpr d: WalkCalls(d.Operand, names); break;
            case CBinaryExpr b: WalkCalls(b.Left, names); WalkCalls(b.Right, names); break;
        }
    }

    private static bool Mentions(Term t, string functor)
    {
        if (t is not CompoundTerm c) return false;
        if (c.Functor == functor) return true;
        foreach (var a in c.Args) if (Mentions(a, functor)) return true;
        return false;
    }
}
