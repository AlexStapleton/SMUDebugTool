using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;
using ZenStatesDebugTool;

namespace ProfileCore.Tests
{
    public class UsableTabsUiTests
    {
        private static async Task Sta(Action action)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => {
                try { Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("en"); action(); done.SetResult(true); }
                catch (Exception ex) { done.SetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Assert.Same(done.Task, await Task.WhenAny(done.Task, Task.Delay(15000))); await done.Task;
        }
        [Theory]
        [InlineData(1F)]
        [InlineData(1.25F)]
        [InlineData(1.5F)]
        [InlineData(2F)]
        public Task VidAndBaseClockDoNotOverlapInTheCompleteNarrowTab(float scale) => Sta(() => {
            using (var form = new Form { ClientSize = new Size(365, 310), Opacity = 0 })
            using (var font = new Font(form.Font.FontFamily, form.Font.Size * scale)) {
                form.Font = font;
                var tabs = new TabControl { Dock = DockStyle.Fill }; var page = new TabPage { Name = "tabPagePstates", Text = "PStates" };
                tabs.TabPages.Add(page); form.Controls.Add(tabs);
                var table = new TableLayoutPanel { ColumnCount = 4, RowCount = 4, AutoSize = true, Dock = DockStyle.Top };
                foreach (var width in new[] { 100, 100, 50, 75 }) table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
                foreach (int row in Enumerable.Range(0, 4)) {
                    table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    table.Controls.Add(new Label { Text = new[] { "PState ID", "DID", "FID", "Frequency" }[row], AutoSize = true, MinimumSize = new Size(77, 27) }, 0, row);
                    table.Controls.Add(new TextBox { Text = row == 3 ? "4300 MHz" : "860", Dock = DockStyle.Fill }, 1, row);
                }
                table.Controls.Add(new Button { Text = "Read", AutoSize = true }, 3, 0);
                table.Controls.Add(new Button { Text = "Write", AutoSize = true }, 3, 1);
                page.Controls.Add(table);
                var clock = new NumericUpDown { DecimalPlaces = 4, Maximum = 150, Minimum = 95, Value = 100, Location = new Point(106, 147) };
                var current = new Label { Text = "100 MHz", Location = new Point(6, 150) };
                var apply = new Button { Text = "Apply", Location = new Point(252, 147) };
                page.Controls.Add(clock); page.Controls.Add(current); page.Controls.Add(apply);
                var vid = new PStateVidControls(table); vid.Value.Text = "201"; vid.Voltage.Text = "1.2500 V (VID)";
                PStateEditorLayout.AttachBaseClock(page, table, clock, current, apply);
                TabGuidance.Attach(page); form.Show(); Application.DoEvents();
                Assert.Same(table, clock.Parent); Assert.Same(table, current.Parent); Assert.Same(table, apply.Parent);
                Assert.Equal(7, table.GetRow(clock)); Assert.Equal(5, table.GetRow(vid.Voltage));
                Assert.True(clock.Top >= vid.Voltage.Bottom);
                Assert.False(clock.Bounds.IntersectsWith(vid.Voltage.Bounds)); Assert.False(apply.Bounds.IntersectsWith(vid.Write.Bounds));
                Assert.Equal(100, clock.Value); Assert.Equal("201", vid.Value.Text);
                var content = (Panel)page.Controls.Find("guidedContent", false).Single();
                Assert.True(content.AutoScroll); Assert.True(table.Height >= apply.Bottom);
                using (var bitmap = new Bitmap(form.Width, form.Height)) form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            }
        });
        [Theory]
        [InlineData(1F)]
        [InlineData(1.5F)]
        [InlineData(2F)]
        public Task AcpiRangeShowsUnitsAndValidatesWithoutAnEmptyChoicesBox(float scale) => Sta(() => {
            using (var form = new Form { ClientSize = new Size(365, 560), Opacity = 0 })
            using (var font = new Font(form.Font.FontFamily, form.Font.Size * scale)) {
                form.Font = font;
                var tabs = new TabControl { Dock = DockStyle.Fill }; var page = new TabPage { Name = "tabPageWmi" };
                tabs.TabPages.Add(page); form.Controls.Add(tabs);
                var table = new TableLayoutPanel { Dock = DockStyle.Fill }; page.Controls.Add(table);
                var commands = new ComboBox(); var choices = new ComboBox(); var input = new TextBox(); var send = new Button();
                var ui = new AcpiCommandControls(table, commands, choices, input, send);
                choices.SelectedIndexChanged += (s, e) => ui.SelectChoice();
                ui.SetCommand(AcpiCommandHelp.For("Set Tcl", 0x20002, true));
                ui.ShowMetadata(AcpiValueMetadata.Range(0x20002, 0x20002, 20, 64, 2, 36));
                TabGuidance.Attach(page); form.Show(); Application.DoEvents();
                Assert.False(choices.Visible); Assert.True(input.Enabled); Assert.False(send.Enabled);
                Assert.Contains("memory clock cycles", ui.Hint.Text + ui.Description.Text);
                Assert.Contains("step 2", ui.Domain.Text); Assert.Contains("36", ui.Current.Text);
                input.Text = "31"; Assert.False(send.Enabled); Assert.Contains("step 2", ui.Validation.Text);
                input.Text = "30"; Assert.True(send.Enabled);
                int sends = 0; uint sent = 0;
                send.Click += (s, e) => { Assert.True(ui.TryArgument(out sent, out _)); sends++; };
                send.PerformClick(); Assert.Equal(1, sends); Assert.Equal((uint)30, sent);
                Assert.Empty(form.OwnedForms.OfType<TuningPreviewForm>());
                ui.ShowMetadata(AcpiValueMetadata.Discrete(0x20002, 0x20002, 2, new uint[] { 0, 30, 99 }, 30));
                Assert.True(choices.Visible); Assert.Equal(2, choices.Items.Count);
                choices.SelectedIndex = 0; Assert.Equal("0", input.Text); Assert.True(send.Enabled);
                ui.SetCommand(AcpiCommandHelp.For("Set VDDG IOD", 9, true));
                ui.ShowMetadata(AcpiValueMetadata.Unknown(9));
                Assert.Equal("", input.Text); Assert.Contains("raw, not volts", ui.Description.Text);
                Assert.Contains("did not report", ui.Domain.Text);
                ui.SetCommand(AcpiCommandHelp.For("Get Tcl", 3, false)); Assert.False(input.Enabled); Assert.True(send.Enabled);
            }
        });
        [Fact]
        public Task FirmwareChoicesLoadedBeforeTheTabIsVisibleStayEnabled() => Sta(() => {
            using (var form = new Form { Opacity = 0 }) {
                var tabs = new TabControl { Dock = DockStyle.Fill };
                tabs.TabPages.Add(new TabPage("CPU")); var page = new TabPage("AMD ACPI"); tabs.TabPages.Add(page); form.Controls.Add(tabs);
                var table = new TableLayoutPanel(); page.Controls.Add(table);
                var commands = new ComboBox(); var choices = new ComboBox(); var input = new TextBox(); var send = new Button();
                var ui = new AcpiCommandControls(table, commands, choices, input, send);
                ui.SetCommand(AcpiCommandHelp.For("Set SMTEn", 0x20026, true));
                ui.ShowMetadata(AcpiValueMetadata.Discrete(0x20026, 0x20026, 2, new uint[] { 0, 1 }, 0));
                form.Show(); tabs.SelectedTab = page; Application.DoEvents();
                Assert.True(choices.Visible); Assert.True(choices.Enabled); Assert.Equal(2, choices.Items.Count);
            }
        });
        [Fact]
        public Task RawDiagnosticsRemainAccessibleBeforeAnyTuningAction() => Sta(() => {
            using (var form = new Form { Opacity = 0 })
            using (var panel = new TuningResultsPanel(new TextBox { Text = "CPUID decoded output" })) {
                form.Controls.Add(panel); form.Show();
                var button = (Button)panel.Controls.Find("buttonTuningHistory", true).Single();
                Assert.True(button.Enabled); button.PerformClick();
                using (var history = form.OwnedForms.OfType<TuningHistoryForm>().Single()) {
                    Assert.Empty(history.Controls.OfType<DataGridView>().Single().Rows.Cast<DataGridViewRow>());
                    history.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single(b => b.Text == "Activity log").PerformClick();
                    using (var output = history.OwnedForms.Single()) {
                        var text = output.Controls.OfType<TextBox>().Single();
                        Assert.True(text.ReadOnly); Assert.Equal("CPUID decoded output", text.Text);
                    }
                }
            }
        });
        [Fact]
        public Task NarrowMainPanelUsesAReadableSeparateHistoryWindow() => Sta(() => {
            using (var form = new Form { ClientSize = new Size(350, 180), Opacity = 0 })
            using (var panel = new TuningResultsPanel(new TextBox { Text = "raw diagnostics" })) {
                form.Controls.Add(panel); form.Show();
                var result = new TuningResult { Label = "Apply selected settings — Long profile name" };
                result.Rows.Add(new TuningRow { Request = new TuningRequest { Kind = TuningKind.Ppt, Value = 150 }, Outcome = TuningOutcome.AcceptedUnverified });
                panel.ShowResult(result); Assert.Empty(panel.Controls.OfType<ComboBox>());
                ((Button)panel.Controls.Find("buttonTuningHistory", true).Single()).PerformClick();
                using (var history = form.OwnedForms.OfType<TuningHistoryForm>().Single()) {
                    history.Size = new Size(500, 300); Application.DoEvents();
                    var grid = history.Controls.OfType<DataGridView>().Single();
                    Assert.True(grid.ReadOnly); Assert.Equal(DataGridViewTriState.True, grid.DefaultCellStyle.WrapMode);
                    Assert.Contains("PPT 150 W", grid.Rows[0].Cells["Result"].Value.ToString());
                    Assert.True(grid.Rows[0].Height > grid.Font.Height);
                    Assert.Equal(FormBorderStyle.Sizable, history.FormBorderStyle);
                }
            }
        });
    }
}
