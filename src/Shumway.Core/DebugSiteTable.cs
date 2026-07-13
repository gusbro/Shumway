using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Shumway.Core;

/// <summary>One source location a debugger can stop at: a goal in a clause, or a
/// clause's entry. Interned in <see cref="DebugSiteTable"/>.</summary>
public readonly record struct DebugSite(int FileId, int Line, int Column);

/// <summary>
/// ADR-035 — the global table of debuggable source locations, and of the files
/// they live in. A <see cref="Opcode.Break"/> instruction's operand is an id
/// from here.
///
/// <para><b>Why a global intern table rather than a per-predicate side table.</b>
/// A side table would have to be keyed by bytecode offset, and every offset in
/// this compiler is relocated at least twice — clause into predicate, predicate
/// into program — so each of the four predicate-assembly paths and the linker
/// would need to know about it. An interned id is invariant under all of that:
/// the compiler bakes it into the instruction, and the runtime resolves it
/// directly, with no relocation anywhere. It is the same argument that makes
/// atom and functor ids global.</para>
///
/// <para>Sites are only ever created under <c>compile_mode=debug</c>, and ids are
/// never reused, so the table's growth is bounded by how much debug code a
/// process compiles. Thread-safe: several engines may compile at once.</para>
/// </summary>
public static class DebugSiteTable
{
    /// <summary>A FILE, not a string. Two names for the same file must intern to the same
    /// id, or a breakpoint binds against a file that has no code in it and is silently never
    /// hit — which is exactly what happened: the engine was started with
    /// <c>shumway --debug c:\temp\Blint.pl</c> and the editor opened
    /// <c>C:\temp\Blint.pl</c>, and those were two different files here. Same for a relative
    /// path against the absolute one the IDE always uses.</summary>
    private static readonly ConcurrentDictionary<string, int> _fileIds =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly List<string> _fileNames = new();

    private static readonly ConcurrentDictionary<DebugSite, int> _siteIds = new();
    private static readonly List<DebugSite> _sites = new();

    // (fileId, line) → the site ids on that line. What a source-level breakpoint
    // resolves through: the user picks a line, we arm every site on it.
    private static readonly Dictionary<(int File, int Line), List<int>> _byLine = new();

    private static readonly object _lock = new();

    /// <summary>Id of the file with this name, interning it if new. The name is
    /// whatever the consult was given — a path, or a synthetic name like
    /// <c>&lt;string&gt;</c> for <c>ConsultString</c>.</summary>
    public static int InternFile(string name)
    {
        name = Canonicalize(name);
        if (_fileIds.TryGetValue(name, out int existing)) return existing;
        lock (_lock)
        {
            if (_fileIds.TryGetValue(name, out existing)) return existing;
            int id = _fileNames.Count;
            _fileNames.Add(name);
            _fileIds[name] = id;
            return id;
        }
    }

    /// <summary>The one spelling of a file's name. Everything that reaches this table names a
    /// file twice over — the engine from a command line, the debugger from an editor — and
    /// they never agree on the drive letter's case, on <c>..</c>, or on whether the path was
    /// relative. A synthetic name (<c>&lt;string&gt;</c>) is not a path and passes through
    /// untouched.</summary>
    private static string Canonicalize(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] == '<') return name;
        try { return System.IO.Path.GetFullPath(name); }
        catch (Exception) { return name; }   // not a path we can resolve: take it as given
    }

    public static string FileName(int fileId)
    {
        lock (_lock)
            return (uint)fileId < (uint)_fileNames.Count ? _fileNames[fileId] : "<unknown>";
    }

    /// <summary>Id of this source location, interning it if new. Two goals at the
    /// same file/line/column share an id — they are the same place to stop.</summary>
    public static int Intern(int fileId, int line, int column)
    {
        var site = new DebugSite(fileId, line, column);
        if (_siteIds.TryGetValue(site, out int existing)) return existing;
        lock (_lock)
        {
            if (_siteIds.TryGetValue(site, out existing)) return existing;
            int id = _sites.Count;
            _sites.Add(site);
            _siteIds[site] = id;
            if (!_byLine.TryGetValue((fileId, line), out var ids))
                _byLine[(fileId, line)] = ids = new List<int>();
            ids.Add(id);
            return id;
        }
    }

    public static DebugSite Get(int siteId)
    {
        lock (_lock)
            return (uint)siteId < (uint)_sites.Count ? _sites[siteId] : default;
    }

    public static int Count { get { lock (_lock) return _sites.Count; } }

    /// <summary>Every site on a source line — what a user's line breakpoint arms.
    /// A line with several goals on it yields several sites; a line that compiled
    /// to no code yields none, which is how a debugger knows the breakpoint
    /// cannot bind there.</summary>
    public static IReadOnlyList<int> SitesOnLine(int fileId, int line)
    {
        lock (_lock)
            return _byLine.TryGetValue((fileId, line), out var ids)
                ? ids.ToArray() : Array.Empty<int>();
    }

    /// <summary>The nearest line at or after <paramref name="line"/> in this file
    /// that has sites, or -1. Lets a debugger snap a breakpoint set on a blank or
    /// comment line down to the next real goal, the way source debuggers do.</summary>
    public static int SnapLine(int fileId, int line)
    {
        lock (_lock)
        {
            int best = -1;
            foreach (var key in _byLine.Keys)
                if (key.File == fileId && key.Line >= line && (best < 0 || key.Line < best))
                    best = key.Line;
            return best;
        }
    }
}
