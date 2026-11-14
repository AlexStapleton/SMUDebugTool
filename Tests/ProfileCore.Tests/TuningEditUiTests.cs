using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;
using ZenStatesDebugTool;
using ZenStatesDebugTool.Profiles;

namespace ProfileCore.Tests
{
    public class TuningEditUiTests
    {
        private static async Task OnSta(Action action)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => {
                try { Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("en"); action(); done.SetResult(true); }
                catch (Exception ex) { done.SetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Assert.Same(done.Task, await Task.WhenAny(done.Task, Task.Delay(15000))); await done.Task;
        }
        [Fact]
        public Task RefreshPreservesPendingDraftAndDiscardIsALocalReset() => OnSta(() => {
            using (var form = new Form { Opacity = 0 })
            using (var control = new NumericUpDown { Minimum = -999, Maximum = 999 })
            using (var edits = new TuningEditControls()) {
                form.Controls.Add(control); form.Show();
                var originalColor = control.BackColor;
                edits.Register(TuningKind.CurveOptimizer, 0, control);
                edits.Observe(new TuningRequest { Kind = TuningKind.CurveOptimizer, Value = -20 }, "CPU readback");
                Assert.Equal(-20, control.Value); Assert.Equal(0, edits.PendingCount);
                control.Value = -25;
                Assert.Equal(1, edits.PendingCount); Assert.Equal(Color.LightGoldenrodYellow, control.BackColor);
                edits.RememberSaved(new Profile { CoMargins = { [0] = -30 } });
                Assert.Equal(1, edits.PendingCount);
                edits.Observe(new TuningRequest { Kind = TuningKind.CurveOptimizer, Value = -22 }, "CPU readback");
                Assert.Equal(-25, control.Value);
                Assert.Contains("-30", control.AccessibleDescription); Assert.Contains("-22", control.AccessibleDescription);
                edits.Discard();
                Assert.Equal(-22, control.Value); Assert.Equal(0, edits.PendingCount); Assert.Equal(originalColor, control.BackColor);
                control.Enabled = false; Assert.True(edits.CanDiscard); Assert.Empty(edits.Capture().CoMargins);
                edits.Discard(); Assert.True(control.Enabled); Assert.False(edits.CanDiscard);
                edits.Observe(new TuningRequest { Kind = TuningKind.CurveOptimizer, Value = 1000 }, "CPU readback");
                edits.Discard(); Assert.Equal(-22, control.Value);
                Assert.Contains("1000", control.AccessibleDescription);
            }
        });
        [Fact]
        public Task ReturningResultKeepsANewerVisibleEditAndDistinguishesUnverifiedValue() => OnSta(() => {
            using (var value = new NumericUpDown { Maximum = 1000 })
            using (var edits = new TuningEditControls()) {
                edits.Register(TuningKind.Ppt, 0, value); value.Value = 75;
                var request = new TuningRequest { Kind = TuningKind.Ppt, Value = 75 };
                value.Value = 90;
                var result = new TuningResult();
                result.Rows.Add(new TuningRow { Request = request, Write = new TuningReply { Status = 1 }, Outcome = TuningOutcome.AcceptedUnverified });
                edits.RecordResult(result);
                Assert.Equal(90, value.Value); Assert.Equal(1, edits.PendingCount);
                Assert.Contains("Last sent; unverified", value.AccessibleDescription);
                Assert.Contains("CPU reported: Unavailable", value.AccessibleDescription);
                edits.Discard(); Assert.Equal(75, value.Value); Assert.Equal(0, edits.PendingCount);
            }
        });
        [Theory]
        [InlineData(1F)]
        [InlineData(1.25F)]
        [InlineData(1.5F)]
        [InlineData(2F)]
        public Task PreviewFreezesRequestsAndCancellationHasNoApproval(float scale) => OnSta(() => {
            var session = new TuningEditSession();
            session.SetBaseline(new TuningRequest { Kind = TuningKind.Ppt, Value = 0 }, "Initial editor value; not a CPU read");
            var requested = new Profile { PptWatts = 75 };
            using (var preview = new TuningPreviewForm(requested, session, "Apply changes"))
            using (var font = new Font(preview.Font.FontFamily, preview.Font.Size * scale)) {
                requested.PptWatts = 100;
                preview.Font = font; preview.Opacity = 0; preview.Show(); Application.DoEvents();
                var grid = preview.Controls.OfType<DataGridView>().Single();
                Assert.True(grid.ReadOnly); Assert.Single(grid.Rows.Cast<DataGridViewRow>());
                Assert.Equal("75 W", grid.Rows[0].Cells["Requested"].Value);
                var cancel = (Button)preview.Controls.Find("buttonCancelPreview", true).Single(); cancel.PerformClick();
                Assert.Null(preview.ApprovedProfile); Assert.Equal(DialogResult.Cancel, preview.DialogResult);
                Assert.Equal(0, session.Baseline(new TuningRequest { Kind = TuningKind.Ppt }).Value);
            }
            requested.PptWatts = 75;
            using (var preview = new TuningPreviewForm(requested, session, "Apply changes")) {
                requested.PptWatts = 90;
                preview.Opacity = 0; preview.Show();
                ((Button)preview.Controls.Find("buttonApplyPreview", true).Single()).PerformClick();
                Assert.Equal(DialogResult.OK, preview.DialogResult); Assert.Equal(75, preview.ApprovedProfile.PptWatts);
            }
        });
        [Fact]
        public Task PreviewLabelsZeroAsNoWriteAndBlocksInvalidRequests() => OnSta(() => {
            foreach (int value in new[] { 0, 1001 })
                using (var preview = new TuningPreviewForm(new Profile { PptWatts = value }, new TuningEditSession(), "Apply selected settings")) {
                    preview.Opacity = 0; preview.Show(); Application.DoEvents();
                    var grid = preview.Controls.OfType<DataGridView>().Single();
                    if (value == 0) Assert.Equal("Leave unchanged (0)", grid.Rows[0].Cells["Action"].Value);
                    Assert.Equal(value == 0, preview.Controls.Find("buttonApplyPreview", true).Single().Enabled);
                }
        });
        [Fact]
        public Task LocalizedEditsPreviewAndHistoryKeepNumericValuesAndProvenance() => OnSta(() => {
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            using (var value = new NumericUpDown { Maximum = 1000 })
            using (var edits = new TuningEditControls()) {
                edits.Register(TuningKind.Ppt, 0, value); value.Value = 75;
                edits.RememberSaved(new Profile { Name = "Efficiency", PptWatts = 75 });
                Assert.Contains("并非 CPU 读取值", value.AccessibleDescription);
                Assert.Contains("Efficiency", value.AccessibleDescription);
                Assert.Equal(1, edits.PendingCount);
                using (var preview = new TuningPreviewForm(edits.Capture(), edits.Session, "Apply changes")) {
                    preview.Opacity = 0; preview.Show(); Application.DoEvents();
                    var grid = preview.Controls.OfType<DataGridView>().Single();
                    Assert.Equal("CPU 报告值", grid.Columns["CpuReported"].HeaderText);
                    Assert.Equal("75 W", grid.Rows[0].Cells["Requested"].Value);
                    Assert.Equal("发送请求", grid.Rows[0].Cells["Action"].Value);
                    Assert.Equal("取消", preview.Controls.Find("buttonCancelPreview", true).Single().Text);
                }
                using (var panel = new TuningResultsPanel(new TextBox())) {
                    var result = new TuningResult();
                    result.Rows.Add(new TuningRow { Request = new TuningRequest { Kind = TuningKind.Ppt, Value = 75 }, Outcome = TuningOutcome.AcceptedUnverified });
                    panel.ShowResult(result);
                    Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("en"); panel.RefreshLanguage();
                    Assert.Equal(1, panel.HistoryCount); Assert.Equal(75, panel.Result.Rows[0].Request.Value);
                    Assert.Contains("readback unavailable", panel.Controls.OfType<Panel>().Single().Controls.OfType<Label>().Single().Text);
                }
            }
        });
        [Fact]
        public Task HistorySelectionDisplaysSnapshotsWithoutAddingOrReplayingActions() => OnSta(() => {
            using (var form = new Form { Opacity = 0 })
            using (var panel = new TuningResultsPanel(new TextBox { Multiline = true })) {
                form.Controls.Add(panel); form.Show();
                for (int i = 0; i < 25; i++) {
                    var result = new TuningResult { Label = "Action " + i };
                    result.Rows.Add(new TuningRow { Request = new TuningRequest { Kind = TuningKind.Ppt, Value = i }, Outcome = TuningOutcome.AcceptedUnverified });
                    panel.ShowResult(result); result.Rows[0].Request.Value = 999;
                }
                Assert.Equal(20, panel.HistoryCount);
                var selector = panel.Controls.OfType<ComboBox>().Single();
                Assert.Equal(20, selector.Items.Count); Assert.Equal(24, panel.Result.Rows[0].Request.Value);
                selector.SelectedIndex = 19;
                Assert.Equal("Action 5", panel.Result.Label); Assert.Equal(5, panel.Result.Rows[0].Request.Value);
                Assert.Equal(20, panel.HistoryCount);
                Assert.True(panel.Controls.OfType<DataGridView>().Single().ReadOnly);
            }
        });
    }
}
