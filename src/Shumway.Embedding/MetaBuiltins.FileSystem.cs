using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

public static partial class MetaBuiltins
{
    // ============================================================================
    // Arity-Prolog file-system operations.
    // Thin wrappers over System.IO. ISO error shapes for instantiation /
    // existence / permission failures so catch/3 can match them.
    // ============================================================================

    public static bool Mkdir1(Activation engine)
    {
        string path = RequireAtomPath(engine, register: 0, builtin: "mkdir/1");
        try { System.IO.Directory.CreateDirectory(path); }
        catch (UnauthorizedAccessException)
        {
            throw new ShumwayPrologException(
                IsoError.PermissionError("create", "directory", new AtomTerm(path)));
        }
        catch (IOException ex)
        {
            throw new ShumwayPrologException(
                IsoError.SystemError(ex.Message));
        }
        return true;
    }

    public static bool Rmdir1(Activation engine)
    {
        string path = RequireAtomPath(engine, register: 0, builtin: "rmdir/1");
        if (!System.IO.Directory.Exists(path))
            throw new ShumwayPrologException(
                IsoError.ExistenceError("directory", new AtomTerm(path)));
        try { System.IO.Directory.Delete(path, recursive: false); }
        catch (IOException)
        {
            // Non-empty directory or in use.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            throw new ShumwayPrologException(
                IsoError.PermissionError("delete", "directory", new AtomTerm(path)));
        }
        return true;
    }

    public static bool Delete1(Activation engine)
    {
        string path = RequireAtomPath(engine, register: 0, builtin: "delete/1");
        if (!System.IO.File.Exists(path))
            throw new ShumwayPrologException(
                IsoError.ExistenceError("source_sink", new AtomTerm(path)));
        try { System.IO.File.Delete(path); }
        catch (UnauthorizedAccessException)
        {
            throw new ShumwayPrologException(
                IsoError.PermissionError("delete", "source_sink", new AtomTerm(path)));
        }
        catch (IOException ex)
        {
            throw new ShumwayPrologException(
                IsoError.SystemError(ex.Message));
        }
        return true;
    }

    public static bool Rename2(Activation engine)
    {
        string from = RequireAtomPath(engine, register: 0, builtin: "rename/2");
        string to = RequireAtomPath(engine, register: 1, builtin: "rename/2");
        if (!System.IO.File.Exists(from))
            throw new ShumwayPrologException(
                IsoError.ExistenceError("source_sink", new AtomTerm(from)));
        if (System.IO.File.Exists(to))
            throw new ShumwayPrologException(
                IsoError.PermissionError("create", "source_sink", new AtomTerm(to)));
        try { System.IO.File.Move(from, to); }
        catch (UnauthorizedAccessException)
        {
            throw new ShumwayPrologException(
                IsoError.PermissionError("modify", "source_sink", new AtomTerm(from)));
        }
        catch (IOException ex)
        {
            throw new ShumwayPrologException(
                IsoError.SystemError(ex.Message));
        }
        return true;
    }

    public static bool ExistsFile1(Activation engine)
    {
        string path = RequireAtomPath(engine, register: 0, builtin: "exists_file/1");
        return System.IO.File.Exists(path);
    }

    // SWI-compatible getenv/2: unify Value with
    // the environment variable's contents, or FAIL (not error) when unset —
    // callers rely on the failure branch for defaults:
    // `(getenv('X', V) ; V = default)`.
    public static bool GetEnv2(Activation engine)
    {
        string name = RequireAtomPath(engine, register: 0, builtin: "getenv/2");
        string? value = Environment.GetEnvironmentVariable(name);
        if (value is null) return false;
        Cell c = Materializer.MaterializeAsCell(engine, new AtomTerm(value));
        return engine.UnifyRegisterWithCell(1, c);
    }

    public static bool ExistsDirectory1(Activation engine)
    {
        string path = RequireAtomPath(engine, register: 0, builtin: "exists_directory/1");
        return System.IO.Directory.Exists(path);
    }

    // ============================================================================
    // process / file-metadata primitives (Logtalk os backend).
    // ============================================================================

    /// <summary>Runs <paramref name="command"/> through the platform shell
    /// and returns its exit code. Blocking; stdout/stderr inherit the
    /// process streams (matching SWI / GNU shell/1-2 behaviour).</summary>
    private static int RunShell(string command)
    {
        var psi = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd.exe", "/C " + command)
            : new System.Diagnostics.ProcessStartInfo("/bin/sh", "-c \"" + command.Replace("\"", "\\\"") + "\"");
        psi.UseShellExecute = false;
        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
                throw new ShumwayPrologException(IsoError.SystemError(
                    "shell: failed to start the platform shell."));
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ShumwayPrologException(IsoError.SystemError(
                $"shell: {ex.Message}"));
        }
    }

    public static bool Shell1(Activation engine)
    {
        string command = RequireAtomPath(engine, register: 0, builtin: "shell/1");
        return RunShell(command) == 0;
    }

    public static bool Shell2(Activation engine)
    {
        string command = RequireAtomPath(engine, register: 0, builtin: "shell/2");
        int status = RunShell(command);
        return engine.UnifyRegisterWithCell(1, Cell.Int(status));
    }

    public static bool Pid1(Activation engine)
        => engine.UnifyRegisterWithCell(
            0, Cell.Int(Environment.ProcessId));

    /// <summary><c>'$choice_level'(-Level)</c> — unifies Level with the
    /// engine's current choice-point pointer B (a stack offset; larger =
    /// more recent). Sampling it before and after a goal detects whether
    /// the goal left a choice point (the determinism test backing the
    /// prelude's <c>call_det/2</c>, used by lgtunit's <c>deterministic/1,2</c>).
    /// Internal — not an ISO/public builtin.</summary>
    public static bool ChoiceLevel1(Activation engine)
        => engine.UnifyRegisterWithCell(0, Cell.Int(engine.B));

    public static bool Sleep1(Activation engine)
    {
        Cell c = MaterializeRegisterAsCell(engine, 0);
        double seconds = c.Tag switch
        {
            Tag.Int => c.AsInt,
            Tag.Float => Cell.DecodeFloat(c, engine.GetHeap(c.FloatPairedIndex)),
            Tag.Ref or Tag.AttVar => throw new ShumwayPrologException(IsoError.InstantiationError()),
            _ => throw new ShumwayPrologException(IsoError.TypeError("number", new AtomTerm("sleep"))),
        };
        if (seconds > 0)
            System.Threading.Thread.Sleep(TimeSpan.FromSeconds(seconds));
        return true;
    }

    public static bool FileSize2(Activation engine)
    {
        string path = RequireAtomPath(engine, register: 0, builtin: "file_size/2");
        var info = new System.IO.FileInfo(path);
        if (!info.Exists)
            throw new ShumwayPrologException(
                IsoError.ExistenceError("source_sink", new AtomTerm(path)));
        return engine.UnifyRegisterWithCell(1, Cell.Int(info.Length));
    }

    public static bool FileModificationTime2(Activation engine)
    {
        string path = RequireAtomPath(engine, register: 0, builtin: "file_modification_time/2");
        var info = new System.IO.FileInfo(path);
        if (!info.Exists)
            throw new ShumwayPrologException(
                IsoError.ExistenceError("source_sink", new AtomTerm(path)));
        long epoch = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
        return engine.UnifyRegisterWithCell(1, Cell.Int(epoch));
    }

    public static bool DirectoryFiles2(Activation engine)
    {
        string path = RequireAtomPath(engine, register: 0, builtin: "directory_files/2");
        if (!System.IO.Directory.Exists(path))
            throw new ShumwayPrologException(
                IsoError.ExistenceError("directory", new AtomTerm(path)));
        Term list = new AtomTerm("[]");
        var entries = System.IO.Directory.GetFileSystemEntries(path);
        // Reverse source order so the cons chain lists them in order;
        // prepend '..' and '.' last so they head the list (SWI shape).
        for (int i = entries.Length - 1; i >= 0; i--)
            list = new CompoundTerm(".", new[]
                { (Term)new AtomTerm(System.IO.Path.GetFileName(entries[i])), list });
        list = new CompoundTerm(".", new[] { (Term)new AtomTerm(".."), list });
        list = new CompoundTerm(".", new[] { (Term)new AtomTerm("."), list });
        Cell cell = Materializer.MaterializeAsCell(engine, list);
        return engine.UnifyRegisterWithCell(1, cell);
    }

    // ============================================================================
    // pseudo-random generation.
    // ============================================================================

    public static bool Randomize1(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "randomize/1");
        Cell c = MaterializeRegisterAsCell(engine, 0);
        if (c.Tag == Tag.Ref || c.Tag == Tag.AttVar)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        if (c.Tag != Tag.Int)
            throw new ShumwayPrologException(
                IsoError.TypeError("integer", new IntTerm(0)));
        host.Randomize((int)c.AsInt);
        return true;
    }

    /// <summary><c>get_seed(-Seed)</c>.
    /// The engine's <see cref="System.Random"/> doesn't expose its internal
    /// state, so we use the standard reseed trick: draw a fresh seed value,
    /// reseed the generator with it, and return it — a later
    /// <c>set_seed(Seed)</c> then reproduces exactly the sequence that
    /// follows this call.</summary>
    public static bool GetSeed1(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "get_seed/1");
        int seed = host.Random.Next();
        host.Randomize(seed);
        return engine.UnifyRegisterWithCell(0, Cell.Int(seed));
    }

    /// <summary><c>set_seed(+Seed)</c> — reseeds the engine's random
    /// generator; alias of <c>randomize/1</c> under the name Logtalk's
    /// backend_random object expects.</summary>
    public static bool SetSeed1(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "set_seed/1");
        Cell c = MaterializeRegisterAsCell(engine, 0);
        if (c.Tag == Tag.Ref || c.Tag == Tag.AttVar)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        if (c.Tag != Tag.Int)
            throw new ShumwayPrologException(
                IsoError.TypeError("integer", new IntTerm(0)));
        host.Randomize((int)c.AsInt);
        return true;
    }

    public static bool Random1(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "random/1");
        double v = host.Random.NextDouble();
        Cell c = Materializer.MaterializeAsCell(engine, new FloatTerm(v));
        return engine.UnifyRegisterWithCell(0, c);
    }

    public static bool RandomBetween3(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "random_between/3");
        Cell loCell = MaterializeRegisterAsCell(engine, 0);
        Cell hiCell = MaterializeRegisterAsCell(engine, 1);
        if (loCell.Tag == Tag.Ref || loCell.Tag == Tag.AttVar
            || hiCell.Tag == Tag.Ref || hiCell.Tag == Tag.AttVar)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        if (loCell.Tag != Tag.Int || hiCell.Tag != Tag.Int)
            throw new ShumwayPrologException(
                IsoError.TypeError("integer", new IntTerm(0)));
        long lo = loCell.AsInt;
        long hi = hiCell.AsInt;
        if (lo > hi) return false;
        // System.Random.Next(int, int) is [min, max) — extend by one
        // to get SWI's [lo, hi] inclusive semantics. Long range guarded
        // against int overflow via NextInt64 when available.
        long v = lo + (long)(host.Random.NextDouble() * (hi - lo + 1));
        if (v > hi) v = hi;  // floating-point edge case
        return engine.UnifyRegisterWithCell(2, Cell.Int(v));
    }

}
