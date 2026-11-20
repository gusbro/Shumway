using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

namespace Shumway.Web;

/// <summary>The files a page mirrors for its notebook sessions (ADR-070):
/// what is under a directory of the in-memory file system, read and written
/// from the page. Contents cross as base64: an array cannot cross inside a
/// Task. These run on the caller's thread, not a session's, which the
/// notebook server holds for as long as the session lives.</summary>
internal static partial class WebShumwayApp
{
    // What tells one version of a file from the next. Not its time: the
    // in-memory file system keeps it to the second, and a cell that rewrites
    // a file within the second of the cell before it would not be copied.
    // The size and a hash of the content do. A file's version is kept once
    // the second of its time is over, as a later write has to move that time.
    private static readonly Dictionary<string, (long Modified, long Size, string Version)> Settled = new();

    private static string VersionOf(string file)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var info = new FileInfo(file);
        long modified = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        lock (Settled)
        {
            if (Settled.TryGetValue(file, out var known) && known.Modified == modified && known.Size == info.Length)
                return known.Version;
        }
        string version = info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ":" + Fnv1a(File.ReadAllBytes(file)).ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        if (now - modified >= 2000)
        {
            lock (Settled) Settled[file] = (modified, info.Length, version);
        }
        return version;
    }

    private static ulong Fnv1a(byte[] bytes)
    {
        ulong hash = 14695981039346656037;
        foreach (byte b in bytes)
        {
            hash ^= b;
            hash *= 1099511628211;
        }
        return hash;
    }

    /// <summary>The files under <paramref name="root"/>, at any depth, as a
    /// JSON array of <c>{path, modified, size}</c>: the path relative to the
    /// root with <c>/</c>, and what tells this version of the file from
    /// another. Empty when there is no such directory.</summary>
    [JSExport]
    internal static Task<string> FilesList(string root) => Answered(FilesJson(root));

    /// <summary>What <see cref="FilesList"/> answers.</summary>
    private static string FilesJson(string root)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            if (Directory.Exists(root))
            {
                string full = Path.GetFullPath(root);
                foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    json.WriteStartObject();
                    json.WriteString("path", Path.GetRelativePath(full, file).Replace('\\', '/'));
                    json.WriteString("modified", VersionOf(file));
                    json.WriteNumber("size", new FileInfo(file).Length);
                    json.WriteEndObject();
                }
            }
            json.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>A file's bytes as base64, or null when there is no such
    /// file.</summary>
    [JSExport]
    internal static Task<string?> FileRead(string path)
        => Answered(File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : null);

    /// <summary>Writes a file from base64, making the directories above it,
    /// and answers what <see cref="FilesList"/> will say of it.</summary>
    [JSExport]
    internal static Task<string> FileWrite(string path, string base64)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, Convert.FromBase64String(base64));
        return Answered(VersionOf(Path.GetFullPath(path)));
    }

    /// <summary>Removes a file. False when there was none.</summary>
    [JSExport]
    internal static Task<bool> FileRemove(string path)
    {
        if (!File.Exists(path)) return Answered(false);
        File.Delete(path);
        return Answered(true);
    }
}
