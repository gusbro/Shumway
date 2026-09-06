using System.Diagnostics.CodeAnalysis;

namespace Shumway.Core;

/// <summary>
/// What the host runtime lets the engine do at runtime. Tier-1 (IL emission) and
/// the IL-introspection helpers that feed it are optional: the bytecode
/// interpreter answers every query without them.
/// </summary>
public static class RuntimeCaps
{
    /// <summary>True when the engine may emit and introspect IL at runtime —
    /// <c>System.Reflection.Emit</c> for Tier-1 compilation, and
    /// <c>MethodBody.GetILAsByteArray()</c> for the backtrackable-builtin scan.
    ///
    /// <para><b>Not</b> just <see cref="System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported"/>.
    /// That flag is false under Native AOT (correctly), but **true** under Mono on
    /// <c>browser-wasm</c> — where both APIs nevertheless throw
    /// <c>PlatformNotSupportedException</c>. Taking the flag at face value there
    /// makes the first query die inside builtin classification, long before any
    /// promotion threshold. The browser is therefore excluded explicitly: it runs
    /// Tier-0, exactly as AOT does.</para>
    ///
    /// <para>A <b>feature switch</b>, so a host that will never emit IL can have the
    /// trimmer fold this to a constant and delete the whole Tier-1 subtree — the IL
    /// compiler and its Sigil dependency included. Set it in the consuming project:
    /// <code>&lt;RuntimeHostConfigurationOption Include="Shumway.RuntimeCodegen"
    ///     Value="false" Trim="true" /&gt;</code>
    /// Nothing changes for a normal build: the getter stays an ordinary check, and
    /// both operands are JIT intrinsics that fold to a constant anyway, so reading
    /// it costs nothing. Callers must consult THIS property rather than caching it
    /// in a static field, or the trimmer has nothing to fold and the subtree
    /// survives.</para></summary>
    [FeatureSwitchDefinition("Shumway.RuntimeCodegen")]
    public static bool SupportsRuntimeCodegen =>
#if NETFRAMEWORK
        // .NET Framework has neither of those APIs, and needs neither: it always
        // JITs, there is no trimmer to fold this, and it does not run in a
        // browser. The one runtime where the answer is a constant by nature.
        true;
#else
        System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported
        && !OperatingSystem.IsBrowser();
#endif

    /// <summary>True when the engine may compile predicates to WebAssembly
    /// modules and run them natively — the browser's Tier-1
    /// (docs/design/wasm-tier1-plan.md), where IL emission is unavailable but
    /// the host IS a wasm engine. Default false everywhere: only Shumway.Web
    /// turns the switch on, and desktop builds trim the whole
    /// Shumway.Compiler.Wasm subtree (plan D7). Same discipline as
    /// <see cref="SupportsRuntimeCodegen"/>: consult the property, never cache
    /// it, so the trimmer can fold it.</summary>
    [FeatureSwitchDefinition("Shumway.WasmCodegen")]
    public static bool SupportsWasmCodegen =>
        AppContext.TryGetSwitch("Shumway.WasmCodegen", out bool enabled) && enabled;

    /// <summary>The largest arity a compound term may have, which is a
    /// question about ADDRESS SPACE and so about the runtime. A term of arity
    /// N occupies N+1 heap cells of eight bytes, so the number is whatever
    /// term size the host can be expected to hold: 4 GiB where addresses are
    /// 64 bits, and 128 MiB where they are 32 bits, which is a browser and a
    /// 32-bit host. Promising the 64-bit figure everywhere would be promising
    /// a term larger than the whole address space the browser has.
    ///
    /// <para>It is a limit rather than a guarantee: the term still has to fit
    /// in memory the host will actually give. What it says is what cannot be
    /// built at all, which is what <c>current_prolog_flag(max_arity, _)</c> is
    /// asked for.</para></summary>
    public static int MaxArity => System.IntPtr.Size >= 8
        ? (1 << 29) - 1     // 4 GiB
        : (1 << 24) - 1;    // 128 MiB
}
