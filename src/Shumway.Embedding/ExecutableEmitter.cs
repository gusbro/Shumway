using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;

namespace Shumway.Embedding;

/// <summary>Outcome of one
/// <see cref="ExecutableEmitter.Emit"/> call.</summary>
public sealed class ExecutableEmitResult
{
    public bool Success { get; }
    public string? OutputPath { get; }
    public IReadOnlyList<LinkDiagnostic> Diagnostics { get; }

    public ExecutableEmitResult(bool success, string? outputPath,
        IReadOnlyList<LinkDiagnostic> diagnostics)
    {
        Success = success;
        OutputPath = outputPath;
        Diagnostics = diagnostics;
    }
}

/// <summary>Self-contained vs framework-dependent. Affects only the
/// final exe size and whether the .NET runtime must be installed on
/// the target machine.</summary>
public enum ExecutableDeploymentMode
{
    /// <summary>Default. Single-file exe (~5-10 MB) that requires
    /// the .NET 10 runtime to be installed on the target machine.</summary>
    FrameworkDependent,

    /// <summary>Single-file exe (~70 MB) with the entire .NET
    /// runtime baked in. Runs on a machine that has no .NET
    /// installed at all.</summary>
    SelfContained,
}

/// <summary>
/// Phase 14 chunk 174: produces a single-file native-platform
/// executable that loads an embedded <see cref="Bundle"/> and runs
/// a user-supplied goal at startup, then exits.
///
/// <para>Mechanism: this stage shells out to <c>dotnet publish</c>
/// with <c>PublishSingleFile=true</c>. Internally <c>dotnet publish</c>
/// uses Roslyn — so a "Roslyn-based" build, just orchestrated by the
/// SDK rather than via the in-process compiler APIs. The build
/// machine must have the .NET 10 SDK installed (which it does
/// already, since it's running this very tool).</para>
///
/// <para>The generated wrapper:</para>
/// <list type="number">
/// <item>Embeds the bundle bytes as a <c>bundle.shum</c> manifest
/// resource.</item>
/// <item>On <c>Main</c>: news up a <see cref="PrologEngine"/>, calls
/// <see cref="PrologEngine.LoadBundle(Bundle)"/>, runs the goal via
/// <see cref="PrologEngine.Query"/>.</item>
/// <item>Exits with <c>0</c> on success, <c>1</c> on failure, <c>2</c>
/// on an uncaught Prolog exception or unexpected host error.</item>
/// </list>
/// </summary>
public static class ExecutableEmitter
{
    /// <summary>Parses and validates <paramref name="goal"/>. A
    /// trailing <c>.</c> (the Prolog clause terminator) is stripped
    /// if present so users can pass <c>"main"</c> or <c>"main."</c>
    /// interchangeably. Returns the normalised
    /// <c>goal-as-Prolog-source</c> with a guaranteed trailing dot,
    /// plus the head predicate's <see cref="PredicateRef"/> the linker
    /// should treat as an additional reachability root.</summary>
    public static bool TryValidateGoal(string goal,
        out string normalisedGoal,
        out PredicateRef headPred,
        out string? error)
    {
        normalisedGoal = "";
        headPred = default;
        error = null;
        if (string.IsNullOrWhiteSpace(goal))
        {
            error = "goal is empty.";
            return false;
        }
        string trimmed = goal.Trim();
        if (trimmed.EndsWith('.')) trimmed = trimmed[..^1].Trim();
        if (trimmed.Length == 0)
        {
            error = "goal is empty after stripping trailing '.'.";
            return false;
        }
        Term term;
        try
        {
            var parser = new Parser(new Lexer(trimmed + " ."), OperatorTable.Default());
            term = parser.ReadClauseTerm();
        }
        catch (ParseException ex)
        {
            error = $"goal parse error: {ex.Message}";
            return false;
        }
        switch (term)
        {
            case AtomTerm a:
                headPred = new PredicateRef(a.Name, 0);
                break;
            case CompoundTerm c:
                headPred = new PredicateRef(c.Functor, c.Args.Length);
                break;
            default:
                error = "goal must be a callable term (atom or compound), not a number / variable.";
                return false;
        }
        normalisedGoal = trimmed + ".";
        return true;
    }

    /// <summary>Builds a single-file executable that, on launch,
    /// loads <paramref name="bundleBytes"/> and runs
    /// <paramref name="goal"/>. <paramref name="outputPath"/> is the
    /// final path of the produced binary (typically <c>app</c> on
    /// Linux/macOS, <c>app.exe</c> on Windows — the OS-appropriate
    /// suffix is appended if absent).</summary>
    public static ExecutableEmitResult Emit(
        byte[] bundleBytes,
        string goal,
        string outputPath,
        ExecutableDeploymentMode mode = ExecutableDeploymentMode.FrameworkDependent,
        TextWriter? verboseOut = null,
        IReadOnlyList<string>? foreignDllPaths = null,
        IReadOnlyList<string>? nativeDllPaths = null,
        bool debug = false,
        bool debugWait = false)
    {
        ArgumentNullException.ThrowIfNull(bundleBytes);
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(outputPath);

        var diagnostics = new List<LinkDiagnostic>();
        if (!TryValidateGoal(goal, out string normalisedGoal, out _, out string? validateError))
        {
            diagnostics.Add(new LinkDiagnostic(LinkSeverity.Error,
                "invalid_goal", validateError!));
            return new ExecutableEmitResult(false, null, diagnostics);
        }

        // ADR-035 — a debuggable executable can only show source it carries: at startup it
        // materialises each module's embedded source to a file the debugger opens. If the
        // bundle is source-stripped (inputs compiled --release, or a --strip link) there is
        // nothing to open, so fail loudly at link time rather than ship an undebuggable
        // "debug" exe. At least one non-prelude module must carry source.
        if (debug)
        {
            Bundle inspected;
            try { inspected = BundleReader.FromBytes(bundleBytes); }
            catch (Exception ex)
            {
                diagnostics.Add(new LinkDiagnostic(LinkSeverity.Error,
                    "debug_bundle_unreadable",
                    "cannot inspect the bundle for --debug: " + ex.Message));
                return new ExecutableEmitResult(false, null, diagnostics);
            }
            bool anySource = false;
            foreach (var e in inspected.Entries)
            {
                if (e.ModuleName == Prelude.ModuleName) continue;
                if (!string.IsNullOrEmpty(e.Source)) { anySource = true; break; }
            }
            if (!anySource)
            {
                diagnostics.Add(new LinkDiagnostic(LinkSeverity.Error,
                    "debug_no_source",
                    "--debug requires the bundle to carry module source, but every module in "
                    + "it is source-stripped. Compile the inputs with `shumway-compile --debug` "
                    + "(release .shmo objects are source-stripped) and link without --strip, so "
                    + "the executable can materialise the source the debugger opens."));
                return new ExecutableEmitResult(false, null, diagnostics);
            }
        }

        string finalPath = AdjustExecutableSuffix(outputPath);
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"shumway-exe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string assemblyName = SanitiseAssemblyName(
                Path.GetFileNameWithoutExtension(finalPath));
            string rid = RuntimeInformation.RuntimeIdentifier;

            // Write wrapper sources.
            File.WriteAllText(Path.Combine(tempDir, "Program.cs"),
                GenerateProgramSource(normalisedGoal, debug, debugWait));
            File.WriteAllBytes(Path.Combine(tempDir, "bundle.shum"), bundleBytes);
            File.WriteAllText(Path.Combine(tempDir, $"{assemblyName}.csproj"),
                GenerateProjectFile(assemblyName, rid, mode));
            // Drop a repo-local nuget.config that clears any inherited
            // sources (corporate proxies, HTTP sources NuGet 6+
            // rejects, etc.) and only uses nuget.org. Otherwise a
            // global ~/.nuget config can break the build.
            File.WriteAllText(Path.Combine(tempDir, "nuget.config"),
                @"<?xml version=""1.0"" encoding=""utf-8""?>
<configuration>
  <packageSources>
    <clear />
    <add key=""nuget.org"" value=""https://api.nuget.org/v3/index.json"" />
  </packageSources>
</configuration>
");

            verboseOut?.WriteLine($"shumway-exe: temp project at {tempDir}, rid={rid}, "
                + $"mode={(mode == ExecutableDeploymentMode.SelfContained ? "self-contained" : "framework-dependent")}.");

            // Run dotnet publish.
            string publishDir = Path.Combine(tempDir, "publish");
            var (exitCode, stdout, stderr) = RunDotnetPublish(tempDir, assemblyName,
                rid, mode, publishDir, verboseOut);
            if (exitCode != 0)
            {
                diagnostics.Add(new LinkDiagnostic(LinkSeverity.Error,
                    "publish_failed",
                    $"`dotnet publish` exited with code {exitCode}.\n"
                    + $"stdout:\n{stdout}\n"
                    + $"stderr:\n{stderr}"));
                return new ExecutableEmitResult(false, null, diagnostics);
            }

            string producedName = assemblyName + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ".exe" : "");
            string producedPath = Path.Combine(publishDir, producedName);
            if (!File.Exists(producedPath))
            {
                diagnostics.Add(new LinkDiagnostic(LinkSeverity.Error,
                    "exe_not_found",
                    $"Expected single-file exe at '{producedPath}' but it does not exist."));
                return new ExecutableEmitResult(false, null, diagnostics);
            }

            string? outputDir = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);
            File.Copy(producedPath, finalPath, overwrite: true);
            verboseOut?.WriteLine($"shumway-exe: wrote {finalPath} "
                + $"({new FileInfo(finalPath).Length:N0} bytes).");

            // Chunk 247: copy each --foreign-dll next to the
            // produced executable. The runtime's LoadBundle path
            // (called from the generated Program.Main) probes the
            // executable's AppContext.BaseDirectory for the names
            // recorded in Bundle.ForeignAssemblies, so a sibling
            // layout is exactly what it expects.
            if (foreignDllPaths is not null && foreignDllPaths.Count > 0)
            {
                string sideDir = string.IsNullOrEmpty(outputDir)
                    ? Directory.GetCurrentDirectory() : outputDir;
                foreach (var src in foreignDllPaths)
                {
                    string dst = Path.Combine(sideDir, Path.GetFileName(src));
                    if (Path.GetFullPath(src) != Path.GetFullPath(dst))
                    {
                        File.Copy(src, dst, overwrite: true);
                        verboseOut?.WriteLine($"shumway-exe: copied foreign dll '{Path.GetFileName(src)}'");
                    }
                }
            }
            // ADR-024: copy each --native-dll next to the executable too, so the
            // LoadBundle native-library auto-load (Bundle.NativeLibraries) finds them
            // in the executable's directory.
            if (nativeDllPaths is not null && nativeDllPaths.Count > 0)
            {
                string sideDir = string.IsNullOrEmpty(outputDir)
                    ? Directory.GetCurrentDirectory() : outputDir;
                foreach (var src in nativeDllPaths)
                {
                    string dst = Path.Combine(sideDir, Path.GetFileName(src));
                    if (Path.GetFullPath(src) != Path.GetFullPath(dst))
                    {
                        File.Copy(src, dst, overwrite: true);
                        verboseOut?.WriteLine($"shumway-exe: copied native dll '{Path.GetFileName(src)}'");
                    }
                }
            }
            return new ExecutableEmitResult(true, finalPath, diagnostics);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new LinkDiagnostic(LinkSeverity.Error,
                "exe_emit_error", ex.Message));
            return new ExecutableEmitResult(false, null, diagnostics);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // ----- Wrapper source generation -----

    private static string GenerateProgramSource(
        string goalWithTrailingDot, bool debug, bool debugWait)
    {
        // Escape for a C# string literal (verbatim form so backslashes
        // pass through cleanly).
        string escaped = goalWithTrailingDot.Replace("\"", "\"\"");

        // ADR-035 — a --debug exe compiles its modules debuggable and materialises their
        // embedded source, so a debugger attached to the process (at any time) can set
        // breakpoints and step. --debug-wait additionally blocks at startup until a debugger
        // has attached and armed its breakpoints, so the very first goal can be stopped in.
        string debugBanner = debugWait
            ? @"System.Console.Error.WriteLine(
                ""shumway: debug mode active; waiting for a debugger to attach..."");"
            : @"if (System.Environment.GetEnvironmentVariable(""SHUMWAY_DEBUG_DIAG"") == ""1"")
                System.Console.Error.WriteLine(""shumway: debug mode active."");";
        string engineConstruction = debug
            ? $@"{debugBanner}
            var engine = PrologEngine.FromBundle(LoadEmbeddedBundle(),
                new Shumway.Embedding.Debugging.DebugOptions {{ WaitForAttach = {(debugWait ? "true" : "false")} }});"
            : @"var engine = PrologEngine.FromBundle(LoadEmbeddedBundle());";

        return $@"using Shumway.Embedding;
using System.IO;
using System.Reflection;

internal static class Program
{{
    public static int Main(string[] args)
    {{
        try
        {{
            // Fast startup: a bare engine loads the bundle's baked, precompiled
            // prelude (shumway-link --exe bakes it) instead of parsing +
            // compiling the ~780-line prelude at runtime; falls back to
            // consulting it if the bundle carries none.
            {engineConstruction}
            // Chunk 173: opt-in Tier-1 IL with per-opcode debug markers.
            // Set SHUMWAY_IL_PROMOTE=N (N>=1) to enable promotion,
            // optionally SHUMWAY_IL_DEBUG=1 to inject post-opcode
            // WAM-semantics assertions in the IL.
            string? promoteStr = System.Environment.GetEnvironmentVariable(""SHUMWAY_IL_PROMOTE"");
            if (int.TryParse(promoteStr, out int promoteN) && promoteN > 0)
                engine.IlPromotion.Threshold = promoteN;
            if (System.Environment.GetEnvironmentVariable(""SHUMWAY_IL_DEBUG"") == ""1"")
                Shumway.Compiler.Il.IlPredicateCompiler.DebugMode = true;
            if (System.Environment.GetEnvironmentVariable(""SHUMWAY_CP_TRACE"") == ""1"")
                Shumway.Core.Activation.TraceCpStack = true;
            // Surface the executable's CLI args to the Prolog program
            // as the `argv` Prolog flag. Match SWI / GNU / SICStus
            // semantics: argv[0] is the program path / name, args
            // proper start at argv[1]. (.NET's Main(args) drops the
            // program name; we reconstruct it via
            // GetCommandLineArgs which keeps it.) Programs that
            // strip argv[0] before reading their own args — the
            // SWI / SICStus idiom — work as-is.
            engine.Flags.Argv = System.Environment.GetCommandLineArgs();
            var sol = engine.Query(@""{escaped}"");
            // halt/1 inside the goal is captured by the engine into
            // LastHaltExitCode; honour it as the process exit code,
            // matching SWI / SICStus stand-alone semantics. Without
            // this a Blint-like program that ends with halt(0) would
            // come back as a failed solution and the wrapper would
            // exit 1 even though the program succeeded.
            if (engine.LastHaltExitCode is int haltCode)
                return haltCode;
            return sol.Success ? 0 : 1;
        }}
        catch (Shumway.Core.PrologRuntimeException ex)
        {{
            System.Console.Error.WriteLine(""shumway: uncaught: "" + ex.Message);
            return 2;
        }}
        catch (System.Exception ex)
        {{
            System.Console.Error.WriteLine(""shumway: error: "" + ex.Message);
            return 2;
        }}
    }}

    private static Bundle LoadEmbeddedBundle()
    {{
        var asm = Assembly.GetExecutingAssembly();
        string? name = null;
        foreach (var n in asm.GetManifestResourceNames())
            if (n.EndsWith(""bundle.shum"")) {{ name = n; break; }}
        if (name is null)
            throw new System.InvalidOperationException(
                ""shumway-exe: bundle.shum resource not found in the binary."");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return BundleReader.FromBytes(ms.ToArray());
    }}
}}
";
    }

    private static string GenerateProjectFile(string assemblyName, string rid,
        ExecutableDeploymentMode mode)
    {
        string selfContained = mode == ExecutableDeploymentMode.SelfContained
            ? "true" : "false";
        // EnableCompressionInSingleFile requires SelfContained=true
        // (NETSDK1176). Toggle the property accordingly.
        string compressionProp = mode == ExecutableDeploymentMode.SelfContained
            ? "    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>\n"
            : "";
        // Locate the engine assemblies next to the running shumway-link
        // process. They are shipped alongside it; the temp project
        // references them via absolute HintPath.
        string linkerDir = Path.GetDirectoryName(
            Assembly.GetExecutingAssembly().Location) ?? "";
        var references = new StringBuilder();
        foreach (string dll in EnumerateRequiredAssemblies(linkerDir))
        {
            string asmName = Path.GetFileNameWithoutExtension(dll);
            references.AppendLine($"    <Reference Include=\"{asmName}\">");
            references.AppendLine($"      <HintPath>{dll}</HintPath>");
            references.AppendLine($"      <Private>true</Private>");
            references.AppendLine($"    </Reference>");
        }
        return $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>{assemblyName}</AssemblyName>
    <RuntimeIdentifier>{rid}</RuntimeIdentifier>
    <SelfContained>{selfContained}</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
{compressionProp}    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
{references}
  </ItemGroup>
  <ItemGroup>
    <EmbeddedResource Include=""bundle.shum"" />
  </ItemGroup>
</Project>
";
    }

    internal static IEnumerable<string> EnumerateRequiredAssemblies(string dir)
    {
        // Every Shumway.*.dll and Sigil.dll alongside the linker is a
        // candidate engine dependency. The publish step's reference
        // resolution prunes anything actually unused.
        foreach (string file in Directory.GetFiles(dir, "Shumway.*.dll"))
            yield return file;
        string sigil = Path.Combine(dir, "Sigil.dll");
        if (File.Exists(sigil)) yield return sigil;
    }

    // ----- Tool invocation -----

    private static (int ExitCode, string Stdout, string Stderr) RunDotnetPublish(
        string projectDir, string assemblyName, string rid,
        ExecutableDeploymentMode mode, string outputDir, TextWriter? verboseOut)
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
        psi.ArgumentList.Add("publish");
        psi.ArgumentList.Add($"{assemblyName}.csproj");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Release");
        psi.ArgumentList.Add("-r"); psi.ArgumentList.Add(rid);
        psi.ArgumentList.Add($"-p:SelfContained={(mode == ExecutableDeploymentMode.SelfContained ? "true" : "false")}");
        psi.ArgumentList.Add("-p:PublishSingleFile=true");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add(outputDir);
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("quiet");

        verboseOut?.WriteLine("shumway-exe: dotnet " + string.Join(" ", psi.ArgumentList));
        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout, stderr);
    }

    private static string AdjustExecutableSuffix(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return path;
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? path : path + ".exe";
    }

    internal static string SanitiseAssemblyName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        string s = sb.ToString();
        if (s.Length == 0 || char.IsDigit(s[0])) s = "_" + s;
        return s;
    }
}
