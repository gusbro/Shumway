using System.Reflection;
using System.Reflection.Emit;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Compiler.Il;

/// <summary>
/// Chunk 71 — emits a persisted .NET assembly (.dll bytes) holding the
/// IL for every IL-promotable predicate in a set of compiled modules.
/// Counterpart to the runtime
/// <see cref="IlPredicateCompiler"/> which targets
/// <c>DynamicMethod</c>; this one targets <c>MethodBuilder</c> via
/// <see cref="PersistedAssemblyBuilder"/> so the IL lives in a .dll
/// that engines can load with <c>Assembly.Load(bytes)</c>, skipping
/// the Sigil emission step at load time.
///
/// <para>Self-referential IL choice-points (used by multi-clause and
/// non-leaf single-clause predicates) point at a static
/// <c>PredicateDelegate[]</c> field in the emitted type rather than
/// the process-wide
/// <see cref="IlPredicateCompiler.IndexedDelegateHolder"/> table. At
/// load time the engine fills the array with delegates created from
/// each <c>MethodInfo</c>, giving each bundle a self-contained
/// indirection that doesn't collide with other bundles' keys.</para>
/// </summary>
public static class PersistedIlBuilder
{
    /// <summary>The fully qualified name of the type emitted into the
    /// assembly. Load-side code uses
    /// <see cref="Assembly.GetType(string)"/> to find it.</summary>
    public const string TypeName = "Shumway.Compiled.ShumwayCompiledPredicates";

    /// <summary>The name of the static <c>PredicateDelegate[]</c>
    /// field that holds the self-referential delegate slots. Load-side
    /// code accesses it via reflection to populate the entries from
    /// <c>MethodInfo.CreateDelegate</c>.</summary>
    public const string DelegatesFieldName = "_delegates";

    /// <summary>Per-method metadata embedded in the assembly so the
    /// load path can map each emitted method back to its functor /
    /// arity / promotion shape without parsing IL.</summary>
    public sealed class Entry
    {
        public required int FunctorId { get; init; }
        public required string FunctorName { get; init; }
        public required int Arity { get; init; }
        public required string MethodName { get; init; }
        public required int DelegateSlot { get; init; }
    }

    /// <summary>Builds an in-memory .dll holding IL for every
    /// predicate in <paramref name="predicates"/> that the runtime IL
    /// compiler can handle. Returns the .dll bytes plus the per-method
    /// metadata callers need at load time.</summary>
    public static (byte[] DllBytes, IReadOnlyList<Entry> Entries) Build(
        string assemblyName,
        IReadOnlyDictionary<int, CompiledPredicate> predicates)
    {
        var psab = new PersistedAssemblyBuilder(
            new AssemblyName(assemblyName), typeof(object).Assembly);
        var module = psab.DefineDynamicModule(assemblyName);
        var typeBuilder = module.DefineType(
            TypeName,
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

        // The static field's array size must be known at type definition
        // time, but the slot count depends on how many predicates we
        // actually emit. Pre-pass to filter the persistable set.
        var ic = new IlPredicateCompiler();
        var probeCalleeMap = predicates;
        var eligible = new List<(int FunctorId, CompiledPredicate Pred)>();
        foreach (var (functorId, pred) in predicates)
        {
            if (CanPersist(pred, probeCalleeMap)) eligible.Add((functorId, pred));
        }

        // Static field _delegates: PredicateDelegate[] populated at load
        // time. Each persisted method references it for self-CP push and
        // for chain-style cross-predicate IL CP targets. Empty bundles
        // still get the field so the load path's reflection lookup
        // doesn't have to special-case "no eligible predicates".
        var delegatesField = typeBuilder.DefineField(
            DelegatesFieldName,
            typeof(PredicateDelegate[]),
            FieldAttributes.Public | FieldAttributes.Static);

        var entries = new List<Entry>();
        int slot = 0;
        foreach (var (functorId, pred) in eligible)
        {
            string functorName = ResolveFunctorName(functorId);
            // Method name layout: P_{slot}_{functorId}_{sanitisedName}.
            // The load path's reflection enumeration order isn't
            // guaranteed by the runtime, so slot and functorId both
            // live in the name where they can be parsed unambiguously.
            string methodName = SanitiseMethodName($"P_{slot}_{functorId}_{functorName}");
            try
            {
                ic.EmitPersistedMethod(
                    typeBuilder, methodName, pred,
                    delegatesField: delegatesField,
                    slot: slot,
                    calleeMap: probeCalleeMap);
            }
            catch (Exception ex)
                when (ex is NotSupportedException
                      || ex.GetType().Namespace?.StartsWith("Sigil") == true)
            {
                // CanPersist accepted this predicate (CanCompile was happy)
                // but the emit blew up. Most often this is Sigil's verifier
                // flagging dead-code or stack-mismatch issues in a generated
                // sequence the predicate compiler hasn't been hardened
                // against yet (chunk-190 corner cases are a known source).
                // The runtime IL promotion store would have caught the same
                // failure and skipped the predicate; do the same here so a
                // single bad predicate doesn't abort the entire .shum
                // build.
                System.Console.Error.WriteLine(
                    $"shumway-persisted-il: skipped {functorName} "
                    + $"(fid={functorId}): {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            entries.Add(new Entry
            {
                FunctorId = functorId,
                FunctorName = functorName,
                Arity = pred.Arity,
                MethodName = methodName,
                DelegateSlot = slot++,
            });
        }

        typeBuilder.CreateType();

        using var stream = new MemoryStream();
        psab.Save(stream);
        return (stream.ToArray(), entries);
    }

    /// <summary>Returns true iff <paramref name="pred"/> falls inside
    /// the IL compiler's promotable subset — exactly the set
    /// <see cref="IlPredicateCompiler.CanCompile"/> accepts. Chunk 71
    /// covers single-clause-leaf, single-clause-with-meta-CP, indexed-
    /// atom dispatch, and try-me-else chains; the runtime path and
    /// the persisted path share the same eligibility check now.</summary>
    public static bool CanPersist(
        CompiledPredicate pred,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        return new IlPredicateCompiler().CanCompile(pred, calleeMap);
    }

    private static string ResolveFunctorName(int functorId)
    {
        var (atomId, arity) = FunctorTable.Lookup(functorId);
        string name = AtomTable.GetById(atomId)?.Name ?? "anon";
        return $"{name}/{arity}";
    }

    /// <summary>Maps a Prolog functor indicator (which can include
    /// operator characters, slashes, module-mangled <c>$</c> etc.) onto
    /// a CLR-identifier-safe method name. Reversibility doesn't matter
    /// here — the per-method <see cref="Entry"/> carries the original
    /// functor metadata so the load path doesn't need to parse names.</summary>
    private static string SanitiseMethodName(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return sb.ToString();
    }
}
