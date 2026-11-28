namespace Shumway.Core;

/// <summary>ADR-071: the directory an engine resolves relative paths against.
///
/// <para>By default it is the process directory: a relative path goes to the
/// operating system as written and <c>working_directory/2</c> moves the whole
/// process. While an engine has a directory of its own it never reads or
/// changes the process directory: every relative path it resolves is made
/// absolute here first.</para>
///
/// <para>Shared by an engine and its activations, like the stream registry.
/// One engine runs one query at a time, so the directory needs no lock of
/// its own; the field is volatile for the host that reads it from another
/// thread.</para></summary>
public sealed class EngineDirectory
{
    // Null: the process directory. Otherwise absolute, in the host's form.
    private volatile string? _own;

    /// <summary>True when the engine has a directory of its own.</summary>
    public bool IsPerEngine => _own is not null;

    /// <summary>The current directory, absolute, in the host's form.</summary>
    public string Current => _own ?? Directory.GetCurrentDirectory();

    /// <summary>Gives the engine a directory of its own, starting at
    /// <paramref name="initial"/>, or where it stands now when null. A
    /// relative <paramref name="initial"/> is taken against the current
    /// directory. Throws <see cref="DirectoryNotFoundException"/> when it
    /// does not exist.</summary>
    public void UseOwn(string? initial)
    {
        string target = initial is null ? Current : FullPath(initial);
        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException(initial ?? target);
        _own = target;
    }

    /// <summary>Back to the process directory: the engine's own is dropped.</summary>
    public void UseProcess() => _own = null;

    /// <summary>The path to hand to the operating system. With the process
    /// directory this is <paramref name="path"/> itself, so nothing a
    /// program can observe changes. With a directory of its own, a relative
    /// path comes back absolute; an absolute one, an empty one and the null
    /// device come back as they are.</summary>
    public string Resolve(string path)
    {
        string? own = _own;
        if (own is null || string.IsNullOrEmpty(path) || PrologPath.IsNullDevice(path))
            return path;
        return Absolute(path, own);
    }

    /// <summary><see cref="Path.GetFullPath(string)"/> against this
    /// directory: the absolute, normalised form of <paramref name="path"/>
    /// in either mode.</summary>
    public string FullPath(string path)
    {
        string? own = _own;
        if (own is null)
            return DriveInBrowser(path) ? path : Path.GetFullPath(path);
        return Absolute(path, own);
    }

    // ADR-044, amendment of 2026-11-26: in a browser a path with a drive is
    // absolute, and stays as written (PrologPath.IsRooted).
    private static bool DriveInBrowser(string path) =>
        OperatingSystem.IsBrowser() && PrologPath.HasDrive(path);

    /// <summary>Changes the directory. A relative <paramref name="directory"/>
    /// is taken against the current one. Throws
    /// <see cref="DirectoryNotFoundException"/> when it does not exist.</summary>
    public void Change(string directory)
    {
        string? own = _own;
        if (own is null)
        {
            Directory.SetCurrentDirectory(directory);
            return;
        }
        string target = Absolute(directory, own);
        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException(directory);
        _own = target;
    }

    private static string Absolute(string path, string baseDirectory)
    {
        if (DriveInBrowser(path)) return path;
#if NETFRAMEWORK
        // No GetFullPath(path, basePath) on .NET Framework. Combine drops the
        // base for a rooted path, which is right for one with a volume and
        // wrong for one that only starts with a separator: that one is
        // relative to the base's volume.
        if (path.Length > 0 && (path[0] == '/' || path[0] == '\\')
            && !(path.Length > 1 && (path[1] == '/' || path[1] == '\\')))
        {
            string root = Path.GetPathRoot(baseDirectory) ?? "";
            return Path.GetFullPath(root + path.TrimStart('/', '\\'));
        }
        return Path.GetFullPath(Path.Combine(baseDirectory, path));
#else
        return Path.GetFullPath(path, baseDirectory);
#endif
    }
}
