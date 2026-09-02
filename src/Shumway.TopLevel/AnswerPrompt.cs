namespace Shumway.TopLevel;

/// <summary>What the reader asked for at the more-solutions prompt.</summary>
public enum MoreAnswers
{
    /// <summary>Anything else, <c>.</c> or RETURN — this answer is enough.</summary>
    Stop,
    /// <summary><c>;</c>, SPACE, Tab or <c>n</c>.</summary>
    One,
    /// <summary><c>a</c> — every remaining solution, without asking again.</summary>
    All,
    /// <summary><c>f</c> — on to the next multiple of five.</summary>
    Chunk,
    /// <summary><c>h</c> — list the keys, then ask again.</summary>
    Help,
}

/// <summary>The keys a top level offers once it has an answer and could look
/// for another. Shared so the console and the browser agree on what a key
/// means: the browser reimplements this in JavaScript (it cannot call in), and
/// the two drifting apart is exactly what this being one rule prevents.
/// </summary>
public static class AnswerPrompt
{
    /// <summary>What a keypress asks for. Unrecognised keys stop, which is the
    /// long-standing top-level convention: the prompt is a question whose
    /// default answer is "no more".</summary>
    public static MoreAnswers KeyMeans(char key, bool isTab = false)
    {
        if (isTab) return MoreAnswers.One;
        return key switch
        {
            ';' or ' ' or 'n' => MoreAnswers.One,
            'a' => MoreAnswers.All,
            'f' => MoreAnswers.Chunk,
            'h' => MoreAnswers.Help,
            _ => MoreAnswers.Stop,
        };
    }

    /// <summary>How many answers <c>f</c> asks for, having already shown
    /// <paramref name="shown"/>.
    ///
    /// <para>Not "five more": five is a chunk BOUNDARY, so pressing it fills
    /// out the current group — four after one answer, five after five. Answers
    /// then arrive in aligned blocks however you got there, which is what makes
    /// a long enumeration countable at a glance.</para></summary>
    public static int ChunkAfter(int shown) => 5 - (((shown % 5) + 5) % 5);

    /// <summary>Tracks how many answers still go out before the reader is asked
    /// again — the bookkeeping behind <c>a</c> and <c>f</c>. Kept apart from the
    /// console so the counting is testable without a keyboard: an off-by-one
    /// here is the difference between <c>f</c> showing four answers and five.
    /// </summary>
    public sealed class Pacer
    {
        private int _auto;    // answers still owed without asking
        private bool _all;

        /// <summary>Answers shown so far.</summary>
        public int Shown { get; private set; }

        /// <summary>Records that an answer was just shown, and says whether the
        /// reader has to be asked before the next one is looked for.</summary>
        public bool AskAfterShowing()
        {
            Shown++;
            if (_all) return false;
            if (_auto > 0) { _auto--; return false; }
            return true;
        }

        /// <summary>Applies what they asked for; <c>false</c> means stop.</summary>
        public bool Accept(MoreAnswers asked)
        {
            switch (asked)
            {
                case MoreAnswers.One: return true;
                case MoreAnswers.All: _all = true; return true;
                case MoreAnswers.Chunk: _auto = ChunkAfter(Shown) - 1; return true;
                default: return false;
            }
        }
    }

    /// <summary>The keys, one per line, for <c>h</c>.</summary>
    public static string Help =>
        ";  SPACE  n   the next solution\n"
      + "a               every remaining solution\n"
      + "f               on to the next multiple of five\n"
      + "h               this list\n"
      + ".  RETURN       stop here";
}
