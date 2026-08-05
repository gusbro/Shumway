using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Shumway.Compiler.Ast;

namespace Shumway.Embedding;

/// <summary>
/// composite-type term converters: collections,
/// tuples, key-value pairs, nullables, dictionaries. The scalar
/// conversions live in <see cref="TermConverters"/>; this file
/// handles the structurally-recursive mappings that need to call
/// back into <see cref="PrologEngine.ToTerm{T}"/> /
/// <see cref="PrologEngine.FromTerm{T}"/> per element.
///
/// <para>The mappings:</para>
/// <list type="bullet">
/// <item><c>T[]</c>, <c>List&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>,
///   <c>IEnumerable&lt;T&gt;</c> ↔ Prolog cons list
///   <c>.(H, .(H, ..., []))</c>;</item>
/// <item><c>Tuple&lt;T1,T2&gt;</c>, <c>ValueTuple&lt;T1,T2&gt;</c>,
///   <c>KeyValuePair&lt;K,V&gt;</c> ↔ pair compound <c>-(A, B)</c>
///   (the same <c>-</c>/2 operator <c>pairs_keys_values</c> /
///   <c>keysort</c> already use);</item>
/// <item><c>Nullable&lt;T&gt;</c> ↔ atom <c>none</c> when null,
///   compound <c>some(T)</c> when present;</item>
/// <item><c>Dictionary&lt;K,V&gt;</c> ↔ list of <c>-(K, V)</c>
///   pairs.</item>
/// </list>
///
/// <para>Dispatch is by <see cref="Type.GetGenericTypeDefinition"/>
/// equality. Each recursive call goes back through the engine's
/// <see cref="PrologEngine.ToTermDynamic"/> /
/// <see cref="PrologEngine.FromTermDynamic"/> so user-registered
/// per-element converters apply uniformly inside a composite.</para>
/// </summary>
internal static class CompositeConverters
{
    /// <summary>The one place trimming is genuinely lossy here, recorded rather
    /// than papered over. A composite's ELEMENT types are discovered at runtime
    /// (<c>GetGenericArguments</c> / <c>GetElementType</c>), which the trimmer
    /// cannot follow, so the annotation chain that keeps a type's generated
    /// <c>ToPrologTerm</c> / <c>FromPrologTerm</c> alive stops at the composite.
    /// Consequence for a trimmed application: converting a <c>List&lt;MyType&gt;</c>
    /// of <c>[PrologTerm]</c> types may find those methods trimmed and decline the
    /// conversion. Rooting the element type (a direct <c>ToTerm&lt;MyType&gt;</c>
    /// call, or <c>[DynamicDependency]</c>) restores it. Top-level and scalar
    /// conversions are annotated and unaffected.</summary>
    private const string ElementTypeLimitation =
        "Composite element types are resolved at runtime and cannot be tracked; a "
        + "trimmed app must root the element types of [PrologTerm] collections "
        + "itself. See ElementTypeLimitation.";

    // The reflection below never touches a user type's members: it reads Item1 /
    // Item2 / Key / Value off closed FRAMEWORK generics (Tuple<,>, ValueTuple<,>,
    // KeyValuePair<,>) and constructs List<>/Dictionary<,>/Nullable<>, all reached
    // only after matching the open type against a typeof(...) literal. Those
    // members belong to types the application itself constructs to make the call,
    // so they are rooted independently of this tier. Annotating T instead would
    // preserve fields and properties on every user type ever converted, to keep
    // members that are not the user's.
    [UnconditionalSuppressMessage("Trimming", "IL2090",
        Justification = "Reflects only over closed framework generics matched by "
        + "typeof literal; their members are rooted by the caller constructing the "
        + "value it passes in.")]
    [UnconditionalSuppressMessage("Trimming", "IL2087",
        Justification = "Same: the constructed types are framework collections "
        + "matched by typeof literal, not caller-supplied types.")]
    [UnconditionalSuppressMessage("Trimming", "IL2062",
        Justification = ElementTypeLimitation)]
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = ElementTypeLimitation)]
    public static bool TryToTerm<T>(PrologEngine engine, T value, out Term result)
    {
        var type = typeof(T);

        // Nullable<T>: ToTerm sees T = Nullable<TInner> here; the
        // generic boxing happens in the (object)value cast. A null
        // value of a reference type with no built-in entry would
        // arrive here too — treat that as `none` for symmetry.
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            if (value is null)
            {
                result = new AtomTerm("none");
                return true;
            }
            var inner = type.GetGenericArguments()[0];
            var payload = engine.ToTermDynamic(inner, value);
            result = new CompoundTerm("some", new Term[] { payload });
            return true;
        }

        // Arrays — every element through the engine's dispatcher.
        if (type.IsArray)
        {
            if (value is null) { result = new AtomTerm("[]"); return true; }
            var elementType = type.GetElementType()!;
            result = BuildList(engine, elementType, (IEnumerable)value);
            return true;
        }

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();

            if (def == typeof(List<>) || def == typeof(IList<>)
                || def == typeof(IEnumerable<>) || def == typeof(IReadOnlyList<>)
                || def == typeof(ICollection<>) || def == typeof(IReadOnlyCollection<>))
            {
                if (value is null) { result = new AtomTerm("[]"); return true; }
                result = BuildList(engine, args[0], (IEnumerable)value);
                return true;
            }

            if (def == typeof(Tuple<,>) || def == typeof(ValueTuple<,>))
            {
                // Reflectively read .Item1 / .Item2 — works for both
                // System.Tuple (class) and System.ValueTuple (struct).
                var item1 = type.GetField("Item1")?.GetValue(value)
                    ?? type.GetProperty("Item1")!.GetValue(value);
                var item2 = type.GetField("Item2")?.GetValue(value)
                    ?? type.GetProperty("Item2")!.GetValue(value);
                result = new CompoundTerm("-", new Term[]
                {
                    engine.ToTermDynamic(args[0], item1),
                    engine.ToTermDynamic(args[1], item2),
                });
                return true;
            }

            if (def == typeof(KeyValuePair<,>))
            {
                var key = type.GetProperty("Key")!.GetValue(value);
                var val = type.GetProperty("Value")!.GetValue(value);
                result = new CompoundTerm("-", new Term[]
                {
                    engine.ToTermDynamic(args[0], key),
                    engine.ToTermDynamic(args[1], val),
                });
                return true;
            }

            if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>)
                || def == typeof(IReadOnlyDictionary<,>))
            {
                if (value is null) { result = new AtomTerm("[]"); return true; }
                // Build list-of-pairs by routing each KVP through ToTermDynamic
                // with KeyValuePair<K,V> as the element type — that hits the
                // pair handler above and naturally honours any user converter
                // registered on KeyValuePair.
                var kvpType = typeof(KeyValuePair<,>).MakeGenericType(args);
                result = BuildList(engine, kvpType, (IEnumerable)value);
                return true;
            }
        }

        result = null!;
        return false;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2087",
        Justification = "Activator.CreateInstance is called only on framework "
        + "collection types (List<>, Dictionary<,>) matched by typeof literal, "
        + "whose constructors the caller's own use already roots.")]
    [UnconditionalSuppressMessage("Trimming", "IL2090",
        Justification = "See TryToTerm: framework generics only.")]
    [UnconditionalSuppressMessage("Trimming", "IL2062",
        Justification = ElementTypeLimitation)]
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = ElementTypeLimitation)]
    public static bool TryFromTerm<T>(PrologEngine engine, Term term, out T result)
    {
        var type = typeof(T);

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            if (term is AtomTerm { Name: "none" })
            {
                result = default!;        // Nullable<TInner> default is null
                return true;
            }
            if (term is CompoundTerm { Functor: "some", Args.Length: 1 } sc)
            {
                var inner = type.GetGenericArguments()[0];
                var payload = engine.FromTermDynamic(inner, sc.Args[0]);
                result = (T)payload!;
                return true;
            }
            throw new InvalidCastException(
                $"FromTerm<{type.Name}> expects 'none' or 'some(_)', got {term}.");
        }

        if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
            var items = ReadList(engine, elementType, term);
            var arr = Array.CreateInstance(elementType, items.Count);
            for (int i = 0; i < items.Count; i++) arr.SetValue(items[i], i);
            result = (T)(object)arr;
            return true;
        }

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();

            if (def == typeof(List<>) || def == typeof(IList<>)
                || def == typeof(IEnumerable<>) || def == typeof(IReadOnlyList<>)
                || def == typeof(ICollection<>) || def == typeof(IReadOnlyCollection<>))
            {
                var listType = typeof(List<>).MakeGenericType(args[0]);
                var list = (IList)Activator.CreateInstance(listType)!;
                foreach (var item in ReadList(engine, args[0], term))
                    list.Add(item);
                result = (T)list;
                return true;
            }

            if (def == typeof(Tuple<,>))
            {
                if (term is not CompoundTerm { Functor: "-", Args.Length: 2 } pc)
                    throw new InvalidCastException(
                        $"FromTerm<Tuple<,>> expects '-(A,B)', got {term}.");
                var a = engine.FromTermDynamic(args[0], pc.Args[0]);
                var b = engine.FromTermDynamic(args[1], pc.Args[1]);
                result = (T)Activator.CreateInstance(type, a, b)!;
                return true;
            }

            if (def == typeof(ValueTuple<,>))
            {
                if (term is not CompoundTerm { Functor: "-", Args.Length: 2 } pc)
                    throw new InvalidCastException(
                        $"FromTerm<(,)> expects '-(A,B)', got {term}.");
                var a = engine.FromTermDynamic(args[0], pc.Args[0]);
                var b = engine.FromTermDynamic(args[1], pc.Args[1]);
                result = (T)Activator.CreateInstance(type, a, b)!;
                return true;
            }

            if (def == typeof(KeyValuePair<,>))
            {
                if (term is not CompoundTerm { Functor: "-", Args.Length: 2 } pc)
                    throw new InvalidCastException(
                        $"FromTerm<KeyValuePair<,>> expects '-(K,V)', got {term}.");
                var k = engine.FromTermDynamic(args[0], pc.Args[0]);
                var v = engine.FromTermDynamic(args[1], pc.Args[1]);
                result = (T)Activator.CreateInstance(type, k, v)!;
                return true;
            }

            if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>)
                || def == typeof(IReadOnlyDictionary<,>))
            {
                var dictType = typeof(Dictionary<,>).MakeGenericType(args);
                var dict = (IDictionary)Activator.CreateInstance(dictType)!;
                var kvpType = typeof(KeyValuePair<,>).MakeGenericType(args);
                foreach (var kvp in ReadList(engine, kvpType, term))
                {
                    var key = kvpType.GetProperty("Key")!.GetValue(kvp);
                    var val = kvpType.GetProperty("Value")!.GetValue(kvp);
                    dict[key!] = val;
                }
                result = (T)dict;
                return true;
            }
        }

        result = default!;
        return false;
    }

    /// <summary>Builds a Prolog cons list (right-associated under
    /// <c>./2</c>, terminated by the atom <c>[]</c>) from any
    /// .NET enumerable. Each element is routed through the engine's
    /// dynamic dispatcher so user converters apply at every depth.</summary>
    private static Term BuildList(PrologEngine engine,
        [DynamicallyAccessedMembers(ConventionConverters.ConventionMembers)] Type elementType,
        IEnumerable items)
    {
        var elements = new List<Term>();
        foreach (var item in items)
            elements.Add(engine.ToTermDynamic(elementType, item));
        Term tail = new AtomTerm("[]");
        for (int i = elements.Count - 1; i >= 0; i--)
            tail = new CompoundTerm(".", new Term[] { elements[i], tail });
        return tail;
    }

    /// <summary>Walks a Prolog cons list, decoding each element via
    /// the engine's dynamic dispatcher. Throws on an improper list
    /// (the tail must be <c>[]</c>, not a partial list or non-list
    /// term).</summary>
    private static List<object?> ReadList(PrologEngine engine,
        [DynamicallyAccessedMembers(ConventionConverters.ConventionMembers)] Type elementType,
        Term term)
    {
        var result = new List<object?>();
        Term cursor = term;
        while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } c)
        {
            result.Add(engine.FromTermDynamic(elementType, c.Args[0]));
            cursor = c.Args[1];
        }
        if (cursor is not AtomTerm { Name: "[]" })
            throw new InvalidCastException(
                $"Expected a proper Prolog list, got tail {cursor}.");
        return result;
    }
}
