using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Shumway.Core;

/// <summary>
/// Phase 20 — opt-in execution profiler. Every recording method is
/// <see cref="ConditionalAttribute"/>("SHUMWAY_PROFILE"): when the
/// constant isn't defined (the default), the C# compiler removes the
/// call sites entirely — arguments aren't even evaluated — so a normal
/// build pays absolutely nothing. Build with
/// <c>dotnet build -p:ShumwayProfile=true</c> to compile the hooks in.
///
/// <para>The profiler is process-global and not thread-safe; Shumway
/// engines are single-threaded, and profiling runs are single-engine,
/// so a plain set of static counters suffices. Call <see cref="Reset"/>
/// before a run and <see cref="Report"/> after.</para>
///
/// <para>What it records:</para>
/// <list type="bullet">
/// <item>Opcode histogram — how many of each WAM instruction executed.</item>
/// <item>Per-predicate call counts, keyed by the callee's bytecode
///   address (resolved to Name/Arity at report time via a caller-
///   supplied map).</item>
/// <item>Per-builtin call counts and <em>inclusive</em> wall-clock time
///   (a nested builtin's time is counted in both itself and its
///   caller).</item>
/// <item>Scalar counters: total opcodes, predicate calls, backtracks,
///   unifications, choice-point pushes.</item>
/// </list>
/// </summary>
public static class Profiler
{
    private static readonly Dictionary<byte, long> _opcodeCounts = new();
    private static readonly Dictionary<int, long> _callsByAddress = new();
    private static readonly Dictionary<int, long> _builtinCounts = new();
    private static readonly Dictionary<int, long> _builtinTicks = new();
    private static readonly Stack<(int Id, long Start)> _builtinStack = new();

    private static long _totalOpcodes;
    private static long _totalCalls;
    private static long _backtracks;
    private static long _unifications;
    private static long _choicePoints;
    private static long _runStartTimestamp;
    private static long _runElapsedTicks;

    /// <summary>True in a build compiled with <c>SHUMWAY_PROFILE</c>.
    /// Lets callers cheaply skip report assembly / printing when the
    /// hooks were stripped. Not <c>[Conditional]</c> so it can be read
    /// from a plain <c>if</c>.</summary>
    public static bool Enabled
    {
#if SHUMWAY_PROFILE
        get => true;
#else
        get => false;
#endif
    }

    [Conditional("SHUMWAY_PROFILE")]
    public static void Reset()
    {
        _opcodeCounts.Clear();
        _pairCounts.Clear();
        _lastOpcode = 0xFF;
        _reallocs.Clear();
        _callsByAddress.Clear();
        _builtinCounts.Clear();
        _builtinTicks.Clear();
        _builtinBytes.Clear();
        _builtinStack.Clear();
        _builtinAllocStack.Clear();
        _totalOpcodes = 0;
        _totalCalls = 0;
        _backtracks = 0;
        _unifications = 0;
        _choicePoints = 0;
        _runStartTimestamp = Stopwatch.GetTimestamp();
        _runElapsedTicks = 0;
        _notes.Clear();
    }

    private static byte _lastOpcode = 0xFF;
    private static readonly Dictionary<int, long> _pairCounts = new();

    /// <summary>Records one dispatched WAM instruction. Also bumps the
    /// (prev, current) pair counter so fusion candidates can be picked
    /// from real workload data.</summary>
    [Conditional("SHUMWAY_PROFILE")]
    public static void Opcode(byte op)
    {
        _opcodeCounts.TryGetValue(op, out long c);
        _opcodeCounts[op] = c + 1;
        _totalOpcodes++;
        int pairKey = (_lastOpcode << 8) | op;
        _pairCounts.TryGetValue(pairKey, out long pc);
        _pairCounts[pairKey] = pc + 1;
        _lastOpcode = op;
    }

    /// <summary>Records a user-predicate call to bytecode address
    /// <paramref name="address"/> (resolved to a name at report time).</summary>
    [Conditional("SHUMWAY_PROFILE")]
    public static void Call(int address)
    {
        _callsByAddress.TryGetValue(address, out long c);
        _callsByAddress[address] = c + 1;
        _totalCalls++;
    }

    /// <summary>Marks the start of a builtin invocation. Pushes a timer
    /// so nested builtins (via <c>call/N</c>) nest correctly.</summary>
    private static readonly Dictionary<int, long> _builtinBytes = new();
    private static readonly Stack<long> _builtinAllocStack = new();

    [Conditional("SHUMWAY_PROFILE")]
    public static void BuiltinEnter(int builtinId)
    {
        _builtinStack.Push((builtinId, Stopwatch.GetTimestamp()));
        _builtinAllocStack.Push(GC.GetAllocatedBytesForCurrentThread());
    }

    /// <summary>Marks the end of the most recently entered builtin and
    /// adds its inclusive elapsed time + allocated bytes to that
    /// builtin's totals.</summary>
    [Conditional("SHUMWAY_PROFILE")]
    public static void BuiltinExit(int builtinId)
    {
        if (_builtinStack.Count == 0) return;
        var (id, start) = _builtinStack.Pop();
        long elapsed = Stopwatch.GetTimestamp() - start;
        _builtinTicks.TryGetValue(id, out long t);
        _builtinTicks[id] = t + elapsed;
        _builtinCounts.TryGetValue(id, out long c);
        _builtinCounts[id] = c + 1;
        long allocStart = _builtinAllocStack.Pop();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocStart;
        _builtinBytes.TryGetValue(id, out long b);
        _builtinBytes[id] = b + bytes;
    }

    private static readonly Dictionary<string, (long Bytes, long Count)> _reallocs = new();

    /// <summary>Records one buffer reallocation (heap/stack/trail) of
    /// <paramref name="bytes"/> bytes — diagnoses how much per-query
    /// .NET allocation is array doubling vs genuine churn.</summary>
    [Conditional("SHUMWAY_PROFILE")]
    public static void Realloc(string buffer, long bytes)
    {
        _reallocs.TryGetValue(buffer, out var v);
        _reallocs[buffer] = (v.Bytes + bytes, v.Count + 1);
    }

    [Conditional("SHUMWAY_PROFILE")]
    public static void Backtrack() => _backtracks++;

    [Conditional("SHUMWAY_PROFILE")]
    public static void Unify() => _unifications++;

    [Conditional("SHUMWAY_PROFILE")]
    public static void ChoicePoint() => _choicePoints++;

    private static readonly Dictionary<string, long> _notes = new();

    /// <summary>Bumps a named counter — ad-hoc instrumentation for
    /// pinning down a hot path (e.g. how many times a global lock is
    /// taken). Shown in the report under "-- notes --".</summary>
    [Conditional("SHUMWAY_PROFILE")]
    public static void Note(string label)
    {
        _notes.TryGetValue(label, out long c);
        _notes[label] = c + 1;
    }

    /// <summary>Stamps the elapsed wall-clock time for the run. Call
    /// once when the run finishes, before <see cref="Report"/>.</summary>
    [Conditional("SHUMWAY_PROFILE")]
    public static void StopRun()
        => _runElapsedTicks = Stopwatch.GetTimestamp() - _runStartTimestamp;

    /// <summary>Renders a human-readable report. <paramref name="addressName"/>
    /// maps a callee bytecode address to a display name (Name/Arity);
    /// addresses it doesn't resolve are shown as <c>@address</c>.
    /// <paramref name="builtinName"/> maps a builtin id to a name.
    /// Returns an empty string in a non-profile build.</summary>
    public static string Report(
        System.Func<int, string?>? addressName = null,
        System.Func<int, string?>? builtinName = null,
        int top = 25)
    {
        if (!Enabled) return string.Empty;

        double msPerTick = 1000.0 / Stopwatch.Frequency;
        var sb = new StringBuilder();
        sb.AppendLine("==== Shumway profile ====");
        sb.AppendLine($"wall time      : {_runElapsedTicks * msPerTick:N1} ms");
        sb.AppendLine($"opcodes        : {_totalOpcodes:N0}");
        sb.AppendLine($"predicate calls: {_totalCalls:N0}");
        sb.AppendLine($"backtracks     : {_backtracks:N0}");
        sb.AppendLine($"unifications   : {_unifications:N0}");
        sb.AppendLine($"choice points  : {_choicePoints:N0}");

        sb.AppendLine();
        sb.AppendLine($"-- top {top} predicates by call count --");
        foreach (var (addr, count) in TopN(_callsByAddress, top))
        {
            string name = addressName?.Invoke(addr) ?? $"@{addr}";
            sb.AppendLine($"  {count,12:N0}  {name}");
        }

        sb.AppendLine();
        sb.AppendLine($"-- top {top} builtins by inclusive time --");
        foreach (var (id, ticks) in TopN(_builtinTicks, top))
        {
            _builtinCounts.TryGetValue(id, out long c);
            string name = builtinName?.Invoke(id) ?? $"#{id}";
            sb.AppendLine($"  {ticks * msPerTick,9:N1} ms  {c,10:N0} calls  {name}");
        }

        sb.AppendLine();
        sb.AppendLine($"-- top {top} builtins by allocated bytes --");
        foreach (var (id, bytes) in TopN(_builtinBytes, top))
        {
            _builtinCounts.TryGetValue(id, out long c);
            string name = builtinName?.Invoke(id) ?? $"#{id}";
            sb.AppendLine($"  {bytes / (1024.0 * 1024),9:N1} MB  {c,10:N0} calls  {name}");
        }

        if (_reallocs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("-- buffer reallocations (cumulative bytes) --");
            foreach (var (buf, v) in _reallocs)
                sb.AppendLine($"  {v.Bytes / (1024.0 * 1024),9:N1} MB  {v.Count,5:N0} grows  {buf}");
        }

        if (_notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("-- notes --");
            foreach (var (label, count) in TopN(_notes, 50))
                sb.AppendLine($"  {count,14:N0}  {label}");
        }

        sb.AppendLine();
        sb.AppendLine($"-- top {top} opcodes --");
        foreach (var (op, count) in TopN(_opcodeCounts, top))
        {
            string name = OpcodeTable.Get(op).Op.ToString();
            sb.AppendLine($"  {count,12:N0}  {name} (0x{op:X2})");
        }

        sb.AppendLine();
        sb.AppendLine($"-- top {top} opcode pairs (prev → curr) — fusion candidates --");
        foreach (var (key, count) in TopN(_pairCounts, top))
        {
            byte prev = (byte)(key >> 8);
            byte curr = (byte)(key & 0xFF);
            string prevName = prev == 0xFF ? "<start>" : OpcodeTable.Get(prev).Op.ToString();
            string currName = OpcodeTable.Get(curr).Op.ToString();
            sb.AppendLine($"  {count,12:N0}  {prevName,-22} → {currName}");
        }

        return sb.ToString();
    }

    private static IEnumerable<KeyValuePair<TKey, long>> TopN<TKey>(
        Dictionary<TKey, long> map, int n) where TKey : notnull
    {
        var list = new List<KeyValuePair<TKey, long>>(map);
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        if (list.Count > n) list.RemoveRange(n, list.Count - n);
        return list;
    }
}
