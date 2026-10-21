namespace Shumway.Compiler.Il;

/// <summary>Every limit of the runtime's JIT the IL tier plans against, in
/// one place: the sizes past which the JIT compiles a method without
/// optimization (no inlining, no register allocation, every accessor a
/// call), and the measures of its inlining. They are RyuJIT's own
/// constants, read from the JIT of .NET <see cref="RuntimeMajor"/>
/// (dotnet/runtime, branch release/10.0) and written here by hand:
/// <list type="bullet">
/// <item>The five MinOpts switches, <c>src/coreclr/jit/compiler.h</c>
/// (<c>DEFAULT_MIN_OPTS_CODE_SIZE</c>, <c>_INSTR_COUNT</c>, <c>_BB_COUNT</c>,
/// <c>_LV_NUM_COUNT</c>, <c>_LV_REF_COUNT</c>), tested in
/// <c>Compiler::compSetOptimizationLevel</c> (<c>compiler.cpp</c>) against
/// the IL as imported, before any inlining: its bytes, its opcodes, the
/// basic blocks found, the locals with the arguments, the loads and stores
/// of them. A release JIT has no knob for them (the <c>JitMinOpts*</c>
/// knobs are a checked JIT's); ahead-of-time compilation never takes the
/// switch.</item>
/// <item><c>JitMaxLocalsToTrack</c>, <c>jitconfigvalues.h</c>: the locals
/// the JIT tracks for optimization, 0x400 unless the environment the
/// process started with says otherwise, and the measure of its inlining:
/// it inlines nothing more into a method whose locals, its own and its
/// inlinees' so far, reach nine tenths of it
/// (<c>Compiler::lvaHaveManyLocals(0.9f)</c> at the candidate's check in
/// <c>fginline.cpp</c>, the rule in <c>compiler.hpp</c>).</item>
/// <item><c>DEFAULT_MAX_INLINE_SIZE</c>, <c>compiler.h</c>: a callee of
/// more IL bytes is never inlined; <c>ALWAYS_INLINE_SIZE</c>,
/// <c>inline.h</c>: one of at most that many always is.</item>
/// </list>
/// None is conditioned on the target architecture: the same on x64, x86
/// and Arm64. The JIT's decisions in between are the target's (its size
/// estimates are), which is why the inline table
/// (<see cref="IlInlineTable"/>) is measured on the architecture that runs
/// the tests and records it. Reviewed at every major of the runtime:
/// <c>JitLimitCanaryTests</c> probes each switch against the JIT of the
/// build, <c>IlInlineTableTests</c> the table and its stamp.
/// <c>docs/design/region-jit-limits.md</c> has the links. Local references
/// are the limit a region reaches first, at about 47 KB of IL.</summary>
internal static class IlJitLimits
{
    /// <summary>The major of the runtime whose JIT the constants were read from.</summary>
    public const int RuntimeMajor = 10;

    public const int IlBytes = 60000;
    public const int Instructions = 20000;
    /// <summary>Arguments included.</summary>
    public const int Locals = 2000;
    public const int LocalRefs = 8000;
    /// <summary>As the JIT finds them in the IL, before inlining
    /// (<see cref="IlEmit.BlockCount"/>).</summary>
    public const int BasicBlocks = 2000;

    /// <summary>A callee of more IL bytes is never inlined.</summary>
    public const int MaxInlineIlBytes = 100;
    /// <summary>A callee of at most this many IL bytes is always inlined.</summary>
    public const int AlwaysInlineIlBytes = 16;

    /// <summary>The locals the JIT tracks, arguments and the temporaries of
    /// inlines included, and the measure of its inlining: past
    /// <see cref="InliningStopsAt"/> of them every later call site of the
    /// method stays a call, a field read or a struct constructor among
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

    /// <summary>The fraction of <see cref="InlineLocals"/> at which the JIT
    /// stops inlining into a method (<c>lvaHaveManyLocals(0.9f)</c>).</summary>
    public const double InliningStopsAt = 0.9;

    /// <summary>The locals at which the JIT stops inlining: 921 at the
    /// JIT's own cap. The planner's target (<c>IlRegionCost.LocalsTarget</c>)
    /// is in its own count of a method's locals and fitted to it.</summary>
    public static int InliningCutoff => (int)(InlineLocals * InliningStopsAt);

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
