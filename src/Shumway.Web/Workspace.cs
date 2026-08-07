using System.IO.Compression;
using System.Runtime.InteropServices.JavaScript;

namespace Shumway.Web;

/// <summary>
/// The files a page's Prolog can see, grouped into workspaces.
///
/// <para>A workspace is a directory — <c>/workspace/&lt;name&gt;</c> — and the
/// active one is the engine's current directory, so <c>consult('lists.pl')</c>,
/// <c>open/4</c> and the library search path all resolve inside it with no
/// special casing. That is what lets a program span several files and what keeps
/// one project's files out of another's.</para>
///
/// <para>The filesystem is the browser's in-memory one, which is what makes the
/// file builtins work in a browser at all. In-memory means gone on reload, so
/// the page mirrors each workspace to OPFS on the JavaScript side. Keeping the
/// persistence there keeps the engine unaware of the browser: nothing in this
/// file is a Shumway concept.</para>
/// </summary>
internal static partial class WebShumwayApp
{
    /// <summary>Where the workspaces live.</summary>
    internal const string WorkspacesRoot = "/workspace";

    /// <summary>The workspace a fresh page starts in.</summary>
    internal const string DefaultWorkspace = "scratch";

    private static string _activeWorkspace = DefaultWorkspace;

    internal static string ActiveWorkspaceDir => WorkspacesRoot + "/" + _activeWorkspace;

    /// <summary>Creates the active workspace if it is not there and makes it the
    /// current directory, which is what Prolog resolves relative paths against.</summary>
    internal static void EnsureWorkspace()
    {
        Directory.CreateDirectory(ActiveWorkspaceDir);
        Directory.SetCurrentDirectory(ActiveWorkspaceDir);
    }

    // These go through the engine gate. They are not engine calls, but they are
    // the SAME filesystem a running goal reads and writes through open/4 — and
    // EnsureWorkspace sets the process-wide current directory. Queueing them
    // behind engine work costs a save nothing in practice (the gate is only held
    // for the length of one solution) and removes the race entirely.

    /// <summary>The workspace names, newline-separated, sorted.</summary>
    [JSExport]
    internal static Task<string> WorkspaceNames()
        => OnEngine(() =>
        {
            EnsureWorkspace();
            var names = Directory.GetDirectories(WorkspacesRoot)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.Ordinal);
            return string.Join('\n', names!);
        });

    /// <summary>Makes <paramref name="name"/> the active workspace, creating it
    /// if needed. Returns null, or the error text.</summary>
    [JSExport]
    internal static Task<string?> WorkspaceSelect(string name)
        => OnEngine(() =>
        {
            if (InvalidWorkspaceName(name) is { } bad) return bad;
            _activeWorkspace = name;
            EnsureWorkspace();
            return null;
        });

    /// <summary>Creates a workspace without switching to it.</summary>
    [JSExport]
    internal static Task<string?> WorkspaceCreate(string name)
        => OnEngine(() =>
        {
            if (InvalidWorkspaceName(name) is { } bad) return bad;
            try
            {
                Directory.CreateDirectory(WorkspacesRoot + "/" + name);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        });

    /// <summary>Removes a workspace AND its files. Switching away first is the
    /// caller's job — removing the active one would leave the engine's current
    /// directory pointing at nothing.</summary>
    [JSExport]
    internal static Task<string?> WorkspaceRemove(string name)
        => OnEngine(() =>
        {
            if (InvalidWorkspaceName(name) is { } bad) return bad;
            if (name == _activeWorkspace)
                return $"'{name}' is the active workspace";
            try
            {
                string dir = WorkspacesRoot + "/" + name;
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        });

    /// <summary>The active workspace packed as a zip, base64-encoded.
    ///
    /// <para>Base64 rather than the bytes: an array inside a Task is not
    /// marshalable. Deflate rather than anything stronger because it is what
    /// browser-wasm has — the same reason bundles load uncompressed here.</para></summary>
    [JSExport]
    internal static Task<string> WorkspaceZip()
        => OnEngine(() =>
        {
            EnsureWorkspace();
            var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                foreach (string path in Directory.GetFiles(ActiveWorkspaceDir).OrderBy(p => p, StringComparer.Ordinal))
                {
                    var entry = zip.CreateEntry(Path.GetFileName(path), CompressionLevel.Optimal);
                    using var into = entry.Open();
                    using var from = File.OpenRead(path);
                    from.CopyTo(into);
                }
            return Convert.ToBase64String(buffer.ToArray());
        });

    /// <summary>The active workspace's file names, newline-separated, sorted.</summary>
    [JSExport]
    internal static Task<string> WorkspaceList()
        => OnEngine(() =>
        {
            EnsureWorkspace();
            var names = Directory.GetFiles(ActiveWorkspaceDir)
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

    /// <summary>Why <paramref name="name"/> is not a usable workspace name, or
    /// null. A name becomes a directory, so anything that could reach out of the
    /// workspaces root is refused rather than sanitised — silently renaming what
    /// someone typed is worse than saying no.</summary>
    private static string? InvalidWorkspaceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "a workspace needs a name";
        if (name is "." or "..") return $"'{name}' is not a name";
        if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
            return "a workspace name cannot contain a path separator";
        return null;
    }

    /// <summary>Rejects anything that would leave the ACTIVE workspace. The page
    /// is not a hostile input, but a path assembled from a Prolog program's
    /// output is not obviously trustworthy either, and the rest of the in-memory
    /// filesystem holds the runtime's own files — and the other workspaces.</summary>
    private static string Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        EnsureWorkspace();
        string root = ActiveWorkspaceDir;
        string full = Path.GetFullPath(Path.Combine(root, name));
        if (!full.StartsWith(root + "/", StringComparison.Ordinal) && full != root)
            throw new ArgumentException($"'{name}' is outside the workspace", nameof(name));
        return full;
    }
}
