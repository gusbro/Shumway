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

        // Single-clause, no-IL-CP shape: deterministic body emit, no
        // self-reference needed. Filter the predicate set to that shape
        // for the MVP — multi-clause and meta-CP shapes need the
        // _delegates field plus a load-time bind, which the next chunk
        // builds on top of this scaffolding.
        var ic = new IlPredicateCompiler();
        var entries = new List<Entry>();
        var probeCalleeMap = predicates;
        int slot = 0;
        foreach (var (functorId, pred) in predicates)
        {
            if (!CanPersist(pred)) continue;
            string functorName = ResolveFunctorName(functorId);
            string methodName = SanitiseMethodName($"P_{functorId}_{functorName}");
            ic.EmitPersistedMethod(typeBuilder, methodName, pred, probeCalleeMap);
            entries.Add(new Entry
            {
                FunctorId = functorId,
                FunctorName = functorName,
                Arity = pred.Arity,
                MethodName = methodName,
                DelegateSlot = slot++,
            });
        }

        // Even if no eligible predicates exist, define an empty
        // _delegates field so the loader's reflection lookup is
        // consistent.
        typeBuilder.DefineField(
            DelegatesFieldName,
            typeof(PredicateDelegate[]),
            FieldAttributes.Public | FieldAttributes.Static);

        typeBuilder.CreateType();

        using var stream = new MemoryStream();
        psab.Save(stream);
        return (stream.ToArray(), entries);
    }

    /// <summary>The persistable subset for the MVP: single-clause
    /// predicates with no non-tail Call sites in the body. They emit
    /// a pure head-match + (optional) tail-call sequence, no IL CPs,
    /// no self-reference, no <see cref="IlPredicateCompiler.IndexedDelegateHolder"/>
    /// touches. Multi-clause and meta-CP shapes need the static
    /// delegates field which the next chunk wires.</summary>
    public static bool CanPersist(CompiledPredicate pred)
    {
        if (pred.ClauseCount != 1) return false;
        var ic = new IlPredicateCompiler();
        if (!ic.CanCompile(pred)) return false;
        // No non-tail Call opcodes → no IL CPs / self-references needed.
        return CountNonTailCalls(pred.Bytecode) == 0;
    }

    private static int CountNonTailCalls(byte[] code)
    {
        int count = 0;
        int pc = 0;
        while (pc < code.Length)
        {
            byte b = code[pc];
            if (b == (byte)Opcode.Call) count++;
            var info = OpcodeTable.Get(b);
            if (!info.IsDefined || info.Size == 0) break;
            pc += info.Size;
        }
        return count;
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
