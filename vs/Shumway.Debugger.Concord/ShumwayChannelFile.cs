// Shumway debugger - finding the debuggee's channel without touching the debuggee
// (ADR-035, phase D4).
//
// The engine writes %TEMP%\shumway-debug\<pid>.channel the moment its debug session opens,
// before a line of Prolog is consulted. This reads it.
//
// It replaces the handshake that used to read static fields out of the debuggee. That read
// was not free: it needs a stopped THREAD, in a FRAME whose module can name the type. A
// debugger attaching by hand has one, because the user pressed Break All. A debugger that
// LAUNCHES the process has none -- nothing ever stops -- so there was no moment at which the
// channel could be found, so the hidden breakpoint was never armed, so nothing ever stopped,
// so the program ran to the end with every breakpoint in it dark. A file has no such
// problem, and it works in both cases, which is why there is now only one path.
//
// (Local debugging: the file is on the target machine, and so is the component that reads
// it -- the SERVER component. The IDE side asks the server rather than the disk.)

using System;
using System.Globalization;
using System.IO;

namespace Shumway.Debugger.Concord
{
    internal sealed class ShumwayChannelInfo
    {
        public int Version;
        public long SnapshotAddress;
        public int SnapshotLength;
        public long CommandAddress;
        public int CommandLength;
        public int NotifyMetadataToken;

        /// <summary>The .pl files the engine was told to consult. A breakpoint binds against a
        /// module, a module IS a file, and under a launch nothing has stopped anywhere yet —
        /// so there are no frames to learn the file names from. The engine says them.</summary>
        public string[] Files = new string[0];

        public bool Usable => SnapshotAddress != 0 && SnapshotLength > 0 && NotifyMetadataToken != 0;
    }

    internal static class ShumwayChannelFile
    {
        public static string PathFor(int processId) => Path.Combine(
            Path.GetTempPath(), "shumway-debug",
            processId.ToString(CultureInfo.InvariantCulture) + ".channel");

        /// <summary>Reads the channel a process published, or null if it published none —
        /// which is simply what a program not running under <c>--debug</c> looks like.</summary>
        public static ShumwayChannelInfo? Read(int processId)
        {
            try
            {
                string path = PathFor(processId);
                if (!File.Exists(path)) return null;
                return Parse(File.ReadAllText(path));
            }
            catch (IOException)
            {
                return null;   // being written right now; the next look will find it
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>v1;snapshot=&lt;hex&gt;,&lt;len&gt;;commands=&lt;hex&gt;,&lt;len&gt;;notify=&lt;token&gt;</summary>
        public static ShumwayChannelInfo? Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            var info = new ShumwayChannelInfo();
            foreach (string field in text.Trim().Split(';'))
            {
                if (field.Length == 0) continue;
                if (field[0] == 'v' && field.IndexOf('=') < 0)
                {
                    int version;
                    if (int.TryParse(field.Substring(1), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out version))
                        info.Version = version;
                    continue;
                }

                int equals = field.IndexOf('=');
                if (equals < 0) continue;
                string key = field.Substring(0, equals);
                string value = field.Substring(equals + 1);

                switch (key)
                {
                    case "snapshot":
                        ParsePair(value, out info.SnapshotAddress, out info.SnapshotLength);
                        break;
                    case "commands":
                        ParsePair(value, out info.CommandAddress, out info.CommandLength);
                        break;
                    case "notify":
                        int token;
                        if (int.TryParse(value, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out token))
                            info.NotifyMetadataToken = token;
                        break;
                    case "files":
                        // '|'-separated: a path may contain anything but that.
                        info.Files = value.Length == 0
                            ? new string[0]
                            : value.Split('|');
                        break;
                }
            }

            // A version we do not speak is worse than nothing: reading the buffer anyway
            // would produce a plausible, wrong stack.
            return info.Version == Shumway.Embedding.Debugging.DebugWire.FormatVersion ? info : null;
        }

        private static void ParsePair(string value, out long address, out int length)
        {
            address = 0;
            length = 0;
            int comma = value.IndexOf(',');
            if (comma < 0) return;
            long.TryParse(value.Substring(0, comma), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out address);
            int.TryParse(value.Substring(comma + 1), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out length);
        }
    }
}
