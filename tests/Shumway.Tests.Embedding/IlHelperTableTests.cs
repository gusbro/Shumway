using System.Reflection;
using System.Text;
using Shumway.Compiler.Il;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-060 item 3: the table of helper effects the IL emitter spills
/// and reloads by. Every method handle the emitter owns has a row, and each
/// row says exactly what the method's code does to the machine registers.
/// The failure message of the second test is the corrected rows, ready to
/// paste.</summary>
public sealed class IlHelperTableTests
{
    private readonly ITestOutputHelper _out;

    public IlHelperTableTests(ITestOutputHelper output) => _out = output;

    /// <summary>The emitter's method handles: every static MethodInfo field of
    /// the compiler, by name.</summary>
    private static Dictionary<string, MethodInfo> Handles()
    {
        var d = new Dictionary<string, MethodInfo>();
        foreach (var f in typeof(IlPredicateCompiler).GetFields(
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (f.FieldType == typeof(MethodInfo) && f.GetValue(null) is MethodInfo m)
                d[f.Name] = m;
        }
        return d;
    }

    [Fact]
    public void EveryHandleOfTheEmitterHasARow()
    {
        var handles = Handles();
        Assert.True(handles.Count >= 100, $"only {handles.Count} handles found");
        var missing = handles.Where(kv => !IlPredicateCompiler.HelperTable.ContainsKey(kv.Value))
            .Select(kv => kv.Key).OrderBy(n => n).ToList();
        Assert.True(missing.Count == 0, "handles without a row: " + string.Join(", ", missing));
    }

    [Fact]
    public void EachRowIsWhatTheMethodDoes()
    {
        var handles = Handles();
        var byMethod = handles.ToDictionary(kv => kv.Value, kv => kv.Key);
        var wrong = new StringBuilder();
        int checkedRows = 0, narrow = 0;
        foreach (var (name, method) in handles.OrderBy(kv => kv.Key))
        {
            if (!IlPredicateCompiler.HelperTable.TryGetValue(method, out var row)) continue;
            checkedRows++;
            var found = RegisterEffectsAnalysis.Analyze(method);
            var expected = found.Row;
            if (expected != HelperEffects.Everything) narrow++;
            if (expected == row) continue;
            wrong.AppendLine($"        Row({name}, {Regs(expected.Reads)}, {Regs(expected.Writes)});");
            foreach (var note in found.Notes.Take(4)) wrong.AppendLine($"            // {note}");
        }
        _out.WriteLine($"{checkedRows} rows checked, {narrow} narrower than everything");
        // The corrected rows as a file too: the console logger wraps long lines.
        if (Environment.GetEnvironmentVariable("SHUMWAY_HELPER_ROWS_OUT") is { Length: > 0 } path)
            File.WriteAllText(path, wrong.ToString());
        Assert.True(checkedRows >= 100, $"only {checkedRows} rows checked");
        Assert.True(wrong.Length == 0, "rows that differ from the methods' code:\n" + wrong);
        // A table that says everything about everything protects nothing:
        // the analysis must find the narrow rows narrow.
        Assert.True(narrow >= 40, $"only {narrow} rows are narrower than everything");
    }

    private static string Regs(MachineRegs r)
    {
        if (r == MachineRegs.None) return "MachineRegs.None";
        if (r == MachineRegs.All) return "MachineRegs.All";
        return string.Join(" | ", r.ToString().Split(", ").Select(n => "MachineRegs." + n));
    }
}
