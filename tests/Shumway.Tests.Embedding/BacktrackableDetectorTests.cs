using System.Collections.Generic;
using System.Linq;
using Shumway.Builtins;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Validates BacktrackableDetector — the reflection-derived replacement
/// for the hand-maintained IsBacktrackableName list. It walks each builtin's IL
/// for a transitive call to a CP-creating sink (PushBuiltinChoicePoint /
/// PushIlChoicePoint / IndexEnumCursor.Start).
///
/// <para>Checks both directions: no false NEGATIVE on a known backtrackable
/// builtin (the dangerous case — a miss is a silent Tier-1 IL solution-loss
/// bug), and no false POSITIVE on common deterministic builtins. We can't assert
/// "exactly this set" because BuiltinsRegistry is process-global: other tests
/// register extra backtrackable builtins (non-det [PrologPredicate] foreigns,
/// CLP), which the detector also — correctly — flags.</para></summary>
public sealed class BacktrackableDetectorTests
{
    // Every standard builtin that pushes a choice point. `arg` joined when it
    // gained the SWI-dialect enumeration mode (arg(N,T,A) with unbound N).
    private static readonly string[] Backtrackable =
    {
        "between", "append", "atom_concat", "string_concat", "nb_current",
        "current_op", "current_char_conversion", "current_stream", "stream_property",
        "repeat", "retract",
        "$clause_enum", "$current_predicate_enum", "$sub_atom_enum",
        "nth0", "nth1", "recorded", "keys", "string_search", "directory",
        "arg",
    };

    // A representative sample of deterministic builtins (must NOT be flagged).
    private static readonly string[] Deterministic =
    {
        "is", "=", "==", "atom_length", "functor", "copy_term", "msort",
        "succ", "write", "nl", "var", "nonvar", "assertz", "throw",
        "atom_codes", "number_codes", "$sub_atom_decompositions",
    };

    [Fact]
    public void Detector_FlagsBacktrackable_NotDeterministic()
    {
        _ = new PrologEngine();   // register the standard builtins
        var byName = new Dictionary<string, BuiltinEntry>();
        foreach (var e in BuiltinsRegistry.AllEntries())
            byName[e.Name] = e;

        var wrong = new List<string>();
        foreach (var n in Backtrackable)
            if (byName.TryGetValue(n, out var e) && !e.IsBacktrackable)
                wrong.Add($"{n}: should be backtrackable, detector said NO (silent IL solution-loss risk)");
        foreach (var n in Deterministic)
            if (byName.TryGetValue(n, out var e) && e.IsBacktrackable)
                wrong.Add($"{n}: should be deterministic, detector said YES (false positive)");

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}
