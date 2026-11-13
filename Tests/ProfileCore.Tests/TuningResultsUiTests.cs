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
                        Assert.True(log.Height > 0); Assert.True(log.Top >= 46);
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
                        Assert.Contains("accepted (unverified)", panel.Controls.OfType<Panel>().Single().Controls.OfType<Label>().Single().Text);
                        using (var details = new ApplyResultsForm(result))
                        {
                            details.CreateControl();
                            var grid = details.Controls.OfType<DataGridView>().Single();
                            Assert.True(grid.ReadOnly); Assert.False(grid.AllowUserToAddRows);
                            Assert.Equal(FormBorderStyle.Sizable, details.FormBorderStyle);
                            Assert.Contains("Current settings", details.Text);
                            using (var bitmap = new Bitmap(details.Width, details.Height))
                                details.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
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
