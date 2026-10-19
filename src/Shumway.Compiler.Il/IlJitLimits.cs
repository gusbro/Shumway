namespace Shumway.Compiler.Il;

/// <summary>The sizes past which the runtime's JIT compiles a method without
/// optimization: no inlining, no register allocation, every accessor a call.
/// They are the JIT's own constants (its switch to MinOpts, tested at import
/// against the IL as emitted); the knobs that raise them exist only in checked
/// JITs. Local references are the one a region reaches first, at about 47 KB
/// of IL, well before the byte limit.</summary>
internal static class IlJitLimits
{
    public const int IlBytes = 60000;
    public const int Instructions = 20000;
    /// <summary>Arguments included.</summary>
    public const int Locals = 2000;
    public const int LocalRefs = 8000;

    /// <summary>Whether the method as emitted so far is past any limit;
    /// <paramref name="what"/> names the first one.</summary>
    public static bool Exceeded(IlEmit emit, out string what)
    {
        if (emit.LocalRefCount > LocalRefs) { what = $"local refs {emit.LocalRefCount} > {LocalRefs}"; return true; }
        if (emit.Size > IlBytes) { what = $"IL bytes {emit.Size} > {IlBytes}"; return true; }
        if (emit.InstructionCount > Instructions) { what = $"instructions {emit.InstructionCount} > {Instructions}"; return true; }
        if (emit.LocalCount + 2 > Locals) { what = $"locals {emit.LocalCount + 2} > {Locals}"; return true; }
        what = "";
        return false;
    }
}
