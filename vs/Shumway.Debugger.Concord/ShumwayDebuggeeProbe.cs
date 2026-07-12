// Shumway debugger - IDE-side debuggee probe (ADR-035, D0 spike leg 1).
//
// Runs ONCE per process, from inside the stack filter (the func-eval context
// PTVS proved viable): func-evals SpikeDebugHelper.Attach() to get the pinned
// channel address, func-evals the Notify metadata token, verifies
// ReadMemory/WriteMemory on the pinned buffer, and hands both values to the
// server component (DkmCustomMessage.SendLower) so it can arm the hidden
// notify breakpoint. Every step's outcome is recorded for display in the
// diagnostic stack frame — the spike's reporting channel.

using System;
using System.Globalization;
using System.Text;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.CallStack;
using Microsoft.VisualStudio.Debugger.Evaluation;

namespace Shumway.Debugger.Concord
{
    internal sealed class ShumwayProbeDataItem : DkmDataItem
    {
        public bool ProbeRan;
        public long ChannelAddress;
        public string ProbeReport = "probe: not-run";
    }

    internal static class ShumwayDebuggeeProbe
    {
        public static ShumwayProbeDataItem GetState(DkmProcess process)
        {
            ShumwayProbeDataItem? item = process.GetDataItem<ShumwayProbeDataItem>();
            if (item == null)
            {
                item = new ShumwayProbeDataItem();
                process.SetDataItem(DkmDataCreationDisposition.CreateNew, item);
            }
            return item;
        }

        /// <summary>Leg-1 probe; called from FilterNextFrame on the first
        /// interpreter frame. Never throws (a filter exception truncates the walk).</summary>
        public static void RunOnce(DkmStackContext stackContext, DkmStackWalkFrame frame)
        {
            ShumwayProbeDataItem state = GetState(frame.Process);
            if (state.ProbeRan)
                return;
            state.ProbeRan = true;

            var report = new StringBuilder("probe:");
            try
            {
                // 1) func-eval Attach() -> pinned channel address.
                string? addrText = FuncEval(stackContext, frame,
                    "SpikeDebuggee.SpikeDebugHelper.Attach()", out string? evalError);
                if (addrText == null)
                {
                    report.Append(" funceval=FAIL(").Append(evalError).Append(')');
                    return;
                }
                long addr = ParseLong(addrText);
                state.ChannelAddress = addr;
                report.Append(" funceval=OK addr=0x").Append(addr.ToString("X"));

                // 2) ReadMemory: magic check on the pinned buffer.
                byte[] magic = new byte[4];
                frame.Process.ReadMemory((ulong)addr, DkmReadMemoryFlags.None, magic);
                bool magicOk = BitConverter.ToUInt32(magic, 0) == Channel.Magic;
                report.Append(" magic=").Append(magicOk ? "OK" : "BAD");

                // 3) WriteMemory: command byte the debuggee echoes to +24.
                frame.Process.WriteMemory((ulong)(addr + Channel.OffCommand), new byte[] { 0xAB });
                report.Append(" write=OK");

                // 4) func-eval the Notify metadata token (no SRM dependency).
                string? tokenText = FuncEval(stackContext, frame,
                    "typeof(SpikeDebuggee.SpikeDebugHelper).GetMethod(\"Notify\").MetadataToken",
                    out evalError);
                if (tokenText == null)
                {
                    report.Append(" token=FAIL(").Append(evalError).Append(')');
                    return;
                }
                int token = (int)ParseLong(tokenText);
                report.Append(" token=0x").Append(token.ToString("X8"));

                // 5) Hand off to the server component to arm the notify breakpoint.
                DkmCustomMessage.Create(
                        frame.Process.Connection, frame.Process,
                        ShumwayGuids.MessageSource, ShumwayGuids.MsgArmNotifyBreakpoint,
                        addr, token)
                    .SendLower();
                report.Append(" msg=SENT");
            }
            catch (Exception ex)
            {
                report.Append(" EX=").Append(ex.GetType().Name).Append(':').Append(ex.Message);
            }
            finally
            {
                state.ProbeReport = report.ToString();
            }
        }

        /// <summary>Reads the channel back (echo, hit count, server status) for the
        /// second-break diagnostic frame.</summary>
        public static string ReadStatus(DkmProcess process)
        {
            ShumwayProbeDataItem state = GetState(process);
            if (state.ChannelAddress == 0)
                return "status: no-channel";
            try
            {
                long addr = state.ChannelAddress;
                byte[] buf = new byte[Channel.OffErrorText + Channel.MaxErrorText];
                process.ReadMemory((ulong)addr, DkmReadMemoryFlags.None, buf);

                byte echo = buf[Channel.OffEcho];
                int hits = BitConverter.ToInt32(buf, Channel.OffHits);
                byte serverStatus = buf[Channel.OffServerStatus];
                long ticks = BitConverter.ToInt64(buf, Channel.OffTicks);

                var sb = new StringBuilder("status:");
                sb.Append(" ticks=").Append(ticks);
                sb.Append(" echo=").Append(echo == 0xAB ? "OK" : ("0x" + echo.ToString("X2")));
                sb.Append(" hits=").Append(hits);
                sb.Append(" server=").Append(
                    serverStatus == Channel.StatusArmed ? "ARMED"
                    : serverStatus == 0 ? "SILENT"
                    : "ERR" + (serverStatus - Channel.StatusErrorBase).ToString());
                if (serverStatus >= Channel.StatusErrorBase)
                {
                    int errLen = Math.Min(BitConverter.ToInt32(buf, Channel.OffErrorLen), Channel.MaxErrorText);
                    if (errLen > 0)
                        sb.Append('(').Append(Encoding.UTF8.GetString(buf, Channel.OffErrorText, errLen)).Append(')');
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "status: EX=" + ex.Message;
            }
        }

        private static long ParseLong(string text)
        {
            // The C# EE renders integers in the context radix (10): plain decimal.
            return long.Parse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        /// <summary>Synchronous func-eval of a C# expression against a CLR frame.</summary>
        private static string? FuncEval(
            DkmStackContext stackContext, DkmStackWalkFrame frame,
            string expression, out string? error)
        {
            error = null;
            DkmLanguage language = DkmLanguage.Create(
                "C#", new DkmCompilerId(ShumwayGuids.MicrosoftVendor, ShumwayGuids.CSharpLanguage));
            DkmInspectionSession session = DkmInspectionSession.Create(frame.Process, null);
            try
            {
                DkmInspectionContext inspection = DkmInspectionContext.Create(
                    session, frame.RuntimeInstance, stackContext.Thread,
                    Timeout: 3000,
                    EvaluationFlags: DkmEvaluationFlags.None,
                    FuncEvalFlags: DkmFuncEvalFlags.None,
                    Radix: 10, Language: language, ReturnValue: null);

                using (DkmLanguageExpression expr = DkmLanguageExpression.Create(
                    language, DkmEvaluationFlags.None, expression, null))
                {
                    string? value = null;
                    string? failure = null;
                    DkmWorkList workList = DkmWorkList.Create(null);
                    inspection.EvaluateExpression(workList, expr, frame, result =>
                    {
                        try
                        {
                            if (result.ErrorCode == 0 && result.ResultObject is DkmSuccessEvaluationResult ok)
                                value = ok.Value;
                            else if (result.ResultObject is DkmFailedEvaluationResult bad)
                                failure = bad.ErrorMessage;
                            else
                                failure = "hr=0x" + result.ErrorCode.ToString("X8");
                            result.ResultObject?.Close();
                        }
                        catch (Exception cbEx)
                        {
                            failure = cbEx.Message;
                        }
                    });
                    workList.Execute();

                    if (value == null)
                        error = failure ?? "no-result";
                    return value;
                }
            }
            finally
            {
                session.Close();
            }
        }
    }
}
