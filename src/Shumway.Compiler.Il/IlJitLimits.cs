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
    /// <summary>As the JIT finds them in the IL, before inlining
    /// (<see cref="IlEmit.BlockCount"/>).</summary>
    public const int BasicBlocks = 2000;

    /// <summary>The locals, arguments and temporaries included, past which
    /// the JIT inlines nothing more into a method: every later call site of
    /// the method stays a call, a field read or a struct constructor among
    /// them. The method's own locals count from the start and every inline
    /// made adds temporaries. This is the JIT's JitMaxLocalsToTrack, read
    /// here the way the JIT reads it: from the environment the process was
    /// started with (<c>DOTNET_JitMaxLocalsToTrack</c>, then the
    /// <c>COMPlus_</c> prefix), as a hexadecimal number, once. A process
    /// cannot set it for itself: the JIT has read it before <c>Main</c>
    /// runs. It is not a MinOpts switch, so <see cref="Exceeded"/> does not
    /// test it.</summary>
    public static readonly int InlineLocals = JitLocalsCap(
        Environment.GetEnvironmentVariable("DOTNET_JitMaxLocalsToTrack")
        ?? Environment.GetEnvironmentVariable("COMPlus_JitMaxLocalsToTrack"));

    public const int DefaultInlineLocals = 0x400;

    /// <summary>The cap the JIT takes from <paramref name="value"/>: the
    /// leading hexadecimal digits after an optional <c>0x</c>, as the
    /// runtime parses its integer knobs (<c>3072</c> reads as 0x3072);
    /// none, or zero, is the default.</summary>
    public static int JitLocalsCap(string? value)
    {
        if (value is null) return DefaultInlineLocals;
        int i = 0, n = value.Length;
        while (i < n && char.IsWhiteSpace(value[i])) i++;
        if (i + 1 < n && value[i] == '0' && (value[i + 1] == 'x' || value[i + 1] == 'X')) i += 2;
        long cap = 0;
        int digits = 0;
        for (; i < n && digits < 8; i++, digits++)
        {
            int d = Uri.IsHexDigit(value[i]) ? Convert.ToInt32(value[i].ToString(), 16) : -1;
            if (d < 0) break;
            cap = cap * 16 + d;
        }
        return digits == 0 || cap == 0 ? DefaultInlineLocals : (int)Math.Min(cap, int.MaxValue / 2);
    }

    /// <summary>Whether the method as emitted so far is past any limit;
    /// <paramref name="what"/> names the first one.</summary>
    public static bool Exceeded(IlEmit emit, out string what)
    {
        if (emit.LocalRefCount > LocalRefs) { what = $"local refs {emit.LocalRefCount} > {LocalRefs}"; return true; }
        if (emit.Size > IlBytes) { what = $"IL bytes {emit.Size} > {IlBytes}"; return true; }
        if (emit.InstructionCount > Instructions) { what = $"instructions {emit.InstructionCount} > {Instructions}"; return true; }
        if (emit.LocalCount + 2 > Locals) { what = $"locals {emit.LocalCount + 2} > {Locals}"; return true; }
        if (emit.BlockCount > BasicBlocks) { what = $"basic blocks {emit.BlockCount} > {BasicBlocks}"; return true; }
        what = "";
        return false;
    }
}
