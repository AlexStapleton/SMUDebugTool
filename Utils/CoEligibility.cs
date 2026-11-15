using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ZenStatesDebugTool
{
    public sealed class CoCoreEvidence
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
    public sealed class CoEligibility
    {
        public IReadOnlyList<CoCoreEvidence> Cores { get; }
        public CoStepProposal Proposal { get; }
        public string StopReason { get; }
        private CoEligibility(List<CoCoreEvidence> cores, CoStepProposal proposal, string stop)
        { Cores = cores.AsReadOnly(); Proposal = proposal; StopReason = stop; }
        private sealed class WindowStats
        {
            public int Count, Ccd;
            public long First = long.MaxValue;
            public CoreVidReading Last;
            public bool Invalid, MixedCcd, Inactive;
            public decimal VidSum, MhzSum, MinMhz = decimal.MaxValue, MaxMhz = decimal.MinValue;
            public void Add(CoreVidReading value)
            {
                if (Count == 0) Ccd = value.Ccd;
                Count++; First = Math.Min(First, value.Timestamp);
                if (Last == null || value.Timestamp >= Last.Timestamp) Last = value;
                MixedCcd |= value.Ccd != Ccd;
                Invalid |= value.Issue != null || !value.VidVolts.HasValue || !value.ReportedMhz.HasValue;
                Inactive |= !value.Busy;
                VidSum += value.VidVolts ?? 0;
                decimal mhz = value.ReportedMhz ?? 0; MhzSum += mhz; MinMhz = Math.Min(MinMhz, mhz); MaxMhz = Math.Max(MaxMhz, mhz);
            }
        }
        public static CoEligibility Analyze(IEnumerable<CoreVidReading> readings, IReadOnlyDictionary<int, int> offsets, int bound, long now, ISet<int> responseBlocked = null)
        {
            if (bound < -999 || bound >= 0) throw new ArgumentOutOfRangeException(nameof(bound));
            // Timestamp extrema and one pass preserve the contract for shuffled input;
            // sorting/copying every core's entire window is unnecessary.
            var groups = new Dictionary<int, WindowStats>();
            foreach (var reading in readings) {
                if (reading.Timestamp > now || (now - reading.Timestamp) / (double)Stopwatch.Frequency > 5) continue;
                if (!groups.TryGetValue(reading.Slot, out var stats)) groups.Add(reading.Slot, stats = new WindowStats());
                stats.Add(reading);
            }
            var rows = new List<CoCoreEvidence>(offsets.Count); bool incomplete = groups.Keys.Any(k => !offsets.ContainsKey(k));
            var comparable = new Dictionary<int, List<CoCoreEvidence>>();
            foreach (int slot in offsets.Keys.OrderBy(k => k)) {
                var row = new CoCoreEvidence { Slot = slot, Co = offsets[slot] }; rows.Add(row);
                if (!groups.TryGetValue(slot, out var values)) { row.Reason = "Waiting for native samples"; incomplete = true; continue; }
                var last = values.Last; row.Ccd = last.Ccd; row.Samples = values.Count;
                if ((now - last.Timestamp) / (double)Stopwatch.Frequency > .5 || last.Issue != null) { row.Reason = "Stale or unavailable native reading"; incomplete = true; continue; }
                if (values.Count < 20 || (last.Timestamp - values.First) / (double)Stopwatch.Frequency < 2) { row.Reason = "Collecting a comparable window"; continue; }
                if (values.Invalid || values.MixedCcd) { row.Reason = "Discarding transition / invalid samples"; continue; }
                if (values.Inactive) { row.Reason = "Insufficient sustained activity"; continue; }
                if (row.Co < -999 || row.Co > 999) { row.Reason = "CO readback outside supported range"; incomplete = true; continue; }
                row.Mhz = values.MhzSum / values.Count; row.Vid = values.VidSum / values.Count;
                if (values.MaxMhz - values.MinMhz > row.Mhz * .03m) { row.Reason = "Reported clock is changing"; continue; }
                row.Comparable = true;
                if (!comparable.TryGetValue(row.Ccd, out var ccd)) comparable.Add(row.Ccd, ccd = new List<CoCoreEvidence>());
                ccd.Add(row);
            }
            var peers = new List<decimal>();
            foreach (var row in rows.Where(r => r.Comparable)) {
                peers.Clear();
                foreach (var peer in comparable[row.Ccd]) if (peer.Slot != row.Slot && Math.Abs(peer.Mhz.Value - row.Mhz.Value) <= row.Mhz * .03m) peers.Add(peer.Vid.Value);
                if (peers.Count < 2) { row.Reason = "Need two comparable peers in this CCD"; continue; }
                peers.Sort();
                row.PeerMedian = peers.Count % 2 == 0 ? (peers[peers.Count / 2 - 1] + peers[peers.Count / 2]) / 2 : peers[peers.Count / 2];
                row.Reason = responseBlocked?.Contains(row.Slot) == true ? "No reproducible response; review before further changes" : row.Co > 0 ? "Positive CO requires a new reference" : row.Co - 1 < bound ? "Negative bound reached" : row.Vid - row.PeerMedian < .0125m ? "Within measured VID tolerance" : "Eligible for one negative step";
            }
            var target = incomplete ? null : rows.Where(r => r.Reason == "Eligible for one negative step").OrderByDescending(r => r.Vid).ThenBy(r => r.Slot).FirstOrDefault();
            var proposal = target == null ? null : new CoStepProposal(target.Slot, target.Co.Value, "Higher VID request than comparable busy peers in CCD " + target.Ccd + "; one step, no stability verdict.");
            string stop = !incomplete && rows.Count > 0 && rows.All(r => r.Reason == "Within measured VID tolerance" || r.Reason == "Negative bound reached") ?
                (rows.All(r => r.Reason == "Within measured VID tolerance") ? "Within measured VID tolerance" : "Bounds / measured tolerance reached") : null;
            return new CoEligibility(rows, proposal, stop);
        }
    }
}
