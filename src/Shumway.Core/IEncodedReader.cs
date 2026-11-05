using System.Text;

namespace Shumway.Core;

/// <summary>A reader for <c>user_input</c> that knows the encoding of what it reads.
/// A host implements this on the reader it gives to <c>PrologEngine.In</c> when the
/// reader is neither a <see cref="System.IO.StreamReader"/> nor the console.</summary>
public interface IEncodedReader
{
    /// <summary>The encoding of the bytes behind the text.</summary>
    Encoding Encoding { get; }
}
