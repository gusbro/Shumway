namespace Shumway.Core;

/// <summary>A reader whose end of input lasts until the program reads past
/// it: a person who ended the input, as Ctrl-D does at a terminal, can type
/// more. A read on a stream with <c>eof_action(reset)</c> that is past its end
/// calls <see cref="Reopen"/> before it tries the source again, and until
/// then the reader keeps answering -1, so a peek and the read after it see the
/// same end. A host implements this on the reader it gives to
/// <c>PrologEngine.In</c>.</summary>
public interface IReopenableInput
{
    /// <summary>The program reads past the end: the next read asks for more.</summary>
    void Reopen();
}
