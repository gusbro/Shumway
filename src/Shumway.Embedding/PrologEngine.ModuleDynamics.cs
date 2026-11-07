using Shumway.Core;

namespace Shumway.Embedding;

public sealed partial class PrologEngine
{
    // ADR-038 (amendment): a dynamic predicate of an export-qualified module
    // compiled with arity_compat off belongs to that module. The store stays
    // one flat table keyed by functor id; the module's dynamics are keyed by
    // their mangled name, Module$p, as its statics are. The clauses in the
    // store keep their bare heads, as a manifest keeps a module's static
    // clauses: the mangling happens when the predicate compiles.

    /// <summary>True when <paramref name="module"/> is loaded and owns its
    /// dynamics (<see cref="ModuleManifest.OwnsDynamics"/>).</summary>
    internal bool ModuleOwnsDynamics(string? module)
        => module is not null && _modules.TryGetValue(module, out var m) && m.OwnsDynamics;

    /// <summary>The module and bare functor of a <c>Module$name</c> functor
    /// whose module is loaded; false for any other functor.</summary>
    internal bool TryDemangleModule(int fid, out string module, out int bareFid)
    {
        module = "";
        bareFid = fid;
        if (!ModuleQualify.TryDemangle(fid, out int moduleAtom, out int bare)) return false;
        string? name = AtomTable.GetById(moduleAtom)?.Name;
        if (name is null || !_modules.ContainsKey(name)) return false;
        module = name;
        bareFid = bare;
        return true;
    }

    internal static int MangleFunctor(string module, int bareFid)
    {
        var (atomId, arity) = FunctorTable.Lookup(bareFid);
        return ModuleQualify.Mangle(AtomTable.Intern(module, permanent: true).Id, atomId, arity);
    }

    /// <summary>The store key a database operation written in
    /// <paramref name="module"/> (null: the global module) reaches by the
    /// bare functor: an import's source predicate when the source owns its
    /// dynamics (an exported dynamic is one predicate), the module's own
    /// <c>Module$p</c> when the module owns its dynamics, the bare functor
    /// otherwise. A module named for the first time here is created when
    /// <paramref name="create"/>; else it holds nothing, and the key is
    /// -1, which no store entry has.</summary>
    internal int DatabaseFunctorFor(string? module, int bareFid, bool create = true)
    {
        module ??= DefaultModuleName;
        if (!_modules.TryGetValue(module, out var m))
        {
            if (!create) return -1;
            m = CreateModuleByName(module);
        }
        if (m.Imports.TryGetValue(bareFid, out string? src))
            return ModuleOwnsDynamics(src) ? MangleFunctor(src, bareFid) : bareFid;
        if (!m.OwnsDynamics) return bareFid;
        int own = MangleFunctor(module, bareFid);
        if (_dynStore.IsDynamic(own)) return own;
        // A static of the module: the modify guards refuse it under its own
        // name. A public dynamic of the legacy world is what a bare call
        // reaches, so a database operation reaches it too.
        if (GetModuleMangledSet(module, m).Contains(bareFid)) return own;
        if (_dynStore.IsDynamic(bareFid) && IsVisibleGlobalFunctor(bareFid)) return bareFid;
        return own;
    }

    /// <summary>A clause body whose goals resolve in <paramref name="module"/>
    /// wherever the clause compiles (ADR-055): for a clause asserted with a
    /// qualified head from another context.</summary>
    internal Shumway.Compiler.Ast.Term QualifyBodyIn(Shumway.Compiler.Ast.Term body, string module)
        => Consults.QualifyBody(body, module);

    /// <summary>A module that exists only because something named it:
    /// <c>assertz(z:q(1))</c> (ADR-038, amendment) or a clause head
    /// <c>z:q(1).</c> (ADR-055). It is of the modern world.</summary>
    internal ModuleManifest CreateModuleByName(string module)
    {
        var m = new ModuleManifest(module)
        {
            IsExportQualified = true,
            OwnsDynamics = !_flags.ArityCompat,
        };
        _modules[module] = m;
        _staticHeadFunctorsCache = null;
        InvalidatePersistent();
        return m;
    }

    /// <summary>What a module sees of the global namespace under a bare name
    /// (ADR-038 section 1 step 3): the prelude, the libraries loaded into the
    /// global module, the global hooks, the builtins and the <c>:- public</c>
    /// predicates. A call to anything else resolves in the module.</summary>
    internal bool IsVisibleGlobalFunctor(int fid)
    {
        if (_preludeFunctors.Contains(fid) || _libraryFunctors.Contains(fid)) return true;
        if (IsGlobalHookFunctor(fid)) return true;
        if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(fid, out _)) return true;
        foreach (var m in _modules.Values)
            if (m.PublicFunctors.Contains(fid)) return true;
        return false;
    }

    /// <summary>A rewrite context for the clauses of <paramref name="module"/>:
    /// the global dynamic set, the meta-argument specs and, for a module that
    /// owns its dynamics, the view of the global namespace it resolves
    /// unknown names against.</summary>
    internal ModuleRewrite.Context NewRewriteContext(string module, HashSet<int> locals,
        IReadOnlyDictionary<int, string> imports, bool ownsDynamics,
        bool qualifiedResolver = false)
        => new ModuleRewrite.Context(module, locals, _dynStore.Functors, imports)
        {
            OwnsDynamics = ownsDynamics,
            IsVisibleGlobal = ownsDynamics ? IsVisibleGlobalFunctor : null,
            SourceOwnsDynamics = ownsDynamics ? ModuleOwnsDynamics : null,
            // Live: a predicate asserted into the module after this context
            // was made is its own from then on.
            IsOwnDynamic = ownsDynamics ? fid => _dynStore.IsDynamic(MangleFunctor(module, fid)) : null,
            MetaArgSpec = MetaArgSpec,
            QualifiedStaticResolver = qualifiedResolver ? ResolveQualifiedStatic : null,
        };

    private readonly Dictionary<string, ModuleRewrite.Context> _ownedDynamicContexts = new();

    /// <summary>The context the clauses of a modern module's dynamic predicate
    /// compile in, or null when <paramref name="fid"/> is not one: the
    /// module's own, with the locals its static transform uses, so a clause
    /// asserted into the module reaches its private predicates. One per
    /// module while its locals and imports stand.</summary>
    internal ModuleRewrite.Context? OwnedDynamicContextFor(int fid)
    {
        if (!TryDemangleModule(fid, out string module, out _)) return null;
        var m = _modules[module];
        if (!m.OwnsDynamics) return null;
        HashSet<int> locals = _moduleTransformCache.TryGetValue(module, out var mte)
            ? mte.Locals
            : GetModuleMangledSet(module, m);
        if (_ownedDynamicContexts.TryGetValue(module, out var ctx)
            && ReferenceEquals(ctx.LocalFunctors, locals)
            && ReferenceEquals(ctx.Imports, m.Imports))
            return ctx;
        ctx = NewRewriteContext(module, locals, m.Imports, ownsDynamics: true, qualifiedResolver: true);
        _ownedDynamicContexts[module] = ctx;
        return ctx;
    }

    /// <summary>The bare functors of the dynamic predicates a modern module
    /// owns: the store's keys under its name, the implicit marks aside.</summary>
    internal IEnumerable<int> OwnDynamicFunctorsOf(string module)
    {
        int moduleAtom = AtomTable.Intern(module, permanent: true).Id;
        foreach (int fid in _dynStore.Functors)
        {
            if (_dynStore.IsImplicitOnly(fid)) continue;
            if (!ModuleQualify.TryDemangle(fid, out int mAtom, out int bare) || mAtom != moduleAtom) continue;
            yield return bare;
        }
    }

    /// <summary>True when a modern module owns the dynamic predicate
    /// <paramref name="bareFid"/>: declared, routed from its source or
    /// asserted into it.</summary>
    internal bool ModuleOwnsDynamicFunctor(string module, int bareFid)
    {
        if (!ModuleOwnsDynamics(module)) return false;
        int own = MangleFunctor(module, bareFid);
        return _dynStore.IsDynamic(own) && !_dynStore.IsImplicitOnly(own);
    }

    /// <summary>ADR-038 (amendment) point 7: a call compiled in a modern
    /// module to a name it could not resolve carries <c>Module$name</c>; when
    /// the address map has no such predicate at run time, the module's
    /// imports and then the global view are tried. The linked address, or
    /// -1.</summary>
    internal int ResolveModuleFallback(Activation engine, int fid)
    {
        if (engine.CurrentFunctorAddresses is not { } map) return -1;
        if (!TryDemangleModule(fid, out string module, out int bare)) return -1;
        var m = _modules[module];
        if (!m.OwnsDynamics) return -1;
        if (m.Imports.TryGetValue(bare, out string? src))
        {
            int target = ModuleOwnsDynamics(src) || !_dynStore.IsDynamic(bare)
                ? MangleFunctor(src, bare) : bare;
            if (map.TryGetValue(target, out int addr) && !CallTarget.IsUnresolved(addr))
            {
                Activation.CountModuleFallbackResolution();
                return addr;
            }
        }
        if (IsVisibleGlobalFunctor(bare)
            && map.TryGetValue(bare, out int global) && !CallTarget.IsUnresolved(global))
        {
            Activation.CountModuleFallbackResolution();
            return global;
        }
        return -1;
    }
}
