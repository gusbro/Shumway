using System.IO;

namespace Shumway.Core;

/// <summary>
/// Per-engine registry of open Prolog stream handles. Lives on
/// <see cref="Shumway.Embedding.PrologEngine"/>.
///
/// <para>The registry owns:</para>
/// <list type="bullet">
/// <item>The id space — every <see cref="StreamHandle"/> gets a fresh
///   id used both as its <see cref="StreamHandle.Id"/> and as the
///   ordering key for <c>current_stream/3</c>'s enumeration.</item>
/// <item>The two terminal-default handles — <c>user_input</c> (a
///   reader wrapping <see cref="System.Console.In"/>) and
///   <c>user_output</c> (a writer initially pointing at the engine's
///   <see cref="Activation.Out"/>). They're registered with their alias
///   set to the conventional name, so a Prolog program can refer to
///   them either by handle or by atom.</item>
/// <item>The current-input / current-output cursors — updated by
///   <c>set_input/1</c> and <c>set_output/1</c>; default to
///   <c>user_input</c> / <c>user_output</c>.</item>
/// <item>An alias map so <c>open/4</c>'s <c>alias(Name)</c> option can
///   reject duplicates (ISO permission_error) and so a stream-arg
///   atom can be resolved back to its handle.</item>
/// </list>
/// </summary>
public sealed class StreamRegistry
{
    private readonly Dictionary<int, StreamHandle> _byId = new();
    private readonly Dictionary<string, StreamHandle> _byAlias = new();
    private int _nextId;

    public StreamHandle CurrentInput { get; private set; }
    public StreamHandle CurrentOutput { get; private set; }

    /// <summary>The handle representing <c>user_input</c> — the
    /// terminal-default reader. Always registered; its underlying
    /// reader is <see cref="System.Console.In"/>.</summary>
    public StreamHandle UserInput { get; }

    /// <summary>The handle representing <c>user_output</c> — the
    /// terminal-default writer. Always registered; its underlying
    /// writer starts as the engine's <see cref="Activation.Out"/>.</summary>
    public StreamHandle UserOutput { get; }

    /// <summary>The handle representing <c>user_error</c> — the ISO
    /// standard-error writer (`write(user_error,
    /// …)` / `display(user_error, …)` are the conventional diagnostics
    /// channel). Always registered; writes to
    /// <see cref="System.Console.Error"/>.</summary>
    public StreamHandle UserError { get; }

    public StreamRegistry(TextWriter defaultOut, TextReader? defaultIn = null)
    {
        ArgumentNullException.ThrowIfNull(defaultOut);

        UserInput = new StreamHandle(
            id: AllocateId(), reader: defaultIn ?? HostInput(),
            mode: "read", filename: null, alias: "user_input");
        // ISO §7.10.2.4: the standard streams report eof_action(reset)
        // (a tty read past eof may succeed again).
        UserInput.EofAction = "reset";
        Register(UserInput);

        UserOutput = new StreamHandle(
            id: AllocateId(), writer: defaultOut,
            // ISO §7.10.2.4: standard output has mode APPEND.
            mode: "append", filename: null, alias: "user_output");
        Register(UserOutput);

        UserError = new StreamHandle(
            id: AllocateId(), writer: System.Console.Error,
            mode: "append", filename: null, alias: "user_error");
        Register(UserError);

        CurrentInput = UserInput;
        CurrentOutput = UserOutput;
    }

    /// <summary>The host's standard input, or an empty reader when it has none.
    /// A browser-wasm host has no stdin at all: <c>Console.In</c> does not return
    /// an exhausted reader there, it throws — which would take down the FIRST
    /// query of every program, since this registry is built during query setup.
    /// Reading <c>user_input</c> as immediate end-of-file is the honest answer,
    /// and it is what a redirected-from-/dev/null desktop run already does.</summary>
    private static TextReader HostInput()
    {
        try { return System.Console.In; }
        catch (PlatformNotSupportedException) { return TextReader.Null; }
    }

    private int AllocateId() => _nextId++;

    private void Register(StreamHandle h)
    {
        _byId[h.Id] = h;
        if (h.Alias is not null) _byAlias[h.Alias] = h;
    }

    /// <summary>Registers a freshly-built handle, returning the same
    /// instance for chaining.</summary>
    public StreamHandle Add(StreamHandle h)
    {
        Register(h);
        return h;
    }

    /// <summary>Allocates a new id for a handle the caller is about
    /// to build. Use this rather than reaching into <c>_nextId</c>
    /// directly so registries stay consistent.</summary>
    public int NextId() => AllocateId();

    /// <summary>Removes a handle from the registry. Idempotent.
    /// Marks the handle <c>Closed</c> rather than disposing — the
    /// caller owns disposal of the underlying reader/writer.
    ///
    /// <para>Closing the CURRENT input or output moves that cursor back to
    /// <c>user_input</c> / <c>user_output</c> (ISO §8.11.6, and what GNU and
    /// SWI both do). Without it the cursor keeps naming a handle that is no
    /// longer registered, so the very next <c>current_output/1</c> hands the
    /// program a stream term that resolves to nothing — a dangling reference
    /// it can only discover by failing to use it. Done here rather than in
    /// each caller so no future one can forget.</para></summary>
    public void Remove(StreamHandle h)
    {
        h.Closed = true;
        _byId.Remove(h.Id);
        if (h.Alias is not null) _byAlias.Remove(h.Alias);
        if (ReferenceEquals(CurrentInput, h)) CurrentInput = UserInput;
        if (ReferenceEquals(CurrentOutput, h)) CurrentOutput = UserOutput;
    }

    /// <summary>Looks up a handle by its alias; returns null when
    /// none.</summary>
    public StreamHandle? GetByAlias(string alias) =>
        _byAlias.TryGetValue(alias, out var h) ? h : null;

    /// <summary>Looks up a handle by the id its stream-term carries
    /// (<c>'$stream'(Id)</c>); null once the stream is closed. Ids are
    /// allocated monotonically and never reused, so a stale id can never
    /// name a different stream — it just stops resolving.</summary>
    public StreamHandle? GetById(int id) =>
        _byId.TryGetValue(id, out var h) ? h : null;

    /// <summary>True iff <paramref name="alias"/> is already taken —
    /// used by <c>open/4</c> to enforce ISO uniqueness.</summary>
    public bool IsAliasTaken(string alias) => _byAlias.ContainsKey(alias);

    /// <summary>Snapshot of every live handle, ordered by id. Used
    /// by <c>current_stream/3</c> and <c>stream_property/2</c>.</summary>
    public IEnumerable<StreamHandle> All() =>
        _byId.Values.OrderBy(h => h.Id);

    public void SetCurrentInput(StreamHandle h)
    {
        if (!h.IsReader)
            throw new PrologRuntimeException("permission_error", "input,stream");
        CurrentInput = h;
    }

    public void SetCurrentOutput(StreamHandle h)
    {
        if (!h.IsWriter)
            throw new PrologRuntimeException("permission_error", "output,stream");
        CurrentOutput = h;
    }
}
