using Shumway.Embedding;

namespace Shumway.WebHost;

/// <summary>The sessions of one page (ADR-068): one runtime, an engine per
/// session, and a limit on how many may be alive.
///
/// <para>Creating a session past the limit fails with a message that says
/// so. A session is never closed to make room for another: its user would
/// lose state without being told.</para></summary>
public sealed class SessionHost
{
    private readonly SynchronizationContext? _pageThread;
    private readonly Dictionary<string, HostSession> _sessions = new(StringComparer.Ordinal);

    /// <param name="pageThread">The context of the thread that owns the
    /// JavaScript side, or null when there is none (a desktop test): page
    /// callbacks are then made on the calling thread.</param>
    /// <param name="maxSessions">How many sessions may be alive at once.</param>
    public SessionHost(SynchronizationContext? pageThread, int maxSessions)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSessions, 1);
        _pageThread = pageThread;
        MaxSessions = maxSessions;
    }

    public int MaxSessions { get; }

    public int Count
    {
        get { lock (_sessions) return _sessions.Count; }
    }

    /// <summary>Creates a session. It has no engine yet: the caller starts
    /// one with <see cref="HostSession.StartEngine"/>, on the session's
    /// thread.</summary>
    /// <param name="boot">Builds the session's engine; called on the
    /// session's thread, at each <see cref="HostSession.StartEngine"/>.</param>
    /// <exception cref="InvalidOperationException">The id is taken, or the
    /// limit is reached.</exception>
    public HostSession Create(string id, ISessionPage page, Func<HostSession, PrologEngine> boot)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(boot);
        lock (_sessions)
        {
            if (_sessions.ContainsKey(id))
                throw new InvalidOperationException($"There is already a session '{id}'.");
            if (_sessions.Count >= MaxSessions)
                throw new InvalidOperationException(
                    $"This page already runs {_sessions.Count} Prolog "
                    + (_sessions.Count == 1 ? "session" : "sessions")
                    + $", which is its limit ({MaxSessions}). Close one to start another.");
            var session = new HostSession(id, _pageThread, page, boot);
            _sessions.Add(id, session);
            return session;
        }
    }

    /// <summary>The session with that id, or null.</summary>
    public HostSession? Find(string id)
    {
        lock (_sessions) return _sessions.GetValueOrDefault(id);
    }

    /// <summary>The session with that id.</summary>
    /// <exception cref="KeyNotFoundException">There is none.</exception>
    public HostSession Get(string id)
        => Find(id) ?? throw new KeyNotFoundException($"There is no session '{id}'.");

    /// <summary>Closes a session and frees its place. The task completes when
    /// its thread has let go of the engine; closing an unknown id does
    /// nothing.</summary>
    public Task Close(string id)
    {
        HostSession? session;
        lock (_sessions)
        {
            if (!_sessions.Remove(id, out session)) return Task.CompletedTask;
        }
        return session.Shutdown();
    }

    /// <summary>Closes every session, as a page that is going away does.</summary>
    public Task CloseAll()
    {
        HostSession[] all;
        lock (_sessions)
        {
            all = _sessions.Values.ToArray();
            _sessions.Clear();
        }
        return Task.WhenAll(all.Select(s => s.Shutdown()));
    }
}
