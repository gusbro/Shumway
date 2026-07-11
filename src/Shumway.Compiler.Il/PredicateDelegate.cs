using Shumway.Core;

namespace Shumway.Compiler.Il;

/// <summary>
/// Signature shared by every Tier-1 IL-compiled predicate. The compiled
/// method takes the engine and a <c>clauseCursor</c> indicating where to
/// resume:
///
/// <list type="bullet">
/// <item><c>clauseCursor == 0</c> — fresh entry from a <c>call</c> /
///   <c>execute</c> opcode (or directly from the embedding API). The
///   IL runs the first clause and, if there are more clauses, pushes an
///   IL choice point with cursor <c>1</c> before doing so.</item>
/// <item><c>clauseCursor &gt; 0</c> — re-entry after a backtrack. The
///   engine has already popped the CP and restored heap / trail / register
///   state; the IL switches on the cursor to find the right clause body
///   and either pushes a new CP (for the next clause) or runs the last
///   clause without one (the analogue of <c>trust_me</c>).</item>
/// </list>
///
/// <para>On success the IL returns <c>true</c>; the engine resumes at
/// <c>CP</c> (the caller's continuation). On failure it returns
/// <c>false</c>; the engine backtracks via <c>TryBacktrack</c> which may
/// pop the IL's own CP and re-enter the IL with the next cursor — that's
/// how external backtracking through alternative clauses works.</para>
/// </summary>
public delegate bool PredicateDelegate(Activation engine, int clauseCursor);
