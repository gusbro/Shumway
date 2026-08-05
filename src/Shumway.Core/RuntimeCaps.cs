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
    /// Tier-0, exactly as AOT does.</para></summary>
    public static readonly bool SupportsRuntimeCodegen =
        System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported
        && !OperatingSystem.IsBrowser();
}
