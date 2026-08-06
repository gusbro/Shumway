using System.Runtime.InteropServices.JavaScript;

namespace Shumway.Web;

/// <summary>
/// The files a page's Prolog can see. They live in the browser's in-memory
/// filesystem, which is what makes <c>consult/1</c>, <c>open/4</c> and the rest
/// of the file builtins work in a browser at all — the engine's ordinary
/// <c>System.IO</c> calls land there with no special casing.
///
/// <para>In-memory means gone on reload, so the page mirrors this directory to
/// OPFS (origin-private storage) on the JavaScript side. These exports are the
/// two directions of that mirror, plus what the UI needs to show a file list.
/// Keeping the persistence in JavaScript keeps the engine unaware of the
/// browser: nothing here is a Shumway concept.</para>
/// </summary>
internal static partial class WebShumwayApp
{
    /// <summary>Where the page's files live. Prolog resolves relative paths
    /// against it, so <c>consult('lists.pl')</c> means what the user expects.</summary>
    internal const string WorkspaceRoot = "/workspace";

    internal static void EnsureWorkspace()
    {
        Directory.CreateDirectory(WorkspaceRoot);
        Directory.SetCurrentDirectory(WorkspaceRoot);
    }

    // These go through the engine gate too. They are not engine calls, but they
    // are the SAME filesystem a running goal reads and writes through open/4 —
    // and EnsureWorkspace sets the process-wide current directory. Queueing them
    // behind engine work costs a save nothing in practice (the gate is only held
    // for the length of one solution) and removes the race entirely.

    /// <summary>The workspace's file names, newline-separated, sorted.</summary>
    [JSExport]
    internal static Task<string> WorkspaceList()
        => OnEngine(() =>
        {
            EnsureWorkspace();
            var names = Directory.GetFiles(WorkspaceRoot)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.Ordinal);
            return string.Join('\n', names!);
        });

    /// <summary>A file's contents, or null when there is no such file.</summary>
    [JSExport]
    internal static Task<string?> WorkspaceRead(string name)
        => OnEngine(() =>
        {
            string path = Resolve(name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        });

    /// <summary>Creates or replaces a file. Returns null, or the error text.</summary>
    [JSExport]
    internal static Task<string?> WorkspaceWrite(string name, string content)
        => OnEngine(() =>
        {
            try
            {
                EnsureWorkspace();
                File.WriteAllText(Resolve(name), content);
                return (string?)null;
            }
            catch (Exception ex) { return ex.Message; }
        });

    /// <summary>Removes a file. Returns null, or the error text.</summary>
    [JSExport]
    internal static Task<string?> WorkspaceDelete(string name)
        => OnEngine(() =>
        {
            try
            {
                string path = Resolve(name);
                if (File.Exists(path)) File.Delete(path);
                return (string?)null;
            }
            catch (Exception ex) { return ex.Message; }
        });

    /// <summary>Consults a workspace file — the path Prolog itself would take,
    /// so a program that spans several files loads the way it does on a desktop.
    /// Returns null, or the diagnostic.</summary>
    [JSExport]
    internal static Task<string?> ConsultWorkspaceFile(string name)
        => OnEngine(() =>
        {
            try
            {
                EndRun();       // as ConsultBuffer: the program is changing
                _session!.Engine.ConsultFile(Resolve(name));
                return (string?)null;
            }
            catch (Exception ex) { return Describe(ex); }
        });

    /// <summary>Rejects anything that would leave the workspace. The page is not
    /// a hostile input, but a path assembled from a Prolog program's output is
    /// not obviously trustworthy either, and the rest of the in-memory
    /// filesystem holds the runtime's own files.</summary>
    private static string Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        EnsureWorkspace();
        string full = Path.GetFullPath(Path.Combine(WorkspaceRoot, name));
        if (!full.StartsWith(WorkspaceRoot + "/", StringComparison.Ordinal)
            && full != WorkspaceRoot)
            throw new ArgumentException($"'{name}' is outside the workspace", nameof(name));
        return full;
    }
}
