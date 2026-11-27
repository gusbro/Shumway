using System.IO.Compression;
using System.Text;

namespace Shumway.WebHost;

/// <summary>The Prolog sources in a <c>.zip</c> or <c>.tar.gz</c> archive of a
/// library collection, which a page imports from a URL or from a file, and
/// the bundles the archive carries of them compiled.</summary>
public static class LibraryArchive
{
    /// <summary>What an archive of a collection holds: its sources, and the
    /// bundle of each library whoever made the archive compiled ahead
    /// (<c>x.shum</c> beside <c>x.pl</c>), as its bytes.</summary>
    public sealed record Contents(
        IReadOnlyList<(string Path, string Text)> Sources,
        IReadOnlyList<(string Path, byte[] Bytes)> Bundles);

    /// <summary>The sources and the bundles of <paramref name="archive"/>,
    /// each with its path inside the collection, as <see cref="Sources"/>
    /// gives the sources.</summary>
    /// <exception cref="InvalidDataException">As <see cref="Sources"/>.</exception>
    public static Contents Read(byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var found = new List<(string Path, byte[] Data)>();
        if (StartsWith(archive, 0x50, 0x4B, 0x03, 0x04)) ReadZip(archive, found);
        else if (StartsWith(archive, 0x1F, 0x8B))
        {
            using var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress);
            ReadTar(gzip, found);
        }
        else if (IsTar(archive)) ReadTar(new MemoryStream(archive), found);
        else throw new InvalidDataException("not a .zip, .tar.gz or .tar archive");

        if (!found.Exists(f => Kept(f.Path)))
            throw new InvalidDataException("no Prolog sources nor libraries compiled ahead in the archive");
        var sources = new List<(string, string)>();
        var bundles = new List<(string, byte[])>();
        foreach ((string path, byte[] data) in WithoutWrapper(found))
        {
            if (IsSource(path)) sources.Add((path, Text(new MemoryStream(data))));
            else bundles.Add((path, data));
        }
        return new Contents(sources, bundles);
    }

    /// <summary>Each Prolog source of <paramref name="archive"/>, with its path
    /// inside the collection. A single directory that holds everything, as a
    /// release archive's does, is not part of the paths: <c>lib-1.0/x.pl</c> is
    /// <c>x.pl</c>, which is <c>library(x)</c>. Entries that are not sources,
    /// or whose path leaves the archive, are left out.</summary>
    /// <exception cref="InvalidDataException">Neither a zip nor a tar (plain
    /// or gzipped), or one with no Prolog source in it.</exception>
    public static IReadOnlyList<(string Path, string Text)> Sources(byte[] archive)
        => Read(archive).Sources;

    /// <summary>A Prolog source, as a collection keeps them.</summary>
    public static bool IsSource(string path)
        => path.EndsWith(".pl", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".pro", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".prolog", StringComparison.OrdinalIgnoreCase);

    /// <summary>A library's compiled bundle, as a collection keeps one.</summary>
    public static bool IsBundle(string path)
        => path.EndsWith(".shum", StringComparison.OrdinalIgnoreCase);

    /// <summary>The text of library <paramref name="library"/>'s own module,
    /// when its bundle carries it: a module compiled in debug mode keeps its
    /// source, and the link keeps it unless it strips the bundle. The module
    /// is the bundle's only one, or the one named as the library is (the
    /// last segment of <paramref name="library"/>). Null when there is no
    /// such module, it carries no text, or the bytes are not a bundle this
    /// engine reads.</summary>
    public static string? SourceOfBundle(byte[] bundle, string library)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(library);
        Shumway.Embedding.Bundle read;
        // Another format of compiled code is refused at the header.
        try { read = Shumway.Embedding.BundleReader.FromBytes(bundle); }
        catch (InvalidDataException) { return null; }
        string own = library.Substring(library.LastIndexOf('/') + 1);
        var modules = new List<(string Name, string Source)>();
        foreach (var entry in read.Entries) modules.Add((entry.ModuleName, entry.Source));
        foreach (var member in read.ArchiveMembers)
        {
            var obj = Shumway.Embedding.ShmoReader.FromBytes(member.ShmoBytes);
            modules.Add((obj.ModuleName, obj.Source));
        }
        string? text = modules.Count == 1 ? modules[0].Source
            : modules.Find(m => m.Name == own).Source;
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>The text of library <paramref name="library"/>'s source to
    /// write beside its bundle, and the time to give the file (ADR-076,
    /// decision 4). The copy the bundle's recipe carries comes first, with
    /// the time the recipe records, in any format of compiled code; otherwise
    /// the text its module carries (<see cref="SourceOfBundle"/>), a second
    /// before the bundle's own time. Either way the source is not newer than
    /// the bundle, which stays the library. Null when the bundle carries
    /// neither.</summary>
    public static (string Text, DateTime WrittenUtc)? SourceToWrite(
        byte[] bundle, string library, DateTime bundleWrittenUtc)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(library);
        string own = library.Substring(library.LastIndexOf('/') + 1);
        if (Shumway.Embedding.BundleFormat.HeaderRecipe(bundle)?.Recipe is { } recipe
            && (recipe.CarriedSource(library + ".pl") ?? recipe.CarriedSource(own + ".pl")) is { } carried)
            return (System.Text.Encoding.UTF8.GetString(carried.Contents!), carried.WrittenUtc);
        return SourceOfBundle(bundle, library) is { } text
            ? (text, bundleWrittenUtc.AddSeconds(-1)) : null;
    }

    private static bool Kept(string path) => IsSource(path) || IsBundle(path);

    private static void ReadZip(byte[] archive, List<(string, byte[])> found)
    {
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (Inside(entry.FullName) is not { } path || !Kept(path)) continue;
            using Stream content = entry.Open();
            using var data = new MemoryStream();
            content.CopyTo(data);
            found.Add((path, data.ToArray()));
        }
    }

    // Read here rather than with System.Formats.Tar, which browser-wasm does
    // not support. Ustar and GNU headers, with the long names of either: a
    // PAX "path" record or a GNU 'L' entry before the file it names.
    private static void ReadTar(Stream tar, List<(string, byte[])> found)
    {
        var header = new byte[512];
        string? longName = null;
        while (ReadBlock(tar, header))
        {
            if (header.All(b => b == 0)) break;            // the end: two zero blocks
            long size = Octal(header, 124, 12);
            byte type = header[156];
            byte[] data = new byte[size];
            if (!ReadExactly(tar, data)) throw new InvalidDataException("the archive ends inside a file");
            Skip(tar, (512 - size % 512) % 512);

            if (type == (byte)'L') { longName = Encoding.UTF8.GetString(data).TrimEnd('\0'); continue; }
            if (type == (byte)'x') { longName = PaxPath(data) ?? longName; continue; }
            if (type is not ((byte)'0' or 0))               // a directory, a link, a global header
            {
                longName = null;
                continue;
            }
            string name = longName ?? HeaderName(header);
            longName = null;
            if (Inside(name) is not { } path || !Kept(path)) continue;
            found.Add((path, data));
        }
    }

    private static string HeaderName(byte[] header)
    {
        string name = Field(header, 0, 100);
        // POSIX ustar keeps the start of a long path apart, in the prefix
        // field; GNU ("ustar" and a space) uses those bytes for other things.
        bool posix = Encoding.ASCII.GetString(header, 257, 5) == "ustar" && header[262] == 0;
        string prefix = posix ? Field(header, 345, 155) : "";
        return prefix.Length > 0 ? prefix + "/" + name : name;
    }

    private static string Field(byte[] header, int at, int length)
    {
        int end = Array.IndexOf(header, (byte)0, at, length);
        return Encoding.UTF8.GetString(header, at, (end < 0 ? at + length : end) - at);
    }

    private static long Octal(byte[] header, int at, int length)
    {
        long value = 0;
        for (int i = at; i < at + length; i++)
        {
            byte b = header[i];
            if (b is (byte)' ' or 0) { if (value > 0) break; continue; }
            if (b is < (byte)'0' or > (byte)'7') throw new InvalidDataException("not a tar header");
            value = value * 8 + (b - '0');
        }
        return value;
    }

    // A PAX record is "<length> path=<name>\n".
    private static string? PaxPath(byte[] data)
    {
        foreach (string record in Encoding.UTF8.GetString(data).Split('\n'))
        {
            int space = record.IndexOf(' ');
            if (space > 0 && record.AsSpan(space + 1).StartsWith("path="))
                return record[(space + 6)..];
        }
        return null;
    }

    private static bool ReadBlock(Stream stream, byte[] block) => ReadExactly(stream, block);

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int got = stream.Read(buffer, read, buffer.Length - read);
            if (got == 0) return read == 0 && buffer.Length > 0 ? false : throw new InvalidDataException("the archive is cut short");
            read += got;
        }
        return true;
    }

    private static void Skip(Stream stream, long count)
    {
        var sink = new byte[512];
        while (count > 0)
        {
            int got = stream.Read(sink, 0, (int)Math.Min(count, sink.Length));
            if (got == 0) throw new InvalidDataException("the archive is cut short");
            count -= got;
        }
    }

    /// <summary>The entry's path with '/' separators and no leading
    /// <c>./</c>, or null when it is absolute or climbs out with <c>..</c>.</summary>
    private static string? Inside(string name)
    {
        string path = name.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        if (path.Length == 0 || path.StartsWith('/') || path.Contains(':')) return null;
        foreach (string segment in path.Split('/'))
            if (segment == "..") return null;
        return path;
    }

    private static List<(string Path, byte[] Data)> WithoutWrapper(List<(string Path, byte[] Data)> found)
    {
        int slash = found[0].Path.IndexOf('/');
        if (slash <= 0) return found;
        string wrapper = found[0].Path[..(slash + 1)];
        foreach ((string path, _) in found)
            if (!path.StartsWith(wrapper, StringComparison.Ordinal)) return found;
        return found.ConvertAll(f => (f.Path[wrapper.Length..], f.Data));
    }

    private static string Text(Stream content)
    {
        using var reader = new StreamReader(content, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static bool StartsWith(byte[] bytes, params byte[] magic)
        => bytes.AsSpan().StartsWith(magic);

    // POSIX and GNU tars carry "ustar" at offset 257 of the first header.
    private static bool IsTar(byte[] bytes)
        => bytes.Length >= 262 && Encoding.ASCII.GetString(bytes, 257, 5) == "ustar";
}
