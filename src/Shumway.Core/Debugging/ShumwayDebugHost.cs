using System;
using System.Runtime.CompilerServices;

namespace Shumway.Core.Debugging;

/// <summary>
/// ADR-035 — the two entry points a debugger reaches into the engine by NAME, and the
/// reason they live in <c>Shumway.Core</c> rather than next to the session that
/// implements them.
///
/// <para>A debugger evaluates an expression in the context of a FRAME, and a frame can
/// only name types its own module can see. The frame we stop on is whichever engine frame
/// is topmost — usually the interpreter, sometimes the machine itself — and
/// <c>Shumway.Interpreter</c> does not reference <c>Shumway.Embedding</c>: the dependency
/// runs the other way. So a helper that lives in Embedding is, from the debugger's point
/// of view, unnameable — "the debugger is unable to evaluate this expression", which is
/// exactly what the first run in Visual Studio said. Core is the one assembly every engine
/// module references, so a name here can be spoken from any frame the debugger can stop
/// on. The work still happens in Embedding; only the door is here.</para>
///
/// <para><b>Notify</b> is the stop: the debugger plants a hidden breakpoint on it, and the
/// engine calls it once the snapshot is already written. It is deliberately the dullest
/// method in the codebase — nothing to marshal, nothing to fail — because everything about
/// it that matters happens in the debugger, not here.</para>
/// </summary>
public static class ShumwayDebugHost
{
    /// <summary>Rises on every stop. A debugger that missed a notification can tell; and a
    /// method with an observable effect is a method the JIT may not delete.</summary>
    public static volatile int NotifyCount;

    // ----- the handshake: FIELDS, not a method -----
    //
    // A debugger asking the debuggee a question can do it two ways, and they are not
    // equally available. READING a field only inspects memory. CALLING a method means
    // running code in the debuggee, on a particular thread — and Visual Studio will not run
    // code on a thread that is not the current one. The thread the engine runs on very often
    // is not: a Break All lands the IDE's current thread wherever it likes, and the very
    // first run of this in VS found it parked on the REPL's ESC watcher while the stack we
    // needed belonged to another thread entirely. The method call failed; a field read on
    // the same frame worked.
    //
    // So the addresses of the pinned buffers are just sitting here, in plain fields, for
    // anyone who can read memory. Which is exactly what a debugger is.

    /// <summary>The wire format a session is speaking, or 0 when none is running. A debugger
    /// reads this FIRST: it says both "there is a session" and "we agree about the
    /// layout".</summary>
    public static volatile int SessionFormatVersion;

    /// <summary>Address and length of the pinned snapshot buffer (the engine writes, the
    /// debugger reads), and of the command region (the debugger writes, the engine
    /// drains). Zero when no session is running.</summary>
    public static long SnapshotAddress;
    public static int SnapshotLength;
    public static long CommandAddress;
    public static int CommandLength;

    /// <summary>The metadata token of <see cref="Notify"/> — the address the debugger plants
    /// its hidden breakpoint at. Published here for the same reason as everything else in
    /// this block: the debugger can READ it, where asking for it (a reflection call in the
    /// debuggee) would mean running code on a thread it may not be allowed to run code
    /// on.</summary>
    public static readonly int NotifyMetadataToken =
        typeof(ShumwayDebugHost).GetMethod(nameof(Notify))!.MetadataToken;

    /// <summary>Set by the debug session that owns the channel. Null when none is running,
    /// which is what an attaching debugger is told.</summary>
    public static Func<string>? OnAttach;

    /// <summary>Set by the debug session. See <see cref="CaptureNow"/>.</summary>
    public static Func<int>? OnCaptureNow;

    /// <summary>The stop. The debugger's hidden breakpoint lives on this method.
    ///
    /// <para>NoInlining because a breakpoint needs a method to be planted on, and
    /// NoOptimization because a method that does nothing observable is a method the JIT is
    /// entitled to make disappear.</para></summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static void Notify(int reason)
    {
        NotifyCount++;
        // The debugger stops the process here. Nothing else belongs in this method:
        // whatever it needs is in the channel, and it reads that with ReadMemory.
        //
        // The loop below runs ZERO iterations and exists for the JIT, not the program: a
        // loop with no call in it forces the whole method to carry FULLY-INTERRUPTIBLE GC
        // info, which makes the breakpoint's IP a GC-safe point even in a Release build.
        // Without it, a func-eval at this stop (Immediate window, Locals edit) is refused —
        // "stopped at a point where garbage collection is impossible" — whenever the
        // engine assemblies are optimized, because MinOpts alone emits only partially-
        // interruptible code whose safe points are call sites, and this method has none.
        for (int i = NotifyCount; i > int.MaxValue - 1; i++) { }
    }

    /// <summary>The handshake — the ONE func-eval the design allows at attach. Returns the
    /// pinned channel addresses, or <c>""</c> if no debug session is running in this
    /// process.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static string Attach()
    {
        Func<string>? handler = OnAttach;
        return handler is null ? "" : handler();
    }

    /// <summary>A method that does nothing, so that asking whether it CAN be called is a
    /// question about the debugger and not about us. A func-eval is the only way to run code
    /// in a stopped process, and whether one is permitted at a given stop is not documented
    /// anywhere — it is discovered. Calling this answers it in isolation: no arguments, no
    /// string to allocate in the debuggee, no engine state touched. If <c>Ping</c> answers
    /// and <see cref="EvaluateGoal"/> does not, the fault is in the call; if neither
    /// answers, the stop grants no func-eval at all, and no amount of fixing the call will
    /// change that.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static int Ping() => NotifyCount;

    /// <summary>Set by the debug session. See <see cref="EvaluateGoal"/>.</summary>
    public static Func<int, string, string>? OnEvaluateGoal;

    /// <summary>ADR-035 — the Immediate window. Runs <paramref name="goalBase64"/> (a
    /// UTF-8 Prolog goal, base64-encoded) in a NEW activation over the live engine, with
    /// the variables of display frame <paramref name="frameIndex"/> substituted by their
    /// current values, and returns the result — base64-encoded UTF-8 again.
    ///
    /// <para>Base64 both ways because this crosses as a C# EXPRESSION: the debugger
    /// func-evals <c>ShumwayDebugHost.EvaluateGoal(3, "...")</c>, and a goal is full of
    /// quotes and backslashes that would otherwise need C#-literal escaping on the way in
    /// and un-escaping of the evaluator's rendered string on the way out. Base64 has
    /// neither problem.</para>
    ///
    /// <para>This is a FUNC-EVAL — the one mechanism that runs code in a stopped process —
    /// and it is user-initiated from a normal break state, which is the context where
    /// func-eval is supported (the stop path itself never evaluates anything; that is what
    /// the pinned channel is for).</para></summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static string EvaluateGoal(int frameIndex, string goalBase64)
    {
        Func<int, string, string>? handler = OnEvaluateGoal;
        if (handler is null)
            return Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes("no debug session is running"));
        try
        {
            string goal = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(goalBase64));
            return Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(handler(frameIndex, goal)));
        }
        catch (Exception ex)
        {
            // The evaluator itself must never throw across the func-eval boundary: the
            // debugger shows a raw exception where the user asked a question.
            return Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes("evaluation failed: " + ex.Message));
        }
    }

    /// <summary>Set by the debug session. See <see cref="SetNextStatement"/>.</summary>
    public static Func<int, int, string>? OnSetNextStatement;

    /// <summary>Set by the debug session. See <see cref="SetFrameVariable"/>.</summary>
    public static Func<int, string, string, string>? OnSetFrameVariable;

    /// <summary>ADR-035 D5+ — the Watch-window EDIT of a frame variable: DESTRUCTIVE
    /// (replaces an existing binding, trailed so backtracking restores it; the term
    /// <c>_</c> un-instantiates). Name and term are base64 UTF-8 both ways, same
    /// rationale as <see cref="EvaluateGoal"/>. A func-eval, user-initiated from a break
    /// state. The Immediate window deliberately does NOT route here — it keeps pure,
    /// non-destructive unification.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static string SetFrameVariable(int frameIndex, string nameBase64, string termBase64)
    {
        Func<int, string, string, string>? handler = OnSetFrameVariable;
        string answer;
        if (handler is null)
        {
            answer = "no debug session is running";
        }
        else
        {
            try
            {
                string name = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(nameBase64));
                string term = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(termBase64));
                answer = handler(frameIndex, name, term);
            }
            catch (Exception ex) { answer = "edit failed: " + ex.Message; }
        }
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(answer));
    }

    /// <summary>ADR-035 D5+ — Set Next Statement (Ctrl+Shift+F10): move the top frame's
    /// next-statement pointer to <paramref name="targetLine"/>. Forward skips; backward
    /// rewinds to the recorded mark (see DebugService.SetNextStatement). Returns "" on
    /// success or the refusal message, base64-encoded UTF-8 (same rationale as
    /// <see cref="EvaluateGoal"/>). A func-eval, user-initiated from a break state.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static string SetNextStatement(int frameIndex, int targetLine)
    {
        Func<int, int, string>? handler = OnSetNextStatement;
        string answer;
        if (handler is null)
        {
            answer = "no debug session is running";
        }
        else
        {
            try { answer = handler(frameIndex, targetLine); }
            catch (Exception ex) { answer = "set next statement failed: " + ex.Message; }
        }
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(answer));
    }

    /// <summary>The asynchronous break. The user hit Break All; the process stopped wherever
    /// it happened to be, which is at no port at all, so nothing has been reported and the
    /// channel still holds the last real stop — which would be a lie. This writes the truth:
    /// the stack as it stands right now. Returns the new sequence number, or 0 if no query is
    /// running.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static int CaptureNow()
    {
        Func<int>? handler = OnCaptureNow;
        return handler is null ? 0 : handler();
    }
}
