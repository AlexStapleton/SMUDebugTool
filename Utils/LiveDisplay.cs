using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZenStatesDebugTool
{
    public sealed class LiveGridRow
    {
        public int Key { get; }
        public object[] Cells { get; }
        public LiveGridRow(int key, params object[] cells) { Key = key; Cells = cells; }
    }
    // Stable row objects preserve selection/scroll and avoid binding/layout resets per frame.
    public sealed class LiveGrid
    {
        private readonly DataGridView grid;
        private int[] keys = new int[0];
        public LiveGrid(DataGridView grid) { this.grid = grid; }
        public int Update(IEnumerable<LiveGridRow> source)
        {
            var rows = source.ToArray();
            if (rows.Any(r => r.Cells.Length != grid.ColumnCount) || rows.Select(r => r.Key).Distinct().Count() != rows.Length) throw new ArgumentException("Unique keys and a complete cell set are required.");
            if (!keys.SequenceEqual(rows.Select(r => r.Key))) {
                int? selected = grid.CurrentRow?.Tag as int?;
                int first = grid.FirstDisplayedScrollingRowIndex;
                var sizing = grid.AutoSizeRowsMode; grid.SuspendLayout();
                try {
                    grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
                    grid.Rows.Clear(); keys = rows.Select(r => r.Key).ToArray();
                    var replacements = rows.Select(row => {
                        var item = (DataGridViewRow)grid.RowTemplate.Clone(); item.CreateCells(grid, row.Cells); item.Tag = row.Key; return item;
                    }).ToArray();
                    if (replacements.Length > 0) grid.Rows.AddRange(replacements);
                } finally { grid.AutoSizeRowsMode = sizing; grid.ResumeLayout(); }
                int selectedIndex = selected.HasValue ? Array.IndexOf(keys, selected.Value) : -1;
                if (selectedIndex >= 0) grid.CurrentCell = grid.Rows[selectedIndex].Cells[0];
                if (first >= 0 && rows.Length > 0) grid.FirstDisplayedScrollingRowIndex = Math.Min(first, rows.Length - 1);
                return rows.Length * grid.ColumnCount;
            }
            int changed = 0;
            for (int i = 0; i < rows.Length; i++)
                for (int cell = 0; cell < rows[i].Cells.Length; cell++)
                    if (!Equals(grid.Rows[i].Cells[cell].Value, rows[i].Cells[cell])) { grid.Rows[i].Cells[cell].Value = rows[i].Cells[cell]; changed++; }
            return changed;
        }
    }
    // Display hint only. Authoritative recovery/ownership checks stay in the action path.
    public sealed class BackgroundStatusProbe : IDisposable
    {
        private readonly object gate = new object();
        private readonly Func<bool> read;
        private readonly Func<long> timestamp;
        private readonly long interval;
        private Task pending;
        private long last;
        private bool attempted;
        private int value, disposed;
        public bool Value => Volatile.Read(ref value) != 0;
        public BackgroundStatusProbe(Func<bool> read, bool initial, int intervalMs = 1000, Func<long> timestamp = null)
        { this.read = read; value = initial ? 1 : 0; interval = (long)(intervalMs * (double)Stopwatch.Frequency / 1000); this.timestamp = timestamp ?? Stopwatch.GetTimestamp; }
        public Task Refresh()
        {
            lock (gate) {
                if (disposed != 0) return Task.CompletedTask;
                if (pending != null && !pending.IsCompleted) return pending;
                long now = timestamp();
                if (attempted && now - last < interval) return Task.CompletedTask;
                attempted = true; last = now;
                return pending = Task.Run(() => {
                    bool next;
                    try { next = read(); } catch { next = true; }
                    lock (gate) if (disposed == 0) Volatile.Write(ref value, next ? 1 : 0);
                });
            }
        }
        public void Invalidate() { lock (gate) attempted = false; }
        public void Dispose() { lock (gate) disposed = 1; }
    }
}
