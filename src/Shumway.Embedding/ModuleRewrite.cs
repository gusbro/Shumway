using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Module-aware functor rewriting: a clause's head is one of the module's
/// own predicates, so its functor is mangled to a synthetic
/// <c>moduleName$name</c> form whenever the functor is local. Each body goal
/// gets the same treatment so the bytecode's <c>call</c> / <c>execute</c>
/// instructions reference the same mangled id the head landed on.
///
/// <para>Mangling lets two modules use the same private predicate name
/// without colliding in the global functor table. Public functors are kept
/// under their bare name so cross-module calls and meta-calls reach them
/// unchanged. Builtins and the AST-level control-flow operators
/// (<c>,/2</c>, <c>;/2</c>, <c>-&gt;/2</c>, <c>!/0</c>) are never mangled —
/// they're either reserved or live in a global namespace by definition.</para>
/// </summary>
public static class ModuleRewrite
{
    public sealed class Context
    {
        public string ModuleName { get; }
        public HashSet<int> LocalFunctors { get; }
        public HashSet<int> DynamicFunctors { get; }

        public Context(string moduleName, HashSet<int> localFunctors)
            : this(moduleName, localFunctors, new HashSet<int>())
        {
        }

        public Context(string moduleName, HashSet<int> localFunctors, HashSet<int> dynamicFunctors)
        {
            ModuleName = moduleName;
            LocalFunctors = localFunctors;
            DynamicFunctors = dynamicFunctors;
        }
    }

    /// <summary>Returns a copy of <paramref name="clause"/> with every local
    /// functor (head or body) mangled per <paramref name="ctx"/>. Clauses
    /// that introduce no callable goals (directives) and clauses whose head
    /// functor isn't local pass through unchanged at that level — the body
    /// is still walked recursively.</summary>
    public static Clause Rewrite(Clause clause, Context ctx)
    {
        Term newTerm = RewriteClauseTerm(clause.Term, ctx);
        return ReferenceEquals(newTerm, clause.Term)
            ? clause
            : new Clause(clause.Kind, newTerm, clause.Position);
    }

    private static Term RewriteClauseTerm(Term term, Context ctx)
    {
        // Rule: (:- /2 Head Body). Mangle both halves independently.
        if (term is CompoundTerm rule && rule.Functor == ":-" && rule.Args.Length == 2)
        {
            Term newHead = RewriteHead(rule.Args[0], ctx);
            Term newBody = RewriteGoal(rule.Args[1], ctx);
            return ReferenceEquals(newHead, rule.Args[0]) && ReferenceEquals(newBody, rule.Args[1])
                ? term
                : new CompoundTerm(":-", new[] { newHead, newBody });
        }
        // Directive (:- /1 Body) — leave alone; directives are consumed
        // before this pass runs and never end up as compiled predicates.
        if (term is CompoundTerm directive && directive.Functor == ":-" && directive.Args.Length == 1)
            return term;

        // Fact: the term IS the head. Mangle if local.
        return RewriteHead(term, ctx);
    }

    private static Term RewriteHead(Term head, Context ctx) => head switch
    {
        AtomTerm a => MangleIfLocal(a.Name, 0, ctx, () => new AtomTerm(MangledName(a.Name, ctx))) ?? a,
        CompoundTerm c => MangleIfLocal(c.Functor, c.Args.Length, ctx,
            () => new CompoundTerm(MangledName(c.Functor, ctx), c.Args)) ?? c,
        _ => head,
    };

    private static Term RewriteGoal(Term goal, Context ctx)
    {
        if (goal is AtomTerm a)
        {
            if (IsControlFlow(a.Name, 0)) return goal;
            if (IsBuiltin(a.Name, 0)) return goal;
            return MangleIfLocal(a.Name, 0, ctx, () => new AtomTerm(MangledName(a.Name, ctx))) ?? goal;
        }

        if (goal is CompoundTerm c)
        {
            // Control-flow constructors are syntactic, not callable predicates;
            // recurse into their sub-goals but never mangle the constructor
            // itself.
            if (IsControlFlow(c.Functor, c.Args.Length))
            {
                Term[]? newArgs = null;
                for (int i = 0; i < c.Args.Length; i++)
                {
                    Term original = c.Args[i];
                    Term rewritten = RewriteGoal(original, ctx);
                    if (!ReferenceEquals(rewritten, original))
                    {
                        newArgs ??= (Term[])c.Args.Clone();
                        newArgs[i] = rewritten;
                    }
                }
                return newArgs is null ? goal : new CompoundTerm(c.Functor, newArgs);
            }
            if (IsBuiltin(c.Functor, c.Args.Length)) return goal;
            return MangleIfLocal(c.Functor, c.Args.Length, ctx,
                () => new CompoundTerm(MangledName(c.Functor, ctx), c.Args)) ?? goal;
        }

        return goal;
    }

    private static Term? MangleIfLocal(string name, int arity, Context ctx, Func<Term> build)
    {
        int functorId = FunctorTable.Intern(
            AtomTable.Intern(name, permanent: true).Id, arity);
        // Dynamic predicates live in a global namespace (their clauses get
        // appended at runtime via assertz from any module), so we never mangle
        // their callers.
        if (ctx.DynamicFunctors.Contains(functorId)) return null;
        return ctx.LocalFunctors.Contains(functorId) ? build() : null;
    }

    private static string MangledName(string name, Context ctx) => ctx.ModuleName + "$" + name;

    private static bool IsControlFlow(string functor, int arity) => (functor, arity) switch
    {
        (",", 2) => true,
        (";", 2) => true,
        ("->", 2) => true,
        ("*->", 2) => true,
        ("!", 0) => true,
        _ => false,
    };

    private static bool IsBuiltin(string functor, int arity)
    {
        int functorId = FunctorTable.Intern(
            AtomTable.Intern(functor, permanent: true).Id, arity);
        return BuiltinsRegistry.TryGetByFunctor(functorId, out _);
    }
}
