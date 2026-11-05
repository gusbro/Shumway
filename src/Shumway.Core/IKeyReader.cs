namespace Shumway.Core;

/// <summary>A reader for an interactive terminal: besides its text it hands out
/// one key at a time, with no echo and no Enter. <c>get_unbuffered_char/1</c>
/// and <c>get_unbuffered_code/1</c> ask it first. A host with its own keyboard
/// implements this on the reader it gives to <c>PrologEngine.In</c>.</summary>
public interface IKeyReader
{
    /// <summary>Waits for one key and gives its code point, or -1 for the end
    /// of input. False, with nothing consumed, when there is no key to wait
    /// for: no terminal, or text that has to be read first. The caller then
    /// reads the text.</summary>
    bool TryReadKey(out int codePoint);
}
