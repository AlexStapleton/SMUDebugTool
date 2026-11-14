using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ZenStatesDebugTool.Profiles;

namespace ZenStatesDebugTool
{
    public sealed class TuningEditControls : IDisposable
    {
        private sealed class Binding
        {
            public TuningKind Kind; public int Index; public NumericUpDown[] Controls;
            public bool[] Enabled; public Color[] Colors;
            public TuningRequest Current => new TuningRequest { Kind = Kind, Index = Index, Value = Controls[0].Value,
                Medium = Controls.Length == 3 ? (int)Controls[1].Value : 0, High = Controls.Length == 3 ? (int)Controls[2].Value : 0 };
            public bool Included => Controls.All(c => c.Enabled);
            public bool InRange(TuningRequest r) => r.Value >= Controls[0].Minimum && r.Value <= Controls[0].Maximum &&
                (Controls.Length != 3 || r.Medium >= Controls[1].Minimum && r.Medium <= Controls[1].Maximum && r.High >= Controls[2].Minimum && r.High <= Controls[2].Maximum);
            public void Set(TuningRequest r)
            {
                if (!InRange(r)) return;
                Controls[0].Value = r.Value;
                if (Controls.Length == 3) { Controls[1].Value = r.Medium; Controls[2].Value = r.High; }
            }
        }
        private readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>();
        private readonly ToolTip tips = new ToolTip { AutoPopDelay = 20000 };
        private bool changing;
        public TuningEditSession Session { get; } = new TuningEditSession();
        public event Action Changed;
        public void Register(TuningKind kind, int index, params NumericUpDown[] controls)
        {
            if (controls.Length != (kind == TuningKind.CurveShaper ? 3 : 1)) throw new ArgumentException("Unexpected editor field count.");
            var binding = new Binding { Kind = kind, Index = index, Controls = controls, Enabled = controls.Select(c => c.Enabled).ToArray(), Colors = controls.Select(c => c.BackColor).ToArray() };
            bindings.Add(TuningSnapshots.Key(binding.Current), binding);
            Session.SetBaseline(binding.Current, "Initial editor value; not a CPU read");
            foreach (var control in controls) { control.ValueChanged += OnChanged; control.EnabledChanged += OnChanged; }
            Refresh();
        }
        private void OnChanged(object sender, EventArgs e) { if (!changing) Refresh(); }
        public Profile Capture() => TuningSnapshots.Subset(new Profile(), bindings.Values.Where(b => b.Included).Select(b => b.Current));
        public int PendingCount => bindings.Values.Count(b => b.Included && Session.IsEdited(b.Current));
        public bool CanDiscard => bindings.Values.Any(b => Session.IsEdited(b.Current) || b.Controls.Where((c, i) => c.Enabled != b.Enabled[i]).Any());
        public void Refresh()
        {
            foreach (var b in bindings.Values)
            {
                var current = b.Current;
                bool edited = Session.IsEdited(current);
                string tip = (edited ? UiText.T("Edited locally") + Environment.NewLine : "") +
                    UiText.T("Baseline") + ": " + TuningEditSession.ValueText(Session.Baseline(current)) + " — " + UiText.T(Session.BaselineSource(current)) + Environment.NewLine +
                    UiText.T("Saved value") + ": " + Session.SavedText(current) + Environment.NewLine +
                    UiText.T("CPU reported") + ": " + Session.ReportedText(current);
                for (int i = 0; i < b.Controls.Length; i++) {
                    b.Controls[i].BackColor = edited ? Color.LightGoldenrodYellow : b.Colors[i];
                    b.Controls[i].AccessibleDescription = tip;
                    tips.SetToolTip(b.Controls[i], tip);
                }
            }
            Changed?.Invoke();
        }
        public void RememberSaved(Profile profile) { Session.RememberSaved(profile); Refresh(); }
        public void Observe(TuningRequest observation, string source, bool cpuReported = true)
        {
            if (!bindings.TryGetValue(TuningSnapshots.Key(observation), out var b)) return;
            bool edited = Session.IsEdited(b.Current);
            bool valid = b.InRange(observation);
            if (cpuReported) Session.Observe(observation, source, valid);
            else if (valid) Session.SetBaseline(observation, source);
            if (!edited && valid) {
                changing = true;
                try { b.Set(observation); } finally { changing = false; }
            }
            Refresh();
        }
        public void RecordResult(TuningResult result) { Session.RecordResult(result); Refresh(); }
        public void Discard()
        {
            changing = true;
            try {
                foreach (var b in bindings.Values) {
                    var baseline = Session.Baseline(b.Current);
                    if (baseline != null) b.Set(baseline);
                    for (int i = 0; i < b.Controls.Length; i++) b.Controls[i].Enabled = b.Enabled[i];
                }
            } finally { changing = false; }
            Refresh();
        }
        public void Dispose()
        {
            foreach (var b in bindings.Values) foreach (var c in b.Controls) { c.ValueChanged -= OnChanged; c.EnabledChanged -= OnChanged; }
            tips.Dispose();
        }
    }
}
