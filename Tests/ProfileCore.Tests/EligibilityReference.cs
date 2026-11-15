// Frozen pre-optimization contract for differential safety tests (6ad5bbab).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ZenStatesDebugTool
{
    public sealed class ReferenceCoreEvidence
    {
        public int Slot { get; internal set; }
        public int Ccd { get; internal set; } = -1;
        public int? Co { get; internal set; }
        public int Samples { get; internal set; }
        public decimal? Vid { get; internal set; }
        public decimal? Mhz { get; internal set; }
        public decimal? PeerMedian { get; internal set; }
        public string Reason { get; internal set; }
        public bool Comparable { get; internal set; }
    }
    public sealed class ReferenceEligibility
    {
        public IReadOnlyList<ReferenceCoreEvidence> Cores { get; }
        public CoStepProposal Proposal { get; }
        public string StopReason { get; }
        private ReferenceEligibility(List<ReferenceCoreEvidence> cores, CoStepProposal proposal, string stop)
        { Cores = cores.AsReadOnly(); Proposal = proposal; StopReason = stop; }
        public static ReferenceEligibility Analyze(IEnumerable<CoreVidReading> readings, IReadOnlyDictionary<int, int> offsets, int bound, long now, ISet<int> responseBlocked = null)
        {
            if (bound < -999 || bound >= 0) throw new ArgumentOutOfRangeException(nameof(bound));
            var groups = readings.Where(r => now >= r.Timestamp && (now - r.Timestamp) / (double)Stopwatch.Frequency <= 5).GroupBy(r => r.Slot).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Timestamp).ToArray());
            var rows = new List<ReferenceCoreEvidence>(); bool incomplete = groups.Keys.Any(k => !offsets.ContainsKey(k));
            foreach (int slot in offsets.Keys.OrderBy(k => k)) {
                var row = new ReferenceCoreEvidence { Slot = slot, Co = offsets[slot] }; rows.Add(row);
                if (!groups.TryGetValue(slot, out var values) || values.Length == 0) { row.Reason = "Waiting for native samples"; incomplete = true; continue; }
                var last = values.Last(); row.Ccd = last.Ccd; row.Samples = values.Length;
                if ((now - last.Timestamp) / (double)Stopwatch.Frequency > .5 || last.Issue != null) { row.Reason = "Stale or unavailable native reading"; incomplete = true; continue; }
                if (values.Length < 20 || (last.Timestamp - values[0].Timestamp) / (double)Stopwatch.Frequency < 2) { row.Reason = "Collecting a comparable window"; continue; }
                if (values.Any(v => v.Issue != null || !v.VidVolts.HasValue || !v.ReportedMhz.HasValue || v.Ccd != last.Ccd)) { row.Reason = "Discarding transition / invalid samples"; continue; }
                if (values.Any(v => !v.Busy)) { row.Reason = "Insufficient sustained activity"; continue; }
                if (row.Co < -999 || row.Co > 999) { row.Reason = "CO readback outside supported range"; incomplete = true; continue; }
                row.Mhz = values.Average(v => v.ReportedMhz.Value); row.Vid = values.Average(v => v.VidVolts.Value);
                if (values.Max(v => v.ReportedMhz.Value) - values.Min(v => v.ReportedMhz.Value) > row.Mhz * .03m) { row.Reason = "Reported clock is changing"; continue; }
                row.Comparable = true;
            }
            foreach (var row in rows.Where(r => r.Comparable)) {
                var peers = rows.Where(p => p.Comparable && p.Slot != row.Slot && p.Ccd == row.Ccd && Math.Abs(p.Mhz.Value - row.Mhz.Value) <= row.Mhz * .03m).Select(p => p.Vid.Value).OrderBy(v => v).ToArray();
                if (peers.Length < 2) { row.Reason = "Need two comparable peers in this CCD"; continue; }
                row.PeerMedian = peers.Length % 2 == 0 ? (peers[peers.Length / 2 - 1] + peers[peers.Length / 2]) / 2 : peers[peers.Length / 2];
                row.Reason = responseBlocked?.Contains(row.Slot) == true ? "No reproducible response; review before further changes" : row.Co > 0 ? "Positive CO requires a new reference" : row.Co - 1 < bound ? "Negative bound reached" : row.Vid - row.PeerMedian < .0125m ? "Within measured VID tolerance" : "Eligible for one negative step";
            }
            var target = incomplete ? null : rows.Where(r => r.Reason == "Eligible for one negative step").OrderByDescending(r => r.Vid).ThenBy(r => r.Slot).FirstOrDefault();
            var proposal = target == null ? null : new CoStepProposal(target.Slot, target.Co.Value, "Higher VID request than comparable busy peers in CCD " + target.Ccd + "; one step, no stability verdict.");
            string stop = !incomplete && rows.Count > 0 && rows.All(r => r.Reason == "Within measured VID tolerance" || r.Reason == "Negative bound reached") ?
                (rows.All(r => r.Reason == "Within measured VID tolerance") ? "Within measured VID tolerance" : "Bounds / measured tolerance reached") : null;
            return new ReferenceEligibility(rows, proposal, stop);
        }
    }
}
