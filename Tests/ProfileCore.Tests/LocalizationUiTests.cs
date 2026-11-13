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
    public class LocalizationUiTests
    {
        [Theory]
        [InlineData(1F)]
        [InlineData(1.25F)]
        [InlineData(1.5F)]
        [InlineData(2F)]
        public async Task LocalizedCaptionsAndFontScalingPreserveValuesAndLiveResults(float scale)
        {
            var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("en");
                    using (var form = new Form { ClientSize = new Size(500, 300) })
                    using (var translated = new Label { Text = "Details", Name = "stableLabelId" })
                    using (var log = new TextBox { Text = "-25", Multiline = true })
                    {
                        form.Controls.Add(log);
                        var panel = TuningResultsPanel.Attach(log);
                        panel.Controls.Add(translated);
                        UiText.Apply(form);
                        panel.ShowResult(new TuningResult());
                        string summary = panel.Controls.OfType<Panel>().Single().Controls.OfType<Label>().Single().Text;
                        Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
                        UiText.Apply(form);
                        Assert.Equal("详情", translated.Text); Assert.Equal("stableLabelId", translated.Name);
                        Assert.Equal("-25", log.Text);
                        Assert.Equal(summary, panel.Controls.OfType<Panel>().Single().Controls.OfType<Label>().Single().Text);
                        using (var scaledFont = new Font(panel.Font.FontFamily, panel.Font.Size * scale))
                        {
                            panel.Font = scaledFont; panel.PerformLayout();
                            var bar = panel.Controls.OfType<Panel>().Single(); bar.PerformLayout();
                            Assert.True(bar.Height >= panel.Font.Height + 10);
                            Assert.True(log.Top >= bar.Bottom);
                            panel.Font = SystemFonts.DefaultFont;
                        }
                        Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("en"); UiText.Apply(form);
                        Assert.Equal("Details", translated.Text);
                    }
                    finished.SetResult(true);
                }
                catch (Exception ex) { finished.SetException(ex); }
            })
            { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Assert.Same(finished.Task, await Task.WhenAny(finished.Task, Task.Delay(10000)));
            Assert.True(await finished.Task);
        }
    }
}
