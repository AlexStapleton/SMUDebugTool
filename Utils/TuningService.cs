using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ZenStatesDebugTool.Profiles;

namespace ZenStatesDebugTool
{
    public interface ITuningTransport
    {
        bool IsSupported(TuningRequest request, out string reason);
        TuningReply Write(TuningRequest request);
        TuningReply Read(TuningRequest request);
    }
    public interface IProfilePreflight
    {
        string ValidateProfile(Profile profile);
    }

    // No UI, driver construction, retries, clamping, or cached readbacks in this layer.
    // Callers serialize the entire operation against other hardware access.
    public sealed class TuningService
    {
        private readonly ITuningTransport transport;
        public TuningService(ITuningTransport transport) { this.transport = transport ?? throw new ArgumentNullException(nameof(transport)); }
        public static string ValidateProfileInput(Profile profile)
            => profile != null && (profile.SchemaVersion < 1 || profile.SchemaVersion > 2) ? "Unsupported profile schema version." :
                new TuningService(new ValidationTransport()).Apply(profile).Rows.FirstOrDefault(r => r.Outcome == TuningOutcome.Invalid)?.Reason;
        private sealed class ValidationTransport : ITuningTransport
        {
            public bool IsSupported(TuningRequest request, out string reason) { reason = "Validation only"; return false; }
            public TuningReply Write(TuningRequest request) => throw new InvalidOperationException("Validation cannot write.");
            public TuningReply Read(TuningRequest request) => throw new InvalidOperationException("Validation cannot read.");
        }

        public TuningResult Apply(Profile profile, CancellationToken cancellation = default(CancellationToken))
        {
            if (profile == null)
            {
                var invalid = new TuningResult();
                invalid.Rows.Add(new TuningRow { Request = new TuningRequest(), Outcome = TuningOutcome.Invalid, Reason = "Profile is null; no commands sent." });
                return invalid;
            }
            string preflightError = (transport as IProfilePreflight)?.ValidateProfile(profile);
            if (preflightError != null)
            {
                var invalid = new TuningResult { Label = profile.Name ?? "Profile" };
                invalid.Rows.Add(new TuningRow { Request = new TuningRequest(), Outcome = TuningOutcome.Invalid, Reason = preflightError + "; no commands sent." });
                return invalid;
            }
            var requests = RequestsFromProfile(profile);
            var result = Apply(requests, cancellation);
            result.Label = string.IsNullOrEmpty(profile.Name) ? "Current settings" : "Profile '" + profile.Name + "'";
            return result;
        }

        public static List<TuningRequest> RequestsFromProfile(Profile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var requests = new List<TuningRequest>();
            if (profile.CoMargins != null)
                requests.AddRange(profile.CoMargins.OrderBy(x => x.Key).Select(x => new TuningRequest { Kind = TuningKind.CurveOptimizer, Index = x.Key, Value = x.Value }));
            if (profile.Fmax.HasValue) requests.Add(new TuningRequest { Kind = TuningKind.Fmax, Value = profile.Fmax.Value });
            if (profile.CurveShaperTiers != null)
                for (int i = 0; i < profile.CurveShaperTiers.Count; i++)
                {
                    var tier = profile.CurveShaperTiers[i];
                    if (tier != null) requests.Add(new TuningRequest { Kind = TuningKind.CurveShaper, Index = i, Value = tier.Low, Medium = tier.Medium, High = tier.High });
                }
            AddLimit(requests, TuningKind.Ppt, profile.PptWatts);
            AddLimit(requests, TuningKind.Tdc, profile.TdcAmps);
            AddLimit(requests, TuningKind.Edc, profile.EdcAmps);
            AddLimit(requests, TuningKind.Scalar, profile.PboScalar);
            return requests;
        }

        private static void AddLimit(List<TuningRequest> requests, TuningKind kind, int? value)
        {
            if (value.HasValue) requests.Add(new TuningRequest { Kind = kind, Value = value.Value });
        }

        public TuningResult Apply(IEnumerable<TuningRequest> requests, CancellationToken cancellation = default(CancellationToken))
        {
            var result = new TuningResult();
            // Copy requests: callers may edit their model while the worker runs.
            foreach (var r in requests)
                result.Rows.Add(new TuningRow { Request = new TuningRequest { Kind = r.Kind, Index = r.Index, Value = r.Value, Medium = r.Medium, High = r.High } });
            foreach (var row in result.Rows)
            {
                string error = Validate(row.Request);
                if (error != null) { row.Outcome = TuningOutcome.Invalid; row.Reason = error; }
            }
            if (result.HasErrors && result.Rows.Any(r => r.Outcome == TuningOutcome.Invalid))
            {
                foreach (var row in result.Rows.Where(r => r.Outcome == TuningOutcome.NotAttempted)) row.Reason = "Batch validation failed; no commands sent.";
                return result;
            }
            foreach (var row in result.Rows)
            {
                if (cancellation.IsCancellationRequested) { row.Reason = "Cancelled before command; remaining settings not attempted."; break; }
                try
                {
                    if (row.Request.Kind >= TuningKind.Ppt && row.Request.Value == 0)
                    { row.Outcome = TuningOutcome.Skipped; row.Reason = "Zero means leave unchanged."; continue; }
                    if (!transport.IsSupported(row.Request, out string reason))
                    { row.Outcome = TuningOutcome.Skipped; row.Reason = reason; continue; }
                    row.TimestampUtc = DateTime.UtcNow;
                    row.Write = transport.Write(row.Request);
                    if (row.Write?.Status != 1)
                    {
                        row.Outcome = TuningOutcome.Rejected;
                        row.Reason = "Write rejected: " + TuningResultFormatter.StatusText(row.Write?.Status) + "; requested value retained.";
                        if (row.Write?.Status == null || IsFatal(row.Write.Status)) break;
                        continue;
                    }
                    row.Outcome = TuningOutcome.AcceptedUnverified;
                    row.Reason = "Accepted; no supported direct readback.";
                    if (row.Request.Kind != TuningKind.CurveOptimizer && row.Request.Kind != TuningKind.Fmax && row.Request.Kind != TuningKind.CurveShaper) continue;
                    // Cancellation never hides an already accepted write.
                    if (cancellation.IsCancellationRequested) { row.Reason = "Accepted; cancelled before readback."; break; }
                    try { row.Read = transport.Read(row.Request); }
                    catch (Exception ex) { row.Reason = "Accepted; readback failed: " + ex.Message; break; }
                    if (row.Read != null && (row.Read.Status != 1 || row.Request.Kind == TuningKind.Fmax && row.Read.Value == 0))
                        { row.Read.Value = null; row.Read.Medium = row.Read.High = null; }
                    bool completeBands = row.Request.Kind != TuningKind.CurveShaper || row.Read?.Medium.HasValue == true && row.Read.High.HasValue;
                    if (row.Read?.Status == 1 && row.Read.Direct && row.Read.ReadIssue == null && row.Read.Value.HasValue && completeBands && !(row.Request.Kind == TuningKind.Fmax && row.Read.Value == 0))
                    {
                        bool matches = row.Read.Value == row.Request.Value && (row.Request.Kind != TuningKind.CurveShaper || row.Read.Medium == row.Request.Medium && row.Read.High == row.Request.High);
                        row.Outcome = matches ? TuningOutcome.Verified : TuningOutcome.Mismatch;
                        row.Reason = row.Outcome == TuningOutcome.Verified ? "Fresh direct readback matched." : "Fresh direct readback differs; requested value retained.";
                    }
                    else if (row.Read?.ReadIssue != null) row.Reason = "Write accepted; " + row.Read.ReadIssue;
                    else if (row.Read?.Status != null) row.Reason = row.Read.Status != 1 ? "Write accepted; readback failed: " + TuningResultFormatter.StatusText(row.Read.Status) + "." : "Write accepted; complete direct readback unavailable.";
                    if (row.Read != null && !row.Read.Status.HasValue || IsFatal(row.Read?.Status)) break;
                }
                catch (Exception ex) { row.Outcome = TuningOutcome.Error; row.Reason = ex.Message; break; }
            }
            return result;
        }

        public static bool IsFatal(uint? status) => status >= 0x30 && status <= 0x33 || status == 0xFC || status == 0xFF;
        private static string Validate(TuningRequest r)
        {
            if (r.Value != decimal.Truncate(r.Value)) return "Whole numbers required; no commands sent.";
            switch (r.Kind)
            {
                case TuningKind.CurveOptimizer:
                    if (r.Index < 0 || r.Index >= 128 || r.Value < -999 || r.Value > 999) return "CO core/value outside the bundled mask or supported input range.";
                    break;
                case TuningKind.Fmax: if (r.Value < 0 || r.Value > 9000) return "FMax must be 0–9000 MHz."; break;
                case TuningKind.CurveShaper:
                    if (r.Index < 0 || r.Index >= 5 || r.Value < -50 || r.Value > 30 || r.Medium < -50 || r.Medium > 30 || r.High < -50 || r.High > 30) return "Curve Shaper tier/margin outside supported input range.";
                    break;
                case TuningKind.Ppt:
                case TuningKind.Tdc:
                case TuningKind.Edc:
                    if (r.Value < 0 || r.Value > 1000) return "PBO limit must be 0–1000."; break;
                case TuningKind.Scalar: if (r.Value < 0 || r.Value > 10) return "Scalar must be 0–10."; break;
                default: return "Unknown setting.";
            }
            return null;
        }
    }
}
