namespace Shumway.WebHost;

/// <summary>What a session needs from the page it lives in (ADR-068). One
/// object per session: an app with several sessions gives each an
/// implementation that knows which one it speaks for.
///
/// <para>Every member is called on the page thread, the one that owns the
/// JavaScript side, when the host was given one; on the calling thread
/// otherwise.</para></summary>
public interface ISessionPage
{
    /// <summary>Appends program output.</summary>
    void Write(string text);

    /// <summary>Appends a diagnostic.</summary>
    void WriteError(string text);

    /// <summary>The running program wants a line of input. The answer comes
    /// back through <see cref="PageInput.Supply"/> or
    /// <see cref="PageInput.SupplyEndOfFile"/>.</summary>
    void AskForInput();
}
