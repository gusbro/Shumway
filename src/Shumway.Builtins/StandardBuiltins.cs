namespace Shumway.Builtins;

/// <summary>
/// Bootstrap for the standard Shumway builtins. <see cref="EnsureRegistered"/>
/// is idempotent — repeated calls have no effect after the first — so callers
/// (typically the <c>PrologEngine</c> constructor) can invoke it unconditionally
/// without worrying about double-registration. Builtins themselves are
/// registered through <see cref="BuiltinsRegistry"/>, which deduplicates by
/// functor.
///
/// <para>Phase 1 currently registers the four unification-comparison
/// predicates (<c>=/2</c>, <c>\=/2</c>, <c>==/2</c>, <c>\==/2</c>). Future
/// chunks will add arithmetic, type tests, I/O, and so on under the same
/// bootstrap.</para>
/// </summary>
public static class StandardBuiltins
{
    private static int _initialized;

    public static void EnsureRegistered()
    {
        // Cheap lock-free guard: the registry itself is thread-safe, but we
        // don't want every call to walk all registrations.
        if (System.Threading.Interlocked.Exchange(ref _initialized, 1) != 0)
            return;

        BuiltinsRegistry.Register("=",   2, UnifyBuiltins.Unify);
        BuiltinsRegistry.Register("\\=", 2, UnifyBuiltins.NotUnifiable);
        BuiltinsRegistry.Register("==",  2, UnifyBuiltins.StructurallyEqual);
        BuiltinsRegistry.Register("\\==",2, UnifyBuiltins.StructurallyNotEqual);
    }
}
