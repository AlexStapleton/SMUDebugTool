using System;
using System.Collections.Generic;
using System.Linq;
using ZenStatesDebugTool.Profiles;

namespace ZenStatesDebugTool
{
    public static class TuningSnapshots
    {
        public static string Key(TuningRequest r) => r.Kind + ":" +
            (r.Kind == TuningKind.CurveOptimizer || r.Kind == TuningKind.CurveShaper ? r.Index : 0);
        public static TuningRequest Request(TuningRequest r) => r == null ? null : new TuningRequest {
            Kind = r.Kind, Index = r.Index, Value = r.Value, Medium = r.Medium, High = r.High };
        public static bool Same(TuningRequest a, TuningRequest b) => a != null && b != null && Key(a) == Key(b) &&
            a.Value == b.Value && (a.Kind != TuningKind.CurveShaper || a.Medium == b.Medium && a.High == b.High);
        public static Profile Profile(Profile p) => new Profile {
            Name = p.Name, SchemaVersion = p.SchemaVersion, TopologySignature = p.TopologySignature,
            CoreBindings = p.CoreBindings == null ? null : new Dictionary<int, string>(p.CoreBindings),
            CoMargins = p.CoMargins == null ? null : new Dictionary<int, int>(p.CoMargins),
            CurveShaperTiers = p.CurveShaperTiers?.Select(t => t == null ? null : new CurveShaperTier { Low = t.Low, Medium = t.Medium, High = t.High }).ToList(),
            Fmax = p.Fmax, PptWatts = p.PptWatts, TdcAmps = p.TdcAmps, EdcAmps = p.EdcAmps, PboScalar = p.PboScalar
        };
        public static Profile Subset(Profile template, IEnumerable<TuningRequest> requests)
        {
            var p = new Profile { Name = template.Name, SchemaVersion = template.SchemaVersion, TopologySignature = template.TopologySignature };
            foreach (var r in requests)
                switch (r.Kind) {
                    case TuningKind.CurveOptimizer: p.CoMargins[r.Index] = (int)r.Value; break;
                    case TuningKind.Fmax: p.Fmax = r.Value; break;
                    case TuningKind.CurveShaper:
                        if (r.Index < 0 || r.Index >= 5) throw new ArgumentOutOfRangeException(nameof(requests));
                        if (p.CurveShaperTiers == null) p.CurveShaperTiers = Enumerable.Repeat<CurveShaperTier>(null, 5).ToList();
                        p.CurveShaperTiers[r.Index] = new CurveShaperTier { Low = (int)r.Value, Medium = r.Medium, High = r.High }; break;
                    case TuningKind.Ppt: p.PptWatts = (int)r.Value; break;
                    case TuningKind.Tdc: p.TdcAmps = (int)r.Value; break;
                    case TuningKind.Edc: p.EdcAmps = (int)r.Value; break;
                    case TuningKind.Scalar: p.PboScalar = (int)r.Value; break;
                    default: throw new ArgumentOutOfRangeException(nameof(requests));
                }
            p.CoreBindings = template.CoreBindings?.Where(kv => p.CoMargins.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            return p;
        }
        private static TuningReply Reply(TuningReply r) => r == null ? null : new TuningReply {
            Status = r.Status, Value = r.Value, Medium = r.Medium, High = r.High, RawArguments = r.RawArguments == null ? null : (uint[])r.RawArguments.Clone(),
            ReadIssue = r.ReadIssue, Direct = r.Direct, Route = r.Route, Command = r.Command,
            TargetMask = r.TargetMask, TargetIdentity = r.TargetIdentity };
        public static TuningResult Result(TuningResult source)
        {
            var result = new TuningResult { Label = source.Label, StartedUtc = source.StartedUtc };
            result.Rows.AddRange(source.Rows.Select(r => new TuningRow { Request = Request(r.Request), Outcome = r.Outcome,
                Write = Reply(r.Write), Read = Reply(r.Read), TimestampUtc = r.TimestampUtc, Reason = r.Reason }));
            return result;
        }
    }

    // UI-thread-owned state. It never reads hardware or writes a profile file.
    public sealed class TuningEditSession
    {
        private readonly Dictionary<string, TuningRequest> baseline = new Dictionary<string, TuningRequest>();
        private readonly Dictionary<string, string> sources = new Dictionary<string, string>();
        private readonly Dictionary<string, TuningRequest> saved = new Dictionary<string, TuningRequest>();
        private readonly Dictionary<string, Observation> reported = new Dictionary<string, Observation>();
        private sealed class Observation { public TuningRequest Request; public string Source; public DateTime TimeUtc; }
        public string SavedProfileName { get; private set; }
        public void SetBaseline(TuningRequest request, string source)
        {
            string key = TuningSnapshots.Key(request);
            baseline[key] = TuningSnapshots.Request(request); sources[key] = source;
        }
        public TuningRequest Baseline(TuningRequest request) => baseline.TryGetValue(TuningSnapshots.Key(request), out var value) ? TuningSnapshots.Request(value) : null;
        public bool IsEdited(TuningRequest request) => !TuningSnapshots.Same(request, Baseline(request));
        public string BaselineSource(TuningRequest request) => sources.TryGetValue(TuningSnapshots.Key(request), out string source) ? source : "Unknown";
        public void RememberSaved(Profile profile)
        {
            SavedProfileName = profile.Name;
            saved.Clear();
            foreach (var r in TuningService.RequestsFromProfile(profile)) saved[TuningSnapshots.Key(r)] = TuningSnapshots.Request(r);
        }
        public static string ValueText(TuningRequest request) => request == null ? UiText.T("Unavailable") : request.Requested + " " + request.Unit;
        public string SavedText(TuningRequest request) => saved.TryGetValue(TuningSnapshots.Key(request), out var value)
            ? ValueText(value) + (string.IsNullOrEmpty(SavedProfileName) ? "" : " (" + SavedProfileName + ")") : UiText.T("Unavailable");
        public string ReportedText(TuningRequest request)
        {
            if (!reported.TryGetValue(TuningSnapshots.Key(request), out var value)) return UiText.T("Unavailable");
            return ValueText(value.Request) + " — " + UiText.T(value.Source) + " (" + value.TimeUtc.ToLocalTime().ToString("HH:mm:ss") + ")";
        }
        public void Observe(TuningRequest request, string source, bool updateBaseline)
        {
            reported[TuningSnapshots.Key(request)] = new Observation { Request = TuningSnapshots.Request(request), Source = source, TimeUtc = DateTime.UtcNow };
            if (updateBaseline) SetBaseline(request, source);
        }
        public void RecordResult(TuningResult result)
        {
            foreach (var row in result.Rows)
            {
                if (row.Read?.Status == 1 && row.Read.Direct && row.Read.ReadIssue == null && row.Read.Value.HasValue &&
                    (row.Request.Kind != TuningKind.CurveShaper || row.Read.Medium.HasValue && row.Read.High.HasValue))
                {
                    var observed = TuningSnapshots.Request(row.Request); observed.Value = row.Read.Value.Value;
                    if (row.Request.Kind == TuningKind.CurveShaper) { observed.Medium = row.Read.Medium.Value; observed.High = row.Read.High.Value; }
                    Observe(observed, "CPU readback", row.Outcome == TuningOutcome.Mismatch);
                }
                if (row.IsAccepted && (row.Outcome == TuningOutcome.Verified || row.Outcome == TuningOutcome.AcceptedUnverified))
                    SetBaseline(row.Request, row.Outcome == TuningOutcome.Verified ? "CPU readback" : "Last sent; unverified");
            }
        }
        public Profile Changes(Profile current) => TuningSnapshots.Subset(current, TuningService.RequestsFromProfile(current).Where(IsEdited));
    }

    public sealed class TuningHistoryEntry
    {
        private readonly TuningResult snapshot;
        public TuningHistoryEntry(TuningResult result) { snapshot = TuningSnapshots.Result(result); }
        public TuningResult Result => TuningSnapshots.Result(snapshot);
        public override string ToString() => snapshot.StartedUtc.ToLocalTime().ToString("HH:mm:ss") + " — " + snapshot.Label + " — " + TuningResultFormatter.FriendlySummary(snapshot);
    }
    public sealed class TuningActionHistory
    {
        private readonly List<TuningHistoryEntry> entries = new List<TuningHistoryEntry>();
        public IReadOnlyList<TuningHistoryEntry> Entries => entries.AsReadOnly();
        public void Add(TuningResult result)
        {
            entries.Insert(0, new TuningHistoryEntry(result));
            if (entries.Count > 20) entries.RemoveAt(20);
        }
    }
}
