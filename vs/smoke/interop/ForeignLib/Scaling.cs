// ADR-035 D4 E2E -- the C# middle of the mixed stack.
//
// A [PrologPredicate] foreign predicate that Prolog calls, and that itself P/Invokes into
// native C. Stopping anywhere in here should show, in ONE call stack: the C# frames (these),
// the Prolog frames that called them (recomposed by the debugger from the engine's own
// state, since the interpreter's C# frames are not the Prolog stack), and -- with native
// debugging enabled -- the C function underneath.

using System.Runtime.InteropServices;
using Shumway.Embedding;

namespace ForeignLib;

public static partial class Scaling
{
    /// <summary>The native library the launcher loads with --native-dll. Resolved by name:
    /// the DLL sits next to the program.</summary>
    [DllImport("native", EntryPoint = "native_scale", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeScale(int value, int factor);

    /// <summary>scale(+Value, -Scaled) — doubles a number, the long way round: through C#,
    /// and through C.</summary>
    [PrologPredicate("scale/2")]
    public static int Scale(int value)
    {
        int factor = Factor(value);
        return NativeScale(value, factor);
    }

    /// <summary>A second C# frame, so the stack has something to show BETWEEN the foreign
    /// entry point and the native call — a one-frame C# layer would not prove much.</summary>
    private static int Factor(int value) => value < 0 ? -2 : 2;
}
