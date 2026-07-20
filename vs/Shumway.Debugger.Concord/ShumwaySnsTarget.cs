// Shumway debugger - Set Next Statement refusal messaging (ADR-035 D5+ cross-frame).
//
// The frame a move targets is the user's Call Stack SELECTION, tracked from the Locals
// refresh (see ShumwaySessionDataItem.SelectedFrame) — never guessed from the clicked
// line: under recursion the same clause sits on several frames at once, and only the
// selection says which one the user means. What lives here is the refusal's
// Output-window summary.

using System.Text;
using Shumway.Embedding.Debugging;

namespace Shumway.Debugger.Concord
{
    internal static class ShumwaySnsTarget
    {
        /// <summary>The refusal's Output-window summary: each frame's valid lines, the
        /// top frame first, capped so a deep stack does not flood the window.</summary>
        public static string DescribeTargets(DebugSnapshot? snapshot)
        {
            if (snapshot == null || snapshot.Frames.Count == 0) return "none at this stop";
            var text = new StringBuilder();
            int described = 0;
            for (int i = 0; i < snapshot.Frames.Count && described < 4; i++)
            {
                DebugSnapshotFrame f = snapshot.Frames[i];
                if (f.SetNextLines.Count == 0) continue;
                if (text.Length > 0) text.Append("; ");
                text.Append(f.Name).Append(" (frame ").Append(i).Append("): ")
                    .Append(string.Join(", ", f.SetNextLines));
                described++;
            }
            return text.Length > 0 ? text.ToString() : "none at this stop";
        }
    }
}
