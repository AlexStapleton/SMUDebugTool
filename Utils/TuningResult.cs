using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ZenStatesDebugTool
{
    public enum TuningKind { CurveOptimizer, Fmax, CurveShaper, Ppt, Tdc, Edc, Scalar }
    public enum TuningOutcome { Verified, AcceptedUnverified, Mismatch, Rejected, Skipped, Invalid, NotAttempted, Error }

    public sealed class TuningRequest
    {
        public TuningKind Kind { get; set; }
        public int Index { get; set; }
        public decimal Value { get; set; }
        public int Medium { get; set; }
        public int High { get; set; }
        public string Setting => Kind + (Kind == TuningKind.CurveOptimizer ? " core " + Index : Kind == TuningKind.CurveShaper ? " tier " + Index : "");
        public string Requested => Kind == TuningKind.CurveShaper ? $"{Value}/{Medium}/{High} low/medium/high" : Value.ToString(CultureInfo.InvariantCulture);
        public string Unit => Kind == TuningKind.Fmax ? "MHz" : Kind == TuningKind.Ppt ? "W" : Kind == TuningKind.Tdc || Kind == TuningKind.Edc ? "A" : Kind == TuningKind.Scalar ? "x" : "steps";
    }

    public sealed class TuningReply
    {
        public uint? Status { get; set; }
        public decimal? Value { get; set; }
        public string Route { get; set; }
        public uint? Command { get; set; }
        public uint? TargetMask { get; set; }
        public string TargetIdentity { get; set; }
        public bool Direct { get; set; }
        public string Source => !Value.HasValue ? "unavailable" : Direct ? "direct" : "derived";
    }

    public sealed class TuningRow
    {
        public TuningRequest Request { get; set; }
        public TuningOutcome Outcome { get; set; } = TuningOutcome.NotAttempted;
        public TuningReply Write { get; set; }
        public TuningReply Read { get; set; }
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public string Reason { get; set; } = "Not attempted.";
        public bool IsAccepted => Write?.Status == 1;
        public bool IsError => Outcome == TuningOutcome.Mismatch || Outcome == TuningOutcome.Rejected || Outcome == TuningOutcome.Invalid || Outcome == TuningOutcome.NotAttempted || Outcome == TuningOutcome.Error;
    }

    public sealed class TuningResult
    {
        public string Label { get; set; } = "Current settings";
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public string ApplicationVersion => typeof(TuningResult).Assembly.GetName().Version.ToString();
        public List<TuningRow> Rows { get; } = new List<TuningRow>();
        public bool Complete => Rows.All(r => r.Outcome != TuningOutcome.NotAttempted);
        public bool HasErrors => Rows.Any(r => r.IsError);
        public bool Success => Complete && !HasErrors;
        public int ExitCode => Success ? 0 : 1;
        public string Summary
        {
            get
            {
                if (Rows.All(r => r.Outcome == TuningOutcome.Skipped)) return "No applicable settings.";
                return string.Join(", ", Enum.GetValues(typeof(TuningOutcome)).Cast<TuningOutcome>()
                    .Select(o => new { Outcome = o, Count = Rows.Count(r => r.Outcome == o) })
                    .Where(x => x.Count > 0).Select(x => $"{x.Count} {OutcomeLabel(x.Outcome)}")) + (Complete ? "." : "; incomplete.");
            }
        }
        public static string OutcomeLabel(TuningOutcome outcome) => outcome == TuningOutcome.AcceptedUnverified ? "accepted (unverified)" : outcome == TuningOutcome.NotAttempted ? "not attempted" : outcome.ToString().ToLowerInvariant();
    }

    public static class TuningResultFormatter
    {
        public static string SettingName(TuningRequest request)
        {
            switch (request.Kind)
            {
                case TuningKind.CurveOptimizer: return UiText.T("Curve Optimizer") + " / " + UiText.T("Core " + request.Index);
                case TuningKind.CurveShaper: return UiText.T("Curve Shaper") + " / " + UiText.T("Tier") + " " + request.Index;
                case TuningKind.Ppt: return "PPT";
                case TuningKind.Tdc: return "TDC";
                case TuningKind.Edc: return "EDC";
                case TuningKind.Scalar: return UiText.T("PBO Scalar");
                default: return "FMax";
            }
        }
        public static string FriendlyOutcome(TuningOutcome outcome)
        {
            switch (outcome)
            {
                case TuningOutcome.Verified: return UiText.T("Confirmed");
                case TuningOutcome.AcceptedUnverified: return UiText.T("Sent; readback unavailable");
                case TuningOutcome.Mismatch: return UiText.T("Readback differs");
                case TuningOutcome.Skipped: return UiText.T("Not applied");
                case TuningOutcome.Rejected: return UiText.T("Rejected");
                case TuningOutcome.Invalid: return UiText.T("Invalid value or target");
                case TuningOutcome.NotAttempted: return UiText.T("Not attempted");
                default: return UiText.T("Failed");
            }
        }
        public static string FriendlySummary(TuningResult result)
        {
            if (result.Rows.Count == 0) return UiText.T("No settings requested.");
            if (result.Rows.Count == 1)
            {
                var row = result.Rows[0];
                return $"{SettingName(row.Request)} {row.Request.Requested} {row.Request.Unit} — {FriendlyOutcome(row.Outcome)}";
            }
            return string.Join("; ", result.Rows.GroupBy(r => r.Outcome).Select(g => g.Count() + " " +
                (g.Key == TuningOutcome.AcceptedUnverified ? UiText.T("sent without readback") :
                 g.Key == TuningOutcome.Verified ? UiText.T("confirmed") : FriendlyOutcome(g.Key))));
        }
        public sealed class DisplayRow
        {
            public string Setting { get; set; }
            public string Requested { get; set; }
            public string Result { get; set; }
            public string Observed { get; set; }
        }
        public static List<DisplayRow> DisplayRows(TuningResult result, bool groupCores)
        {
            var rows = new List<DisplayRow>();
            foreach (var group in result.Rows.GroupBy(r => new { r.Request.Kind, r.Outcome }))
            {
                var first = group.First();
                if (groupCores && group.Count() > 1 && !first.IsError &&
                    (first.Request.Kind == TuningKind.CurveOptimizer || first.Request.Kind == TuningKind.CurveShaper))
                    rows.Add(new DisplayRow {
                        Setting = UiText.T(first.Request.Kind == TuningKind.CurveOptimizer ? "Curve Optimizer" : "Curve Shaper"),
                        Requested = string.Format(UiText.T(first.Request.Kind == TuningKind.CurveOptimizer ? "{0} cores" : "{0} tiers"), group.Count()),
                        Result = FriendlyOutcome(first.Outcome)
                    });
                else foreach (var row in group)
                    rows.Add(new DisplayRow {
                        Setting = SettingName(row.Request), Requested = row.Request.Requested + " " + row.Request.Unit,
                        Result = FriendlyOutcome(row.Outcome),
                        Observed = row.Read?.Value?.ToString(CultureInfo.InvariantCulture) + (row.Read?.Value != null ? " " + row.Request.Unit : "")
                    });
            }
            return rows;
        }
        private static string Reply(TuningReply reply)
        {
            if (reply == null) return "unavailable";
            return $"{reply.Route ?? "CPU API"} cmd={Hex(reply.Command)} target={Hex(reply.TargetMask)} {reply.TargetIdentity} status={Hex(reply.Status)} ({StatusText(reply.Status)})";
        }
        private static string Hex(uint? value) => value.HasValue ? $"0x{value.Value:X2}" : "unavailable";
        public static string StatusText(uint? status)
        {
            switch (status)
            {
                case 1: return "OK";
                case 0x30: return "mutex timeout";
                case 0x31: return "mailbox ready timeout";
                case 0x32: return "message write timeout";
                case 0x33: return "PCI failure";
                case 0xFC: return "busy";
                case 0xFD: return "prerequisites not met";
                case 0xFE: return "unknown command";
                case 0xFF: return "failed";
                case null: return "unavailable";
                default: return "unrecognized status";
            }
        }
        public static string Row(TuningRow row) => $"{row.TimestampUtc:O} {row.Request.Setting}: requested {row.Request.Requested} {row.Request.Unit}; {TuningResult.OutcomeLabel(row.Outcome)}; observed {row.Read?.Value?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"} ({row.Read?.Source ?? "unavailable"}); write [{Reply(row.Write)}]; read [{Reply(row.Read)}]; {row.Reason}";
        public static string Report(TuningResult result) => $"{result.Label} — {result.StartedUtc:O} — v{result.ApplicationVersion}: {result.Summary}" + Environment.NewLine + string.Join(Environment.NewLine, result.Rows.Select(Row));
        // The startup reader consumes the last physical line; never embed newlines from errors/names.
        public static string LogLine(string name, TuningResult result) => ($"Profile '{name}' — {result.StartedUtc:O} — v{result.ApplicationVersion}: {result.Summary} Success={result.Success}; " + string.Join(" | ", result.Rows.Select(Row))).Replace('\r', ' ').Replace('\n', ' ');
    }
}
