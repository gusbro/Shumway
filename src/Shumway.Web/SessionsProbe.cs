using System.Runtime.InteropServices.JavaScript;
using System.Text;
using Shumway.WebHost;

namespace Shumway.Web;

/// <summary>#sessions: two sessions in this page's runtime, each with its own
/// engine, thread and wasm tier (ADR-068). What the desktop tests of the host
/// cannot show is here: two engines inside wasm at once, on two threads, over
/// the tables the process shares.</summary>
internal static partial class WebShumwayApp
{
    /// <summary>A session's page that keeps what it is sent.</summary>
    private sealed class RecordingPage : ISessionPage
    {
        private readonly StringBuilder _out = new();
        public int Asked;
        public string Out { get { lock (_out) return _out.ToString(); } }
        public void Write(string text) { lock (_out) _out.Append(text); }
        public void WriteError(string text) { lock (_out) _out.Append(text); }
        public void AskForInput() => Interlocked.Increment(ref Asked);
    }

    private static Task<QueryStep> Ask(HostSession session, string query)
        => session.OnEngine(() =>
        {
            try { session.StartQuery(query); }
            catch (Exception ex) { return new QueryStep(QueryStepKind.Error, session.Describe(ex)); }
            QueryStep step = session.Next(80);
            session.EndRun();
            return step;
        });

    private static Task<int> ConsultAndCompile(HostSession session, string source)
        => session.OnEngine(() =>
        {
            session.TopLevel!.Consult(source);
            return session.Engine.IlPromotion.Wasm is { CompileAllOnConsult: true } wasm
                ? wasm.CompileAllTick(session.Engine) : 0;
        });

    /// <summary>#sessionsmem: what a session costs (ADR-068, verification V2).
    /// Opens sessions one at a time, each holding what <paramref name="what"/>
    /// says, and reports after each what the managed heap grew by and how long
    /// it took. A session whose thread does not start ends the run: that is
    /// the page's limit, whatever the memory.</summary>
    [JSExport]
    internal static async Task<string> SessionsMemoryProbe(int count, string what)
    {
        // What each session holds: "thread" (its thread alone), "tier0" (an
        // engine with no wasm in it), "engine" (the engine as the page boots
        // it) or "clpfd" (that, with the library loaded and a query run).
        string level = string.IsNullOrEmpty(what) ? "clpfd" : what;
        void Say(string line)
        {
            if (_jsThread is { } js) js.Post(static l => WriteToPage((string)l!), line);
        }
        static double Mb(long bytes) => bytes / (1024.0 * 1024.0);
        static long Managed() => GC.GetTotalMemory(forceFullCollection: true);

        int wanted = Math.Max(1, count);
        var host = new SessionHost(_jsThread, maxSessions: wanted);
        var open = new List<HostSession>();
        long start = Managed(), previous = start;
        Say($"note before any session: managed heap {Mb(previous):F1} MB, "
            + $"committed {Mb(GC.GetGCMemoryInfo().TotalCommittedBytes):F1} MB\n");
        try
        {
            for (int i = 0; i < wanted; i++)
            {
                HostSession s = host.Create("m" + i, new RecordingPage(),
                    _ => BootEngine(new WasmTierSettings { Disabled = level == "tier0" }));
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                Task<bool> boot = s.OnEngine(() => { if (level != "thread") s.StartEngine(); return true; });
                if (await Task.WhenAny(boot, Task.Delay(60_000)).ConfigureAwait(false) != boot)
                {
                    Say($"FAIL session {i + 1}: its thread did not start within 60 s\n");
                    break;
                }
                await boot.ConfigureAwait(false);
                double bootMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                long booted = Managed();

                t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                QueryStep load = new(QueryStepKind.Last, "X = 4");
                if (level == "clpfd")
                {
                    // Two queries: the operators are the library's, and a query
                    // is read whole before it runs.
                    await Ask(s, "use_module(library(clpfd)).").ConfigureAwait(false);
                    load = await Ask(s, "X #> 3, X #< 6, label([X]).").ConfigureAwait(false);
                }
                else if (level != "thread")
                    load = await Ask(s, "X is 2 + 2.").ConfigureAwait(false);
                double loadMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                long loaded = Managed();
                open.Add(s);

                bool right = load.Text.Contains("X = 4");
                Say((right ? "ok   " : "FAIL ") + $"session {i + 1}: boot {bootMs:F0} ms, +{Mb(booted - previous):F1} MB; "
                    + $"{(level == "clpfd" ? "clpfd and a query" : "a query")} {loadMs:F0} ms, +{Mb(loaded - booted):F1} MB; "
                    + $"managed heap {Mb(loaded):F1} MB, committed {Mb(GC.GetGCMemoryInfo().TotalCommittedBytes):F1} MB"
                    + (right ? "" : $" [{load.Kind}: {load.Text.Replace('\n', ' ')}]") + "\n");
                previous = loaded;
            }
            // Every session answers again with all of them open.
            int answering = 0;
            foreach (HostSession s in open)
                if (level == "thread"
                    || (await Ask(s, level == "clpfd" ? "X #> 3, X #< 6, label([X])." : "X is 2 + 2.")
                        .ConfigureAwait(false)).Text.Contains("X = 4"))
                    answering++;
            Say((answering == open.Count ? "ok   " : "FAIL ")
                + $"{answering} of {open.Count} sessions answer with all of them open\n");
            // What a session left behind while it booted may still be held
            // from its thread's stack: this is the heap once each has run again.
            await Task.Delay(200).ConfigureAwait(false);
            long settled = Managed();
            Say($"note with every session run again: managed heap {Mb(settled):F1} MB, "
                + $"{Mb((settled - start) / Math.Max(1, open.Count)):F1} MB a session\n");
        }
        catch (Exception ex)
        {
            Say("FAIL the probe threw: " + ex + "\n");
        }
        // The sessions stay open: the page measures its memory after this.
        GC.KeepAlive(open);
        return $"--- sessions memory probe ({level}): done, {open.Count} of {wanted} sessions ---\n";
    }

    [JSExport]
    internal static async Task<string> SessionsProbe(int rounds)
    {
        var report = new StringBuilder();
        int failures = 0;
        // Each line goes to the page as it is known: a probe that stops
        // half way says where.
        void Say(string line)
        {
            if (_jsThread is { } js) js.Post(static l => WriteToPage((string)l!), line);
        }
        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failures++;
            var line = new StringBuilder(ok ? "ok   " : "FAIL ").Append(name);
            if (detail.Length > 0) line.Append(": ").Append(detail.Replace('\n', ' '));
            Say(line.Append('\n').ToString());
        }
        // Output crosses to the page on a later turn of its thread.
        async Task<bool> Shows(RecordingPage page, string text)
        {
            for (int i = 0; i < 200 && !page.Out.Contains(text); i++)
                await Task.Delay(25).ConfigureAwait(false);
            return page.Out.Contains(text);
        }

        var host = new SessionHost(_jsThread, maxSessions: 2);
        try
        {
            var pageA = new RecordingPage();
            var pageB = new RecordingPage();
            var tierA = new WasmTierSettings();
            var tierB = new WasmTierSettings();
            HostSession a = host.Create("a", pageA, _ => BootEngine(tierA));
            HostSession b = host.Create("b", pageB, _ => BootEngine(tierB));
            await Task.WhenAll(
                a.OnEngine(() => { a.StartEngine(); return true; }),
                b.OnEngine(() => { b.StartEngine(); return true; })).ConfigureAwait(false);

            int threadA = await a.OnEngine(() => Environment.CurrentManagedThreadId).ConfigureAwait(false);
            int threadB = await b.OnEngine(() => Environment.CurrentManagedThreadId).ConfigureAwait(false);
            int threadMain = await OnEngineThreadId().ConfigureAwait(false);
            Check("each session has a thread of its own",
                threadA != threadB && threadA != threadMain && threadB != threadMain,
                $"{threadA}, {threadB}; the page's own {threadMain}");

            // The same names, a different program in each.
            int[] compiled = await Task.WhenAll(
                ConsultAndCompile(a, TierProbeCorpus + "\nwho(a).\n"),
                ConsultAndCompile(b, TierProbeCorpus + "\nwho(b).\n")).ConfigureAwait(false);
            bool tier = Shumway.Core.RuntimeCaps.SupportsWasmCodegen;
            Check(tier ? "the tier compiled the program in both" : "no tier in this build: Tier-0 in both",
                !tier || (compiled[0] > 0 && compiled[1] > 0), $"{compiled[0]}, {compiled[1]} predicates");

            const string work = "loop(300000), tak(18, 12, 6, A), range(1, 30, L), nrev(L, [F|_]), who(W).";
            bool right = true;
            string last = "";
            for (int r = 0; r < Math.Max(1, rounds); r++)
            {
                QueryStep[] both = await Task.WhenAll(Ask(a, work), Ask(b, work)).ConfigureAwait(false);
                right &= both[0].Text.Contains("A = 7") && both[0].Text.Contains("F = 30")
                      && both[0].Text.Contains("W = a")
                      && both[1].Text.Contains("A = 7") && both[1].Text.Contains("F = 30")
                      && both[1].Text.Contains("W = b");
                last = both[0].Text + " | " + both[1].Text;
            }
            Check($"both answer right while running at once, {Math.Max(1, rounds)} rounds", right, last);

            // One session interns thousands of functors while the other is
            // inside wasm: the tables the modules read are replaced under it.
            var many = new StringBuilder();
            bool grewRight = true;
            for (int r = 0; r < 4; r++)
            {
                many.Clear();
                for (int i = 0; i < 3000; i++)
                    many.Append("grow_").Append(r).Append('_').Append(i).Append("(x, y, z).\n");
                string source = many.ToString();
                Task<QueryStep> running = Ask(a, "loop(2000000), tak(18, 12, 6, A).");
                Task<int> consulting = b.OnEngine(() => { b.TopLevel!.Consult(source); return 0; });
                await Task.WhenAll(running, consulting).ConfigureAwait(false);
                grewRight &= running.Result.Text.Contains("A = 7");
            }
            Check("a search inside wasm survives the other session growing the tables", grewRight);
            QueryStep afterGrowth = await Ask(b, "grow_3_2999(X, Y, Z), tak(18, 12, 6, A).").ConfigureAwait(false);
            Check("and the session that grew them is whole",
                afterGrowth.Text.Contains("A = 7") && afterGrowth.Text.Contains("X = x"), afterGrowth.Text);

            // Flags and operators.
            await Ask(a, "set_prolog_flag(double_quotes, atom), op(700, xfx, ===>).").ConfigureAwait(false);
            QueryStep flagA = await Ask(a, "X = \"ab\", atom(X), Y = (p ===> q).").ConfigureAwait(false);
            QueryStep flagB = await Ask(b, "X = \"ab\", atom(X).").ConfigureAwait(false);
            QueryStep opB = await Ask(b, "Y = (p ===> q).").ConfigureAwait(false);
            Check("flags and operators are each session's own",
                flagA.Kind is QueryStepKind.Solution or QueryStepKind.Last
                && flagB.Kind == QueryStepKind.Failed && opB.Kind == QueryStepKind.Error,
                $"{flagA.Kind}, {flagB.Kind}, {opB.Kind}");

            // Cancelling one leaves the other running: here in the default
            // form, and again below once the session compiles to the other.
            async Task CancelLeavesTheOther(string form)
            {
                string mark = "started_" + form.Replace(' ', '_');
                Task<QueryStep> endless = Ask(a, $"write({mark}), repeat, fail.");
                bool started = await Shows(pageA, mark).ConfigureAwait(false);
                QueryStep during = await Ask(b, "who(W).").ConfigureAwait(false);
                bool wasRunning = started && !endless.IsCompleted;
                a.Cancel();
                Task finished = await Task.WhenAny(endless, Task.Delay(30_000)).ConfigureAwait(false);
                if (finished != endless)
                {
                    Check($"cancelling one session leaves the other untouched ({form})", false,
                        $"the search did not stop within 30 s; started {started}");
                    throw new TimeoutException("a cancelled search did not stop; the probe ends here");
                }
                QueryStep stopped = await endless.ConfigureAwait(false);
                QueryStep after = await Ask(a, "who(W).").ConfigureAwait(false);
                Check($"cancelling one session leaves the other untouched ({form})",
                    wasRunning && during.Text.Contains("W = b") && stopped.Kind == QueryStepKind.Failed
                    && after.Text.Contains("W = a"),
                    $"running {wasRunning}, {stopped.Kind}, {after.Text}");
            }
            await CancelLeavesTheOther("partitions").ConfigureAwait(false);

            // jit_compile/1 is the session's.
            await Ask(a, "jit_compile(cps).").ConfigureAwait(false);
            Check("jit_compile(cps) in one session leaves the other's form alone",
                !tier || (tierA.Cps && !tierB.Cps && !Tier.Cps), $"{tierA.Cps}, {tierB.Cps}; the page's own {Tier.Cps}");
            QueryStep[] forms = await Task.WhenAll(Ask(a, work), Ask(b, work)).ConfigureAwait(false);
            Check("and both still answer right",
                forms[0].Text.Contains("A = 7") && forms[0].Text.Contains("W = a")
                && forms[1].Text.Contains("A = 7") && forms[1].Text.Contains("W = b"),
                forms[0].Text + " | " + forms[1].Text);
            await Ask(b, "jit_compile(off).").ConfigureAwait(false);
            Check("jit_compile(off) in one session leaves the other's tier on",
                !tier || (tierB.Disabled && !tierA.Disabled && !Tier.Disabled));

            // Output and input.
            await Ask(a, "write(said_by_a), write(user_error, erred_by_a).").ConfigureAwait(false);
            await Ask(b, "write(said_by_b).").ConfigureAwait(false);
            bool shown = await Shows(pageA, "said_by_a").ConfigureAwait(false)
                      && await Shows(pageA, "erred_by_a").ConfigureAwait(false)
                      && await Shows(pageB, "said_by_b").ConfigureAwait(false);
            Check("output and user_error reach the session's own page",
                shown && !pageB.Out.Contains("said_by_a") && !pageB.Out.Contains("erred_by_a")
                && !pageA.Out.Contains("said_by_b"));

            await CancelLeavesTheOther("continuation functions").ConfigureAwait(false);

            // The limit, and room after closing.
            string refusal = "";
            try { host.Create("c", new RecordingPage(), _ => BootEngine(new WasmTierSettings())); }
            catch (InvalidOperationException ex) { refusal = ex.Message; }
            Check("a third session is refused with a message", refusal.Contains("limit (2)"), refusal);
            await host.Close("a").ConfigureAwait(false);
            HostSession c = host.Create("c", new RecordingPage(), _ => BootEngine(new WasmTierSettings()));
            await c.OnEngine(() => { c.StartEngine(); return true; }).ConfigureAwait(false);
            QueryStep fresh = await Ask(c, "catch(who(_), error(existence_error(_, _), _), true).").ConfigureAwait(false);
            Check("closing one makes room, and the new session starts empty",
                fresh.Kind is QueryStepKind.Solution or QueryStepKind.Last, fresh.Kind.ToString());

            // The page's own session never noticed.
            QueryStep mine = await Ask(S, "catch(who(_), error(existence_error(_, _), _), true).").ConfigureAwait(false);
            Check("the page's own session saw none of it",
                mine.Kind is QueryStepKind.Solution or QueryStepKind.Last, mine.Kind.ToString());
        }
        catch (Exception ex)
        {
            failures++;
            Say("FAIL the probe threw: " + ex + "\n");
        }
        finally
        {
            // Bounded: a session whose search never stopped never closes.
            await Task.WhenAny(host.CloseAll(), Task.Delay(10_000)).ConfigureAwait(false);
        }
        report.Append("--- sessions probe: ")
              .Append(failures == 0 ? "passed" : failures + " FAILED").Append(" ---\n");
        return report.ToString();
    }
}
