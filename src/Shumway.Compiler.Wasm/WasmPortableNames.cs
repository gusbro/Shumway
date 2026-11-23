using System;
using System.Collections.Generic;
using Shumway.Embedding;

namespace Shumway.Compiler.Wasm;

/// <summary>The two directions between the name an engine gave a rule's helper
/// (<c>'$disj_41'</c>, with a number of that engine's counter) and the name
/// every process gives it (<see cref="PrologEngine.PortableHelperNames"/>). A
/// relocatable module names such a helper by the second: the engine that
/// loads the module numbered the same rule's helper differently.
///
/// <para>A helper's own name is the tail of the functor's from its last
/// <c>$</c>; a module's mangling is in front of it and is kept as it is.</para></summary>
public sealed class WasmPortableNames
{
    private readonly Func<Dictionary<string, string>> _source;
    private Dictionary<string, string>? _portableByLive;
    private Dictionary<string, string>? _liveByPortable;

    public WasmPortableNames(PrologEngine engine) => _source = engine.PortableHelperNames;

    private Dictionary<string, string> PortableByLive => _portableByLive ??= _source();

    /// <summary>The name as a module carries it: a helper's portable name, any
    /// other name as it is.</summary>
    public string ToPortable(string name)
    {
        int tail = name.LastIndexOf('$');
        if (tail < 0) return name;
        return PortableByLive.TryGetValue(name.Substring(tail), out var portable)
            ? name.Substring(0, tail) + portable
            : name;
    }

    /// <summary>The name in this engine of a name a module carries. Null for a
    /// portable name of a helper this engine has not: the rule is not here,
    /// or was transformed another way.</summary>
    public string? ToLive(string name)
    {
        int tail = name.LastIndexOf('$');
        if (tail < 0 || !IsPortable(name, tail)) return name;
        if (_liveByPortable is null)
        {
            _liveByPortable = new Dictionary<string, string>(PortableByLive.Count);
            foreach (var (live, portable) in PortableByLive) _liveByPortable[portable] = live;
        }
        return _liveByPortable.TryGetValue(name.Substring(tail), out var mine)
            ? name.Substring(0, tail) + mine
            : null;
    }

    /// <summary>Whether a functor of this engine is a helper with a portable name.</summary>
    public bool Names(int functorId)
    {
        string name = RelocatingCompileEnv.NameOf(functorId).Name;
        int tail = name.LastIndexOf('$');
        return tail >= 0 && PortableByLive.ContainsKey(name.Substring(tail));
    }

    /// <summary>Whether a name a module carries is a helper's portable name.</summary>
    public static bool IsPortable(string name)
    {
        int tail = name.LastIndexOf('$');
        return tail >= 0 && IsPortable(name, tail);
    }

    // $<kind>_@<32 hex digits>.<ordinal>
    private static bool IsPortable(string name, int tail)
    {
        int at = name.IndexOf("_@", tail, StringComparison.Ordinal);
        if (at < 0 || at == tail + 1) return false;
        int dot = at + 2 + 32;
        if (dot >= name.Length - 1 || name[dot] != '.') return false;
        for (int i = at + 2; i < dot; i++)
            if (!Uri.IsHexDigit(name[i])) return false;
        for (int i = dot + 1; i < name.Length; i++)
            if (name[i] < '0' || name[i] > '9') return false;
        return true;
    }
}
