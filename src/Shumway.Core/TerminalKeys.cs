namespace Shumway.Core;

/// <summary>Single keys from the process's console.</summary>
public static class TerminalKeys
{
    /// <summary>Waits for one key of the console. False when standard input is
    /// not an interactive console.</summary>
    public static bool TryRead(out int codePoint) =>
        TryRead(() => Console.ReadKey(intercept: true), out codePoint);

    /// <summary>As <see cref="TryRead(out int)"/>, with the keys taken from
    /// <paramref name="readKey"/>.</summary>
    public static bool TryRead(Func<ConsoleKeyInfo> readKey, out int codePoint)
    {
        codePoint = -1;
        try
        {
            if (Console.IsInputRedirected) return false;
            codePoint = Next(readKey);
            return true;
        }
        // Some hosts do not mark input as redirected and still have no console.
        catch (InvalidOperationException) { return false; }
        // A browser has no console at all.
        catch (PlatformNotSupportedException) { return false; }
    }

    /// <summary>The code point the next key stands for: Enter is a newline, on
    /// every platform; Ctrl+D is the end of input (-1), and so is Ctrl+Z where it
    /// reaches the program: a Unix terminal keeps it for job control; a key with
    /// no character, such as an arrow, is skipped; a character outside the BMP
    /// arrives as two keys and comes back as one code point (ADR-048).</summary>
    public static int Next(Func<ConsoleKeyInfo> readKey)
    {
        while (true)
        {
            ConsoleKeyInfo key = readKey();
            // By key and modifier: a console may deliver these with no character.
            if ((key.Modifiers & ConsoleModifiers.Control) != 0
                && key.Key is ConsoleKey.D or ConsoleKey.Z) return -1;
            char c = key.KeyChar;
            if (c == '\r') return '\n';
            if (c == '\0') continue;
            if (c is '\u0004' or '\u001A') return -1;
            if (!char.IsHighSurrogate(c)) return c;
            char low = readKey().KeyChar;
            return char.IsLowSurrogate(low) ? char.ConvertToUtf32(c, low) : c;
        }
    }
}
