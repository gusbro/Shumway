using System.Collections.Concurrent;
using System.Reflection;
using Shumway.Compiler.Ast;
using static System.Linq.Expressions.Expression;

namespace Shumway.Embedding;

/// <summary>
/// convention-based dispatch tier. Discovers
/// generator-emitted (or hand-written) <c>ToPrologTerm</c> /
/// <c>FromPrologTerm</c> methods on a type and routes through them.
///
/// <para>The contract:</para>
/// <list type="bullet">
/// <item>Instance method
///   <c>Term ToPrologTerm(PrologEngine engine)</c>
///   — invoked for the encoder direction.</item>
/// <item>Static method
///   <c>static T FromPrologTerm(PrologEngine engine, Term term)</c>
///   — preferred decoder. If absent, falls back to the simpler
///   <c>static T FromPrologTerm(Term term)</c> form (suitable for
///   nullary types that don't need the engine to recurse).</item>
/// </list>
///
/// <para>Reflection cost is amortised — a per-type
/// <see cref="ConcurrentDictionary"/> holds the resolved delegate
/// pair. The cache key is the closed runtime <see cref="Type"/>, so
/// a generic <c>[PrologTerm]</c> type's instantiations cache
/// independently.</para>
/// </summary>
internal static class ConventionConverters
{
    private static readonly ConcurrentDictionary<Type, ConvertersEntry> _cache = new();

    public static bool TryToTerm<T>(PrologEngine engine, T value, out Term result)
    {
        if (value is null) { result = null!; return false; }
        var entry = _cache.GetOrAdd(typeof(T), static t => BuildEntry(t));
        if (entry.ToTerm is null) { result = null!; return false; }
        result = entry.ToTerm(engine, value!);
        return true;
    }

    public static bool TryFromTerm<T>(PrologEngine engine, Term term, out T result)
    {
        var entry = _cache.GetOrAdd(typeof(T), static t => BuildEntry(t));
        if (entry.FromTerm is null) { result = default!; return false; }
        result = (T)entry.FromTerm(engine, term)!;
        return true;
    }

    private static ConvertersEntry BuildEntry(Type type)
    {
        // the resolved MethodInfos are COMPILED to delegates here,
        // once per type, instead of MethodInfo.Invoke (+ a fresh object[]) per
        // conversion (~100× a direct call). Expression.Compile falls back to its
        // interpreter under Native AOT, so this stays AOT-correct. A direct
        // delegate also throws the user's exception unwrapped — no
        // TargetInvocationException translation needed.

        // Encoder: T.ToPrologTerm(PrologEngine) returning Term.
        Func<PrologEngine, object, Term>? toTerm = null;
        var toMethod = type.GetMethod(
            "ToPrologTerm",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: new[] { typeof(PrologEngine) },
            modifiers: null);
        if (toMethod is not null && toMethod.ReturnType == typeof(Term))
        {
            var engP = Parameter(typeof(PrologEngine), "engine");
            var valP = Parameter(typeof(object), "value");
            toTerm = Lambda<Func<PrologEngine, object, Term>>(
                Call(Convert(valP, type), toMethod, engP), engP, valP).Compile();
        }

        // Decoder, preferred: static T FromPrologTerm(PrologEngine, Term).
        Func<PrologEngine, Term, object?>? fromTerm = null;
        var fromMethod2 = type.GetMethod(
            "FromPrologTerm",
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            types: new[] { typeof(PrologEngine), typeof(Term) },
            modifiers: null);
        if (fromMethod2 is not null && fromMethod2.ReturnType == type)
        {
            var engP = Parameter(typeof(PrologEngine), "engine");
            var termP = Parameter(typeof(Term), "term");
            fromTerm = Lambda<Func<PrologEngine, Term, object?>>(
                Convert(Call(fromMethod2, engP, termP), typeof(object)), engP, termP).Compile();
        }

        // Fallback decoder: static T FromPrologTerm(Term) — fine for
        // nullary or engine-free shapes (the generator emits this
        // form when the term has zero args).
        if (fromTerm is null)
        {
            var fromMethod1 = type.GetMethod(
                "FromPrologTerm",
                BindingFlags.Static | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(Term) },
                modifiers: null);
            if (fromMethod1 is not null && fromMethod1.ReturnType == type)
            {
                var engP = Parameter(typeof(PrologEngine), "engine");
                var termP = Parameter(typeof(Term), "term");
                fromTerm = Lambda<Func<PrologEngine, Term, object?>>(
                    Convert(Call(fromMethod1, termP), typeof(object)), engP, termP).Compile();
            }
        }

        return new ConvertersEntry(toTerm, fromTerm);
    }

    private readonly record struct ConvertersEntry(
        Func<PrologEngine, object, Term>? ToTerm,
        Func<PrologEngine, Term, object?>? FromTerm);
}
