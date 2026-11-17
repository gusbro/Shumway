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
    private static double ModifiedOf(string file)
        => new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds();

    /// <summary>The files under <paramref name="root"/>, at any depth, as a
    /// JSON array of <c>{path, modified, size}</c>: the path relative to the
    /// root with <c>/</c>, the time in milliseconds. Empty when there is no
    /// such directory.</summary>
    [JSExport]
    internal static Task<string> FilesList(string root)
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
                    json.WriteNumber("modified", ModifiedOf(file));
                    json.WriteNumber("size", new FileInfo(file).Length);
                    json.WriteEndObject();
                }
            }
            json.WriteEndArray();
        }
        return Task.FromResult(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
    }

    /// <summary>A file's bytes as base64, or null when there is no such
    /// file.</summary>
    [JSExport]
    internal static Task<string?> FileRead(string path)
        => Task.FromResult(File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : null);

    /// <summary>Writes a file from base64, making the directories above it,
    /// and answers its modification time, in milliseconds.</summary>
    [JSExport]
    internal static Task<double> FileWrite(string path, string base64)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, Convert.FromBase64String(base64));
        return Task.FromResult(ModifiedOf(path));
    }

    /// <summary>Removes a file. False when there was none.</summary>
    [JSExport]
    internal static Task<bool> FileRemove(string path)
    {
        if (!File.Exists(path)) return Task.FromResult(false);
        File.Delete(path);
        return Task.FromResult(true);
    }
}
