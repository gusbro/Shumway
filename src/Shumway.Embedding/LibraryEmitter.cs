using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Shumway.Embedding;

/// <summary>Outcome of one <see cref="LibraryEmitter.Emit"/> call.</summary>
public sealed class LibraryEmitResult
{
    public bool Success { get; }
    public string? OutputPath { get; }
    /// <summary>The fully-qualified name of the generated factory class
    /// (<c>Namespace.Class</c>), for the diagnostic the CLI prints.</summary>
    public string? FactoryTypeName { get; }
    public IReadOnlyList<LinkDiagnostic> Diagnostics { get; }

    public LibraryEmitResult(bool success, string? outputPath, string? factoryTypeName,
        IReadOnlyList<LinkDiagnostic> diagnostics)
    {
        Success = success;
        OutputPath = outputPath;
        FactoryTypeName = factoryTypeName;
        Diagnostics = diagnostics;
    }
}

/// <summary>
/// produces a .NET CLASS LIBRARY (<c>.dll</c>) that embeds a
/// <see cref="Bundle"/> and exposes a small generated factory so a host .NET
/// application can spin up a Shumway engine with the bundled program already
/// loaded — no <c>MemoryStream</c> / reflection boilerplate on the consumer side:
///
/// <code>
/// var engine = MyProg.Bundle.CreateEngine();
/// foreach (var sol in engine.QueryAll("main(X).")) ...
/// </code>
///
/// <para>The contrast with <see cref="ExecutableEmitter"/> (<c>--exe</c>): there is
/// NO Prolog goal entry point — the host decides which goals to run, when. The DLL
/// is a library, not a self-launching program.</para>
///
/// <para>Mechanism (like <c>--exe</c>): shells out to <c>dotnet build</c> on a
/// generated temp project. The generated DLL references the Shumway engine
/// assemblies (so <see cref="PrologEngine"/> / <see cref="Bundle"/> are real types
/// in its public API); those dependency DLLs are copied next to the output so a
/// consumer that references the generated DLL has everything it needs.</para>
/// </summary>
public static class LibraryEmitter
{
    /// <summary>The fixed manifest-resource logical name the generated factory reads
    /// the embedded bundle from. Documented so an advanced consumer who wants to
    /// bypass the factory can read it directly.</summary>
    public const string BundleResourceName = "shumway.bundle";

    /// <summary>Builds a class library at <paramref name="outputPath"/> embedding
    /// <paramref name="bundleBytes"/>. The generated factory lives in
    /// <paramref name="namespaceName"/> (null → inferred from the output file name)
    /// and is named <paramref name="className"/> (null → <c>Bundle</c>).</summary>
    public static LibraryEmitResult Emit(
        byte[] bundleBytes,
        string outputPath,
        string? namespaceName = null,
        string? className = null,
        TextWriter? verboseOut = null,
        IReadOnlyList<string>? foreignDllPaths = null,
        IReadOnlyList<string>? nativeDllPaths = null)
    {
        ArgumentNullException.ThrowIfNull(bundleBytes);
        ArgumentNullException.ThrowIfNull(outputPath);

        var diagnostics = new List<LinkDiagnostic>();
        string finalPath = outputPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? outputPath : outputPath + ".dll";
        string assemblyName = ExecutableEmitter.SanitiseAssemblyName(
            Path.GetFileNameWithoutExtension(finalPath));
        string ns = SanitiseNamespace(namespaceName)
            ?? InferNamespace(finalPath);
        string cls = SanitiseIdentifier(className) ?? "Bundle";
        string factoryTypeName = ns + "." + cls;

        string tempDir = Path.Combine(Path.GetTempPath(), $"shumway-dll-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Factory.cs"),
                GenerateFactorySource(ns, cls));
            File.WriteAllBytes(Path.Combine(tempDir, "bundle.shum"), bundleBytes);
            File.WriteAllText(Path.Combine(tempDir, $"{assemblyName}.csproj"),
                GenerateProjectFile(assemblyName, ns));
            // Clear inherited NuGet sources (corporate HTTP proxies NuGet rejects).
            File.WriteAllText(Path.Combine(tempDir, "nuget.config"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <packageSources>\n"
                + "    <clear />\n    <add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" />\n"
                + "  </packageSources>\n</configuration>\n");

            verboseOut?.WriteLine($"shumway-dll: temp project at {tempDir}, factory {factoryTypeName}.");

            string buildDir = Path.Combine(tempDir, "out");
            var (exitCode, stdout, stderr) = RunDotnetBuild(tempDir, assemblyName, buildDir, verboseOut);
            if (exitCode != 0)
            {
                diagnostics.Add(new LinkDiagnostic(LinkSeverity.Error, "build_failed",
                    $"`dotnet build` exited with code {exitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}"));
                return new LibraryEmitResult(false, null, null, diagnostics);
            }

            string producedPath = Path.Combine(buildDir, assemblyName + ".dll");
            if (!File.Exists(producedPath))
            {
                diagnostics.Add(new LinkDiagnostic(LinkSeverity.Error, "dll_not_found",
                    $"Expected '{producedPath}' but it does not exist."));
                return new LibraryEmitResult(false, null, null, diagnostics);
            }

            string outputDir = Path.GetDirectoryName(Path.GetFullPath(finalPath))!;
            Directory.CreateDirectory(outputDir);
            File.Copy(producedPath, finalPath, overwrite: true);

            // Copy the Shumway engine dependency DLLs next to the generated DLL —
            // a consumer referencing it needs them at build + run time.
            foreach (string dll in Directory.GetFiles(buildDir, "*.dll"))
            {
                if (Path.GetFileName(dll).Equals(assemblyName + ".dll", StringComparison.OrdinalIgnoreCase))
                    continue;
                string dst = Path.Combine(outputDir, Path.GetFileName(dll));
                if (Path.GetFullPath(dll) != Path.GetFullPath(dst))
                    File.Copy(dll, dst, overwrite: true);
            }

            // Foreign DLLs: the bundle's LoadBundle probes
            // AppContext.BaseDirectory for these, so a sibling layout works.
            if (foreignDllPaths is not null)
                foreach (var src in foreignDllPaths)
                {
                    string dst = Path.Combine(outputDir, Path.GetFileName(src));
                    if (Path.GetFullPath(src) != Path.GetFullPath(dst))
                        File.Copy(src, dst, overwrite: true);
                }

            // ADR-024 native DLLs: LoadBundle (called by the generated CreateEngine)
            // auto-loads Bundle.NativeLibraries, probing AppContext.BaseDirectory — so
            // the native lib must sit next to the generated DLL, like a foreign one.
            if (nativeDllPaths is not null)
                foreach (var src in nativeDllPaths)
                {
                    string dst = Path.Combine(outputDir, Path.GetFileName(src));
                    if (Path.GetFullPath(src) != Path.GetFullPath(dst))
                        File.Copy(src, dst, overwrite: true);
                    verboseOut?.WriteLine($"shumway-dll: copied native dll '{Path.GetFileName(src)}'");
                }

#if NETFRAMEWORK
            // A Framework CONSUMER exe needs binding redirects that match the
            // versions DEPLOYED HERE — its own build's auto-generated ones can
            // land lower (RAR only walks the references it was handed, not
            // this folder). Ship a ready-to-merge sample computed from the
            // actual files, plus the gcAllowVeryLargeObjects knob the
            // Framework-hosts guide recommends.
            string samplePath = Path.Combine(outputDir, assemblyName + ".app.config.sample");
            File.WriteAllText(samplePath, BuildConsumerConfigSample(outputDir));
            verboseOut?.WriteLine($"shumway-dll: wrote {Path.GetFileName(samplePath)} "
                + "(merge into the consumer exe's app.config).");
#endif
            verboseOut?.WriteLine($"shumway-dll: wrote {finalPath} ({new FileInfo(finalPath).Length:N0} bytes), "
                + $"factory {factoryTypeName}.CreateEngine().");
            return new LibraryEmitResult(true, finalPath, factoryTypeName, diagnostics);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new LinkDiagnostic(LinkSeverity.Error, "dll_emit_error", ex.Message));
            return new LibraryEmitResult(false, null, null, diagnostics);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

#if NETFRAMEWORK
    /// <summary>Binding redirects for every strong-named assembly deployed in
    /// <paramref name="dir"/>, each redirected to exactly the version that
    /// sits there — the one configuration that is correct for THIS folder.</summary>
    private static string BuildConsumerConfigSample(string dir)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <runtime>\n");
        sb.Append("    <gcAllowVeryLargeObjects enabled=\"true\" />\n");
        sb.Append("    <assemblyBinding xmlns=\"urn:schemas-microsoft-com:asm.v1\">\n");
        foreach (string dll in Directory.GetFiles(dir, "*.dll"))
        {
            AssemblyName an;
            try { an = AssemblyName.GetAssemblyName(dll); }
            catch (BadImageFormatException) { continue; }   // native dll alongside
            byte[]? token = an.GetPublicKeyToken();
            if (token is null || token.Length == 0) continue;   // unsigned: simple-name binding, no redirect
            string tokenHex = string.Concat(token.Select(b => b.ToString("x2")));
            sb.Append("      <dependentAssembly>\n");
            sb.Append($"        <assemblyIdentity name=\"{an.Name}\" publicKeyToken=\"{tokenHex}\" culture=\"neutral\" />\n");
            sb.Append($"        <bindingRedirect oldVersion=\"0.0.0.0-{an.Version}\" newVersion=\"{an.Version}\" />\n");
            sb.Append("      </dependentAssembly>\n");
        }
        sb.Append("    </assemblyBinding>\n  </runtime>\n</configuration>\n");
        return sb.ToString();
    }
#endif

    private static string GenerateFactorySource(string ns, string cls) =>
        // Everything fully qualified so the chosen class name can collide with a
        // Shumway type (e.g. the default class `Bundle` vs Shumway.Embedding.Bundle)
        // without a name clash.
        $@"namespace {ns}
{{
    /// <summary>Loads the Prolog program embedded in this assembly. Generated by
    /// <c>shumway-link --dll</c>.</summary>
    public static class {cls}
    {{
        private static Shumway.Embedding.Bundle? _bundle;

        /// <summary>The embedded program bundle (decoded once, then cached). For
        /// advanced hosts that want to configure an engine before loading.</summary>
        public static Shumway.Embedding.Bundle GetBundle() => _bundle ??= LoadEmbeddedBundle();

        /// <summary>A Prolog engine with the embedded program already loaded
        /// (fast path — the bundle's baked, precompiled prelude is used).
        /// Pass <c>debug: true</c> to make it source-level debuggable: attach a
        /// debugger to this process and set breakpoints in the bundled modules
        /// (shown from the source embedded in the bundle). One debugger per
        /// process — a second <c>CreateEngine(debug: true)</c> throws.</summary>
        public static Shumway.Embedding.PrologEngine CreateEngine(bool debug = false)
            => Shumway.Embedding.PrologEngine.FromBundle(
                GetBundle(),
                debug ? new Shumway.Embedding.Debugging.DebugOptions() : null);

        private static Shumway.Embedding.Bundle LoadEmbeddedBundle()
        {{
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(""{BundleResourceName}"")
                ?? throw new System.InvalidOperationException(
                    ""Embedded Shumway bundle resource '{BundleResourceName}' not found in this assembly."");
            using var ms = new System.IO.MemoryStream();
            stream.CopyTo(ms);
            return Shumway.Embedding.BundleReader.FromBytes(ms.ToArray());
        }}
    }}
}}
";

    private static string GenerateProjectFile(string assemblyName, string ns)
    {
        string linkerDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
        var references = new StringBuilder();
        foreach (string dll in ExecutableEmitter.EnumerateRequiredAssemblies(linkerDir))
        {
            string asmName = Path.GetFileNameWithoutExtension(dll);
            references.AppendLine($"    <Reference Include=\"{asmName}\">");
            references.AppendLine($"      <HintPath>{dll}</HintPath>");
            references.AppendLine($"      <Private>true</Private>");
            references.AppendLine($"    </Reference>");
        }
        // The stub inherits the RUNNING toolchain's TFM: the linker's own
        // directory holds the matching Shumway flavors, and a net48 toolchain
        // exists to serve Framework consumers. LangVersion latest on net48
        // (the factory uses nullable annotations; net48 defaults to C# 7.3).
#if NETFRAMEWORK
        const string tfm = "net48";
        const string langVersion = "\n    <LangVersion>latest</LangVersion>";
#else
        const string tfm = "net10.0";
        const string langVersion = "";
#endif
        return $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>{tfm}</TargetFramework>{langVersion}
    <AssemblyName>{assemblyName}</AssemblyName>
    <RootNamespace>{ns}</RootNamespace>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
{references}  </ItemGroup>
  <ItemGroup>
    <EmbeddedResource Include=""bundle.shum"">
      <LogicalName>{BundleResourceName}</LogicalName>
    </EmbeddedResource>
  </ItemGroup>
</Project>
";
    }

    private static (int ExitCode, string Stdout, string Stderr) RunDotnetBuild(
        string projectDir, string assemblyName, string outputDir, TextWriter? verboseOut)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = projectDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        ExecutableEmitter.AddProcessArgs(psi,
            "build", $"{assemblyName}.csproj",
            "-c", "Release",
            "-o", outputDir,
            "--nologo", "-v", "quiet");
        verboseOut?.WriteLine("shumway-dll: dotnet " + ExecutableEmitter.DescribeProcessArgs(psi));
        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>Derives a C# namespace from the output file name — each
    /// dot-separated segment sanitised to an identifier with a capitalised first
    /// letter (e.g. <c>my-rules.dll</c> → <c>My_rules</c>,
    /// <c>Acme.Rules.dll</c> → <c>Acme.Rules</c>).</summary>
    internal static string InferNamespace(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var segs = new List<string>(parts.Length);
        foreach (string p in parts)
        {
            string id = SanitiseIdentifier(p)!;
            segs.Add(char.ToUpperInvariant(id[0]) + id.Substring(1));
        }
        return segs.Count == 0 ? "ShumwayProgram" : string.Join(".", segs);
    }

    /// <summary>Sanitises a dotted namespace (each segment to a valid identifier).
    /// Null/blank → null (caller falls back to the inferred default).</summary>
    private static string? SanitiseNamespace(string? ns)
    {
        if (string.IsNullOrWhiteSpace(ns)) return null;
        var segs = new List<string>();
        foreach (string p in ns!.Split('.', StringSplitOptions.RemoveEmptyEntries))   // ! for net48 (no NotNullWhen there)
            segs.Add(SanitiseIdentifier(p)!);
        return segs.Count == 0 ? null : string.Join(".", segs);
    }

    /// <summary>Sanitises a single C# identifier; null/blank → null.</summary>
    internal static string? SanitiseIdentifier(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var sb = new StringBuilder(name!.Length);   // ! for net48 (no NotNullWhen there)
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        string s = sb.ToString();
        if (s.Length == 0 || char.IsDigit(s[0])) s = "_" + s;
        return s;
    }
}
