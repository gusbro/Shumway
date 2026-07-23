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
        public IReadOnlySet<int> DynamicFunctors { get; }

        public Context(string moduleName, HashSet<int> localFunctors)
            : this(moduleName, localFunctors, new HashSet<int>())
        {
        }

        public Context(string moduleName, HashSet<int> localFunctors, IReadOnlySet<int> dynamicFunctors)
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
                : new CompoundTerm(":-", new[] { newHead, newBody }) { Position = term.Position };
        }
        // Directive (:- /1 Body) — leave alone; directives are consumed
        // before this pass runs and never end up as compiled predicates.
        if (term is CompoundTerm directive && directive.Functor == ":-" && directive.Args.Length == 1)
            return term;

        // Fact: the term IS the head. Mangle if local.
        return RewriteHead(term, ctx);
    }

    // ADR-035 — every rebuilt term carries the source position of the term it
    // replaces. Mangling changes a name, not a place: without this the debug
    // stop sites (and any future position-driven diagnostics) would see 0:0 for
    // every goal in a module, which is to say for every goal in every program.
    private static Term RewriteHead(Term head, Context ctx) => head switch
    {
        AtomTerm a => MangleIfLocal(a.Name, 0, ctx,
            () => new AtomTerm(MangledName(a.Name, ctx)) { Position = a.Position }) ?? a,
        CompoundTerm c => MangleIfLocal(c.Functor, c.Args.Length, ctx,
            () => new CompoundTerm(MangledName(c.Functor, ctx), c.Args)
                { Position = c.Position }) ?? c,
        _ => head,
    };

    private static Term RewriteGoal(Term goal, Context ctx)
    {
        // A variable in a goal position (a clause body, a control-flow sub-goal)
        // is a runtime meta-call. Wrap it call('$mqual'(Module, Var)) so the
        // live-engine dispatch resolves its bound goal against THIS module's
        // locals first — the same module-relative resolution a direct meta-arg
        // (findall/call) gets. The explicit call/1 keeps it compiling as a
        // meta-call (a bare variable body goal is call(Var) by ISO anyway).
        if (goal is VarTerm)
            return new CompoundTerm("call", new[]
            {
                (Term)new CompoundTerm(MqualFunctor,
                    new[] { (Term)new AtomTerm(ctx.ModuleName), goal }),
            });

        if (goal is AtomTerm a)
        {
            if (IsControlFlow(a.Name, 0)) return goal;
            // Local predicates shadow builtins (ADR-008). Check local first.
            // Dynamic predicates live in a flat global namespace (their
            // clauses can land from any module via assertz) so call sites
            // skip the mangle just like the head does in MangleIfLocal.
            if (IsLocal(a.Name, 0, ctx) && !IsDynamic(a.Name, 0, ctx))
                return new AtomTerm(MangledName(a.Name, ctx)) { Position = a.Position };
            if (IsBuiltin(a.Name, 0)) return goal;
            return goal;
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
                return newArgs is null
                    ? goal
                    : new CompoundTerm(c.Functor, newArgs) { Position = c.Position };
            }
            // Meta-predicate with a VARIABLE goal argument (a callable goal was
            // already inlined + mangled by MetaTransform): tag the variable with
            // the compile-time module so a runtime meta-call (findall/call/…)
            // resolves the bare goal relative to THIS module's locals. The tag
            // travels with the goal term into the live-engine dispatch, where
            // DispatchCall / MetaCallInEngine unwrap it. Public / builtin goals
            // fall through the tag transparently (module$name lookup misses, then
            // the bare name resolves). See the module-local-meta-call fix.
            if (MetaGoalPositions(c.Functor, c.Args.Length) is int[] positions)
            {
                Term[]? tagged = null;
                foreach (int pos in positions)
                {
                    if (c.Args[pos] is not VarTerm) continue;
                    tagged ??= (Term[])c.Args.Clone();
                    tagged[pos] = new CompoundTerm(MqualFunctor,
                        new[] { (Term)new AtomTerm(ctx.ModuleName), c.Args[pos] })
                        { Position = c.Position };
                }
                if (tagged is not null)
                    c = new CompoundTerm(c.Functor, tagged) { Position = c.Position };
            }

            if (IsLocal(c.Functor, c.Args.Length, ctx) && !IsDynamic(c.Functor, c.Args.Length, ctx))
                return new CompoundTerm(MangledName(c.Functor, ctx), c.Args)
                    { Position = c.Position };
            if (IsBuiltin(c.Functor, c.Args.Length)) return c;
            return c;
        }

        return goal;
    }

    /// <summary>the module-qualifier wrapper for a runtime-variable meta-goal.
    /// <c>'$mqual'(Module, Goal)</c> — unwrapped at the meta-dispatch sites so
    /// <c>Goal</c>'s bare functor resolves against <c>Module</c>'s locals first.</summary>
    public const string MqualFunctor = "$mqual";

    /// <summary>The goal-carrying argument positions of the control
    /// meta-predicates (0-based). A variable in one of these positions is tagged
    /// with the clause's module. Callable goals in these positions are already
    /// inlined + mangled by MetaTransform before this pass, so only variables are
    /// seen here. Higher-order library predicates (maplist/foldl/…) that pass a
    /// callable closure by name are a separate case — deferred.</summary>
    private static int[]? MetaGoalPositions(string functor, int arity) => (functor, arity) switch
    {
        ("findall", 3) => Pos1,
        ("findall", 4) => Pos1,
        ("bagof", 3) => Pos1,
        ("setof", 3) => Pos1,
        ("aggregate_all", 3) => Pos1,
        ("forall", 2) => Pos01,
        ("catch", 3) => Pos02,
        ("once", 1) => Pos0,
        ("ignore", 1) => Pos0,
        ("\\+", 1) => Pos0,
        ("not", 1) => Pos0,
        ("call", 1) => Pos0,
        ("call", 2) => Pos0,
        ("call", 3) => Pos0,
        ("call", 4) => Pos0,
        ("call", 5) => Pos0,
        ("call", 6) => Pos0,
        ("call", 7) => Pos0,
        ("call", 8) => Pos0,
        _ => null,
    };

    private static readonly int[] Pos0 = { 0 };
    private static readonly int[] Pos1 = { 1 };
    private static readonly int[] Pos01 = { 0, 1 };
    private static readonly int[] Pos02 = { 0, 2 };

    private static bool IsDynamic(string name, int arity, Context ctx)
    {
        int functorId = FunctorTable.Intern(
            AtomTable.Intern(name, permanent: true).Id, arity);
        return ctx.DynamicFunctors.Contains(functorId);
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

    /// <summary>Per ADR-008 the resolution order at call sites is "local
    /// predicates of the module ▸ builtins ▸ publics of other modules". A
    /// user-defined local shadows a same-named builtin within its module.
    /// The check is folded into <see cref="RewriteGoal"/> so the goal
    /// walker can short-circuit before falling back to the builtin path.</summary>
    private static bool IsLocal(string name, int arity, Context ctx)
    {
        int functorId = FunctorTable.Intern(
            AtomTable.Intern(name, permanent: true).Id, arity);
        return ctx.LocalFunctors.Contains(functorId);
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
