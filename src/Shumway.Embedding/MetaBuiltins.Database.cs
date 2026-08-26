using System.Linq;
using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

public static partial class MetaBuiltins
{
    /// <summary><c>predicate_property(+Head, ?Property)</c> — enumerates the
    /// properties of the predicate named by <paramref name="Head"/>'s functor
    /// (a callable). A defined predicate has <c>defined</c> plus exactly one of
    /// <c>built_in</c> / <c>dynamic</c> / <c>static</c>; an undefined predicate
    /// has none (the call fails). Non-deterministic: on backtracking it yields
    /// each property in turn, so a bound <c>Property</c> acts as a filter. Head
    /// must be instantiated (ISO instantiation_error / type_error(callable)).
    /// Enough for the SWI/GNU-style introspection Logtalk's compiler relies on.</summary>
    /// <summary>Cheap pre-unification filter: can this property term possibly
    /// unify with the bound argument? Name and arity only — the cursor still
    /// does the real unification.</summary>
    private static bool PropertyCanMatch(Term prop, Term wanted) => (prop, wanted) switch
    {
        (_, VarTerm) => true,
        (AtomTerm a, AtomTerm b) => a.Name == b.Name,
        (CompoundTerm c, CompoundTerm d) =>
            c.Functor == d.Functor && c.Args.Length == d.Args.Length,
        _ => false,
    };

    /// <summary>The property names predicate_property/2 can answer. A bound
    /// argument outside this set is domain_error(predicate_property, P).</summary>
    private static bool IsKnownPredicateProperty(Term t) => t switch
    {
        AtomTerm a => a.Name is "built_in" or "dynamic" or "static" or "defined"
            or "multifile" or "discontiguous" or "control_construct"
            or "logtalk" or "foreign" or "iso" or "deterministic",
        CompoundTerm { Functor: "imported_from", Args.Length: 1 } => true,
        CompoundTerm { Functor: "meta_predicate", Args.Length: 1 } => true,
        CompoundTerm { Functor: "number_of_clauses", Args.Length: 1 } => true,
        _ => false,
    };

    public static bool PredicateProperty(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "predicate_property/2 requires a PrologEngine host.");
        Term head = MaterializeRegister(engine, 0);
        // A Module:Head query answers from M's VIEWPOINT (the SICStus
        // doctrine: current_predicate(M:PI) is strictly M's definitions,
        // predicate_property is where visibility — imports included — shows).
        // Innermost module wins for a nested qualification. Logtalk's
        // compiler asks exactly this shape (`user:freeze(X, G)`) to learn
        // whether the callee is a meta-predicate.
        string? module = null;
        while (head is CompoundTerm { Functor: ":", Args.Length: 2 } qualified)
        {
            switch (qualified.Args[0])
            {
                case AtomTerm ma: module = ma.Name; break;
                case VarTerm:
                    throw new ShumwayPrologException(IsoError.InstantiationError());
                default:
                    throw new ShumwayPrologException(
                        IsoError.TypeError("atom", qualified.Args[0]));
            }
            head = qualified.Args[1];
        }
        int fid;
        switch (head)
        {
            case VarTerm:
                throw new ShumwayPrologException(IsoError.InstantiationError());
            case AtomTerm a:
                fid = FunctorTable.Intern(AtomTable.Intern(a.Name, permanent: true).Id, 0);
                break;
            case CompoundTerm c:
                fid = FunctorTable.Intern(
                    AtomTable.Intern(c.Functor, permanent: true).Id, c.Args.Length);
                break;
            default:
                throw new ShumwayPrologException(IsoError.TypeError("callable", head));
        }
        // A bound Property that names no property at all is a domain error
        // (§predicate_property), not a quiet failure.
        Term propTerm = MaterializeRegister(engine, 1);
        if (propTerm is not VarTerm && !IsKnownPredicateProperty(propTerm))
            throw new ShumwayPrologException(
                IsoError.DomainError("predicate_property", propTerm));

        // M's viewpoint: its own definition; else the import's SOURCE
        // predicate plus imported_from(Source); else the bare-global /
        // builtin the module sees like everyone else.
        string? importedFrom = null;
        List<Term> props;
        if (module is not null && host.ModuleDefinesFunctor(module, fid))
        {
            props = new List<Term>
            {
                new AtomTerm(host.IsDynamic(fid) ? "dynamic" : "static"),
                new AtomTerm("defined"),
            };
        }
        else
        {
            if (module is not null
                && host.ModuleImportSource(module, fid) is { } src
                && host.ModuleDefinesFunctor(src, fid))
            {
                importedFrom = src;
                props = new List<Term>
                {
                    new AtomTerm(host.IsDynamic(fid) ? "dynamic" : "static"),
                    new AtomTerm("defined"),
                };
            }
            else
            {
                var atomProps = host.PredicatePropertyAtomIds(fid);
                if (atomProps.Count == 0) return false;
                props = new List<Term>(atomProps.Count + 2);
                foreach (int atomId in atomProps)
                    props.Add(new AtomTerm(AtomTable.GetById(atomId)?.Name ?? "?"));
            }
        }
        // `:- multifile PI` is a property of the predicate, whichever module
        // declared it — the clauses accumulate across files.
        if (props.Count > 0 && host.IsMultifileFunctor(fid))
            props.Add(new AtomTerm("multifile"));
        if (importedFrom is not null)
            props.Add(new CompoundTerm("imported_from",
                new Term[] { new AtomTerm(importedFrom) }));
        // The declared meta-template, when one was recorded (`:- meta_predicate`).
        if (host._metaPredicateTemplates.TryGetValue(fid, out Term? template))
            props.Add(new CompoundTerm("meta_predicate", new[] { template }));
        // A BOUND Property narrows the list, so the cursor enumerates
        // SOLUTIONS rather than every property this predicate has —
        // `predicate_property(p(_), dynamic)` is then deterministic.
        Term wanted = MaterializeRegister(engine, 1);
        if (wanted is not VarTerm)
            props = props.Where(pr => PropertyCanMatch(pr, wanted)).ToList();

        int returnPc = engine.BuiltinReturnPc;
        return Shumway.Core.IndexEnumCursor.Start(engine, props.Count, 2, returnPc,
            (e, i) => e.UnifyRegisterWithCell(
                1, Materializer.MaterializeAsCell(e, props[i])));
    }

    /// <summary>Promotes a Core-level <see cref="PrologRuntimeException"/>
    /// into the canonical ISO <c>error(Kind, _)</c> Prolog term that
    /// user-written catchers expect.</summary>
    /// <summary>Builds the three-argument
    /// <c>permission_error(Op, ObjType, Obj)</c> from a Detail string
    /// shaped <c>"Op,ObjType"</c>. The Obj slot is a fresh anonymous
    /// variable — PrologRuntimeException can't carry a Term payload
    /// yet, so the offending object is lost in translation; a catcher
    /// can still pattern-match on Op and ObjType.</summary>
    private static Term BuildPermissionError(PrologRuntimeException re)
    {
        string[] parts = re.Detail.Split(',', 2);
        string op = parts.Length > 0 ? parts[0] : "?";
        string objType = parts.Length > 1 ? parts[1] : "?";
        return new CompoundTerm("permission_error", new Term[]
        {
            new AtomTerm(op),
            new AtomTerm(objType),
            ValueTermOrVar(re),
        });
    }

    /// <summary>Builds the ISO Context indicator <c>Name/Arity</c> from
    /// the exception's stamped builtin identity, or returns
    /// <c>null</c> when no builtin stamped it — meaning the throw arose
    /// outside builtin dispatch (e.g. from the bytecode interpreter's
    /// undefined-procedure resolver) and the Context should fall back
    /// to a fresh anonymous variable.</summary>
    private static Term? StampedContext(PrologRuntimeException re) =>
        re.BuiltinName is string name
            ? new CompoundTerm("/",
                new Term[] { new AtomTerm(name), new IntTerm(re.BuiltinArity) })
            : null;

    /// <summary>Constructs <c>error(Inner, Context)</c> with the
    /// stamped Context if one is available, falling back to a fresh
    /// anonymous variable when the exception predates any builtin
    /// dispatch.</summary>
    private static Term WrapWithStampedContext(Term inner, PrologRuntimeException re) =>
        new CompoundTerm("error",
            new Term[] { inner, StampedContext(re) ?? new VarTerm("_") });

    internal static Term TranslateRuntimeError(PrologRuntimeException re) => re.Kind switch
    {
        "evaluation_error" => WrapWithStampedContext(
            new CompoundTerm("evaluation_error", new Term[] { new AtomTerm(re.Detail) }), re),
        "instantiation_error" => WrapWithStampedContext(
            new AtomTerm("instantiation_error"), re),
        // type_error / domain_error now report the
        // offending value in the second slot when the throw site
        // captured it.
        "type_error" => WrapWithStampedContext(
            new CompoundTerm("type_error",
                new Term[] { new AtomTerm(re.Detail), ValueTermOrVar(re) }), re),
        "uninstantiation_error" => WrapWithStampedContext(
            new CompoundTerm("uninstantiation_error",
                new Term[] { ValueTermOrVar(re) }), re),
        "existence_error" => WrapWithStampedContext(BuildExistenceError(re), re),
        "ambiguous_module_local" => BuildAmbiguousModuleLocal(re),
        "domain_error" => WrapWithStampedContext(
            new CompoundTerm("domain_error",
                new Term[] { new AtomTerm(re.Detail), ValueTermOrVar(re) }), re),
        "representation_error" => WrapWithStampedContext(
            new CompoundTerm("representation_error", new Term[] { new AtomTerm(re.Detail) }), re),
        "syntax_error" => WrapWithStampedContext(
            new CompoundTerm("syntax_error", new Term[] { new AtomTerm(re.Detail) }), re),
        "resource_error" => WrapWithStampedContext(
            new CompoundTerm("resource_error", new Term[] { new AtomTerm(re.Detail) }), re),
        // ISO permission_error has three args. The Detail
        // string encodes "Operation,ObjectType" (e.g. "modify,static_procedure");
        // we split on the comma and put a fresh var in the Obj slot
        // (the exception carries the offending object too when present).
        "permission_error" => WrapWithStampedContext(
            BuildPermissionError(re), re),
        "system_error" => WrapWithStampedContext(
            string.IsNullOrEmpty(re.Detail)
                ? (Term)new AtomTerm("system_error")
                : new CompoundTerm("system_error", new Term[] { new AtomTerm(re.Detail) }),
            re),
        _ => new CompoundTerm("error",
            new Term[] { new AtomTerm(re.Kind), new AtomTerm(re.Detail) }),
    };

    /// <summary>Returns the captured offending term (from
    /// <see cref="PrologRuntimeException.Value"/>) when the throw site
    /// snapshotted one, or a fresh anonymous var otherwise.
    /// </summary>
    private static Term ValueTermOrVar(PrologRuntimeException re) => re.Value switch
    {
        Term t => t,
        long l => new IntTerm(l),
        double d => new FloatTerm(d),
        System.Numerics.BigInteger bi => new BigIntTerm(bi),
        _ => new VarTerm("_"),
    };

    /// <summary>Builds the procedure-indicator term for an
    /// <c>existence_error(procedure, Name/Arity)</c> from the
    /// <see cref="PrologRuntimeException.Detail"/> string
    /// <c>"Name/Arity"</c> (as written by
    /// <see cref="PrologRuntimeException.UndefinedProcedure"/>). ISO requires
    /// the culprit to be the COMPOUND <c>'/'(Name, Arity)</c>, not an atom whose
    /// name happens to be <c>"Name/Arity"</c> — otherwise a catcher pattern
    /// <c>error(existence_error(procedure, foo/3), _)</c> can never unify with
    /// the ball. Splits on the LAST <c>/</c> (so a quoted name containing a
    /// slash, e.g. <c>'a/b'/2</c>, still resolves correctly) and falls back to
    /// the bare atom if the suffix isn't a non-negative integer.</summary>
    /// <summary>An existence_error's Detail is either a <c>Name/Arity</c>
    /// procedure indicator (undefined-predicate path) or an ISO object-type
    /// atom — <c>source_sink</c>, <c>stream</c>, <c>variable</c> — in which
    /// case the culprit is the captured offending value. Distinguished by
    /// whether the Detail parses as an indicator; collapsing both onto
    /// <c>existence_error(procedure, Detail)</c> made
    /// <c>catch(open(...), error(existence_error(source_sink, _), _), _)</c>
    /// unreachable.</summary>
    /// <summary>The consult-direct fallback's ambiguity ball:
    /// <c>error(existence_error(procedure, N/A),
    /// shumway(ambiguous_module_local, Modules))</c>. Still an
    /// existence_error — a catcher for the undefined-procedure shape
    /// matches — but the context lists the modules that each define the
    /// name, so the message says how to disambiguate (qualify the call).
    /// Detail is <c>"Name/Arity|m1,m2"</c> (see
    /// <see cref="PrologRuntimeException.AmbiguousModuleLocal"/>).</summary>
    private static Term BuildAmbiguousModuleLocal(PrologRuntimeException re)
    {
        int bar = re.Detail.LastIndexOf('|');
        string pi = bar < 0 ? re.Detail : re.Detail[..bar];
        Term modules = new AtomTerm("[]");
        if (bar >= 0)
        {
            string[] names = re.Detail[(bar + 1)..].Split(',');
            for (int i = names.Length - 1; i >= 0; i--)
                modules = new CompoundTerm(".",
                    new Term[] { new AtomTerm(names[i]), modules });
        }
        return new CompoundTerm("error", new Term[]
        {
            new CompoundTerm("existence_error",
                new Term[] { new AtomTerm("procedure"), ProcedureIndicatorTerm(pi) }),
            new CompoundTerm("shumway", new Term[]
                { new AtomTerm("ambiguous_module_local"), modules }),
        });
    }

    private static Term BuildExistenceError(PrologRuntimeException re)
    {
        Term culprit = ProcedureIndicatorTerm(re.Detail);
        if (culprit is CompoundTerm)
            return new CompoundTerm("existence_error",
                new Term[] { new AtomTerm("procedure"), culprit });
        return new CompoundTerm("existence_error",
            new Term[] { new AtomTerm(re.Detail), ValueTermOrVar(re) });
    }

    private static Term ProcedureIndicatorTerm(string detail)
    {
        int slash = detail.LastIndexOf('/');
        if (slash > 0 && slash < detail.Length - 1
            && int.TryParse(detail.AsSpan(slash + 1), out int arity) && arity >= 0)
            return new CompoundTerm("/",
                new Term[] { new AtomTerm(detail.Substring(0, slash)), new IntTerm(arity) });
        return new AtomTerm(detail);
    }

    private static int ExtractCallableFunctorId(Term head, string builtinName)
    {
        return head switch
        {
            AtomTerm a => FunctorTable.Intern(
                AtomTable.Intern(a.Name, permanent: true).Id, 0),
            CompoundTerm c => FunctorTable.Intern(
                AtomTable.Intern(c.Functor, permanent: true).Id, c.Args.Length),
            VarTerm => throw new ShumwayPrologException(IsoError.InstantiationError()),
            _ => throw new ShumwayPrologException(
                IsoError.TypeError("callable", head)),
        };
    }

    // ============================================================================
    // throw / catch
    // ============================================================================

    /// <summary><c>throw(Error)</c> — raises <see cref="ShumwayPrologException"/>
    /// carrying <c>Error</c>'s materialised term. Propagates up the C# stack
    /// until a <c>catch/3</c> or the engine's top-level intercepts it.</summary>
    public static bool Throw(Activation engine)
    {
        Term error = MaterializeRegister(engine, 0);
        // ISO §7.8.10.3.a — an unbound ball is
        // instantiation_error. (Other shapes are user-defined and
        // pass through verbatim.)
        if (error is VarTerm)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        throw new ShumwayPrologException(error);
    }

    // catch/3 is now a prelude predicate built on the catch-frame
    // plumbing ($catch_begin/$catch_end), running the guarded goal in the LIVE
    // engine. The old isolated-sub-engine builtin (which ran Goal in a peer
    // sub-engine and bound back only the first solution) was removed — it hid
    // the guarded goal's assert/retract and other side effects from the caller,
    // and was only ever the fallback for a variable Goal/Recovery anyway (a
    // statically-callable catch/3 is rewritten inline by MetaTransform). See
    // Prelude catch/3.

    /// <summary><c>'$catch_begin'(Catcher, RecoveryGoal)</c> —
    /// opens a catch/3 scope. Copies the catcher and the recovery-goal call
    /// onto the heap (so they survive a caught throw's heap truncation) and
    /// pushes a catch frame snapshotting the live machine. Emitted by the
    /// MetaTransform rewrite of catch/3 as the first goal of the goal
    /// helper, so the engine reads the recovery continuation off that
    /// helper's environment header.</summary>
    public static bool CatchBegin(Activation engine)
    {
        int catcherSlot = engine.AllocateHeap(1);
        engine.SetHeap(catcherSlot, engine.GetRegister(0));
        int recoverySlot = engine.AllocateHeap(1);
        engine.SetHeap(recoverySlot, engine.GetRegister(1));
        engine.PushCatchFrame(catcherSlot, recoverySlot);
        return true;
    }

    /// <summary><c>'$catch_end'/0</c> — closes a catch/3 scope:
    /// the guarded goal has produced a solution, so the catch frame is
    /// deactivated and a throw from the continuation will no longer be
    /// caught here. Backtracking into the guarded goal re-activates it.</summary>
    public static bool CatchEnd(Activation engine)
    {
        engine.DeactivateTopCatchFrame();
        return true;
    }

    // ============================================================================
    // call/N — registered so the compiler emits call_builtin; dispatched
    // IN THE LIVE ENGINE (never these bodies)
    // ============================================================================

    public static bool Call1(Activation engine) => CallN(engine, totalArity: 1);
    public static bool Call2(Activation engine) => CallN(engine, totalArity: 2);
    public static bool Call3(Activation engine) => CallN(engine, totalArity: 3);
    public static bool Call4(Activation engine) => CallN(engine, totalArity: 4);
    public static bool Call5(Activation engine) => CallN(engine, totalArity: 5);
    public static bool Call6(Activation engine) => CallN(engine, totalArity: 6);
    public static bool Call7(Activation engine) => CallN(engine, totalArity: 7);
    public static bool Call8(Activation engine) => CallN(engine, totalArity: 8);

    /// <summary><c>'$call'(Goal, Barrier)</c> — the cut-barrier-carrying
    /// meta-call. It is intercepted by the bytecode interpreter
    /// exactly like <c>call/N</c> and never reaches this body; the entry
    /// exists only so the compiler emits a <c>call_builtin</c> for it.</summary>
    public static bool CallWithBarrier(Activation engine) =>
        throw new InvalidOperationException(
            "'$call'/2 must be dispatched by the interpreter, not invoked directly.");

    /// <summary><c>call(Goal, ExtraArgs...)</c> — like <c>'$call'/2</c>
    /// above, the registration exists so the compiler emits a
    /// <c>call_builtin</c>; the interpreter's <c>IsCall</c> routing runs the
    /// goal in the live engine and this body must never be reached.</summary>
    private static bool CallN(Activation engine, int totalArity)
    {
        // DEAD PATH — must never run. call/N is dispatched IN THE LIVE ENGINE:
        // the call_builtin opcode handler sees the builtin's IsCall flag and
        // routes to BytecodeInterpreter.DispatchCall (Tier-0) — and the Tier-1
        // IL emit routes through IlMetaCallHelper.Dispatch — both of which run
        // the goal directly in this engine (so assert/retract from the called
        // goal are visible to the caller). This
        // builtin body (the historical isolated-sub-engine fallback) is never
        // reached. The sub-engine deep-copies the dynamic store, so if it DID
        // run, side effects from the called goal would silently not bleed back —
        // a correctness bug. Fail loudly instead of producing wrong answers.
        _ = totalArity;
        throw new InvalidOperationException(
            "call/N reached the sub-engine fallback in MetaBuiltins.CallN, but " +
            "call/N must be dispatched in the live engine by DispatchCall (Tier-0) " +
            "or IlMetaCallHelper (Tier-1). Reaching here means the IsCall meta-" +
            "dispatch routing was bypassed, a bug to fix at the dispatch site, " +
            "not here.");
    }

    /// <summary><c>garbage_collect/0</c> (ADR-016) — mark-compacts the
    /// heap. A no-op when attributed variables are in use (the collector
    /// bails) or when there is nothing to reclaim.</summary>
    public static bool GarbageCollect(Activation engine)
    {
        // Arity 0: no X register is live, so none of the bank's stale
        // leftovers can root garbage — the explicit collection the user
        // asked for reclaims everything genuinely dead.
        engine.CollectHeapBounded(0);
        return true;
    }

    /// <summary><c>compile_all/0</c> — eagerly compile every compilable static
    /// predicate to Tier-1 IL now (opt-in warm-up; the load default is lazy).</summary>
    public static bool CompileAll0(Activation engine)
    {
        if (engine.Host is PrologEngine host) host.WarmAllCompilable();
        return true;
    }

    /// <summary><c>compile_all(-Count)</c> — as <c>compile_all/0</c>, unifying
    /// Count with the number of predicates newly compiled.</summary>
    public static bool CompileAll1(Activation engine)
    {
        int n = engine.Host is PrologEngine host ? host.WarmAllCompilable() : 0;
        return engine.UnifyRegisterWithCell(0, Shumway.Core.Cell.Int(n));
    }

    /// <summary><c>'$scc_register'(-Ref)</c> — registers a setup_call_cleanup
    /// cleanup handler at the current choice-point level; Ref keys the stored
    /// Cleanup goal.</summary>
    public static bool SccRegister(Activation engine)
    {
        // arg 1 is the LIVE Cleanup term — capture its dereffed cell so an
        // async fire (cut / unwind / teardown) runs it with bindings intact.
        Cell live = engine.GetRegister(1);
        if (live.Tag == Tag.Ref)
        {
            int idx = engine.Deref(live.AsHeapIndex);
            Cell at = engine.GetHeap(idx);
            // Bound: keep the VALUE cell; unbound: keep a REF to its home
            // (bindings made later flow through when the async fire runs).
            live = at.Tag == Tag.Ref ? Cell.Ref(idx) : at;
        }
        return engine.UnifyRegisterWithCell(0,
            Shumway.Core.Cell.Int(engine.RegisterCleanupHandler(live)));
    }

    /// <summary><c>'$scc_forget'(+Ref)</c> — drops a handler the prelude fired
    /// synchronously so it can never fire again asynchronously.</summary>
    public static bool SccForget(Activation engine)
    {
        Term r = MaterializeRegister(engine, 0);
        if (r is IntTerm it) engine.ForgetCleanupHandler((int)it.Value);
        return true;
    }

    /// <summary><c>'$pop_pending_cleanup'(-Ref)</c> — pops one Ref enqueued by an
    /// engine teardown path (cut / exception unwind / query end); fails when the
    /// queue is empty. The prelude's '$drain_cleanups'/0 loops on it.</summary>
    public static bool PopPendingCleanup(Activation engine) =>
        engine.TryPopPendingCleanup(out int refId, out Cell liveCleanup, out bool useLive)
        && engine.UnifyRegisterWithCell(0, Shumway.Core.Cell.Int(refId))
        && engine.UnifyRegisterWithCell(1,
            useLive ? liveCleanup
                    : Cell.Atom(AtomTable.Intern("$scc_use_copy", permanent: true).Id));

    /// <summary><c>module_property(?Module, ?Property)</c> — introspects a loaded
    /// module. Supports <c>exports(List)</c> (the <c>Name/Arity</c> indicators the
    /// module exports — non-empty only for an export-qualified <c>:- module(Name,
    /// [Exports])</c> module) and <c>class(Class)</c> (<c>user</c> for the default
    /// module, <c>system</c> for the prelude host, else <c>library</c>). With an
    /// unbound <c>Module</c> it enumerates the loaded modules on backtracking.
    /// A minimal SWI-compatible surface — enough for library introspection.</summary>
    public static bool ModuleProperty(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "module_property/2 requires a PrologEngine host.");

        Term m = MaterializeRegister(engine, 0);
        if (m is AtomTerm mAtom)
        {
            if (!host.Modules.TryGetValue(mAtom.Name, out var manifest))
                return false;
            return UnifyModuleProperty(engine, mAtom.Name, manifest);
        }
        if (m is not VarTerm)
            throw new ShumwayPrologException(IsoError.TypeError("atom", m));

        // Unbound module — enumerate every loaded module, binding arg 0 to each
        // name and re-testing arg 1 against that module's properties.
        var names = new List<string>(host.Modules.Keys);
        int returnPc = engine.BuiltinReturnPc;
        return Shumway.Core.IndexEnumCursor.Start(engine, names.Count, 2, returnPc,
            (e, i) =>
                e.UnifyRegisterWithCell(0,
                    Shumway.Core.Cell.Atom(AtomTable.Intern(names[i], permanent: true).Id))
                && UnifyModuleProperty(e, names[i], host.Modules[names[i]]));
    }

    /// <summary>Unifies register 1 against one property of <paramref name="manifest"/>.
    /// The property term is already in the register (bound = filter, unbound =
    /// take the first — <c>exports</c>).</summary>
    private static bool UnifyModuleProperty(Activation engine, string name, ModuleManifest manifest)
    {
        Term prop = MaterializeRegister(engine, 1);
        if (prop is CompoundTerm c && c.Args.Length == 1)
        {
            switch (c.Functor)
            {
                case "class":
                {
                    string cls = name == "user" ? "user"
                        : name == "system" ? "system" : "library";
                    int strBase = engine.AllocateHeap(3);
                    engine.SetHeap(strBase, Shumway.Core.Cell.Str(strBase + 1));
                    engine.SetHeap(strBase + 1,
                        Shumway.Core.Cell.Functor(FunctorTable.Intern(
                            AtomTable.Intern("class", permanent: true).Id, 1)));
                    engine.SetHeap(strBase + 2,
                        Shumway.Core.Cell.Atom(AtomTable.Intern(cls, permanent: true).Id));
                    return engine.UnifyRegisterWithHeapAt(1, strBase);
                }
                case "exports":
                {
                    // Build each Name/Arity structure, then the [ ... ] spine.
                    int slashFid = FunctorTable.Intern(
                        AtomTable.Intern("/", permanent: true).Id, 2);
                    var elems = new List<Shumway.Core.Cell>(manifest.ExportFunctors.Count);
                    foreach (int fid in manifest.ExportFunctors)
                    {
                        var (atomId, arity) = FunctorTable.Lookup(fid);
                        // Name/Arity: functor-then-args, contiguous.
                        int strBase = engine.AllocateHeap(3);
                        engine.SetHeap(strBase, Shumway.Core.Cell.Functor(slashFid));
                        engine.SetHeap(strBase + 1, Shumway.Core.Cell.Atom(atomId));
                        engine.SetHeap(strBase + 2, Shumway.Core.Cell.Int(arity));
                        elems.Add(Shumway.Core.Cell.Str(strBase));
                    }
                    int listStart = BuildCellList(engine, elems);
                    Shumway.Core.Cell listCell = engine.GetHeap(listStart);
                    int es = engine.AllocateHeap(2);
                    engine.SetHeap(es, Shumway.Core.Cell.Functor(FunctorTable.Intern(
                        AtomTable.Intern("exports", permanent: true).Id, 1)));
                    engine.SetHeap(es + 1, listCell);
                    return engine.UnifyRegisterWithCell(1, Shumway.Core.Cell.Str(es));
                }
            }
        }
        // Unrecognised property → fail (unknown property).
        return false;
    }

    /// <summary>Builds a fresh cons-list from a set of element cells, terminated
    /// by <c>[]</c>. Returns the heap index whose cell is the list value
    /// (a <c>Lis</c>, or the lone <c>[]</c> atom when empty). Layout matches the
    /// standard contiguous spine: <c>[Lis→h0][h0][Lis→h1][h1]…[nil]</c>.</summary>
    private static int BuildCellList(Activation engine, IReadOnlyList<Shumway.Core.Cell> elements)
    {
        if (elements.Count == 0)
        {
            int nil = engine.AllocateHeap(1);
            engine.SetHeap(nil, Shumway.Core.Cell.Atom(AtomTable.EmptyListId));
            return nil;
        }
        int start = engine.AllocateHeap(2 * elements.Count + 1);
        for (int i = 0; i < elements.Count; i++)
        {
            int lisIdx = start + 2 * i;
            int headIdx = lisIdx + 1;
            engine.SetHeap(lisIdx, Shumway.Core.Cell.Lis(headIdx));
            engine.SetHeap(headIdx, elements[i]);
        }
        engine.SetHeap(start + 2 * elements.Count, Shumway.Core.Cell.Atom(AtomTable.EmptyListId));
        return start;
    }

    /// <summary><c>trace/0</c> (ADR-035) — attaches the four-port tracer. It is
    /// attached to the running activation as well as to the engine, so tracing
    /// starts with the very next goal of the query that called <c>trace</c>
    /// rather than at the next query.</summary>
    public static bool Trace(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException("trace/0 requires a PrologEngine host.");
        host.SetTracing(true, engine.Out);
        engine.Debug = host.DebugSession;
        return true;
    }

}
