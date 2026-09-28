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
        public ISet<int> DynamicFunctors { get; }

        /// <summary>ADR-038 — this module's import table: bare functor id →
        /// the export-qualified source module that provides it. A call that
        /// misses <see cref="LocalFunctors"/> resolves through here to
        /// <c>Source$name</c> before the bare-global namespace. Empty for a
        /// module that imports nothing.</summary>
        public IReadOnlyDictionary<int, string> Imports { get; }

        /// <summary>Resolves a statically written <c>Module:Goal</c> body goal
        /// at compile time: (module, name, arity) → the final functor name
        /// (mangled <c>Module$name</c>, an import's <c>Source$name</c>, or the
        /// bare name for the global/builtin fallback), or <c>null</c> to keep
        /// the runtime <c>':'/2</c> dispatch (module not loaded yet). Must
        /// mirror the runtime PrepareMqualGoal chain exactly. When unset,
        /// every qualified goal stays on the runtime path.</summary>
        public Func<string, string, int, string?>? QualifiedStaticResolver { get; init; }

        /// <summary>The distinct qualified resolutions this rewrite performed:
        /// (module, name, arity) → resolved functor name (or null for
        /// left-on-runtime). The caller's transform cache revalidates each
        /// against the live resolver instead of invalidating wholesale —
        /// loading an unrelated module must not re-transform every
        /// qualified-goal user (the clpz load chain regression).</summary>
        public Dictionary<(string Mod, string Name, int Arity), string?>?
            QualifiedResolutions { get; private set; }

        /// <summary>ADR-056: the meta-argument positions of the predicate a
        /// call resolves to, by its bare functor id and, when it is a
        /// module's own predicate, that module's name; or null. A goal or
        /// closure in one of them is qualified with this module, so the
        /// callee's call/N resolves it here: the callee's own module cannot
        /// see this module's private predicates.</summary>
        public Func<int, string?, int[]?>? MetaArgSpec { get; init; }

        /// <summary>The meta-argument specs this rewrite looked up, so a
        /// cached transform can be revalidated when a later load declares a
        /// <c>meta_predicate</c>.</summary>
        public Dictionary<(int Fid, string? Local), int[]?>? MetaArgLookups { get; private set; }

        internal int[]? LookupMetaArgSpec(int fid, string? localModule)
        {
            MetaArgLookups ??= new Dictionary<(int, string?), int[]?>();
            if (MetaArgLookups.TryGetValue((fid, localModule), out int[]? memo)) return memo;
            int[]? spec = MetaArgSpec!(fid, localModule);
            MetaArgLookups[(fid, localModule)] = spec;
            return spec;
        }

        internal string? ResolveQualified(string mod, string name, int arity)
        {
            var key = (mod, name, arity);
            QualifiedResolutions ??= new Dictionary<(string, string, int), string?>();
            if (QualifiedResolutions.TryGetValue(key, out string? memo)) return memo;
            string? resolved = QualifiedStaticResolver!(mod, name, arity);
            QualifiedResolutions[key] = resolved;
            return resolved;
        }

        public Context(string moduleName, HashSet<int> localFunctors)
            : this(moduleName, localFunctors, new HashSet<int>())
        {
        }

        public Context(string moduleName, HashSet<int> localFunctors, ISet<int> dynamicFunctors)
            : this(moduleName, localFunctors, dynamicFunctors, EmptyImports)
        {
        }

        public Context(string moduleName, HashSet<int> localFunctors,
            ISet<int> dynamicFunctors, IReadOnlyDictionary<int, string> imports)
        {
            ModuleName = moduleName;
            LocalFunctors = localFunctors;
            DynamicFunctors = dynamicFunctors;
            Imports = imports;
        }

        private static readonly IReadOnlyDictionary<int, string> EmptyImports =
            new Dictionary<int, string>();
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

        // Fact: the term is the head. Mangle if local.
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

    // Control-flow constructors are syntactic, not callable predicates: the
    // walk descends through them and never mangles the constructor itself.
    // Iteratively (GoalTreeRewrite) -- a body is a run of ,/2 as long as the
    // program cares to write, and a frame per conjunct overflowed the stack.
    private static Term RewriteGoal(Term goal, Context ctx)
        => Shumway.Compiler.Parsing.GoalTreeRewrite.Apply(goal,
            c => IsControlFlow(c.Functor, c.Args.Length), g => RewritePlainGoal(g, ctx));

    private static Term RewritePlainGoal(Term goal, Context ctx)
    {
        // A variable in a goal position (a clause body, a control-flow sub-goal)
        // is a runtime meta-call. Wrap it call('$mqual'(Module, Var)) so the
        // live-engine dispatch resolves its bound goal against this module's
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
            // ADR-038 — imported name → Source$name (before the bare-global path).
            if (!IsDynamic(a.Name, 0, ctx) && TryResolveImport(a.Name, 0, ctx, out string aSrc))
                return new AtomTerm(ImportedName(aSrc, a.Name)) { Position = a.Position };
            if (IsBuiltin(a.Name, 0)) return goal;
            return goal;
        }

        if (goal is CompoundTerm c)
        {
            // A statically written Module:Goal resolves at compile time when
            // the resolver is available — the runtime ':'/2 path costs a full
            // meta-dispatch per call (the atts goal_expansion emits one per
            // get_atts/put_atts, ~112k per queens(12) solve). Nested
            // qualifications collapse innermost-first, mirroring
            // PrepareMqualGoal.
            if (c.Functor == ":" && c.Args.Length == 2
                && c.Args[0] is AtomTerm qualMod
                && ctx.QualifiedStaticResolver is { } qualResolve)
            {
                string mod = qualMod.Name;
                Term inner = c.Args[1];
                while (inner is CompoundTerm { Functor: ":", Args.Length: 2 } nested
                       && nested.Args[0] is AtomTerm innerMod)
                {
                    mod = innerMod.Name;
                    inner = nested.Args[1];
                }
                if (inner is VarTerm)
                {
                    // Runtime-variable goal: tag with the qualifying module so
                    // the meta-dispatch resolves it module-relative — one hop
                    // cheaper than the ':'/2 predicate, same semantics.
                    return new CompoundTerm("call", new[]
                    {
                        (Term)new CompoundTerm(MqualFunctor,
                            new[] { (Term)new AtomTerm(mod), inner }),
                    });
                }
                string? innerName = inner switch
                {
                    AtomTerm a2 => a2.Name,
                    CompoundTerm c2 => c2.Functor,
                    _ => null,
                };
                int innerArity = inner is CompoundTerm ic ? ic.Args.Length : 0;
                // \+ has no predicate to resolve to either: the compiler
                // lowers it inline, so M:(\+ G) takes the runtime path, which
                // carries M into the negation.
                if (innerName is not null && !IsControlFlow(innerName, innerArity)
                    && !(innerName == "\\+" && innerArity == 1))
                {
                    string? resolved = ctx.ResolveQualified(mod, innerName, innerArity);
                    if (resolved is not null)
                        return inner is CompoundTerm icc
                            ? new CompoundTerm(resolved, QualifyMetaArgsWith(icc, mod, ctx, resolved).Args)
                                { Position = c.Position }
                            : new AtomTerm(resolved) { Position = c.Position };
                }
                return goal;   // control construct / unknown module → runtime ':'/2
            }

            // Meta-predicate with a variable goal argument (a callable goal was
            // already inlined + mangled by MetaTransform): tag the variable with
            // the compile-time module so a runtime meta-call (findall/call/…)
            // resolves the bare goal relative to this module's locals. The tag
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

            if (ctx.MetaArgSpec is not null && QualifiesMetaArgs(ctx)
                && MetaGoalPositions(c.Functor, c.Args.Length) is null)
                c = QualifyMetaArgs(c, ctx);

            if (IsLocal(c.Functor, c.Args.Length, ctx) && !IsDynamic(c.Functor, c.Args.Length, ctx))
                return new CompoundTerm(MangledName(c.Functor, ctx), c.Args)
                    { Position = c.Position };
            // ADR-038 — imported name → Source$name (before the bare-global path).
            if (!IsDynamic(c.Functor, c.Args.Length, ctx)
                && TryResolveImport(c.Functor, c.Args.Length, ctx, out string cSrc))
                return new CompoundTerm(ImportedName(cSrc, c.Functor), c.Args)
                    { Position = c.Position };
            // The module-sensitive reflection builtins: the textual module is
            // their context, stamped at compile time exactly as $mqual stamps
            // meta-goals — there is no runtime context register. Runs only
            // after the local/import resolution above, so a module defining
            // its own clause/2 keeps it. Explicit qualification in the
            // argument nests and the innermost module wins, so the blind
            // wrap stays correct.
            if (ctx.ModuleName != PrologEngine.DefaultModuleName
                && ctx.ModuleName.Length > 0 && ctx.ModuleName[0] != '$')
            {
                switch (c.Functor, c.Args.Length)
                {
                    case ("current_predicate", 1):
                        return new CompoundTerm("$cp_ctx", new[]
                            { (Term)new AtomTerm(ctx.ModuleName), c.Args[0] })
                            { Position = c.Position };
                    // ADR-046 — op/3 and current_op/3 are module-sensitive:
                    // a runtime op inside module code targets the module's
                    // layer; current_op sees the module's effective view.
                    case ("op", 3):
                        return new CompoundTerm("$op_ctx", new[]
                            { (Term)new AtomTerm(ctx.ModuleName),
                              c.Args[0], c.Args[1], c.Args[2] })
                            { Position = c.Position };
                    case ("current_op", 3):
                        return new CompoundTerm("$current_op_ctx", new[]
                            { (Term)new AtomTerm(ctx.ModuleName),
                              c.Args[0], c.Args[1], c.Args[2] })
                            { Position = c.Position };
                    case ("clause", 2):
                    case ("predicate_property", 2):
                        return new CompoundTerm(c.Functor, new[]
                            {
                                (Term)new CompoundTerm(":", new[]
                                    { (Term)new AtomTerm(ctx.ModuleName), c.Args[0] }),
                                c.Args[1],
                            })
                            { Position = c.Position };
                }
            }
            if (IsBuiltin(c.Functor, c.Args.Length)) return c;
            return c;
        }

        return goal;
    }

    /// <summary>Any module but the prelude, whose helpers resolve by their
    /// own names. A consult's hidden early-hook module is an ordinary one
    /// here: its clauses are the loading module's own.</summary>
    private static bool QualifiesMetaArgs(Context ctx)
        => ctx.ModuleName.Length > 0 && ctx.ModuleName != "$prelude";

    /// <summary>ADR-056: <c>maplist(ok, L)</c> in module <c>m</c> becomes
    /// <c>maplist(m:ok, L)</c>. The closure is called from maplist's clauses,
    /// where <c>ok</c> would resolve in maplist's module. An argument that
    /// already names its module keeps it. A call to the module's own
    /// predicate is left alone: its clauses resolve in this module already,
    /// and wrapping a library's recursion costs a qualified goal per step.</summary>
    private static CompoundTerm QualifyMetaArgs(CompoundTerm c, Context ctx)
        => IsLocal(c.Functor, c.Args.Length, ctx) && !IsDynamic(c.Functor, c.Args.Length, ctx)
            ? c
            : QualifyMetaArgs(c, ctx.ModuleName, ctx, onlyWhereNeeded: true,
                localModule: null);

    /// <summary>Qualifies <paramref name="c"/>'s meta-arguments with
    /// <paramref name="module"/>, those already naming a module aside; with
    /// <paramref name="onlyWhereNeeded"/>, only those
    /// <see cref="NeedsContext"/> says resolve differently here.</summary>
    private static CompoundTerm QualifyMetaArgs(CompoundTerm c, string module, Context ctx,
        bool onlyWhereNeeded, string? localModule)
    {
        int fid = FunctorTable.Intern(
            AtomTable.Intern(c.Functor, permanent: true).Id, c.Args.Length);
        int[]? spec = ctx.LookupMetaArgSpec(fid, localModule);
        if (spec is null || spec.Length != c.Args.Length) return c;
        Term[]? args = null;
        for (int i = 0; i < spec.Length; i++)
        {
            if (spec[i] < 0) continue;
            Term a = c.Args[i];
            if (a is CompoundTerm { Functor: ":" or MqualFunctor, Args.Length: 2 }) continue;
            if (onlyWhereNeeded && !NeedsContext(a, spec[i], ctx)) continue;
            args ??= (Term[])c.Args.Clone();
            args[i] = new CompoundTerm(":", new[] { (Term)new AtomTerm(module), a })
                { Position = a.Position };
        }
        return args is null ? c : new CompoundTerm(c.Functor, args) { Position = c.Position };
    }

    /// <summary>False only for a closure that resolves the same from any
    /// module: one naming a global or builtin predicate, which is not a
    /// control construct and not a meta-predicate (whose own goal arguments
    /// may name this module's privates). user's locals are global. Anything
    /// else, a variable included, takes the module.</summary>
    private static bool NeedsContext(Term a, int extra, Context ctx)
    {
        // In user, whose own predicates are global, a control construct needs
        // the module only through a sub-goal that does. Wrapped anyway, the
        // module shows in an ISO error's culprit, (user:fail, user:3), and a
        // `;` no longer sees the if-then-else it is handed.
        bool inUser = ctx.ModuleName == PrologEngine.DefaultModuleName;
        string name;
        int arity;
        switch (a)
        {
            case VarTerm: return true;
            case AtomTerm at: name = at.Name; arity = extra; break;
            case CompoundTerm ct:
                // A grammar body handed to phrase is judged by its own shape:
                // (A, B) there is a conjunction, not ','/4.
                if (IsControlFlow(ct.Functor, ct.Args.Length)
                    || (ct.Functor == "{}" && ct.Args.Length == 1))
                {
                    if (!inUser) return true;
                    foreach (Term sub in ct.Args)
                        if (NeedsContext(sub, 0, ctx)) return true;
                    return false;
                }
                name = ct.Functor;
                arity = ct.Args.Length + extra;
                break;
            default: return false;
        }
        if (IsControlFlow(name, arity)) return !inUser;
        if (MetaGoalPositions(name, arity) is not null)
            return true;
        if (IsDynamic(name, arity, ctx)) return false;
        bool local = IsLocal(name, arity, ctx);
        if (local && ctx.ModuleName != PrologEngine.DefaultModuleName) return true;
        if (TryResolveImport(name, arity, ctx, out _)) return true;
        int fid = FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);
        return ctx.LookupMetaArgSpec(fid, local ? ctx.ModuleName : null) is not null;
    }

    /// <summary>ADR-056: <c>M:Goal</c> makes <c>M</c> the context of Goal's
    /// meta-arguments too (Scryer), so each takes <c>M</c>.</summary>
    private static CompoundTerm QualifyMetaArgsWith(CompoundTerm c, string module, Context ctx,
        string resolvedName)
        => ctx.MetaArgSpec is null || module == "$prelude"
            ? c
            : QualifyMetaArgs(c, module, ctx, onlyWhereNeeded: false,
                localModule: resolvedName == module + "$" + c.Functor ? module : null);

    /// <summary>The meta-arguments of the prelude's higher-order predicates
    /// that declare none: per argument, how many arguments the callee adds to
    /// the closure, or -1. Kept here rather than as <c>meta_predicate</c>
    /// directives so <c>predicate_property/2</c> reports what it always
    /// did.</summary>
    public static int[]? LibraryMetaArgSpec(string name, int arity) => (name, arity) switch
    {
        ("maplist", >= 2 and <= 8) => Spec(arity, 0, arity - 1),
        ("foldl", >= 4 and <= 6) => Spec(arity, 0, arity - 1),
        ("include" or "exclude", 3) => Spec(arity, 0, 1),
        ("partition", 4) => Spec(arity, 0, 1),
        ("predsort", 3) => Spec(arity, 0, 3),
        ("countall" or "call_det" or "call_residue_vars", 2) => Spec(arity, 0, 0),
        ("time", 1) => Spec(arity, 0, 0),
        ("phrase", 2 or 3) => Spec(arity, 0, 2),
        ("with_output_to", 2) => Spec(arity, 1, 0),
        _ => null,
    };

    /// <summary>The spec a compile without an engine uses: the prelude's
    /// <c>meta_predicate</c> declarations, then
    /// <see cref="LibraryMetaArgSpec"/>. A module's own predicate has none
    /// here: only that module's declarations could describe it.</summary>
    public static int[]? DefaultMetaArgSpec(int fid, string? localModule)
    {
        if (localModule is not null) return null;
        if (PreludeMetaTemplates.Value.TryGetValue(fid, out Term? template))
            return MetaArgSpecOfTemplate(template);
        var (atomId, arity) = FunctorTable.Lookup(fid);
        string? name = AtomTable.GetById(atomId)?.Name;
        return name is null ? null : LibraryMetaArgSpec(name, arity);
    }

    private static readonly Lazy<Dictionary<int, Term>> PreludeMetaTemplates = new(() =>
    {
        var templates = new Dictionary<int, Term>();
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(Prelude.Source,
                @"^\s*:-\s*meta_predicate\((.*)\)\s*\.\s*$",
                System.Text.RegularExpressions.RegexOptions.Multiline))
        {
            try
            {
                var parser = new Shumway.Compiler.Parsing.Parser(
                    new Shumway.Compiler.Lexer.Lexer(m.Groups[1].Value),
                    Shumway.Compiler.Parsing.OperatorTable.Default());
                RecordMetaTemplate(templates, parser.ReadTerm());
            }
            catch (Exception ex) when (
                ex is Shumway.Compiler.Parsing.ParseException
                    or Shumway.Compiler.Lexer.LexerException)
            {
            }
        }
        return templates;
    });

    /// <summary>Records a <c>meta_predicate</c> directive's argument: one
    /// template, a <c>','</c>-conjunction of them, or <c>Module:Template</c>
    /// (under the bare name).</summary>
    public static void RecordMetaTemplate(Dictionary<int, Term> templates, Term spec)
    {
        switch (spec)
        {
            case CompoundTerm { Functor: ",", Args.Length: 2 } conj:
                RecordMetaTemplate(templates, conj.Args[0]);
                RecordMetaTemplate(templates, conj.Args[1]);
                break;
            case CompoundTerm { Functor: ":", Args.Length: 2 } qual:
                RecordMetaTemplate(templates, qual.Args[1]);
                break;
            case CompoundTerm template:
                templates[FunctorTable.Intern(
                    AtomTable.Intern(template.Functor, permanent: true).Id,
                    template.Args.Length)] = template;
                break;
        }
    }

    private static int[] Spec(int arity, int position, int extra)
    {
        var spec = new int[arity];
        System.Array.Fill(spec, -1);
        spec[position] = extra;
        return spec;
    }

    /// <summary>A <c>meta_predicate</c> template as a meta-argument spec:
    /// 0 to 9 is a goal or closure taking that many extra arguments, and
    /// <c>:</c> and <c>^</c> are module-sensitive; anything else is data.
    /// Null when no argument is a meta-argument.</summary>
    public static int[]? MetaArgSpecOfTemplate(Term template)
    {
        if (template is not CompoundTerm t) return null;
        int[]? spec = null;
        for (int i = 0; i < t.Args.Length; i++)
        {
            int extra = t.Args[i] switch
            {
                IntTerm { Value: >= 0 and <= 9 } n => (int)n.Value,
                AtomTerm { Name: ":" or "^" } => 0,
                _ => -1,
            };
            if (extra < 0) continue;
            if (spec is null)
            {
                spec = new int[t.Args.Length];
                System.Array.Fill(spec, -1);
            }
            spec[i] = extra;
        }
        return spec;
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

    // ADR-038 — the mangled name of an imported predicate in its source module.
    private static string ImportedName(string sourceModule, string name) =>
        sourceModule + "$" + name;

    // ADR-038 — resolve name/arity through the module's import table.
    private static bool TryResolveImport(string name, int arity, Context ctx, out string sourceModule)
    {
        if (ctx.Imports.Count == 0)
        {
            sourceModule = "";
            return false;
        }
        int functorId = FunctorTable.Intern(
            AtomTable.Intern(name, permanent: true).Id, arity);
        return ctx.Imports.TryGetValue(functorId, out sourceModule!);
    }

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
