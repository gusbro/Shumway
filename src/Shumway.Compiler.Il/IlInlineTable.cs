using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;

namespace Shumway.Compiler.Il;

/// <summary>The inlines the JIT makes for a call the emitter emits: the
/// callee itself and, inside it, the callees it inlines in turn; 0 for a
/// callee the JIT leaves as a call. A property of the callee's code, not
/// of the program, measured once from the JIT's own events by the probe in
/// <c>IlInlineTableTests</c> and kept in <c>IlInlineTable.Generated.cs</c>
/// (regenerated with <c>SHUMWAY_INLINE_TABLE_OUT</c>). The planner sums it
/// per member against the JIT's cap on locals
/// (<see cref="IlJitLimits.InlineLocals"/>). A callee the table does not
/// name is judged by its metadata: a body of at most
/// <see cref="IlJitLimits.MaxInlineIlBytes"/> of IL or marked for aggressive
/// inlining counts one, the rest none. The generated half records the
/// runtime and the architecture it was measured on
/// (<see cref="MeasuredMajor"/>, <see cref="MeasuredArchitecture"/>).</summary>
internal static partial class IlInlineTable
{
    private static readonly ConcurrentDictionary<MethodBase, int> Memo = new();

    public static int Inlines(MethodBase method)
    {
        if (method is MethodBuilder) return 0;   // a region or a continuation: far past any inline size
        return Memo.GetOrAdd(method, static m => Table.TryGetValue(Key(m), out int n) ? n : Guess(m));
    }

    /// <summary>The table's key: the declaring type, the name and the
    /// parameter types, generic arguments by name without their assembly.</summary>
    public static string Key(MethodBase method)
    {
        var ps = method.GetParameters();
        var sb = new System.Text.StringBuilder();
        sb.Append(TypeName(method.DeclaringType!)).Append("::").Append(method.Name).Append('(');
        for (int i = 0; i < ps.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(TypeName(ps[i].ParameterType));
        }
        return sb.Append(')').ToString();
    }

    private static string TypeName(Type t)
    {
        if (t.IsByRef) return TypeName(t.GetElementType()!) + "&";
        if (t.IsArray) return TypeName(t.GetElementType()!) + "[]";
        if (t.IsGenericType)
        {
            var args = t.GetGenericArguments();
            var sb = new System.Text.StringBuilder();
            sb.Append(t.Namespace).Append('.').Append(t.Name).Append('[');
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(TypeName(args[i]));
            }
            return sb.Append(']').ToString();
        }
        return t.FullName ?? t.Name;
    }

    /// <summary>Whether the table knows the method; the fallback stands in
    /// for one it does not.</summary>
    public static bool Knows(MethodBase method) => Table.ContainsKey(Key(method));

    public static int Count => Table.Count;

    // GetMethodBody is trim-unsafe (IL2026): trimming may change a body, and
    // so its size. The size here only guesses the inlines of a helper the
    // table does not name, and a trimmed app (the browser) runs no IL
    // emitter: Tier-1 needs dynamic code.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The body's size is a guess at the JIT's inlining for a method "
        + "the table does not know, and the emitter never runs in a trimmed app.")]
    private static int Guess(MethodBase m)
    {
        var flags = m.GetMethodImplementationFlags();
        if ((flags & MethodImplAttributes.NoInlining) != 0 || m.IsAbstract) return 0;
        if ((flags & MethodImplAttributes.AggressiveInlining) != 0) return 1;
        byte[]? il;
        try { il = m.GetMethodBody()?.GetILAsByteArray(); }
        catch (Exception) { return 0; }
        return il is not null && il.Length <= IlJitLimits.MaxInlineIlBytes ? 1 : 0;
    }
}
