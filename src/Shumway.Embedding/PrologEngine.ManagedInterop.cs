using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Interpreter;

namespace Shumway.Embedding;

public sealed partial class PrologEngine
{
    /// <summary>registers every method on
    /// <paramref name="instance"/> that carries a
    /// <see cref="PrologPredicateAttribute"/>, binding it as a
    /// foreign Prolog predicate. The method's C# signature must be
    /// <c>bool Method(Activation engine)</c>; instance methods capture
    /// <paramref name="instance"/> into the registered delegate, so
    /// the instance stays alive for as long as any engine has the
    /// predicate registered.
    ///
    /// <para>Throws <see cref="InvalidOperationException"/> when a
    /// decorated method has the wrong signature, or when the
    /// resulting <c>Name/Arity</c> collides with a previously
    /// registered builtin (the existing builtin would silently win
    /// otherwise — confusing during development).</para></summary>
    /// <summary>per-engine custom term converters
    /// registered via <see cref="RegisterConverter{T}"/>. Lazily
    /// allocated; null when the engine only uses the built-in
    /// scalar mappings.</summary>
    private Dictionary<Type, (object ToTerm, object FromTerm)>? _userConverters;

    /// <summary>register a custom C#-type ↔ Prolog-term
    /// pair for <typeparamref name="T"/>. Takes precedence over the
    /// built-in scalar conversions (so a host can override the
    /// default <c>string</c> → <see cref="StringTerm"/> mapping with,
    /// say, <see cref="AtomTerm"/> semantics). Replaces any prior
    /// registration for the same type.</summary>
    public void RegisterConverter<T>(
        Func<PrologEngine, T, Term> toTerm, Func<Term, T> fromTerm)
    {
        ArgumentNullException.ThrowIfNull(toTerm);
        ArgumentNullException.ThrowIfNull(fromTerm);
        _userConverters ??= new();
        _userConverters[typeof(T)] = (toTerm, fromTerm);
    }

    /// <summary>converts <paramref name="value"/> to a
    /// Prolog <see cref="Term"/>. Resolution order: a user converter
    /// for <typeparamref name="T"/> if registered, then the built-in
    /// scalar mapping (<see cref="TermConverters"/>). Throws
    /// <see cref="InvalidOperationException"/> when neither covers
    /// the type — the diagnostic names the type so the host can
    /// register a converter.</summary>
    public Term ToTerm<[DynamicallyAccessedMembers(ConventionConverters.ConventionMembers)] T>(
        T value)
    {
        if (_userConverters is not null
            && _userConverters.TryGetValue(typeof(T), out var pair))
        {
            return ((Func<PrologEngine, T, Term>)pair.ToTerm)(this, value);
        }
        if (TermConverters.TryToTerm<T>(value, out Term result))
            return result;
        if (CompositeConverters.TryToTerm<T>(this, value, out result))
            return result;
        // convention discovery — a generator-emitted (or
        // hand-written) ToPrologTerm(engine) on T.
        if (ConventionConverters.TryToTerm<T>(this, value, out result))
            return result;
        throw new InvalidOperationException(
            $"No term converter registered for type '{typeof(T).FullName}'. "
            + "Register one with engine.RegisterConverter<T>(toTerm, fromTerm), "
            + "or add [PrologTerm] to a partial type to get generated converters.");
    }

    /// <summary>inverse of <see cref="ToTerm{T}"/>:
    /// extracts a <typeparamref name="T"/> from <paramref name="term"/>.
    /// User converters win over the built-in mappings.</summary>
    public T FromTerm<[DynamicallyAccessedMembers(ConventionConverters.ConventionMembers)] T>(
        Term term)
    {
        ArgumentNullException.ThrowIfNull(term);
        if (_userConverters is not null
            && _userConverters.TryGetValue(typeof(T), out var pair))
        {
            return ((Func<Term, T>)pair.FromTerm)(term);
        }
        if (TermConverters.TryFromTerm<T>(term, out T result))
            return result;
        if (CompositeConverters.TryFromTerm<T>(this, term, out result))
            return result;
        if (ConventionConverters.TryFromTerm<T>(this, term, out result))
            return result;
        throw new InvalidOperationException(
            $"No term converter registered for type '{typeof(T).FullName}'. "
            + "Register one with engine.RegisterConverter<T>(toTerm, fromTerm), "
            + "or add [PrologTerm] to a partial type to get generated converters.");
    }

    /// <summary>reflective bridge: invoke
    /// <see cref="ToTerm{T}"/> when the element type is only known
    /// at runtime (the path the collection / tuple / nullable /
    /// dictionary handlers take to recurse into element types).
    /// The generic method handle is built and cached on first use
    /// per element type; subsequent calls are a dictionary probe +
    /// delegate invoke.</summary>
    internal Term ToTermDynamic(
        [DynamicallyAccessedMembers(ConventionConverters.ConventionMembers)] Type type,
        object? value)
    {
        // the cached delegate is now COMPILED (engine.ToTerm<T>((T)v))
        // instead of a wrapper that re-ran MethodInfo.Invoke + a fresh object[] per
        // ELEMENT of every converted collection. Expression.Compile interprets
        // under Native AOT, so this stays AOT-correct.
        // Built through an annotated helper rather than GetOrAdd's factory lambda,
        // whose Type parameter carries no annotation and would lose the trimmer's
        // guarantee that T's convention methods survive.
        if (!_toTermDynamicCache.TryGetValue(type, out var del))
            del = _toTermDynamicCache.GetOrAdd(type, BuildToTermDelegate(type));
        return del(this, value);
    }

    private static Func<PrologEngine, object?, Term> BuildToTermDelegate(
        [DynamicallyAccessedMembers(ConventionConverters.ConventionMembers)] Type t)
    {
        var m = typeof(PrologEngine).GetMethod(nameof(ToTerm))!.MakeGenericMethod(t);
        var engP = System.Linq.Expressions.Expression.Parameter(typeof(PrologEngine), "engine");
        var valP = System.Linq.Expressions.Expression.Parameter(typeof(object), "value");
        return System.Linq.Expressions.Expression.Lambda<Func<PrologEngine, object?, Term>>(
            System.Linq.Expressions.Expression.Call(engP, m,
                System.Linq.Expressions.Expression.Convert(valP, t)),
            engP, valP).Compile();
    }

    /// <summary>reflective bridge for the inverse
    /// direction; same caching strategy as
    /// <see cref="ToTermDynamic"/>.</summary>
    internal object? FromTermDynamic(
        [DynamicallyAccessedMembers(ConventionConverters.ConventionMembers)] Type type,
        Term term)
    {
        if (!_fromTermDynamicCache.TryGetValue(type, out var del))
            del = _fromTermDynamicCache.GetOrAdd(type, BuildFromTermDelegate(type));
        return del(this, term);
    }

    private static Func<PrologEngine, Term, object?> BuildFromTermDelegate(
        [DynamicallyAccessedMembers(ConventionConverters.ConventionMembers)] Type t)
    {
        var m = typeof(PrologEngine).GetMethod(nameof(FromTerm))!.MakeGenericMethod(t);
        var engP = System.Linq.Expressions.Expression.Parameter(typeof(PrologEngine), "engine");
        var termP = System.Linq.Expressions.Expression.Parameter(typeof(Term), "term");
        return System.Linq.Expressions.Expression.Lambda<Func<PrologEngine, Term, object?>>(
            System.Linq.Expressions.Expression.Convert(
                System.Linq.Expressions.Expression.Call(engP, m, termP), typeof(object)),
            engP, termP).Compile();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        Type, Func<PrologEngine, object?, Term>> _toTermDynamicCache = new();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        Type, Func<PrologEngine, Term, object?>> _fromTermDynamicCache = new();

    public void RegisterPredicates(object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        RegisterPredicatesImpl(instance.GetType(), instance);
    }

    /// <summary>Static-class overload: discovers and registers every
    /// <c>static</c> method on <paramref name="type"/> annotated
    /// with <see cref="PrologPredicateAttribute"/>. Use for classes
    /// that group stateless predicates (the common case for
    /// embedding-side helpers).</summary>
    public void RegisterPredicates(
        [DynamicallyAccessedMembers(PredicateMembers)] Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        RegisterPredicatesImpl(type, instance: null, staticOnly: false);
    }

    /// <summary>overload — when
    /// <paramref name="staticOnly"/> is true, instance methods
    /// decorated with <c>[PrologPredicate]</c> are silently
    /// skipped instead of throwing. Used by the auto-loader path
    /// that walks every type in a foreign-DLL: instance methods
    /// can't be auto-registered without an instance, but other
    /// (static) methods in the same DLL should still surface.</summary>
    public void RegisterPredicates(
        [DynamicallyAccessedMembers(PredicateMembers)] Type type, bool staticOnly)
    {
        ArgumentNullException.ThrowIfNull(type);
        RegisterPredicatesImpl(type, instance: null, staticOnly: staticOnly);
    }

    /// <summary>Generic convenience: <c>engine.RegisterPredicates&lt;MyClass&gt;()</c>
    /// for static classes.</summary>
    public void RegisterPredicates<[DynamicallyAccessedMembers(PredicateMembers)] T>()
        => RegisterPredicates(typeof(T));

    /// <summary>loads <paramref name="assemblyPath"/>
    /// via <see cref="System.Reflection.Assembly.LoadFrom"/> and
    /// registers every <c>[PrologPredicate]</c>-decorated <c>static</c>
    /// method across all types in the assembly. Instance methods are
    /// skipped (they need a constructed instance the loader doesn't
    /// have). The runtime <see cref="LoadBundle(Bundle)"/> path calls
    /// this for each <see cref="Bundle.ForeignAssemblies"/> entry the
    /// linker recorded.</summary>
    /// <summary>probes for a foreign assembly by name
    /// in the directories the bundle's foreign-DLL convention
    /// inspects: the bundle's own directory first (typical
    /// <c>myapp.shum</c> + <c>MyForeigns.dll</c> layout), then the
    /// executable's base directory (the <c>--exe</c> path that
    /// copies foreign DLLs next to the produced executable), then
    /// the runtime's default <c>Assembly.Load</c> probe path as a
    /// last resort. Returns <c>null</c> if every probe misses; the
    /// caller surfaces a file-not-found.</summary>
    internal static string? ResolveForeignAssemblyPath(string name, string? bundleDir)
    {
        if (bundleDir is not null)
        {
            string candidate = System.IO.Path.Combine(bundleDir, name);
            if (System.IO.File.Exists(candidate)) return candidate;
        }
        string baseDir = AppContext.BaseDirectory;
        string baseCandidate = System.IO.Path.Combine(baseDir, name);
        if (System.IO.File.Exists(baseCandidate)) return baseCandidate;
        // Last resort: Assembly.Load on the bare assembly name (no
        // extension) — the runtime walks its probing paths. Returns
        // a path-less reference if successful; we hand back the
        // assembly's location for symmetry with the file paths above.
        try
        {
            string nameNoExt = System.IO.Path.GetFileNameWithoutExtension(name);
            var asm = System.Reflection.Assembly.Load(nameNoExt);
            return asm.Location;
        }
        catch { return null; }
    }

    [RequiresUnreferencedCode(
        "Loads an assembly from disk and reflects over every type in it. Nothing "
        + "statically references those types, so a trimmed application cannot "
        + "guarantee the foreign assembly's predicates survive; register them with "
        + "RegisterPredicates(typeof(...)) instead, which is annotated.")]
    public void RegisterForeignAssembly(string assemblyPath)
    {
        ArgumentNullException.ThrowIfNull(assemblyPath);
        var asm = System.Reflection.Assembly.LoadFrom(assemblyPath);
        foreach (var type in asm.GetTypes())
        {
            // Cheap pre-filter: skip types with no [PrologPredicate]
            // decoration anywhere. The full RegisterPredicates pass
            // does this check too, but a `false` quick reject avoids
            // the BindingFlags walk per type for the typical case.
            bool hasAttribute = false;
            foreach (var method in type.GetMethods(
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (System.Reflection.CustomAttributeExtensions
                    .GetCustomAttribute<PrologPredicateAttribute>(method) is not null)
                {
                    hasAttribute = true;
                    break;
                }
            }
            if (!hasAttribute) continue;
            RegisterPredicates(type, staticOnly: true);
        }
    }

    // What RegisterPredicates reflects over: it walks the type's methods (public and
    // not) looking for [PrologPredicate], then re-finds the generated bridge by name.
    // Naming the set once keeps the annotations on the overloads honest and identical.
    internal const DynamicallyAccessedMemberTypes PredicateMembers =
        DynamicallyAccessedMemberTypes.PublicMethods
        | DynamicallyAccessedMemberTypes.NonPublicMethods;

    private void RegisterPredicatesImpl(
        [DynamicallyAccessedMembers(PredicateMembers)] Type type,
        object? instance, bool staticOnly = false)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.DeclaredOnly;

        // Walk type + bases so inherited [PrologPredicate] methods
        // are picked up exactly once.
        var seen = new HashSet<System.Reflection.MethodInfo>();
        for (Type? t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (var method in t.GetMethods(flags))
            {
                if (!seen.Add(method)) continue;
                var attr = System.Reflection.CustomAttributeExtensions
                    .GetCustomAttribute<PrologPredicateAttribute>(method);
                if (attr is null) continue;

                if (method.IsStatic == false && instance is null)
                {
                    // the static-only auto-loader path
                    // (foreign-DLL scanning) silently skips instance
                    // methods — there's no instance available, and
                    // failing the whole DLL because one type mixes
                    // static + instance [PrologPredicate]s would
                    // be hostile. The explicit RegisterPredicates
                    // (Type) call still throws.
                    if (staticOnly) continue;
                    throw new InvalidOperationException(
                        $"[PrologPredicate] on instance method '{type.FullName}.{method.Name}' "
                        + "requires RegisterPredicates(instance); call the (Type) / generic overload "
                        + "only for static methods.");
                }

                var parameters = method.GetParameters();
                bool isCanonical = method.ReturnType == typeof(bool)
                    && parameters.Length == 1
                    && parameters[0].ParameterType == typeof(Shumway.Core.Activation);

                System.Reflection.MethodInfo dispatchTarget;
                if (isCanonical)
                {
                    dispatchTarget = method;
                }
                else
                {
                    // typed signature — locate the
                    // generator-emitted bridge in the same type.
                    // Name convention: "_{Method}_PrologBridge". The
                    // bridge has the same staticness as the user
                    // method (so the same CreateDelegate path below
                    // works); RegisterPredicates was already
                    // checking instance / Type-overload mismatches
                    // upstream, so the bridge inherits that check.
                    string bridgeName = "_" + method.Name + "_PrologBridge";
                    var bridge = t.GetMethod(
                        bridgeName,
                        flags,
                        binder: null,
                        types: new[] { typeof(Shumway.Core.Activation) },
                        modifiers: null);
                    if (bridge is null || bridge.ReturnType != typeof(bool))
                    {
                        throw new InvalidOperationException(
                            $"[PrologPredicate] method '{type.FullName}.{method.Name}' has a typed "
                            + "signature but no matching generator-emitted bridge "
                            + $"'{bridgeName}(Shumway.Core.Activation)' was found. Ensure the "
                            + "Shumway.SourceGen analyzer is referenced — "
                            + "<ProjectReference ... OutputItemType=\"Analyzer\" /> — and the build "
                            + "succeeded.");
                    }
                    if (bridge.IsStatic != method.IsStatic)
                    {
                        throw new InvalidOperationException(
                            $"[PrologPredicate] bridge '{bridgeName}' has different staticness than "
                            + $"the user method '{method.Name}'. The generator should have matched it — "
                            + "this likely means a hand-written method named the same as a generator "
                            + "output. Rename the user method.");
                    }
                    dispatchTarget = bridge;
                }

                string name = attr.Name ?? method.Name;
                int arity = attr.Arity;
                var del = (Shumway.Builtins.BuiltinImpl)dispatchTarget.CreateDelegate(
                    typeof(Shumway.Builtins.BuiltinImpl),
                    dispatchTarget.IsStatic ? null : instance);

                // BuiltinsRegistry.Register is idempotent — a second call with
                // the same functor returns the existing id and silently
                // discards the new impl. That's the right behaviour when the
                // same [PrologPredicate] is re-registered (e.g. the same
                // attribute discovered across two engines in one process, or
                // a test re-running with shared static state), but wrong when
                // a *different* implementation tries to use the name —
                // there's no diagnostic and the second impl just never runs.
                // Detect the latter case explicitly: an existing entry whose
                // delegate target+method differ from ours is a real conflict.
                int functorId = Shumway.Core.FunctorTable.Intern(
                    Shumway.Core.AtomTable.Intern(name, permanent: true).Id, arity);
                if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(functorId, out int existingId))
                {
                    var existing = Shumway.Builtins.BuiltinsRegistry.GetById(existingId);
                    if (!ReferenceEquals(existing.Impl.Method, del.Method)
                        || !Equals(existing.Impl.Target, del.Target))
                    {
                        // the static-only auto-loader
                        // path (foreign-DLL scanning) silently skips
                        // collisions for the same reason it skips
                        // instance methods — failing the whole DLL
                        // load because one type's [PrologPredicate]
                        // happens to collide with a standard builtin
                        // would be hostile to the embedder. The
                        // explicit-Type RegisterPredicates call
                        // still throws.
                        if (staticOnly) continue;
                        throw new InvalidOperationException(
                            $"[PrologPredicate] '{name}/{arity}' from '{type.FullName}.{method.Name}' "
                            + "collides with an already-registered builtin. Re-registration would be a "
                            + "no-op and the new implementation would never run — pick a different name/arity.");
                    }
                    // Same method+target — silent no-op, exactly what
                    // BuiltinsRegistry.Register would do anyway.
                    _foreignBuiltinIds[existingId] = 0;
                    continue;
                }

                _foreignBuiltinIds[Shumway.Builtins.BuiltinsRegistry.Register(
                    name, arity, del, attr.Category, attr.Template, attr.Summary)] = 0;
            }
        }
    }

    /// <summary>The builtins that are somebody's C# — a <c>[PrologPredicate]</c> foreign
    /// predicate rather than an implementation of ours. Global, like the registry itself.
    ///
    /// <para>ADR-035 reads it: a foreign call is the one place a debugger can end up stopped
    /// in code the ENGINE is not standing in, and the Prolog stack under that C# is what
    /// makes the stack mixed rather than merely managed. The engine cannot be asked for it
    /// then — it is frozen inside the call — so it publishes it on the way in.</para></summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte>
        _foreignBuiltinIds = new();

    internal static bool IsForeignBuiltin(int builtinId)
        => _foreignBuiltinIds.ContainsKey(builtinId);

}
