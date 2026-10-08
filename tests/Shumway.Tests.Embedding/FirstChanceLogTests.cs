using System.Runtime.ExceptionServices;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The SHUMWAY_DEBUG_DIAG exception log is written from a
/// FirstChanceException handler. Its own write failing is a first-chance
/// exception too, so with the file held elsewhere the handler re-entered itself
/// until the stack overflowed: the test host crash of the net48-x86 CI lane,
/// where seven test processes append to one log. Exclusive: the event is the
/// whole process's.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class FirstChanceLogTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "shumway-fclog-" + Guid.NewGuid().ToString("N") + ".log");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    private void ThrowOneWhileLogging(string message)
    {
        EventHandler<FirstChanceExceptionEventArgs> log = (_, e) => PrologEngine.LogFirstChance(_path, e.Exception);
        AppDomain.CurrentDomain.FirstChanceException += log;
        try
        {
            try { throw new InvalidOperationException(message); }
            catch (InvalidOperationException) { }
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= log; }
    }

    /// <summary>Reaching the end is the assertion: before the guard, this
    /// overflowed the stack and took the process with it.</summary>
    [Fact]
    public void AFileHeldExclusivelyElsewhereIsSkipped()
    {
        using (new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None))
            ThrowOneWhileLogging("held");
        ThrowOneWhileLogging("free");
        string log = File.ReadAllText(_path);
        Assert.DoesNotContain("held", log);
        Assert.Contains("InvalidOperationException: free", log);
    }

    /// <summary>Another process appending at the same time does not lose the
    /// entry. It did: the write shared the file for reading only.</summary>
    [Fact]
    public void AnotherWriterDoesNotLoseTheEntry()
    {
        using (new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            ThrowOneWhileLogging("concurrent");
        Assert.Contains("InvalidOperationException: concurrent", File.ReadAllText(_path));
    }
}
