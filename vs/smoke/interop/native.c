/* ADR-035 D4 E2E -- the native end of the mixed stack.
 *
 * Real C, in a real DLL, reached by P/Invoke from the C# foreign predicate, which is reached
 * from Prolog. Three languages, one call chain: that is the thing the debugger has to be able
 * to show in one stack. */

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT
#endif

EXPORT int native_scale(int value, int factor)
{
    return value * factor;
}
