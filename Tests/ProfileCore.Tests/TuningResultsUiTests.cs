using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;
using ZenStatesDebugTool;

namespace ProfileCore.Tests
{
    public class TuningResultsUiTests
    {
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public async Task SummaryPreservesProfileAndLogRowsAndDetailsAreReadOnly(int scale)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    using (var form = new Form { ClientSize = new Size(700 * scale, 500 * scale) })
                    using (var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 })
                    {
                        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 200 * scale));
                        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                        var profileControls = new Panel { Dock = DockStyle.Fill };
                        var log = new TextBox { Dock = DockStyle.Fill, Multiline = true, Text = "Existing log" };
                        table.Controls.Add(profileControls, 0, 0);
                        table.Controls.Add(log, 0, 1);
                        form.Controls.Add(table);
                        var panel = TuningResultsPanel.Attach(log);
                        form.CreateControl();
                        form.PerformLayout(); table.PerformLayout(); panel.PerformLayout();
                        Assert.Equal(0, table.GetRow(profileControls));
                        Assert.Equal(1, table.GetRow(panel));
                        Assert.Same(panel, log.Parent);
                        Assert.Equal("Existing log", log.Text);
                        Assert.False(log.Visible);
                        var overview = panel.Controls.OfType<DataGridView>().Single();
                        var bar = panel.Controls.OfType<Panel>().Single();
                        bar.PerformLayout();
                        Assert.Equal(26, bar.Height);
                        Assert.True(overview.Top >= bar.Bottom);
                        Assert.All(bar.Controls.OfType<Button>(), button => Assert.True(button.Height <= 26));
                        var result = new TuningResult();
                        result.Rows.Add(new TuningRow
                        {
                            Request = new TuningRequest { Kind = TuningKind.Fmax, Value = 5700 },
                            Outcome = TuningOutcome.AcceptedUnverified,
                            Write = new TuningReply { Status = 1 },
                            Reason = "No getter"
                        });
                        panel.ShowResult(result);
                        Assert.Same(result, panel.Result);
                        Assert.Contains("readback unavailable", panel.Controls.OfType<Panel>().Single().Controls.OfType<Label>().Single().Text);
                        Assert.Equal(3, overview.Columns.Count);
                        using (var details = new ApplyResultsForm(result))
                        {
                            details.Opacity = 0; details.Show();
                            var grid = details.Controls.OfType<DataGridView>().Single();
                            Assert.True(grid.ReadOnly); Assert.False(grid.AllowUserToAddRows);
                            Assert.Equal(FormBorderStyle.Sizable, details.FormBorderStyle);
                            Assert.Contains("Current settings", details.Text);
                            Assert.Equal(4, grid.Columns.Count);
                            Assert.False(details.Controls.OfType<TextBox>().Single().Visible);
                            details.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<CheckBox>().Single().Checked = true;
                            Assert.True(details.Controls.OfType<TextBox>().Single().Visible);
                            using (var bitmap = new Bitmap(details.Width, details.Height))
                                details.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        }
                        form.Opacity = 0; form.Show();
                        Assert.True(overview.Visible); Assert.False(log.Visible);
                        foreach (var kind in new[] { TuningKind.Ppt, TuningKind.Tdc, TuningKind.Edc, TuningKind.Scalar })
                        {
                            ZenStatesDebugTool.Profiles.Profile captured = null;
                            using (var editor = new PowerLimitEditor(kind, new NumericUpDown { Value = kind == TuningKind.Scalar ? 3 : 75 },
                                profile => { captured = profile; return Task.CompletedTask; }))
                            {
                                profileControls.Controls.Add(editor);
                                editor.Controls.OfType<Button>().Single().PerformClick();
                                Assert.NotNull(captured);
                                Assert.Empty(captured.CoMargins); Assert.Null(captured.CurveShaperTiers); Assert.Null(captured.Fmax);
                                var limits = new[] { captured.PptWatts, captured.TdcAmps, captured.EdcAmps, captured.PboScalar };
                                Assert.Single(limits, value => value.HasValue);
                                Assert.Equal(kind == TuningKind.Scalar ? 3 : 75, limits[(int)kind - (int)TuningKind.Ppt]);
                            }
                        }
                    }
                    done.SetResult(true);
                }
                catch (Exception ex) { done.SetException(ex); }
            })
            { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.Same(done.Task, await Task.WhenAny(done.Task, Task.Delay(10000)));
            Assert.True(await done.Task);
        }
    }
}
