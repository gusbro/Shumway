using System.Diagnostics;

namespace Shumway.Core.Diagnostics;

/// <summary>Opt-in dump of the live choice-point chain. Activated by
/// the <c>SHUMWAY_CP_TRACE</c> compile constant (set via the
/// <c>ShumwayCpTrace=true</c> MSBuild property — see
/// <c>Directory.Build.props</c>). Zero runtime cost when the symbol
/// is not defined: every public method here carries
/// <see cref="ConditionalAttribute"/>, so call sites are stripped at
/// compile time.
///
/// <para>Origin: chasing "extra backtracking causes builtin X to be
/// re-entered with a now-unbound arg" class of bug — pre-fix the
/// engine left CPs alive past their cut and a later failure
/// backtracked through them, undoing variable bindings before the
/// re-entry. Dumping the CP chain at the throw site labels each
/// live CP with the predicate / offset its saved BP points at, so
/// we can identify which clause should have committed and didn't.
/// </para></summary>
public static class ChoicePointTrace
{
    private const string TraceSymbol = "SHUMWAY_CP_TRACE";

    [Conditional(TraceSymbol)]
    public static void DumpAtSite(Engine engine, string label)
    {
        Console.Error.WriteLine($"[cp] === {label} ===");
        int depth = 0;
        foreach (var (stackB, savedBp, arity) in engine.EnumerateChoicePoints())
        {
            string bpDesc;
            if (savedBp == Engine.IlChoicePointSentinelBp)
                bpDesc = "[il-sentinel]";
            else if (engine.ResolveAddressToLabel is { } resolve)
                bpDesc = resolve(savedBp) ?? $"@0x{savedBp:X}";
            else
                bpDesc = $"@0x{savedBp:X}";
            Console.Error.WriteLine(
                $"[cp]  #{depth} B={stackB} arity={arity} bp={savedBp} -> {bpDesc}");
            depth++;
            if (depth > 64)
            {
                Console.Error.WriteLine($"[cp]  ... (truncated)");
                break;
            }
        }
        Console.Error.WriteLine($"[cp]  total depth = {depth}, P={engine.P}");
        Console.Error.WriteLine($"[cp] --- env-frame call return chain ---");
        int frameIdx = 0;
        foreach (int retAddr in engine.EnumerateCallReturnAddresses())
        {
            string desc = engine.ResolveAddressToLabel?.Invoke(retAddr - 1) ?? $"@0x{retAddr:X}";
            // -1 because the return address is the instruction AFTER the
            // Call, but our label resolver finds the predicate the address
            // falls inside — same target either way unless the Call was
            // the last byte of its predicate's bytecode.
            Console.Error.WriteLine($"[cp]  env#{frameIdx} ret={retAddr} -> {desc}");
            frameIdx++;
            if (frameIdx > 32) { Console.Error.WriteLine("[cp]  ... (truncated)"); break; }
        }
        Console.Error.WriteLine($"[cp] === end ===");
    }
}
