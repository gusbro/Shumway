using System.Collections.Generic;
using System.Linq;
using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Compiler.NativeC;

namespace Shumway.Embedding;

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
    private static int _counter;

    public static List<Clause> Apply(IReadOnlyList<Clause> clauses, List<CDecl> cDecls,
        Func<string, System.Reflection.MethodInfo?> resolveInterop)
    {
        var result = new List<Clause>(clauses.Count);
        foreach (var clause in clauses)
        {
            if (!Mentions(clause.Term, "$native_goal")) { result.Add(clause); continue; }
            result.Add(Clause.From(Rewrite(clause.Term, clause.Term, cDecls, resolveInterop)));
        }
        return result;
    }

    /// <summary>Returns true if any clause carries a native block — so the caller
    /// only pays the C-symbol-table parse when there is work to do.</summary>
    public static bool HasNativeBlock(IEnumerable<Clause> clauses)
        => clauses.Any(c => Mentions(c.Term, "$native_goal"));

    // Rewrite occurrences of $native_goal(StringTerm) in `t`. `clauseTerm` is the
    // WHOLE clause (kept intact for guard-based inference).
    private static Term Rewrite(Term t, Term clauseTerm, List<CDecl> cDecls,
        Func<string, System.Reflection.MethodInfo?> resolve)
    {
        if (t is not CompoundTerm c) return t;
        if (c.Functor == "$native_goal" && c.Args.Length == 1 && c.Args[0] is StringTerm s)
            return TransformBlock(s.Content, clauseTerm, cDecls, resolve);
        var args = new Term[c.Args.Length];
        for (int i = 0; i < c.Args.Length; i++)
            args[i] = Rewrite(c.Args[i], clauseTerm, cDecls, resolve);
        return new CompoundTerm(c.Functor, args) { Position = c.Position };
    }

    private static Term TransformBlock(string text, Term clauseTerm, List<CDecl> cDecls,
        Func<string, System.Reflection.MethodInfo?> resolve)
    {
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
        // silently. Fail at consult instead.
        foreach (var fn in CollectCallNames(stmts))
            if (resolve(fn) is null)
                throw Error($"calls '{fn}', which is not a public static method of the interop "
                    + "class. Register the class with PrologEngine.UseNativeInterop(typeof(...)) "
                    + "(or name it Shumway.Native.Interop for auto-discovery) and implement the method.");

        string name = "$nb$" + System.Threading.Interlocked.Increment(ref _counter);
        BuiltinsRegistry.Register(name, info.PrologVars.Count,
            NativeBlockRunner.Build(info.PrologVars, stmts));
        var callArgs = info.PrologVars.Select(v => (Term)new VarTerm(v.Name)).ToArray();
        return new CompoundTerm(name, callArgs);
    }

    private static System.InvalidOperationException Error(string detail)
        => new($"embedded native block: {detail}");

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
