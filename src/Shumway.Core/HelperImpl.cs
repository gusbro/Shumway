using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Shumway.Core;

/// <summary>MethodImpl options for the engine methods compiled code inlines.
/// Such a method compiles with full optimization at once and is never
/// instrumented, so it has no dynamic PGO profile, and the JIT lays it out as
/// written wherever it inlines it: the fast path first, the slow path in a
/// call. With a profile, the layout of compiled code followed what the
/// interpreter had run by the time the JIT compiled it (ADR-061, "What a
/// promotion costs"). Public for Shumway's other assemblies.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class HelperImpl
{
    /// <summary>MethodImplOptions.AggressiveOptimization, which .NET
    /// Framework's enum lacks and its runtime ignores.</summary>
    public const MethodImplOptions Fixed = (MethodImplOptions)0x0200;

    public const MethodImplOptions FixedInline = MethodImplOptions.AggressiveInlining | Fixed;
}
