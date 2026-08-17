namespace Shumway.Core;

/// <summary>ADR-044 — the canonical path form Prolog sees: <c>/</c> as the
/// separator on every platform, so path text is ordinary re-readable term
/// text and portable code can treat a path as data.
///
/// <para>The native separator is a boundary detail: a path ARGUMENT may
/// arrive in either form (Win32 accepts both), and every path Shumway
/// RETURNS is canonical. The translation is Windows-only and one-way —
/// on Unix a backslash is a legal character in a file name, so rewriting it
/// would make that file unreachable.</para></summary>
public static class PrologPath
{
    /// <summary>Windows path prefixes whose backslashes are naming syntax
    /// rather than separators: UNC (<c>\\server\share</c>), device
    /// (<c>\\.\nul</c>) and extended-length (<c>\\?\C:\…</c>). Rewriting
    /// these can change which object is named, so they pass through.</summary>
    private static bool HasSpecialPrefix(string path) =>
        path.Length >= 2 && path[0] == '\\' && path[1] == '\\';

    /// <summary>Converts a host path to the canonical Prolog form. On Unix
    /// this is the identity.</summary>
    public static string ToCanonical(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (!OperatingSystem.IsWindows()) return path;
        if (HasSpecialPrefix(path)) return path;
        return path.Replace('\\', '/');
    }

    /// <summary>The canonical form of a DIRECTORY: as <see cref="ToCanonical"/>,
    /// and ending in <c>/</c> (ADR-044 §5 — the form a caller can concatenate
    /// a file name onto).</summary>
    public static string ToCanonicalDirectory(string path)
    {
        string canonical = ToCanonical(path);
        if (canonical.Length == 0) return canonical;
        char last = canonical[canonical.Length - 1];
        if (last == '/' || last == '\\') return canonical;
        return canonical + "/";
    }

    /// <summary>True for the Windows null device, under every spelling a
    /// caller can arrive with: the bare name portable Prolog uses
    /// (<c>nul</c>, Logtalk's <c>os::null_device_path</c>), the drive-style
    /// <c>nul:</c>, and the device path <c>\\.\nul</c> that
    /// <c>Path.GetFullPath</c> expands the bare name into — which is what
    /// reaches a builtin after <c>absolute_file_name/2</c>. .NET reports the
    /// device as non-existent, so callers substitute
    /// <see cref="System.IO.Stream.Null"/> / size 0. On Unix
    /// <c>/dev/null</c> is an ordinary file and needs no special case.</summary>
    public static bool IsNullDevice(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(path)) return false;
        return path.Equals("nul", StringComparison.OrdinalIgnoreCase)
            || path.Equals("nul:", StringComparison.OrdinalIgnoreCase)
            || path.Equals(@"\\.\nul", StringComparison.OrdinalIgnoreCase)
            || path.Equals("//./nul", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Converts a canonical path to the host's own form — what
    /// <c>prolog_to_os_filename/2</c> returns, for handing a path to an
    /// external tool that insists on the native separator. Identity on
    /// Unix.</summary>
    public static string ToOs(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (!OperatingSystem.IsWindows()) return path;
        if (HasSpecialPrefix(path)) return path;
        return path.Replace('/', '\\');
    }
}
