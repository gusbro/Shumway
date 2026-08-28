using Shumway.Embedding;
using System.Runtime.InteropServices.JavaScript;

namespace Shumway.Web;

/// <summary>
/// Libraries the user brought in, so <c>:- use_module(library(clpz)).</c> works
/// in a page.
///
/// <para>What is imported is a COLLECTION — a directory of Prolog sources on the
/// engine's library search path (ADR-038). Scryer's <c>lib/</c> is one folder
/// and forty-six libraries: every <c>x.pl</c> in it is <c>library(x)</c>. So a
/// collection is named for where it came from, and the libraries are what it
/// contains. It may carry a DIALECT (ADR-040): a library resolved from a
/// directory tagged <c>scryer</c> loads under Scryer's name resolution and
/// double_quotes, which is what lets Scryer's and SWI's versions of the same
/// library coexist.</para>
///
/// <para>COMPILATION is per library, not per collection: nobody wants to wait
/// for forty-six when they came for one. A compiled <c>x.shum</c> sits at the
/// collection's root, which the search path reaches before the sources, so it
/// is what <c>library(x)</c> resolves to from then on.</para>
///
/// <para>Libraries are GLOBAL: they are not part of any workspace, they survive
/// switching between them, and they do not travel in a workspace's zip or
/// share link. Every engine — a fresh one after a workspace switch included —
/// registers them again at startup, which is bookkeeping and costs nothing; a
/// library's CLAUSES arrive when a program imports it.</para>
/// </summary>
internal static partial class WebShumwayApp
{
    internal const string LibrariesRoot = "/libraries";

    /// <summary>Where a library's sources live, under its own directory. NOT on
    /// the search path: what resolves is the compiled bundle beside it, so a
    /// source edited here does nothing until the library is compiled again.
    /// That rule is the layout rather than a permission — the alternative was a
    /// read-only flag somebody has to enforce.
    ///
    /// <para>Until a library HAS been compiled, this directory is searched too,
    /// so an uncompiled library still works — just slowly.</para></summary>
    private const string SourceDir = "src";

    /// <summary>Names the dialect a library loads under. A file rather than a
    /// setting: it belongs to the library, so it survives in the same place the
    /// sources do and cannot drift from them. Hidden from the file listings.</summary>
    private const string DialectMarker = ".dialect";

    /// <summary>What the last compile of one library had to say, kept beside its
    /// bundle as <c>&lt;library&gt;.diag</c>.
    ///
    /// <para>It exists because importing a collection compiles forty-odd
    /// libraries nobody asked for one at a time, and what THEY have to say is
    /// not the page's output: a foreign-interface library from another system
    /// declares functions this engine has no way to bind, and reporting that
    /// forty times buries whatever the user was doing. So the diagnostics go
    /// here and the library list shows which libraries have one — the user
    /// learns that a library is unusable when they look for a library, not
    /// while they are typing a query.</para>
    ///
    /// <para>First line is <c>failed</c> or <c>warned</c>, second is the
    /// headline, the rest is the detail. The kind is written rather than
    /// inferred: a library that compiled once and then failed still has its old
    /// bundle beside it, so the bundle's presence does not say how the last
    /// compile went.</para></summary>
    private const string DiagnosticSuffix = ".diag";

    /// <summary>How many warning lines one compile's diagnostic keeps.</summary>
    private const int DiagnosticLineLimit = 200;

    /// <summary>Puts every imported library on the new engine's search path.
    /// Called from StartEngine, so a reset engine knows them too.</summary>
    internal static void RegisterLibraries()
    {
        Directory.CreateDirectory(LibrariesRoot);
        foreach (string dir in Directory.GetDirectories(LibrariesRoot))
        {
            string marker = Path.Combine(dir, DialectMarker);
            string? dialect = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
            // The library's ROOT first, so a compiled bundle there is what
            // `library(X)` finds; its sources after, so an uncompiled library
            // still resolves. Order is what makes the compiled one win.
            foreach (string searched in new[] { dir, Path.Combine(dir, SourceDir) })
            {
                try
                {
                    if (string.IsNullOrEmpty(dialect))
                        _session!.Engine.AddLibraryDirectory(searched);
                    else _session!.Engine.AddLibraryDirectory(searched, dialect);
                }
                catch (ArgumentException)
                {
                    // An unknown dialect (a marker written by an older build, or
                    // by hand). The library is still usable, just untagged.
                    _session!.Engine.AddLibraryDirectory(searched);
                }
            }
        }
    }

    /// <summary>The imported libraries, newline-separated, sorted.</summary>
    [JSExport]
    internal static Task<string> LibraryNames()
        => OnEngine(() =>
        {
            Directory.CreateDirectory(LibrariesRoot);
            var names = Directory.GetDirectories(LibrariesRoot)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.Ordinal);
            return string.Join('\n', names!);
        });

    /// <summary>The dialect a library loads under, or the empty string.</summary>
    [JSExport]
    internal static Task<string> LibraryDialect(string name)
        => OnEngine(() =>
        {
            string marker = Path.Combine(ResolveLibrary(name), DialectMarker);
            return File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
        });

    /// <summary>Creates (or re-tags) a library and puts it on the search path.
    /// Returns null, or the error text.</summary>
    [JSExport]
    internal static Task<string?> LibraryCreate(string name, string dialect)
        => OnEngine(() =>
        {
            if (InvalidWorkspaceName(name) is { } bad) return bad;
            try
            {
                string dir = ResolveLibrary(name);
                Directory.CreateDirectory(dir);
                string marker = Path.Combine(dir, DialectMarker);
                if (string.IsNullOrWhiteSpace(dialect))
                {
                    if (File.Exists(marker)) File.Delete(marker);
                    _session!.Engine.AddLibraryDirectory(dir);
                }
                else
                {
                    // Tag first: an unknown dialect must not leave a marker
                    // behind that every later boot would trip over.
                    _session!.Engine.AddLibraryDirectory(dir, dialect.Trim());
                    File.WriteAllText(marker, dialect.Trim());
                }
                _session!.Engine.AddLibraryDirectory(Path.Combine(dir, SourceDir));
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        });

    /// <summary>Compiles a library's sources into a bundle beside them, which is
    /// then what <c>library(X)</c> resolves to.
    ///
    /// <para>Worth the wait it costs once: Scryer's clpz loads about six times
    /// faster from a bundle than from source, because the compiling — the
    /// expensive part — has already happened. Compiled by CONSULTING, the only
    /// way that works for a library which GENERATES clauses as it loads, which
    /// is exactly what clpz and its attributed-variable machinery do.</para>
    ///
    /// <para>Returns null, or the diagnostic.</para></summary>
    [JSExport]
    internal static Task<string?> LibraryCompile(string name, string library)
        // NOT on the engine gate. Compiling builds its OWN ephemeral engine and
        // never touches the session's, so holding the gate for its whole
        // duration bought nothing and cost everything: pressing Consult while a
        // big library compiled meant waiting minutes for it to finish. Two
        // engines at once is the model working as designed — activations are
        // single-threaded INTERNALLY and thread-agile between them, and the
        // tables they share are thread-safe.
        => Task.Run<string?>(() =>
        {
            string root = ResolveLibrary(name);
            string sources = Path.Combine(root, SourceDir);
            string entry = Path.Combine(sources, library + ".pl");
            if (!File.Exists(entry)) return $"{name} has no {library}.pl";

            string marker = Path.Combine(root, DialectMarker);
            string? dialect = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;

            // The consult's warnings are CAUGHT, not printed. This runs on the
            // page's behalf rather than the user's, and what a library from
            // another system says while it loads is not their output.
            var said = new StringWriter();
            try
            {
                var errors = new List<ShmoCompileError>();
                var compiled = ShmoViaConsult.CompileMany(
                    new[] { entry }, new[] { sources }, ShmoBuildMode.Release, errors,
                    string.IsNullOrEmpty(dialect) ? null : dialect, said);
                if (errors.Count > 0)
                {
                    string text = string.Join(
                        "\n", errors.Select(e => $"{e.Line}:{e.Column}: {e.Message}"));
                    return RecordDiagnostic(root, library, text, said, failed: true);
                }
                if (compiled.Count == 0)
                    return RecordDiagnostic(root, library, "nothing compiled", said, failed: true);

                // Packed by the LIBRARIAN, not the linker: a library has no entry
                // point, so there is nothing to compute reachability from — every
                // module it brought in is kept.
                byte[] bytes = Librarian.CreateArchive(compiled
                    .Select(c => new BundleArchiveMember(
                        c.ModuleName + ".shmo", ShmoWriter.ToBytes(c.Object)))
                    .ToList());
                // Written under a TEMPORARY name and moved into place, so what
                // `library(X)` can see is either the old bundle or the new one
                // and never half of one — a consult may look while this runs.
                string target = Path.Combine(root, library + ".shum");
                string partial = target + ".partial";
                File.WriteAllBytes(partial, bytes);
                File.Move(partial, target, overwrite: true);
                RecordDiagnostic(root, library, null, said, failed: false);
                return null;
            }
            catch (Exception ex)
            {
                return RecordDiagnostic(root, library, ex.Message, said, failed: true);
            }
        });

    /// <summary>Files what a compile had to say, and gives back the headline —
    /// which is what <see cref="LibraryCompile"/> returns, so a caller that
    /// compiles ONE library on purpose still sees why it did not work.
    ///
    /// <para>A clean compile removes the marker: the record describes the LAST
    /// compile, and a library that has been fixed must stop reading as
    /// broken.</para></summary>
    private static string? RecordDiagnostic(
        string root, string library, string? error, StringWriter said, bool failed)
    {
        string marker = Path.Combine(root, library + DiagnosticSuffix);
        var lines = said.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToList();

        if (!failed && lines.Count == 0)
        {
            if (File.Exists(marker)) File.Delete(marker);
            return null;
        }

        string headline = error is { Length: > 0 }
            ? Flatten(error)
            : $"compiled, but {lines.Count} warning{(lines.Count == 1 ? "" : "s")} while loading it";

        var body = new List<string> { failed ? "failed" : "warned", headline };
        if (error is { Length: > 0 }) body.Add(error);
        if (lines.Count > DiagnosticLineLimit)
        {
            body.AddRange(lines.Take(DiagnosticLineLimit));
            body.Add($"… and {lines.Count - DiagnosticLineLimit} more");
        }
        else body.AddRange(lines);

        try { File.WriteAllText(marker, string.Join("\n", body)); }
        catch { /* a diagnostic that cannot be filed is not worth failing over */ }
        return failed ? headline : null;
    }

    /// <summary>One line, short enough to sit in a list row.</summary>
    private static string Flatten(string text)
    {
        string first = text.Split('\n')[0].TrimEnd('\r').Replace('\t', ' ').Trim();
        return first.Length <= 140 ? first : first[..140] + "…";
    }

    /// <summary>The libraries a collection provides: the Prolog files at the top
    /// of it, which are the names <c>library(X)</c> can ask for. Each line is
    /// the name, a tab, the state — <c>compiled</c>, <c>source</c> or
    /// <c>failed</c> — and, when the last compile had something to say, another
    /// tab and its headline.</summary>
    [JSExport]
    internal static Task<string> LibraryEntries(string name)
        => OnEngine(() =>
        {
            string root = ResolveLibrary(name);
            string sources = Path.Combine(root, SourceDir);
            if (!Directory.Exists(sources)) return "";
            var entries = Directory.GetFiles(sources, "*.pl", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.Ordinal)
                .Select(n => DescribeEntry(root, n!));
            return string.Join('\n', entries);
        });

    private static string DescribeEntry(string root, string library)
    {
        var (kind, headline) = ReadDiagnostic(root, library);
        string state = kind == "failed" ? "failed"
            : File.Exists(Path.Combine(root, library + ".shum")) ? "compiled"
            : "source";
        return headline is null ? $"{library}\t{state}" : $"{library}\t{state}\t{headline}";
    }

    /// <summary>The kind and headline of a library's filed diagnostic, or
    /// nulls when the last compile was clean.</summary>
    private static (string? Kind, string? Headline) ReadDiagnostic(string root, string library)
    {
        string marker = Path.Combine(root, library + DiagnosticSuffix);
        if (!File.Exists(marker)) return (null, null);
        try
        {
            string[] lines = File.ReadAllLines(marker);
            if (lines.Length < 2) return (null, null);
            return (lines[0].Trim(), lines[1]);
        }
        catch { return (null, null); }
    }

    /// <summary>Everything the last compile of one library had to say, or the
    /// empty string. What the library list's <c>details</c> shows.</summary>
    [JSExport]
    internal static Task<string> LibraryDiagnostic(string name, string library)
        => OnEngine(() =>
        {
            string marker = Path.Combine(ResolveLibrary(name), library + DiagnosticSuffix);
            return File.Exists(marker) ? File.ReadAllText(marker) : "";
        });

    /// <summary>Puts a stored diagnostic back, so a library that could not be
    /// compiled still reads as unusable after a reload — that is when it
    /// matters, since the batch that found out runs once, at import.</summary>
    [JSExport]
    internal static Task<string?> LibraryPutDiagnostic(string name, string library, string text)
        => OnEngine(() =>
        {
            try
            {
                string root = ResolveLibrary(name);
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, library + DiagnosticSuffix), text);
                return (string?)null;
            }
            catch (Exception ex) { return ex.Message; }
        });

    /// <summary>Removes a library and its files. It stays on the engine's search
    /// path until the next engine — a path that resolves nothing is harmless.</summary>
    [JSExport]
    internal static Task<string?> LibraryRemove(string name)
        => OnEngine(() =>
        {
            if (InvalidWorkspaceName(name) is { } bad) return bad;
            try
            {
                string dir = ResolveLibrary(name);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        });

    /// <summary>A library's files, newline-separated, sorted — including those in
    /// subdirectories, as relative paths, since <c>library(dcg/basics)</c> is a
    /// real thing. The dialect marker is not one of them.</summary>
    [JSExport]
    internal static Task<string> LibraryFiles(string name)
        => OnEngine(() =>
        {
            string dir = Path.Combine(ResolveLibrary(name), SourceDir);
            if (!Directory.Exists(dir)) return "";
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(dir, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal);
            return string.Join('\n', files);
        });

    /// <summary>One compiled bundle, base64-encoded, or the empty string when
    /// there is none — so the page can put it in storage and not have to compile
    /// the library again on every visit.</summary>
    [JSExport]
    internal static Task<string> LibraryBundle(string name, string library)
        => OnEngine(() =>
        {
            string path = Path.Combine(ResolveLibrary(name), library + ".shum");
            return File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : "";
        });

    /// <summary>Puts a stored bundle back.</summary>
    [JSExport]
    internal static Task<string?> LibraryPutBundle(string name, string library, string base64)
        => OnEngine(() =>
        {
            try
            {
                string root = ResolveLibrary(name);
                Directory.CreateDirectory(root);
                File.WriteAllBytes(Path.Combine(root, library + ".shum"),
                                   Convert.FromBase64String(base64));
                return (string?)null;
            }
            catch (Exception ex) { return ex.Message; }
        });

    /// <summary>A library file's contents, or null when there is no such file.</summary>
    [JSExport]
    internal static Task<string?> LibraryRead(string name, string file)
        => OnEngine(() =>
        {
            string path = ResolveLibraryFile(name, file);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        });

    /// <summary>Creates or replaces a library file. Returns null, or the error.</summary>
    [JSExport]
    internal static Task<string?> LibraryWrite(string name, string file, string content)
        => OnEngine(() =>
        {
            try
            {
                string path = ResolveLibraryFile(name, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
                return (string?)null;
            }
            catch (Exception ex) { return ex.Message; }
        });

    private static string ResolveLibrary(string name)
    {
        if (InvalidWorkspaceName(name) is { } bad) throw new ArgumentException(bad, nameof(name));
        return LibrariesRoot + "/" + name;
    }

    /// <summary>Rejects a path that would leave the library. Its files arrive
    /// from a folder the user picked, and a relative path out of one is not
    /// something to follow.</summary>
    private static string ResolveLibraryFile(string name, string file)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        // A library's FILES are its sources; the bundle beside them is built,
        // not edited.
        string root = Path.Combine(ResolveLibrary(name), SourceDir);
        string full = Path.GetFullPath(Path.Combine(root, file));
        if (!full.StartsWith(root + "/", StringComparison.Ordinal))
            throw new ArgumentException($"'{file}' is outside the library", nameof(file));
        return full;
    }
}
