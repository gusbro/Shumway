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

    /// <summary>The largest arity a compound TERM can be represented with —
    /// an ADDRESS-SPACE capacity, not the <c>max_arity</c> flag. A term of
    /// arity N occupies N+1 heap cells of eight bytes, so the cap is the
    /// arity whose term fills 4 GiB where addresses are 64 bits and 128 MiB
    /// where they are 32 (a browser, a 32-bit host).
    ///
    /// <para>The FLAG reports <c>unbounded</c> (issue #106, following
    /// SICStus): terms have no arity limit of their own, only capacity.
    /// Asking past this cap is answered with
    /// <c>resource_error(finite_memory)</c> — checked BEFORE any allocation,
    /// so <c>functor(T, f, 2^29)</c> errors instead of thrashing the host
    /// toward the very limit it is probing.</para></summary>
    public static int MaxArity => System.IntPtr.Size >= 8
        ? (1 << 29) - 1     // 4 GiB
        : (1 << 24) - 1;    // 128 MiB

    /// <summary>The largest arity a PREDICATE (procedure) may be defined
    /// with — the <c>max_procedure_arity</c> flag of stc#70, defined exactly
    /// for processors whose <c>max_arity</c> is <c>unbounded</c>. Terms stay
    /// unbounded; defining a clause, asserting one, or naming an indicator
    /// past this raises
    /// <c>representation_error(max_procedure_arity)</c>. 1023 is generous
    /// (the survey's common value is 255) and costs nothing; it also bounds
    /// the register bank a head can demand.</summary>
    public const int MaxProcedureArity = 1023;
}
