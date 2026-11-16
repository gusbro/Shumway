namespace Shumway.Core;

/// <summary>A reader that can be asked whether its input has ended, because
/// waiting for the answer is what its source expects: a file or a pipe behind
/// the standard input, and not a person at a console.
/// <c>at_end_of_stream/1</c> asks it of <c>user_input</c>, which otherwise
/// answers that it is not at its end until the end has been read. A host
/// implements this on the reader it gives to <c>PrologEngine.In</c>.</summary>
public interface IEndOfInputReader
{
    /// <summary>True when <see cref="System.IO.TextReader.Peek"/> waits for
    /// the next character and answers -1 only at the end of the input.</summary>
    bool PeekWaitsForInput { get; }
}
